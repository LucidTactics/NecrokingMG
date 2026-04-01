"""Add a Log foragable env def to the env_defs JSON files."""
import json, sys

LOG_DEF = {
    "id": "log2",
    "name": "Log",
    "category": "Foragable",
    "texturePath": "assets/Environment/Misc/Log2.png",
    "heightMapPath": "",
    "spriteWorldHeight": 0.5,
    "worldHeight": 0,
    "pivotX": 0.5,
    "pivotY": 1,
    "collisionRadius": 0,
    "collisionOffsetX": 0,
    "collisionOffsetY": 0,
    "scale": 0.5,
    "placementScale": 1,
    "group": "",
    "groupWeight": 1,
    "isBuilding": False,
    "playerBuildable": False,
    "buildingMaxHP": 100,
    "buildingProtection": 0,
    "buildingDefaultOwner": 1,
    "costWood": 0,
    "costStone": 0,
    "costGold": 0,
    "boundTriggerID": "",
    "input1": {"kind": "", "resourceID": ""},
    "input2": {"kind": "", "resourceID": ""},
    "output": {"kind": "", "resourceID": ""},
    "processTime": 10,
    "maxInputQueue": 10,
    "maxOutputQueue": 10,
    "autoSpawn": False,
    "spawnOffsetX": 0,
    "spawnOffsetY": 1.5,
    "isForagable": True,
    "foragableType": "Log",
    "respawnTime": 240,
    "scaleMin": 0.8,
    "scaleMax": 1.2,
    "tintColor": {"r": 255, "g": 255, "b": 255, "a": 255, "intensity": 1}
}

files = [
    "e:/Nightfall/NecrokingMG/Necroking/bin/Publish/maps/env_defs.json",
    "e:/Nightfall/NecrokingMG/Necroking/bin/Publish/assets/maps/env_defs.json",
    "e:/Nightfall/NecrokingMG/Necroking/bin/Publish/assets/maps/default.json",
]

for path in files:
    try:
        with open(path, 'r') as f:
            data = json.load(f)
    except Exception as e:
        print(f"Skip {path}: {e}")
        continue

    # Flat array or dict with envDefs key
    if isinstance(data, list):
        if any(d.get("id") == "log2" for d in data):
            print(f"  {path}: already has log2, skipping")
            continue
        data.append(LOG_DEF)
    elif isinstance(data, dict) and "envDefs" in data:
        defs = data["envDefs"]
        if any(d.get("id") == "log2" for d in defs):
            print(f"  {path}: already has log2, skipping")
            continue
        defs.append(LOG_DEF)
    else:
        print(f"  {path}: unknown format, skipping")
        continue

    with open(path, 'w') as f:
        json.dump(data, f, indent=2)
    print(f"  {path}: added log2 def")

print("Done")
