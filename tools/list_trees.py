"""List current tree env_defs with placement status (read-only)."""
import json, collections

cur = json.load(open("data/env_defs.json", encoding="utf-8"))
mp = json.load(open("data/maps/default.json", encoding="utf-8"))
placed = collections.Counter(o["defId"] for o in mp.get("placedObjects", []))

trees = [d for d in cur if (d.get("category") == "Tree")
         or "/Trees/" in (d.get("texturePath") or "")]

print(f"{'id':24} {'name':22} {'placed':6} {'cat':10} texture")
for d in sorted(trees, key=lambda x: x.get("name","")):
    tex = (d.get("texturePath") or "").replace("assets/Environment/", "")
    print(f"{d.get('id',''):24} {d.get('name',''):22} {placed.get(d.get('id',''),0):<6} {d.get('category',''):10} {tex}")
print(f"\nTOTAL tree defs: {len(trees)}   placed (any): {sum(1 for d in trees if placed.get(d.get('id','')))}")
