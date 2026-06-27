"""Install a chosen PixelLab variant into assets/: autocrop the transparent
border (so a pivotY=1 / bottom-anchored env def sits the object on the ground)
and save. Usage: python tools/pixellab_install.py <src.png> <dest.png>
"""
import sys

from PIL import Image


def main():
    src, dst = sys.argv[1], sys.argv[2]
    im = Image.open(src).convert("RGBA")
    bbox = im.getbbox()
    if bbox:
        im = im.crop(bbox)
    im.save(dst)
    print(f"{src} -> {dst}  {im.size}")


if __name__ == "__main__":
    main()
