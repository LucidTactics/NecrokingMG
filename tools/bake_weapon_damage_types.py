#!/usr/bin/env python3
"""Bake explicit damageType/twoHanded into data/weapons.json.

Bug fix (Review 1, S7-coincidental-proxy): WeaponStats.DamageType / TwoHanded were
inferred from the weapon's display Name at runtime, so a cosmetic rename silently
changed combat mechanics (e.g. the wolf weapon "Pounce" matched no keyword and fell
through to default Slashing, gaining limb-chop). The code now reads explicit
"damageType"/"twoHanded" from weapons.json and only falls back to name inference when
absent. This script freezes CURRENT behavior into the data by writing each weapon's
currently-inferred type explicitly.

Idempotent: existing damageType/twoHanded values are left untouched (setdefault), so a
designer can override "Pounce"/"Hook"/"Sweep" etc. and a re-run won't clobber them.

The keyword logic below mirrors Necroking/Data/WeaponClassifier.cs EXACTLY — keep in
sync if the C# keyword lists change.
"""
import json
import pathlib

BLUNT = ["club", "mace", "hammer", "maul", "staff", "kick", "fist", "punch",
         "unarmed", "smash", "bash", "flail", "trample", "stomp", "cudgel",
         "quarterstaff"]
PIERCING = ["spear", "pike", "lance", "dagger", "bite", "tusk", "fang", "sting",
            "javelin", "arrow", "bolt", "beak", "horn", "antler", "gore", "trident",
            "needle", "pick", "stiletto", "rapier"]
SLASHING = ["sword", "axe", "blade", "claw", "slash", "scythe", "glaive", "halberd",
            "saber", "sabre", "cleaver", "hook", "talon", "scimitar", "katana",
            "falchion", "sickle", "sweep", "rend"]
TWO_HANDED = ["greatsword", "great sword", "greataxe", "great axe", "greatclub",
              "maul", "halberd", "pike", "glaive", "zweihander", "two-hand",
              "twohand", "longspear", "warhammer", "war hammer", "polearm"]


def contains_any(n, kws):
    return any(k in n for k in kws)


def classify(name):
    # Mirrors WeaponClassifier.Classify: blunt, then slashing, then piercing, default slashing.
    n = (name or "").lower()
    if not n:
        return "Slashing"
    if contains_any(n, BLUNT):
        return "Blunt"
    if contains_any(n, SLASHING):
        return "Slashing"
    if contains_any(n, PIERCING):
        return "Piercing"
    return "Slashing"


def is_two_handed(name):
    n = (name or "").lower()
    return bool(n) and contains_any(n, TWO_HANDED)


def main():
    path = pathlib.Path(__file__).resolve().parents[1] / "data" / "weapons.json"
    data = json.loads(path.read_text(encoding="utf-8"))
    weapons = data["weapons"]
    added_dt = 0
    added_th = 0
    for w in weapons:
        name = w.get("name", "")
        if "damageType" not in w:
            w["damageType"] = classify(name)
            added_dt += 1
        if "twoHanded" not in w:
            w["twoHanded"] = is_two_handed(name)
            added_th += 1
    path.write_text(json.dumps(data, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    print(f"baked {len(weapons)} weapons: +{added_dt} damageType, +{added_th} twoHanded")


if __name__ == "__main__":
    main()
