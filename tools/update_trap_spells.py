"""Update trap env defs with trap_zap spell and uses."""
import json

TRAP_SPELLS = {
    "spike_trap": {"trapSpellId": "trap_zap", "trapUses": 1},
    "poison_spike_trap": {"trapSpellId": "trap_zap", "trapUses": 1},
}

files = [
    "e:/Nightfall/NecrokingMG/Necroking/bin/Publish/maps/env_defs.json",
    "e:/Nightfall/NecrokingMG/Necroking/bin/Publish/assets/maps/env_defs.json",
    "e:/Nightfall/NecrokingMG/Necroking/bin/Publish/assets/maps/default.json",
]

for path in files:
    try:
        with open(path, 'r') as f:
            data = json.load(f)
    except Exception as e:
        print(f"Skip {path}: {e}")
        continue

    defs = data if isinstance(data, list) else data.get("envDefs", [])
    updated = 0
    for d in defs:
        did = d.get("id", "")
        if did in TRAP_SPELLS:
            for k, v in TRAP_SPELLS[did].items():
                d[k] = v
            updated += 1

    with open(path, 'w') as f:
        json.dump(data, f, indent=2)
    print(f"  {path}: updated {updated} traps")

print("Done")
