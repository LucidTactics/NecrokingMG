"""
Fix mushroom foragableType values in env_defs.json files.

Each mushroom should have its own unique foragableType instead of all being "Mushroom".
"""

import json
import os

# Mapping from mushroom name to foragableType
MUSHROOM_TYPE_MAP = {
    "Deathcap": "Mushroom",          # Keep as generic Mushroom
    "Ghostcap": "Ghostcap",
    "Magic Mushroom": "MagicMushroom",
    "Poison Mushroom": "PoisonMushroom",
    "Rotgill": "Rotgill",
    "Toadstool": "Toadstool",
}


def update_defs(defs, filepath):
    """Update foragableType for mushroom definitions in a list of env defs."""
    changed = 0
    for item in defs:
        if not isinstance(item, dict):
            continue
        name = item.get("name")
        if name in MUSHROOM_TYPE_MAP:
            old_type = item.get("foragableType")
            new_type = MUSHROOM_TYPE_MAP[name]
            if old_type != new_type:
                item["foragableType"] = new_type
                changed += 1
                print(f"  [{filepath}] {name}: {old_type} -> {new_type}")
            else:
                print(f"  [{filepath}] {name}: already {new_type} (no change)")
    return changed


def process_file(filepath):
    """Process a single JSON file, detecting format automatically."""
    print(f"\nProcessing: {filepath}")

    if not os.path.exists(filepath):
        print(f"  SKIPPED: file does not exist")
        return 0

    with open(filepath, "r", encoding="utf-8") as f:
        data = json.load(f)

    # Detect format: flat array or dict with envDefs key
    if isinstance(data, list):
        # Flat array format (maps/env_defs.json)
        changed = update_defs(data, filepath)
    elif isinstance(data, dict):
        defs = data.get("envDefs")
        if defs is not None and isinstance(defs, list):
            changed = update_defs(defs, filepath)
        else:
            print(f"  SKIPPED: dict but no envDefs array found")
            return 0
    else:
        print(f"  SKIPPED: unexpected top-level type {type(data).__name__}")
        return 0

    if changed > 0:
        with open(filepath, "w", encoding="utf-8") as f:
            json.dump(data, f, indent=2, ensure_ascii=False)
            f.write("\n")
        print(f"  Wrote {changed} changes")
    else:
        print(f"  No changes needed")

    return changed


def main():
    base = "e:/Nightfall/NecrokingMG/Necroking/bin/Publish"
    files = [
        os.path.join(base, "maps", "env_defs.json"),
        os.path.join(base, "assets", "maps", "env_defs.json"),
        os.path.join(base, "assets", "maps", "default.json"),
    ]

    total = 0
    for filepath in files:
        total += process_file(filepath)

    print(f"\nDone. Total changes: {total}")


if __name__ == "__main__":
    main()
