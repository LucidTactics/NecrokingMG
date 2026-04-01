"""Add Spike Trap and Poison Spike Trap env defs to the env_defs JSON files."""
import json

TRAPS = [
    {
        "id": "spike_trap",
        "name": "Spike Trap",
        "category": "Traps",
        "texturePath": "assets/Environment/Misc/LogBridge2.png",
        "heightMapPath": "",
        "spriteWorldHeight": 1.0,
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
        "isBuilding": True,
        "playerBuildable": True,
        "buildingMaxHP": 50,
        "buildingProtection": 0,
        "buildingDefaultOwner": 0,
        "costWood": 0, "costStone": 0, "costGold": 0,
        "cost1ItemId": "Log",
        "cost1Amount": 2,
        "cost2ItemId": "",
        "cost2Amount": 0,
        "placementRadius": 2.5,
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
        "isForagable": False,
        "foragableType": "",
        "respawnTime": 180,
        "scaleMin": 0.8,
        "scaleMax": 1.2,
        "tintColor": {"r": 255, "g": 255, "b": 255, "a": 255, "intensity": 1}
    },
    {
        "id": "poison_spike_trap",
        "name": "Poison Spike Trap",
        "category": "Traps",
        "texturePath": "assets/Environment/Misc/LogBridge2.png",
        "heightMapPath": "",
        "spriteWorldHeight": 1.0,
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
        "isBuilding": True,
        "playerBuildable": True,
        "buildingMaxHP": 50,
        "buildingProtection": 0,
        "buildingDefaultOwner": 0,
        "costWood": 0, "costStone": 0, "costGold": 0,
        "cost1ItemId": "Log",
        "cost1Amount": 2,
        "cost2ItemId": "PoisonMushroom",
        "cost2Amount": 1,
        "placementRadius": 2.5,
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
        "isForagable": False,
        "foragableType": "",
        "respawnTime": 180,
        "scaleMin": 0.8,
        "scaleMax": 1.2,
        "tintColor": {"r": 255, "g": 255, "b": 255, "a": 255, "intensity": 1}
    }
]

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

    if isinstance(data, list):
        defs = data
    elif isinstance(data, dict) and "envDefs" in data:
        defs = data["envDefs"]
    else:
        print(f"  {path}: unknown format, skipping")
        continue

    existing_ids = {d.get("id") for d in defs}
    added = 0
    for trap in TRAPS:
        if trap["id"] not in existing_ids:
            defs.append(trap)
            added += 1

    with open(path, 'w') as f:
        json.dump(data, f, indent=2)
    print(f"  {path}: added {added} trap defs")

print("Done")
