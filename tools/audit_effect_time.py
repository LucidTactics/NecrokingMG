"""Audit attack/impact animations for missing effect_time_ms.

All combat choreography timing flows from effect_time_ms (the anim's impact
frame): damage rolls there, hit reacts key off the damage, pounce liftoff and
landing sync to it. An attack anim without one falls back to 50%-of-clip,
which usually looks mistimed. Run this after adding sprites/animations:

    python tools/audit_effect_time.py

Lists every (sprite, category) whose meta lacks a positive effect_time_ms,
for the categories where the impact frame matters.
"""
import glob
import json
import os
import re

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
META_GLOB = os.path.join(REPO, "assets", "Sprites", "*.animationmeta")

# Categories whose effect_time_ms drives gameplay timing.
PREFIXES = ("Attack", "Shoot", "Spell", "JumpTakeoff", "JumpLand", "Kick", "Bite")

LINE_RE = re.compile(
    r'"unit"\s*:\s*"([^"]+)"\s*,\s*"category"\s*:\s*"([^"]+)"')
EFFECT_RE = re.compile(r'"effect_time_ms"\s*:\s*(-?\d+)')

def main():
    missing = {}   # (atlas, sprite, category) -> True
    present = set()
    files = sorted(glob.glob(META_GLOB))
    if not files:
        print(f"no .animationmeta files found under {META_GLOB}")
        return
    for path in files:
        atlas = os.path.splitext(os.path.basename(path))[0]
        with open(path, "r", encoding="utf-8") as f:
            for line in f:
                m = LINE_RE.search(line)
                if not m:
                    continue
                sprite, category = m.group(1), m.group(2)
                if not category.startswith(PREFIXES):
                    continue
                key = (atlas, sprite, category)
                e = EFFECT_RE.search(line)
                if e and int(e.group(1)) > 0:
                    present.add(key)
                elif key not in missing:
                    missing[key] = True

    # A category is fine if ANY yaw variant carries the value (they share timing).
    problems = sorted(k for k in missing if k not in present)
    if not problems:
        print(f"OK: all {len(present)} impact-timed anims have effect_time_ms")
        return
    print(f"{len(problems)} anims missing effect_time_ms "
          f"({len(present)} OK). Damage/impact will fall back to 50% of clip:\n")
    last_atlas = None
    for atlas, sprite, category in problems:
        if atlas != last_atlas:
            print(f"[{atlas}]")
            last_atlas = atlas
        print(f"  {sprite}.{category}")

if __name__ == "__main__":
    main()
