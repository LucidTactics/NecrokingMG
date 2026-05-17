"""Apply per-species acceleration tuning: maxAcceleration, maxDeceleration,
maxLateralAccel. Defaults from CombatSettings (6/25/15 human-like) apply to
anything not explicitly listed; this script just sets the bigger animals.

Decel/Accel ratio is ~4-5× for legged units (you brake faster than you start).
Lateral cap controls turn radius: r = v² / lateralAccel. Higher = sharper turns.

Idempotent: re-running is a no-op if values already match.
"""
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
UNITS_JSON = ROOT / "data" / "units.json"

# (accel, decel, lateral) in wu/s²
TARGETS = {
    # Bipeds with explicit values (others fall back to settings default 6/25/15)
    "knight":           (3.0, 15.0,  8.0),  # heavy armor — slower
    "abomination":      (4.0, 18.0,  9.0),  # bigger, lumbering
    # Wolves (nimble predators)
    "Wolf":             (10.0, 50.0, 30.0),
    "DireWolf":         (8.0,  40.0, 22.0),  # larger wolf, slightly slower
    "JuvWolf":          (12.0, 55.0, 35.0),  # smallest, fastest
    "ZombieWolf":       (8.0,  40.0, 25.0),  # undead — less reactive
    "WolfCubZombie":    (10.0, 45.0, 30.0),
    # Deer (skittish, sharp turns)
    "FemaleDeer":       (12.0, 60.0, 35.0),
    "MaleDeer":         (12.0, 60.0, 35.0),
    "ZombieFemaleDeer": (8.0,  40.0, 22.0),
    "ZombieMaleDeer":   (8.0,  40.0, 22.0),
    # Bears (lumbering, slow turns)
    "Bear":             (4.0, 20.0, 10.0),
    "GrizzlyBear":      (3.0, 16.0,  8.0),
    "ZombieJuvenileBear": (3.0, 16.0,  8.0),
    "ZombieGrizzlyBear":  (2.5, 12.0,  6.0),
    # Boars (charge-specialist, poor lateral)
    "Boar":             (5.0, 15.0,  8.0),
    "GreatBoar":        (4.0, 12.0,  6.0),
    "ZombieBoar":       (4.0, 12.0,  6.0),
    "ZombieGreatBoar":  (3.0, 10.0,  5.0),
}

def main():
    data = json.loads(UNITS_JSON.read_text(encoding="utf-8"))
    units = data.get("units", [])
    changed, skipped = [], []
    not_found = set(TARGETS.keys())
    for u in units:
        uid = u.get("id")
        if uid not in TARGETS:
            continue
        not_found.discard(uid)
        accel, decel, lat = TARGETS[uid]
        cur = (u.get("maxAcceleration"), u.get("maxDeceleration"), u.get("maxLateralAccel"))
        if cur == (accel, decel, lat):
            skipped.append(uid)
            continue
        u["maxAcceleration"] = accel
        u["maxDeceleration"] = decel
        u["maxLateralAccel"] = lat
        changed.append((uid, accel, decel, lat))
    if changed:
        UNITS_JSON.write_text(json.dumps(data, indent=2) + "\n", encoding="utf-8")
        print(f"Tuned {len(changed)} units:")
        for uid, a, d, l in sorted(changed):
            print(f"  {uid:<22} accel={a} decel={d} lat={l}")
    if skipped:
        print(f"Already set: {', '.join(sorted(skipped))}")
    if not_found:
        print(f"WARNING — not found: {', '.join(sorted(not_found))}")

if __name__ == "__main__":
    main()
