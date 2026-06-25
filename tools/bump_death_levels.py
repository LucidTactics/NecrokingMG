#!/usr/bin/env python3
"""One-off: bump every playable necromancer form's Death magic-path level by +1.

Reanimate Corpse is a Death-1 ability, so the base Wretched form (Death 0) needs
to reach Death 1 to cast it; every later form steps up in lockstep. The debug
form (all paths = 9) is intentionally left alone.

Preserves units.json's exact serialization (2-space indent, ensure_ascii, no
trailing newline) — same as tools/json_data.py — so the diff stays minimal.
"""
import json
import sys

PATH = "data/units.json"
FORMS = ["necromancer", "wretched", "pale_acolyte", "wight", "lich", "grand_necromancer"]

with open(PATH, "r", encoding="utf-8") as f:
    root = json.load(f)

units = root["units"]
by_id = {u["id"]: u for u in units}

for fid in FORMS:
    u = by_id.get(fid)
    if u is None:
        print(f"WARN: form '{fid}' not found", file=sys.stderr)
        continue
    paths = u.setdefault("paths", {})
    before = paths.get("death", 0)
    paths["death"] = before + 1
    print(f"  {fid}: death {before} -> {paths['death']}")

with open(PATH, "w", encoding="utf-8") as f:
    json.dump(root, f, indent=2, ensure_ascii=True)

print(f"OK: bumped Death +1 on {len(FORMS)} forms")
