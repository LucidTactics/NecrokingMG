"""Generate buff/status icons via PixelLab pixflux (same pipeline as
gen_spell_icons.py — REST API, secret from Corpobot .env.secrets).

96x96 to match the spell icons; the unit-sheet Abilities & Buffs row draws them
at 24px. Output: assets/UI/Icons/Buffs/{id}.png (+ _x4 preview in log/).
Skips ids whose png already exists — safe to re-run for failures.
"""
import base64
import io
import json
import os

import requests
from PIL import Image

BASE = "https://api.pixellab.ai/v1"
OUT = "assets/UI/Icons/Buffs"
PREVIEW = "log/bag_inspect/buff_icons"
os.makedirs(OUT, exist_ok=True)
os.makedirs(PREVIEW, exist_ok=True)

with open(r"E:\Nightfall\Corpobot\art_prototype\.env.secrets") as f:
    SECRET = f.read().strip().split("=", 1)[1]

# Match the GodMenu spell-icon look: moody dark background, one vivid accent.
STYLE = (", dark fantasy status effect icon, painterly pixel art, moody dark background, "
         "single vivid accent glow, detailed shading, centered emblem, no text, no border")

# Buff id -> prompt. Beneficial buffs read warm/vivid; debuffs/status read
# sickly/cold so the player can tell help from harm at a glance.
PROMPTS = {
    "iron_skin":            "torso sheathed in interlocking grey iron plates, hard metallic sheen, protective glow",
    "strength_buff":        "flexed muscular arm clenching a fist, radiating red power aura",
    "iron_skin_copy":       "winged boot wreathed in cyan speed streaks, blurred with motion",   # Quickness
    "buff_3":               "glowing golden four-leaf clover charm, sparkles of fortune",          # Lucky
    "buff_frenzy":          "snarling fanged mouth with bloodshot red eyes, savage rage aura",
    "buff_paralysis_slow":  "figure frozen mid-stride locked in crackling yellow electric paralysis",
    "buff_zombie_mark":     "sickly green skull brand seared onto flesh, dripping necrotic curse",
    "buff_poison_dot":      "dripping green poison droplet with a faint skull, toxic fumes rising",
    "buff_miasma_slow":     "swirling sickly green miasma fog clinging to a hunched figure, sluggish",
    "buff_plagued":         "diseased flesh covered in oozing green plague boils, weakened",
    "buff_knockdown":       "stunned figure knocked flat on its back with spinning daze stars",
    "buff_unholy_movement": "skeletal legs sprinting wreathed in unholy purple necrotic energy",
    "buff_unholy_strength": "skeletal arm flexed and surging with unholy purple necrotic power",
    "buff_satiated":        "contented full belly with a warm orange glow, well-fed",
    "buff_god_mode":        "radiant divine figure haloed in overwhelming golden holy light, all-powerful",
}


def gen(buff_id, prompt):
    out_path = f"{OUT}/{buff_id}.png"
    if os.path.exists(out_path):
        print(f"   skip {buff_id} (exists)")
        return True
    r = requests.post(
        f"{BASE}/generate-image-pixflux",
        json={"description": prompt + STYLE, "image_size": {"width": 96, "height": 96}},
        headers={"Authorization": f"Bearer {SECRET}"}, timeout=300)
    if r.status_code != 200:
        print(f"   ERROR {buff_id} {r.status_code}: {r.text[:200]}")
        return False
    img = Image.open(io.BytesIO(base64.b64decode(r.json()["image"]["base64"])))
    img.save(out_path)
    img.resize((384, 384), Image.NEAREST).save(f"{PREVIEW}/{buff_id}_x4.png")
    print(f"   ok {buff_id}")
    return True


def main():
    fails = []
    for bid, prompt in PROMPTS.items():
        if not prompt:
            continue
        print(f"-> {bid}")
        if not gen(bid, prompt):
            fails.append(bid)
    print(f"\nDone. {len(PROMPTS)} requested, {len(fails)} failed: {fails}")


if __name__ == "__main__":
    main()
