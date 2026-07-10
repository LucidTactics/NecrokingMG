"""One-off: rename naturalProt -> toughness (values copied 1:1) in units.json,
and retarget buff effects from stat NaturalProt -> Toughness in buffs.json.
Reuses json_data's load/save so file formatting is preserved."""
import sys, os
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from json_data import _load, _find_list, _save

# units.json: stats.naturalProt -> stats.toughness (keep field position)
path = 'data/units.json'
root = _load(path)
units, _, _ = _find_list(root, path)
n = 0
for u in units:
    stats = u.get('stats')
    if not stats or 'naturalProt' not in stats:
        continue
    # rebuild dict preserving key order, renaming in place
    u['stats'] = {('toughness' if k == 'naturalProt' else k): v for k, v in stats.items()}
    n += 1
_save(path, root)
print(f"units.json: renamed naturalProt->toughness on {n} units")

# buffs.json: effects[].stat "NaturalProt" -> "Toughness"
path = 'data/buffs.json'
root = _load(path)
buffs, _, _ = _find_list(root, path)
n = 0
for b in buffs:
    for eff in b.get('effects', []):
        if isinstance(eff, dict) and eff.get('stat') == 'NaturalProt':
            eff['stat'] = 'Toughness'
            n += 1
_save(path, root)
print(f"buffs.json: retargeted {n} effects NaturalProt->Toughness")
