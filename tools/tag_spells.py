"""Tag data/spells.json with grimoire fields: school (tab), tileTemplate, icon,
hidden. Mapping deduced from the GodMenu3 tile examples:
  Summon Tile    -> summons/reanimates (target-unit icon on the right)
  Evocation Tile -> damage dealers (damage number + AN/MRN tags)
  Buff Tile      -> friendly buffs/toggles ("Buff:" + icon)
  Debuff Tile    -> hostile non-damage effects ("Debuff:" + icon)
Idempotent — recomputes all four fields every run.
"""
import json
import os

SCHOOL = {
    'Summon': 'Conjuration',
    'Buff': 'Alteration', 'Toggle': 'Alteration',
    'Projectile': 'Evocation', 'Strike': 'Evocation', 'Beam': 'Evocation',
    'Drain': 'Evocation', 'Cloud': 'Evocation',
}
# Per-id overrides
SCHOOL_OVR = {
    'skeleton_warrior_upgrade': 'Construction',
    'skeleton_warrior_upgrade_copy': 'Construction',
    'poison_cloud': 'Alteration', 'paralyze_burst': 'Alteration',
}
HIDDEN = {'order_attack', 'debug_militia_summon', 'debug_militia_summon_copy', 'debug_skeleton_summon'}


def template(s):
    cat, dmg = s.get('category', ''), s.get('damage') or 0
    if cat == 'Summon':
        return 'summon'
    if cat in ('Buff', 'Toggle'):
        return 'buff'
    if dmg > 0:
        return 'evocation'
    return 'debuff'  # non-damaging hostile effects (paralyze, miasma)


def main():
    d = json.load(open('data/spells.json', encoding='utf-8'))
    for s in d['spells']:
        sid = s['id']
        s['hidden'] = sid in HIDDEN
        s['school'] = SCHOOL_OVR.get(sid, SCHOOL.get(s.get('category', ''), 'Evocation'))
        s['tileTemplate'] = template(s)
        icon = f'assets/UI/Icons/Spells/{sid}.png'
        s['icon'] = icon if not s['hidden'] else ''
        print(f"{sid:34s} {s['school']:12s} {s['tileTemplate']:10s} hidden={s['hidden']}")
    json.dump(d, open('data/spells.json', 'w', encoding='utf-8'), indent=2)
    print('tagged', len(d['spells']), 'spells')


if __name__ == '__main__':
    main()
