"""Migrate env_defs to canonical location: data/env_defs.json (flat array format).
Reads from assets/maps/env_defs.json (has traps + all defs) and writes flat array."""
import json

# Source: the most complete version (has traps, mushroom types, etc.)
SRC = "e:/Nightfall/NecrokingMG/Necroking/bin/Publish/assets/maps/env_defs.json"
# Destinations
DESTINATIONS = [
    "e:/Nightfall/NecrokingMG/Necroking/bin/Publish/data/env_defs.json",
    "e:/Nightfall/NecrokingMG/data/env_defs.json",
    "e:/Nightfall/NecrokingMG/Necroking/data/env_defs.json",
]

with open(SRC, 'r') as f:
    data = json.load(f)

# Extract defs array (handle both formats)
if isinstance(data, list):
    defs = data
elif isinstance(data, dict) and "envDefs" in data:
    defs = data["envDefs"]
else:
    print(f"Unknown format in {SRC}")
    exit(1)

print(f"Source: {len(defs)} defs from {SRC}")

# Write flat array to all destinations
for dest in DESTINATIONS:
    import os
    os.makedirs(os.path.dirname(dest), exist_ok=True)
    with open(dest, 'w') as f:
        json.dump(defs, f, indent=2)
    print(f"  Wrote {len(defs)} defs to {dest}")

print("Done")
