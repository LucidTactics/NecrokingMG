#!/usr/bin/env python3
"""Generate a small playable test map: assets/maps/testmap.json (+ triggers/roads).

Layout (160x160 world):
  - Grass everywhere, a dirt cross-path, a cobblestone plaza in the center.
  - Grass visual layer over the whole map.
  - Player necromancer at center on the plaza.
  - A small enemy village to the east (cottages + soldiers/militia) to fight.
  - Trees ringing the map, berry bushes + death essence to forage.

Ground type indices match the `groundTypes` array written below:
  0 = grass, 1 = dirt, 2 = cobblestone.
Grass map values are 1-based (0 = empty, 1 = grass type 0).

Run from anywhere:  python tools/gen_test_map.py
"""
import base64
import json
import math
import os

WORLD = 160                      # world size in tiles (square)
VW = VH = WORLD + 1              # vertex map dimensions
GRASS_CELL = 0.8
GW = math.ceil(WORLD / GRASS_CELL)
GH = math.ceil(WORLD / GRASS_CELL)
CENTER = WORLD / 2.0

ROOT = os.path.normpath(os.path.join(os.path.dirname(__file__), ".."))
MAPS_DIR = os.path.join(ROOT, "assets", "maps")

GROUND_TYPES = [
    {"id": "grass", "name": "Grass",
     "texturePath": "assets/Environment/Ground/GroundGrass1.png",
     "corruptedTypeId": "grass_corrupted"},
    {"id": "dirt", "name": "Dirt",
     "texturePath": "assets/Environment/Ground/GroundDirt1.png",
     "corruptedTypeId": "dirt_corrupted"},
    {"id": "cobblestone", "name": "Cobblestone",
     "texturePath": "assets/Environment/Ground/GroundCobblestone1.png"},
    {"id": "grass_corrupted", "name": "Grass (Corrupted)",
     "texturePath": "assets/Environment/Ground/GroundGrass1_Corrupted.png"},
    {"id": "dirt_corrupted", "name": "Dirt (Corrupted)",
     "texturePath": "assets/Environment/Ground/GroundDirt1_Corrupted.png"},
]


def build_vertex_map():
    """1 byte per vertex; ground type index."""
    vm = bytearray(VW * VH)  # all grass (0)
    for y in range(VH):
        for x in range(VW):
            i = y * VW + x
            # Dirt cross-path: a vertical and horizontal band through center.
            if abs(x - CENTER) <= 3 or abs(y - CENTER) <= 3:
                vm[i] = 1  # dirt
            # Cobblestone plaza in the very center (overrides dirt).
            if abs(x - CENTER) <= 8 and abs(y - CENTER) <= 8:
                vm[i] = 2  # cobblestone
    return bytes(vm)


def build_grass_map():
    """1 byte per cell; 1-based grass type (1 = grass type 0). Skip plaza/paths."""
    gm = bytearray(GW * GH)
    for gy in range(GH):
        for gx in range(GW):
            wx = gx * GRASS_CELL
            wy = gy * GRASS_CELL
            # Leave the cobblestone plaza and dirt paths bare.
            on_path = abs(wx - CENTER) <= 3.0 or abs(wy - CENTER) <= 3.0
            on_plaza = abs(wx - CENTER) <= 8.0 and abs(wy - CENTER) <= 8.0
            if on_path or on_plaza:
                continue
            gm[gy * GW + gx] = 1  # grass type 0
    return bytes(gm)


def placed(def_id, x, y, scale=1.0, seed=-1.0):
    return {"defId": def_id, "x": float(x), "y": float(y),
            "scale": float(scale), "seed": float(seed)}


def unit(unit_id, x, y, faction="", patrol=""):
    return {"unitDefId": unit_id, "x": float(x), "y": float(y),
            "faction": faction, "patrolRouteId": patrol}


def build_objects():
    objs = []
    # Tree border ring (a few per side) to frame the play area.
    tree_ids = ["green_oak", "red_oak", "dark_spruce", "broad_elm", "mossy_oak"]
    for t in range(8, WORLD, 18):
        objs.append(placed(tree_ids[t % len(tree_ids)], t, 8))
        objs.append(placed(tree_ids[(t + 1) % len(tree_ids)], t, WORLD - 8))
        objs.append(placed(tree_ids[(t + 2) % len(tree_ids)], 8, t))
        objs.append(placed(tree_ids[(t + 3) % len(tree_ids)], WORLD - 8, t))

    # Forageables scattered on the west side (near where the player starts).
    for i in range(6):
        objs.append(placed("berry_bush", CENTER - 40 + i * 4, CENTER - 20 + (i % 3) * 6))
    for i in range(4):
        objs.append(placed("death_essence", CENTER - 30 + i * 5, CENTER + 18))

    # Enemy village to the east: a cluster of cottages.
    vx = CENTER + 45
    objs.append(placed("Cottage1", vx, CENTER - 8))
    objs.append(placed("Cottage2", vx + 10, CENTER + 4))
    objs.append(placed("Cottage3", vx - 6, CENTER + 10))
    objs.append(placed("house1", vx + 6, CENTER - 16))
    return objs


def build_units():
    units = []
    # Player necromancer on the central plaza.
    units.append(unit("necromancer", CENTER, CENTER))
    # A starting skeleton or two beside the player.
    units.append(unit("skeleton", CENTER - 3, CENTER + 2))
    units.append(unit("skeleton", CENTER + 3, CENTER + 2))

    # Enemy garrison defending the eastern village.
    vx = CENTER + 45
    for i in range(4):
        units.append(unit("soldier", vx - 4 + i * 3, CENTER))
    for i in range(3):
        units.append(unit("militia", vx + 2 + i * 3, CENTER + 8))
    units.append(unit("knight", vx + 4, CENTER - 4))
    units.append(unit("archer", vx - 2, CENTER - 10))
    return units


def main():
    os.makedirs(MAPS_DIR, exist_ok=True)

    map_obj = {
        "groundTypes": GROUND_TYPES,
        "groundMap": {
            "width": VW,
            "height": VH,
            "tilesBase64": base64.b64encode(build_vertex_map()).decode("ascii"),
        },
        "grassTypes": [
            {
                "id": "grass_0",
                "name": "Green Grass",
                "defaultTint": {"r": 255, "g": 255, "b": 255, "a": 255},
                "corruptedTint": {"r": 80, "g": 60, "b": 70, "a": 255},
                "spritePaths": [
                    "assets/Environment/Grass/GrnGrassTuftNO1.png",
                    "assets/Environment/Grass/GrnGrassTuftNO2.png",
                    "assets/Environment/Grass/GrnGrassTuftNO3.png",
                    "assets/Environment/Grass/GrnGrassTuftNO4.png",
                ],
                "scale": 1.0,
                "density": 1.0,
            }
        ],
        "grassMap": {
            "width": GW,
            "height": GH,
            "cellsBase64": base64.b64encode(build_grass_map()).decode("ascii"),
        },
        "placedObjects": build_objects(),
        "walls": [],
        "placedUnits": build_units(),
    }

    out = os.path.join(MAPS_DIR, "testmap.json")
    with open(out, "w", encoding="utf-8") as f:
        json.dump(map_obj, f, indent=2)
    print(f"Wrote {out} ({os.path.getsize(out)} bytes)")

    # Empty companion files so trigger/road loaders find something valid.
    with open(os.path.join(MAPS_DIR, "testmap_triggers.json"), "w", encoding="utf-8") as f:
        json.dump({"regions": [], "patrolRoutes": [], "triggers": [], "instances": []}, f, indent=2)
    with open(os.path.join(MAPS_DIR, "testmap_roads.json"), "w", encoding="utf-8") as f:
        json.dump({"roads": [], "junctions": []}, f, indent=2)
    print("Wrote testmap_triggers.json and testmap_roads.json")


if __name__ == "__main__":
    main()
