"""Mark known quadruped unit-defs in units.json: set `isQuadruped: true` and
`dutyCycle: 0.5` (alternating-diagonal-pair walk, same duty as bipeds —
empirically matches the artist-drawn gait pattern better than the 0.75 lateral-
sequence walk assumption v6-v7 used).

Idempotent: re-running is a no-op (skips entries already at the target values).
"""
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
UNITS_JSON = ROOT / "data" / "units.json"

QUADRUPEDS = {
    "FemaleDeer", "MaleDeer",
    "ZombieFemaleDeer", "ZombieMaleDeer",
    "Wolf", "DireWolf", "JuvWolf",
    "ZombieWolf", "WolfCubZombie",
    "Bear", "GrizzlyBear",
    "ZombieJuvenileBear", "ZombieGrizzlyBear",
    "Boar", "GreatBoar",
    "ZombieBoar", "ZombieGreatBoar",
}

def main():
    text = UNITS_JSON.read_text(encoding="utf-8")
    data = json.loads(text)
    units = data.get("units", [])
    changed = []
    skipped_already = []
    not_found = set(QUADRUPEDS)
    for u in units:
        uid = u.get("id")
        if uid in QUADRUPEDS:
            not_found.discard(uid)
            if u.get("isQuadruped") is True and u.get("dutyCycle") == 0.5:
                skipped_already.append(uid)
                continue
            u["isQuadruped"] = True
            u["dutyCycle"] = 0.5
            changed.append(uid)
    if not changed and not skipped_already:
        print("No matching quadruped units found — nothing to do.")
    else:
        # Preserve original indent (2 spaces) and trailing newline.
        out = json.dumps(data, indent=2) + "\n"
        UNITS_JSON.write_text(out, encoding="utf-8")
        print(f"Marked {len(changed)} quadrupeds: {', '.join(sorted(changed))}")
        if skipped_already:
            print(f"Already marked: {', '.join(sorted(skipped_already))}")
    if not_found:
        print(f"WARNING — id not found in units.json: {', '.join(sorted(not_found))}")

if __name__ == "__main__":
    main()
