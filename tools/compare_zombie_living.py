#!/usr/bin/env python3
"""Print living-vs-zombie animal statline diffs (ignoring morale, which is
uniformly different for zombies). One-off check for the parity principle."""
import json
import os

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
with open(os.path.join(ROOT, "data/units.json"), encoding="utf-8") as f:
    defs = {u["id"]: u for u in json.load(f)["units"]}

PAIRS = [
    ("Rat", "ZombieRat"),
    ("FemaleDeer", "ZombieFemaleDeer"),
    ("MaleDeer", "ZombieMaleDeer"),
    ("Wolf", "ZombieWolf"),
    ("Boar", "ZombieBoar"),
    ("Bear", "ZombieJuvenileBear"),
    ("GrizzlyBear", "ZombieGrizzlyBear"),
    ("GreatBoar", "ZombieGreatBoar"),
]
IGNORE = {"morale"}

for living_id, zombie_id in PAIRS:
    living, zombie = defs[living_id], defs[zombie_id]
    diffs = []
    keys = set(living["stats"]) | set(zombie["stats"])
    for k in sorted(keys - IGNORE):
        lv, zv = living["stats"].get(k), zombie["stats"].get(k)
        if lv != zv:
            diffs.append(f"{k}: living {lv} vs zombie {zv}")
    lw = [w["id"] if isinstance(w, dict) else w for w in living.get("weapons", [])]
    zw = [w["id"] if isinstance(w, dict) else w for w in zombie.get("weapons", [])]
    if lw != zw:
        diffs.append(f"weapons: living {lw} vs zombie {zw}")
    if living.get("size") != zombie.get("size"):
        diffs.append(f"size: living {living.get('size')} vs zombie {zombie.get('size')}")
    status = "PARITY" if not diffs else "DIFFERS"
    print(f"=== {living_id} vs {zombie_id}: {status}")
    for d in diffs:
        print(f"    {d}")
