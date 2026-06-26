"""Generate backgroundless map sprites for the worker-job buildings via PixelLab
pixflux (same REST pipeline as gen_buff_icons.py / gen_spell_icons.py).

Requires the PixelLab secret at:
    E:\\Nightfall\\Corpobot\\art_prototype\\.env.secrets   (KEY=<token>)

Outputs 192x192 PNGs to assets/Environment/Buildings/jobs/<id>.png and a x2
preview under log/. Skips ids whose png already exists — safe to re-run.

The job-system env defs (data/env_defs.json) currently point at PLACEHOLDER
sprites (OpenGrave, MorticianTable, RockPile4, Table2, Bush3, Deathcap). After
running this, repoint each def's texturePath at assets/Environment/Buildings/jobs/<id>.png
(and re-check pivotY/spriteWorldHeight/collisionRadius so it sits on the ground).

Style is matched to the existing graveyard props / Necro Table: grimdark, muted,
top-down-ish 3/4 view, single light source, no background.
"""
import base64
import io
import os

import requests
from PIL import Image

BASE = "https://api.pixellab.ai/v1"
OUT = "assets/Environment/Buildings/jobs"
PREVIEW = "log/building_sprites"
SECRET_PATH = r"E:\Nightfall\Corpobot\art_prototype\.env.secrets"

os.makedirs(OUT, exist_ok=True)
os.makedirs(PREVIEW, exist_ok=True)

STYLE = (", grimdark fantasy necromancer base prop, muted desaturated palette, "
         "3/4 top-down game view, single moody light source, painterly pixel art, "
         "sitting on the ground, no background, transparent background, no text, no border")

PROMPTS = {
    "empty_grave":      "an open empty grave pit with a freshly dug mound of dark earth and a small wooden grave marker",
    "mushroom_pile":    "a heap of harvested pale fungus and toadstools piled on a low wooden pallet, a foraging stockpile",
    "corpse_pile":      "a grim stacked pile of shrouded corpses and bones, a body stockpile, wrapped in tattered cloth",
    "harvesting_table": "a blood-stained butcher's harvesting table with cleavers and hooks for breaking down corpses",
    "alchemist_table":  "an alchemist's brewing table with bubbling green potion flasks, vials, and a mortar and pestle",
    "necro_table":      "a dark stone necromancer's reanimation altar etched with glowing green runes",
}


def remove_bg(img: Image.Image) -> Image.Image:
    """Best-effort: key out a near-uniform background sampled from the corners."""
    img = img.convert("RGBA")
    px = img.load()
    w, h = img.size
    corners = [px[0, 0], px[w - 1, 0], px[0, h - 1], px[w - 1, h - 1]]
    br = sum(c[0] for c in corners) // 4
    bg = sum(c[1] for c in corners) // 4
    bb = sum(c[2] for c in corners) // 4
    tol = 28
    for y in range(h):
        for x in range(w):
            r, g, b, a = px[x, y]
            if abs(r - br) < tol and abs(g - bg) < tol and abs(b - bb) < tol:
                px[x, y] = (r, g, b, 0)
    return img


def load_secret() -> str:
    with open(SECRET_PATH) as f:
        return f.read().strip().split("=", 1)[1]


def gen(secret: str, sprite_id: str, prompt: str) -> bool:
    out_path = f"{OUT}/{sprite_id}.png"
    if os.path.exists(out_path):
        print(f"   skip {sprite_id} (exists)")
        return True
    r = requests.post(
        f"{BASE}/generate-image-pixflux",
        json={"description": prompt + STYLE, "image_size": {"width": 192, "height": 192}},
        headers={"Authorization": f"Bearer {secret}"}, timeout=300)
    if r.status_code != 200:
        print(f"   ERROR {sprite_id} {r.status_code}: {r.text[:200]}")
        return False
    img = Image.open(io.BytesIO(base64.b64decode(r.json()["image"]["base64"])))
    img = remove_bg(img)
    img.save(out_path)
    img.resize((384, 384), Image.NEAREST).save(f"{PREVIEW}/{sprite_id}_x2.png")
    print(f"   ok {sprite_id}")
    return True


def main():
    if not os.path.exists(SECRET_PATH):
        print(f"NO SECRET at {SECRET_PATH} — cannot call PixelLab. "
              f"Placeholders remain in data/env_defs.json.")
        return
    secret = load_secret()
    fails = []
    for sid, prompt in PROMPTS.items():
        print(f"-> {sid}")
        if not gen(secret, sid, prompt):
            fails.append(sid)
    print(f"\nDone. {len(PROMPTS)} requested, {len(fails)} failed: {fails}")


if __name__ == "__main__":
    main()
