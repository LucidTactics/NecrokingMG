"""Generate UnitSheetDyn — auto-size version of UnitTooltipWindow, built by
programmatically restructuring the static widget's children into nested
sections (desc/stats fixed; equipment/attacks auto-size rows; abilities fixed).
The static UnitTooltipWindow is kept untouched as reference/backup.
Idempotent: removes/re-adds the UTD_* widgets each run.
"""
import json

SECTIONS = [('desc', 0, 192), ('stats', 192, 357), ('eq', 357, 573), ('at', 573, 680), ('ab', 680, 745)]
EQ_ROWS, AT_ROWS, ROW_H = 7, 3, 27


def main():
    wd = json.load(open('assets/UI/definitions/widgets.json', encoding='utf-8'))
    ed = json.load(open('assets/UI/definitions/elements.json', encoding='utf-8'))
    els = {e['id']: e for e in ed['elements']}
    static = next(w for w in wd['widgets'] if w['id'] == 'UnitTooltipWindow')
    wd['widgets'] = [w for w in wd['widgets'] if not (w['id'] == 'UnitSheetDyn' or w['id'].startswith('UTD_'))]

    def rebase(c, dx, dy, name=None):
        c = dict(c)
        c['x'] -= dx
        c['y'] -= dy
        if name is not None: c['name'] = name
        return c

    by_name = {c['name']: c for c in static['children']}

    # ── fixed sections: desc (ud_*) and stats (st_*) keep their children ──
    desc_children = [rebase(c, 0, 0) for c in static['children'] if c['name'].startswith('ud_')]
    stats_children = [rebase(c, 0, 192) for c in static['children'] if c['name'].startswith('st_')]
    ab_children = [rebase(c, 0, 680) for c in static['children'] if c['name'].startswith('ab_')]
    wd['widgets'].append(dict(id='UTD_DescSection', width=468, height=192, modal=False, children=desc_children))
    wd['widgets'].append(dict(id='UTD_StatsSection', width=468, height=165, modal=False, children=stats_children))
    wd['widgets'].append(dict(id='UTD_AbilitiesSection', width=468, height=65, modal=False, children=ab_children))

    # ── row-section builder: title widget + auto-size box of row instances ──
    def build_rows_section(prefix, sec_y, box_y, n_rows, row0, swatch_el, box_el, pat_el, extras):
        title_children = [rebase(by_name[f'{prefix}_title_swatch'], 5, sec_y),
                          rebase(by_name[f'{prefix}_title_heraldry'], 5, sec_y),
                          rebase(by_name[f'{prefix}_title'], 5, sec_y)]
        wd['widgets'].append(dict(id=f'UTD_{prefix}Title', width=463, height=box_y - sec_y,
                                  modal=False, children=title_children))
        # Row template: r0's children rebased row-local (box at window x3, row0 at window y)
        row_y = by_name[f'{prefix}_r0_icon']['y']
        row_children = [dict(name='swatch', element=swatch_el, x=0, y=0, width=463, height=ROW_H, anchor=0)]
        for c in static['children']:
            nm = c['name']
            if nm.startswith(f'{prefix}_r0') and not nm.startswith(f'{prefix}_rowswatch'):
                row_children.append(rebase(c, 3, row_y, name=nm[len(prefix) + 3:].lstrip('_') or nm))
        # extra image child for armor-row stat icons where the template has text 'Len'
        row_children += extras
        # column bar segments (continuous when rows stack)
        for k, bx in enumerate((221, 279, 341, 402)):
            row_children.append(dict(name=f'col{k}', element='ST_ColumnBar', x=bx, y=0, width=3, height=ROW_H, anchor=0))
        wd['widgets'].append(dict(id=f'UTD_{prefix}Row', width=463, height=ROW_H, modal=False, children=row_children))

        box = els[box_el]
        pat = els[pat_el]
        box_h = n_rows * ROW_H + 9
        wd['widgets'].append(dict(
            id=f'UTD_{prefix}Box', width=463, height=box_h, autoSizeHeight=True,
            layout='vertical', layoutPadTop=1, layoutPadBottom=8,
            backgroundImagePath=box['imagePath'], backgroundTint=box['tintColor'],
            bgHarmonize=box['harmonize'],
            stencilImagePath=pat['imagePath'], stencilTint=pat['tintColor'], stencilInset=4,
            stencilHarmonize=pat['harmonize'],
            modal=False,
            children=[dict(name=f'row{i}', widget=f'UTD_{prefix}Row', x=0, y=0, width=463, height=ROW_H, anchor=0)
                      for i in range(n_rows)]))
        wd['widgets'].append(dict(
            id=f'UTD_{prefix}Section', width=468, height=(box_y - sec_y) + box_h, autoSizeHeight=True,
            layout='vertical', modal=False, children=[
                dict(name='title', widget=f'UTD_{prefix}Title', x=5, y=0, width=463, height=box_y - sec_y, anchor=0),
                dict(name='box', widget=f'UTD_{prefix}Box', x=3, y=0, width=463, height=box_h, anchor=0),
            ]))

    # armor rows show an Enc icon where weapon rows show the 'Len' text
    s2 = by_name['eq_r0s2']
    enc_img = dict(name='s2b', element='EQ_Icon_EncEq', x=s2['x'] - 3, y=s2['y'] - by_name['eq_r0_icon']['y'],
                   width=24, height=24, anchor=0)
    build_rows_section('eq', 357, 385, EQ_ROWS, 'eq_r0', 'EQ_RowSwatch27', 'EQ_StatBox', 'EQ_BoxPattern', [enc_img])
    build_rows_section('at', 573, 600, AT_ROWS, 'at_r0', 'AT_RowSwatch', 'AT_StatBox', 'AT_BoxPattern', [])

    # ── root ──
    root = dict(id='UnitSheetDyn', width=468, height=745, autoSizeHeight=True, layout='vertical')
    for k in ('background', 'frame', 'backgroundScale', 'backgroundInset', 'frameScale',
              'frameInset', 'frameInsetR', 'backgroundTint', 'frameTint', 'bgHarmonize', 'frameHarmonize'):
        if k in static: root[k] = static[k]
    root['modal'] = False
    root['children'] = [
        dict(name='sec_desc', widget='UTD_DescSection', x=0, y=0, width=468, height=192, anchor=0),
        dict(name='sec_stats', widget='UTD_StatsSection', x=0, y=0, width=468, height=165, anchor=0),
        dict(name='sec_eq', widget='UTD_eqSection', x=0, y=0, width=468, height=216, anchor=0),
        dict(name='sec_at', widget='UTD_atSection', x=0, y=0, width=468, height=107, anchor=0),
        dict(name='sec_ab', widget='UTD_AbilitiesSection', x=0, y=0, width=468, height=65, anchor=0),
    ]
    wd['widgets'].append(root)

    json.dump(wd, open('assets/UI/definitions/widgets.json', 'w', encoding='utf-8'), indent=2)
    print('generated UnitSheetDyn (+UTD_* sections/rows)')


if __name__ == '__main__':
    main()
