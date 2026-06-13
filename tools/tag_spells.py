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

# Magic path per spell. Death-majority (it's a necromancer), with thematic
# exceptions so the path-filter has spread to test: lightning -> shock,
# living/poison summons -> nature, holy/fortune -> heavens, ethereal -> spirit,
# hardening/petrify -> earth. (User asked us to guess per spell.)
PATH = {
    'fireball': 'death', 'fireball_kb': 'death', 'nether_darts': 'death',
    'summon_skeleton': 'death', 'summon_abomination': 'death',
    'summon_skeleton_copy': 'nature',          # Summon Deer
    'summon_skeleton_copy_copy': 'nature',     # Summon Wolf
    'summon_skeleton_copy_copy_copy': 'death', # Summon Wolf Zombie
    'skeleton_warrior_upgrade': 'death', 'skeleton_warrior_upgrade_copy': 'death',
    'sky_lightning': 'shock', 'lightning_zap': 'shock', 'lightning_beam': 'shock',
    'spell_9': 'earth',         # Iron Skin (Dominions Ironskin = Earth)
    'spell_9_copy': 'shock',    # Quickness
    'spell_11': 'heavens',      # Lucky (fortune)
    'spell_12': 'death',        # Lifedrain
    'raise_zombie': 'death', 'raise_zombie_throng': 'death', 'life_drain': 'death',
    'god_ray': 'heavens', 'ghost_mode': 'spirit',
    'poison_cloud': 'death',    # Necromantic Miasma
    'poison_burst': 'nature', 'paralyze_burst': 'earth',
    'poison_berries_poison': 'nature', 'poison_berries_paralysis': 'nature',
    'reanimate_imbue': 'death', 'reanimate_raise': 'death',
}


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
        if sid in PATH:
            s['primaryPath'] = PATH[sid]
            if s.get('primaryLevel', 0) < 1:
                s['primaryLevel'] = 1
        icon = f'assets/UI/Icons/Spells/{sid}.png'
        s['icon'] = icon if not s['hidden'] else ''
        print(f"{sid:34s} {s['school']:12s} {s['tileTemplate']:10s} path={s.get('primaryPath',''):8s} hidden={s['hidden']}")
    json.dump(d, open('data/spells.json', 'w', encoding='utf-8'), indent=2)
    print('tagged', len(d['spells']), 'spells')


if __name__ == '__main__':
    main()
