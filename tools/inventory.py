"""Inventory every vanilla card against the interpreter's coverage.

Classifies each card: covered (attack w/ damage expr, block var, power table,
rider keys, generated-card special, heal, hp loss, summon, economy) or
uncovered, and emits the uncovered list with the OnPlay feature fingerprint
for agent review.
"""
from __future__ import annotations

import json
import re
from pathlib import Path

SRC = Path("D:/L/Study/modbulid/src/Core/Models")
CARDS = SRC / "Cards"
POWERS = SRC / "Powers"
MIRROR = Path("D:/L/Study/mod/MirrorDuelist")

RIDER_KEYS = {"VulnerablePower", "WeakPower", "PoisonPower", "DoomPower",
              "StrengthPower", "DexterityPower", "AccuracyPower"}

APPLY_RE = re.compile(r"PowerCmd\.Apply<\s*(\w+)\s*>")
ONPLAY_RE = re.compile(r"protected override (?:async )?Task OnPlay\(PlayerChoiceContext choiceContext, CardPlay cardPlay\)\s*\{")


def body_after(text: str, start: int) -> tuple[str, int]:
    depth = 0
    i = start
    while i < len(text):
        if text[i] == "{":
            depth += 1
        elif text[i] == "}":
            depth -= 1
            if depth == 0:
                return text[start + 1:i], i
        i += 1
    return text[start:], len(text)


def normalize(name: str) -> str:
    return re.sub(r"[^A-Za-z0-9]", "", name).upper()


def main() -> None:
    table_src = (MIRROR / "MirrorCardPowers.cs").read_text(encoding="utf-8")
    table_ids = set(re.findall(r'\["([A-Z0-9]+)"\]', table_src))

    uncovered = []
    covered_count = 0
    stealable_count = 0
    for path in sorted(CARDS.glob("*.cs")):
        text = path.read_text(encoding="utf-8")
        if "Mocks" in str(path):
            continue
        m = re.search(r": base\(\s*[-\d.]+\s*,\s*CardType\.(\w+)", text)
        ctype = m.group(1) if m else "?"
        if ctype not in ("Attack", "Skill", "Power", "Curse"):
            continue
        stealable_count += 1
        cid = normalize(path.stem)

        op = ONPLAY_RE.search(text)
        body = ""
        if op:
            body, _ = body_after(text, op.end() - 1)

        has_damage = "DamageCmd.Attack" in body
        has_block = "GainBlock" in body
        has_heal = "CreatureCmd.Heal" in body
        has_hpl = '"HpLoss"' in text and ctype != "Attack" or '"HpLoss"' in body
        has_summon = '"Summon"' in body or "CreatureCmd.Add" in body
        applies = APPLY_RE.findall(body)
        applies_full = APPLY_RE.findall(text)
        ui_select = "CardSelectCmd" in body
        creates_card = re.search(r"CreateCard<|CreateInHand|CardFactory\.", body) is not None
        gold = "GoldCmd" in body or '"Gold"' in text
        potion = "PotionCmd" in body
        map_or_run = re.search(r"RunState\.|MapCmd|CurrentRoom|AddExtraReward", body) is not None

        # Table covers applies (safe/economy). Rider keys cover some others.
        table_has = cid in table_ids
        rider_only = (not table_has) and any(a in RIDER_KEYS for a in applies_full)

        dmg_expr = "Damage" in text and "new DamageVar" in text
        dmg_calc = "new CalculatedVar" in text or "CalculatedDamage" in text
        block_var = 'new BlockVar' in text or '"Block"' in text

        reasons = []
        if table_has or rider_only:
            covered_count += 1
            continue
        if has_damage and (dmg_expr or dmg_calc):
            covered_count += 1
            continue
        if has_block and block_var:
            covered_count += 1
            continue
        if has_heal or has_hpl or has_summon:
            covered_count += 1
            continue
        if ctype == "Attack" and has_damage:
            # attack but unusual damage expression
            reasons.append("ATTACK_CUSTOM_DMG")
        if ui_select:
            reasons.append("UI_SELECT")
        if creates_card:
            reasons.append("CREATES_CARDS")
        if gold:
            reasons.append("GOLD")
        if potion:
            reasons.append("POTION")
        if map_or_run:
            reasons.append("RUN_LEVEL")
        if applies and not reasons:
            reasons.append("APPLY_NOT_IN_TABLE")
        if not reasons:
            reasons.append("UNIQUE_EFFECT")

        uncovered.append({
            "id": cid,
            "file": path.stem,
            "type": ctype,
            "reasons": reasons,
            "onplay_len": len(body),
        })

    out = MIRROR / "tools" / "uncovered_cards.json"
    out.write_text(json.dumps(uncovered, indent=1), encoding="utf-8")
    print(f"stealable cards: {stealable_count}")
    print(f"covered: {covered_count}  uncovered: {len(uncovered)}")
    from collections import Counter
    for r, n in Counter(tuple(c["reasons"]) for c in uncovered).most_common(15):
        print(f"  {n:3d}  {'+'.join(r)}")


if __name__ == "__main__":
    main()
