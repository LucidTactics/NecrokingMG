"""One-shot tuning pass: set each biped's combatSpeed to its PropCS value from
the audit (`tools/audit_combat_speeds.py`). PropCS = the pixel walk velocity
that makes Walk anim play at exactly 1.0x cadence at the unit's max walk speed,
i.e. perfectly grounded + natural-looking.

Idempotent: re-running is a no-op if values already match. Run once, commit.
"""
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
UNITS_JSON = ROOT / "data" / "units.json"

# Pulled from audit_combat_speeds.py output (2026-05-17). Biped units only;
# quadrupeds are deferred pending the user's gait-formula debugging.
TARGETS = {
    "skeleton": 1.3,
    "pale_acolyte": 1.4,
    "wretched": 1.4,
    "knight": 1.8,
    "wight": 1.7,
    "archer": 1.7,
    "soldier": 1.8,
    "militia": 2.3,
    "lich": 2.2,
    "grand_necromancer": 2.3,
    "necromancer": 2.4,
    "abomination": 4.2,
}

def main():
    data = json.loads(UNITS_JSON.read_text(encoding="utf-8"))
    units = data.get("units", [])
    changed = []
    skipped_same = []
    not_found = set(TARGETS.keys())
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
        stats["combatSpeed"] = new
        changed.append((uid, cur, new))
    if changed:
        UNITS_JSON.write_text(json.dumps(data, indent=2) + "\n", encoding="utf-8")
        print("Changed:")
        for uid, old, new in sorted(changed):
            print(f"  {uid:<22} {old} -> {new}")
    else:
        print("No changes — every target already at proposed value.")
    if skipped_same:
        print(f"Already at target: {', '.join(sorted(skipped_same))}")
    if not_found:
        print(f"WARNING — id not found: {', '.join(sorted(not_found))}")

if __name__ == "__main__":
    main()
