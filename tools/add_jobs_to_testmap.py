"""Insert the worker-job buildings (+ a few sources) into assets/maps/testmap.json,
clustered around the necromancer spawn at (80,80). Targeted text insertion right
after the "placedObjects": [ marker so existing entries keep their exact formatting.
Idempotent-ish guard: refuses to run twice (checks for a sentinel objectId comment
via the first building's coords).
"""
import random

PATH = "assets/maps/testmap.json"

# (defId, x, y) — buildings first, then sources so collect jobs have something to do.
OBJECTS = [
    # worker homes
    ("empty_grave", 74.0, 74.0),
    ("empty_grave", 76.5, 74.0),
    ("empty_grave", 79.0, 74.0),
    # storage + production buildings (one of each)
    ("mushroom_pile", 86.0, 73.0),
    ("corpse_pile", 86.0, 77.0),
    ("poison_vat", 86.0, 81.0),
    ("harvesting_table", 90.0, 74.0),
    ("necro_table", 90.0, 78.0),
    ("alchemist_table", 94.0, 76.0),
    # foragable sources for Forage Mushrooms
    ("deathcap", 96.0, 71.0),
    ("deathcap", 98.0, 71.0),
    ("deathcap", 100.0, 71.0),
    ("deathcap", 96.0, 73.5),
    ("deathcap", 98.0, 73.5),
    ("deathcap", 100.0, 73.5),
    # berry bushes for Poison Berries
    ("BerryBush1Ber", 73.0, 84.0),
    ("BerryBush1Ber", 76.0, 84.0),
]


def main():
    with open(PATH, "r", encoding="utf-8") as f:
        text = f.read()

    if '"defId": "mushroom_pile"' in text:
        print("Already inserted (found mushroom_pile) — skipping.")
        return

    nl = "\r\n" if "\r\n" in text else "\n"
    random.seed(7)
    rows = []
    for def_id, x, y in OBJECTS:
        seed = round(random.random(), 6)
        rows.append(
            "    {" + nl +
            f'      "defId": "{def_id}",' + nl +
            f'      "x": {x},' + nl +
            f'      "y": {y},' + nl +
            '      "scale": 1,' + nl +
            f'      "seed": {seed}' + nl +
            "    },")
    block = nl.join(rows) + nl

    marker = '"placedObjects": [' + nl
    if marker not in text:
        marker = '"placedObjects": [\n'
        if marker not in text:
            raise SystemExit("Could not find placedObjects array marker.")
    idx = text.index(marker) + len(marker)
    text = text[:idx] + block + text[idx:]

    with open(PATH, "w", encoding="utf-8", newline="") as f:
        f.write(text)
    print(f"Inserted {len(OBJECTS)} objects into {PATH}")


if __name__ == "__main__":
    main()
