"""Bake a pre-harmonized (dark bronze) copy of the cloth window frame so it can
be 9-sliced for the wider skill-book window while keeping the EXACT colour the
grimoire's Grim_WindowBorder shows at render time.

DrawNineSlice can't run the runtime harmonizer, so we apply it here offline,
replicating ColorHarmonizer.Harmonize (non-HCL) with the Grim_WindowBorder
element's params: target #261F11 (38,31,17), hue/sat strength 1.0, val 0.052,
then the element's tintColor 156/255. With hue+sat fully shifted to the target,
every pixel keeps its own brightness V but takes the target's hue (~40deg) and
saturation (~0.553) — i.e. value*[1, 0.816, 0.447].

Output: assets/UI/Frames/ClothFrameHarmonized.png (gitignored like other art;
re-run this to regenerate).
"""
import numpy as np
from PIL import Image

SRC = "assets/UI/Frames/ClothUpgradeFrame2-DarkBorder1.png"
OUT = "assets/UI/Frames/ClothFrameHarmonized.png"

# Grim_WindowBorder harmonize params.
TARGET = np.array([38, 31, 17]) / 255.0
SAT_STR, VAL_STR = 1.0, 0.052
TINT = 156 / 255.0


def rgb_to_v(rgb):
    return rgb.max(axis=-1) / 255.0


def main():
    arr = np.asarray(Image.open(SRC).convert("RGBA")).astype(float)
    rgb, alpha = arr[..., :3], arr[..., 3:4]

    # target HSV (max-based V, standard S/H)
    tmax, tmin = TARGET.max(), TARGET.min()
    tV = tmax
    tS = 0.0 if tmax == 0 else (tmax - tmin) / tmax
    # target hue (deg) — red is max for #261F11
    r, g, b = TARGET
    tH = (60 * ((g - b) / (tmax - tmin))) if tmax != tmin else 0.0  # ~40deg

    # With hueStr=satStr=1, every pixel -> (tH, tS, newV). For tH in [0,60):
    #   R = V, G = V*(1 - (1 - tH/60)*tS), B = V*(1 - tS)
    gx = 1.0 - (1.0 - tH / 60.0) * tS
    bx = 1.0 - tS
    direction = np.array([1.0, gx, bx])

    V = rgb_to_v(rgb)
    newV = V * (1.0 - VAL_STR) + tV * VAL_STR
    out = (newV[..., None] * direction * 255.0 * TINT).clip(0, 255)
    res = np.concatenate([out, alpha], axis=-1).astype(np.uint8)
    Image.fromarray(res).save(OUT)

    # report a couple of sample band colours
    h, w, _ = res.shape
    print("baked", OUT, res.shape)
    print("sample band:", tuple(int(v) for v in res[8, w // 2][:3]),
          tuple(int(v) for v in res[h // 2, 8][:3]))


if __name__ == "__main__":
    main()
