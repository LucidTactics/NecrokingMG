"""Apply PropCS values from the v7 stride audit to every unit (bipeds +
quadrupeds). Saves the previous CombatSpeed values to a backup file in this
directory so the change can be reverted via revert_propCS.py.

Idempotent: if previous values already match the targets, no change is made
and no new backup is written.
"""
import json
from datetime import datetime
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
UNITS_JSON = ROOT / "data" / "units.json"
BACKUP = Path(__file__).resolve().parent / "cs_backup_propCS_change.json"

# From audit_combat_speeds.py output 2026-05-17 with v8 algorithm (envelope-
# based stride measurement) and quadruped dutyCycle = 0.5. PropCS = (stride
# /dutyCycle/pxPerWorld) / TgtCyc — the CombatSpeed that locks feet AND hits
# the target cycle time when the unit walks. Values nearly doubled for
# quadrupeds vs the v7 numbers (when dutyCycle was 0.75 and stride was
# per-frame instead of envelope).
TARGETS = {
    # Quadrupeds (17)
    "JuvWolf": 1.86,
    "ZombieBoar": 0.75,
    "MaleDeer": 0.84,
    "FemaleDeer": 0.91,
    "Boar": 0.91,
    "ZombieJuvenileBear": 0.97,
    "WolfCubZombie": 2.23,
    "DireWolf": 2.09,
    "ZombieGreatBoar": 0.87,
    "ZombieGrizzlyBear": 1.10,
    "ZombieWolf": 1.93,
    "Bear": 0.80,
    "ZombieMaleDeer": 0.84,
    "GreatBoar": 1.14,
    "ZombieFemaleDeer": 0.91,
    "GrizzlyBear": 1.10,
    "Wolf": 1.93,
    # Bipeds (12)
    "militia": 1.62,
    "grand_necromancer": 2.24,
    "necromancer": 2.37,
    "archer": 1.72,
    "lich": 2.52,
    "wight": 1.81,
    "soldier": 1.97,
    "knight": 1.97,
    "skeleton": 1.41,
    "pale_acolyte": 1.77,
    "abomination": 5.59,
    "wretched": 2.16,
}

def main():
    data = json.loads(UNITS_JSON.read_text(encoding="utf-8"))
    units = data.get("units", [])
    changes = []  # (id, old, new)
    not_found = set(TARGETS.keys())
    skipped_same = []

    for u in units:
        uid = u.get("id")
        if uid not in TARGETS:
            continue
        not_found.discard(uid)
        stats = u.get("stats")
        if not isinstance(stats, dict):
            print(f"WARN: {uid} has no stats block — skipping")
            continue
        cur = stats.get("combatSpeed")
        new = TARGETS[uid]
        if cur == new:
            skipped_same.append(uid)
            continue
        changes.append((uid, cur, new))
        stats["combatSpeed"] = new

    if changes:
        # Backup BEFORE writing units.json — but only on first run. If a backup
        # already exists, it has the ORIGINAL pre-PropCS values (from before
        # any of this work) and we want to preserve that as the revert path.
        # Subsequent re-runs (e.g. when PropCS values shift due to algo bumps)
        # shouldn't overwrite it.
        if not BACKUP.exists():
            prior = {uid: old for uid, old, _ in changes}
            backup_payload = {
                "saved_at_utc": datetime.utcnow().isoformat() + "Z",
                "source": "PropCS application — original pre-PropCS values",
                "prior_combat_speeds": prior,
            }
            BACKUP.write_text(json.dumps(backup_payload, indent=2) + "\n",
                              encoding="utf-8")
            print(f"Backed up {len(prior)} prior CS values to {BACKUP.name}")
        else:
            print(f"Backup already exists at {BACKUP.name} — preserving original revert path.")
        UNITS_JSON.write_text(json.dumps(data, indent=2) + "\n", encoding="utf-8")
        print("Changes:")
        for uid, old, new in sorted(changes):
            print(f"  {uid:<22} {old} -> {new}")
    else:
        print("No changes — every target already at PropCS.")
    if skipped_same:
        print(f"Already at PropCS: {', '.join(sorted(skipped_same))}")
    if not_found:
        print(f"WARNING — id not found: {', '.join(sorted(not_found))}")

if __name__ == "__main__":
    main()
