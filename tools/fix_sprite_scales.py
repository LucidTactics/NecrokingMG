#!/usr/bin/env python3
"""Restore animal / animal-zombie render sizes after a "compress rect" re-export.

WHY: A unit's on-screen size is `(spriteWorldHeight * spriteScale * zoom) / RefFrameHeight`,
where RefFrameHeight is the *packed* pixel height of the idle reference frame
(Game1.Animation.cs -> PickIdleFrames -> kfs[0].Frame.Rect.Height). Re-exporting the
animal sheets with "compress rect" trimmed the transparent padding, shrinking that packed
height. Smaller divisor -> bigger sprite. The creature's own pixels never changed.

FIX: For each affected unit, multiply its spriteScale by (newRefHeight / oldRefHeight).
That makes the new scale equal the old scale, so every frame renders at its pre-trim size
again. Exact and per-creature — no eyeballing.

old reference heights come from the pre-trim metadata frozen in a leftover agent worktree
(assets/ is gitignored, so git has no history). new heights come from the live assets.

This mirrors the game's RefFrameHeight selection EXACTLY: PickIdleFrames prefers the first
present angle in [30,0,45,60,315,90,270,300], then the lowest-time keyframe of that angle.

Usage:
    python tools/fix_sprite_scales.py            # dry run: print the before/after table
    python tools/fix_sprite_scales.py --apply    # write the new spriteScale values

The write is a line-level edit of `spriteScale:` for affected unit ids only; every other
byte of data/units.json is left untouched (clean diff, exact formatting preserved).
"""
import json
import os
import re
import sys

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
NEW_DIR = os.path.join(REPO, "assets", "Sprites")
OLD_DIR = os.path.join(REPO, ".claude", "worktrees", "agent-a57ed59d",
                       "Necroking", "assets", "Sprites")
UNITS_JSON = os.path.join(REPO, "data", "units.json")

# Atlases that were re-exported with compress rect (animals + animal zombies).
AFFECTED_ATLASES = {"Animals", "ZombieAnimals1", "ZombieAnimals2"}
NEW_METAS = ["Animals.spritemeta", "ZombieAnimals1.spritemeta", "ZombieAnimals2.spritemeta"]
OLD_METAS = ["Animals.spritemeta", "ZombieAnimals.spritemeta"]  # pre-split, pre-trim

# Game's PickIdleFrames angle preference (Game1.Render.Corpses.cs:175).
ANGLE_PREF = [30, 0, 45, 60, 315, 90, 270, 300]


def parse_meta(path):
    """unit -> anim -> angle -> sorted list[(time, height)]. Mirrors SpriteAtlas.ParseMeta."""
    data = {}
    with open(path, encoding="utf-8") as f:
        for raw in f:
            line = raw.strip()
            if not line:
                continue
            parts = line.split("\t")
            if len(parts) < 3:
                continue
            name = parts[0].split(".")
            if len(name) < 5:
                continue
            unit, anim = name[0], name[1]
            try:
                time = int(name[2])
                angle = int(name[4])
            except ValueError:
                continue
            rect = parts[1].split(",")
            if len(rect) < 4:
                continue
            try:
                height = int(rect[3])
            except ValueError:
                continue
            data.setdefault(unit, {}).setdefault(anim, {}).setdefault(angle, []).append((time, height))
    for ud in data.values():
        for ad in ud.values():
            for frames in ad.values():
                frames.sort(key=lambda t: t[0])
    return data


def ref_height(unitdata):
    """Replicate RefFrameHeight: Idle anim, first present angle by preference, lowest-time frame."""
    idle = unitdata.get("Idle")
    if not idle:
        return None
    chosen = None
    for a in ANGLE_PREF:
        if idle.get(a):
            chosen = idle[a]
            break
    if chosen is None:  # last resort: any authored angle
        for frames in idle.values():
            if frames:
                chosen = frames
                break
    return chosen[0][1] if chosen else None  # height of lowest-time keyframe


def build_refs(directory, metas):
    refs = {}
    for m in metas:
        path = os.path.join(directory, m)
        if not os.path.exists(path):
            print(f"  WARN: missing {path}")
            continue
        for unit, ud in parse_meta(path).items():
            r = ref_height(ud)
            if r:
                refs[unit] = r
    return refs


def fmt(v):
    return f"{v:.6f}".rstrip("0").rstrip(".")


def main():
    apply = "--apply" in sys.argv
    # Old export used a uniform 128px cell for every animal (verified: all matched
    # creatures, small to large, measured 128). For units added after the frozen
    # worktree snapshot (bears/boars) we have no real pre-trim ref, so optionally
    # fall back to that observed constant. Flagged "infer" in the table.
    assume_old = None
    if "--assume-old" in sys.argv:
        assume_old = int(sys.argv[sys.argv.index("--assume-old") + 1])

    new_ref = build_refs(NEW_DIR, NEW_METAS)
    old_ref = build_refs(OLD_DIR, OLD_METAS)
    print(f"Parsed refs: {len(new_ref)} new sprites, {len(old_ref)} old sprites.\n")

    with open(UNITS_JSON, encoding="utf-8") as f:
        doc = json.load(f)
    units = doc if isinstance(doc, list) else next(v for v in doc.values() if isinstance(v, list))

    corrections = {}   # id -> (atlas, sprite, oldScale, ratio, newScale, oldRef, newRef, inferred)
    unmatched = []     # (id, atlas, sprite, reason)
    for u in units:
        sp = u.get("sprite") or {}
        atlas, name = sp.get("atlas"), sp.get("name")
        if atlas not in AFFECTED_ATLASES:
            continue
        if name not in new_ref:
            unmatched.append((u.get("id"), atlas, name, "no current ref"))
            continue
        inferred = False
        old_h = old_ref.get(name)
        if old_h is None:
            if assume_old is None:
                unmatched.append((u.get("id"), atlas, name, "no pre-trim ref"))
                continue
            old_h = assume_old
            inferred = True
        ratio = new_ref[name] / old_h
        old_scale = float(u.get("spriteScale", 1.0))
        corrections[u.get("id")] = (atlas, name, old_scale, ratio,
                                    old_scale * ratio, old_h, new_ref[name], inferred)

    # Report
    print(f"{'unit id':28} {'atlas':16} {'oldH':>5} {'newH':>5} {'ratio':>7} {'scale':>9} -> {'newScale':>9}  src")
    print("-" * 102)
    for uid in sorted(corrections):
        atlas, name, oldsc, ratio, newsc, oh, nh, inferred = corrections[uid]
        src = "INFER" if inferred else "exact"
        print(f"{uid:28} {atlas:16} {oh:5d} {nh:5d} {ratio:7.4f} {oldsc:9.4f} -> {newsc:9.4f}  {src}")
    print(f"\n{len(corrections)} unit(s) to correct.")

    if unmatched:
        print(f"\n{len(unmatched)} affected-atlas unit(s) WITHOUT a usable ref (hand-tune these):")
        for uid, atlas, name, why in unmatched:
            print(f"  {uid} ({atlas}/{name}): {why}")

    if not apply:
        print("\n(dry run — re-run with --apply to write data/units.json)")
        return

    # Line-level edit: only touch `spriteScale:` lines of affected unit ids.
    with open(UNITS_JSON, encoding="utf-8") as f:
        lines = f.readlines()
    id_re = re.compile(r'^      "id": "(.+?)",?\s*$')
    scale_re = re.compile(r'^(      "spriteScale": )([^,]+)(,?)\s*$')
    current = None
    written = 0
    for i, line in enumerate(lines):
        m = id_re.match(line)
        if m:
            current = m.group(1)
            continue
        if current in corrections:
            sm = scale_re.match(line)
            if sm:
                newsc = corrections[current][4]
                lines[i] = f"{sm.group(1)}{fmt(newsc)}{sm.group(3)}\n"
                written += 1
                current = None  # only the first spriteScale per unit
    with open(UNITS_JSON, "w", encoding="utf-8", newline="") as f:
        f.writelines(lines)
    print(f"\nAPPLIED: rewrote {written} spriteScale value(s) in data/units.json")


if __name__ == "__main__":
    main()
