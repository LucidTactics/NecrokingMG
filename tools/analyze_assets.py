"""
READ-ONLY analysis for the defunct-asset cleanup. Produces a plan; moves nothing.

Classifies every env_def in data/env_defs.json as:
  PLACED      - its id appears in default.json placedObjects (keep)
  BUILDING    - building / player-buildable / texture under Environment/Buildings (keep, user exception)
  FUNCTIONAL  - has functional fields (input/output/autospawn/slots/essence/trap/glyph/trigger) (keep)
  FORAGABLE   - isForagable (review separately - gameplay objects)
  DEFUNCT?    - unplaced plain object (candidate to move)

For DEFUNCT candidates, lists image files + their git first-add date + filesystem mtime,
and compares against the date range of currently-PLACED tree/object images ("current era").
"""
import json, os, subprocess, datetime, collections

ROOT = "."
ENV = "data/env_defs.json"
MAP = "data/maps/default.json"

def git_add_date(path):
    try:
        out = subprocess.run(
            ["git", "log", "--diff-filter=A", "--follow", "--format=%aI", "-1", "--", path],
            capture_output=True, text=True, cwd=ROOT, timeout=20)
        s = out.stdout.strip()
        return s[:10] if s else None
    except Exception:
        return None

def mtime(path):
    try:
        return datetime.date.fromtimestamp(os.path.getmtime(path)).isoformat()
    except OSError:
        return None

def png_fields(defn):
    """All string values that look like image paths."""
    out = []
    for k, v in defn.items():
        if isinstance(v, str) and v.lower().endswith(".png"):
            out.append(v)
    return out

def is_building(d):
    tex = (d.get("texturePath") or "")
    return (d.get("isBuilding") or d.get("playerBuildable")
            or "/Buildings/" in tex)

def is_functional(d):
    if d.get("autoSpawn") or d.get("isGlyphTrap"):
        return True
    for n in ("corpseSlots", "itemSlots", "essenceCost"):
        if d.get(n, 0):
            return True
    for n in ("boundTriggerID", "trapSpellId"):
        if (d.get(n) or "").strip():
            return True
    for io in ("input1", "input2", "output"):
        sub = d.get(io) or {}
        if (sub.get("kind") or "").strip():
            return True
    return False

defs = json.load(open(ENV, encoding="utf-8"))
mp = json.load(open(MAP, encoding="utf-8"))
placed_ids = collections.Counter(o["defId"] for o in mp.get("placedObjects", []))

buckets = collections.defaultdict(list)
for d in defs:
    did = d.get("id", "")
    if did in placed_ids:
        buckets["PLACED"].append(d)
    elif is_building(d):
        buckets["BUILDING"].append(d)
    elif is_functional(d):
        buckets["FUNCTIONAL"].append(d)
    elif d.get("isForagable"):
        buckets["FORAGABLE"].append(d)
    else:
        buckets["DEFUNCT"].append(d)

print(f"TOTAL env_defs: {len(defs)}   placed-object instances: {sum(placed_ids.values())}   distinct placed ids: {len(placed_ids)}")
for k in ("PLACED", "BUILDING", "FUNCTIONAL", "FORAGABLE", "DEFUNCT"):
    print(f"  {k:11}: {len(buckets[k])}")

# Current era: git-add dates of PLACED objects' textures (esp. trees)
print("\n=== CURRENT-ERA placed images (git-add date) — folder: date count ===")
era = collections.defaultdict(list)
for d in buckets["PLACED"]:
    for p in png_fields(d):
        folder = "/".join(p.split("/")[:3])
        era[folder].append(git_add_date(p) or "??")
for folder in sorted(era):
    dates = sorted(x for x in era[folder] if x != "??")
    if dates:
        print(f"  {folder:38} {dates[0]} .. {dates[-1]}  (n={len(era[folder])})")

print("\n=== DEFUNCT CANDIDATES (unplaced plain objects) ===")
print(f"{'id':28} {'folder':34} {'git-add':12} {'mtime':12} name")
defunct_files = []
for d in sorted(buckets["DEFUNCT"], key=lambda x: x.get("texturePath", "")):
    imgs = png_fields(d)
    tex = d.get("texturePath", "")
    folder = "/".join(tex.split("/")[:3]) if tex else "(no texture)"
    ga = git_add_date(tex) if tex else None
    mt = mtime(tex) if tex else None
    print(f"{d.get('id',''):28} {folder:34} {str(ga):12} {str(mt):12} {d.get('name','')}")
    for p in imgs:
        defunct_files.append(p)

print(f"\nDEFUNCT candidate image files: {len(defunct_files)} (across {len(buckets['DEFUNCT'])} defs)")
by_folder = collections.Counter("/".join(p.split('/')[:3]) for p in defunct_files)
print("Per-folder image counts:")
for folder, n in sorted(by_folder.items()):
    print(f"  {folder:38} {n}")

# Missing files (referenced but not on disk)
missing = [p for p in defunct_files if not os.path.exists(p)]
if missing:
    print(f"\n(NB {len(missing)} referenced images don't exist on disk)")
