"""Generate CommanderEquipWindow + StatTooltipWindow widgets and their elements.

Spec: log/bag_inspect/commander_box_tree.txt + stat_tooltip_tree.txt, with
geometry for INACTIVE Unity sections (EquipmentBox, Horiz Tabs) fitted to the
user's reference screenshots (serialized rects for inactive nodes are stale).
Idempotent: CB_/TT_ element prefixes, widget ids replaced wholesale.
"""
import json

WARM_TITLE = [237, 207, 167, 255]
WARM_BODY = [242, 212, 170, 255]


def harm(t=None, h=0, s=0.0, v=0.0, oc=None, ot=0.0, oo=0.0, grad=False):
    d = dict(targetColor=(t or [255, 255, 255]) + [255], hueStrength=h, satStrength=s, valStrength=v, useHcl=False)
    if oc is not None:
        d.update(outlineColor=oc, outlineThickness=ot, outlineOpacity=oo)
    if grad:
        d.update(gradColor=[18, 15, 11, 112], gradStrength=0.992)
    return d


# slots: (x, y, label, iconKey, iconAlpha)
SLOTS = [
    (114, 64, 'Helm', 'Helmet4', 91),
    (215, 64, 'Armor', 'ChestArmor3', 99),
    (215, 162, 'Off-Hand', 'Shield3', 80),
    (215, 261, 'Misc', 'Ring2', 113),
    (114, 261, 'Boots', 'Boots2', 99),
    (19, 261, 'Misc', 'Ring2', 113),
    (19, 162, 'Misc', 'Sword2', 128),
]
# Icon outline verbatim from Unity (white 233, th 0.95, op 0.812); the per-slot
# m_Color alpha multiplies the whole draw, matching Unity's vertex alpha.
SLOT_ICON_HARM = {
    'Helmet4': harm(oc=[233, 233, 233, 255], ot=0.95, oo=0.812),
    'ChestArmor3': harm(oc=[233, 233, 233, 255], ot=0.95, oo=0.812),
    'Shield3': harm(oc=[233, 233, 233, 255], ot=0.87, oo=0.812),
    'Ring2': harm(t=[20, 44, 79], h=0, s=0.517, v=0.258, oc=[233, 233, 233, 255], ot=0.95, oo=0.812),
    'Boots2': harm(oc=[233, 233, 233, 255], ot=0.95, oo=0.812),
    'Sword2': harm(oc=[233, 233, 233, 255], ot=0.95, oo=0.812),
}
SLOT_ICON_ALPHA = {'Helmet4': 91, 'ChestArmor3': 99, 'Shield3': 80, 'Ring2': 113, 'Boots2': 99, 'Sword2': 128}
# buttons: (x, w, label, iconKey)
BUTTONS = [(-5, 108, 'Stats', 'Stats'), (100, 101, 'Job', 'Attack2'), (198, 114, 'Deploy', 'Attack')]
BTN_Y = 350


def main():
    ed = json.load(open('assets/UI/definitions/elements.json', encoding='utf-8'))
    ed['elements'] = [e for e in ed['elements'] if not (e['id'].startswith('CB_') or e['id'].startswith('TT_') or e['id'].startswith('RT_'))]
    els = ed['elements']

    # ---- Commander box elements ----
    els.append(dict(id='CB_Blacksmith', type='image', imagePath='assets/UI/Background/blacksmith_cb_287x246.png',
                    width=287, height=246, tintColor=[255, 255, 255, 26],
                    harmonize=harm(t=[132, 100, 69], h=1, s=0.191, v=0.267)))
    els.append(dict(id='CB_TitleSwatch', type='image', imagePath='assets/UI/Ribbons/Swatch1.3_304x56.png',
                    width=304, height=56, tintColor=[255, 255, 255, 230],
                    harmonize=harm(t=[71, 76, 106], h=1, s=0.8, v=0.46)))
    # Unity's swatch has a scalloped dark edge hanging over the leather at the
    # banner/bar seam (row 58); our nine-slice squash absorbs it. Explicit 1px
    # seam line calibrated to the capture (35,31,26).
    els.append(dict(id='CB_SeamLine', type='image', imagePath='assets/UI/Misc/white4.png',
                    width=4, height=4, tintColor=[25, 18, 12, 160]))
    els.append(dict(id='CB_TitleBar', type='image', imagePath='assets/UI/Bars/RenaiThinBar_304x5.png',
                    width=304, height=5, tintColor=[255, 255, 255, 230],
                    harmonize=harm(t=[118, 97, 60], h=1, s=0.75, v=0.665)))
    # Title recipe = the proven UD title pattern (face + dark copy at +2,+2),
    # size calibrated to the capture's glyph bbox (w 174, Unity TMP 70 x 0.65).
    els.append(dict(id='CB_Title', type='text', width=304, height=55, tintColor=[255, 255, 255, 0],
                    defaultText='Templar', textRegion=dict(x=0, y=0, w=304, h=55, align='center', valign='center',
                    fontFamily='Quintessential', fontSize=75, fontColor=[245, 223, 182, 255],
                    bold=True, charSpacing=2, outlineWidth=2, outlineColor=[25, 14, 8, 230])))
    els.append(dict(id='CB_TitleShadow', type='text', width=304, height=55, tintColor=[255, 255, 255, 0],
                    defaultText='Templar', textRegion=dict(x=0, y=0, w=304, h=55, align='center', valign='center',
                    fontFamily='Quintessential', fontSize=75, fontColor=[25, 14, 8, 255],
                    bold=True, charSpacing=2)))
    els.append(dict(id='CB_SlotTex', type='image', imagePath='assets/UI/Frames/CF2I_75x75.png',
                    width=75, height=75, tintColor=[255, 255, 255, 255],
                    harmonize=harm(t=[121, 99, 54], h=1, s=1, v=0.325)))
    els.append(dict(id='CB_SlotStencil', type='image', imagePath='assets/UI/Patterns/Thatch_75x75.png',
                    width=75, height=75, tintColor=[255, 255, 255, 22],
                    harmonize=harm(t=[81, 66, 36], h=1, s=1, v=0.233)))
    els.append(dict(id='CB_SlotBorder', type='image', imagePath='assets/UI/Frames/Cloth_75x75.png',
                    width=75, height=75, tintColor=[152, 152, 152, 255],
                    harmonize=harm(t=[123, 100, 55], h=1, s=1, v=0.302, oc=[55, 25, 9, 255], ot=1.56, oo=0.257)))
    els.append(dict(id='CB_SlotHighlight', type='image', imagePath='assets/UI/Frames/Cloth_66x64.png',
                    width=66, height=64, tintColor=[255, 255, 255, 96],
                    harmonize=harm(t=[38, 33, 23], h=1, s=1, v=0.052, oc=[2, 1, 0, 255], ot=1.87, oo=0.456)))
    for key in ('Helmet4', 'ChestArmor3', 'Shield3', 'Ring2', 'Boots2', 'Sword2'):
        els.append(dict(id='CB_Icon_' + key, type='image', imagePath=f'assets/UI/Icons/Equipment/{key}.png',
                        width=48, height=48, tintColor=[137, 137, 137, SLOT_ICON_ALPHA[key]],
                        harmonize=SLOT_ICON_HARM[key]))
    els.append(dict(id='CB_SlotLabel', type='text', width=97, height=26, tintColor=[255, 255, 255, 0],
                    defaultText='Slot', textRegion=dict(x=0, y=0, w=97, h=26, align='center', valign='center',
                    fontFamily='Quintessential', fontSize=28, fontColor=WARM_BODY,
                    bold=True, boldStrength=0.7, outlineWidth=1, outlineColor=[0, 0, 0, 150])))
    els.append(dict(id='CB_UnitShadow', type='image', imagePath='assets/UI/Misc/Diffuse_149x34.png',
                    width=149, height=34, tintColor=[0, 0, 0, 141]))
    # Outline pre-baked in texture space by bake_extra_panels.py (see there)
    els.append(dict(id='CB_Unit', type='image', imagePath='assets/UI/Portraits/SampleUnit_88x91.png',
                    width=88, height=91, tintColor=[255, 255, 255, 255]))
    for w in (108, 101, 114):
        els.append(dict(id=f'CB_BtnTex{w}', type='image', imagePath=f'assets/UI/Frames/CF2I_{w}x60.png',
                        width=w, height=60, tintColor=[255, 255, 255, 255],
                        harmonize=harm(t=[67, 55, 30], h=1, s=1, v=0.5)))
        els.append(dict(id=f'CB_BtnBorder{w}', type='image', imagePath=f'assets/UI/Frames/Cloth_{w}x60.png',
                        width=w, height=60, tintColor=[152, 152, 152, 255],
                        harmonize=harm(t=[123, 100, 55], h=1, s=1, v=0.5, oc=[0, 0, 0, 255], ot=1.56, oo=0.133)))
    for key in ('Stats', 'Attack2', 'Attack'):
        els.append(dict(id='CB_BtnIcon_' + key, type='image', imagePath=f'assets/UI/Icons/Actions/{key}_39x36.png',
                        width=39, height=36, tintColor=[255, 255, 255, 242],
                        harmonize=harm(oc=[255, 255, 255, 255], ot=0.7, oo=0.24)))
    els.append(dict(id='CB_BtnLabel', type='text', width=62, height=36, tintColor=[255, 255, 255, 0],
                    defaultText='Btn', textRegion=dict(x=0, y=0, w=62, h=36, align='left', valign='center',
                    fontFamily='Quintessential', fontSize=28, fontColor=WARM_BODY,
                    bold=True, boldStrength=0.7, outlineWidth=1, outlineColor=[0, 0, 0, 150])))

    # ---- Stat tooltip elements ----
    els.append(dict(id='TT_GoldLine', type='image', imagePath='assets/UI/Misc/white4.png',
                    width=4, height=4, tintColor=[255, 207, 118, 102]))
    els.append(dict(id='TT_GoldLineBright', type='image', imagePath='assets/UI/Misc/white4.png',
                    width=4, height=4, tintColor=[255, 207, 118, 154]))
    els.append(dict(id='TT_StrengthIcon', type='image', imagePath='assets/UI/Icons/SturmIcons/SturmStrength_36.png',
                    width=36, height=36, tintColor=[255, 255, 255, 255],
                    harmonize=harm(oc=[51, 40, 40, 255], ot=0.5, oo=0.88)))
    els.append(dict(id='TT_Underline', type='image', imagePath='assets/UI/Bars/goldbar_h_173x3.png',
                    width=173, height=3, tintColor=[42, 41, 36, 120],
                    harmonize=harm(t=[72, 58, 32], h=1, s=0.8, v=0.36, oc=[0, 0, 0, 255], ot=0.5, oo=1)))
    els.append(dict(id='TT_Title', type='text', width=178, height=31, tintColor=[255, 255, 255, 0],
                    defaultText='Strength', textRegion=dict(x=0, y=0, w=178, h=31, align='left', valign='center',
                    fontFamily='Quintessential', fontSize=31, fontColor=[245, 217, 180, 255],
                    bold=True, outlineWidth=1, outlineColor=[25, 14, 8, 255])))
    els.append(dict(id='TT_Desc', type='text', width=204, height=61, tintColor=[255, 255, 255, 0],
                    defaultText='Strength is raw physical power and increases most melee attacks (and some ranged). Its also used for various effect checks (knockdown).',
                    textRegion=dict(x=0, y=0, w=204, h=61, align='left', valign='center',
                    fontFamily='Quintessential', fontSize=17, fontColor=[250, 224, 186, 255],
                    bold=True, boldStrength=0.6, wordWrap=True, lineSpacing=-3,
                    outlineWidth=1, outlineColor=[0, 0, 0, 150])))

    # ---- Resource HUD tooltip elements ----
    els.append(dict(id='RT_HumansIcon', type='image', imagePath='assets/UI/Icons/Population/Humans_36.png',
                    width=36, height=36, tintColor=[255, 255, 255, 255],
                    harmonize=harm(oc=[51, 40, 40, 255], ot=0.5, oo=0.88)))
    els.append(dict(id='RT_Title', type='text', width=143, height=31, tintColor=[255, 255, 255, 0],
                    defaultText='Human Population', textRegion=dict(x=0, y=0, w=143, h=31, align='left', valign='center',
                    fontFamily='Quintessential', fontSize=24, fontColor=[245, 217, 180, 255],
                    bold=True, charSpacing=1.4, outlineWidth=1, outlineColor=[25, 14, 8, 255])))
    els.append(dict(id='RT_HeaderValue', type='text', width=30, height=24, tintColor=[255, 255, 255, 0],
                    defaultText='20', textRegion=dict(x=0, y=0, w=30, h=24, align='right', valign='center',
                    fontFamily='Roboto', fontSize=30, fontColor=[88, 130, 90, 255],
                    bold=True, outlineWidth=1, outlineColor=[0, 0, 0, 140])))
    els.append(dict(id='RT_Parchment', type='image', imagePath='assets/UI/Patterns/Parchment_205x123.png',
                    width=205, height=123, tintColor=[255, 255, 255, 255],
                    harmonize=harm(t=[7, 6, 3], h=0, s=0.438, v=0.787)))
    # BoxStencil swapper strengths are all 0 -> raw sprite, tint alpha 10 only
    els.append(dict(id='RT_BoxStencil', type='image', imagePath='assets/UI/Patterns/Thatch_206x123.png',
                    width=206, height=123, tintColor=[255, 255, 255, 10]))
    els.append(dict(id='RT_BoxFrame', type='image', imagePath='assets/UI/Frames/Renai_207x125.png',
                    width=207, height=125, tintColor=[255, 255, 255, 255],
                    harmonize=harm(t=[150, 134, 107], h=1, s=0.266, v=0.466)))
    els.append(dict(id='RT_Label', type='text', width=167, height=24, tintColor=[255, 255, 255, 0],
                    defaultText='Label', textRegion=dict(x=0, y=0, w=167, h=24, align='right', valign='center',
                    fontFamily='Quintessential', fontSize=24, fontColor=[248, 219, 184, 255],
                    bold=True, boldStrength=0.7, charSpacing=1.25, outlineWidth=1, outlineColor=[0, 0, 0, 150])))
    for cid, col in (('Default', [202, 179, 143]), ('Green', [88, 130, 90]), ('Red', [153, 45, 37])):
        els.append(dict(id='RT_Value' + cid, type='text', width=45, height=24, tintColor=[255, 255, 255, 0],
                        defaultText='0', textRegion=dict(x=0, y=0, w=45, h=24, align='right', valign='center',
                        fontFamily='Roboto', fontSize=22, fontColor=col + [255],
                        bold=True, outlineWidth=1, outlineColor=[0, 0, 0, 140])))
    els.append(dict(id='RT_Desc', type='text', width=200, height=60, tintColor=[255, 255, 255, 0],
                    defaultText='The population of humans in your town. Only available humans can be used for new assignments.',
                    textRegion=dict(x=0, y=0, w=200, h=60, align='left', valign='top',
                    fontFamily='Quintessential', fontSize=21, fontColor=[241, 216, 180, 255],
                    bold=True, boldStrength=0.6, wordWrap=True, lineSpacing=-4,
                    outlineWidth=1, outlineColor=[0, 0, 0, 150])))
    json.dump(ed, open('assets/UI/definitions/elements.json', 'w', encoding='utf-8'), indent=2)

    wd = json.load(open('assets/UI/definitions/widgets.json', encoding='utf-8'))
    wd['widgets'] = [w for w in wd['widgets'] if w['id'] not in ('CommanderEquipWindow', 'StatTooltipWindow', 'ResourceTooltipWindow')]

    def child(name, element, x, y, w, h, **extra):
        c = dict(name=name, element=element, x=x, y=y, width=w, height=h, anchor=0)
        c.update(extra)
        return c

    # ---- CommanderEquipWindow ----
    ch = []
    ch.append(child('cb_blacksmith', 'CB_Blacksmith', 25, 79, 287, 246))
    for i, (x, y, label, icon, ia) in enumerate(SLOTS):
        ch.append(child(f'cb_s{i}_tex', 'CB_SlotTex', x, y, 75, 75))
        ch.append(child(f'cb_s{i}_stencil', 'CB_SlotStencil', x, y, 75, 75))
        ch.append(child(f'cb_s{i}_border', 'CB_SlotBorder', x, y, 75, 75))
        ch.append(child(f'cb_s{i}_highlight', 'CB_SlotHighlight', x + 5, y + 5, 66, 64))
        ch.append(child(f'cb_s{i}_icon', 'CB_Icon_' + icon, x + 14, y + 12, 48, 48))
        ch.append(child(f'cb_s{i}_label', 'CB_SlotLabel', x - 11, y + 68, 97, 26, defaultText=label))
    ch.append(child('cb_unitshadow', 'CB_UnitShadow', 81, 231, 149, 34))
    ch.append(child('cb_unit', 'CB_Unit', 112, 163, 88, 91))
    for i, (x, w, label, icon) in enumerate(BUTTONS):
        ch.append(child(f'cb_b{i}_tex', f'CB_BtnTex{w}', x, BTN_Y, w, 60))
        ch.append(child(f'cb_b{i}_border', f'CB_BtnBorder{w}', x, BTN_Y, w, 60))
        ch.append(child(f'cb_b{i}_icon', 'CB_BtnIcon_' + icon, x + 13, BTN_Y + 12, 39, 36))
        ch.append(child(f'cb_b{i}_label', 'CB_BtnLabel', x + 52, BTN_Y + 12, w - 56, 36, defaultText=label))
    ch.append(child('cb_title_swatch', 'CB_TitleSwatch', 4, 3, 304, 56))
    ch.append(child('cb_title_bar', 'CB_TitleBar', 4, 58, 304, 5))
    ch.append(child('cb_seam', 'CB_SeamLine', 4, 58, 304, 1))
    ch.append(child('cb_title_shadow', 'CB_TitleShadow', 6, 5, 304, 55))
    ch.append(child('cb_title', 'CB_Title', 4, 3, 304, 55))
    wd['widgets'].append(dict(
        id='CommanderEquipWindow', background='LeatherBackground', frame='RenaiThinBorder16',
        width=312, height=366, backgroundScale=0.14, backgroundInset=-17, frameScale=0.49,
        # WindowInner m_Color is a cool multiply tint (199,215,229) over the
        # leather; valStrength 0.48 is Unity's verbatim PercentNewValue.
        backgroundTint=[199, 215, 229, 255], frameTint=[255, 255, 255, 255],
        bgHarmonize=dict(targetColor=[157, 123, 82, 255], hueStrength=1, satStrength=1, valStrength=0.72,
                         useHcl=False, gradColor=[18, 15, 11, 112], gradStrength=0.992),
        frameHarmonize=dict(targetColor=[118, 97, 60, 255], hueStrength=1, satStrength=0.75, valStrength=0.665, useHcl=False),
        modal=False, children=ch))

    # ---- StatTooltipWindow ----
    tch = []
    tch.append(child('tt_line_top', 'TT_GoldLineBright', 0, 0, 222, 1))
    tch.append(child('tt_line_left', 'TT_GoldLine', 0, 1, 1, 101))
    tch.append(child('tt_line_right', 'TT_GoldLine', 218, 5, 1, 94))
    tch.append(child('tt_line_bottom', 'TT_GoldLineBright', 5, 99, 213, 1))
    tch.append(child('tt_icon', 'TT_StrengthIcon', 5, 4, 36, 36))
    tch.append(child('tt_underline', 'TT_Underline', 42, 36, 173, 3))
    tch.append(child('tt_title', 'TT_Title', 41, 5, 178, 31))
    tch.append(child('tt_desc', 'TT_Desc', 8, 38, 204, 61,
                     textOverride=dict(x=0, y=0, w=204, h=61, align='left', valign='center',
                                       fontFamily='Quintessential', fontSize=17, fontColor=[250, 224, 186, 255],
                                       bold=True, boldStrength=0.6, wordWrap=True, lineSpacing=-3,
                                       outlineWidth=1, outlineColor=[0, 0, 0, 150])))
    wd['widgets'].append(dict(
        id='StatTooltipWindow', background='LeatherBackground', frame='RenaiThinBorder16',
        width=222, height=103, backgroundScale=0.019, backgroundInset=-2, frameScale=0.36,
        backgroundTint=[255, 255, 255, 255], frameTint=[255, 255, 255, 255],
        stencilImagePath='assets/UI/Background/dragonpattern_stattip_222x103.png',
        stencilTint=[255, 255, 255, 36],
        bgHarmonize=dict(targetColor=[31, 24, 17, 255], hueStrength=1, satStrength=0.756, valStrength=0.588,
                         useHcl=False, gradColor=[18, 15, 11, 112], gradStrength=0.992),
        stencilHarmonize=dict(targetColor=[170, 102, 31, 255], hueStrength=1, satStrength=0.756, valStrength=0.544,
                              useHcl=False, gradColor=[18, 15, 11, 112], gradStrength=0.992),
        frameHarmonize=dict(targetColor=[118, 97, 60, 255], hueStrength=1, satStrength=0.75, valStrength=0.665, useHcl=False),
        modal=False, children=tch))

    # ---- ResourceTooltipWindow ----
    ROWS = [
        ('(Population Cap)', '200', 'Default'),
        ('Total Population', '100', 'Green'),
        ('Allocated', '80', 'Red'),
        ('Available Population', '20', 'Green'),
        ('Growth', '+10', 'Green'),
    ]
    rch = []
    rch.append(child('rt_line_top', 'TT_GoldLineBright', 0, 0, 222, 1))
    rch.append(child('rt_line_left', 'TT_GoldLine', 0, 1, 1, 229))
    rch.append(child('rt_line_right', 'TT_GoldLine', 218, 5, 1, 222))
    rch.append(child('rt_line_bottom', 'TT_GoldLineBright', 5, 227, 213, 1))
    rch.append(child('rt_icon', 'RT_HumansIcon', 5, 5, 36, 36))
    rch.append(child('rt_underline', 'TT_Underline', 44, 32, 140, 3))
    rch.append(child('rt_title', 'RT_Title', 41, 6, 139, 31))
    rch.append(child('rt_header_value', 'RT_HeaderValue', 182, 11, 32, 24))
    rch.append(child('rt_box_parchment', 'RT_Parchment', 9, 44, 205, 123))
    rch.append(child('rt_box_stencil', 'RT_BoxStencil', 8, 44, 206, 123))
    rch.append(child('rt_box_frame', 'RT_BoxFrame', 7, 42, 207, 125))
    for i, (label, value, color) in enumerate(ROWS):
        y = 44 + 24 * i
        rch.append(child(f'rt_r{i}_label', 'RT_Label', 5, y, 167, 24, defaultText=label))
        rch.append(child(f'rt_r{i}_value', 'RT_Value' + color, 167, y + 1, 45, 24, defaultText=value))
    rch.append(child('rt_desc', 'RT_Desc', 13, 169, 200, 60))
    wd['widgets'].append(dict(
        id='ResourceTooltipWindow', background='LeatherBackground', frame='RenaiThinBorder16',
        width=222, height=231, backgroundScale=0.019, backgroundInset=-2, frameScale=0.36,
        backgroundTint=[255, 255, 255, 255], frameTint=[255, 255, 255, 255],
        stencilImagePath='assets/UI/Background/dragonpattern_rt_222x231.png',
        stencilTint=[255, 255, 255, 36],
        bgHarmonize=dict(targetColor=[31, 24, 17, 255], hueStrength=1, satStrength=0.756, valStrength=0.588,
                         useHcl=False, gradColor=[18, 15, 11, 112], gradStrength=0.992),
        stencilHarmonize=dict(targetColor=[170, 102, 31, 255], hueStrength=1, satStrength=0.756, valStrength=0.544,
                              useHcl=False, gradColor=[18, 15, 11, 112], gradStrength=0.992),
        frameHarmonize=dict(targetColor=[118, 97, 60, 255], hueStrength=1, satStrength=0.75, valStrength=0.665, useHcl=False),
        modal=False, children=rch))

    json.dump(wd, open('assets/UI/definitions/widgets.json', 'w', encoding='utf-8'), indent=2)
    print('generated CommanderEquipWindow + StatTooltipWindow + ResourceTooltipWindow')


if __name__ == '__main__':
    main()
