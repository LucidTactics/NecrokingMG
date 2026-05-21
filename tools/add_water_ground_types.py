"""Append two water ground types (shallow_water, deep_water) to the
groundTypes block at the top of data/maps/default.json.

Targeted text patch on the file's prefix only — the multi-MB groundMap base64
that follows is streamed through untouched.

Idempotent: re-running after the entries exist is a no-op.
"""
import re
from pathlib import Path


WATER_ENTRIES = """    {
      "id": "shallow_water",
      "name": "Shallow Water",
      "texturePath": "assets/Environment/Ground/ShallowWater.png",
      "movementTerrain": "ShallowWater"
    },
    {
      "id": "deep_water",
      "name": "Deep Water",
      "texturePath": "assets/Environment/Ground/DeepWater.png",
      "movementTerrain": "DeepWater"
    }"""


def main():
    repo = Path(__file__).resolve().parent.parent
    map_path = repo / "data" / "maps" / "default.json"

    with map_path.open("rb") as f:
        head_bytes = f.read(8192)
    head = head_bytes.decode("utf-8")

    # Locate the entire groundTypes block (sits at the very top of the file).
    pattern = re.compile(
        r'(  "groundTypes":\s*\[)([\s\S]*?)(\n  \],\s*\n)(?=  "groundMap")',
    )
    m = pattern.search(head)
    if not m:
        raise SystemExit("ERROR: could not locate groundTypes block in head buffer")

    body = m.group(2)
    if '"id": "shallow_water"' in body and '"id": "deep_water"' in body:
        print("default.json: water types already present, no changes")
        return

    # Append the two water entries after the existing last entry. The existing
    # body always ends with a `\n    }` (last entry's closing brace) before the
    # array's closing `\n  ]`. Add a comma + newline + our entries.
    new_body = body.rstrip() + ",\n" + WATER_ENTRIES
    new_block = m.group(1) + new_body + m.group(3)
    new_head = head[:m.start()] + new_block + head[m.end():]
    new_head_bytes = new_head.encode("utf-8")

    # Stream-rewrite: new head then everything from byte 8192 of the original.
    tmp_path = map_path.with_suffix(".json.tmp")
    with tmp_path.open("wb") as out, map_path.open("rb") as src:
        out.write(new_head_bytes)
        src.seek(8192)
        while True:
            chunk = src.read(1 << 20)
            if not chunk:
                break
            out.write(chunk)

    map_path.unlink()
    tmp_path.rename(map_path)
    delta = len(new_head_bytes) - 8192
    print(f"default.json: appended shallow_water + deep_water (head delta = {delta:+d} bytes)")


if __name__ == "__main__":
    main()
