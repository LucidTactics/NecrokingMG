"""Revert the CombatSpeed changes made by retune_all_to_propCS.py. Reads the
backup file cs_backup_propCS_change.json and restores each unit's previous
CombatSpeed. Safe to run at any time — it always uses the most recent backup.
"""
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
UNITS_JSON = ROOT / "data" / "units.json"
BACKUP = Path(__file__).resolve().parent / "cs_backup_propCS_change.json"

def main():
    if not BACKUP.exists():
        print(f"No backup found at {BACKUP}. Nothing to revert.")
        return
    backup = json.loads(BACKUP.read_text(encoding="utf-8"))
    prior = backup.get("prior_combat_speeds", {})
    if not prior:
        print("Backup has no prior values. Nothing to revert.")
        return

    data = json.loads(UNITS_JSON.read_text(encoding="utf-8"))
    units = data.get("units", [])
    changes = []
    not_found = set(prior.keys())
    for u in units:
        uid = u.get("id")
        if uid not in prior:
            continue
        not_found.discard(uid)
        stats = u.get("stats")
        if not isinstance(stats, dict):
            continue
        cur = stats.get("combatSpeed")
        restored = prior[uid]
        if cur == restored:
            continue
        changes.append((uid, cur, restored))
        stats["combatSpeed"] = restored

    if changes:
        UNITS_JSON.write_text(json.dumps(data, indent=2) + "\n", encoding="utf-8")
        print(f"Reverted {len(changes)} CombatSpeed values from backup "
              f"({backup.get('saved_at_utc', '?')}):")
        for uid, cur, restored in sorted(changes):
            print(f"  {uid:<22} {cur} -> {restored}")
    else:
        print("No changes — everything already matches backup.")
    if not_found:
        print(f"Note — id not found in units.json: {', '.join(sorted(not_found))}")

if __name__ == "__main__":
    main()
