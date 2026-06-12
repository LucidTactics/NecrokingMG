"""Generate ResourceTooltipDyn — the AUTO-SIZE version of the resource HUD
tooltip (arbitrary tabulation row count; rows hide + collapse at runtime).

The static ResourceTooltipWindow is kept untouched as the reference/backup.
Structure (vertical layout, autoSizeHeight at every level):
  ResourceTooltipDyn (root, w 222, max 396)
    rtd_header  -> RTD_Header widget (fixed 222x40: icon/title/value/underline)
    rtd_box     -> RTD_TabBox widget (x7, w 207, autoSize, max 288 = 12 rows)
                     rows: 12 x RTD_Row instances (label + value children)
    rtd_desc    -> RT_Desc element (x13, w 196, h 56)
Idempotent: removes/re-adds the RTD_* elements and widgets each run.
"""
import json

MAX_ROWS = 12
ROW_H = 24
BOX_MAX_H = MAX_ROWS * ROW_H          # 288
ROOT_MAX_H = 40 + 4 + BOX_MAX_H + 4 + 56 + 4   # header+gap+box+gap+desc+padB = 396


def main():
    ed = json.load(open('assets/UI/definitions/elements.json', encoding='utf-8'))
    ed['elements'] = [e for e in ed['elements'] if not e['id'].startswith('RTD_')]
    els = ed['elements']

    # Row label/value: reuse the calibrated RT_ text styles; value color is
    # driven per-instance at runtime via SetTextColor (green/red/default).
    els.append(dict(id='RTD_RowLabel', type='text', width=163, height=24, tintColor=[255, 255, 255, 0],
                    defaultText='', textRegion=dict(x=0, y=0, w=163, h=24, align='right', valign='center',
                    fontFamily='Quintessential', fontSize=24, fontColor=[248, 219, 184, 255],
                    bold=True, boldStrength=0.7, charSpacing=1.25, outlineWidth=1, outlineColor=[0, 0, 0, 150])))
    els.append(dict(id='RTD_RowValue', type='text', width=41, height=24, tintColor=[255, 255, 255, 0],
                    defaultText='', textRegion=dict(x=0, y=0, w=41, h=24, align='right', valign='center',
                    fontFamily='Roboto', fontSize=22, fontColor=[202, 179, 143, 255],
                    bold=True, outlineWidth=1, outlineColor=[0, 0, 0, 140])))
    json.dump(ed, open('assets/UI/definitions/elements.json', 'w', encoding='utf-8'), indent=2)

    wd = json.load(open('assets/UI/definitions/widgets.json', encoding='utf-8'))
    wd['widgets'] = [w for w in wd['widgets'] if w['id'] not in ('ResourceTooltipDyn', 'RTD_Header', 'RTD_TabBox', 'RTD_Row')]

    def child(name, x, y, w, h, element=None, widget=None, **extra):
        c = dict(name=name, x=x, y=y, width=w, height=h, anchor=0)
        if element: c['element'] = element
        if widget: c['widget'] = widget
        c.update(extra)
        return c

    # Row: label + value, absolute within the 207x24 row strip.
    wd['widgets'].append(dict(
        id='RTD_Row', width=207, height=ROW_H, modal=False, children=[
            child('label', 4, 0, 163, 24, element='RTD_RowLabel'),
            child('value', 162, 1, 41, 24, element='RTD_RowValue'),
        ]))

    # Header: icon / title / green value / underline (same elements as the
    # static version — they're shared styles, not copies).
    wd['widgets'].append(dict(
        id='RTD_Header', width=222, height=40, modal=False, children=[
            child('icon', 5, 5, 36, 36, element='RT_HumansIcon'),
            child('underline', 44, 32, 140, 3, element='TT_Underline'),
            child('title', 44, 6, 136, 31, element='RT_Title'),
            child('value', 182, 11, 32, 24, element='RT_HeaderValue'),
        ]))

    # Tabulation box: auto-size vertical stack of row instances; parchment +
    # thatch are image layers baked at MAX height and cropped from the top.
    wd['widgets'].append(dict(
        id='RTD_TabBox', width=207, height=BOX_MAX_H, autoSizeHeight=True,
        layout='vertical', backgroundInset=2, stencilInset=2,
        backgroundImagePath='assets/UI/Patterns/Parchment_203x284.png',
        backgroundTint=[255, 255, 255, 255],
        bgHarmonize=dict(targetColor=[7, 6, 3, 255], hueStrength=0, satStrength=0.438, valStrength=0.787, useHcl=False),
        stencilImagePath='assets/UI/Patterns/Thatch_203x284.png',
        stencilTint=[255, 255, 255, 10],
        frame='RenaiThinBorder16', frameScale=0.25, frameTint=[255, 255, 255, 255],
        frameHarmonize=dict(targetColor=[150, 134, 107, 255], hueStrength=1, satStrength=0.266, valStrength=0.466, useHcl=False),
        modal=False,
        children=[child(f'row{i}', 0, 0, 207, ROW_H, widget='RTD_Row') for i in range(MAX_ROWS)]))

    # Root: leather window, dragon stencil (max-height bake), thin gold frame.
    wd['widgets'].append(dict(
        id='ResourceTooltipDyn', width=222, height=ROOT_MAX_H, autoSizeHeight=True,
        layout='vertical', layoutSpacing=4, layoutPadBottom=4,
        background='LeatherBackground', backgroundScale=0.019, backgroundInset=-2,
        backgroundTint=[255, 255, 255, 255],
        stencilImagePath='assets/UI/Background/dragonpattern_rtd_222x396.png',
        stencilTint=[255, 255, 255, 36],
        frame='RenaiThinBorder16', frameScale=0.36, frameTint=[255, 255, 255, 255],
        bgHarmonize=dict(targetColor=[31, 24, 17, 255], hueStrength=1, satStrength=0.756, valStrength=0.588,
                         useHcl=False, gradColor=[18, 15, 11, 112], gradStrength=0.992),
        stencilHarmonize=dict(targetColor=[170, 102, 31, 255], hueStrength=1, satStrength=0.756, valStrength=0.544,
                              useHcl=False, gradColor=[18, 15, 11, 112], gradStrength=0.992),
        frameHarmonize=dict(targetColor=[118, 97, 60, 255], hueStrength=1, satStrength=0.75, valStrength=0.665, useHcl=False),
        modal=False, children=[
            child('rtd_header', 0, 0, 222, 40, widget='RTD_Header'),
            child('rtd_box', 7, 0, 207, BOX_MAX_H, widget='RTD_TabBox'),
            child('rtd_desc', 13, 0, 196, 56, element='RT_Desc'),
        ]))

    json.dump(wd, open('assets/UI/definitions/widgets.json', 'w', encoding='utf-8'), indent=2)
    print('generated ResourceTooltipDyn (+RTD_Header/TabBox/Row)')


if __name__ == '__main__':
    main()
