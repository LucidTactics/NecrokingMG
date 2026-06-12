"""Bake a Unity UI icon at display size, replicating Unity's exact sampling.

Unity imports sprites with alphaIsTransparency=1: opaque RGB is DILATED into
transparent pixels (nearest opaque color, alpha stays 0). The GPU then bilinear
filters STRAIGHT (non-premultiplied) RGBA: transparent pixels contribute their
dilated RGB to the interpolated color at full weight. A premultiplied-weighted
resize gives a different (lighter) edge color wherever the silhouette rim is
darker than the interior — the "gray halo" mismatch on the Strength icon.

Usage: python tools/bake_unity_icon.py <src.png> <out_w> <out_h> <out.png>
"""
import sys
import numpy as np
from PIL import Image


def dilate_rgb(arr):
    """Fill transparent pixels' RGB with the nearest filled color (8-neighbor
    iterative flood, like Unity's alphaIsTransparency import dilation)."""
    rgb = arr[..., :3].astype(np.float32)
    filled = arr[..., 3] > 0
    if filled.all():
        return arr
    dirs = [(-1, -1), (-1, 0), (-1, 1), (0, -1), (0, 1), (1, -1), (1, 0), (1, 1)]
    out = rgb.copy()
    for _ in range(max(arr.shape[:2])):
        if filled.all():
            break
        acc = np.zeros_like(rgb)
        cnt = np.zeros(filled.shape, np.float32)
        for dy, dx in dirs:
            sf = np.roll(np.roll(filled, dy, 0), dx, 1)
            sc = np.roll(np.roll(out, dy, 0), dx, 1)
            take = sf & ~filled
            acc[take] += sc[take]
            cnt[take] += 1
        grew = (cnt > 0) & ~filled
        out[grew] = acc[grew] / cnt[grew][:, None]
        filled |= grew
    res = arr.copy()
    res[..., :3] = np.uint8(np.clip(out, 0, 255) + 0.5)
    return res


def bake_outline(arr, thickness, color, opacity):
    """Bake a Unity-style silhouette outline into a straight-alpha array AT
    SOURCE RESOLUTION (the shader works in texels; when the sprite is drawn
    scaled, the outline scales with it — bake before any resize). Coverage =
    clamp(th + 1 - dist): hard band + 1px AA rim, only where srcA < 128."""
    h, w = arr.shape[:2]
    a = arr[..., 3].astype(np.float32)
    cov = np.zeros((h, w), np.float32)
    r = int(np.ceil(thickness + 1))
    for dy in range(-r, r + 1):
        for dx in range(-r, r + 1):
            if dx == 0 and dy == 0:
                continue
            dist = np.hypot(dx, dy)
            wgt = np.clip(thickness + 1 - dist, 0, 1)
            if wgt <= 0:
                continue
            na = np.roll(np.roll(a, dy, 0), dx, 1)
            if dy > 0: na[:dy] = 0
            elif dy < 0: na[dy:] = 0
            if dx > 0: na[:, :dx] = 0
            elif dx < 0: na[:, dx:] = 0
            cov = np.maximum(cov, wgt * na / 255.0)
    out_a = cov * opacity
    apply = a < 128
    sa = a / 255.0
    fin_a = sa + out_a * (1 - sa)
    oc = np.array(color, np.float32)
    res = arr.astype(np.float32)
    safe = np.maximum(fin_a, 1e-4)
    for c in range(3):
        blended = (res[..., c] * sa + oc[c] * out_a * (1 - sa)) / safe
        res[..., c] = np.where(apply, blended, res[..., c])
    res[..., 3] = np.where(apply, fin_a * 255.0, res[..., 3])
    return np.uint8(np.clip(res, 0, 255) + 0.5)


def bake(src_path, w, h, out_path):
    arr = np.asarray(Image.open(src_path).convert('RGBA'))
    dil = dilate_rgb(arr)
    # Straight-channel bilinear == what the GPU does on a non-premultiplied
    # texture. Do NOT premultiply here.
    out = Image.fromarray(dil, 'RGBA').resize((w, h), Image.BILINEAR)
    out.save(out_path)
    print(f'baked {src_path} -> {out_path} ({w}x{h}, dilated straight-bilinear)')


if __name__ == '__main__':
    bake(sys.argv[1], int(sys.argv[2]), int(sys.argv[3]), sys.argv[4])
