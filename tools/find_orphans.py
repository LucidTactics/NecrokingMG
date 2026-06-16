"""
Find orphaned object images: PNGs under assets/Environment (excluding Buildings)
that are no longer referenced by any env_def, ground/grass type, wall def, map
sidecar, or code. These correspond to objects removed from the editor.

Also compares current env_defs.json vs git HEAD to name which defs were removed.
Read-only.
"""
import json, os, glob, re, subprocess, collections

def pngs_in_text(path):
    try: txt = open(path, encoding="utf-8", errors="ignore").read()
    except OSError: return []
    return re.findall(r"assets/[A-Za-z0-9_/\.\- ]+?\.png", txt, re.IGNORECASE)

def norm(p): return p.replace("\\", "/").lower()

cur = json.load(open("data/env_defs.json", encoding="utf-8"))
mp = json.load(open("data/maps/default.json", encoding="utf-8"))

ref = set()
# current env_defs
for d in cur:
    for v in d.values():
        if isinstance(v, str) and v.lower().endswith(".png"): ref.add(norm(v))
# ground + grass from the live map
for g in mp.get("groundTypes", []):
    if g.get("texturePath"): ref.add(norm(g["texturePath"]))
for g in mp.get("grassTypes", []):
    for p in g.get("spritePaths", []) or []: ref.add(norm(p))
# walls + map sidecars (skip legacy env_defs.json + huge default.json, handled above)
for p in pngs_in_text("data/wall_defs.json"): ref.add(norm(p))
for f in glob.glob("data/maps/*.json"):
    if os.path.basename(f).lower() in ("env_defs.json", "default.json"): continue
    for p in pngs_in_text(f): ref.add(norm(p))
# code
for f in glob.glob("Necroking/**/*.cs", recursive=True):
    for p in pngs_in_text(f): ref.add(norm(p))

# All Environment PNGs on disk, excluding Buildings (user exception) and the defunct tree
disk = [p.replace("\\", "/") for p in glob.glob("assets/Environment/**/*.png", recursive=True)]
disk = [p for p in disk if "/Buildings/" not in p]

orphans = sorted(p for p in disk if norm(p) not in ref)
print(f"Environment PNGs on disk (excl. Buildings): {len(disk)}   referenced: {sum(1 for p in disk if norm(p) in ref)}   ORPHANS: {len(orphans)}")
by_folder = collections.Counter("/".join(p.split("/")[:3]) for p in orphans)
print("\nOrphans per folder:")
for k, v in sorted(by_folder.items()): print(f"  {k:40} {v}")
import datetime
_gc = {}
def git_add(path):
    if path in _gc: return _gc[path]
    out = subprocess.run(["git","log","--diff-filter=A","--follow","--format=%aI","-1","--",path],
                         capture_output=True, text=True, timeout=20).stdout.strip()
    _gc[path] = out[:10] if out else None
    return _gc[path]
def mt(path):
    try: return datetime.date.fromtimestamp(os.path.getmtime(path)).isoformat()
    except OSError: return None
print("\nOrphan files (git-add / mtime):")
for p in orphans:
    print(f"  {str(git_add(p)):12} {str(mt(p)):12} {p}")

# Which env_defs were removed since the last commit (names them)
try:
    head_txt = subprocess.run(["git", "show", "HEAD:data/env_defs.json"],
                              capture_output=True, text=True).stdout
    head = json.loads(head_txt)
    head_ids = {d["id"]: d for d in head}
    cur_ids = {d["id"] for d in cur}
    removed = [head_ids[i] for i in head_ids if i not in cur_ids]
    # restrict to tree/ground-ish (texture folder) for clarity
    print(f"\nDefs in HEAD but not in current env_defs (count {len(removed)}):")
    rf = collections.Counter("/".join((d.get('texturePath') or '?').split('/')[:3]) for d in removed)
    for k, v in sorted(rf.items()): print(f"  {k:40} {v}")
except Exception as e:
    print("\n(could not compare to HEAD:", e, ")")
