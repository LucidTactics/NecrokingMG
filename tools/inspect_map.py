"""Inspect data/maps/default.json structure without dumping it into context."""
import json

with open(r"data/maps/default.json", encoding="utf-8") as f:
    d = json.load(f)

print("TOP-LEVEL KEYS:")
for k, v in d.items():
    if isinstance(v, list):
        sample = list(v[0].keys()) if v and isinstance(v[0], dict) else (type(v[0]).__name__ if v else "empty")
        print(f"  {k}: list[{len(v)}]  record-keys={sample}")
    elif isinstance(v, dict):
        print(f"  {k}: dict  keys={list(v.keys())[:12]}")
    else:
        print(f"  {k}: {type(v).__name__} = {str(v)[:60]}")
