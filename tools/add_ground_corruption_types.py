"""Rewrite the groundTypes block at the top of data/maps/default.json to:
  - tag grass and dirt with corruptedTypeId pointing at their _corrupted variants
  - add two new ground types: grass_corrupted and dirt_corrupted

Targeted text patch on the file's prefix only. The 21MB groundMap base64 below
is left byte-for-byte untouched.
"""
import re
from pathlib import Path


NEW_GROUND_TYPES = """  "groundTypes": [
    {
      "id": "grass",
      "name": "Grass",
      "texturePath": "assets/Environment/Ground/GroundGrass1.png",
      "corruptedTypeId": "grass_corrupted"
    },
    {
      "id": "dirt",
      "name": "Dirt",
      "texturePath": "assets/Environment/Ground/GroundDirt1.png",
      "corruptedTypeId": "dirt_corrupted"
    },
    {
      "id": "cobblestone",
      "name": "Cobblestone",
      "texturePath": "assets/Environment/Ground/GroundCobblestone1.png"
    },
    {
      "id": "grass_corrupted",
      "name": "Grass (Corrupted)",
      "texturePath": "assets/Environment/Ground/GroundGrass1_Corrupted.png"
    },
    {
      "id": "dirt_corrupted",
      "name": "Dirt (Corrupted)",
      "texturePath": "assets/Environment/Ground/GroundDirt1_Corrupted.png"
    }
  ],"""


def main():
    repo = Path(__file__).resolve().parent.parent
    map_path = repo / "data" / "maps" / "default.json"

    # Read just the first 4KB to find and rewrite the groundTypes block.
    # Then stream the rest of the file unchanged.
    with map_path.open("rb") as f:
        head_bytes = f.read(8192)

    head = head_bytes.decode("utf-8")

    # Match from `  "groundTypes": [` through the closing `],` (followed by the
    # next key, expected to be "groundMap"). This block sits at the top of the
    # file so it's well within the head buffer.
    pattern = re.compile(
        r'  "groundTypes":\s*\[[\s\S]*?\],\s*\n(?=  "groundMap")',
    )
    m = pattern.search(head)
    if not m:
        raise SystemExit("ERROR: could not locate groundTypes block in head buffer")

    if 'corruptedTypeId' in m.group(0):
        # Already patched — check whether all expected entries are present.
        if all(name in m.group(0) for name in ("grass_corrupted", "dirt_corrupted")):
            print("default.json: already patched, no changes")
            return

    new_head = head[:m.start()] + NEW_GROUND_TYPES + "\n" + head[m.end():]
    new_head_bytes = new_head.encode("utf-8")

    # Stream-rewrite: write the new head, then copy the rest of the file
    # starting at position 8192 of the original.
    tmp_path = map_path.with_suffix(".json.tmp")
    with tmp_path.open("wb") as out, map_path.open("rb") as src:
        out.write(new_head_bytes)
        src.seek(8192)
        while True:
            chunk = src.read(1 << 20)
            if not chunk: break
            out.write(chunk)

    map_path.unlink()
    tmp_path.rename(map_path)
    delta = len(new_head_bytes) - 8192
    print(f"default.json: rewrote groundTypes header (head delta = {delta:+d} bytes)")


if __name__ == "__main__":
    main()
