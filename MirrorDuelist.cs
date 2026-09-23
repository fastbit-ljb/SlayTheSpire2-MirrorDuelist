using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Commands.Builders;
using MegaCrit.Sts2.Core.Entities.Ascension;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Orbs;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Factories;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.CardPools;
using MegaCrit.Sts2.Core.Models.Enchantments;
using MegaCrit.Sts2.Core.Models.Monsters;
using MegaCrit.Sts2.Core.Models.Orbs;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Models.Relics;
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
    // Do not impose a fake monster minimum/maximum on copied cards. The old
    // six-damage floor made Claw, low-level multi-hit cards and generated
    // attacks stronger than their printed values.
    private const decimal DamageClampMin = 0m;
    private const decimal DamageClampMax = 999m;
    private const decimal DamageFallback = 8m;
    private const decimal StrikeDamage = 6m;
    private const float HpMultiplier = 1.5f;
    private const int StealCount = 3;
    private const int TurnEnergy = 5;
    // The mirror owns a real PlayerCombatState so Regent-style star cards can
    // use their actual resource instead of being treated as harmless duds.
    // Stars persist between turns just like they do for the Regent player.
    private const int InitialStars = 10;
    // Keep the real player-side CardModel pipeline enabled for the mirror.
    // The synthetic Player is deliberately not registered in CombatState
    // (doing so makes CombatManager wait for a non-existent extra player), so
    // we drive the same Vakuu/Whispering Earring loop from the enemy turn.
    private const bool UseNativeVakuuAutoplay = true;
    private const int NativeEnergyPerCycle = TurnEnergy;
    private const int NativeAutoplayCap = 64;
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
    private int _mirrorCardsDrawnThisCombat;
    private int _mirrorOrbsChanneledThisCombat;

    // Energy granted mid-turn by played cards (Bloodletting, Offering, ...);
    // the greedy continuation spends it the same turn.
    private int _bonusEnergyThisTurn;

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
        _mirrorCardsDrawnThisCombat = 0;
        _mirrorOrbsChanneledThisCombat = 0;
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
                // A copied player is not registered in the normal player-turn
                // setup, so CombatManager would otherwise leave its star
                // counter at zero.  Give the monster its requested starting
                // pool after the combat piles have been populated.
                await PlayerCmd.SetStars(InitialStars, _mirrorPlayer);
                // Vakuu is the vanilla relic that owns the automatic card
                // selection path.  We keep it on the synthetic player so
                // card hooks that inspect the owner's relics see the same
                // setup as the real player.  Its vanilla hook is capped at
                // 13 cards; the enemy driver below intentionally removes
                // that cap while retaining a hard recursion guard.
                if (_mirrorPlayer.GetRelic<WhisperingEarring>() == null)
                {
                    _mirrorPlayer.AddRelicInternal(
                        ModelDb.Relic<WhisperingEarring>().ToMutable(),
                        silent: true);
                }
                Log.Info($"[MirrorDuelist] mirror resource initialized: {InitialStars} stars.");
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
            var ctx = new ThrowingPlayerChoiceContext();
            _generatedPlayDepth = 0;
            _generatedCardsPlayedThisTurn = 0;
            CardPile? hand = MirrorPile(PileType.Hand);
            CardPile? discard = MirrorPile(PileType.Discard);
            await ApplyMirrorTurnStartPowers(ctx);
            // BlockNextTurnPower-style pendings land at the start of the turn.
            if (_pendingBlockBonus > 0)
            {
                int block = _pendingBlockBonus;
                _pendingBlockBonus = 0;
                await CreatureCmd.GainBlock(Creature, block, ValueProp.Move, null);
            }
            // Run the actual CardModel.OnPlay pipeline through the same
            // deterministic selector used by Whispering Earring (Vakuu).
            // This is deliberately a private enemy-turn driver instead of
            // registering a second Player in CombatState: CombatManager would
            // then wait for a real network/player turn and can hang or break
            // save-and-exit.  If the synthetic player could not be created,
            // retain the old UI-free interpreter as a safe fallback.
            if (UseNativeVakuuAutoplay && _mirrorPlayer != null &&
                await TryNativeVakuuTurn(plays, ctx))
            {
                _bonusEnergyThisTurn = 0;
                _plannedLeftoverEnergy = 0;
                return;
            }
            foreach (CardModel? card in plays)
            {
                await PlayTurnCard(card, target);
            }
            // Greedy continuation: cards drawn or kept this turn (Draw vars,
            // cheap leftovers) get played beyond the declared intents while
            // energy remains, exactly like a player spending their turn.
            // Bonus energy from played cards (Bloodletting, Offering...) joins
            // the leftover budget here.
            int energy = Math.Max(0, _plannedLeftoverEnergy) + _bonusEnergyThisTurn;
            _bonusEnergyThisTurn = 0;
            int extra = 0;
            while (energy > 0 && extra < 12 && hand != null)
            {
                if (energy > 0 && _bonusEnergyThisTurn != 0)
                {
                    energy = Math.Max(0, energy + _bonusEnergyThisTurn);
                    _bonusEnergyThisTurn = 0;
                }
                CardModel? pick = null;
                foreach (CardModel c in hand.Cards.OrderBy(_ => RunRng.MonsterAi.NextFloat()))
                {
                    if (PlayCost(c) <= energy && HasMirrorStarsFor(c))
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

    /// <summary>
    /// Runs stolen cards through the game's own CardModel.OnPlayWrapper path.
    /// This is the important difference from the legacy translation table:
    /// card hooks, replay, powers, generated cards and card-specific math all
    /// execute exactly as they do for a player.  Vakuu's selector is installed
    /// while the loop is active, so ordinary card-selection effects choose a
    /// deterministic option instead of opening the player's UI.
    ///
    /// The synthetic player is intentionally kept out of CombatState.  Adding
    /// it there changes PlayerCreatures and makes the turn manager wait for a
    /// second human/network player.  From the card engine's point of view the
    /// owner is still a real Player with a real PlayerCombatState and relics;
    /// from CombatManager's point of view this remains one enemy turn.
    /// </summary>
    private async Task<bool> TryNativeVakuuTurn(IReadOnlyList<CardModel?> planned, PlayerChoiceContext choiceContext)
    {
        Player? player = _mirrorPlayer;
        PlayerCombatState? combatPlayer = player?.PlayerCombatState;
        CardPile? hand = MirrorPile(PileType.Hand);
        CardPile? discard = MirrorPile(PileType.Discard);
        ICombatState? combatState = Creature.CombatState;
        if (player == null || combatPlayer == null || hand == null || discard == null || combatState == null)
        {
            return false;
        }

        // TakePlays removes the cards used for the displayed intents from the
        // hand.  Native autoplay expects them to still be in the hand, so put
        // those same instances back before selecting cards (never duplicate a
        // card: the one-card pool intentionally appears twice in `planned`).
        foreach (CardModel card in planned.OfType<CardModel>().Distinct())
        {
            if (hand.Cards.Contains(card))
            {
                continue;
            }
            if (card.Pile != null && card.Pile != hand)
            {
                card.RemoveFromCurrentPile(silent: true);
            }
            if (card.Pile == null)
            {
                await CardPileCmd.Add(card, hand, skipVisuals: true);
            }
        }

        if (hand.IsEmpty)
        {
            // Let the old path produce its normal Strike fallback when there
            // are no stolen cards at all.
            return false;
        }

        int energyRefresh = NativeEnergyRefresh(hand);
        combatPlayer.Energy = energyRefresh;
        int played = 0;
        bool hadPlayableCard = false;

        // This is the only intentional deviation from WhisperingEarring:
        // vanilla stops at 13 cards, while the mirror keeps refreshing its
        // turn energy and continues until the hand is empty.  The cap is a
        // last-resort recursion guard for cards that generate themselves.
        using (CardSelectCmd.PushSelector(new VakuuCardSelector()))
        {
            for (int safety = 0; safety < NativeAutoplayCap; safety++)
            {
                if (CombatManager.Instance.IsOverOrEnding || Creature.IsDead)
                {
                    break;
                }

                CardModel? card = hand.Cards
                    .Where(c => NativeTargetAvailable(c, combatState))
                    .FirstOrDefault(c => c.CanPlay());
                if (card == null)
                {
                    // A normal card may simply need another five-energy
                    // cycle.  If every remaining card is blocked by its own
                    // logic, a second scan will still be empty and we stop.
                    if (combatPlayer.Energy < energyRefresh)
                    {
                        combatPlayer.Energy = energyRefresh;
                        continue;
                    }
                    break;
                }

                hadPlayableCard = true;
                Creature? target = NativeTargetFor(card, combatState);
                try
                {
                    await card.SpendResources();
                    await CardCmd.AutoPlay(
                        choiceContext,
                        card,
                        target,
                        AutoPlayType.Default,
                        skipXCapture: true,
                        skipCardPileVisuals: true);
                    played++;
                }
                catch (Exception e)
                {
                    // Some cards still contain a genuinely player-only
                    // choice.  Do not let that choice freeze the enemy turn;
                    // file the card safely and continue with the next one.
                    Log.Error($"[MirrorDuelist] native Vakuu play failed for {card.Id.Entry}: {e}");
                    await MoveNativeFailureToDiscard(card, discard);
                    played++;
                }

                // Native OnPlayWrapper owns the pile transition, but the
                // mirror's stolen-card node is still parented above the
                // monster.  Reuse the existing visual settlement/cleanup so
                // powers and exhaust cards do not leave ghost cards behind.
                SettleVisual(card, _cardNodes.GetValueOrDefault(card));

                if (combatPlayer.Energy <= 0)
                {
                    combatPlayer.Energy = energyRefresh;
                }
            }
        }

        // If every card was blocked before a single native play, leave the
        // hand intact and let the legacy path produce its normal fallback.
        if (!hadPlayableCard || played == 0)
        {
            return false;
        }

        // Match the existing mirror turn contract: cards not selected by the
        // automatic player are discarded at enemy-turn end.  Powers and
        // exhausted cards have already left the hand in OnPlayWrapper.
        foreach (CardModel card in hand.Cards.ToList())
        {
            if (!discard.Cards.Contains(card))
            {
                await CardPileCmd.Add(card, discard, skipVisuals: true);
            }
        }

        return true;
    }

    private int NativeEnergyRefresh(CardPile hand)
    {
        int max = NativeEnergyPerCycle;
        foreach (CardModel card in hand.Cards)
        {
            try
            {
                if (!card.EnergyCost.CostsX)
                {
                    max = Math.Max(max, card.EnergyCost.GetWithModifiers(CostModifiers.All));
                }
            }
            catch
            {
                // Keep the five-energy fallback for cards with unusual costs.
            }
        }
        return Math.Clamp(max, NativeEnergyPerCycle, 99);
    }

    private bool NativeTargetAvailable(CardModel card, ICombatState combatState)
    {
        return card.TargetType switch
        {
            TargetType.AnyEnemy => NativeTargetFor(card, combatState) != null,
            TargetType.AnyAlly => NativeTargetFor(card, combatState) != null,
            _ => true,
        };
    }

    private Creature? NativeTargetFor(CardModel card, ICombatState combatState)
    {
        if (card.TargetType == TargetType.AnyEnemy)
        {
            // The owner is an enemy-side Creature, so CardCmd's default
            // HittableEnemies lookup would point back at the mirror.  Pass a
            // player-side opponent explicitly.
            return combatState.GetOpponentsOf(Creature)
                .FirstOrDefault(c => c.IsAlive && c.IsHittable);
        }
        if (card.TargetType == TargetType.AnyAlly)
        {
            return _mirrorOsty?.IsAlive == true ? _mirrorOsty : Creature;
        }
        if (card.TargetType == TargetType.AnyPlayer)
        {
            return Creature;
        }
        return null;
    }

    private static async Task MoveNativeFailureToDiscard(CardModel card, CardPile discard)
    {
        try
        {
            if (card.Pile == null)
            {
                await CardPileCmd.Add(card, discard, skipVisuals: true);
            }
            else if (card.Pile != discard)
            {
                await CardPileCmd.Add(card, discard, skipVisuals: true);
            }
        }
        catch
        {
            // A failed card is cosmetic state only; never propagate a second
            // pile error into CombatManager's turn coroutine.
        }
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
            _mirrorCardsDrawnThisCombat++;
        }
        var order = hand.Cards.OrderBy(_ => RunRng.MonsterAi.NextFloat()).ToList();
        var plays = new List<CardModel?>();
        int availableStars = CurrentMirrorStars();
        bool progress = true;
        while (energy > 0 && order.Count > 0 && progress)
        {
            progress = false;
            foreach (CardModel card in order)
            {
                int cost = PlayCost(card);
                int starCost = MirrorStarCost(card);
                if (cost <= energy && starCost <= availableStars)
                {
                    plays.Add(card);
                    energy -= cost;
                    availableStars -= starCost;
                    order.Remove(card);
                    hand.RemoveInternal(card);
                    progress = true;
                    break;
                }
            }
        }
        // Spec: with the whole pool down to a single card, it is played twice.
        CardModel? onlyPlay = plays.Count == 1 ? plays[0] : null;
        if (onlyPlay != null && draw.Cards.Count == 0 && hand.Cards.Count == 0 && discard.Cards.Count == 0 &&
            MirrorStarCost(onlyPlay) <= availableStars)
        {
            plays.Add(onlyPlay);
            availableStars -= MirrorStarCost(onlyPlay);
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

    private int CurrentMirrorStars()
    {
        return Math.Max(0, _mirrorPlayer?.PlayerCombatState?.Stars ?? 0);
    }

    private static int MirrorStarCost(CardModel card)
    {
        // A negative canonical cost means that the card does not use stars.
        // GetStarCostWithModifiers also accounts for temporary effects such as
        // Enlightenment and generated free cards.
        return Math.Max(0, card.GetStarCostWithModifiers());
    }

    private bool HasMirrorStarsFor(CardModel card)
    {
        return MirrorStarCost(card) <= CurrentMirrorStars();
    }

    private async Task<bool> SpendMirrorStars(CardModel card)
    {
        if (_mirrorPlayer == null)
        {
            return false;
        }
        int amount = MirrorStarCost(card);
        card.LastStarsSpent = amount;
        if (amount <= 0)
        {
            return true;
        }
        if (amount > CurrentMirrorStars())
        {
            Log.Info($"[MirrorDuelist] skipped {card.Id.Entry}: needs {amount} stars, has {CurrentMirrorStars()}.");
            return false;
        }
        await PlayerCmd.LoseStars(amount, _mirrorPlayer);
        return true;
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
            if (!await SpendMirrorStars(card))
            {
                break;
            }
            int starCost = MirrorStarCost(card);
            var cardPlay = new CardPlay
            {
                Card = card,
                Player = card.Owner!,
                Target = target,
                ResultPile = PileType.Discard,
                Resources = new ResourceInfo { EnergySpent = 0, EnergyValue = cost, StarsSpent = starCost, StarValue = starCost },
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
                    await ExecuteMirrorAttack(card, cardPlay, target, ctx, damage, hits, needsOsty);
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
            interpreted |= await TrySpecialEffect(card, target, ctx, cardPlay);
            interpreted |= await ApplyCardEffects(card, target, ctx, cardPlay);
            interpreted |= await ApplyTablePowers(card, target, ctx, cardPlay);
            interpreted |= await ApplyMirrorStorm(card, ctx);
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
        // The normal CardModel play wrapper dispatches the card's
        // enchantment hooks after all replayed copies finish.  The mirror
        // deliberately avoids that wrapper, so consume one-shot enchantments
        // here as well (Glam/华彩 and Vigorous/活力 must not work every turn).
        if (card.Enchantment != null)
        {
            try
            {
                await card.Enchantment.AfterCardPlayed(ctx, new CardPlay
                {
                    Card = card,
                    Player = card.Owner!,
                    Target = target,
                    ResultPile = PileType.Discard,
                    Resources = new ResourceInfo { EnergySpent = 0, EnergyValue = PlayCost(card), StarsSpent = 0, StarValue = PlayCost(card) },
                    IsAutoPlay = true,
                    PlayIndex = 0,
                    PlayCount = 1,
                });
            }
            catch (Exception e)
            {
                Log.Error($"[MirrorDuelist] enchantment cleanup failed for {card.Id.Entry}: {e}");
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
    /// <summary>
    /// Executes the data-driven effect table (MirrorCardEffects) filled from
    /// the per-card source audit. Amounts resolve from the card's own vars
    /// first, falling back to the stored constant.
    /// </summary>
    private async Task<bool> ApplyCardEffects(CardModel card, Creature target, PlayerChoiceContext ctx, CardPlay cardPlay)
    {
        if (!MirrorCardEffects.Table.TryGetValue(NormalizedId(card), out CardEffect[]? effects))
        {
            return false;
        }
        bool applied = false;
        foreach (CardEffect effect in effects)
        {
            decimal amount = ResolveAmount(card, effect.VarKey, effect.Const);
            switch (effect.Kind)
            {
                case "damage":
                {
                    if (amount <= 0m)
                    {
                        break;
                    }
                    bool isShiv = card.Tags.Contains(CardTag.Shiv);
                    decimal clamped = Clamp(amount, 0m, DamageClampMax);
                    await ExecuteMirrorAttack(card, cardPlay, target, ctx, clamped,
                        Math.Clamp(effect.Hits, 1, 8), fromOsty: false);
                    applied = true;
                    break;
                }
                case "block":
                    if (amount > 0m)
                    {
                        await CreatureCmd.GainBlock(Creature, Clamp(amount, 0m, 40m), ValueProp.Move, cardPlay);
                        applied = true;
                    }
                    break;
                case "power":
                {
                    Type? powerType = ResolvePowerModel(effect.Power);
                    if (amount <= 0m || powerType == null)
                    {
                        break;
                    }
                    try
                    {
                        PowerModel canonical = ModelDb.DebugPower(powerType);
                        Creature dest = effect.Side == EffectTarget.Self ? Creature : target;
                        await PowerCmd.Apply(ctx, canonical.ToMutable(), dest, amount, Creature, card);
                        applied = true;
                    }
                    catch (Exception e)
                    {
                        Log.Error($"[MirrorDuelist] effect power {effect.Power} failed for {card.Id.Entry}: {e}");
                    }
                    break;
                }
                case "heal":
                    if (amount > 0m)
                    {
                        await CreatureCmd.Heal(Creature, amount);
                        applied = true;
                    }
                    break;
                case "hp_loss":
                    if (amount > 0m)
                    {
                        await CreatureCmd.Damage(ctx, Creature, amount,
                            ValueProp.Unblockable | ValueProp.Unpowered | ValueProp.Move, card, cardPlay);
                        applied = true;
                    }
                    break;
                case "draw_hand":
                    if (amount > 0m)
                    {
                        DrawFromMirrorDrawIntoHand((int)amount);
                        applied = true;
                    }
                    break;
                case "add_status":
                    await AddStatusCards(effect, card);
                    applied = true;
                    break;
                case "shivs":
                {
                    int n = (int)Math.Clamp(amount, 1m, 6m);
                    for (int i = 0; i < n; i++)
                    {
                        CardModel shiv = card.CombatState!.CreateCard<Shiv>(_mirrorPlayer!);
                        await PlayGeneratedCard(shiv, target, makeFree: true);
                    }
                    applied = true;
                    break;
                }
                case "energy_now":
                    if (amount > 0m)
                    {
                        _bonusEnergyThisTurn += (int)amount;
                        applied = true;
                    }
                    break;
                case "summon_osty":
                    if (_mirrorPlayer != null && amount > 0m)
                    {
                        await SummonMirrorOsty(ctx, Math.Clamp(amount, 0m, 30m), card);
                        applied = true;
                    }
                    break;
                case "forge":
                {
                    if (_mirrorPlayer == null || amount <= 0m)
                    {
                        break;
                    }
                    try
                    {
                        await ForgeCmd.Forge((int)amount, _mirrorPlayer, card);
                        applied = true;
                    }
                    catch (Exception e)
                    {
                        Log.Error($"[MirrorDuelist] forge failed for {card.Id.Entry}: {e}");
                    }
                    break;
                }
                case "max_hp_loss":
                    if (amount > 0m)
                    {
                        await CreatureCmd.LoseMaxHp(ctx, Creature, amount, isFromCard: true);
                        applied = true;
                    }
                    break;
            }
        }
        return applied;
    }

    private static decimal ResolveAmount(CardModel card, string? varKey, decimal constValue)
    {
        if (varKey != null && card.DynamicVars.ContainsKey(varKey))
        {
            return card.DynamicVars[varKey].BaseValue;
        }
        return constValue;
    }

    /// <summary>Creates n copies of a status card and files them into the
    /// duelist's own discard/draw, or the real player's discard for
    /// side-inverted "shuffle statuses into the enemy's pile" effects.</summary>
    private async Task AddStatusCards(CardEffect effect, CardModel source)
    {
        if (_mirrorPlayer == null || effect.Status == null || source.CombatState == null)
        {
            return;
        }
        CardModel? canonical = ModelDb.GetByIdOrNull<CardModel>(new ModelId("CARD", Slug(effect.Status)));
        if (canonical == null)
        {
            Log.Error($"[MirrorDuelist] unknown status card {effect.Status}");
            return;
        }
        Player? realPlayer = CurrentPlayer();
        CardPile? pile = effect.Pile switch
        {
            "own_draw" => MirrorPile(PileType.Draw),
            "target_discard" => realPlayer != null ? PileType.Discard.GetPile(realPlayer) : null,
            _ => MirrorPile(PileType.Discard),
        };
        if (pile == null)
        {
            return;
        }
        for (int i = 0; i < Math.Clamp((int)effect.Const, 1m, 6m); i++)
        {
            CardModel status = canonical.CreateDupe(_mirrorPlayer);
            if (!pile.Cards.Contains(status))
            {
                await CardPileCmd.Add(status, pile, skipVisuals: true);
            }
        }
    }

    /// <summary>Mirrors StringHelper.Slugify for PascalCase class names.</summary>
    private static string Slug(string name)
    {
        string withUnderscores = System.Text.RegularExpressions.Regex.Replace(name.Trim(), "([A-Za-z0-9])([A-Z])", "$1_$2");
        return System.Text.RegularExpressions.Regex.Replace(withUnderscores.ToUpperInvariant(), @"[^A-Z0-9_]", "");
    }

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
                        if (t.Side == PowerSide.Target && card.TargetType == TargetType.AllEnemies &&
                            Creature.CombatState != null)
                        {
                            foreach (Creature dest in MirrorEnemyTargets(target))
                            {
                                await PowerCmd.Apply(ctx, canonical.ToMutable(), dest, amount, Creature, card);
                                applied = true;
                            }
                        }
                        else
                        {
                            Creature dest = t.Side == PowerSide.Self ? Creature : target;
                            await PowerCmd.Apply(ctx, canonical.ToMutable(), dest, amount, Creature, card);
                            applied = true;
                        }
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

    /// <summary>
    /// Bespoke translations for cards whose vanilla OnPlay does something the
    /// data tables cannot express (dynamic amounts, hand sweeps, teammate
    /// draws). Verified against each card's decompiled OnPlay; these ids are
    /// excluded from the generated power table to avoid double application.
    /// </summary>
    private async Task<bool> TrySpecialEffect(CardModel card, Creature target, PlayerChoiceContext ctx, CardPlay cardPlay)
    {
        // Orb cards cannot use the normal card OnPlay path here: that path is
        // allowed to open player-only selection UI, which would deadlock an
        // enemy turn. Resolve the Defect orb operations directly instead.
        if (await TryOrbCardEffect(card, target, ctx, cardPlay))
        {
            return true;
        }
        // Regent cards use the star resource directly. Keep this separate
        // from the generated effect table so a card can gain stars and still
        // perform its normal damage/block/draw effect in the same play.
        bool starEffectApplied = await ApplyMirrorStarCardEffect(card, target);
        switch (NormalizedId(card))
        {
            // Deterministic replacements for vanilla choose-a-card screens.
            // The mirror picks the highest-scoring legal card and never opens
            // a player UI during the enemy turn.
            case "ABUNDANCE":
            {
                IEnumerable<CardModel> pool = _mirrorPlayer!.Character.CardPool
                    .GetUnlockedCards(_mirrorPlayer.UnlockState, _mirrorPlayer.RunState.CardMultiplayerConstraint)
                    .Where(c => c.Type == CardType.Power);
                CardModel? choice = CardFactory.GetDistinctForCombat(_mirrorPlayer, pool, 3,
                        _mirrorPlayer.RunState.Rng.CombatCardGeneration)
                    .OrderByDescending(GeneratedCardScore).FirstOrDefault();
                if (choice != null)
                {
                    CardCmd.Upgrade(choice);
                    await AddMirrorGeneratedToHand(choice, freeThisTurn: true);
                }
                return true;
            }
            case "CLEANSE":
                await ExhaustMirrorDrawCards(1);
                return true;
            case "DECISIONSDECISIONS":
            {
                DrawFromMirrorDrawIntoHand(card.DynamicVars.ContainsKey("Cards")
                    ? card.DynamicVars.Cards.IntValue : 3);
                CardModel? choice = MirrorPile(PileType.Hand)?.Cards
                    .Where(c => c.Type == CardType.Skill && !c.Keywords.Contains(CardKeyword.Unplayable))
                    .OrderByDescending(GeneratedCardScore).FirstOrDefault();
                if (choice != null)
                {
                    MirrorPile(PileType.Hand)?.RemoveInternal(choice);
                    int repeats = card.DynamicVars.ContainsKey("Repeat") ? card.DynamicVars.Repeat.IntValue : 3;
                    for (int i = 0; i < Math.Clamp(repeats, 1, 5); i++)
                    {
                        await PlayTurnCard(choice, target);
                    }
                }
                return true;
            }
            case "NIGHTMARE":
            {
                CardModel? choice = MirrorPile(PileType.Hand)?.Cards
                    .OrderByDescending(GeneratedCardScore).FirstOrDefault();
                if (choice != null)
                {
                    // Nightmare's normal effect waits for the next hand draw.
                    // The mirror has no player draw phase, so put the three
                    // deterministic copies into its hand immediately.
                    for (int i = 0; i < 3; i++)
                    {
                        await AddMirrorGeneratedToHand(choice.CreateClone(), freeThisTurn: false);
                    }
                }
                return true;
            }
            case "SPLASH":
            {
                IEnumerable<CardModel> otherCharacterCards = _mirrorPlayer!.UnlockState.CharacterCardPools
                    .Where(p => p != _mirrorPlayer.Character.CardPool)
                    .SelectMany(p => p.GetUnlockedCards(_mirrorPlayer.UnlockState,
                        _mirrorPlayer.RunState.CardMultiplayerConstraint))
                    .Where(c => c.Type == CardType.Attack);
                CardModel? choice = CardFactory.GetDistinctForCombat(_mirrorPlayer, otherCharacterCards, 3,
                        _mirrorPlayer.RunState.Rng.CombatCardGeneration)
                    .OrderByDescending(GeneratedCardScore).FirstOrDefault();
                if (choice != null)
                {
                    if (card.IsUpgraded)
                    {
                        CardCmd.Upgrade(choice);
                    }
                    await AddMirrorGeneratedToHand(choice, freeThisTurn: true);
                }
                return true;
            }
            case "STORM":
                return await ApplyMirrorSelfPower(card, ctx, "StormPower");
            case "LOOP":
                return await ApplyMirrorSelfPower(card, ctx, "LoopPower");
            case "THUNDER":
                return await ApplyMirrorSelfPower(card, ctx, "ThunderPower");
            case "HAILSTORM":
                return await ApplyMirrorSelfPower(card, ctx, "HailstormPower");
            case "SPINNER":
                return await ApplyMirrorSelfPower(card, ctx, "SpinnerPower");
            case "CONSUMINGSHADOW":
                return await ApplyMirrorSelfPower(card, ctx, "ConsumingShadowPower");
            case "ALLFORONE":
                MoveZeroCostDiscardToMirrorHand();
                return true;
            case "CLAW":
                BuffMirrorCopies("CLAW", card.DynamicVars.ContainsKey("Increase")
                    ? card.DynamicVars["Increase"].BaseValue : 2m);
                return true;
            case "MAUL":
                BuffMirrorCopies("MAUL", card.DynamicVars.ContainsKey("Increase")
                    ? card.DynamicVars["Increase"].BaseValue : 2m);
                return true;
            case "BEATINTOSHAPE":
            {
                if (_mirrorPlayer == null || card.DynamicVars["CalculatedForge"] is not CalculatedVar calculated)
                {
                    return false;
                }
                try
                {
                    decimal amount = calculated.Calculate(target);
                    if (card.DynamicVars.ContainsKey("CalculationExtra"))
                    {
                        amount -= HitCountOf(card, target) * card.DynamicVars["CalculationExtra"].BaseValue;
                    }
                    if (amount > 0m)
                    {
                        await ForgeCmd.Forge((int)amount, _mirrorPlayer, card);
                    }
                    return true;
                }
                catch (Exception e)
                {
                    Log.Error($"[MirrorDuelist] Beat Into Shape forge failed: {e}");
                    return false;
                }
            }
            case "DRAINPOWER":
            {
                int count = card.DynamicVars.ContainsKey("Cards") ? card.DynamicVars.Cards.IntValue : 2;
                CardPile? discard = MirrorPile(PileType.Discard);
                foreach (CardModel upgrade in discard?.Cards.Where(c => c.IsUpgradable)
                    .OrderByDescending(GeneratedCardScore).Take(Math.Max(0, count)).ToList()
                    ?? Enumerable.Empty<CardModel>())
                {
                    CardCmd.Upgrade(upgrade);
                }
                return true;
            }
            case "FIENDFIRE":
            {
                int count = MirrorPile(PileType.Hand)?.Cards.Count ?? 0;
                await ExhaustMirrorCards(count);
                return true;
            }
            case "REBOOT":
            {
                MoveMirrorHandToDraw();
                ShuffleMirrorDraw();
                DrawFromMirrorDrawIntoHand(card.DynamicVars.ContainsKey("Cards")
                    ? card.DynamicVars.Cards.IntValue : 4);
                return true;
            }
            case "SCRAPE":
            {
                int count = card.DynamicVars.ContainsKey("Cards") ? card.DynamicVars.Cards.IntValue : 4;
                CardPile? hand = MirrorPile(PileType.Hand);
                HashSet<CardModel> before = hand == null ? new() : hand.Cards.ToHashSet();
                DrawFromMirrorDrawIntoHand(count);
                IEnumerable<CardModel> drawnExpensive = (hand?.Cards ?? Array.Empty<CardModel>())
                    .Where(c => !before.Contains(c) && (c.EnergyCost.GetWithModifiers(CostModifiers.All) != 0 || c.EnergyCost.CostsX))
                    .ToList();
                DiscardMirrorCards(drawnExpensive);
                return true;
            }
            case "PILLAGE":
            {
                CardPile? draw = MirrorPile(PileType.Draw);
                CardPile? hand = MirrorPile(PileType.Hand);
                while (draw != null && hand != null && hand.Cards.Count < CardPile.MaxCardsInHand && draw.Cards.Count > 0)
                {
                    CardModel top = draw.Cards[^1];
                    draw.RemoveInternal(top);
                    if (!hand.Cards.Contains(top))
                    {
                        hand.AddInternal(top);
                    }
                    if (top.Type != CardType.Attack)
                    {
                        break;
                    }
                }
                return true;
            }
            // Generated-card cards: make the same random choices as vanilla,
            // but file the results into the mirror player's private hand or
            // draw pile instead of opening a player selection screen.
            case "BLADEDANCE":
                await AddMirrorShivs(card, target,
                    card.DynamicVars.ContainsKey("Cards") ? card.DynamicVars.Cards.IntValue : 3,
                    enchanted: false, playImmediately: false);
                return true;
            case "BLADEOFINK":
                await AddMirrorShivs(card, target,
                    card.DynamicVars.ContainsKey("Cards") ? card.DynamicVars.Cards.IntValue : 2,
                    enchanted: true, playImmediately: false);
                return true;
            case "BLADESYMPHONY":
                await AddMirrorShivs(card, target,
                    card.DynamicVars.ContainsKey("Cards") ? card.DynamicVars.Cards.IntValue : 2,
                    enchanted: false, playImmediately: false);
                return true;
            case "STORMOFSTEEL":
            {
                CardPile? hand = MirrorPile(PileType.Hand);
                int count = hand?.Cards.Count ?? 0;
                if (hand != null)
                {
                    CardPile? discard = MirrorPile(PileType.Discard);
                    foreach (CardModel c in hand.Cards.ToList())
                    {
                        hand.RemoveInternal(c);
                        if (discard != null && !discard.Cards.Contains(c))
                        {
                            discard.AddInternal(c);
                        }
                    }
                }
                await AddMirrorShivs(card, target, count, enchanted: false, playImmediately: false,
                    upgraded: card.IsUpgraded);
                return true;
            }
            case "HIDDENDAGGERS":
            {
                int discardCount = card.DynamicVars.ContainsKey("Cards") ? card.DynamicVars.Cards.IntValue : 2;
                await DiscardMirrorCards(discardCount);
                int shivs = card.DynamicVars.ContainsKey("Shivs") ? card.DynamicVars["Shivs"].IntValue : 2;
                await AddMirrorShivs(card, target, shivs, enchanted: false, playImmediately: false,
                    upgraded: card.IsUpgraded);
                return true;
            }
            case "HIDDENCACHE":
            {
                // Hidden Cache also banks stars for the next turn. Its
                // vanilla implementation applies a player hook, so apply
                // the real power to the mirror creature explicitly.
                Type? powerType = ResolvePowerModel("StarNextTurnPower");
                if (powerType != null)
                {
                    try
                    {
                        PowerModel canonical = ModelDb.DebugPower(powerType);
                        decimal amount = card.DynamicVars.ContainsKey("StarNextTurnPower")
                            ? card.DynamicVars["StarNextTurnPower"].BaseValue
                            : 3m;
                        await PowerCmd.Apply(ctx, canonical.ToMutable(), Creature, amount, Creature, card);
                    }
                    catch (Exception e)
                    {
                        Log.Error($"[MirrorDuelist] Hidden Cache star power failed: {e}");
                    }
                }
                return true;
            }
            case "BUNDLEOFJOY":
            case "JACKOFALLTRADES":
            case "LARGESSE":
            case "QUASAR":
            {
                int count = card.DynamicVars.ContainsKey("Cards") ? card.DynamicVars.Cards.IntValue : 1;
                IEnumerable<CardModel> pool = ModelDb.CardPool<ColorlessCardPool>()
                    .GetUnlockedCards(_mirrorPlayer!.UnlockState, _mirrorPlayer.RunState.CardMultiplayerConstraint);
                IEnumerable<CardModel> generated = CardFactory.GetDistinctForCombat(
                    _mirrorPlayer, pool, Math.Clamp(count, 1, 3),
                    _mirrorPlayer.RunState.Rng.CombatCardGeneration);
                foreach (CardModel generatedCard in generated)
                {
                    if (card.IsUpgraded)
                    {
                        CardCmd.Upgrade(generatedCard);
                    }
                    await AddMirrorGeneratedToHand(generatedCard, freeThisTurn: false);
                }
                return true;
            }
            case "DISTRACTION":
            case "INFERNALBLADE":
            case "WHITENOISE":
            {
                IEnumerable<CardModel> pool = _mirrorPlayer!.Character.CardPool.GetUnlockedCards(
                    _mirrorPlayer.UnlockState, _mirrorPlayer.RunState.CardMultiplayerConstraint);
                CardType wanted = NormalizedId(card) switch
                {
                    "DISTRACTION" => CardType.Skill,
                    "INFERNALBLADE" => CardType.Attack,
                    _ => CardType.Power,
                };
                CardModel? generatedCard = CardFactory.GetDistinctForCombat(
                    _mirrorPlayer, pool.Where(c => c.Type == wanted), 1,
                    _mirrorPlayer.RunState.Rng.CombatCardGeneration).FirstOrDefault();
                if (generatedCard != null)
                {
                    await AddMirrorGeneratedToHand(generatedCard, freeThisTurn: true);
                }
                return true;
            }
            case "METAMORPHOSIS":
            {
                int count = card.DynamicVars.ContainsKey("Cards") ? card.DynamicVars.Cards.IntValue : 3;
                IEnumerable<CardModel> pool = _mirrorPlayer!.Character.CardPool.GetUnlockedCards(
                    _mirrorPlayer.UnlockState, _mirrorPlayer.RunState.CardMultiplayerConstraint)
                    .Where(c => c.Type == CardType.Attack);
                CardPile? draw = MirrorPile(PileType.Draw);
                if (draw != null)
                {
                    foreach (CardModel generatedCard in CardFactory.GetForCombat(
                        _mirrorPlayer, pool, Math.Clamp(count, 1, 6),
                        _mirrorPlayer.RunState.Rng.CombatCardGeneration))
                    {
                        generatedCard.SetToFreeThisCombat();
                        draw.AddInternal(generatedCard);
                    }
                }
                return true;
            }
            case "OVERCLOCK":
            {
                int count = card.DynamicVars.ContainsKey("Cards") ? card.DynamicVars.Cards.IntValue : 2;
                DrawFromMirrorDrawIntoHand(count);
                CardModel burn = card.CombatState!.CreateCard<Burn>(_mirrorPlayer!);
                CardPile? discard = MirrorPile(PileType.Discard);
                if (discard != null && !discard.Cards.Contains(burn))
                {
                    discard.AddInternal(burn);
                }
                return true;
            }
            case "STOKE":
            {
                CardPile? hand = MirrorPile(PileType.Hand);
                CardPile? exhaust = MirrorPile(PileType.Exhaust);
                int count = hand?.Cards.Count ?? 0;
                if (hand != null && exhaust != null)
                {
                    foreach (CardModel c in hand.Cards.ToList())
                    {
                        hand.RemoveInternal(c);
                        if (!exhaust.Cards.Contains(c))
                        {
                            exhaust.AddInternal(c);
                        }
                    }
                }
                IEnumerable<CardModel> pool = _mirrorPlayer!.Character.CardPool.GetUnlockedCards(
                    _mirrorPlayer.UnlockState, _mirrorPlayer.RunState.CardMultiplayerConstraint);
                foreach (CardModel generatedCard in CardFactory.GetForCombat(
                    _mirrorPlayer, pool, count, _mirrorPlayer.RunState.Rng.CombatCardGeneration))
                {
                    if (card.IsUpgraded)
                    {
                        CardCmd.Upgrade(generatedCard);
                    }
                    await AddMirrorGeneratedToHand(generatedCard, freeThisTurn: false);
                }
                return true;
            }
            case "TURBO":
            {
                int energy = card.DynamicVars.ContainsKey("Energy") ? card.DynamicVars.Energy.IntValue : 2;
                _bonusEnergyThisTurn += Math.Max(0, energy);
                CardModel voidCard = card.CombatState!.CreateCard<MegaCrit.Sts2.Core.Models.Cards.Void>(_mirrorPlayer!);
                CardPile? discard = MirrorPile(PileType.Discard);
                if (discard != null && !discard.Cards.Contains(voidCard))
                {
                    discard.AddInternal(voidCard);
                }
                return true;
            }
            // Automatic, deterministic replacements for hand/draw selection.
            case "ACROBATICS":
            case "PREPARED":
            {
                int drawCount = card.DynamicVars.ContainsKey("Cards") ? card.DynamicVars.Cards.IntValue : 1;
                DrawFromMirrorDrawIntoHand(drawCount);
                await DiscardMirrorCards(drawCount);
                return true;
            }
            case "BURNINGPACT":
            {
                await ExhaustMirrorCards(1);
                int drawCount = card.DynamicVars.ContainsKey("Cards") ? card.DynamicVars.Cards.IntValue : 2;
                DrawFromMirrorDrawIntoHand(drawCount);
                return true;
            }
            case "DREDGE":
            {
                int count = card.DynamicVars.ContainsKey("Cards") ? card.DynamicVars.Cards.IntValue : 3;
                MoveDiscardToMirrorHand(count);
                return true;
            }
            case "DUALWIELD":
            {
                int count = card.DynamicVars.ContainsKey("Cards") ? card.DynamicVars.Cards.IntValue : 1;
                CardPile? hand = MirrorPile(PileType.Hand);
                CardModel? source = hand?.Cards
                    .Where(c => c.Type is CardType.Attack or CardType.Power)
                    .OrderByDescending(GeneratedCardScore).FirstOrDefault();
                if (source != null)
                {
                    for (int i = 0; i < Math.Clamp(count, 1, 3); i++)
                    {
                        await AddMirrorGeneratedToHand(source.CreateDupe(_mirrorPlayer!), freeThisTurn: false);
                    }
                }
                return true;
            }
            case "SECRETTECHNIQUE":
                MoveTopMatchingDrawToHand(CardType.Skill);
                return true;
            case "SECRETWEAPON":
            case "TUTOR":
                MoveTopMatchingDrawToHand(CardType.Attack);
                return true;
            case "WISH":
                MoveTopMatchingDrawToHand(null);
                return true;
            case "GLIMMER":
            {
                int drawCount = card.DynamicVars.ContainsKey("Cards") ? card.DynamicVars.Cards.IntValue : 3;
                DrawFromMirrorDrawIntoHand(drawCount);
                MoveHighestMirrorHandToDraw();
                return true;
            }
            case "THINKINGAHEAD":
            {
                int drawCount = card.DynamicVars.ContainsKey("Cards") ? card.DynamicVars.Cards.IntValue : 2;
                DrawFromMirrorDrawIntoHand(drawCount);
                MoveHighestMirrorHandToDraw();
                return true;
            }
            case "PURITY":
                await ExhaustMirrorCards(card.DynamicVars.ContainsKey("Cards") ? card.DynamicVars.Cards.IntValue : 3);
                return true;
            case "TRANSFIGURE":
            {
                CardModel? selected = MirrorPile(PileType.Hand)?.Cards
                    .OrderByDescending(GeneratedCardScore).FirstOrDefault();
                if (selected != null && !selected.EnergyCost.CostsX)
                {
                    selected.EnergyCost.AddThisCombat(1);
                    selected.BaseReplayCount++;
                }
                return selected != null;
            }
            case "PRIMALFORCE":
            {
                CardPile? hand = MirrorPile(PileType.Hand);
                if (hand == null)
                {
                    return false;
                }
                foreach (CardModel selected in hand.Cards.Where(c => c.Type == CardType.Attack && c.IsTransformable).ToList())
                {
                    await CardCmd.TransformTo<GiantRock>(selected);
                }
                return true;
            }
            case "BEGONE":
            {
                CardModel? selected = MirrorPile(PileType.Hand)?.Cards
                    .OrderByDescending(GeneratedCardScore).FirstOrDefault();
                if (selected != null)
                {
                    await CardCmd.TransformTo<MinionStrike>(selected);
                }
                return selected != null;
            }
            case "CHARGE":
            {
                CardPile? draw = MirrorPile(PileType.Draw);
                int count = card.DynamicVars.ContainsKey("Cards") ? card.DynamicVars.Cards.IntValue : 2;
                foreach (CardModel selected in draw?.Cards.Where(c => c.Type == CardType.Attack).Take(count).ToList()
                    ?? Enumerable.Empty<CardModel>())
                {
                    await CardCmd.TransformTo<MinionDiveBomb>(selected);
                }
                return true;
            }
            case "SEANCE":
            {
                CardPile? draw = MirrorPile(PileType.Draw);
                int count = card.DynamicVars.ContainsKey("Cards") ? card.DynamicVars.Cards.IntValue : 1;
                foreach (CardModel selected in draw?.Cards.Take(count).ToList() ?? Enumerable.Empty<CardModel>())
                {
                    await CardCmd.TransformTo<Soul>(selected);
                }
                return true;
            }
            case "GUARDS":
            {
                foreach (CardModel selected in MirrorPile(PileType.Hand)?.Cards.ToList() ?? Enumerable.Empty<CardModel>())
                {
                    await CardCmd.TransformTo<MinionSacrifice>(selected);
                }
                return true;
            }
            case "BATTLETANCE":
            {
                int n = card.DynamicVars.ContainsKey("Cards") ? Math.Clamp(card.DynamicVars.Cards.IntValue, 1, 10) : 3;
                DrawFromMirrorDrawIntoHand(n);
                return true;
            }
            case "BULLETTIME":
            {
                CardPile? hand = MirrorPile(PileType.Hand);
                if (hand == null)
                {
                    return false;
                }
                foreach (CardModel c in hand.Cards.ToList())
                {
                    if (!c.EnergyCost.CostsX)
                    {
                        c.SetToFreeThisTurn();
                    }
                }
                return true;
            }
            case "ONEFORALL":
            {
                decimal amount = card.DynamicVars.ContainsKey("OneForAllPower") ? card.DynamicVars["OneForAllPower"].BaseValue : 1m;
                PowerModel canonical = ModelDb.DebugPower(ResolvePowerModel("OneForAllPower")!);
                await PowerCmd.Apply(ctx, canonical.ToMutable(), target, amount, Creature, card);
                return true;
            }
            case "PLOT":
            {
                int n = card.DynamicVars.ContainsKey("Cards") ? Math.Clamp(card.DynamicVars.Cards.IntValue, 1, 5) : 1;
                _pendingDrawBonus += n;
                return true;
            }
            case "PROLONG":
            {
                _pendingBlockBonus += (int)Math.Clamp(Creature.Block, 0m, 999m);
                return true;
            }
            case "ENTRENCH":
            {
                await CreatureCmd.GainBlock(Creature, Creature.Block, ValueProp.Unpowered | ValueProp.Move, cardPlay);
                return true;
            }
            case "DOUBLEENERGY":
            {
                _bonusEnergyThisTurn += Math.Max(0, _plannedLeftoverEnergy);
                return true;
            }
            case "BELIEVEINYOU":
            {
                Player? targetPlayer = target.Player;
                if (targetPlayer != null)
                {
                    int n = card.DynamicVars.ContainsKey("Energy") ? (int)card.DynamicVars.Energy.BaseValue : 2;
                    await PlayerCmd.GainEnergy(n, targetPlayer);
                    return true;
                }
                return false;
            }
            case "CALCULATEDGAMBLE":
            {
                CardPile? hand = MirrorPile(PileType.Hand);
                if (hand == null)
                {
                    return false;
                }
                int count = hand.Cards.Count;
                foreach (CardModel c in hand.Cards.ToList())
                {
                    hand.RemoveInternal(c);
                    CardPile? discard = MirrorPile(PileType.Discard);
                    if (discard != null && !discard.Cards.Contains(c))
                    {
                        discard.AddInternal(c);
                    }
                }
                DrawFromMirrorDrawIntoHand(count);
                return true;
            }
            case "CASCADE":
            case "HAVOC":
            {
                CardPile? draw = MirrorPile(PileType.Draw);
                if (draw == null || draw.Cards.Count == 0)
                {
                    return false;
                }
                CardModel top = draw.Cards[^1];
                draw.RemoveInternal(top);
                if (!MirrorCardEffects.Table.ContainsKey(NormalizedId(top)) && top.Type != CardType.Attack
                    && !top.DynamicVars.ContainsKey("Damage") && !top.DynamicVars.ContainsKey("Block")
                    && !top.DynamicVars.ContainsKey("CalculatedBlock"))
                {
                    // The real pipeline risks hand-UI deadlocks; only auto-play
                    // cards the safe interpreter can express.
                    CardPile? discard = MirrorPile(PileType.Discard);
                    if (discard != null && !discard.Cards.Contains(top))
                    {
                        discard.AddInternal(top);
                    }
                    return NormalizedId(card) == "CASCADE";
                }
                await PlayTurnCard(top, target);
                if (NormalizedId(card) == "HAVOC")
                {
                    CardPile? exhaust = MirrorPile(PileType.Exhaust);
                    foreach (CardPile? p in new[] { MirrorPile(PileType.Discard), MirrorPile(PileType.Hand), MirrorPile(PileType.Play) })
                    {
                        if (exhaust != null && p != null && p.Cards.Contains(top))
                        {
                            p.RemoveInternal(top);
                            exhaust.AddInternal(top);
                        }
                    }
                }
                return true;
            }
            case "SACRIFICE":
            {
                if (_mirrorOsty != null && _mirrorOsty.IsAlive)
                {
                    await CreatureCmd.Kill(_mirrorOsty);
                    return true;
                }
                return false;
            }
            case "DEMONICSHIELD":
            {
                decimal block = BlockOf(card, target);
                if (block > 0m)
                {
                    await CreatureCmd.GainBlock(target, block, ValueProp.Unpowered | ValueProp.Move, cardPlay);
                    return true;
                }
                return false;
            }
            case "ENLIGHTENMENT":
            {
                CardPile? hand = MirrorPile(PileType.Hand);
                if (hand == null)
                {
                    return false;
                }
                foreach (CardModel c in hand.Cards.ToList())
                {
                    if (!c.EnergyCost.CostsX)
                    {
                        c.EnergyCost.SetThisTurnOrUntilPlayed(1, reduceOnly: true);
                    }
                }
                return true;
            }
            case "APOTHEOSIS":
            {
                Player? owner = CurrentPlayer();
                if (owner == null)
                {
                    return false;
                }
                foreach (CardModel c in PileType.Deck.GetPile(owner).Cards.ToList())
                {
                    if (c.IsUpgradable)
                    {
                        CardCmd.Upgrade(c);
                    }
                }
                return true;
            }
            case "ENERGYSURGE":
            {
                Player? targetPlayer = target.Player;
                if (targetPlayer != null)
                {
                    int n = card.DynamicVars.ContainsKey("Energy") ? (int)card.DynamicVars.Energy.BaseValue : 2;
                    await PlayerCmd.GainEnergy(n, targetPlayer);
                    return true;
                }
                return false;
            }
            default:
                return starEffectApplied;
        }
    }

    /// <summary>
    /// Applies deterministic star-gain portions of Regent cards. These cards
    /// were previously unsupported because the mirror player had no combat
    /// star pool at all.
    /// </summary>
    private async Task<bool> ApplyMirrorStarCardEffect(CardModel card, Creature target)
    {
        if (_mirrorPlayer == null)
        {
            return false;
        }
        string id = NormalizedId(card);
        bool shouldGain = id is "BIGBANG" or "GATHERLIGHT" or "GLOW" or
            "HIDDENCACHE" or "ROYALGAMBLE" or "SHININGSTRIKE" or
            "SOLARSTRIKE" or "VENERATE";
        // Knockout Blow grants stars only when its attack kills. The attack
        // portion has already resolved before this helper is called.
        if (id == "KNOCKOUTBLOW")
        {
            shouldGain = target.IsDead;
        }
        if (!shouldGain || !card.DynamicVars.ContainsKey("Stars"))
        {
            return false;
        }
        int amount = Math.Max(0, card.DynamicVars.Stars.IntValue);
        if (amount <= 0)
        {
            return false;
        }
        await PlayerCmd.GainStars(amount, _mirrorPlayer);
        Log.Info($"[MirrorDuelist] {id} gained {amount} stars (now {CurrentMirrorStars()}).");
        return true;
    }

    private async Task<bool> ApplyMirrorSelfPower(CardModel card, PlayerChoiceContext ctx, string powerName)
    {
        Type? powerType = ResolvePowerModel(powerName);
        if (powerType == null)
        {
            return false;
        }
        try
        {
            PowerModel canonical = ModelDb.DebugPower(powerType);
            string varKey = card.DynamicVars.ContainsKey(powerName) ? powerName :
                (card.DynamicVars.ContainsKey("Loop") ? "Loop" : powerName);
            decimal amount = card.DynamicVars.ContainsKey(varKey)
                ? card.DynamicVars[varKey].BaseValue : 1m;
            await PowerCmd.Apply(ctx, canonical.ToMutable(), Creature, amount, Creature, card);
            return true;
        }
        catch (Exception e)
        {
            Log.Error($"[MirrorDuelist] {powerName} failed for {card.Id.Entry}: {e}");
            return false;
        }
    }

    /// <summary>
    /// Safe translations for cards that channel, evoke, or resize the orb
    /// queue. The mirror owns a real PlayerCombatState, so OrbCmd can still run
    /// the vanilla orb models (passives, evocation damage, Focus modifiers and
    /// orb visuals) while the card itself is resolved without OnPlay.
    /// </summary>
    private async Task<bool> TryOrbCardEffect(CardModel card, Creature target,
        PlayerChoiceContext ctx, CardPlay cardPlay)
    {
        if (_mirrorPlayer == null)
        {
            return false;
        }

        string id = NormalizedId(card);
        switch (id)
        {
            case "BALLLIGHTNING":
                await ChannelMirrorOrb<LightningOrb>(ctx);
                return true;
            case "COLDSNAP":
                await ChannelMirrorOrb<FrostOrb>(ctx);
                return true;
            case "CHAOS":
                for (int i = 0; i < Math.Max(0, IntVar(card, "Repeat", 1)); i++)
                {
                    OrbModel orb = OrbModel.GetRandomOrb(
                        _mirrorPlayer.RunState.Rng.CombatOrbGeneration).ToMutable();
                    await OrbCmd.Channel(ctx, orb, _mirrorPlayer);
                    _mirrorOrbsChanneledThisCombat++;
                }
                return true;
            case "CHILL":
            {
                int targets = Creature.CombatState?.GetOpponentsOf(Creature)
                    .Count(c => c.IsHittable) ?? 1;
                for (int i = 0; i < Math.Max(1, targets); i++)
                {
                    await ChannelMirrorOrb<FrostOrb>(ctx);
                }
                return true;
            }
            case "DARKNESS":
                await ChannelMirrorOrb<DarkOrb>(ctx);
                foreach (DarkOrb orb in _mirrorPlayer!.PlayerCombatState!.OrbQueue.Orbs
                    .OfType<DarkOrb>().ToList())
                {
                    int triggers = card.IsUpgraded ? 2 : 1;
                    for (int i = 0; i < triggers; i++)
                    {
                        await OrbCmd.Passive(ctx, orb, null);
                    }
                }
                return true;
            case "FUSION":
                await ChannelMirrorOrb<PlasmaOrb>(ctx);
                return true;
            case "GLACIER":
                await ChannelMirrorOrb<FrostOrb>(ctx);
                await ChannelMirrorOrb<FrostOrb>(ctx);
                return true;
            case "GLASSWORK":
                await ChannelMirrorOrb<GlassOrb>(ctx);
                return true;
            case "RAINBOW":
                await ChannelMirrorOrb<LightningOrb>(ctx);
                await ChannelMirrorOrb<FrostOrb>(ctx);
                await ChannelMirrorOrb<DarkOrb>(ctx);
                return true;
            case "ZAP":
                await ChannelMirrorOrb<LightningOrb>(ctx);
                return true;
            case "COOLHEADED":
                await ChannelMirrorOrb<FrostOrb>(ctx);
                return true;
            case "CONSUMINGSHADOW":
                for (int i = 0; i < Math.Max(0, IntVar(card, "Repeat", 2)); i++)
                {
                    await ChannelMirrorOrb<DarkOrb>(ctx);
                }
                // The card also applies ConsumingShadowPower; let the
                // continuous-power branch below finish that part.
                return false;
            case "HIBERNATE":
            case "ICELANCE":
                for (int i = 0; i < Math.Max(0, IntVar(card, "Repeat", id == "ICELANCE" ? 3 : 2)); i++)
                {
                    await ChannelMirrorOrb<FrostOrb>(ctx);
                }
                return true;
            case "IGNITION":
                // The vanilla card targets an ally's Player. The mirror has no
                // ally target selection, so its generated Plasma belongs to
                // the mirror itself rather than the real player's side.
                await ChannelMirrorOrb<PlasmaOrb>(ctx);
                return true;
            case "METEORSTRIKE":
                for (int i = 0; i < 3; i++)
                {
                    await ChannelMirrorOrb<PlasmaOrb>(ctx);
                }
                return true;
            case "NULL":
            case "SHADOWSHIELD":
                await ChannelMirrorOrb<DarkOrb>(ctx);
                return true;
            case "REFRACT":
                for (int i = 0; i < Math.Max(0, IntVar(card, "Repeat", 2)); i++)
                {
                    await ChannelMirrorOrb<GlassOrb>(ctx);
                }
                return true;
            case "SPINNER":
                if (card.IsUpgraded)
                {
                    await ChannelMirrorOrb<GlassOrb>(ctx);
                }
                // The power itself is applied by TrySpecialEffect below.
                return false;
            case "TEMPEST":
            {
                int amount = ResolveMirrorX(card);
                if (card.IsUpgraded)
                {
                    amount++;
                }
                for (int i = 0; i < amount; i++)
                {
                    await ChannelMirrorOrb<LightningOrb>(ctx);
                }
                return true;
            }
            case "VOLTAIC":
                for (int i = 0; i < _mirrorOrbsChanneledThisCombat; i++)
                {
                    await ChannelMirrorOrb<LightningOrb>(ctx);
                }
                return true;
            case "CAPACITOR":
            case "MODDED":
                await OrbCmd.AddSlots(_mirrorPlayer,
                    Math.Max(0, IntVar(card, "Repeat", id == "CAPACITOR" ? 2 : 1)));
                return true;
            case "BULKUP":
                OrbCmd.RemoveSlots(_mirrorPlayer,
                    Math.Max(0, IntVar(card, "OrbSlots", 1)));
                return true;
            case "DUALCAST":
                await EvokeMirrorFront(ctx, 2);
                return true;
            case "MULTICAST":
                await EvokeMirrorFront(ctx, ResolveMirrorX(card));
                return true;
            case "QUADCAST":
                await EvokeMirrorFront(ctx, Math.Max(0, IntVar(card, "Repeat", 4)));
                return true;
            case "SHATTER":
            {
                int count = _mirrorPlayer!.PlayerCombatState!.OrbQueue.Orbs.Count;
                for (int i = 0; i < count; i++)
                {
                    await OrbCmd.EvokeNext(ctx, _mirrorPlayer, dequeue: false);
                    await OrbCmd.EvokeNext(ctx, _mirrorPlayer);
                }
                return count > 0;
            }
            case "TESLACOIL":
            {
                foreach (LightningOrb orb in _mirrorPlayer!.PlayerCombatState!.OrbQueue.Orbs
                    .OfType<LightningOrb>().ToList())
                {
                    await OrbCmd.Passive(ctx, orb, target);
                    if (card.IsUpgraded)
                    {
                        await OrbCmd.Passive(ctx, orb, target);
                    }
                }
                return true;
            }
            default:
                return false;
        }
    }

    private async Task<bool> ApplyMirrorStorm(CardModel card, PlayerChoiceContext ctx)
    {
        // Storm's vanilla hook observes every Power card played after Storm.
        // The safe interpreter does not dispatch PowerModel card hooks, so
        // reproduce that one deterministic trigger explicitly.
        if (card.Type != CardType.Power || NormalizedId(card) == "STORM")
        {
            return false;
        }
        int amount = Creature.GetPowerAmount<StormPower>();
        if (amount <= 0)
        {
            return false;
        }
        for (int i = 0; i < amount; i++)
        {
            await ChannelMirrorOrb<LightningOrb>(ctx);
        }
        return true;
    }

    private async Task ApplyMirrorTurnStartPowers(PlayerChoiceContext ctx)
    {
        if (_mirrorPlayer?.PlayerCombatState == null)
        {
            return;
        }
        // StarNextTurnPower normally runs from AfterEnergyReset on a real
        // player. The mirror has no player-turn phase, so advance it here.
        StarNextTurnPower? stars = Creature.GetPower<StarNextTurnPower>();
        if (stars != null)
        {
            await PlayerCmd.GainStars(stars.Amount, _mirrorPlayer);
            await PowerCmd.Remove(stars);
        }
        LoopPower? loop = Creature.GetPower<LoopPower>();
        if (loop != null && _mirrorPlayer.PlayerCombatState.OrbQueue.Orbs.Count > 0)
        {
            for (int i = 0; i < loop.Amount; i++)
            {
                await OrbCmd.Passive(ctx, _mirrorPlayer.PlayerCombatState.OrbQueue.Orbs[0], null);
            }
        }
        SpinnerPower? spinner = Creature.GetPower<SpinnerPower>();
        if (spinner != null)
        {
            for (int i = 0; i < spinner.Amount; i++)
            {
                await ChannelMirrorOrb<GlassOrb>(ctx);
            }
        }
    }

    private async Task ChannelMirrorOrb<T>(PlayerChoiceContext ctx) where T : OrbModel
    {
        await OrbCmd.Channel<T>(ctx, _mirrorPlayer!);
        _mirrorOrbsChanneledThisCombat++;
    }

    private async Task EvokeMirrorFront(PlayerChoiceContext ctx, int count)
    {
        count = Math.Max(0, count);
        for (int i = 0; i < count && _mirrorPlayer!.PlayerCombatState!.OrbQueue.Orbs.Count > 0; i++)
        {
            await OrbCmd.EvokeNext(ctx, _mirrorPlayer!, dequeue: i == count - 1);
        }
    }

    private static int IntVar(CardModel card, string key, int fallback)
    {
        return card.DynamicVars.ContainsKey(key)
            ? card.DynamicVars[key].IntValue
            : fallback;
    }

    private static int ResolveMirrorX(CardModel card)
    {
        if (!card.EnergyCost.CostsX)
        {
            return 0;
        }
        int captured = card.EnergyCost.CapturedXValue;
        // The mirror planner does not run CardModel.SpendResources (that would
        // mutate the real player's energy). Five is its normal per-turn budget;
        // use it when no captured X value was supplied by a generated card.
        return captured > 0 ? captured : TurnEnergy;
    }

    private async Task AddMirrorShivs(CardModel source, Creature target, int count,
        bool enchanted, bool playImmediately, bool upgraded = false)
    {
        if (_mirrorPlayer == null || source.CombatState == null)
        {
            return;
        }
        for (int i = 0; i < Math.Clamp(count, 0, 12); i++)
        {
            CardModel shiv = source.CombatState.CreateCard<Shiv>(_mirrorPlayer);
            if (enchanted)
            {
                CardCmd.Enchant<Inky>(shiv, 1m);
            }
            if (upgraded)
            {
                CardCmd.Upgrade(shiv);
            }
            if (playImmediately)
            {
                await PlayGeneratedCard(shiv, target, makeFree: true);
            }
            else
            {
                await AddMirrorGeneratedToHand(shiv, freeThisTurn: true);
            }
        }
    }

    private Task AddMirrorGeneratedToHand(CardModel card, bool freeThisTurn)
    {
        if (_mirrorPlayer == null)
        {
            return Task.CompletedTask;
        }
        if (freeThisTurn)
        {
            card.SetToFreeThisTurn();
        }
        CardPile? hand = MirrorPile(PileType.Hand);
        if (hand != null && !hand.Cards.Contains(card))
        {
            hand.AddInternal(card);
        }
        return Task.CompletedTask;
    }

    private async Task DiscardMirrorCards(int count)
    {
        CardPile? hand = MirrorPile(PileType.Hand);
        CardPile? discard = MirrorPile(PileType.Discard);
        if (hand == null || discard == null)
        {
            return;
        }
        foreach (CardModel card in hand.Cards
            .OrderBy(c => PlayCost(c))
            .ThenBy(c => c.Id.Entry)
            .Take(Math.Max(0, count)).ToList())
        {
            hand.RemoveInternal(card);
            if (!discard.Cards.Contains(card))
            {
                discard.AddInternal(card);
            }
        }
        await Task.CompletedTask;
    }

    private async Task ExhaustMirrorCards(int count)
    {
        CardPile? hand = MirrorPile(PileType.Hand);
        CardPile? exhaust = MirrorPile(PileType.Exhaust);
        if (hand == null || exhaust == null)
        {
            return;
        }
        foreach (CardModel card in hand.Cards
            .OrderBy(c => PlayCost(c))
            .ThenBy(c => c.Id.Entry)
            .Take(Math.Max(0, count)).ToList())
        {
            hand.RemoveInternal(card);
            if (!exhaust.Cards.Contains(card))
            {
                exhaust.AddInternal(card);
            }
        }
        await Task.CompletedTask;
    }

    private async Task ExhaustMirrorDrawCards(int count)
    {
        CardPile? draw = MirrorPile(PileType.Draw);
        CardPile? exhaust = MirrorPile(PileType.Exhaust);
        if (draw == null || exhaust == null)
        {
            return;
        }
        foreach (CardModel card in draw.Cards.Take(Math.Max(0, count)).ToList())
        {
            draw.RemoveInternal(card);
            if (!exhaust.Cards.Contains(card))
            {
                exhaust.AddInternal(card);
            }
        }
        await Task.CompletedTask;
    }

    private void MoveDiscardToMirrorHand(int count)
    {
        CardPile? discard = MirrorPile(PileType.Discard);
        CardPile? hand = MirrorPile(PileType.Hand);
        if (discard == null || hand == null)
        {
            return;
        }
        foreach (CardModel card in discard.Cards.Take(Math.Max(0, count)).ToList())
        {
            discard.RemoveInternal(card);
            if (!hand.Cards.Contains(card))
            {
                hand.AddInternal(card);
            }
        }
    }

    private void MoveTopMatchingDrawToHand(CardType? type)
    {
        CardPile? draw = MirrorPile(PileType.Draw);
        CardPile? hand = MirrorPile(PileType.Hand);
        if (draw == null || hand == null)
        {
            return;
        }
        CardModel? card = draw.Cards.AsEnumerable().Reverse()
            .FirstOrDefault(c => type == null || c.Type == type);
        if (card == null)
        {
            return;
        }
        draw.RemoveInternal(card);
        if (!hand.Cards.Contains(card))
        {
            hand.AddInternal(card);
        }
    }

    private void MoveHighestMirrorHandToDraw()
    {
        CardPile? hand = MirrorPile(PileType.Hand);
        CardPile? draw = MirrorPile(PileType.Draw);
        if (hand == null || draw == null)
        {
            return;
        }
        CardModel? card = hand.Cards.OrderByDescending(GeneratedCardScore).FirstOrDefault();
        if (card == null)
        {
            return;
        }
        hand.RemoveInternal(card);
        if (!draw.Cards.Contains(card))
        {
            draw.AddInternal(card);
        }
    }

    /// <summary>Moves up to n cards from the mirror draw pile into its hand
    /// (no draw command: the mirror player has no hand UI). The turn's greedy
    /// continuation plays them while energy remains.</summary>
    private void DrawFromMirrorDrawIntoHand(int n)
    {
        CardPile? draw = MirrorPile(PileType.Draw);
        CardPile? hand = MirrorPile(PileType.Hand);
        if (draw == null || hand == null)
        {
            return;
        }
        while (n > 0)
        {
            if (draw.Cards.Count == 0 && MirrorPile(PileType.Discard) is { } discard && discard.Cards.Count > 0)
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
                break;
            }
            CardModel top = draw.Cards[^1];
            draw.RemoveInternal(top);
            if (!hand.Cards.Contains(top))
            {
                hand.AddInternal(top);
            }
            _mirrorCardsDrawnThisCombat++;
            n--;
        }
    }

    private IEnumerable<Creature> MirrorEnemyTargets(Creature fallback)
    {
        IReadOnlyList<Creature>? hittable = Creature.CombatState?.HittableEnemies;
        if (hittable != null)
        {
            List<Creature> living = hittable.Where(c => c.IsAlive && c.IsHittable).ToList();
            if (living.Count > 0)
            {
                return living;
            }
        }
        return fallback.IsAlive ? new[] { fallback } : Array.Empty<Creature>();
    }

    private void MoveZeroCostDiscardToMirrorHand()
    {
        CardPile? discard = MirrorPile(PileType.Discard);
        CardPile? hand = MirrorPile(PileType.Hand);
        if (discard == null || hand == null)
        {
            return;
        }
        foreach (CardModel card in discard.Cards
            .Where(c => !c.EnergyCost.CostsX && c.EnergyCost.GetWithModifiers(CostModifiers.All) == 0 &&
                c.Type is CardType.Attack or CardType.Skill or CardType.Power)
            .ToList())
        {
            discard.RemoveInternal(card);
            if (hand.Cards.Count < CardPile.MaxCardsInHand && !hand.Cards.Contains(card))
            {
                hand.AddInternal(card);
            }
            else if (!discard.Cards.Contains(card))
            {
                discard.AddInternal(card);
            }
        }
    }

    private void BuffMirrorCopies(string cardId, decimal amount)
    {
        if (amount <= 0m)
        {
            return;
        }
        foreach (CardModel copy in (_mirrorPlayer?.PlayerCombatState?.AllCards ?? Enumerable.Empty<CardModel>())
            .Where(c => NormalizedId(c) == cardId && c.DynamicVars.ContainsKey("Damage")))
        {
            copy.DynamicVars.Damage.BaseValue += amount;
        }
    }

    private void MoveMirrorHandToDraw()
    {
        CardPile? hand = MirrorPile(PileType.Hand);
        CardPile? draw = MirrorPile(PileType.Draw);
        if (hand == null || draw == null)
        {
            return;
        }
        foreach (CardModel card in hand.Cards.ToList())
        {
            hand.RemoveInternal(card);
            if (!draw.Cards.Contains(card))
            {
                draw.AddInternal(card);
            }
        }
    }

    private void ShuffleMirrorDraw()
    {
        CardPile? draw = MirrorPile(PileType.Draw);
        if (draw == null || draw.Cards.Count < 2)
        {
            return;
        }
        List<CardModel> cards = draw.Cards.ToList();
        foreach (CardModel card in cards)
        {
            draw.RemoveInternal(card);
        }
        ShuffleList(cards);
        foreach (CardModel card in cards)
        {
            draw.AddInternal(card);
        }
    }

    private void DiscardMirrorCards(IEnumerable<CardModel> cards)
    {
        CardPile? hand = MirrorPile(PileType.Hand);
        CardPile? discard = MirrorPile(PileType.Discard);
        if (hand == null || discard == null)
        {
            return;
        }
        foreach (CardModel card in cards.ToList())
        {
            if (!hand.Cards.Contains(card))
            {
                continue;
            }
            hand.RemoveInternal(card);
            if (!discard.Cards.Contains(card))
            {
                discard.AddInternal(card);
            }
        }
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
            IEnumerable<Creature> destinations = !toSelf && card.TargetType == TargetType.AllEnemies
                ? MirrorEnemyTargets(target)
                : new[] { toSelf ? Creature : target };
            foreach (Creature dest in destinations)
            {
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

    /// <summary>
    /// Executes a copied attack against the same target set as the vanilla
    /// card. The old interpreter always called Targeting(target), which made
    /// every AllEnemies card silently hit only the first player in a party.
    /// CombatState.HittableEnemies is redirected by ModInit while the mirror
    /// is taking its turn, so TargetingAllOpponents is safe here.
    /// </summary>
    private async Task ExecuteMirrorAttack(CardModel card, CardPlay cardPlay,
        Creature target, PlayerChoiceContext ctx, decimal damage, int hits, bool fromOsty)
    {
        AttackCommand attack = DamageCmd.Attack(damage).WithHitCount(hits);
        if (fromOsty && _mirrorOsty != null)
        {
            attack = attack.FromOsty(_mirrorOsty, card, cardPlay)
                .WithHitFx("vfx/vfx_attack_blunt");
            if (card.TargetType == TargetType.AllEnemies && Creature.CombatState != null)
            {
                attack.TargetingAllOpponents(Creature.CombatState);
            }
            else
            {
                attack.Targeting(target);
            }
        }
        else if (card.TargetType == TargetType.AllEnemies && Creature.CombatState != null)
        {
            // FromMonster already calls TargetingAllOpponents internally.  The
            // previous code called it a second time here, which throws
            // "Already set to target opponents of attacker" and turns the
            // entire mirror turn into a dud.  Keep the vanilla builder chain
            // intact and only add the explicit target for single-target cards.
            attack = attack.FromMonster(this)
                .WithAttackerAnim("Attack", 0.3f)
                .WithAttackerFx(null, AttackSfxPath)
                .WithHitFx("vfx/vfx_attack_blunt");
        }
        else
        {
            // FromMonster is intentionally an all-opponents helper.  For a
            // single-target mirror attack use FromCard instead; the synthetic
            // mirror Player is bound to this same creature, so the command
            // still records the duelist as the attacker while allowing an
            // explicit target to be selected.
            attack = attack.FromCard(card, cardPlay)
                .WithAttackerAnim("Attack", 0.3f)
                .WithAttackerFx(null, AttackSfxPath)
                .WithHitFx("vfx/vfx_attack_blunt")
                .Targeting(target);
        }
        await attack.Execute(ctx);
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
        bool isMurder = card is Murder ||
            NormalizedId(card).Equals("MURDER", StringComparison.Ordinal);
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
        else if (isMurder && card.DynamicVars.ContainsKey("CalculationBase") &&
            card.DynamicVars.ContainsKey("ExtraDamage"))
        {
            // Murder's CalculatedDamage delegate counts CardDrawnEntry for
            // the card owner. The mirror player has no normal draw phase, so
            // maintain the equivalent count in our private pile interpreter.
            damage = card.DynamicVars["CalculationBase"].BaseValue +
                card.DynamicVars["ExtraDamage"].BaseValue * _mirrorCardsDrawnThisCombat;
            Log.Info($"[MirrorDuelist] Murder uses {_mirrorCardsDrawnThisCombat} mirror draws => {damage} damage.");
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
        damage = Clamp(damage, isShiv || isMurder ? 0m : DamageClampMin,
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
        decimal minimum = isShiv || isMurder ? 0m : DamageClampMin;
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
        if (MirrorHitCounts.TryGet(NormalizedId(card), out int fixedHits))
        {
            return Math.Clamp(fixedHits, 1, 8);
        }
        string id = NormalizedId(card);
        if (id == "FIENDFIRE")
        {
            return Math.Clamp(MirrorPile(PileType.Hand)?.Cards.Count ?? 1, 1, 12);
        }
        if (id == "WHIRLWIND")
        {
            return Math.Clamp(ResolveMirrorX(card), 1, 12);
        }
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
