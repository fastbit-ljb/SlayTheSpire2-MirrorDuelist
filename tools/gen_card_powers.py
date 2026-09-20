"""Generate MirrorCardPowers.cs: card-ID -> applied-power translation table.

Extracts every `PowerCmd.Apply<X>(ctx, dest, amount, ...)` call from the
vanilla card models, classifies the destination (self / target), and audits
each power's source for player-only API access that would throw when the
power's Owner is the duelist (a monster creature). Unsafe powers are dropped
(stay duds); a small set of "next turn" resource powers is redirected to the
interpreter's own pending-economy fields instead of the real power.
"""
from __future__ import annotations

import re
from pathlib import Path

SRC = Path("D:/L/Study/modbulid/src/Core/Models")
CARDS = SRC / "Cards"
POWERS = SRC / "Powers"

APPLY_RE = re.compile(r"PowerCmd\.Apply<\s*(\w+)\s*>\s*\(")
VAR_KEY_RE = re.compile(r'DynamicVars(?:\["([A-Za-z]+)"\]|\.([A-Za-z]+))')

# Powers the interpreter handles through its own pending economy instead of
# applying the real model (their hooks only make sense for a real player).
SPECIAL_POWERS = {
    "EnergyNextTurnPower": "Energy",
    "DrawCardsNextTurnPower": "Draw",
    "BlockNextTurnPower": "Block",
}

# Useless or UI-bound on the duelist: never translated.
EXCLUDED_POWERS = {
    "StarNextTurnPower",     # no star economy
    "NoDrawPower",           # no draw phase to suppress
    "RetainHandPower",       # no persistent hand UI
    "WellLaidPlansPower",    # retain semantics
    "ToolsOfTheTradePower",  # hand-related triggers
    "ThunderPower",          # Osty-bound
    "TagTeamPower",          # Osty-bound
    "SummonNextTurnPower",   # Summon var path already exists
}

# Cards handled by bespoke code in MirrorDuelist.TrySpecialEffect instead of
# table data (conditional amounts, free-hand sweeps, pending-economy forms).
SPECIAL_CASES = {
    "BattleTrance", "BulletTime", "OneForAll", "Plot", "Prolong",
    "Entrench", "DoubleEnergy", "BelieveInYou", "CalculatedGamble", "Apotheosis", "EnergySurge",
    "Cascade", "Havoc", "Sacrifice", "DemonicShield", "Enlightenment",
}

# Powers cleared by the audit to apply even though the pattern scan flagged
# them. Filled from agent review results.
UNBANNED_POWERS: set[str] = {
    "PlatingPower",  # audit: all hooks null-safe on monster owner (round-1
                     # block, per-turn block, correct decrement)
    "InfernoPower",  # audit: AfterDamageReceived null-safe, functional with
                     # the HittableEnemies redirect; turn-start hook is a
                     # gated no-op
    "LethalityPower",  # audit: ModifyDamageMultiplicative passes its gate for
                       # duelist attacks (card owner's Creature == duelist)
}

# Player-only API access that would NRE when Owner is the duelist creature.
UNSAFE_PATTERNS = [
    r"Owner\??\.Player\b(?!\s*(?:==|!=|is\b))",
    r"Owner\??\.Osty\b",
    r"Owner\??\.Hand\b",
    r"Owner\??\.Deck\b",
    r"Owner\??\.Relics?\b",
    r"Owner\??\.Potions?\b",
    r"Owner\??\.PlayerCombatState\b",
    r"Owner\??\.IsOstyMissing",
    r"Owner\??\.DrawPile\b",
    r"Owner\??\.DiscardPile\b",
]
UNSAFE_RE = re.compile("|".join(UNSAFE_PATTERNS))


def split_args(text: str, start: int) -> tuple[list[str], int]:
    """Split a balanced-paren argument list starting at '('. Returns
    (args, index_after_close)."""
    depth = 0
    args: list[str] = []
    cur: list[str] = []
    i = start
    while i < len(text):
        ch = text[i]
        if ch == "(":
            depth += 1
            if depth == 1:
                i += 1
                continue
        elif ch == ")":
            depth -= 1
            if depth == 0:
                args.append("".join(cur))
                return args, i + 1
        if depth == 1 and ch == ",":
            args.append("".join(cur))
            cur = []
        else:
            cur.append(ch)
        i += 1
    raise ValueError("unbalanced parens")


def normalize(name: str) -> str:
    return re.sub(r"[^A-Za-z0-9]", "", name).upper()


def main() -> None:
    power_cache: dict[str, bool] = {}
    entries: dict[str, list[str]] = {}
    stats = {"applies": 0, "self": 0, "target": 0, "skipped_dest": 0,
             "unsafe": 0, "excluded": 0, "special": 0, "cards": 0}

    for path in sorted(CARDS.glob("*.cs")):
        text = path.read_text(encoding="utf-8")
        card_id = normalize(path.stem)
        card_entries: list[str] = []
        for m in APPLY_RE.finditer(text):
            stats["applies"] += 1
            power = m.group(1)
            try:
                args, _ = split_args(text, m.end() - 1)
            except ValueError:
                continue
            if len(args) < 3:
                continue
            dest = args[1]
            low = dest.lower()
            if "osty" in low:
                continue
            if "owner" in low:
                side = "Self"
            elif "hittable" in low or "enemies" in low or "enemy" in low:
                side = "Target"
            elif "allies" in low or "teammates" in low:
                side = "Self"
            elif "target" in low:
                side = "Target"
            else:
                stats["skipped_dest"] += 1
                continue

            if path.stem in SPECIAL_CASES or power in EXCLUDED_POWERS:
                stats["excluded"] += 1
                continue
            if power in SPECIAL_POWERS:
                kind = SPECIAL_POWERS[power]
                vm = VAR_KEY_RE.search(args[2]) if len(args) > 2 else None
                key = (vm.group(1) or vm.group(2)) if vm else None
                card_entries.append(
                    f'        CardPowerTranslation.{kind}({("null" if key is None else chr(34) + key + chr(34))}),')
                stats["special"] += 1
                continue

            if power not in power_cache:
                pfile = POWERS / f"{power}.cs"
                if not pfile.exists():
                    power_cache[power] = False
                else:
                    ptext = pfile.read_text(encoding="utf-8")
                    power_cache[power] = UNSAFE_RE.search(ptext) is None or power in UNBANNED_POWERS
            if not power_cache[power]:
                stats["unsafe"] += 1
                continue

            vm = VAR_KEY_RE.search(args[2]) if len(args) > 2 else None
            key = (vm.group(1) or vm.group(2)) if vm else None
            key_lit = "null" if key is None else f'"{key}"'
            card_entries.append(
                f'        CardPowerTranslation.Model("{power}", {key_lit}, PowerSide.{side}),')
            stats["self" if side == "Self" else "target"] += 1
        if card_entries:
            stats["cards"] += 1
            entries[card_id] = card_entries

    # Emit C#.
    lines = [
        "// <auto-generated by tools/gen_card_powers.py - do not edit by hand>",
        "// Extracted from the vanilla card models: every PowerCmd.Apply call,",
        "// destination-classified and audited for monster-owner safety.",
        "#nullable enable",
        "using System;",
        "using System.Collections.Generic;",
        "",
        "namespace MirrorDuelistMod;",
        "",
        "internal enum PowerSide",
        "{",
        "    Self,",
        "    Target,",
        "}",
        "",
        "/// <summary>One translated power application for a stolen card.</summary>",
        "internal readonly struct CardPowerTranslation",
        "{",
        '    /// <summary>Kind "Model" applies the real PowerModel; "Energy"/"Draw"/',
        '    /// "Block" feed the interpreter\'s pending next-turn economy instead.</summary>',
        "    public readonly string Kind;",
        "    public readonly string? Power;",
        "    public readonly string? VarKey;",
        "    public readonly PowerSide Side;",
        "",
        "    private CardPowerTranslation(string kind, string? power, string? varKey, PowerSide side)",
        "    {",
        "        Kind = kind;",
        "        Power = power;",
        "        VarKey = varKey;",
        "        Side = side;",
        "    }",
        "",
        "    public static CardPowerTranslation Model(string power, string? varKey, PowerSide side) => new(\"Model\", power, varKey, side);",
        "    public static CardPowerTranslation Energy(string? varKey) => new(\"Energy\", null, varKey, PowerSide.Self);",
        "    public static CardPowerTranslation Draw(string? varKey) => new(\"Draw\", null, varKey, PowerSide.Self);",
        "    public static CardPowerTranslation Block(string? varKey) => new(\"Block\", null, varKey, PowerSide.Self);",
        "}",
        "",
        "internal static class MirrorCardPowers",
        "{",
        "    public static IReadOnlyDictionary<string, CardPowerTranslation[]> Table => _table;",
        "",
        "    private static readonly Dictionary<string, CardPowerTranslation[]> _table = new(StringComparer.Ordinal)",
        "    {",
    ]
    for card_id in sorted(entries):
        lines.append(f'        ["{card_id}"] = new CardPowerTranslation[]')
        lines.append("        {")
        lines.extend(entries[card_id])
        lines.append("        },")
    lines.extend([
        "    };",
        "}",
    ])

    out = Path("D:/L/Study/mod/MirrorDuelist/MirrorCardPowers.cs")
    out.write_text("\n".join(lines) + "\n", encoding="utf-8")

    print(f"cards with entries: {stats['cards']}")
    print(f"total applies: {stats['applies']}  self: {stats['self']}  target: {stats['target']}")
    print(f"special economy: {stats['special']}  excluded: {stats['excluded']}  unsafe: {stats['unsafe']}  skipped-dest: {stats['skipped_dest']}")
    unsafe = sorted(p for p, ok in power_cache.items() if not ok)
    print(f"unsafe powers ({len(unsafe)}): {', '.join(unsafe)}")


if __name__ == "__main__":
    main()
