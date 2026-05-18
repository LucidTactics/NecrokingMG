"""Tag undead units in units.json with UndeadCategory (Human/Monster).
Living units, player evolutions (wretched/lich/etc.) stay UndeadCategory.None
since the necromancer doesn't count against caps and the game serialises
"None" by default. Idempotent.
"""
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
UNITS_JSON = ROOT / "data" / "units.json"

# id -> category
TAGS = {
    # Human-derived
    "skeleton":             "Human",
    "skeleton_copy":        "Human",
    "skeleton_copy_copy":   "Human",
    "skeleton_copy_copy_copy": "Human",
    "abomination":          "Human",
    # Animal/monster-derived
    "ZombieWolf":           "Monster",
    "WolfCubZombie":        "Monster",
    "ZombieFemaleDeer":     "Monster",
    "ZombieMaleDeer":       "Monster",
    "ZombieJuvenileBear":   "Monster",
    "ZombieGrizzlyBear":    "Monster",
    "ZombieBoar":           "Monster",
    "ZombieGreatBoar":      "Monster",
}

def main():
    data = json.loads(UNITS_JSON.read_text(encoding="utf-8"))
    units = data.get("units", [])
    changed, skipped = [], []
    not_found = set(TAGS.keys())
    for u in units:
        uid = u.get("id")
        if uid not in TAGS:
            continue
        not_found.discard(uid)
        cat = TAGS[uid]
        cur = u.get("undeadCategory")
        if cur == cat:
            skipped.append(uid)
            continue
        u["undeadCategory"] = cat
        changed.append((uid, cat))
    if changed:
        UNITS_JSON.write_text(json.dumps(data, indent=2) + "\n", encoding="utf-8")
        print(f"Tagged {len(changed)} units:")
        for uid, cat in sorted(changed):
            print(f"  {uid:<25} -> {cat}")
    if skipped:
        print(f"Already tagged: {', '.join(sorted(skipped))}")
    if not_found:
        print(f"WARNING - not found in units.json: {', '.join(sorted(not_found))}")

if __name__ == "__main__":
    main()
