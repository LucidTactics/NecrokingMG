"""
Defunct a specific list of tree defs (from the editor screenshot): remove each
from env_defs.json, back it up, and move its image(s) to "defunct assets/" unless
a kept def/ground/grass/wall/code still references the file. Refuses if any target
is placed on the map. Dry-run by default; --execute to apply.
"""
import json, os, glob, re, shutil, sys, collections

DEFUNCT_DIR = "defunct assets"
EXECUTE = "--execute" in sys.argv

TARGET_IDS = [
    "autumn_maple","green_fir","old_yew","red_oak","round_maple","silver_birch",
    "slender_birch","stout_ash","sturdy_beech","tall_cedar","tall_linden","tall_pine",
    "thick_willow","wild_cherry","winter_pine","puffy_tree_1","puffy_tree_2","red_tree_2",
    "Oak1","Oak2","Oak3","Oak4","Oak5","Oak6",
]

def pngs_in_text(path):
    try: txt = open(path, encoding="utf-8", errors="ignore").read()
    except OSError: return []
    return re.findall(r"assets/[A-Za-z0-9_/\.\- ]+?\.png", txt, re.IGNORECASE)
def norm(p): return p.replace("\\","/").lower()
def png_fields(d): return [v for v in d.values() if isinstance(v,str) and v.lower().endswith(".png")]

defs = json.load(open("data/env_defs.json", encoding="utf-8"))
mp = json.load(open("data/maps/default.json", encoding="utf-8"))
placed = collections.Counter(o["defId"] for o in mp.get("placedObjects", []))
by_id = {d.get("id",""): d for d in defs}

# Validate
missing = [i for i in TARGET_IDS if i not in by_id]
placed_targets = [i for i in TARGET_IDS if placed.get(i, 0) > 0]
if missing:
    print("ERROR: not found in env_defs:", missing); sys.exit(1)
if placed_targets:
    print("ERROR: these are PLACED on the map (refusing):", placed_targets); sys.exit(1)

target_set = set(TARGET_IDS)
defunct = [by_id[i] for i in TARGET_IDS]

# KEEP set = everything except the targets
keep = set()
for d in defs:
    if d.get("id","") in target_set: continue
    for p in png_fields(d): keep.add(norm(p))
for g in mp.get("groundTypes", []):
    if g.get("texturePath"): keep.add(norm(g["texturePath"]))
for g in mp.get("grassTypes", []):
    for p in g.get("spritePaths", []) or []: keep.add(norm(p))
for p in pngs_in_text("data/wall_defs.json"): keep.add(norm(p))
for f in glob.glob("data/maps/*.json"):
    if os.path.basename(f).lower() in ("env_defs.json","default.json"): continue
    for p in pngs_in_text(f): keep.add(norm(p))
for f in glob.glob("Necroking/**/*.cs", recursive=True):
    for p in pngs_in_text(f): keep.add(norm(p))

move_files, kept_in_place = [], []
for d in defunct:
    for p in png_fields(d):
        if not os.path.exists(p): continue
        (kept_in_place if norm(p) in keep else move_files).append(p)
move_files = sorted(set(move_files)); kept_in_place = sorted(set(kept_in_place))

print(f"{'EXECUTE' if EXECUTE else 'DRY-RUN'}: defunct {len(defunct)} tree defs")
print(f"image files to MOVE: {len(move_files)}")
for p in move_files: print("   move:", p)
print(f"kept-in-place (still referenced): {len(kept_in_place)}")
for p in kept_in_place: print("   keep:", p)

if not EXECUTE:
    print("\n(dry-run; pass --execute)"); sys.exit(0)

for p in move_files:
    rel = p[len("assets/"):] if p.lower().startswith("assets/") else p
    dst = os.path.join(DEFUNCT_DIR, rel)
    os.makedirs(os.path.dirname(dst), exist_ok=True); shutil.move(p, dst)

bpath = os.path.join(DEFUNCT_DIR, "defunct_env_defs.json")
existing = json.load(open(bpath, encoding="utf-8")) if os.path.exists(bpath) else []
have = set(e.get("id","") for e in existing)
existing.extend(d for d in defunct if d.get("id","") not in have)
json.dump(existing, open(bpath,"w",encoding="utf-8"), indent=2, ensure_ascii=False)

kept = [d for d in defs if d.get("id","") not in target_set]
json.dump(kept, open("data/env_defs.json","w",encoding="utf-8"), indent=2, ensure_ascii=False)
print(f"\nMOVED {len(move_files)} files; backed up {len(defunct)} defs; env_defs {len(defs)} -> {len(kept)}.")
