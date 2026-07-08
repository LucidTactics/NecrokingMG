"""One-off: initial DRN-tier assignment for every unit in data/units.json.

Tiers (see UnitUtil.RollDRN): 1=d3, 2=d6, 3=d6 exploding once, 4=d6 open-ended.
Rules used for this first pass (user hand-tunes afterwards):
  - animals (incl. their zombie forms) size <= 3 -> 1, bigger -> 2
  - untrained humans / undead humanoids / anything uncertain -> 2
  - trained humans + player necromancer forms -> 3
Reuses json_data's load/save so the file formatting is preserved.
"""
import sys, os
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from json_data import _load, _find_list, _save

LEVEL1 = {
    # living animals size <= 3
    'FemaleDeer', 'Rat', 'MaleDeer', 'Wolf', 'Bear', 'DireWolf', 'JuvWolf',
    'Boar', 'GreatBoar', 'watchdog',
    # zombie animals keep their body's animal tier
    'ZombieRat', 'ZombieFemaleDeer', 'ZombieWolf', 'WolfCubZombie',
    'ZombieMaleDeer', 'ZombieJuvenileBear', 'ZombieBoar', 'ZombieGreatBoar',
}
LEVEL3 = {
    # trained humans
    'militia', 'militia_copy', 'soldier', 'knight', 'archer', 'hunter',
    # player necromancer forms
    'necromancer', 'wretched', 'pale_acolyte', 'wight', 'lich',
    'grand_necromancer', 'necromancer_debug',
}
# everything else (undead humanoids, skeletons, peasant, priest,
# GrizzlyBear size 4, ZombieGrizzlyBear, ...) -> 2

path = 'data/units.json'
root = _load(path)
units, _, _ = _find_list(root, path)
for u in units:
    uid = u.get('id')
    level = 1 if uid in LEVEL1 else 3 if uid in LEVEL3 else 2
    stats = u.setdefault('stats', {})
    stats['drn'] = level
    print(f"{uid}: drn={level}")
_save(path, root)
print(f"\nWrote {len(units)} units to {path}")
