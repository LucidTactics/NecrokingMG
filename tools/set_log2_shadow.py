"""One-shot: flip the foragable `log2` env def to shadowType=1 (ellipse).
Trees & standing objects use sprite-projection (0); a log lying flat needs
the wide-flat ellipse instead so its shadow reads as grounded."""
import json
from pathlib import Path

p = Path(__file__).resolve().parent.parent / "data" / "env_defs.json"
data = json.loads(p.read_text(encoding="utf-8"))
hits = 0
for d in data:
    if d.get("id") == "log2":
        d["shadowType"] = 1
        hits += 1
assert hits == 1, f"expected 1 log2 def, found {hits}"
p.write_text(json.dumps(data, indent=2), encoding="utf-8")
print(f"Updated log2.shadowType = 1")
