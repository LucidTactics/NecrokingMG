"""One-shot: populate `targetWalkCycle` per UnitDef with size-based defaults.
The editor uses this to compute the "Suggested CombatSpeed" hint next to the
CombatSpeed input. Values mirror the TARGET_CYCLES dict in audit_combat_speeds.py.

Idempotent — re-running is a no-op if values already match.
"""
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
UNITS_JSON = ROOT / "data" / "units.json"

TARGETS = {
    # Bipeds (human-class, ~1.8 wu tall)
    "skeleton": 1.2, "militia": 1.2, "knight": 1.2, "soldier": 1.2, "archer": 1.2,
    "pale_acolyte": 1.2, "wretched": 1.0, "necromancer": 1.2, "wight": 1.2,
    "lich": 1.2, "grand_necromancer": 1.2,
    "abomination": 1.5,

    # Wolves
    "Wolf": 0.8, "ZombieWolf": 0.8,
    "DireWolf": 1.0,
    "JuvWolf": 0.6, "WolfCubZombie": 0.5,

    # Deer
    "FemaleDeer": 1.0, "MaleDeer": 1.0,
    "ZombieFemaleDeer": 1.0, "ZombieMaleDeer": 1.0,

    # Bears
    "Bear": 1.7, "GrizzlyBear": 1.8, "ZombieGrizzlyBear": 1.8,
    "ZombieJuvenileBear": 1.4,

    # Boars
    "Boar": 1.0, "ZombieBoar": 1.0,
    "GreatBoar": 1.4, "ZombieGreatBoar": 1.4,
}

def main():
    data = json.loads(UNITS_JSON.read_text(encoding="utf-8"))
    units = data.get("units", [])
    changed, skipped, not_found = [], [], set(TARGETS.keys())
    for u in units:
        uid = u.get("id")
        if uid not in TARGETS:
            continue
        not_found.discard(uid)
        new = TARGETS[uid]
        if u.get("targetWalkCycle") == new:
            skipped.append(uid)
            continue
        u["targetWalkCycle"] = new
        changed.append((uid, new))
    if changed:
        UNITS_JSON.write_text(json.dumps(data, indent=2) + "\n", encoding="utf-8")
        for uid, v in sorted(changed):
            print(f"  {uid:<22} targetWalkCycle = {v}")
        print(f"Set {len(changed)} target cycles.")
    if skipped:
        print(f"Already set: {', '.join(sorted(skipped))}")
    if not_found:
        print(f"WARNING — not found: {', '.join(sorted(not_found))}")

if __name__ == "__main__":
    main()
