"""Tag the Oak1..Oak10 env defs in data/env_defs.json with isCorruptable + corruptedSprite.

Targeted regex insert: only the 10 Oak blocks are modified, every other entry
in the file is left byte-for-byte identical.
"""
import re
from pathlib import Path


# Map Oak id -> dead sprite file (paired with its spritesheet).
DEAD_SPRITES = {
    "Oak1":  "assets/Environment/Trees/OakExports/OakSprite1_Dead.png",
    "Oak2":  "assets/Environment/Trees/OakExports/OakSprite2_Dead.png",
    "Oak3":  "assets/Environment/Trees/OakExports/OakSprite3_Dead.png",
    "Oak4":  "assets/Environment/Trees/OakExports/OakSprite4_Dead.png",
    "Oak5":  "assets/Environment/Trees/OakExports/OakSprite5_Dead.png",
    "Oak6":  "assets/Environment/Trees/OakExports/OakSprite6_Dead.png",
    "Oak7":  "assets/Environment/Trees/OakExports/OakSprite7_Dead.png",
    "Oak8":  "assets/Environment/Trees/OakExports/OakSprite8_Dead.png",
    "Oak9":  "assets/Environment/Trees/OakExports/OakSprite9_Dead.png",
    "Oak10": "assets/Environment/Trees/OakExports/OakSprite10_Dead1.png",
}


def main():
    repo_root = Path(__file__).resolve().parent.parent
    path = repo_root / "data" / "env_defs.json"
    text = path.read_text(encoding="utf-8")
    original = text

    for oid, dead in DEAD_SPRITES.items():
        # Anchor: the unique "id": "<oid>" line. Then within the same object
        # block, find "fogAbsorbRate": <num>, and insert the two new keys
        # right after it. We bound the search to the next "}," (end of block).
        # The Oak blocks contain nested objects (input1, output, tintColor) so
        # we can't bound on [^\{\}]. Use lazy any-char from the unique id anchor
        # to the first "fogAbsorbRate" line in that object.
        block_re = re.compile(
            r'(\{\s*"id":\s*"' + re.escape(oid) + r'"[\s\S]*?'
            r'"fogAbsorbRate":\s*[-\d.]+,)',
        )
        m = block_re.search(text)
        if not m:
            print(f"WARN: could not find {oid} block — skipping")
            continue

        # Skip if already tagged (idempotent).
        # Look ahead to end of object block; if isCorruptable is already in
        # the block, leave it alone.
        block_end = text.find("}", m.end())
        if 'isCorruptable' in text[m.start():block_end]:
            print(f"  {oid}: already tagged, leaving as-is")
            continue

        insert = (
            f'\n    "isCorruptable": true,'
            f'\n    "corruptedSprite": "{dead}",'
        )
        text = text[:m.end()] + insert + text[m.end():]
        print(f"  {oid}: inserted -> {dead}")

    if text == original:
        print("no changes")
        return

    path.write_text(text, encoding="utf-8")
    print(f"wrote {path}")


if __name__ == "__main__":
    main()
