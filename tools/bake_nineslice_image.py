"""Bake a Unity-style nine-slice rendering of a sprite into a fixed-size PNG.

Reproduces what Unity's sliced Image does: the four source border bands are
compressed/stretched to the destination border sizes, the center stretches to
fill the rest. Resizing is premultiplied-aware LANCZOS so soft alpha edges
don't halo. Use this when porting Unity sliced sprites into the MonoGame UI,
which point-samples and therefore needs textures pre-baked at display size.

Usage:
  python tools/bake_nineslice_image.py <src.png> <dstW> <dstH> \
      <srcL,srcB,srcR,srcT> <dstL,dstB,dstR,dstT> <out.png>

Border order matches Unity's spriteBorder: left, bottom, right, top (pixels).
Unity dest border px = border / (spritePPU * pixelsPerUnitMultiplier / 100).
"""
import sys
import numpy as np
from PIL import Image


def resize_premul(img, size):
    if img.width == 0 or img.height == 0 or size[0] <= 0 or size[1] <= 0:
        return None
    if img.size == size:
        return img
    a = np.asarray(img.convert("RGBA")).astype(np.float32)
    rgb, alpha = a[..., :3], a[..., 3:4]
    pm = np.concatenate([rgb * alpha / 255.0, alpha], axis=-1)
    pm_img = Image.fromarray(np.uint8(pm + 0.5), "RGBA").resize(size, Image.LANCZOS)
    b = np.asarray(pm_img).astype(np.float32)
    out_a = b[..., 3:4]
    safe = np.maximum(out_a, 1e-3)
    out_rgb = np.clip(b[..., :3] * 255.0 / safe, 0, 255)
    return Image.fromarray(np.uint8(np.concatenate([out_rgb, out_a], axis=-1) + 0.5), "RGBA")


def bake(src, dst_w, dst_h, src_borders, dst_borders):
    sl, sb, sr, st = src_borders
    dl, db, dr, dt = dst_borders
    w, h = src.size
    # source x/y cut points
    sx = [0, sl, w - sr, w]
    sy = [0, st, h - sb, h]
    # dest x/y cut points
    dx = [0, dl, dst_w - dr, dst_w]
    dy = [0, dt, dst_h - db, dst_h]

    out = Image.new("RGBA", (dst_w, dst_h), (0, 0, 0, 0))
    for ry in range(3):
        for rx in range(3):
            sw, sh = sx[rx + 1] - sx[rx], sy[ry + 1] - sy[ry]
            dw, dh = dx[rx + 1] - dx[rx], dy[ry + 1] - dy[ry]
            if sw <= 0 or sh <= 0 or dw <= 0 or dh <= 0:
                continue
            region = src.crop((sx[rx], sy[ry], sx[rx + 1], sy[ry + 1]))
            scaled = resize_premul(region, (dw, dh))
            if scaled is not None:
                out.paste(scaled, (dx[rx], dy[ry]))
    return out


def main():
    if len(sys.argv) != 7:
        print(__doc__)
        sys.exit(1)
    src_path, dst_w, dst_h, src_b, dst_b, out_path = sys.argv[1:]
    src = Image.open(src_path).convert("RGBA")
    src_borders = [int(v) for v in src_b.split(",")]
    dst_borders = [int(v) for v in dst_b.split(",")]
    out = bake(src, int(dst_w), int(dst_h), src_borders, dst_borders)
    out.save(out_path)
    print(f"baked {out_path} ({out.width}x{out.height})")


if __name__ == "__main__":
    main()
