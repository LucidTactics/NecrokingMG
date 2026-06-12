"""Generate the AttacksBox elements + children in the UI definition JSONs.

Spec source: Unit Tooltip2 scene dump, AttacksBox section (root top y=583.6).
Idempotent (AT_/at_ prefixes). Reuses elements from the stats/equipment
generators: ST_Value, ST_RowSwatch, ST_ColumnBar, EQ_Name, EQ_Len,
EQ_Item_Sword, EQ_Icon_Slash, EQ_Icon_Pierce, EQ_Icon_AttackEq.
"""
import json

# rows: (name, [(value, slot)]) — all rows use the Sword item icon
ROWS = [
    ('Broadsword Slash', [('14', 'EQ_Icon_Slash'), ('14', 'EQ_Icon_AttackEq'), ('2', 'LEN'), ('4', 'AT_Icon_Fat')]),
    ('Broadsword Stab',  [('14', 'EQ_Icon_Pierce'), ('14', 'EQ_Icon_AttackEq'), ('2', 'LEN'), ('4', 'AT_Icon_Fat')]),
    ('Dagger Stab',      [('11', 'EQ_Icon_Pierce'), ('14', 'EQ_Icon_AttackEq'), ('2', 'LEN'), ('4', 'AT_Icon_Fat')]),
]
ROW_TOPS = [601, 628, 655]


def main():
    ed = json.load(open('assets/UI/definitions/elements.json', encoding='utf-8'))
    ed['elements'] = [e for e in ed['elements'] if not e['id'].startswith('AT_')]
    els = ed['elements']
    els.append(dict(id='AT_Icon_Fat', type='image', imagePath='assets/UI/Icons/SturmIcons/exhausted.2.24.png',
                    width=24, height=24, tintColor=[255, 255, 255, 255],
                    harmonize=dict(targetColor=[138, 136, 117, 255], hueStrength=0, satStrength=0.1, valStrength=0.12,
                                   useHcl=False, outlineColor=[255, 255, 255, 255], outlineThickness=0.4, outlineOpacity=0.64)))
    els.append(dict(id='AT_RowSwatch', type='image', imagePath='assets/UI/Ribbons/BlueSwath_row_463x30.png',
                    width=463, height=30, tintColor=[255, 255, 255, 42],
                    harmonize=dict(targetColor=[5, 8, 11, 255], hueStrength=1, satStrength=0.591, valStrength=0.142, useHcl=False)))
    els.append(dict(id='AT_StatBox', type='image', imagePath='assets/UI/Ribbons/BlueSwath_atbox_463x83.png',
                    width=463, height=83, tintColor=[255, 255, 255, 115],
                    harmonize=dict(targetColor=[20, 22, 25, 255], hueStrength=1, satStrength=0.36, valStrength=0.269, useHcl=False)))
    els.append(dict(id='AT_TitleSwatch', type='image', imagePath='assets/UI/Ribbons/Swatch1.3_458x28.png',
                    width=458, height=28, tintColor=[255, 255, 255, 230],
                    harmonize=dict(targetColor=[85, 92, 136, 255], hueStrength=1, satStrength=0.8, valStrength=0.46, useHcl=False, outlineColor=[0, 0, 0, 255], outlineThickness=0.6, outlineOpacity=1)))
    els.append(dict(id='AT_TitleHeraldry', type='image', imagePath='assets/UI/Background/heraldry_at_strip.png',
                    width=456, height=26, tintColor=[178, 178, 178, 12],
                    harmonize=dict(targetColor=[255, 255, 255, 255], hueStrength=1, satStrength=1, valStrength=0.761, useHcl=False)))
    els.append(dict(id='AT_BoxPattern', type='image', imagePath='assets/UI/Background/nations_at_box.png',
                    width=456, height=80, tintColor=[178, 178, 178, 10],
                    harmonize=dict(targetColor=[231, 133, 71, 255], hueStrength=1, satStrength=0.29, valStrength=0.761, useHcl=False)))
    els.append(dict(id='AT_Title', type='text', width=463, height=34, tintColor=[255, 255, 255, 0],
                    defaultText='Attacks', textRegion=dict(x=0, y=0, w=463, h=34, align='center', valign='bottom',
                    fontFamily='Quintessential', fontSize=36, fontColor=[237, 207, 167, 255],
                    bold=True, charSpacing=2, outlineWidth=1, outlineColor=[25, 14, 8, 230])))
    json.dump(ed, open('assets/UI/definitions/elements.json', 'w', encoding='utf-8'), indent=2)

    wd = json.load(open('assets/UI/definitions/widgets.json', encoding='utf-8'))
    win = next(w for w in wd['widgets'] if w['id'] == 'UnitTooltipWindow')
    win['children'] = [c for c in win['children'] if not c['name'].startswith('at_')]
    ch = win['children']

    def add(name, element, x, y, w, h, **extra):
        c = dict(name=name, element=element, x=x, y=y, width=w, height=h, anchor=0)
        c.update(extra)
        ch.append(c)

    add('at_box', 'AT_StatBox', 3, 600, 463, 83)
    add('at_boxpattern', 'AT_BoxPattern', 7, 601, 456, 80)
    for i in (0, 2):  # zebra rows (row 1 swatch INACTIVE)
        add('at_rowswatch%d' % i, 'AT_RowSwatch', 3, ROW_TOPS[i] - 3, 463, 30)
    for r, (top, (name, slots)) in enumerate(zip(ROW_TOPS, ROWS)):
        add('at_r%d_icon' % r, 'EQ_Item_Sword', 7, top, 24, 24)
        add('at_r%d_name' % r, 'EQ_Name', 31, top - 2, 186, 27, defaultText=name)
        for k, (value, slot) in enumerate(slots):
            add('at_r%dv%d' % (r, k), 'ST_Value', 217 + 60 * k, top, 36, 24, defaultText=value)
            if slot == 'LEN':
                add('at_r%ds%d' % (r, k), 'EQ_Len', 253 + 60 * k, top, 24, 24)
            else:
                add('at_r%ds%d' % (r, k), slot, 253 + 60 * k, top, 24, 24)
    add('at_col1', 'ST_ColumnBar', 224, 601, 3, 90)
    add('at_col2', 'ST_ColumnBar', 282, 599, 3, 91)
    add('at_col3', 'ST_ColumnBar', 344, 599, 3, 92)
    add('at_col4', 'ST_ColumnBar', 405, 598, 3, 93)
    add('at_title_swatch', 'AT_TitleSwatch', 5, 573, 458, 28)
    add('at_title_heraldry', 'AT_TitleHeraldry', 7, 575, 456, 26)
    add('at_title', 'AT_Title', 7, 574, 463, 34)
    json.dump(wd, open('assets/UI/definitions/widgets.json', 'w', encoding='utf-8'), indent=2)
    print('generated; UnitTooltipWindow children:', len(ch))


if __name__ == '__main__':
    main()
