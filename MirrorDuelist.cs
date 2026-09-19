using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Ascension;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Factories;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Monsters;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.MonsterMoves.Intents;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.ValueProps;

namespace MirrorDuelistMod;

/// <summary>
/// An Act 3 enemy that mirrors the player: it copies the played character's
/// visuals, starts with 1.5x the player's max HP, and at combat start steals
/// three real cards out of the player's draw/discard piles (weighted: attack
/// &gt; power &gt; skill &gt; curse, uncommon &gt; common &gt; rare), ThievingHopper
/// style. The stolen cards hang above its head; every turn it spends 5 energy playing
/// them through a translation layer - attacks hit with their printed damage,
/// block skills grant it block, a small whitelist of powers applies to the
/// matching side. Powers are spent after one use, exhaust cards leave the
/// pool (their card visuals go with them), a pool down to one card is played
/// twice a turn, and an empty pool falls back to Strike-equivalent attacks.
/// Stolen cards return to the player automatically: combat only ends once the
/// duelist dies, and RemoveFromCombat never touches the run deck.
/// </summary>
public sealed class MirrorDuelist : MonsterModel
{
    private const decimal DamageClampMin = 6m;
    private const decimal DamageClampMax = 30m;
    private const decimal DamageFallback = 8m;
    private const decimal StrikeDamage = 6m;
    private const float HpMultiplier = 1.5f;
    private const int StealCount = 3;
    private const int TurnEnergy = 5;
    private const float CardScale = 0.55f;
    private const string MoveId = "MIRROR_PLAY_MOVE";
    private const string StealMoveId = "MIRROR_STEAL_MOVE";

    // The hopper's FMOD events are the only enemy attack/die paths verified to
    // exist; our id has none, so reuse them instead of firing missing events.
    private const string AttackSfxPath = "event:/sfx/enemy/enemy_attacks/thieving_hopper/thieving_hopper_attack";
    private const string DeathSfxPath = "event:/sfx/enemy/enemy_attacks/thieving_hopper/thieving_hopper_die";

    private readonly List<CardModel> _pool = new();
    private readonly Dictionary<CardModel, NCard> _cardNodes = new();
    private readonly Dictionary<CardModel, Vector2> _cardHomes = new();
    private Player? _mirrorPlayer;
    private Creature? _mirrorOsty;
    private int _generatedPlayDepth;
    private int _generatedCardsPlayedThisTurn;

    // Next-turn economy granted by translated resource powers
    // (EnergyNextTurnPower & friends). Consumed by the next TakePlays.
    private int _pendingEnergyBonus;
    private int _pendingDrawBonus;
    private int _pendingBlockBonus;

    // Energy left unspent by the last planning; the turn Perform reuses it to
    // play drawn/bonus cards beyond the declared intent list.
    private int _plannedLeftoverEnergy;

    private const int MaxGeneratedPlayDepth = 4;
    private const int MaxGeneratedCardsPerTurn = 12;

    public override int MinInitialHp => ComputeInitialHp();

    public override int MaxInitialHp => ComputeInitialHp();

    // SceneHelper paths carry the .tscn extension; the load fails into the
    // fallback visuals without it.
    protected override string VisualsPath => "res://scenes/creature_visuals/" + MirroredCharacterId() + ".tscn";

    protected override string AttackSfx => AttackSfxPath;

    protected override string CastSfx => AttackSfxPath;

    public override string DeathSfx => DeathSfxPath;

    public override string HurtSfx => string.Empty;

    public override bool HasDeathSfx => true;

    public override bool HasHurtSfx => false;

    protected override MonsterMoveStateMachine GenerateMoveStateMachine()
    {
        // ThievingHopper-style: turn one IS the steal move (performed from the
        // draw/discard piles mid-combat); 5-energy card turns chain from it.
        MirrorMoveState steal = NewStealMove();
        var machine = new MonsterMoveStateMachine(new MonsterState[] { steal }, steal);
        steal.Machine = machine;
        return machine;
    }

    public override async Task AfterAddedToRoom()
    {
        await base.AfterAddedToRoom();
        FlipToFaceThePlayer();
        Player? real = CurrentPlayer();
        _mirrorPlayer = real != null ? MirrorPlayerFactory.Create(real, Creature) : null;
        if (_mirrorPlayer != null && Creature.CombatState is CombatState combatState)
        {
            try
            {
                // Give the mirror player real combat piles so stolen cards
                // resolve their CombatState while being played.
                _mirrorPlayer.ResetCombatState();
                _mirrorPlayer.PopulateCombatState(real!.RunState.Rng.Shuffle, combatState);
            }
            catch (Exception e)
            {
                Log.Error("[MirrorDuelist] mirror pile setup failed: " + e);
            }
        }
    }

    /// <summary>Player skeletons are authored facing right (towards the enemy
    /// side); as an enemy the duelist must face left, so mirror the body.</summary>
    private void FlipToFaceThePlayer()
    {
        try
        {
            Node2D? body = Creature.GetCreatureNode()?.Visuals?.Body;
            if (body != null)
            {
                body.Scale = new Vector2(-body.Scale.X, body.Scale.Y);
            }
        }
        catch
        {
            // Cosmetic only.
        }
    }

    /// <summary>
    /// Hoppe-parity rewards: on death, every stolen card is offered back as a
    /// SpecialCardReward. Claiming adds it to the deck; skipping leaves it out
    /// of the run (it was already removed from the deck at steal time).
    /// </summary>
    public override Task BeforeDeath(Creature creature)
    {
        if (creature != Creature || _pool.Count == 0)
        {
            return Task.CompletedTask;
        }
        try
        {
            Player? player = CurrentPlayer();
            if (player == null || creature.CombatState is not CombatState combatState)
            {
                return Task.CompletedTask;
            }
            IRunState runState = combatState.RunState;
            if (runState.CurrentRoom is not CombatRoom combatRoom)
            {
                return Task.CompletedTask;
            }
            foreach (CardModel card in _pool)
            {
                if (card.DeckVersion == null)
                {
                    continue;
                }
                runState.AddCard(card.DeckVersion, player);
                SpecialCardReward reward = new SpecialCardReward(card.DeckVersion, player);
                reward.SetCustomDescriptionEncounterSource(ModelDb.Encounter<MirrorDuelistNormal>().Id);
                combatRoom.AddExtraReward(player, reward);
            }
        }
        catch (Exception e)
        {
            // Reward plumbing must never break the death sequence, but a
            // silent failure would swallow the remaining cards' rewards.
            Log.Error($"[MirrorDuelist] reward return failed: {e}");
        }
        return Task.CompletedTask;
    }

    // ---- HP: 1.5x the mirrored player's max HP ----

    private static int ComputeInitialHp()
    {
        int baseHp = 76;
        try
        {
            Player? player = CurrentPlayer();
            int? maxHp = player?.Creature?.MaxHp;
            if (maxHp.HasValue && maxHp.Value > 0)
            {
                baseHp = maxHp.Value;
            }
        }
        catch
        {
            // No active run (menus/bestiary) - keep the fallback.
        }
        int hp = (int)MathF.Round(baseHp * HpMultiplier);
        return AscensionHelper.GetValueIfAscension(AscensionLevel.ToughEnemies, (int)MathF.Round(hp * 1.07f), hp);
    }

    // ---- visuals: copy the character being played ----

    private static string MirroredCharacterId()
    {
        try
        {
            CharacterModel? character = CurrentPlayer()?.Character;
            if (character != null)
            {
                return character.Id.Entry.ToLowerInvariant();
            }
        }
        catch
        {
            // fall through
        }
        return "thieving_hopper";
    }

    private static Player? CurrentPlayer()
    {
        return RunManager.Instance?.DebugOnlyGetState()?.Players?.FirstOrDefault();
    }

    // ---- stealing ----

    private async Task StealCards()
    {
        _pool.Clear();
        foreach (NCard node in _cardNodes.Values)
        {
            node.QueueFreeSafely();
        }
        _cardNodes.Clear();
        _cardHomes.Clear();
        try
        {
            Player? player = CurrentPlayer();
            if (player == null)
            {
                return;
            }
            List<CardModel> candidates = CardPile
                .GetCards(player, PileType.Draw, PileType.Discard)
                .Where(c => c.DeckVersion != null && IsStealable(c))
                .GroupBy(c => c.Id.Entry)
                .Select(g => g.First())
                .ToList();
            Rng rng = RunRng.CombatCardGeneration;
            NCreature? creatureNode = Creature.GetCreatureNode();
            while (_pool.Count < StealCount && candidates.Count > 0)
            {
                CardModel? pick = rng.WeightedNextItem(candidates, CardWeight);
                if (pick == null)
                {
                    break;
                }
                candidates.Remove(pick);
                await CardPileCmd.RemoveFromCombat(pick);
                // Hoppe-parity: also pull the deck version out of the run deck,
                // so the card stays gone after the fight until it is claimed back.
                if (pick.DeckVersion != null)
                {
                    await CardPileCmd.RemoveFromDeck(pick.DeckVersion, showPreview: false);
                }
                _pool.Add(pick);
                // The duelist plays a fresh copy owned by the mirror player,
                // filed into its own draw pile — generated cards (Shivs etc.)
                // then also land in its hand instead of the player's.
                if (_mirrorPlayer != null)
                {
                    CardModel dupe = pick.CreateDupe(_mirrorPlayer);
                    CardPile? drawPile = MirrorPile(PileType.Draw);
                    if (drawPile != null)
                    {
                        await CardPileCmd.Add(dupe, drawPile, clonedBy: pick, skipVisuals: true);
                        AttachCardVisual(dupe, creatureNode, _pool.Count - 1);
                    }
                }
            }
        }
        catch
        {
            // Never let the flavour hook break combat setup; an empty pool
            // just means the duelist fights with Strikes.
        }
    }

    private CardPile? MirrorPile(PileType type)
    {
        return _mirrorPlayer == null ? null : PileTypeExtensions.GetPile(type, _mirrorPlayer);
    }

    private static bool IsStealable(CardModel card)
    {
        return card.Type is CardType.Attack or CardType.Skill or CardType.Power or CardType.Curse;
    }

    private static float CardWeight(CardModel? card)
    {
        if (card == null)
        {
            return 0f;
        }
        float byType = card.Type switch
        {
            CardType.Attack => 4f,
            CardType.Power => 3f,
            CardType.Skill => 2f,
            _ => 1f,
        };
        float byRarity = card.Rarity switch
        {
            CardRarity.Uncommon => 3f,
            CardRarity.Common => 2f,
            CardRarity.Rare => 1f,
            _ => 1f,
        };
        return byType * byRarity;
    }

    /// <summary>Hang the stolen card face above the duelist, hopper-style. The
    /// player-visual skeletons have no %StolenCardPos bone, so the cards are
    /// pinned around the body-center marker instead.</summary>
    private void AttachCardVisual(CardModel card, NCreature? creatureNode, int slot)
    {
        try
        {
            if (creatureNode == null)
            {
                return;
            }
            Marker2D? anchor = creatureNode.GetSpecialNode<Marker2D>("%CenterPos");
            NCard? nCard = NCard.Create(card);
            if (anchor == null || nCard == null)
            {
                return;
            }
            anchor.AddChildSafely(nCard);
            nCard.UpdateVisuals(PileType.Deck, CardPreviewMode.Normal);
            nCard.Scale = Vector2.One * CardScale;
            Vector2 home = new Vector2((slot - 1) * 150f, -230f) + nCard.Size * 0.275f;
            nCard.Position = home;
            _cardNodes[card] = nCard;
            _cardHomes[card] = home;
        }
        catch
        {
            // Visual garnish only.
        }
    }

    // ---- per-turn move building ----

    private MirrorMoveState NewStealMove()
    {
        async Task Perform(IReadOnlyList<Creature> targets)
        {
            try
            {
                await StealCards();
            }
            catch
            {
                // A failed theft must never freeze the fight; the duelist then
                // just duels with Strikes.
            }
        }

        // Use the vanilla card-debuff intent (the orange card icon used by
        // Thieving Hopper) instead of StatusIntent. StatusIntent means
        // "shuffle status cards" and also draws the unwanted purple icon and
        // "steal three cards" label above the creature.
        return new MirrorMoveState(this, StealMoveId, Perform, new MirrorStealIntent());
    }

    private MirrorMoveState NewTurnMove(string stateId, List<CardModel?> plays)
    {
        List<AbstractIntent> intents = plays.Select(BuildIntent).ToList();

        async Task Perform(IReadOnlyList<Creature> targets)
        {
            Creature target = targets.Count > 0 ? targets[0] : Creature;
            _generatedPlayDepth = 0;
            _generatedCardsPlayedThisTurn = 0;
            CardPile? hand = MirrorPile(PileType.Hand);
            CardPile? discard = MirrorPile(PileType.Discard);
            // BlockNextTurnPower-style pendings land at the start of the turn.
            if (_pendingBlockBonus > 0)
            {
                int block = _pendingBlockBonus;
                _pendingBlockBonus = 0;
                await CreatureCmd.GainBlock(Creature, block, ValueProp.Move, null);
            }
            foreach (CardModel? card in plays)
            {
                await PlayTurnCard(card, target);
            }
            // Greedy continuation: cards drawn or kept this turn (Draw vars,
            // cheap leftovers) get played beyond the declared intents while
            // energy remains, exactly like a player spending their turn.
            int energy = Math.Max(0, _plannedLeftoverEnergy);
            int extra = 0;
            while (energy > 0 && extra < 8 && hand != null)
            {
                CardModel? pick = null;
                foreach (CardModel c in hand.Cards.OrderBy(_ => RunRng.MonsterAi.NextFloat()))
                {
                    if (PlayCost(c) <= energy)
                    {
                        pick = c;
                        break;
                    }
                }
                if (pick == null)
                {
                    break;
                }
                energy -= PlayCost(pick);
                hand.RemoveInternal(pick);
                extra++;
                await PlayTurnCard(pick, target);
            }
            // End of turn: only the unplayed hand moves forward here — played
            // cards were already filed by PlayTurnCard. A duplicate AddInternal
            // throws and KILLS the engine's turn loop, so every pile move is
            // guarded against cards already in the pile.
            if (hand != null && discard != null)
            {
                foreach (CardModel c in hand.Cards.ToList())
                {
                    if (!discard.Cards.Contains(c))
                    {
                        hand.RemoveInternal(c);
                        discard.AddInternal(c);
                    }
                }
            }
        }

        return new MirrorMoveState(this, stateId, Perform, intents.ToArray());
    }

    private MirrorMoveState BuildTurnMove(MonsterMoveStateMachine machine)
    {
        List<CardModel?> plays = TakePlays();
        MirrorMoveState state = NewTurnMove(MoveId, plays);
        state.Machine = machine;
        machine.States[MoveId] = state;
        return state;
    }

    /// <summary>
    /// Player-style turn over the mirror player's real piles: reshuffle the
    /// discard pile when the draw pile is empty, draw up to five (plus
    /// pending draw bonuses) into the hand, then spend 5 energy (plus pending
    /// energy bonuses) greedily — too-expensive cards are skipped, not wasted.
    /// Generated cards (Shivs etc.) live in the same piles and get played on
    /// later turns. A pool down to exactly one card plays it twice.
    /// </summary>
    private List<CardModel?> TakePlays()
    {
        CardPile? draw = MirrorPile(PileType.Draw);
        CardPile? hand = MirrorPile(PileType.Hand);
        CardPile? discard = MirrorPile(PileType.Discard);
        _plannedLeftoverEnergy = 0;
        if (draw == null || hand == null || discard == null)
        {
            return new List<CardModel?> { null, null };
        }
        int energy = TurnEnergy + _pendingEnergyBonus;
        int handCap = 5 + _pendingDrawBonus;
        _pendingEnergyBonus = 0;
        _pendingDrawBonus = 0;
        if (draw.Cards.Count == 0 && discard.Cards.Count > 0)
        {
            var shuffled = discard.Cards.ToList();
            foreach (CardModel c in shuffled)
            {
                discard.RemoveInternal(c);
            }
            ShuffleList(shuffled);
            foreach (CardModel c in shuffled)
            {
                draw.AddInternal(c);
            }
        }
        if (draw.Cards.Count == 0)
        {
            return new List<CardModel?> { null, null };
        }
        while (hand.Cards.Count < handCap && draw.Cards.Count > 0)
        {
            CardModel top = draw.Cards[^1];
            draw.RemoveInternal(top);
            hand.AddInternal(top);
        }
        var order = hand.Cards.OrderBy(_ => RunRng.MonsterAi.NextFloat()).ToList();
        var plays = new List<CardModel?>();
        bool progress = true;
        while (energy > 0 && order.Count > 0 && progress)
        {
            progress = false;
            foreach (CardModel card in order)
            {
                int cost = PlayCost(card);
                if (cost <= energy)
                {
                    plays.Add(card);
                    energy -= cost;
                    order.Remove(card);
                    hand.RemoveInternal(card);
                    progress = true;
                    break;
                }
            }
        }
        // Spec: with the whole pool down to a single card, it is played twice.
        if (plays.Count == 1 && draw.Cards.Count == 0 && hand.Cards.Count == 0 && discard.Cards.Count == 0)
        {
            plays.Add(plays[0]);
        }
        _plannedLeftoverEnergy = energy;
        return plays;
    }

    private void ShuffleList(List<CardModel> pile)
    {
        Rng rng = RunRng.MonsterAi;
        for (int i = pile.Count - 1; i > 0; i--)
        {
            int j = rng.NextInt(i + 1);
            (pile[i], pile[j]) = (pile[j], pile[i]);
        }
    }

    private static int PlayCost(CardModel card)
    {
        int cost = card.CurrentStarCost;
        return Math.Clamp(cost < 0 ? 1 : cost, 0, TurnEnergy);
    }

    // ---- translation layer ----

    /// <summary>Play one card like a player would: the hanging card flies to
    /// the target, the effect lands, then the card either returns to its slot
    /// (kept) or is freed (power/exhaust spent).</summary>
    private async Task PlayTurnCard(CardModel? card, Creature target)
    {
        NCard? node = card != null ? _cardNodes.GetValueOrDefault(card) : null;
        await FlyCard(node, target);
        CardPile? playPile = MirrorPile(PileType.Play);
        if (card != null && playPile != null && !playPile.Cards.Contains(card))
        {
            // Keep the copied card in a valid combat pile while the safe
            // interpreter resolves it. Guard every pile insertion: duplicate
            // Add calls throw and can terminate the engine's turn coroutine.
            await CardPileCmd.Add(card, playPile, skipVisuals: true);
        }
        try
        {
            await DoEffect(card, target);
        }
        catch (Exception e)
        {
            // A malformed/unrecognised card becomes a dud instead of killing
            // the monster turn loop or opening player-only selection UI.
            Log.Error($"[MirrorDuelist] safe play failed for {card?.Id.Entry ?? "STRIKE"}: {e}");
        }
        if (card != null && playPile != null)
        {
            if (playPile.Cards.Contains(card))
            {
                playPile.RemoveInternal(card);
            }
            CardPile? dest = IsConsumed(card) ? MirrorPile(PileType.Exhaust) : MirrorPile(PileType.Discard);
            if (dest != null && !dest.Cards.Contains(card))
            {
                await CardPileCmd.Add(card, dest, skipVisuals: true);
            }
            if (IsConsumed(card))
            {
                _cardNodes.Remove(card);
                _cardHomes.Remove(card);
            }
        }
        SettleVisual(card, node);
    }

    private async Task FlyCard(NCard? node, Creature target)
    {
        if (node == null)
        {
            await Cmd.Wait(0.15f);
            return;
        }
        try
        {
            Vector2 targetPos = target.GetCreatureNode()?.GlobalPosition ?? node.GlobalPosition;
            Tween tween = node.CreateTween();
            tween.SetParallel();
            tween.TweenProperty(node, "global_position", targetPos, 0.28f);
            tween.TweenProperty(node, "scale", Vector2.One * 0.3f, 0.28f);
            await Cmd.Wait(0.28f);
        }
        catch
        {
            // Visual only.
        }
    }

    private void SettleVisual(CardModel? card, NCard? node)
    {
        if (card == null || node == null)
        {
            return;
        }
        if (IsConsumed(card))
        {
            _cardNodes.Remove(card);
            _cardHomes.Remove(card);
            node.QueueFreeSafely();
            return;
        }
        try
        {
            if (!_cardHomes.TryGetValue(card, out Vector2 home))
            {
                return;
            }
            Tween tween = node.CreateTween();
            tween.SetParallel();
            tween.TweenProperty(node, "position", home, 0.3f);
            tween.TweenProperty(node, "scale", Vector2.One * CardScale, 0.3f);
        }
        catch
        {
            // Visual only.
        }
    }

    private async Task DoEffect(CardModel? card, Creature target)
    {
        if (card == null)
        {
            await DamageCmd.Attack(StrikeDamage).FromMonster(this)
                .WithAttackerAnim("Attack", 0.3f).WithAttackerFx(null, AttackSfxPath)
                .WithHitFx("vfx/vfx_attack_slash").Execute(null);
            return;
        }
        await SafeDoEffect(card, target);
    }

    /// <summary>
    /// Deterministic, UI-free interpreter for stolen cards. It intentionally
    /// never calls CardModel.OnPlay or arbitrary enchantment callbacks: those
    /// methods may wait for the real player's hand/selection UI and cannot be
    /// cancelled safely from a monster turn. Unsupported effects are duds,
    /// while their energy and normal discard/exhaust movement still happen.
    /// </summary>
    private async Task SafeDoEffect(CardModel card, Creature target)
    {
        var ctx = new ThrowingPlayerChoiceContext();
        // 重放N (Replay N) repeats the whole card, self-cost included — same
        // count the vanilla OnPlayWrapper pipeline would use. Glam (华彩)
        // adds extra plays through the same call.
        int playCount = 1 + card.GetEnchantedReplayCount();
        for (int i = 0; i < playCount; i++)
        {
            DynamicVarSet vars = card.DynamicVars;
            int cost = PlayCost(card);
            var cardPlay = new CardPlay
            {
                Card = card,
                Player = card.Owner!,
                Target = target,
                ResultPile = PileType.Discard,
                Resources = new ResourceInfo { EnergySpent = 0, EnergyValue = cost, StarsSpent = 0, StarValue = cost },
                IsAutoPlay = true,
                PlayIndex = i,
                PlayCount = playCount,
            };
            // Self HP-loss cost (Breakthrough and friends) — unblockable HP
            // loss on the duelist, exactly like the card does to its caster.
            if (vars.ContainsKey("HpLoss"))
            {
                decimal hpLoss = Math.Max(0m, vars.HpLoss.BaseValue);
                if (hpLoss > 0m)
                {
                    await CreatureCmd.Damage(ctx, Creature, hpLoss,
                        ValueProp.Unblockable | ValueProp.Unpowered | ValueProp.Move, card, cardPlay);
                }
            }
            bool interpreted = false;
            if (card.Type == CardType.Attack)
            {
                bool needsOsty = card.Tags.Contains(CardTag.OstyAttack);
                if (!needsOsty || _mirrorOsty?.IsAlive == true)
                {
                    decimal damage = DamageOf(card, target);
                    int hits = HitCountOf(card, target);
                    if (needsOsty && _mirrorOsty != null)
                    {
                        await DamageCmd.Attack(damage).WithHitCount(hits)
                            .FromOsty(_mirrorOsty, card, cardPlay).Targeting(target)
                            .WithHitFx("vfx/vfx_attack_blunt").Execute(ctx);
                    }
                    else
                    {
                        await DamageCmd.Attack(damage).WithHitCount(hits).FromMonster(this)
                            .WithAttackerAnim("Attack", 0.3f).WithAttackerFx(null, AttackSfxPath)
                            .WithHitFx("vfx/vfx_attack_blunt").Execute(ctx);
                    }
                    interpreted = true;
                }
            }
            decimal block = BlockOf(card, target);
            if (block > 0m)
            {
                await CreatureCmd.GainBlock(Creature, block, ValueProp.Move, cardPlay);
                interpreted = true;
            }
            // Do not use vanilla OstyCmd with the fake player. NCombatRoom
            // classifies every PetOwner creature as an ally visual even when
            // its combat side is Enemy, which scrambles both sides' layout.
            // A regular enemy-side Osty plus a hidden guard power gives the
            // same combat result without corrupting the scene hierarchy.
            if (vars.ContainsKey("Summon") && _mirrorPlayer != null)
            {
                decimal summon = Clamp(vars.Summon.BaseValue, 0m, 30m);
                if (summon > 0m)
                {
                    await SummonMirrorOsty(ctx, summon, card);
                    interpreted = true;
                }
            }
            interpreted |= await ApplyTablePowers(card, target, ctx, cardPlay);
            if (vars.ContainsKey("Heal"))
            {
                decimal heal = Math.Max(0m, vars.Heal.BaseValue);
                if (heal > 0m)
                {
                    await CreatureCmd.Heal(Creature, heal);
                    interpreted = true;
                }
            }
            interpreted |= await ApplyRiders(card, target, CoveredVarKeys(card));
            interpreted |= await ApplySafeGeneratedCards(card, target);
            if (!interpreted)
            {
                Log.Info($"[MirrorDuelist] {card.Id.Entry} resolved as a safe dud.");
            }
        }
        // Curses, statuses and untranslatable skills are played as duds - the
        // duelist wastes the card and its energy, exactly like you would.
    }

    private async Task SummonMirrorOsty(PlayerChoiceContext ctx, decimal amount, CardModel source)
    {
        ICombatState? combatState = Creature.CombatState;
        if (combatState == null)
        {
            return;
        }
        if (_mirrorOsty == null || _mirrorOsty.IsDead || _mirrorOsty.CombatState == null)
        {
            _mirrorOsty = await CreatureCmd.Add(
                ModelDb.Monster<Osty>().ToMutable(),
                combatState,
                CombatSide.Enemy);
            await CreatureCmd.SetMaxAndCurrentHp(_mirrorOsty, amount);
            // Let cards owned by the synthetic mirror player resolve
            // card.Owner.Osty to THIS enemy-side Osty.  AddPetInternal cannot
            // be used here because it also sets PetOwner, which moves Osty to
            // the real player visual layer and breaks the combat layout.
            BindEnemyOstyToMirrorPlayer();
            _mirrorOsty.CurrentHpChanged += OnMirrorOstyHpChanged;
            PositionMirrorOsty();
            await PowerCmd.Apply<MirrorOstyGuardPower>(ctx, Creature, 1m, Creature, source);
        }
        else
        {
            await CreatureCmd.GainMaxHp(_mirrorOsty, amount);
            BindEnemyOstyToMirrorPlayer();
            PositionMirrorOsty();
        }
        RefreshMirrorCardVisuals();
    }

    /// <summary>
    /// Exposes the opponent's Osty to the mirror card owner without changing
    /// the creature's combat side or PetOwner.  This makes vanilla calculated
    /// variables such as Unleash/\u51fa\u51fb read the enemy Osty's HP, never the
    /// player's own Osty.
    /// </summary>
    private void BindEnemyOstyToMirrorPlayer()
    {
        if (_mirrorOsty == null || _mirrorPlayer?.PlayerCombatState?.Pets is not List<Creature> pets)
        {
            return;
        }
        pets.RemoveAll(c => c.Monster is Osty);
        pets.Add(_mirrorOsty);
    }

    private void OnMirrorOstyHpChanged(int oldHp, int newHp)
    {
        RefreshMirrorCardVisuals();
    }

    private void RefreshMirrorCardVisuals()
    {
        foreach (NCard node in _cardNodes.Values.ToList())
        {
            try
            {
                node.UpdateVisuals(PileType.Deck, CardPreviewMode.Normal);
            }
            catch
            {
                // Card text is cosmetic; never interrupt combat for it.
            }
        }
    }

    private void PositionMirrorOsty()
    {
        if (_mirrorOsty == null)
        {
            return;
        }
        try
        {
            NCreature? ownerNode = Creature.GetCreatureNode();
            NCreature? ostyNode = NCombatRoom.Instance?.GetCreatureNode(_mirrorOsty);
            if (ownerNode == null || ostyNode == null)
            {
                return;
            }
            // This encounter has no authored multi-monster slots. Dynamically
            // added regular enemies otherwise remain at their container origin.
            // Pin Osty just left of the duelist, still inside the enemy layer.
            ostyNode.GlobalPosition = ownerNode.GlobalPosition + new Vector2(-260f, 45f);
            // NCreature.OstyScaleToSize assumes Entity.PetOwner is non-null.
            // Mirror Osty is deliberately a regular enemy, so calling that
            // vanilla helper throws before the facing code below can run.
            // Reproduce only its visual size calculation without pet access.
            float size = Mathf.Lerp(
                Osty.ScaleRange.X,
                Osty.ScaleRange.Y,
                Mathf.Clamp(_mirrorOsty.MaxHp / 150f, 0f, 1f));
            ostyNode.Visuals.Scale = Vector2.One * size * ostyNode.Visuals.DefaultScale;
            // Osty's authored player-side sprite faces right. Only mirror the
            // visual body (not NCreature itself), otherwise its health bar and
            // status UI would also be reversed.
            Node2D? body = ostyNode.Visuals?.Body;
            if (body != null)
            {
                body.Scale = new Vector2(-Math.Abs(body.Scale.X), body.Scale.Y);
            }
        }
        catch
        {
            // Positioning is cosmetic; summon mechanics must still resolve.
        }
    }

    /// <summary>
    /// Safe replacements for cards that generate more cards. Enemy turns must
    /// never open a Choose-a-Card screen, so Discovery samples the same three
    /// cards as vanilla and lets a small AI pick one. Generated cards are then
    /// played immediately (free where vanilla makes them free) through this
    /// same interpreter, including recursively generated cards.
    /// </summary>
    private async Task<bool> ApplySafeGeneratedCards(CardModel source, Creature target)
    {
        if (_mirrorPlayer == null || source.CombatState == null)
        {
            return false;
        }
        string id = NormalizedId(source);
        if (id == "UPMYSLEEVE")
        {
            int count = source.DynamicVars.ContainsKey("Cards")
                ? Math.Clamp(source.DynamicVars.Cards.IntValue, 1, 6)
                : 3;
            for (int i = 0; i < count; i++)
            {
                CardModel shiv = source.CombatState.CreateCard<Shiv>(_mirrorPlayer);
                await PlayGeneratedCard(shiv, target, makeFree: true);
            }
            return true;
        }
        if (id == "DISCOVERY")
        {
            List<CardModel> choices = CardFactory.GetDistinctForCombat(
                    _mirrorPlayer,
                    _mirrorPlayer.Character.CardPool.GetUnlockedCards(
                        _mirrorPlayer.UnlockState,
                        _mirrorPlayer.RunState.CardMultiplayerConstraint),
                    3,
                    _mirrorPlayer.RunState.Rng.CombatCardGeneration)
                .ToList();
            CardModel? choice = choices
                .OrderByDescending(GeneratedCardScore)
                .ThenBy(_ => _mirrorPlayer.RunState.Rng.MonsterAi.NextFloat())
                .FirstOrDefault();
            if (choice != null)
            {
                Log.Info($"[MirrorDuelist] Discovery chose {choice.Id.Entry} from [{string.Join(", ", choices.Select(c => c.Id.Entry))}].");
                await PlayGeneratedCard(choice, target, makeFree: true);
            }
            return true;
        }
        return false;
    }

    private async Task PlayGeneratedCard(CardModel card, Creature target, bool makeFree)
    {
        if (_generatedPlayDepth >= MaxGeneratedPlayDepth ||
            _generatedCardsPlayedThisTurn >= MaxGeneratedCardsPerTurn)
        {
            Log.Info($"[MirrorDuelist] generated-card safety limit skipped {card.Id.Entry}.");
            return;
        }
        CardPile? hand = MirrorPile(PileType.Hand);
        if (hand == null)
        {
            return;
        }
        if (makeFree)
        {
            card.SetToFreeThisTurn();
        }
        if (!hand.Cards.Contains(card))
        {
            hand.AddInternal(card);
        }
        // Show the generated card briefly above the duelist, then remove only
        // that temporary node. The card model remains in discard/exhaust and
        // can be drawn again later when appropriate.
        AttachCardVisual(card, Creature.GetCreatureNode(), 1);
        await Cmd.Wait(0.12f);
        if (hand.Cards.Contains(card))
        {
            hand.RemoveInternal(card);
        }
        _generatedCardsPlayedThisTurn++;
        _generatedPlayDepth++;
        try
        {
            await PlayTurnCard(card, target);
        }
        finally
        {
            _generatedPlayDepth--;
            if (_cardNodes.Remove(card, out NCard? node))
            {
                node.QueueFreeSafely();
            }
            _cardHomes.Remove(card);
        }
    }

    private static int GeneratedCardScore(CardModel card)
    {
        int score = card.Type switch
        {
            CardType.Attack => 50,
            CardType.Power => 35,
            CardType.Skill => 25,
            _ => 0,
        };
        if (card.DynamicVars.ContainsKey("Damage") || card.DynamicVars.ContainsKey("CalculatedDamage")) score += 20;
        if (card.DynamicVars.ContainsKey("Block") || card.DynamicVars.ContainsKey("CalculatedBlock")) score += 12;
        if (card.DynamicVars.ContainsKey("PoisonPower") || card.DynamicVars.ContainsKey("VulnerablePower")) score += 10;
        // Prefer cards the safe interpreter can actually translate.
        if (NormalizedId(card) is "DISCOVERY" or "UPMYSLEEVE") score += 15;
        return score - Math.Max(0, card.CurrentStarCost) * 2;
    }

    private static string NormalizedId(CardModel card)
    {
        return new string(card.Id.Entry.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
    }

    /// <summary>
    /// Var keys this card's generated power table already covers, so the
    /// generic rider pass does not apply the same power twice.
    /// </summary>
    private static HashSet<string> CoveredVarKeys(CardModel card)
    {
        var covered = new HashSet<string>(StringComparer.Ordinal);
        if (MirrorCardPowers.Table.TryGetValue(NormalizedId(card), out CardPowerTranslation[]? translations))
        {
            foreach (CardPowerTranslation t in translations)
            {
                if (t.VarKey != null)
                {
                    covered.Add(t.VarKey);
                }
            }
        }
        return covered;
    }

    /// <summary>
    /// Applies the per-card power translations extracted from the vanilla card
    /// models (tools/gen_card_powers.py). "Model" entries apply the real
    /// PowerModel to the correct side — the duelist for self-buffs (Demon
    /// Form, Echo Form...), the targeted player for debuffs. "Energy"/"Draw"/
    /// "Block" entries feed the interpreter's pending next-turn economy since
    /// those vanilla powers only make sense on a real player.
    /// </summary>
    private async Task<bool> ApplyTablePowers(CardModel card, Creature target, PlayerChoiceContext ctx, CardPlay cardPlay)
    {
        if (!MirrorCardPowers.Table.TryGetValue(NormalizedId(card), out CardPowerTranslation[]? translations))
        {
            return false;
        }
        bool applied = false;
        foreach (CardPowerTranslation t in translations)
        {
            decimal amount = 1m;
            if (t.VarKey != null && card.DynamicVars.ContainsKey(t.VarKey))
            {
                amount = card.DynamicVars[t.VarKey].BaseValue;
            }
            amount = Math.Clamp(decimal.Truncate(amount), 0m, 99m);
            switch (t.Kind)
            {
                case "Energy":
                    _pendingEnergyBonus += (int)amount;
                    applied = true;
                    break;
                case "Draw":
                    _pendingDrawBonus += (int)amount;
                    applied = true;
                    break;
                case "Block":
                    _pendingBlockBonus += (int)amount;
                    applied = true;
                    break;
                default:
                    if (amount <= 0m)
                    {
                        break;
                    }
                    Type? powerType = ResolvePowerModel(t.Power);
                    if (powerType == null)
                    {
                        break;
                    }
                    try
                    {
                        // Model ctors self-register in ModelDb, so never
                        // Activator.CreateInstance a model: pull the canonical
                        // instance and clone it, exactly like vanilla call
                        // sites do.
                        PowerModel canonical = ModelDb.DebugPower(powerType);
                        Creature dest = t.Side == PowerSide.Self ? Creature : target;
                        await PowerCmd.Apply(ctx, canonical.ToMutable(), dest, amount, Creature, card);
                        applied = true;
                    }
                    catch (Exception e)
                    {
                        Log.Error($"[MirrorDuelist] table power {t.Power} failed for {card.Id.Entry}: {e}");
                    }
                    break;
            }
        }
        return applied;
    }

    private static readonly Dictionary<string, Type?> PowerModelTypes = new(StringComparer.Ordinal);

    private static Type? ResolvePowerModel(string? name)
    {
        if (name == null)
        {
            return null;
        }
        if (PowerModelTypes.TryGetValue(name, out Type? cached))
        {
            return cached;
        }
        Type? type = typeof(PowerModel).Assembly.GetType("MegaCrit.Sts2.Core.Models.Powers." + name);
        PowerModelTypes[name] = type;
        return type;
    }

    /// <summary>Rider powers carried on attack/skill cards (Bash's vulnerable,
    /// weak riders, self-buffs, poison...) land on the matching side after the
    /// primary effect, using the card's own printed amounts.</summary>
    private async Task<bool> ApplyRiders(CardModel card, Creature target, HashSet<string> coveredKeys)
    {
        DynamicVarSet vars = card.DynamicVars;
        var ctx = new ThrowingPlayerChoiceContext();
        bool applied = false;
        bool isBubbleBubble = card is BubbleBubble ||
            NormalizedId(card).EndsWith("BUBBLEBUBBLE", StringComparison.Ordinal);
        foreach ((string key, bool toSelf) in new[]
                 {
                     ("VulnerablePower", false), ("WeakPower", false),
                     ("PoisonPower", false), ("DoomPower", false),
                     ("StrengthPower", true), ("DexterityPower", true),
                     ("AccuracyPower", true),
                 })
        {
            if (!vars.ContainsKey(key) || coveredKeys.Contains(key))
            {
                continue;
            }
            // Bubble Bubble only poisons an already-poisoned target.  The
            // generic rider interpreter previously ignored that condition and
            // also capped every power at 4, turning its vanilla 9 (12 when
            // upgraded) into an unconditional 4 poison.
            if (key == "PoisonPower" && isBubbleBubble && !target.HasPower<PoisonPower>())
            {
                continue;
            }
            decimal amount = Math.Clamp(vars[key].BaseValue, 0m, 999m);
            if (amount <= 0m)
            {
                continue;
            }
            Creature dest = toSelf ? Creature : target;
            applied = true;
            switch (key)
            {
                case "VulnerablePower":
                    await PowerCmd.Apply<VulnerablePower>(ctx, dest, amount, Creature, null);
                    break;
                case "WeakPower":
                    await PowerCmd.Apply<WeakPower>(ctx, dest, amount, Creature, null);
                    break;
                case "PoisonPower":
                    await PowerCmd.Apply<PoisonPower>(ctx, dest, amount, Creature, null);
                    break;
                case "DoomPower":
                    await PowerCmd.Apply<DoomPower>(ctx, dest, amount, Creature, null);
                    break;
                case "StrengthPower":
                    await PowerCmd.Apply<StrengthPower>(ctx, dest, amount, Creature, null);
                    break;
                case "DexterityPower":
                    await PowerCmd.Apply<DexterityPower>(ctx, dest, amount, Creature, null);
                    break;
                case "AccuracyPower":
                    await PowerCmd.Apply<AccuracyPower>(ctx, dest, amount, Creature, card);
                    break;
            }
        }
        return applied;
    }

    private static bool IsConsumed(CardModel card)
    {
        try
        {
            // Powers are single-use per StS rules; exhaust cards leave the
            // deck. Keywords is the merged (canonical + local) keyword set.
            return card.Type == CardType.Power || card.Keywords.Contains(CardKeyword.Exhaust);
        }
        catch
        {
            return false;
        }
    }

    private decimal BlockOf(CardModel card, Creature? target)
    {
        decimal block = 0m;
        if (card.DynamicVars.ContainsKey("Block"))
        {
            block = card.DynamicVars["Block"].BaseValue;
        }
        else if (card.DynamicVars.TryGetValue("CalculatedBlock", out DynamicVar? value) && value is CalculatedVar calculated)
        {
            try { block = calculated.Calculate(target); } catch { block = 0m; }
        }
        EnchantmentModel? enchantment = card.Enchantment;
        if (enchantment != null)
        {
            block += enchantment.EnchantBlockAdditive(block);
        }
        return Clamp(block, 0m, 40m);
    }

    private decimal DamageOf(CardModel card, Creature? target)
    {
        // Unleash (Chinese title: 出击) always reads the opponent's Osty.
        // Rebuild the formula explicitly as a safety net even though the
        // synthetic card owner is also bound to that enemy-side creature for
        // vanilla preview calculation.
        bool scalesWithOstyHp = card is Unleash ||
            NormalizedId(card).EndsWith("UNLEASH", StringComparison.Ordinal);
        bool isShiv = card is Shiv || card.Tags.Contains(CardTag.Shiv);
        decimal damage;
        if (scalesWithOstyHp &&
            card.DynamicVars.ContainsKey("CalculationBase") &&
            card.DynamicVars.ContainsKey("ExtraDamage"))
        {
            Creature? enemyOsty = Creature.CombatState?.Enemies
                .FirstOrDefault(c => ReferenceEquals(c, _mirrorOsty) && c.IsAlive && c.Monster is Osty);
            decimal ostyHp = enemyOsty?.CurrentHp ?? 0m;
            damage = card.DynamicVars["CalculationBase"].BaseValue +
                card.DynamicVars["ExtraDamage"].BaseValue * ostyHp;
        }
        else if (card.DynamicVars.ContainsKey("Damage"))
        {
            damage = card.DynamicVars.Damage.BaseValue;
        }
        else if (card.DynamicVars.ContainsKey("OstyDamage"))
        {
            // Necrobinder attacks printed as Osty damage still resolve through
            // the enemy-side interpreter; the summoned Osty remains visible
            // and keeps its normal Die For You behaviour.
            damage = card.DynamicVars.OstyDamage.BaseValue;
        }
        else if (card.DynamicVars.TryGetValue("CalculatedDamage", out DynamicVar? value) && value is CalculatedVar calculated)
        {
            try { damage = calculated.Calculate(target); } catch { damage = DamageFallback; }
        }
        else
        {
            damage = DamageFallback;
        }
        damage = Clamp(damage, isShiv ? 0m : DamageClampMin,
            scalesWithOstyHp ? 999m : DamageClampMax);
        // Card enchantments ride along (Sharp/Momentum/Vigorous/Tezcataras's
        // Ember add damage, Corrupted/Instinct multiply it) — same hooks the
        // vanilla damage pipeline calls with the card as cardSource.
        EnchantmentModel? enchantment = card.Enchantment;
        if (enchantment != null)
        {
            damage += enchantment.EnchantDamageAdditive(damage, ValueProp.Move);
            damage *= enchantment.EnchantDamageMultiplicative(damage, ValueProp.Move);
        }
        // Shivs are 4 base damage.  They were incorrectly raised to 6 by the
        // monster fallback minimum. Accuracy adds its exact stacked amount,
        // matching the vanilla AccuracyPower rule for CardTag.Shiv.
        if (isShiv)
        {
            damage += Creature.GetPowerAmount<AccuracyPower>();
        }
        decimal minimum = isShiv ? 0m : DamageClampMin;
        return Clamp(damage, minimum, scalesWithOstyHp ? 999m : 60m);
    }

    private static readonly HashSet<string> FixedDoubleHitCards = new(StringComparer.OrdinalIgnoreCase)
    {
        "ASTRALPULSE", "DAGGERSPRAY", "MAUL", "REFRACT", "RIPANDTEAR",
        "THRASH", "TWINSTRIKE", "UPROAR",
    };

    private int HitCountOf(CardModel card, Creature? target)
    {
        DynamicVarSet vars = card.DynamicVars;
        if (vars.ContainsKey("Repeat"))
        {
            return Math.Clamp(vars.Repeat.IntValue, 1, 8);
        }
        if (vars.TryGetValue("CalculatedHits", out DynamicVar? value) && value is CalculatedVar calculated)
        {
            try { return Math.Clamp((int)calculated.Calculate(target), 1, 8); } catch { return 1; }
        }
        string id = NormalizedId(card);
        return FixedDoubleHitCards.Contains(id) ? 2 : 1;
    }

    private static decimal Clamp(decimal value, decimal min, decimal max)
    {
        return Math.Max(min, Math.Min(max, value));
    }

    private static int PowerAmount(decimal baseValue, int min, int max)
    {
        return Math.Clamp((int)MathF.Round((float)baseValue), min, max);
    }

    // ---- intents ----

    private AbstractIntent BuildIntent(CardModel? card)
    {
        if (card == null)
        {
            return new MirrorAttackIntent(null, StrikeDamage);
        }
        return new MirrorPlayedCardIntent(card);
    }

    /// <summary>Attack intent labelled with the stolen card's name. The damage
    /// detail lives in the base tooltip; the card face hanging overhead carries
    /// the full text.</summary>
    internal sealed class MirrorAttackIntent : SingleAttackIntent
    {
        private readonly CardModel? _card;

        public MirrorAttackIntent(CardModel? card, decimal damage)
            : base((int)Math.Round(damage))
        {
            _card = card;
        }

        public override LocString GetIntentLabel(IEnumerable<Creature> targets, Creature owner)
        {
            return _card?.TitleLocString ?? base.GetIntentLabel(targets, owner);
        }
    }

    internal sealed class MirrorStealIntent : AbstractIntent
    {
        public override IntentType IntentType => IntentType.CardDebuff;

        protected override string IntentPrefix => "MIRROR_STEAL";

        protected override string? SpritePath => null;

        public override Texture2D? GetTexture(IEnumerable<Creature> targets, Creature owner)
        {
            return MirrorIntentIcons.Steal;
        }

        public override string GetAnimation(IEnumerable<Creature> targets, Creature owner)
        {
            return "card_debuff";
        }
    }

    /// <summary>A single custom intent for every stolen card. The full card is
    /// already shown over the duelist, so this avoids falsely presenting
    /// skills as status-card insertion or attacks as ordinary monster moves.</summary>
    internal sealed class MirrorPlayedCardIntent : AbstractIntent
    {
        private readonly CardModel _card;

        public MirrorPlayedCardIntent(CardModel card)
        {
            _card = card;
        }

        public override IntentType IntentType => IntentType.CardDebuff;

        protected override string IntentPrefix => "MIRROR_PLAY_CARD";

        protected override string? SpritePath => null;

        public override Texture2D? GetTexture(IEnumerable<Creature> targets, Creature owner)
        {
            return MirrorIntentIcons.PlayCard;
        }

        public override string GetAnimation(IEnumerable<Creature> targets, Creature owner)
        {
            return "card_debuff";
        }

        public override LocString GetIntentLabel(IEnumerable<Creature> targets, Creature owner)
        {
            return _card.TitleLocString;
        }
    }

    /// <summary>
    /// One combat turn. The first instance is the steal move; every later
    /// instance chains from the previous one by re-registering under the same
    /// id (per-combat state machine, so id reuse is safe).
    /// </summary>
    internal sealed class MirrorMoveState : MoveState
    {
        private readonly MirrorDuelist _owner;

        public MonsterMoveStateMachine? Machine { get; set; }

        public MirrorMoveState(MirrorDuelist owner, string stateId, Func<IReadOnlyList<Creature>, Task> onPerform, params AbstractIntent[] intents)
            : base(stateId, onPerform, intents)
        {
            _owner = owner;
        }

        public override string GetNextState(Creature owner, Rng rng)
        {
            if (Machine == null)
            {
                throw new InvalidOperationException("Mirror move state was never bound to its state machine.");
            }
            MirrorMoveState next = _owner.BuildTurnMove(Machine);
            return next.Id;
        }
    }
}
