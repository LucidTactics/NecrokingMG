"""Add trap_zap spell to spells.json - a short-range lightning zap for traps."""
import json

SPELL_FILE = "e:/Nightfall/NecrokingMG/Necroking/bin/Publish/data/spells.json"

with open(SPELL_FILE, 'r') as f:
    data = json.load(f)

spells = data.get("spells", [])
if any(s.get("id") == "trap_zap" for s in spells):
    print("trap_zap already exists, skipping")
else:
    # Find lightning_zap as template
    template = None
    for s in spells:
        if s.get("id") == "lightning_zap":
            template = json.loads(json.dumps(s))  # deep copy
            break
    if template is None:
        print("ERROR: lightning_zap not found")
        exit(1)

    # Modify for trap use
    template["id"] = "trap_zap"
    template["name"] = "Trap Zap"
    template["range"] = 1.5          # short range for traps
    template["manaCost"] = 0         # traps don't use mana
    template["cooldown"] = 2.0       # refire rate for multi-use traps
    template["damage"] = 8           # slightly less than player version
    template["castTime"] = 0
    template["strikeTargetUnit"] = True
    template["zapDuration"] = 0.2
    template["strikeChainBranches"] = 0  # no chaining
    template["strikeChainDepth"] = 0

    spells.append(template)
    data["spells"] = spells

    with open(SPELL_FILE, 'w') as f:
        json.dump(data, f, indent=2)

    # Also copy to source tree
    SRC_FILE = "e:/Nightfall/NecrokingMG/data/spells.json"
    with open(SRC_FILE, 'w') as f:
        json.dump(data, f, indent=2)

    print("Added trap_zap spell")
