"""One-off (2026-07-15): seed the default mastery fatigue perks on every spell.

The spell-mastery rework removed the blanket "cost / (levels above + 1)" fatigue
discount; cost reduction is now per-spell data in `masteryBonuses`. This adds the
agreed default ladder (30/50/75/85% at +1..+4 above the primary requirement) to
every spell that HAS a primary path requirement (path set AND level >= 1 — the
same SpellPathReq.HasRequirement condition the game uses) and doesn't already
carry a masteryBonuses list.

Run from the repo root, then `--roundtrip-data` to bake the game's formatting.
"""
import json

PATH = "data/spells.json"
PERKS = [
    "+1: fatigue -30%",
    "+2: fatigue -50%",
    "+3: fatigue -75%",
    "+4: fatigue -85%",
]

with open(PATH, encoding="utf-8") as f:
    data = json.load(f)

changed, skipped = [], []
for sp in data["spells"]:
    if not sp.get("primaryPath") or sp.get("primaryLevel", 0) < 1:
        skipped.append(sp["id"])
        continue
    if "masteryBonuses" in sp:
        skipped.append(sp["id"] + " (already has masteryBonuses)")
        continue
    sp["masteryBonuses"] = PERKS[:]
    changed.append(sp["id"])

with open(PATH, "w", encoding="utf-8", newline="\n") as f:
    json.dump(data, f, indent=2, ensure_ascii=False)
    f.write("\n")

print(f"added masteryBonuses to {len(changed)} spells:")
for cid in changed:
    print(f"  {cid}")
print(f"skipped {len(skipped)}: {', '.join(skipped)}")
