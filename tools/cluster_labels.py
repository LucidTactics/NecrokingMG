"""Cluster labeled methods into duplicate-candidate groups.

Reads catalog.json + labels/*.labels.json from the extract/labels dirs,
joins them, and writes:
  clusters/joined.json     - full join (id -> catalog + label)
  clusters/clusters.json   - candidate clusters (verb,target) with members
  clusters/summary.txt     - human-readable overview for curation

Usage: python tools/cluster_labels.py <scratchpadDir>
"""
import json
import os
import sys
import glob
from collections import defaultdict

scratch = sys.argv[1]
extract_dir = os.path.join(scratch, "extract")
labels_dir = os.path.join(scratch, "labels")
out_dir = os.path.join(scratch, "clusters")
os.makedirs(out_dir, exist_ok=True)

catalog = {m["Id"]: m for m in json.load(open(os.path.join(extract_dir, "catalog.json"), encoding="utf-8"))}

labels = {}
bad_files = []
for f in sorted(glob.glob(os.path.join(labels_dir, "*.labels.json"))):
    try:
        arr = json.load(open(f, encoding="utf-8"))
    except Exception as e:
        bad_files.append((os.path.basename(f), str(e)))
        continue
    for lab in arr:
        if isinstance(lab, dict) and "id" in lab:
            labels[lab["id"]] = lab

def domain_of(path):
    parts = path.split("/")
    if len(parts) >= 3 and parts[0] == "Necroking":
        if parts[1].endswith(".cs"):
            return "game1-glue" if parts[1].startswith("Game1") else "root"
        if len(parts) >= 4 and parts[1] == "Game" and parts[2] == "Jobs":
            return "Game/Jobs"
        return parts[1]
    return "?"

joined = []
unlabeled = []
for mid, m in catalog.items():
    if m["File"].startswith("Necroking/Scenario/Scenarios/"):
        continue  # auto-labeled boilerplate, excluded from clustering
    lab = labels.get(mid)
    if not lab:
        unlabeled.append(mid)
        continue
    joined.append({
        "id": mid, "file": m["File"], "type": m["Type"], "name": m["Name"],
        "sig": m["Sig"][:160], "line": m["StartLine"], "lines": m["Lines"],
        "domain": domain_of(m["File"]),
        "verb": (lab.get("verb") or "?").strip().lower(),
        "target": (lab.get("target") or "?").strip().lower(),
        "mechanism": (lab.get("mechanism") or "").strip().lower(),
        "summary": (lab.get("summary") or "").strip(),
        "dup_hint": (lab.get("dup_hint") or "").strip(),
    })

json.dump(joined, open(os.path.join(out_dir, "joined.json"), "w", encoding="utf-8"))

# ---- clusters ----
def build_clusters(keyfn, min_members=2, min_files=2):
    groups = defaultdict(list)
    for j in joined:
        groups[keyfn(j)].append(j)
    out = []
    for k, members in groups.items():
        files = {m["file"] for m in members}
        if len(members) >= min_members and len(files) >= min_files:
            out.append({"key": k, "n": len(members), "files": len(files), "members": members})
    out.sort(key=lambda c: (-c["n"] * min(c["files"], 8),))
    return out

vt_clusters = build_clusters(lambda j: f'{j["verb"]}|{j["target"]}')
json.dump(vt_clusters, open(os.path.join(out_dir, "clusters.json"), "w", encoding="utf-8"))

# ---- summary ----
lines = []
lines.append(f"joined={len(joined)} unlabeled={len(unlabeled)} bad_label_files={bad_files}")
if unlabeled:
    lines.append("unlabeled ids sample: " + ", ".join(str(u) for u in unlabeled[:40]))

lines.append("\n=== verb histogram ===")
vh = defaultdict(int)
for j in joined:
    vh[j["verb"]] += 1
for v, n in sorted(vh.items(), key=lambda kv: -kv[1]):
    lines.append(f"{n:5d}  {v}")

lines.append("\n=== verb x domain matrix (counts >= 2) ===")
vd = defaultdict(lambda: defaultdict(int))
for j in joined:
    vd[j["verb"]][j["domain"]] += 1
for v in sorted(vd, key=lambda v: -vh[v]):
    row = ", ".join(f"{d}:{n}" for d, n in sorted(vd[v].items(), key=lambda kv: -kv[1]) if n >= 2)
    if row:
        lines.append(f"{v:18s} {row}")

lines.append("\n=== (verb,target) clusters spanning >=2 files, by score ===")
for c in vt_clusters[:120]:
    lines.append(f"\n--- {c['key']}  n={c['n']} files={c['files']}")
    for m in sorted(c["members"], key=lambda m: m["file"])[:40]:
        lines.append(f"    {m['file']}:{m['line']} {m['type']}.{m['name']} ({m['lines']}L) [{m['mechanism']}] {m['summary'][:80]}")
    if c["n"] > 40:
        lines.append(f"    ... +{c['n']-40} more")

lines.append("\n=== dup hints from labelers ===")
for j in joined:
    if j["dup_hint"]:
        lines.append(f"  {j['file']}:{j['line']} {j['name']}: {j['dup_hint'][:140]}")

open(os.path.join(out_dir, "summary.txt"), "w", encoding="utf-8").write("\n".join(lines))
print(f"joined={len(joined)} unlabeled={len(unlabeled)} clusters={len(vt_clusters)} bad={bad_files}")
print("wrote", out_dir)
