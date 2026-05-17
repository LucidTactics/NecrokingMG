"""One-shot: set jogSpeedMultiplier=3.0 and sprintSpeedMultiplier=9.0 on every
quadruped UnitDef. Quadrupeds run much faster than they walk than bipeds do
(real horses gallop ~9× walk speed). Default biped values (2.0/4.0) stay
implicit on biped UnitDefs.

Idempotent: re-running is a no-op if values already match.
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
    data = json.loads(UNITS_JSON.read_text(encoding="utf-8"))
    units = data.get("units", [])
    changed, skipped = [], []
    not_found = set(QUADRUPEDS)
    for u in units:
        uid = u.get("id")
        if uid not in QUADRUPEDS:
            continue
        not_found.discard(uid)
        if u.get("jogSpeedMultiplier") == 3.0 and u.get("sprintSpeedMultiplier") == 9.0:
            skipped.append(uid)
            continue
        u["jogSpeedMultiplier"] = 3.0
        u["sprintSpeedMultiplier"] = 9.0
        changed.append(uid)
    if changed:
        UNITS_JSON.write_text(json.dumps(data, indent=2) + "\n", encoding="utf-8")
        print(f"Set jog=3.0, sprint=9.0 on {len(changed)} quadrupeds:")
        for uid in sorted(changed):
            print(f"  {uid}")
    if skipped:
        print(f"Already set: {', '.join(sorted(skipped))}")
    if not_found:
        print(f"WARNING — not found: {', '.join(sorted(not_found))}")

if __name__ == "__main__":
    main()
