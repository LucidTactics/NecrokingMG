"""Generate the StatBoxMain elements + children in the UI definition JSONs.

Spec source: Unit Tooltip2 scene dump (log/bag_inspect/tooltip2_tree.txt,
StatBoxMain section). Idempotent: removes existing ST_*/st_* entries first.
"""
import json

ICONS = {
    'HP':       ('assets/UI/Icons/NewIcons/Health24.png',          dict(t=[226, 169, 82], h=0, s=0.475, v=0, ot=0.6, oo=0.64)),
    'Spirit':   ('assets/UI/Icons/SturmIcons/Spirit2_24.png',      dict(t=None, ot=0.6, oo=0.64)),
    'Morale':   ('assets/UI/Icons/SturmIcons/morale2_24.png',      dict(t=None, ot=0.7, oo=0.47)),
    'Size':     ('assets/UI/Icons/NewIcons/Size324.png',           dict(t=[226, 169, 82], h=1, s=0.57, v=0, ot=0.6, oo=0.64)),
    'Tough':    ('assets/UI/Icons/NewIcons/Tough24.png',           dict(t=None, ot=0.6, oo=0.64)),
    'Magic':    ('assets/UI/Icons/NewIcons/MagicWand48.png',       dict(t=None, ot=0.7, oo=0.47)),
    'Strength': ('assets/UI/Icons/SturmIcons/SturmStrength24.png', dict(t=None, ot=0.5, oo=0.88)),
    'Prot':     ('assets/UI/Icons/NewIcons/Prot24.png',            dict(t=None, ot=0.6, oo=0.64)),
    'Coverage': ('assets/UI/Icons/NewIcons/Coverage24.png',        dict(t=None, ot=0.6, oo=0.64)),
    'Attack':   ('assets/UI/Icons/NewIcons/Attack24.png',          dict(t=[138, 136, 117], h=0, s=0.1, v=0.12, ot=0.4, oo=0.64)),
    'Defense':  ('assets/UI/Icons/NewIcons/Defense24.png',         dict(t=[120, 74, 22], h=0, s=0, v=0.3, ot=0.6, oo=0.64)),
    'Prec':     ('assets/UI/Icons/NewIcons/Prec24.png',            dict(t=None, ot=0.6, oo=0.64)),
    'Speed':    ('assets/UI/Icons/NewIcons/Speed24.png',           dict(t=None, ot=0.6, oo=0.64)),
    'Enc':      ('assets/UI/Icons/NewIcons/Enc24.png',             dict(t=None, ot=0.6, oo=0.64)),
    'Gold':     ('assets/UI/Icons/NewIcons/Gold24.png',            dict(t=None, ot=0.6, oo=0.64)),
}
# (label, value, iconKey, labelFontSize override) — 'Protectrion' typo is faithful to Unity
ROWS = [
    [('Hp', '10/10', 'HP', None), ('Spirit', '2', 'Spirit', None), ('Morale', '16/16', 'Morale', None)],
    [('Size', '2', 'Size', None), ('Toughness', '1', 'Tough', None), ('Magic Power', '2', 'Magic', 22)],
    [('Strength', '10', 'Strength', None), ('Protectrion', '16', 'Prot', None), ('Coverage', '80%', 'Coverage', None)],
    [('Attack', '12', 'Attack', None), ('Defense', '15', 'Defense', None), ('Precision', '10', 'Prec', None)],
    [('Speed', '8', 'Speed', None), ('Encumberance', '4', 'Enc', 21), ('Upkeep', '2', 'Gold', None)],
]
ROW_TOPS = [226, 253, 280, 307, 334]
CELL_LEFTS = [7, 160, 313]

LABEL_STYLE = dict(x=0, y=0, w=90, h=24, align='left', valign='center',
                   fontFamily='Quintessential', fontSize=24, fontColor=[242, 212, 170, 255],
                   bold=True, boldStrength=0.7, outlineWidth=1, outlineColor=[0, 0, 0, 150])
VALUE_STYLE = dict(x=0, y=0, w=54, h=24, align='right', valign='center',
                   fontFamily='Roboto', fontSize=25, fontColor=[213, 189, 151, 255],
                   bold=True, outlineWidth=1, outlineColor=[0, 0, 0, 140])


def harm(cfg):
    h = {}
    if cfg.get('t') is not None:
        h.update(targetColor=cfg['t'] + [255], hueStrength=cfg.get('h', 0),
                 satStrength=cfg.get('s', 0), valStrength=cfg.get('v', 0), useHcl=False)
    else:
        h.update(targetColor=[255, 255, 255, 255], hueStrength=0, satStrength=0, valStrength=0, useHcl=False)
    h.update(outlineColor=[255, 255, 255, 255], outlineThickness=cfg['ot'], outlineOpacity=cfg['oo'])
    return h


def main():
    ed = json.load(open('assets/UI/definitions/elements.json', encoding='utf-8'))
    ed['elements'] = [e for e in ed['elements'] if not e['id'].startswith('ST_')]
    els = ed['elements']
    for key, (path, cfg) in ICONS.items():
        els.append(dict(id='ST_Icon_' + key, type='image', imagePath=path, width=24, height=24,
                        tintColor=[255, 255, 255, 255], harmonize=harm(cfg)))
    els.append(dict(id='ST_Label', type='text', width=90, height=24, tintColor=[255, 255, 255, 0],
                    defaultText='Label', textRegion=dict(LABEL_STYLE)))
    els.append(dict(id='ST_Value', type='text', width=54, height=24, tintColor=[255, 255, 255, 0],
                    defaultText='0', textRegion=dict(VALUE_STYLE)))
    els.append(dict(id='ST_StatBox', type='image', imagePath='assets/UI/Ribbons/BlueSwath_statbox_463x137.png',
                    width=463, height=137, tintColor=[255, 255, 255, 56],
                    harmonize=dict(targetColor=[46, 41, 30, 255], hueStrength=1, satStrength=0.36, valStrength=0.33, useHcl=False)))
    els.append(dict(id='ST_RowSwatch', type='image', imagePath='assets/UI/Ribbons/BlueSwath_row_463x30.png',
                    width=463, height=30, tintColor=[255, 255, 255, 61],
                    harmonize=dict(targetColor=[20, 18, 14, 255], hueStrength=1, satStrength=0.36, valStrength=0.187, useHcl=False)))
    els.append(dict(id='ST_TitleSwatch', type='image', imagePath='assets/UI/Ribbons/Swatch1.3_459x27.png',
                    width=459, height=27, tintColor=[255, 255, 255, 230],
                    harmonize=dict(targetColor=[228, 143, 20, 255], hueStrength=1, satStrength=0.369, valStrength=0.412, useHcl=False, outlineColor=[0, 0, 0, 255], outlineThickness=0.6, outlineOpacity=1)))
    els.append(dict(id='ST_TitleHeraldry', type='image', imagePath='assets/UI/Background/heraldry_stats_strip.png',
                    width=457, height=26, tintColor=[178, 178, 178, 18],
                    harmonize=dict(targetColor=[255, 255, 255, 255], hueStrength=1, satStrength=1, valStrength=0.761, useHcl=False)))
    els.append(dict(id='ST_Gradient', type='image', imagePath='assets/UI/Fills/DiagnolFade_stats_456x144.png',
                    width=456, height=144, tintColor=[255, 255, 255, 18],
                    harmonize=dict(targetColor=[20, 12, 2, 255], hueStrength=1, satStrength=1, valStrength=1, useHcl=False)))
    els.append(dict(id='ST_BoxPattern', type='image', imagePath='assets/UI/Background/dragonpattern_stats_box.png',
                    width=456, height=134, tintColor=[178, 178, 178, 10],
                    harmonize=dict(targetColor=[231, 133, 71, 255], hueStrength=1, satStrength=0.29, valStrength=0.761, useHcl=False)))
    els.append(dict(id='ST_ColumnBar', type='image', imagePath='assets/UI/Bars/goldbar_v_3x154.png',
                    width=3, height=154, tintColor=[42, 41, 36, 116],
                    harmonize=dict(targetColor=[72, 58, 32, 255], hueStrength=1, satStrength=0.8, valStrength=0.36, useHcl=False,
                                   outlineColor=[0, 0, 0, 255], outlineThickness=0.5, outlineOpacity=1)))
    els.append(dict(id='ST_Title', type='text', width=463, height=38, tintColor=[255, 255, 255, 0],
                    defaultText='Stats', textRegion=dict(x=0, y=0, w=463, h=38, align='center', valign='bottom',
                    fontFamily='Quintessential', fontSize=34, fontColor=[237, 207, 167, 255],
                    bold=True, charSpacing=1, outlineWidth=1, outlineColor=[25, 14, 8, 230])))
    json.dump(ed, open('assets/UI/definitions/elements.json', 'w', encoding='utf-8'), indent=2)

    wd = json.load(open('assets/UI/definitions/widgets.json', encoding='utf-8'))
    win = next(w for w in wd['widgets'] if w['id'] == 'UnitTooltipWindow')
    win['children'] = [c for c in win['children'] if not c['name'].startswith('st_')]
    ch = win['children']

    def add(name, element, x, y, w, h, **extra):
        c = dict(name=name, element=element, x=x, y=y, width=w, height=h, anchor=0)
        c.update(extra)
        ch.append(c)

    add('st_box', 'ST_StatBox', 3, 223, 463, 137)
    for i, top in enumerate(ROW_TOPS):
        if i % 2 == 0:  # rows 1/3/5 have the dark zebra band (2/4 inactive in Unity)
            add('st_rowswatch%d' % i, 'ST_RowSwatch', 3, top - 3, 463, 30)
    for r, (top, cells) in enumerate(zip(ROW_TOPS, ROWS)):
        for c, ((label, value, icon, lsize), left) in enumerate(zip(cells, CELL_LEFTS)):
            add('st_r%dc%d_icon' % (r, c), 'ST_Icon_' + icon, left, top, 24, 24)
            extra = {}
            if lsize:  # auto-shrunk long labels (Magic Power, Encumberance)
                ov = dict(LABEL_STYLE)
                ov['fontSize'] = lsize
                extra['textOverride'] = ov
            add('st_r%dc%d_label' % (r, c), 'ST_Label', left + 27, top, 90, 24, defaultText=label, **extra)
            add('st_r%dc%d_value' % (r, c), 'ST_Value', left + 86, top, 54, 24, defaultText=value)
    add('st_col1', 'ST_ColumnBar', 154, 214, 3, 154)
    add('st_col2', 'ST_ColumnBar', 308, 214, 3, 154)
    add('st_title_swatch', 'ST_TitleSwatch', 4, 197, 459, 27)
    add('st_title_heraldry', 'ST_TitleHeraldry', 6, 198, 457, 26)
    add('st_gradient', 'ST_Gradient', 7, 215, 456, 144)
    add('st_boxpattern', 'ST_BoxPattern', 7, 224, 456, 134)
    add('st_title', 'ST_Title', 7, 192, 463, 38)
    json.dump(wd, open('assets/UI/definitions/widgets.json', 'w', encoding='utf-8'), indent=2)
    print('generated; UnitTooltipWindow children:', len(ch))


if __name__ == '__main__':
    main()
