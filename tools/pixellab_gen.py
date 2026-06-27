"""PixelLab style-matched sprite generator (bitforge endpoint).

Generates pixel-art sprites that match the art style of a REFERENCE image, with a
transparent background — the closest the public PixelLab API gets to the web app's
"Pro consistent-style" tool. (The public REST API / Python SDK only expose a single
`style_image`; the multi-reference Pro flow has no public endpoint.)

Endpoint: POST https://api.pixellab.ai/v1/generate-image-bitforge
  body: { description, image_size:{width,height}, style_image:{base64}, style_strength,
          text_guidance_scale, no_background, seed }
  resp: { image: { base64 } }   (raw base64 PNG, RGBA when no_background)

Secret: PIXELLAB_SECRET=<token> at E:\\Nightfall\\Corpobot\\art_prototype\\.env.secrets

Usage:
  python tools/pixellab_gen.py --style assets/Environment/Buildings/MorticianTable.png \
      --prompt "alchemist brewing table with bubbling green potion flasks" \
      --name alchemist_table --n 4 --size 128x128 --strength 55
"""
import argparse
import base64
import io
import os
import sys

import requests
from PIL import Image

BASE = "https://api.pixellab.ai/v1"
SECRET_PATH = r"E:\Nightfall\Corpobot\art_prototype\.env.secrets"


def load_secret() -> str:
    with open(SECRET_PATH) as f:
        return f.read().strip().split("=", 1)[1].strip()


def b64_of(path: str) -> str:
    with open(path, "rb") as f:
        return base64.b64encode(f.read()).decode("ascii")


def style_b64_resized(path: str, w: int, h: int, flatten=(46, 44, 52)) -> str:
    """bitforge requires the style image to match the output size exactly, and
    chokes on a transparent style reference (reads it as empty → blank/noise).
    Flatten the sprite onto a solid background so the model sees a full image."""
    im = Image.open(path).convert("RGBA").resize((w, h), Image.NEAREST)
    if flatten is not None:
        bg = Image.new("RGBA", (w, h), flatten + (255,))
        bg.alpha_composite(im)
        im = bg
    buf = io.BytesIO()
    im.save(buf, format="PNG")
    return base64.b64encode(buf.getvalue()).decode("ascii")


def gen_one(secret, endpoint, style_b64, color_b64, prompt, w, h, strength, guidance, no_bg, seed):
    body = {
        "description": prompt,
        "image_size": {"width": w, "height": h},
        "text_guidance_scale": float(guidance),
        "no_background": bool(no_bg),
    }
    if seed is not None:
        body["seed"] = int(seed)
    if color_b64:  # forced color palette (both endpoints support color_image)
        body["color_image"] = {"base64": color_b64}
    if endpoint == "bitforge":
        body["style_image"] = {"base64": style_b64}
        body["style_strength"] = float(strength)
        path = "/generate-image-bitforge"
    else:  # pixflux: reliable text->pixel-art, optional color palette ref
        path = "/generate-image-pixflux"
    r = requests.post(f"{BASE}{path}", json=body,
                      headers={"Authorization": f"Bearer {secret}"}, timeout=300)
    if r.status_code != 200:
        raise RuntimeError(f"HTTP {r.status_code}: {r.text[:400]}")
    return Image.open(io.BytesIO(base64.b64decode(r.json()["image"]["base64"]))).convert("RGBA")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--endpoint", default="bitforge", choices=["bitforge", "pixflux"])
    ap.add_argument("--style", default="", help="reference image path (bitforge: defines art style)")
    ap.add_argument("--color", default="", help="palette reference image (pixflux/bitforge color_image)")
    ap.add_argument("--prompt", required=True)
    ap.add_argument("--name", required=True, help="output base name")
    ap.add_argument("--out", default="log/pixellab", help="output dir")
    ap.add_argument("--size", default="128x128")
    ap.add_argument("--n", type=int, default=4)
    ap.add_argument("--strength", type=float, default=55.0, help="style_strength 0-100")
    ap.add_argument("--guidance", type=float, default=8.0, help="text_guidance_scale 1-20")
    ap.add_argument("--seed", type=int, default=1000, help="base seed; +i per variant")
    ap.add_argument("--background", action="store_true", help="keep background (default: removed)")
    args = ap.parse_args()

    w, h = (int(v) for v in args.size.lower().split("x"))
    os.makedirs(args.out, exist_ok=True)
    secret = load_secret()
    style_b64 = style_b64_resized(args.style, w, h) if args.style else ""
    color_b64 = style_b64_resized(args.color, w, h, flatten=None) if args.color else ""
    no_bg = not args.background

    print(f"endpoint={args.endpoint}  style={args.style or '-'}  size={w}x{h}  strength={args.strength}  guidance={args.guidance}  no_bg={no_bg}")
    print(f"prompt: {args.prompt}")
    paths = []
    for i in range(args.n):
        seed = args.seed + i
        try:
            img = gen_one(secret, args.endpoint, style_b64, color_b64, args.prompt, w, h, args.strength, args.guidance, no_bg, seed)
        except Exception as e:
            print(f"  [{i}] ERROR: {e}")
            continue
        p = os.path.join(args.out, f"{args.name}_{i}.png")
        img.save(p)
        img.resize((w * 3, h * 3), Image.NEAREST).save(os.path.join(args.out, f"{args.name}_{i}_x3.png"))
        paths.append(p)
        print(f"  [{i}] seed={seed} -> {p}")
    print(f"done: {len(paths)} images")


if __name__ == "__main__":
    sys.exit(main())
