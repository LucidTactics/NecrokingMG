"""Generate skill-tree icons via PixelLab pixflux (same pipeline as
gen_spell_icons.py: call the REST API directly; the pip client breaks on
subscription accounts).

One 96x96 icon per skill across all five skill trees. The SkillBookPanel
grimoire tiles draw them at ~38px in a spider frame, replacing the placeholder
skull. Output: assets/UI/Icons/Skills/{skill_id}.png (+ _x4 preview).
Skips ids whose png already exists — safe to re-run for failures.
"""
import base64
import io
import json
import os

import requests
from PIL import Image, ImageDraw

BASE = "https://api.pixellab.ai/v1"
OUT = "assets/UI/Icons/Skills"
PREVIEW = "log/bag_inspect/skill_icons"
os.makedirs(OUT, exist_ok=True)
os.makedirs(PREVIEW, exist_ok=True)

with open(r"E:\Nightfall\Corpobot\art_prototype\.env.secrets") as f:
    SECRET = f.read().strip().split("=", 1)[1]

# Match the spell-icon look so skills and spells feel like one set.
STYLE = (", dark fantasy game skill icon, painterly pixel art, moody dark "
         "background, single vivid accent glow, detailed shading, centered, "
         "no text, no border")

PROMPTS = {
    # --- Potions: glass flasks, colour-coded by effect ---
    "skill_paralysis": "glass potion flask of crackling pale-yellow paralytic fluid, tiny sparks inside",
    "skill_reanimation": "glass potion flask of swirling green necromantic fluid, faint skull bubble",
    "skill_death_evolution": "ornate dark flask of deep purple-black death essence, violet glow",
    "skill_enlargement": "potion flask of bubbling orange fluid swelling upward, growth motif",
    "skill_poison": "potion flask of sickly green venom dripping toxic droplets, skull cork",
    "skill_greater_reanimation": "large ornate flask of bright emerald necromantic fluid, twin skull emblem",
    "skill_death_poison": "black flask of dark purple venom with a green skull swirl inside",
    "skill_efficient_tinctures": "mortar and pestle with glowing herbs on an alchemy bench, soft golden glow",
    # --- Monstrology: undead beasts + horde emblems ---
    "monster_summoner": "necromancer banner bearing a green skull, summoning circle glyph",
    "improved_monstrology": "open alchemy tome, a potion poured onto a zombie beast diagram, green glow",
    "monsterous_pets": "three small zombie beasts gathered together, faint green glow",
    "wolf_autopsy": "rotting zombie wolf head, exposed ribs, sickly green glow, grey fur",
    "boar_autopsy": "rotting zombie boar head with tusks, exposed flesh, green glow",
    "monsterous_horde": "horde of zombie beast silhouettes massing under a green moon",
    "dire_wolf_reanimation": "huge undead dire wolf, glowing green eyes, tattered fur, snarling",
    "wolf_lunge": "zombie wolf leaping forward mid-lunge, claws out, motion streaks",
    "boar_charge": "zombie boar charging with lowered tusks, dust trail, trample",
    "ancient_boar_reanimation": "massive ancient undead boar, huge curved tusks, bone armor, green glow",
    "monsterous_army": "army of undead beasts in formation beneath tattered banners",
    "bear_autopsy": "rotting zombie bear head roaring, exposed ribs, sickly green glow",
    "monsterous_legion": "vast legion of undead monsters to the horizon, green miasma",
    "corpse_eater": "zombie beast feasting on a corpse, green glow, hunger motif",
    "improved_corpse_eating": "zombie bear devouring a carcass, bloody, intense green glow",
    "bear_sweep": "zombie bear swinging a massive claw in a wide sweep, motion arc",
    "ancient_bear_reanimation": "colossal ancient undead bear, bone plating, glowing green eyes, towering",
    # --- Necromancy ---
    "brothers_keeper": "humanoid zombie soldier rising, rotting flesh, green necromantic glow",
    # --- Magic: spell glyphs ---
    "arcane_apprentice": "single candle flame lighting an open spellbook, faint arcane sparks",
    "nether_darts": "three slender dark-purple energy darts in formation, shadow trails",
    "lightning_zap": "small crackling violet electric arc between a hand and a skull",
    "god_ray": "brilliant column of holy light breaking through dark clouds onto the ground",
    "nether_storm": "swirling storm of dark-purple energy darts raining down",
    "chain_lightning": "violet lightning arcing between several points, branching bolts",
    "pyre_of_judgement": "towering pillar of radiant golden-white fire, divine judgement",
    "archmage": "ornate dark wizard robe with glowing arcane runes and hood, mastery",
    # --- Metamorphosis: evolution forms + stat boons ---
    "corpse_eating": "hooded figure drawing green soul essence from a corpse, healing glow",
    "become_pale_acolyte": "pale robed acolyte with glowing eyes, dark hood, death-magic aura",
    "death_fog_consumption": "hooded figure inhaling green death fog turning to glowing mana",
    "become_wight": "armored undead wight warrior, glowing eyes, spectral cold aura",
    "become_necromancer": "dark necromancer in flowing robes, staff topped with a green skull",
    "unholy_movement": "spectral undead feet with green speed streaks, swift motion",
    "unholy_strength": "skeletal arm flexing with dark-purple power, cracking ground",
    "soul_consumption": "hooded figure drawing a glowing blue soul from a human corpse",
    "become_lich": "skeletal lich with a glowing phylactery and crown, cold blue aura",
    "become_grand_necromancer": "imposing grand necromancer, ornate robes, floating skulls, green-purple power",
}


def gen(skill_id, prompt):
    out_path = f"{OUT}/{skill_id}.png"
    if os.path.exists(out_path):
        print(f"   skip {skill_id} (exists)")
        return True
    r = requests.post(
        f"{BASE}/generate-image-pixflux",
        json={"description": prompt + STYLE, "image_size": {"width": 96, "height": 96}},
        headers={"Authorization": f"Bearer {SECRET}"}, timeout=300)
    if r.status_code != 200:
        print(f"   ERROR {skill_id} {r.status_code}: {r.text[:200]}")
        return False
    img = Image.open(io.BytesIO(base64.b64decode(r.json()["image"]["base64"])))
    img.save(out_path)
    img.resize((384, 384), Image.NEAREST).save(f"{PREVIEW}/{skill_id}_x4.png")
    print(f"   ok {skill_id}")
    return True


def main():
    tabs = ["potions", "monstrology", "necromancy", "magic", "metamorphosis"]
    ids = []
    for tab in tabs:
        d = json.load(open(f"data/skills/{tab}.json", encoding="utf-8"))
        for s in d["skills"]:
            ids.append(s["id"])
    fails, missing = [], []
    for sid in ids:
        prompt = PROMPTS.get(sid)
        if not prompt:
            print(f"   NO PROMPT for {sid}")
            missing.append(sid)
            continue
        if not gen(sid, prompt):
            fails.append(sid)
    # contact sheet for review
    have = [s for s in ids if os.path.exists(f"{OUT}/{s}.png")]
    if have:
        cols = 7
        rows = (len(have) + cols - 1) // cols
        sheet = Image.new("RGB", (cols * 100, rows * 112), (40, 40, 46))
        dr = ImageDraw.Draw(sheet)
        for i, sid in enumerate(have):
            im = Image.open(f"{OUT}/{sid}.png").convert("RGB")
            sheet.paste(im, ((i % cols) * 100 + 2, (i // cols) * 112 + 2))
            dr.text(((i % cols) * 100 + 2, (i // cols) * 112 + 99), sid[:15], fill=(220, 220, 220))
        sheet.save("log/bag_inspect/skill_icons_sheet.png")
    print("MISSING PROMPT:", missing if missing else "none")
    print("FAILS:", fails if fails else "none")


if __name__ == "__main__":
    main()
