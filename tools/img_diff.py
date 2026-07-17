#!/usr/bin/env python3
"""Compare two screenshots (optionally a crop) and report pixel differences.

Usage:
    python tools/img_diff.py a.png b.png [x y w h]

Prints size mismatch, differing-pixel count/percent, and max per-channel delta
for the full image or the given crop rect. Exit 0 always (a reporting tool,
not a gate). Requires Pillow; falls back to a byte-compare if unavailable.
"""

import sys


def main() -> int:
    if len(sys.argv) not in (3, 7):
        print(__doc__)
        return 2
    pa, pb = sys.argv[1], sys.argv[2]
    try:
        from PIL import Image
    except ImportError:
        with open(pa, "rb") as f:
            da = f.read()
        with open(pb, "rb") as f:
            db = f.read()
        print(f"Pillow not installed - byte compare only: "
              f"{'IDENTICAL' if da == db else 'DIFFER'} "
              f"({len(da)} vs {len(db)} bytes)")
        return 0

    a = Image.open(pa).convert("RGBA")
    b = Image.open(pb).convert("RGBA")
    if a.size != b.size:
        print(f"size mismatch: {a.size} vs {b.size}")
        return 0
    if len(sys.argv) == 7:
        x, y, w, h = map(int, sys.argv[3:7])
        box = (x, y, x + w, y + h)
        a = a.crop(box)
        b = b.crop(box)
        print(f"crop {box}:")
    da, db = a.tobytes(), b.tobytes()
    if da == db:
        print(f"IDENTICAL ({a.size[0]}x{a.size[1]})")
        return 0
    n = len(da) // 4
    diff_px = 0
    max_d = 0
    for i in range(0, len(da), 4):
        if da[i:i + 4] != db[i:i + 4]:
            diff_px += 1
            for c in range(4):
                d = abs(da[i + c] - db[i + c])
                if d > max_d:
                    max_d = d
    print(f"{diff_px}/{n} pixels differ ({100.0 * diff_px / n:.2f}%), "
          f"max channel delta {max_d}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
