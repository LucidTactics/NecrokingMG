"""Generate the AbilitiesAndBuffs footer elements + children (AB_/ab_ prefixes).

Spec source: Unit Tooltip2 scene dump, AbilitiesAndBuffs section (root top
y=682.85). Title bar + empty box only — Flag and X_CloseButton are INACTIVE
in the scene and therefore skipped.
"""
import json


def main():
    ed = json.load(open('assets/UI/definitions/elements.json', encoding='utf-8'))
    ed['elements'] = [e for e in ed['elements'] if not e['id'].startswith('AB_')]
    els = ed['elements']
    els.append(dict(id='AB_StatBox', type='image', imagePath='assets/UI/Ribbons/BlueSwath_abbox_463x35.png',
                    width=463, height=35, tintColor=[255, 255, 255, 255],
                    harmonize=dict(targetColor=[26, 27, 26, 255], hueStrength=1, satStrength=0.674, valStrength=0.774, useHcl=False)))
    els.append(dict(id='AB_Icon_Flying', type='image', imagePath='assets/UI/Abilities/Flying_24.png',
                    width=24, height=24, tintColor=[255, 255, 255, 255],
                    harmonize=dict(targetColor=[255, 255, 255, 255], hueStrength=0, satStrength=0.262, valStrength=0.137,
                                   useHcl=False, outlineColor=[255, 255, 255, 255], outlineThickness=0.75, outlineOpacity=0.33)))
    els.append(dict(id='AB_TitleSwatch', type='image', imagePath='assets/UI/Ribbons/Swatch1.3_457x29.png',
                    width=457, height=29, tintColor=[255, 255, 255, 230],
                    harmonize=dict(targetColor=[54, 80, 52, 255], hueStrength=1, satStrength=0.8, valStrength=0.46, useHcl=False, outlineColor=[0, 0, 0, 255], outlineThickness=0.6, outlineOpacity=1)))
    els.append(dict(id='AB_TitleHeraldry', type='image', imagePath='assets/UI/Background/heraldry_ab_strip.png',
                    width=456, height=29, tintColor=[178, 178, 178, 12],
                    harmonize=dict(targetColor=[255, 255, 255, 255], hueStrength=1, satStrength=1, valStrength=0.761, useHcl=False)))
    els.append(dict(id='AB_BoxPattern', type='image', imagePath='assets/UI/Background/nations_ab_box.png',
                    width=456, height=30, tintColor=[178, 178, 178, 10],
                    harmonize=dict(targetColor=[231, 133, 71, 255], hueStrength=1, satStrength=0.29, valStrength=0.761, useHcl=False)))
    els.append(dict(id='AB_Title', type='text', width=463, height=34, tintColor=[255, 255, 255, 0],
                    defaultText='Abilities & Buffs', textRegion=dict(x=0, y=0, w=463, h=34, align='center', valign='bottom',
                    fontFamily='Quintessential', fontSize=36, fontColor=[237, 207, 167, 255],
                    bold=True, charSpacing=2, outlineWidth=1, outlineColor=[25, 14, 8, 230])))
    json.dump(ed, open('assets/UI/definitions/elements.json', 'w', encoding='utf-8'), indent=2)

    wd = json.load(open('assets/UI/definitions/widgets.json', encoding='utf-8'))
    win = next(w for w in wd['widgets'] if w['id'] == 'UnitTooltipWindow')
    win['children'] = [c for c in win['children'] if not c['name'].startswith('ab_')]
    ch = win['children']

    def add(name, element, x, y, w, h, **extra):
        c = dict(name=name, element=element, x=x, y=y, width=w, height=h, anchor=0)
        c.update(extra)
        ch.append(c)

    add('ab_box', 'AB_StatBox', 3, 707, 463, 35)
    add('ab_boxpattern', 'AB_BoxPattern', 7, 710, 456, 30)
    add('ab_title_swatch', 'AB_TitleSwatch', 5, 680, 457, 29)
    add('ab_title_heraldry', 'AB_TitleHeraldry', 7, 680, 456, 29)
    add('ab_title', 'AB_Title', 7, 679, 463, 34)
    add('ab_r0_icon', 'AB_Icon_Flying', 7, 713, 24, 24)
    json.dump(wd, open('assets/UI/definitions/widgets.json', 'w', encoding='utf-8'), indent=2)
    print('generated; UnitTooltipWindow children:', len(ch))


if __name__ == '__main__':
    main()
