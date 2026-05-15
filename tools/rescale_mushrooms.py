"""Set scaleMin=0.6, scaleMax=0.8 on all mushroom foragable env defs.

Targets defs by foragableType (Mushroom, MagicMushroom, PoisonMushroom,
Ghostcap, Rotgill, Toadstool). Edits data/env_defs.json in-place.
"""

import json
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
DEFS_PATH = ROOT / "data" / "env_defs.json"

MUSHROOM_TYPES = {
    "Mushroom",
    "MagicMushroom",
    "PoisonMushroom",
    "Ghostcap",
    "Rotgill",
    "Toadstool",
}

NEW_MIN = 0.6
NEW_MAX = 0.8


def main():
    print(f"Loading {DEFS_PATH}...")
    with open(DEFS_PATH, "r", encoding="utf-8") as f:
        data = json.load(f)

    # env_defs is a top-level array
    defs = data if isinstance(data, list) else data.get("defs") or data.get("env_defs")
    if not isinstance(defs, list):
        # Fallback: pick the first list value in the dict
        for v in data.values():
            if isinstance(v, list):
                defs = v
                break

    changed = []
    for d in defs:
        if not isinstance(d, dict):
            continue
        if d.get("foragableType") in MUSHROOM_TYPES:
            old_min = d.get("scaleMin")
            old_max = d.get("scaleMax")
            d["scaleMin"] = NEW_MIN
            d["scaleMax"] = NEW_MAX
            changed.append((d.get("id", "?"), d["foragableType"], old_min, old_max))

    print(f"\nUpdated {len(changed)} mushroom defs:")
    for did, ft, old_min, old_max in changed:
        print(f"  {did:24s} ({ft:14s}) {old_min}..{old_max} -> {NEW_MIN}..{NEW_MAX}")

    print(f"\nWriting {DEFS_PATH}...")
    with open(DEFS_PATH, "w", encoding="utf-8") as f:
        json.dump(data, f, indent=2)
    print("Done.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
