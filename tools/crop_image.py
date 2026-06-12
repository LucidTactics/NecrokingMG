"""Crop a region from an image and optionally scale it up (nearest) for
pixel-level inspection of UI screenshots.

Usage: python tools/crop_image.py <in.png> <x> <y> <w> <h> <scale> <out.png>
"""
import sys
from PIL import Image

def main():
    if len(sys.argv) != 8:
        print(__doc__)
        sys.exit(1)
    path, x, y, w, h, scale, out = sys.argv[1:]
    x, y, w, h, scale = int(x), int(y), int(w), int(h), int(scale)
    img = Image.open(path)
    crop = img.crop((x, y, x + w, y + h))
    if scale != 1:
        crop = crop.resize((w * scale, h * scale), Image.NEAREST)
    crop.save(out)
    print(f"saved {out} ({crop.width}x{crop.height})")

if __name__ == "__main__":
    main()
