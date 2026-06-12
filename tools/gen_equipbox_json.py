"""Generate the EquipmentBox elements + children in the UI definition JSONs.

Spec source: Unit Tooltip2 scene dump, EquipmentBox section (root top y=357).
Idempotent: removes existing EQ_*/eq_* entries first. Reuses ST_Value/ST_RowSwatch/
ST_ColumnBar element defs from gen_statbox_json.py output.
"""
import json

# slot icon elements: name -> (path, harmonize cfg)
SLOT_ICONS = {
    'Slash':    ('assets/UI/Icons/DamageTypes/Slash_24.png',  dict(t=[48, 9, 7], h=1, s=0.481, v=0.812, ot=2, oo=0.4, oc=[221, 166, 166, 255])),
    'Pierce':   ('assets/UI/Icons/DamageTypes/Pierce_24.png', dict(t=[48, 9, 7], h=1, s=0.481, v=0.812, ot=2, oo=0.4, oc=[221, 166, 166, 255])),
    'ProtEq':   ('assets/UI/Icons/NewIcons/Prot24.png',       dict(t=None, ot=1, oo=0.4, oc=[221, 166, 166, 255])),
    'Parry':    ('assets/UI/Icons/NewIcons/Parry24.png',      dict(t=None, ot=1, oo=0.256)),
    'EncEq':    ('assets/UI/Icons/NewIcons/Enc24.png',        dict(t=None, ot=1, oo=0.256)),
    'AttackEq': ('assets/UI/Icons/NewIcons/Attack24.png',     dict(t=[138, 136, 117], h=0, s=0.1, v=0.12, ot=0.4, oo=0.64)),
    'DefEq':    ('assets/UI/Icons/NewIcons/Defense24.png',    dict(t=[138, 136, 117], h=0, s=0.1, v=0.12, ot=0.4, oo=0.64)),
    'CovEq':    ('assets/UI/Icons/NewIcons/Coverage24.png',   dict(t=[138, 136, 117], h=0, s=0.1, v=0.12, ot=0.4, oo=0.64)),
}
ITEM_ICON_CFG = dict(t=[226, 169, 82], h=0, s=0.475, v=0, ot=0.6, oo=0.64)
ITEM_ICONS = {
    'Sword':  'assets/UI/Icons/Equipment/Sword_24.png',
    'Shield': 'assets/UI/Icons/Equipment/Shield_24.png',
    'Helmet': 'assets/UI/Icons/Equipment/Helmet_24.png',
    'Chest':  'assets/UI/Icons/Equipment/ChestArmor_24.png',
    'Boot':   'assets/UI/Icons/Equipment/Boot_24.png',
    'Ring':   'assets/UI/Icons/Equipment/Ring_24.png',
}
# rows: (name, itemIcon, [(value, slot)]) — slot 'LEN' = the " Len" text; '%': small font
ROWS = [
    ('Broad Sword',   'Sword',  [('4', 'Slash'), ('2', 'AttackEq'), ('2', 'LEN'), ('1', 'DefEq')]),
    ('Kite Shield',   'Shield', [('00', 'ProtEq'), ('00', 'Parry'), ('00', 'EncEq'), ('00', 'DefEq')]),
    ('Templar Helm',  'Helmet', [('00', 'ProtEq'), ('100%', 'CovEq'), ('00', 'EncEq'), ('00', 'DefEq')]),
    ('Chainmail',     'Chest',  [('12', 'ProtEq'), ('100%', 'CovEq'), ('00', 'EncEq'), ('00', 'DefEq')]),
    ('Leather Boots', 'Boot',   [('12', 'ProtEq'), ('100%', 'CovEq'), ('00', 'EncEq'), ('00', 'DefEq')]),
    ('Dagger',        'Ring',   [('1', 'Pierce'), ('4', 'AttackEq'), ('0', 'LEN'), ('3', 'DefEq')]),
    ('(Empty)',       'Ring',   []),
]
ROW_TOPS = [386, 413, 440, 467, 494, 521, 548]
VALUE_SMALL = dict(x=0, y=0, w=36, h=24, align='right', valign='center',
                   fontFamily='Roboto', fontSize=17, fontColor=[213, 189, 151, 255],
                   bold=True, outlineWidth=1, outlineColor=[0, 0, 0, 140])


def harm(cfg):
    h = {}
    if cfg.get('t') is not None:
        h.update(targetColor=cfg['t'] + [255], hueStrength=cfg.get('h', 0),
                 satStrength=cfg.get('s', 0), valStrength=cfg.get('v', 0), useHcl=False)
    else:
        h.update(targetColor=[255, 255, 255, 255], hueStrength=0, satStrength=0, valStrength=0, useHcl=False)
    h.update(outlineColor=cfg.get('oc', [255, 255, 255, 255]), outlineThickness=cfg['ot'], outlineOpacity=cfg['oo'])
    return h


def main():
    ed = json.load(open('assets/UI/definitions/elements.json', encoding='utf-8'))
    ed['elements'] = [e for e in ed['elements'] if not e['id'].startswith('EQ_')]
    els = ed['elements']
    for key, (path, cfg) in SLOT_ICONS.items():
        els.append(dict(id='EQ_Icon_' + key, type='image', imagePath=path, width=24, height=24,
                        tintColor=[255, 255, 255, 255], harmonize=harm(cfg)))
    for key, path in ITEM_ICONS.items():
        els.append(dict(id='EQ_Item_' + key, type='image', imagePath=path, width=24, height=24,
                        tintColor=[255, 255, 255, 255], harmonize=harm(ITEM_ICON_CFG)))
    els.append(dict(id='EQ_Name', type='text', width=186, height=27, tintColor=[255, 255, 255, 0],
                    defaultText='Item', textRegion=dict(x=0, y=0, w=186, h=27, align='left', valign='center',
                    fontFamily='Quintessential', fontSize=27, fontColor=[242, 212, 170, 255],
                    bold=True, boldStrength=0.7, outlineWidth=1, outlineColor=[0, 0, 0, 150])))
    els.append(dict(id='EQ_Len', type='text', width=24, height=24, tintColor=[255, 255, 255, 0],
                    defaultText=' Len', textRegion=dict(x=0, y=0, w=24, h=24, align='left', valign='center',
                    fontFamily='Quintessential', fontSize=20, fontColor=[242, 212, 170, 255],
                    bold=True, boldStrength=0.7, outlineWidth=1, outlineColor=[0, 0, 0, 150])))
    els.append(dict(id='EQ_StatBox', type='image', imagePath='assets/UI/Ribbons/BlueSwath_eqbox_463x198.png',
                    width=463, height=198, tintColor=[255, 255, 255, 116],
                    harmonize=dict(targetColor=[27, 22, 21, 255], hueStrength=1, satStrength=0.36, valStrength=0.368, useHcl=False)))
    els.append(dict(id='EQ_RowSwatch27', type='image', imagePath='assets/UI/Ribbons/BlueSwath_row_463x27.png',
                    width=463, height=27, tintColor=[255, 255, 255, 53],
                    harmonize=dict(targetColor=[16, 13, 12, 255], hueStrength=1, satStrength=0.36, valStrength=0.192, useHcl=False)))
    els.append(dict(id='EQ_RowSwatch30', type='image', imagePath='assets/UI/Ribbons/BlueSwath_row_463x30.png',
                    width=463, height=30, tintColor=[255, 255, 255, 53],
                    harmonize=dict(targetColor=[16, 13, 12, 255], hueStrength=1, satStrength=0.36, valStrength=0.192, useHcl=False)))
    els.append(dict(id='EQ_TitleSwatch', type='image', imagePath='assets/UI/Ribbons/Swatch1.3_458x30.png',
                    width=458, height=30, tintColor=[255, 255, 255, 230],
                    harmonize=dict(targetColor=[163, 78, 78, 255], hueStrength=1, satStrength=0.8, valStrength=0.46, useHcl=False, outlineColor=[0, 0, 0, 255], outlineThickness=0.6, outlineOpacity=1)))
    els.append(dict(id='EQ_TitleHeraldry', type='image', imagePath='assets/UI/Background/heraldry_eq_strip.png',
                    width=455, height=28, tintColor=[178, 178, 178, 12],
                    harmonize=dict(targetColor=[255, 255, 255, 255], hueStrength=1, satStrength=1, valStrength=0.761, useHcl=False)))
    els.append(dict(id='EQ_BoxPattern', type='image', imagePath='assets/UI/Background/nations_eq_box.png',
                    width=456, height=187, tintColor=[178, 178, 178, 10],
                    harmonize=dict(targetColor=[231, 133, 71, 255], hueStrength=1, satStrength=0.29, valStrength=0.761, useHcl=False)))
    els.append(dict(id='EQ_Title', type='text', width=463, height=34, tintColor=[255, 255, 255, 0],
                    defaultText='Equipment', textRegion=dict(x=0, y=0, w=463, h=34, align='center', valign='bottom',
                    fontFamily='Quintessential', fontSize=36, fontColor=[237, 207, 167, 255],
                    bold=True, charSpacing=2, outlineWidth=1, outlineColor=[25, 14, 8, 230])))
    json.dump(ed, open('assets/UI/definitions/elements.json', 'w', encoding='utf-8'), indent=2)

    wd = json.load(open('assets/UI/definitions/widgets.json', encoding='utf-8'))
    win = next(w for w in wd['widgets'] if w['id'] == 'UnitTooltipWindow')
    win['children'] = [c for c in win['children'] if not c['name'].startswith('eq_')]
    ch = win['children']

    def add(name, element, x, y, w, h, **extra):
        c = dict(name=name, element=element, x=x, y=y, width=w, height=h, anchor=0)
        c.update(extra)
        ch.append(c)

    add('eq_box', 'EQ_StatBox', 3, 385, 463, 198)
    add('eq_boxpattern', 'EQ_BoxPattern', 7, 387, 456, 187)
    # zebra rows 0,2,4,6 (odd rows' swatches INACTIVE in Unity); row 0 is 27 tall
    add('eq_rowswatch0', 'EQ_RowSwatch27', 3, 385, 463, 27)
    for i in (2, 4, 6):
        add('eq_rowswatch%d' % i, 'EQ_RowSwatch30', 3, ROW_TOPS[i] - 3, 463, 30)
    for r, (top, (name, item, slots)) in enumerate(zip(ROW_TOPS, ROWS)):
        add('eq_r%d_icon' % r, 'EQ_Item_' + item, 7, top, 24, 24)
        add('eq_r%d_name' % r, 'EQ_Name', 31, top - 2, 186, 27, defaultText=name)
        for k, (value, slot) in enumerate(slots):
            vextra = {}
            if value.endswith('%'):
                vextra['textOverride'] = dict(VALUE_SMALL)
            add('eq_r%dv%d' % (r, k), 'ST_Value', 217 + 60 * k, top, 36, 24, defaultText=value, **vextra)
            if slot == 'LEN':
                add('eq_r%ds%d' % (r, k), 'EQ_Len', 253 + 60 * k, top, 24, 24)
            else:
                add('eq_r%ds%d' % (r, k), 'EQ_Icon_' + slot, 253 + 60 * k, top, 24, 24)
    add('eq_col1', 'ST_ColumnBar', 224, 387, 3, 200)
    add('eq_col2', 'ST_ColumnBar', 282, 383, 3, 203)
    add('eq_col3', 'ST_ColumnBar', 344, 382, 3, 205)
    add('eq_col4', 'ST_ColumnBar', 405, 381, 3, 206)
    add('eq_title_swatch', 'EQ_TitleSwatch', 5, 357, 458, 30)
    add('eq_title_heraldry', 'EQ_TitleHeraldry', 7, 359, 455, 28)
    add('eq_title', 'EQ_Title', 7, 359, 463, 34)
    json.dump(wd, open('assets/UI/definitions/widgets.json', 'w', encoding='utf-8'), indent=2)
    print('generated; UnitTooltipWindow children:', len(ch))


if __name__ == '__main__':
    main()
