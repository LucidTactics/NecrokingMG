"""Generate spell icons via PixelLab pixflux (REST, per Corpobot pipeline notes:
the pip client breaks on subscription accounts — call the API directly).

96x96 to match the Unity spell icons (SummonWolves 98x96, shadowblast 85x85);
the grimoire PerkIcon draws them at ~64px. One icon per non-debug spell in
data/spells.json; variants (_kb, lifedrain twins) share their parent's icon.
Output: assets/UI/Icons/Spells/{id}.png (+ _x4 preview in log/bag_inspect/).
Skips ids whose png already exists — safe to re-run for failures.
"""
import base64
import io
import json
import os
import sys

import requests
from PIL import Image

BASE = "https://api.pixellab.ai/v1"
OUT = "assets/UI/Icons/Spells"
PREVIEW = "log/bag_inspect/spell_icons"
os.makedirs(OUT, exist_ok=True)
os.makedirs(PREVIEW, exist_ok=True)

with open(r"E:\Nightfall\Corpobot\art_prototype\.env.secrets") as f:
    SECRET = f.read().strip().split("=", 1)[1]

# Shared style suffix modeled on the GodMenu icon look (shadowblast/SummonWolves):
# moody dark background, one vivid accent, painterly pixel shading.
STYLE = (", dark fantasy spell icon, painterly pixel art, moody dark background, "
         "single vivid accent glow, detailed shading, no text, no border")

PROMPTS = {
    "fireball": "swirling orb of purple-black necrotic fire hurtling forward, wisps of dark flame",
    "nether_darts": "volley of three slender dark purple energy darts flying in formation, trailing shadow",
    "summon_skeleton": "skeleton warrior rising out of cracked earth, bony hand reaching up, green necromantic glow",
    "summon_skeleton_copy": "spectral deer with glowing antlers standing in dark mist, ghostly green sheen",
    "summon_skeleton_copy_copy": "snarling dark wolf head and shoulders, grey fur, glowing eyes",
    "summon_skeleton_copy_copy_copy": "rotting undead zombie wolf, exposed ribs, sickly green glow, tattered fur",
    "skeleton_warrior_upgrade": "skeleton warrior clad in rusted plate armor holding a sword upright, green eye sockets",
    "summon_abomination": "hulking stitched flesh abomination with mismatched limbs, hunched, ominous shadow",
    "sky_lightning": "thick violet lightning bolt striking down from storm clouds",
    "lightning_zap": "small crackling arc of violet electricity between two points",
    "lightning_beam": "continuous beam of violet lightning energy firing horizontally, crackling edges",
    "skeleton_warrior_upgrade_copy": "elite skeleton knight in gilded ornate armor with plumed helm, sword raised",
    "spell_9": "forearm and clenched fist turning to grey iron, metallic sheen spreading over skin",
    "spell_9_copy": "winged boots with cyan speed streaks trailing behind, motion blur",
    "spell_11": "golden four-leaf clover charm glowing softly, sparkles of fortune",
    "spell_12": "stream of red life essence flowing from a fading figure into an open clawed hand",
    "raise_zombie": "rotting zombie hand bursting up from a grave, dirt flying, green glow",
    "raise_zombie_throng": "three zombie silhouettes rising together from graveyard soil, green miasma",
    "life_drain": "torrent of crimson soul energy siphoning between two figures, skull motif",
    "god_ray": "brilliant column of holy light breaking through dark clouds onto the ground",
    "ghost_mode": "translucent hooded ghost figure fading into mist, pale blue glow",
    "poison_cloud": "billowing cloud of green poison miasma with a faint skull shape inside",
    "poison_burst": "explosive burst of green poison droplets radiating outward",
    "paralyze_burst": "radial burst of yellow paralyzing sparks, frozen jagged arcs",
    "poison_berries_poison": "cluster of dark nightshade berries dripping green venom",
    "poison_berries_paralysis": "cluster of pale yellow berries crackling with tiny stunning sparks",
    "reanimate_imbue": "dark spirit flame being pressed into a corpse's chest, purple glow",
    "reanimate_raise": "corpse sitting up from the ground wreathed in purple necromantic energy",
    "ghost_mode_extra_unused": "",
}
SHARED = {  # variant id -> source id (reuse icon)
    "fireball_kb": "fireball",
}


def gen(spell_id, prompt):
    out_path = f"{OUT}/{spell_id}.png"
    if os.path.exists(out_path):
        print(f"   skip {spell_id} (exists)")
        return True
    r = requests.post(
        f"{BASE}/generate-image-pixflux",
        json={"description": prompt + STYLE, "image_size": {"width": 96, "height": 96}},
        headers={"Authorization": f"Bearer {SECRET}"}, timeout=300)
    if r.status_code != 200:
        print(f"   ERROR {spell_id} {r.status_code}: {r.text[:200]}")
        return False
    img = Image.open(io.BytesIO(base64.b64decode(r.json()["image"]["base64"])))
    img.save(out_path)
    img.resize((384, 384), Image.NEAREST).save(f"{PREVIEW}/{spell_id}_x4.png")
    print(f"   ok {spell_id}")
    return True


def main():
    spells = json.load(open("data/spells.json", encoding="utf-8"))["spells"]
    fails = []
    for s in spells:
        sid = s["id"]
        if sid.startswith("debug_") or sid == "order_attack":
            continue
        if sid in SHARED:
            src = f"{OUT}/{SHARED[sid]}.png"
            dst = f"{OUT}/{sid}.png"
            if os.path.exists(src) and not os.path.exists(dst):
                Image.open(src).save(dst)
            continue
        prompt = PROMPTS.get(sid)
        if not prompt:
            print(f"   NO PROMPT for {sid}")
            fails.append(sid)
            continue
        if not gen(sid, prompt):
            fails.append(sid)
    # contact sheet for review
    ids = [s["id"] for s in spells if os.path.exists(f"{OUT}/{s['id']}.png")]
    if ids:
        cols = 7
        rows = (len(ids) + cols - 1) // cols
        sheet = Image.new("RGB", (cols * 100, rows * 112), (40, 40, 46))
        from PIL import ImageDraw
        dr = ImageDraw.Draw(sheet)
        for i, sid in enumerate(ids):
            im = Image.open(f"{OUT}/{sid}.png").convert("RGB")
            sheet.paste(im, ((i % cols) * 100 + 2, (i // cols) * 112 + 2))
            dr.text(((i % cols) * 100 + 2, (i // cols) * 112 + 99), sid[:15], fill=(220, 220, 220))
        sheet.save("log/bag_inspect/spell_icons_sheet.png")
    print("FAILS:", fails if fails else "none")


if __name__ == "__main__":
    main()
