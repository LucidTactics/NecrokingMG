"""Convert a dump_unity_subtree.py dump into a widget (elements + children).

First-pass importer for large panels: computes absolute rects from the
RectTransform chain (anchors/pivot/stretch, y-flip to top-left), copies used
textures from the Unity project into assets/UI/Imported/, and emits one flat
absolute-positioned widget with verbatim swapper harmonize + tints. Sliced and
tiled sprites are drawn stretched on this first pass (calibration/bake pass
refines the worst offenders afterward, per the importing-unity-ui skill).

Usage: python tools/unity2widget.py <dump.txt> <WidgetId> <prefix> [--list]
"""
import json
import os
import re
import shutil
import sys

UNITY_ASSETS = r'E:\Nightfall\NightfallRogueRelease\Assets'
IMPORT_DIR = 'assets/UI/Imported'

# TMP -> FontStash size ratios learned from prior panels
QUINT_RATIO = 1.45
ROBOTO_RATIO = 1.25


def fnum(s):
    return float(s)


class Node:
    def __init__(self, name, depth, active):
        self.name, self.depth, self.active = name, depth, active
        self.children = []
        self.parent = None
        self.anchor_min = self.anchor_max = (0.5, 0.5)
        self.pos = (0, 0)
        self.size = (100, 100)
        self.pivot = (0.5, 0.5)
        self.scale = 1.0
        self.image = None      # dict: sprite, color, type, ppu
        self.swapper = None    # dict
        self.tmp = None        # dict: text, fontSize, color, align, style
        self.rect = None       # absolute (x, y, w, h) top-left


def parse(path):
    rx_vec = re.compile(r'\{x: ([-\d.e]+), y: ([-\d.e]+)')
    root = None
    stack = []
    cur = None
    section = None
    for line in open(path, encoding='utf-8'):
        m = re.match(r"( *)GO '(.+?)'( \(INACTIVE\))?", line)
        if m:
            depth = len(m.group(1)) // 2
            cur = Node(m.group(2), depth, m.group(3) is None)
            while stack and stack[-1].depth >= depth:
                stack.pop()
            if stack:
                cur.parent = stack[-1]
                stack[-1].children.append(cur)
            else:
                root = cur
            stack.append(cur)
            section = None
            continue
        if cur is None:
            continue
        s = line.strip()
        if s.startswith('RectTransform:'):
            vs = rx_vec.findall(line)
            if len(vs) >= 4:
                cur.anchor_min = (fnum(vs[0][0]), fnum(vs[0][1]))
                cur.anchor_max = (fnum(vs[1][0]), fnum(vs[1][1]))
                cur.pos = (fnum(vs[2][0]), fnum(vs[2][1]))
                cur.size = (fnum(vs[3][0]), fnum(vs[3][1]))
                if len(vs) >= 5:
                    cur.pivot = (fnum(vs[4][0]), fnum(vs[4][1]))
        elif s.startswith('scale={'):
            mm = re.search(r'x: ([-\d.e]+)', s)
            if mm: cur.scale = fnum(mm.group(1))
        elif s.startswith('Image:'):
            section = 'img'
            cur.image = {'sprite': '', 'color': (1, 1, 1, 1), 'type': 0, 'ppu': 1.0}
        elif s.startswith('Script['):
            section = 'swap' if 'Swapper' in s else None
            if section == 'swap':
                cur.swapper = {}
        elif s.startswith('TextMeshProUGUI'):
            section = 'tmp'
            cur.tmp = {'text': '', 'fontSize': 14.0, 'color': (1, 1, 1, 1), 'align': 1, 'style': 0}
        elif section == 'img':
            if s.startswith('m_Sprite:'):
                mm = re.search(r'<(.+?)>', s)
                cur.image['sprite'] = mm.group(1) if mm else ''
            elif s.startswith('m_Color:'):
                vals = re.findall(r'-?\d+\.?\d*(?:e-?\d+)?', s.split(':', 1)[1])
                cur.image['color'] = tuple(float(v) for v in vals[:4])
            elif s.startswith('m_Type:'):
                cur.image['type'] = int(s.split()[-1])
            elif s.startswith('m_PixelsPerUnitMultiplier:'):
                cur.image['ppu'] = float(s.split()[-1])
        elif section == 'swap' and cur.swapper is not None:
            for key in ('TargetColor', 'OutlineColor', 'GradColor'):
                if s.startswith(key + ':'):
                    vals = re.findall(r'-?\d+\.?\d*(?:e-?\d+)?', s.split(':', 1)[1])
                    cur.swapper[key] = tuple(float(v) for v in vals[:4])
            for key in ('PercentNewHue', 'PercentNewSat', 'PercentNewValue',
                        'OutlineThickness', 'OutlineOpacity', 'VerGradD2U', 'Intensity'):
                if s.startswith(key + ':'):
                    cur.swapper[key] = float(s.split()[-1])
            if s.startswith('material: {fileID: 0}'):
                cur.swapper['inert_material'] = True
        elif section == 'tmp':
            if s.startswith('m_text:'):
                t = s[len('m_text:'):].strip()
                if len(t) >= 2 and t[0] == t[-1] and t[0] in ("'", '"'):
                    t = t[1:-1]
                cur.tmp['text'] = t.lstrip("'\"")
            elif s.startswith('m_fontSize:'):
                cur.tmp['fontSize'] = float(s.split()[-1])
            elif s.startswith('m_fontColor:'):
                vals = re.findall(r'-?\d+\.?\d*(?:e-?\d+)?', s.split(':', 1)[1])
                cur.tmp['color'] = tuple(float(v) for v in vals[:4])
            elif s.startswith('m_HorizontalAlignment:'):
                cur.tmp['align'] = int(s.split()[-1])
            elif s.startswith('m_fontStyle:'):
                cur.tmp['style'] = int(s.split()[-1])
            elif s.startswith('m_fontAsset:'):
                cur.tmp['font'] = 'Roboto' if 'Roboto' in s else 'Quintessential'
    return root


def layout(node, px, py, pw, ph, scale=1.0):
    """Compute absolute top-left rect from parent rect (y-down)."""
    s = scale * node.scale
    amin, amax, pos, size, piv = node.anchor_min, node.anchor_max, node.pos, node.size, node.pivot
    # anchor region in parent, y-up -> compute y-up box then flip
    ax0, ax1 = px + amin[0] * pw, px + amax[0] * pw
    # y-up: parent's bottom edge is py+ph in y-down. anchor fraction from bottom.
    ay0 = py + ph - amax[1] * ph   # top of anchor region (y-down)
    ay1 = py + ph - amin[1] * ph   # bottom of anchor region (y-down)
    if abs(amin[0] - amax[0]) < 1e-6:
        w = size[0] * s
        cx = ax0 + (pos[0] - (piv[0] - 0.5) * size[0]) * s
    else:
        w = (ax1 - ax0) + size[0] * s
        cx = (ax0 + ax1) / 2 + (pos[0] - (piv[0] - 0.5) * size[0]) * s
    if abs(amin[1] - amax[1]) < 1e-6:
        h = size[1] * s
        cy = ay0 - (pos[1] - (piv[1] - 0.5) * size[1]) * s  # y-down: up is minus
    else:
        h = (ay1 - ay0) + size[1] * s
        cy = (ay0 + ay1) / 2 - (pos[1] - (piv[1] - 0.5) * size[1]) * s
    node.rect = (cx - w / 2, cy - h / 2, w, h)
    for c in node.children:
        layout(c, *node.rect, s)


def harm_from_swapper(sw):
    if not sw or sw.get('inert_material'):
        sw = sw or {}
    t = sw.get('TargetColor', (1, 1, 1, 1))
    d = dict(targetColor=[round(t[0] * 255), round(t[1] * 255), round(t[2] * 255), 255],
             hueStrength=int(sw.get('PercentNewHue', 0)),
             satStrength=round(sw.get('PercentNewSat', 0), 3),
             valStrength=round(sw.get('PercentNewValue', 0), 3), useHcl=False)
    if sw.get('OutlineThickness', 0) > 0 and sw.get('OutlineOpacity', 0) > 0:
        oc = sw.get('OutlineColor', (0, 0, 0, 1))
        d.update(outlineColor=[round(oc[0] * 255), round(oc[1] * 255), round(oc[2] * 255), 255],
                 outlineThickness=round(sw['OutlineThickness'], 2),
                 outlineOpacity=round(sw['OutlineOpacity'], 3))
    if sw.get('VerGradD2U', 0) > 0:
        g = sw.get('GradColor', (0, 0, 0, 1))
        d.update(gradColor=[round(g[0] * 255), round(g[1] * 255), round(g[2] * 255), round(g[3] * 255)],
                 gradStrength=round(sw['VerGradD2U'], 3))
    if (d['hueStrength'] == 0 and d['satStrength'] == 0 and d['valStrength'] == 0
            and 'outlineColor' not in d and 'gradColor' not in d):
        return None
    return d


SIZE_OVERRIDES = {  # '[prefix:]name' -> FontStash size (measured vs captures)
    'GMW:Title Text': 72, 'SWT:Title Text': 26, 'PerkTitle': 30, 'Tab Text': 24, 'ALLText': 24,
    'PathPrompt': 17, 'CostPrompt': 17, 'PathReqText': 22, 'Cost': 22,
    'Cost2': 22, 'BuffText': 16, 'DamageText': 16, 'DamgeMod1Text': 16,
    'DamgeMod2Text': 16,
}
WARM_FACE = [240, 218, 178, 255]

_meta_cache = {}

def sprite_meta(unity_rel):
    if unity_rel in _meta_cache: return _meta_cache[unity_rel]
    import re as _re
    p = os.path.join(UNITY_ASSETS, unity_rel.replace('\\', os.sep)) + '.meta'
    border, ppu = (0, 0, 0, 0), 100.0
    if os.path.exists(p):
        txt = open(p, encoding='utf-8', errors='ignore').read()
        mb = _re.search(r'spriteBorder: \{x: ([\d.]+), y: ([\d.]+), z: ([\d.]+), w: ([\d.]+)', txt)
        if mb: border = tuple(float(v) for v in mb.groups())  # L,B,R,T
        mp = _re.search(r'spritePixelsToUnits: ([\d.]+)', txt)
        if mp: ppu = float(mp.group(1))
    _meta_cache[unity_rel] = (border, ppu)
    return border, ppu


def bake_variant(unity_rel, base_tex, img_type, ppu_mult, w, h):
    """Bake sliced (nine) or tiled sprites at the drawn size (PointClamp rule)."""
    from PIL import Image
    border, ppu = sprite_meta(unity_rel)
    name = os.path.splitext(os.path.basename(base_tex))[0]
    out = f'{IMPORT_DIR}/baked_{name}_{w}x{h}.png'
    if os.path.exists(out): return out
    src_im = Image.open(base_tex).convert('RGBA')
    if img_type == 1 and any(b > 0 for b in border):
        sl, sb, sr, st = (int(b) for b in border)
        scale = 100.0 / (ppu * ppu_mult) if ppu * ppu_mult else 1.0
        dl, db, dr, dt = (max(1, round(b * scale)) for b in (sl, sb, sr, st))
        sw, sh = src_im.size
        sx = [0, sl, sw - sr, sw]; sy = [0, st, sh - sb, sh]
        dx = [0, dl, w - dr, w]; dy = [0, dt, h - db, h]
        o = Image.new('RGBA', (w, h), (0, 0, 0, 0))
        for ry in range(3):
            for rx in range(3):
                ssw, ssh = sx[rx+1]-sx[rx], sy[ry+1]-sy[ry]
                ddw, ddh = dx[rx+1]-dx[rx], dy[ry+1]-dy[ry]
                if ssw <= 0 or ssh <= 0 or ddw <= 0 or ddh <= 0: continue
                o.paste(src_im.crop((sx[rx], sy[ry], sx[rx+1], sy[ry+1])).resize((ddw, ddh), Image.LANCZOS), (dx[rx], dy[ry]))
        o.save(out); return out
    if img_type == 2:
        scale = 100.0 / (ppu * ppu_mult) if ppu * ppu_mult else 1.0
        if any(b > 0 for b in border):
            # Unity Tiled + sprite borders = nine-slice frame whose edges TILE
            # along their axis (center tiles too; usually transparent).
            sl, sb, sr, st = (int(b) for b in border)
            sw, sh = src_im.size
            dl, db, dr, dt = (max(1, round(b * scale)) for b in (sl, sb, sr, st))
            o = Image.new('RGBA', (w, h), (0, 0, 0, 0))
            def piece(x0, y0, x1, y1):
                return src_im.crop((x0, y0, x1, y1))
            def scaled(im2, fw, fh):
                return im2.resize((max(1, fw), max(1, fh)), Image.LANCZOS)
            # corners
            o.paste(scaled(piece(0, 0, sl, st), dl, dt), (0, 0))
            o.paste(scaled(piece(sw - sr, 0, sw, st), dr, dt), (w - dr, 0))
            o.paste(scaled(piece(0, sh - sb, sl, sh), dl, db), (0, h - db))
            o.paste(scaled(piece(sw - sr, sh - sb, sw, sh), dr, db), (w - dr, h - db))
            # edges: tile along the run
            top = scaled(piece(sl, 0, sw - sr, st), max(1, round((sw - sl - sr) * scale)), dt)
            bot = scaled(piece(sl, sh - sb, sw - sr, sh), top.width, db)
            x = dl
            while x < w - dr:
                o.paste(top, (x, 0)); o.paste(bot, (x, h - db)); x += top.width
            left = scaled(piece(0, st, sl, sh - sb), dl, max(1, round((sh - st - sb) * scale)))
            right = scaled(piece(sw - sr, st, sw, sh - sb), dr, left.height)
            y = dt
            while y < h - db:
                o.paste(left, (0, y)); o.paste(right, (w - dr, y)); y += left.height
            o.save(out); return out
        tw, th = max(1, round(src_im.width * scale)), max(1, round(src_im.height * scale))
        tile = src_im.resize((tw, th), Image.LANCZOS)
        o = Image.new('RGBA', (w, h), (0, 0, 0, 0))
        ty = h
        while ty > -th:
            ty -= th
            tx = 0
            while tx < w:
                o.paste(tile, (tx, ty))
                tx += tw
        o.save(out); return out
    return base_tex


def copy_texture(unity_rel):
    src = os.path.join(UNITY_ASSETS, unity_rel.replace('\\', os.sep))
    base = os.path.basename(unity_rel).replace('.psd', '.png')
    dst = f'{IMPORT_DIR}/{base}'
    if not os.path.exists(dst):
        if not os.path.exists(src):
            return None
        os.makedirs(IMPORT_DIR, exist_ok=True)
        if src.lower().endswith('.psd'):
            from PIL import Image
            Image.open(src).convert('RGBA').save(dst)
        else:
            shutil.copy(src, dst)
    return dst


def convert(dump_path, widget_id, prefix, root_override_active=True):
    root = parse(dump_path)
    # Pin the root at (0,0): its scene-canvas position must not offset children
    root.pos = (0, 0)
    root.anchor_min = root.anchor_max = (0.5, 0.5)
    layout(root, -root.size[0] / 2, -root.size[1] / 2, root.size[0], root.size[1])
    root.rect = (0, 0, root.size[0], root.size[1])
    for c in root.children:
        layout(c, 0, 0, root.size[0], root.size[1])
    rw, rh = round(root.size[0]), round(root.size[1])

    elements, children = [], []
    counter = [0]

    widget_layers = {}

    def emit(node):
        if not node.active and node is not root:
            return
        x, y, w, h = node.rect
        # The leather window background must be the widget's nine-slice BG
        # layer (inset -17 handles the texture's baked margins + gradient),
        # not a stretched element — proven recipe from the earlier panels.
        if node.image and 'EmbossedLeatherBorderInner' in node.image['sprite']:
            ppu_mult = node.image['ppu'] or 11.2
            bscale = round(1.57 / ppu_mult, 3)   # 0.019@80.4, 0.14@11.2 (proven panels)
            widget_layers['background'] = 'LeatherBackground'
            widget_layers['backgroundScale'] = bscale
            widget_layers['backgroundInset'] = -round(122 * bscale)
            col = node.image['color']
            widget_layers['backgroundTint'] = [round(col[0] * 255), round(col[1] * 255), round(col[2] * 255), 255]
            hm = harm_from_swapper(node.swapper)
            if hm: widget_layers['bgHarmonize'] = hm
            return
        # Rotated decorations (e.g. vertical slider poles serialized at their
        # unrotated size) come out wider than the window — skip on first pass.
        if w > root.size[0] * 1.05:
            return
        cx, cy, cw, chh = round(x), round(y), max(1, round(w)), max(1, round(h))
        if node.image and node.image['sprite']:
            tex = copy_texture(node.image['sprite'])
            if tex and node.image['type'] in (1, 2):
                tex = bake_variant(node.image['sprite'], tex, node.image['type'], node.image['ppu'], cw, chh)
            if tex:
                col = node.image['color']
                tint = [round(col[0] * 255), round(col[1] * 255), round(col[2] * 255), round(col[3] * 255)]
                eid = f'{prefix}_{counter[0]}'
                counter[0] += 1
                el = dict(id=eid, type='image', imagePath=tex, width=cw, height=chh, tintColor=tint)
                hm = harm_from_swapper(node.swapper)
                if hm: el['harmonize'] = hm
                elements.append(el)
                children.append(dict(name=f'{prefix.lower()}_{counter[0]}_{node.name[:18].replace(" ", "")}',
                                     element=eid, x=cx, y=cy, width=cw, height=chh, anchor=0))
        if node.tmp and node.tmp['text'] and 'DropShadow' not in node.name:
            tm = node.tmp
            font = tm.get('font', 'Quintessential')
            fs = max(8, round(tm['fontSize'] * (ROBOTO_RATIO if font == 'Roboto' else QUINT_RATIO)))
            fs = SIZE_OVERRIDES.get(f'{prefix}:{node.name}', SIZE_OVERRIDES.get(node.name, fs))
            colr = tm['color']
            fc = [round(colr[0] * 255), round(colr[1] * 255), round(colr[2] * 255), 255]
            # Near-white Quintessential faces render warm parchment via the
            # TMP DropShadow material — measured (240,218,178) on captures.
            if font == 'Quintessential' and fc[0] > 230 and fc[1] > 230 and fc[2] > 230:
                fc = list(WARM_FACE)
            align = {1: 'left', 2: 'center', 4: 'right'}.get(tm['align'], 'left')
            text = re.sub(r'<.*?>', '', tm['text'])
            eid = f'{prefix}_{counter[0]}'
            counter[0] += 1
            elements.append(dict(id=eid, type='text', width=cw, height=chh, tintColor=[255, 255, 255, 0],
                                 defaultText=text,
                                 textRegion=dict(x=0, y=0, w=cw, h=chh, align=align, valign='center',
                                                 fontFamily=font, fontSize=fs, fontColor=fc,
                                                 bold=tm['style'] == 1 or font == 'Roboto',
                                                 outlineWidth=1, outlineColor=[0, 0, 0, 150])))
            children.append(dict(name=f'{prefix.lower()}_{counter[0]}_{node.name[:18].replace(" ", "")}',
                                 element=eid, x=cx, y=cy, width=cw, height=chh, anchor=0))
        for c in node.children:
            emit(c)

    for c in root.children:
        emit(c)

    ed = json.load(open('assets/UI/definitions/elements.json', encoding='utf-8'))
    ed['elements'] = [e for e in ed['elements'] if not e['id'].startswith(prefix + '_')]
    ed['elements'] += elements
    json.dump(ed, open('assets/UI/definitions/elements.json', 'w', encoding='utf-8'), indent=2)

    wd = json.load(open('assets/UI/definitions/widgets.json', encoding='utf-8'))
    wd['widgets'] = [w for w in wd['widgets'] if w['id'] != widget_id]
    wdef = dict(id=widget_id, width=rw, height=rh, modal=False, children=children)
    wdef.update(widget_layers)
    wd['widgets'].append(wdef)
    json.dump(wd, open('assets/UI/definitions/widgets.json', 'w', encoding='utf-8'), indent=2)
    print(f'{widget_id}: {len(elements)} elements, {len(children)} children, {rw}x{rh}')


if __name__ == '__main__':
    convert(sys.argv[1], sys.argv[2], sys.argv[3])
