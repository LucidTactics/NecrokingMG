"""
Plan (and optionally execute) the defunct-asset cleanup.

Dry-run by default: prints the plan, writes nothing. Pass --execute to perform it.

Rules:
  - DEFUNCT def = env_def that is (a) NOT placed on the map, (b) NOT a building
    (texture under /Buildings/ or isBuilding/playerBuildable), (c) NOT functional
    (input/output/slots/essence/trap/glyph/trigger), (d) OLD: its texture entered
    git on/before the cutoff date. New/uncommitted unplaced objects (e.g. Oak1-6,
    SwampTree5, logs) are KEPT.
  - A def's image FILE is moved to "defunct assets/<same path>" ONLY if it is not
    in the KEEP set (referenced by any kept def, ground/grass, wall_defs, roads,
    triggers, or code). Files also used elsewhere stay in place; only the def entry
    is removed.
  - Removed def entries are backed up to "defunct assets/defunct_env_defs.json".
"""
import json, os, subprocess, sys, shutil, glob

ROOT = "."
ENV = "data/env_defs.json"
MAP = "data/maps/default.json"
DEFUNCT_DIR = "defunct assets"
CUTOFF = "2026-04-10"   # textures added to git before this are "old"; on/after = new (keep)
EXECUTE = "--execute" in sys.argv

_gitcache = {}
def git_add_date(path):
    if path in _gitcache: return _gitcache[path]
    try:
        out = subprocess.run(["git","log","--diff-filter=A","--follow","--format=%aI","-1","--",path],
                             capture_output=True, text=True, cwd=ROOT, timeout=20)
        s = out.stdout.strip()
        v = s[:10] if s else None
    except Exception:
        v = None
    _gitcache[path] = v
    return v

def png_fields(d):
    return [v for v in d.values() if isinstance(v, str) and v.lower().endswith(".png")]

def is_building(d):
    return bool(d.get("isBuilding") or d.get("playerBuildable") or "/Buildings/" in (d.get("texturePath") or ""))

def is_functional(d):
    if d.get("autoSpawn") or d.get("isGlyphTrap"): return True
    if any(d.get(n,0) for n in ("corpseSlots","itemSlots","essenceCost")): return True
    if any((d.get(n) or "").strip() for n in ("boundTriggerID","trapSpellId")): return True
    for io in ("input1","input2","output"):
        if ((d.get(io) or {}).get("kind") or "").strip(): return True
    return False

def norm(p):  # case-insensitive, forward-slash key
    return p.replace("\\","/").lower()

defs = json.load(open(ENV, encoding="utf-8"))
mp = json.load(open(MAP, encoding="utf-8"))
placed_ids = set(o["defId"] for o in mp.get("placedObjects", []))

# Classify
defunct, kept_defs = [], []
for d in defs:
    did = d.get("id","")
    tex = d.get("texturePath","")
    if did in placed_ids or is_building(d) or is_functional(d) or d.get("isForagable"):
        kept_defs.append(d); continue
    ga = git_add_date(tex) if tex else None
    is_new = (ga is None) or (ga >= CUTOFF)   # uncommitted or added on/after cutoff => keep
    if is_new:
        kept_defs.append(d)
    else:
        defunct.append(d)

# KEEP image set (normalized): kept defs + ground/grass + wall_defs + map jsons + code
keep = set()
for d in kept_defs:
    for p in png_fields(d): keep.add(norm(p))
for g in mp.get("groundTypes", []):
    if g.get("texturePath"): keep.add(norm(g["texturePath"]))
for g in mp.get("grassTypes", []):
    for p in g.get("spritePaths", []) or []: keep.add(norm(p))
def add_pngs_from_text(path):
    try: txt = open(path, encoding="utf-8", errors="ignore").read()
    except OSError: return
    import re
    for m in re.findall(r"assets/[A-Za-z0-9_/\.]+\.png", txt, re.IGNORECASE):
        keep.add(norm(m))
add_pngs_from_text("data/wall_defs.json")
# Scan map sidecar JSONs, but SKIP the legacy data/maps/env_defs.json (dead file the
# engine ignores — MapData.cs skips embedded envDefs) and default.json (its real
# ground/grass refs are already captured from the parsed map above).
for f in glob.glob("data/maps/*.json"):
    base = os.path.basename(f).lower()
    if base in ("env_defs.json", "default.json"): continue
    add_pngs_from_text(f)
for f in glob.glob("Necroking/**/*.cs", recursive=True): add_pngs_from_text(f)

# Movable files = defunct defs' pngs that exist and are NOT in keep set
move_files, kept_in_place = [], []
for d in defunct:
    for p in png_fields(d):
        if not os.path.exists(p):       continue
        (kept_in_place if norm(p) in keep else move_files).append(p)
move_files = sorted(set(move_files))
kept_in_place = sorted(set(kept_in_place))

import collections
per_folder = collections.Counter("/".join(p.split("/")[:3]) for p in move_files)

print(f"{'EXECUTE' if EXECUTE else 'DRY-RUN'}")
print(f"env_defs total={len(defs)}  kept={len(kept_defs)}  DEFUNCT defs={len(defunct)}")
print(f"image files to MOVE: {len(move_files)}")
print(f"image files KEPT-IN-PLACE (def removed but file still referenced): {len(kept_in_place)}")
for p in kept_in_place: print(f"    keep-file: {p}")
print("MOVE per folder:")
for k,v in sorted(per_folder.items()): print(f"    {k:40} {v}")
print("\nDEFUNCT def ids ({}):".format(len(defunct)))
print("   " + ", ".join(d.get("id","") for d in defunct))

if not EXECUTE:
    print("\n(dry-run; pass --execute to move files, rewrite env_defs.json, write backup)")
    sys.exit(0)

# ---- EXECUTE ----
moved = 0
for p in move_files:
    # Mirror the assets/ structure WITHOUT the redundant leading "assets/" segment,
    # so "defunct assets/Environment/Rocks/X.png" parallels "assets/Environment/Rocks/X.png".
    rel = p[len("assets/"):] if p.lower().startswith("assets/") else p
    dst = os.path.join(DEFUNCT_DIR, rel)
    os.makedirs(os.path.dirname(dst), exist_ok=True)
    shutil.move(p, dst)
    moved += 1

# Backup removed defs, then rewrite env_defs without them
defunct_ids = set(d.get("id","") for d in defunct)
os.makedirs(DEFUNCT_DIR, exist_ok=True)
backup_path = os.path.join(DEFUNCT_DIR, "defunct_env_defs.json")
existing = []
if os.path.exists(backup_path):
    try: existing = json.load(open(backup_path, encoding="utf-8"))
    except Exception: existing = []
existing_ids = set(e.get("id","") for e in existing)
existing.extend(d for d in defunct if d.get("id","") not in existing_ids)
json.dump(existing, open(backup_path, "w", encoding="utf-8"), indent=2, ensure_ascii=False)

kept_only = [d for d in defs if d.get("id","") not in defunct_ids]
json.dump(kept_only, open(ENV, "w", encoding="utf-8"), indent=2, ensure_ascii=False)

print(f"\nMOVED {moved} files to '{DEFUNCT_DIR}/'.")
print(f"Backed up {len(defunct)} defs to {backup_path}.")
print(f"Rewrote {ENV}: {len(defs)} -> {len(kept_only)} defs.")
