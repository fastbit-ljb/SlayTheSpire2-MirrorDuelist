# Mirror Duelist (镜像决斗家)

Slay the Spire 2 custom-enemy mod (test version). An Act 3 normal enemy that
mirrors the player.

## Behavior

- Copies the played character's visuals (facing the player).
- **HP = 1.5x** the player's max HP (x1.07 on Tough Enemies ascension).
- Turn 1 steals **three real cards** from the player's draw/discard piles
  (Thieving Hopper parity: the deck version is pulled out of the run deck too).
  Weighted: attack > power > skill > curse, uncommon > common > rare; at most
  one copy of each card.
- Stolen cards hang above the duelist. Each turn it draws up to 5 and spends
  **5 energy** playing them at the player through a UI-free interpreter:
  - Attacks hit with printed damage/hit counts (incl. Shiv + Accuracy,
    Unleash scaling off the mirrored Osty).
  - Block skills grant it block; self HP-loss costs (Breakthrough) apply.
  - **Power cards apply their real PowerModel** via a generated translation
    table (`MirrorCardPowers.cs`, extracted from the vanilla card sources by
    `tools/gen_card_powers.py`): Demon Form, Echo Form, Noxious Fumes, The
    Bomb, Thorns... Powers whose hooks require a real player are audited out
    (71 blacklisted) and resolve as harmless duds.
  - Next-turn resource powers (energy/draw/block) feed the interpreter's own
    pending economy for the following turn.
  - The mirror starts combat with **10 stars**. Star-cost cards spend the
    mirror's real combat star resource; Regent star-gain cards replenish it.
  - Generated cards (Shivs, Discovery) are played by the duelist, never given
    to the player; recursion is capped (depth 4, 12 per turn).
  - Selection cards use deterministic AI choices instead of opening a player
    screen (`Abundance`, `Cleanse`, `DecisionsDecisions`, `Nightmare`, `Splash`).
  - AOE attacks now use the real player-side target list, low-damage cards no
    longer receive an artificial six-damage floor, and common pile cards such
    as `AllForOne`, `FiendFire`, `Reboot`, `Scrape`, `Pillage`, `Claw`, `Maul`,
    `BeatIntoShape` and `DrainPower` have dedicated translations.
  - Storm/Loop/Thunder/Hailstorm/Spinner/Consuming Shadow orb powers are
    attached to the mirror and advanced through enemy-turn-safe hooks.
  - Powers are single-use (exhaust), exhaust cards leave the pool, a pool down
    to one card plays it twice, an empty pool falls back to Strikes.
- `CombatState.HittableEnemies` is redirected to the player side during the
  enemy turn of a duelist fight, so stolen AOE powers hit the player's team.
- On death, every stolen card is offered back as a Special card reward
  (vanilla SwipePower flow); skipping leaves it out of the run.
- Appears once per run in one of Act 3's first three normal fights.

## Testing (dev console)

Press `` ` `` in-game:

- `fight MIRROR_DUELIST_NORMAL` — fight it immediately.
- `power STRENGTH_POWER 10 0` / `power STRENGTH_POWER -10 0` — pump/debuff
  yourself while observing its output.

## Project layout

- `MirrorDuelist.cs` — the monster: stealing, pile-based turns, the UI-free
  safe interpreter (no vanilla `OnPlay` invocation, so player-choice cards can
  never deadlock the enemy turn), intents, the mirrored Osty summon.
- `MirrorCardPowers.cs` — generated card→power translation table (do not edit
  by hand; run `tools/gen_card_powers.py`).
- `MirrorOstyGuardPower.cs` — hidden take-hits-for-the-duelist power (enemy-side
  Osty without PetOwner, so the combat layout stays intact).
- `MirrorPlayGate.cs` — the synthetic mirror player factory (owner of copied
  cards and their private combat piles).
- `ModInit.cs` — ModelDb.Inject registration fallback, Glory pool injection,
  Act 3 placement, HittableEnemies redirect.
- `MirrorIntentIcons.cs` + `assets/*.png` — embedded intent icons.
- `tools/gen_card_powers.py` — regenerates `MirrorCardPowers.cs` from the
  decompiled card sources (extraction + monster-owner safety audit).
- `tools/build_pck.py` / `tools/pck_tool.py` — builds `MirrorDuelist.pck`
  (localization only).

## Status

Test version (v0.6.8), installed locally. Star-resource support is enabled;
the remaining unsupported cards are catalogued in `UNIMPLEMENTED.md`.

Coverage: ~74% of all 575 stealable cards have real effects on the duelist
(power table 168 + effect table 37 + bespoke specials 16 + the var-driven
attack/block paths). The remaining 151 dud cards are catalogued with reasons
in UNIMPLEMENTED.md — mostly powers whose hooks are hardwired to the owning
player (audit-verified), orb/star cards, UI-selection cards and curses.
