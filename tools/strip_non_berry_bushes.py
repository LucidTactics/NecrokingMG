"""Remove non-berry-group bushes from data/maps/default.json.

Keeps only the BerryBush1Ber..BerryBush5Ber entries (the BerryBush group from
env_defs.json). All other defs with category "Bush" are stripped from the map's
object list. Edits the file in-place.
"""

import json
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
MAP_PATH = ROOT / "data" / "maps" / "default.json"

KEEP_IDS = {f"BerryBush{i}Ber" for i in range(1, 6)}

# Non-berry bush def IDs from env_defs.json. Anything matching these gets stripped.
STRIP_IDS = {
    "berry_bush",      # the OLD generic Trees/BerryBush.png entry (no IsBerryBush flag)
    "bushy_holly",
    "dense_bush",
    "dense_hedge",
    "dwarf_spruce",
    "green_hedge",
    "hedge_row",
    "low_bush",
    "low_hedge",
    "round_shrub",
    "small_bush",
    "thick_bush",
    "wild_shrub",
}


def main():
    print(f"Loading {MAP_PATH} ({MAP_PATH.stat().st_size / 1024 / 1024:.1f} MB)...")
    with open(MAP_PATH, "r", encoding="utf-8") as f:
        data = json.load(f)

    # Find the objects list. Typical schema has "envObjects" or "objects" at top level.
    obj_keys = [k for k in data.keys() if "object" in k.lower() or "env" in k.lower()]
    print(f"Top-level keys: {list(data.keys())}")
    print(f"Object-ish keys: {obj_keys}")

    # Try common keys.
    for key in ("envObjects", "objects", "placedObjects"):
        if key in data and isinstance(data[key], list):
            objs = data[key]
            break
    else:
        # Walk all top-level lists, pick the one with defId entries.
        objs = None
        for k, v in data.items():
            if isinstance(v, list) and v and isinstance(v[0], dict) and "defId" in v[0]:
                objs = v
                key = k
                print(f"Auto-detected object list at key '{k}' ({len(v)} entries)")
                break
        if objs is None:
            print("ERROR: could not locate object list with defId entries")
            return 1

    before = len(objs)
    stripped_counts = {}
    kept_bush_counts = {}
    kept = []
    for o in objs:
        did = o.get("defId", "")
        if did in STRIP_IDS:
            stripped_counts[did] = stripped_counts.get(did, 0) + 1
            continue
        if did in KEEP_IDS:
            kept_bush_counts[did] = kept_bush_counts.get(did, 0) + 1
        kept.append(o)

    data[key] = kept
    after = len(kept)

    print(f"\nStripped (non-berry bushes):")
    for did, n in sorted(stripped_counts.items()):
        print(f"  {did}: {n}")
    print(f"  total stripped: {sum(stripped_counts.values())}")
    print(f"\nKept berry bushes (BerryBush group):")
    for did, n in sorted(kept_bush_counts.items()):
        print(f"  {did}: {n}")
    print(f"\nObjects: {before} -> {after} ({before - after} removed)")

    print(f"\nWriting {MAP_PATH}...")
    with open(MAP_PATH, "w", encoding="utf-8") as f:
        json.dump(data, f, indent=2)
    print("Done.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
