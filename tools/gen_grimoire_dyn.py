"""Generate GrimoireDyn: the grimoire window with a 2-wide grid of GM_Tile
sub-widget instances (union of the four GodMenu3 tile variants — the binder
shows/hides the optional parts per spell tileTemplate: summon / evocation /
buff / debuff). Phase 1 = population only; scrolling/tabs interaction later.

Chrome (window layers, title ribbon, school tabs, path-tab strip) is copied
from the converter output; tile children come from one ACTIVE instance of
each variant, rebased tile-local and renamed to stable binder names.
"""
import json
import sys
sys.path.insert(0, 'tools')
import unity2widget as u2w

DUMP = 'log/bag_inspect/grimoire_tree.txt'
COLS, ROWS_VISIBLE = 2, 11
TILE_W, TILE_H, STRIDE_Y = 330, 80, 77
GRID_X = (24, 346)
GRID_Y0 = 185

# tile-variant child name -> stable binder name
RENAME = {
    'Box Background': 'bg', 'Box Background Gradient': 'grad', 'BoxFrame': 'frame',
    'Underline': 'underline', 'PerkIcon': 'icon', 'PerkFrame': 'iconframe',
    'PerkTitle': 'title', 'PathPrompt': 'path_p', 'PathReqText': 'path_v',
    'PathReqIcon': 'path_i', 'Path2ReqText': 'path2_v', 'Path2ReqIcon': 'path2_i',
    'CostPrompt': 'cost_p', 'Cost': 'cost_v', 'CostIcon': 'cost_i',
    'Cost2': 'cost2_v', 'Cost2Icon': 'cost2_i', 'DamageText': 'dmg_v',
    'DamgeMod1Text': 'dmg_m1', 'DamgeMod2Text': 'dmg_m2', 'TargetIcon': 'target',
    'BuffText': 'buff_p', 'BuffIcon': 'buff_i',
}


def find_tiles(root):
    """One node per variant from the ACTIVE SpellContainer."""
    out = {}
    def walk(n, active_chain):
        active = active_chain and (n.active or n is root)
        if n.name == 'SpellContainer' and active:
            for row in n.children:
                for tile in row.children:
                    key = tile.name.split(' ')[0].lower()
                    if key not in out and tile.active:
                        out[key] = tile
        for c in n.children:
            walk(c, active)
    walk(root, True)
    return out


def main():
    root = u2w.parse(DUMP)
    root.pos = (0, 0)
    root.anchor_min = root.anchor_max = (0.5, 0.5)
    root.rect = (0, 0, root.size[0], root.size[1])
    for c in root.children:
        u2w.layout(c, 0, 0, root.size[0], root.size[1])

    tiles = find_tiles(root)
    assert set(tiles) >= {'summon', 'evocation', 'buff', 'debuff'}, tiles.keys()

    elements, tile_children = [], []
    seen = set()
    counter = [0]

    def emit_tile_node(node, ox, oy, force=False):
        """Emit one node as a tile-local element+child (active or force)."""
        name = RENAME.get(node.name)
        if name is None or name in seen:
            return
        x, y, w, h = node.rect
        cx, cy, cw, chh = round(x - ox), round(y - oy), max(1, round(w)), max(1, round(h))
        if node.image and node.image['sprite']:
            tex = u2w.copy_texture(node.image['sprite'])
            if not tex:
                return
            if node.image['type'] in (1, 2):
                tex = u2w.bake_variant(node.image['sprite'], tex, node.image['type'], node.image['ppu'], cw, chh)
            elif node.image['preserve']:
                from PIL import Image as _Im
                tw, th = _Im.open(tex).size
                fit = min(cw / tw, chh / th)
                fw, fh = max(1, round(tw * fit)), max(1, round(th * fit))
                cx += (cw - fw) // 2; cy += (chh - fh) // 2; cw, chh = fw, fh
            col = node.image['color']
            tint = [round(col[0] * 255), round(col[1] * 255), round(col[2] * 255), round(col[3] * 255)]
            eid = f'GMT_{counter[0]}'; counter[0] += 1
            el = dict(id=eid, type='image', imagePath=tex, width=cw, height=chh, tintColor=tint)
            hm = u2w.harm_from_swapper(node.swapper)
            if hm: el['harmonize'] = hm
            elements.append(el)
            tile_children.append(dict(name=name, element=eid, x=cx, y=cy, width=cw, height=chh, anchor=0))
            seen.add(name)
        elif node.tmp is not None:
            tm = node.tmp
            font = tm.get('font', 'Quintessential')
            fs = max(8, round(tm['fontSize'] * (u2w.ROBOTO_RATIO if font == 'Roboto' else u2w.QUINT_RATIO)))
            fs = u2w.SIZE_OVERRIDES.get('GMW:' + node.name, u2w.SIZE_OVERRIDES.get(node.name, fs))
            colr = tm['color']
            fc = [round(colr[0] * 255), round(colr[1] * 255), round(colr[2] * 255), 255]
            if font == 'Quintessential' and fc[0] > 230 and fc[1] > 230 and fc[2] > 230:
                fc = list(u2w.WARM_FACE)
            align = {1: 'left', 2: 'center', 4: 'right'}.get(tm['align'], 'left')
            import re
            text = re.sub(r'<.*?>', '', tm['text'])
            eid = f'GMT_{counter[0]}'; counter[0] += 1
            elements.append(dict(id=eid, type='text', width=cw, height=chh, tintColor=[255, 255, 255, 0],
                                 defaultText=text,
                                 textRegion=dict(x=0, y=0, w=cw, h=chh, align=align, valign='center',
                                                 fontFamily=font, fontSize=fs, fontColor=fc,
                                                 bold=tm['style'] == 1 or font == 'Roboto',
                                                 outlineWidth=1, outlineColor=[0, 0, 0, 150])))
            tile_children.append(dict(name=name, element=eid, x=cx, y=cy, width=cw, height=chh, anchor=0))
            seen.add(name)

    def walk_emit(node, ox, oy, include_inactive_names=()):
        if node.active or node.name in include_inactive_names:
            emit_tile_node(node, ox, oy)
            for c in node.children:
                walk_emit(c, ox, oy, include_inactive_names)

    # Core + target from the summon tile; damage block from evocation
    # (TargetIcon inactive there); buff block from the buff tile; second
    # path/cost slots are inactive everywhere — force-include from debuff.
    for variant, extras in (('summon', ()), ('evocation', ()), ('buff', ()),
                            ('debuff', ('Path2ReqText', 'Path2ReqIcon', 'Cost2', 'Cost2Icon'))):
        t = tiles[variant]
        ox, oy = t.rect[0], t.rect[1]
        walk_emit(t, ox, oy, extras)

    ed = json.load(open('assets/UI/definitions/elements.json', encoding='utf-8'))
    ed['elements'] = [e for e in ed['elements'] if not e['id'].startswith('GMT_')]
    ed['elements'] += elements
    json.dump(ed, open('assets/UI/definitions/elements.json', 'w', encoding='utf-8'), indent=2)

    wd = json.load(open('assets/UI/definitions/widgets.json', encoding='utf-8'))
    wd['widgets'] = [w for w in wd['widgets'] if w['id'] not in ('GM_Tile', 'GrimoireDyn')]
    wd['widgets'].append(dict(id='GM_Tile', width=TILE_W, height=TILE_H, modal=False, children=tile_children))

    # Chrome: everything in the converted GrimoireWindow except the tile grid
    # (children generated from SpellContainer) and the slider knob.
    gmw = next(w for w in wd['widgets'] if w['id'] == 'GrimoireWindow')
    drop_prefixes = ('bg', 'grad', 'frame', 'underline', 'icon', 'iconframe', 'title')
    chrome = []
    for c in gmw['children']:
        nm = c['name']
        # converter names look like gmw_60_BoxBackground — tile-grid pieces and slider
        tail = nm.split('_', 2)[-1]
        if tail.startswith(('BoxBackground', 'BoxFrame', 'Underline', 'PerkIcon', 'PerkFrame', 'PerkTitle',
                            'PathPrompt', 'PathReqText', 'PathReqIcon', 'Path2ReqText', 'Path2ReqIcon',
                            'CostPrompt', 'Cost', 'CostIcon', 'Cost2', 'Cost2Icon', 'DamageText',
                            'DamgeMod', 'TargetIcon', 'BuffText', 'BuffIcon', 'Slider')):
            continue
        chrome.append(dict(c))

    grid = []
    for i in range(COLS * ROWS_VISIBLE):
        col, row = i % COLS, i // COLS
        grid.append(dict(name=f'tile{i}', widget='GM_Tile', x=GRID_X[col], y=GRID_Y0 + row * STRIDE_Y,
                         width=TILE_W, height=TILE_H, anchor=0))

    dyn = dict(id='GrimoireDyn', width=gmw['width'], height=gmw['height'], modal=False,
               children=chrome + grid)
    for k in ('background', 'backgroundScale', 'backgroundInset', 'backgroundTint', 'bgHarmonize'):
        if k in gmw: dyn[k] = gmw[k]
    wd['widgets'].append(dyn)
    json.dump(wd, open('assets/UI/definitions/widgets.json', 'w', encoding='utf-8'), indent=2)
    print(f'GM_Tile: {len(tile_children)} children; GrimoireDyn: {len(chrome)} chrome + {len(grid)} tiles')


if __name__ == '__main__':
    main()
