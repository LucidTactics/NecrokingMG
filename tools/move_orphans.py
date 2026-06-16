"""
Move OLD orphaned object images (no longer referenced by any env_def / ground /
grass / wall / code) to "defunct assets/", mirroring the assets structure.
Only moves orphans that are committed/old (have a git first-add date); recent
uncommitted files (active WIP: swamp trees, mangroves, May logs, bushes) are left
in place. Also backs up any matching def entries recoverable from git HEAD.

Dry-run by default; pass --execute.
"""
import json, os, glob, re, subprocess, collections, shutil, sys

DEFUNCT_DIR = "defunct assets"
EXECUTE = "--execute" in sys.argv

def pngs_in_text(path):
    try: txt = open(path, encoding="utf-8", errors="ignore").read()
    except OSError: return []
    return re.findall(r"assets/[A-Za-z0-9_/\.\- ]+?\.png", txt, re.IGNORECASE)
def norm(p): return p.replace("\\", "/").lower()
_gc = {}
def git_add(path):
    if path in _gc: return _gc[path]
    out = subprocess.run(["git","log","--diff-filter=A","--follow","--format=%aI","-1","--",path],
                         capture_output=True, text=True, timeout=20).stdout.strip()
    _gc[path] = out[:10] if out else None
    return _gc[path]

cur = json.load(open("data/env_defs.json", encoding="utf-8"))
mp = json.load(open("data/maps/default.json", encoding="utf-8"))
ref = set()
for d in cur:
    for v in d.values():
        if isinstance(v, str) and v.lower().endswith(".png"): ref.add(norm(v))
for g in mp.get("groundTypes", []):
    if g.get("texturePath"): ref.add(norm(g["texturePath"]))
for g in mp.get("grassTypes", []):
    for p in g.get("spritePaths", []) or []: ref.add(norm(p))
for p in pngs_in_text("data/wall_defs.json"): ref.add(norm(p))
for f in glob.glob("data/maps/*.json"):
    if os.path.basename(f).lower() in ("env_defs.json", "default.json"): continue
    for p in pngs_in_text(f): ref.add(norm(p))
for f in glob.glob("Necroking/**/*.cs", recursive=True):
    for p in pngs_in_text(f): ref.add(norm(p))

disk = [p.replace("\\", "/") for p in glob.glob("assets/Environment/**/*.png", recursive=True)]
disk = [p for p in disk if "/Buildings/" not in p]
orphans = [p for p in disk if norm(p) not in ref]
# OLD = committed (has a git first-add date). Uncommitted (None) = recent WIP -> skip.
move = sorted(p for p in orphans if git_add(p) is not None)
skip = sorted(p for p in orphans if git_add(p) is None)

print(f"{'EXECUTE' if EXECUTE else 'DRY-RUN'}: orphans={len(orphans)}  MOVE(old)={len(move)}  SKIP(uncommitted WIP)={len(skip)}")
for p in move: print("  move:", p)

# Recover any defs from git HEAD that referenced these files (best-effort backup)
movenorm = set(norm(p) for p in move)
recovered = []
try:
    head = json.loads(subprocess.run(["git","show","HEAD:data/env_defs.json"],
                                     capture_output=True, text=True).stdout)
    for d in head:
        if any(isinstance(v,str) and norm(v) in movenorm for v in d.values()):
            recovered.append(d)
except Exception as e:
    print("  (HEAD def recovery skipped:", e, ")")
print(f"recoverable def entries from HEAD: {len(recovered)}")

if not EXECUTE:
    print("\n(dry-run; pass --execute)")
    sys.exit(0)

moved = 0
for p in move:
    rel = p[len("assets/"):] if p.lower().startswith("assets/") else p
    dst = os.path.join(DEFUNCT_DIR, rel)
    os.makedirs(os.path.dirname(dst), exist_ok=True)
    shutil.move(p, dst); moved += 1

if recovered:
    bpath = os.path.join(DEFUNCT_DIR, "defunct_env_defs.json")
    existing = json.load(open(bpath, encoding="utf-8")) if os.path.exists(bpath) else []
    have = set(e.get("id","") for e in existing)
    existing.extend(d for d in recovered if d.get("id","") not in have)
    json.dump(existing, open(bpath,"w",encoding="utf-8"), indent=2, ensure_ascii=False)
    print(f"appended {len(recovered)} recovered defs to {bpath}")
print(f"MOVED {moved} files to '{DEFUNCT_DIR}/'.")
