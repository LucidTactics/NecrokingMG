"""UI structural audit against the ui-widget-design best practices skill.
Reports: dead/reference widgets, element redundancy (per-size bakes + cross-window
shared images), orphans, flatness, layout/sizeMode coverage. Re-run as you fix
things to watch the numbers improve.

    python tools/ui_audit.py
"""
import json, os, re, collections

WD = json.load(open('data/ui/widgets.json', encoding='utf-8'))['widgets']
ELS = json.load(open('data/ui/elements.json', encoding='utf-8'))['elements']
WMAP = {w['id']: w for w in WD}
ELMAP = {e['id']: e for e in ELS}

# --- read all C# so we can spot code-referenced widget/element ids ---
CODE = ''
for root, _, files in os.walk('Necroking'):
    for f in files:
        if f.endswith('.cs'):
            CODE += open(os.path.join(root, f), encoding='utf-8', errors='ignore').read()

def code_ref(idstr):
    return ('"' + idstr + '"') in CODE or ("'" + idstr + "'") in CODE

def nested_widgets(w):
    out = []
    def walk(o):
        if isinstance(o, dict):
            for k, v in o.items():
                if k == 'widget' and isinstance(v, str): out.append(v)
                walk(v)
        elif isinstance(o, list):
            for x in o: walk(x)
    walk(w); return out

# Drawn roots = widgets whose id is code-referenced (DrawWidget/WidgetId/etc).
# NOTE: a few widgets are drawn via special paths (e.g. SpellSlot through
# HUDRenderer.DrawWidgetBackground) — list them here so they aren't flagged dead.
SPECIAL_LIVE = {'SpellSlot'}
drawn_roots = {i for i in WMAP if code_ref(i)} | (SPECIAL_LIVE & set(WMAP))

# reachability from drawn roots through nested widget refs
seen, stack = set(), list(drawn_roots)
while stack:
    x = stack.pop()
    if x in seen or x not in WMAP: continue
    seen.add(x); stack += nested_widgets(WMAP[x])
not_drawn = [i for i in WMAP if i not in seen]

# elements used per widget (element / overrideElement / childOverride.overrideElement)
elwidgets = collections.defaultdict(set)
for w in WD:
    for c in w.get('children', []):
        for k in ('element', 'overrideElement'):
            if c.get(k): elwidgets[c[k]].add(w['id'])
        for co in c.get('childOverrides', []) or []:
            if co.get('overrideElement'): elwidgets[co['overrideElement']].add(w['id'])

nd = set(not_drawn)
excl_dead = [e for e, ws in elwidgets.items() if ws and ws <= nd]
live_els = {e for e, ws in elwidgets.items() if ws & seen}
orphan_els = [e['id'] for e in ELS if e['id'] not in elwidgets and not code_ref(e['id'])]

def basefam(p):
    n = (p or '').split('/')[-1]
    return re.sub(r'\.png$', '', re.sub(r'_?\d+x\d+', '', n), flags=re.I)

# per-size baked frame/swatch families (each ~ one nine-slice opportunity)
fam = collections.defaultdict(list)
for e in ELS:
    if e.get('type') == 'image' and re.search(r'baked_|BlueSwath|Swatch1|RenaiThin', e.get('imagePath') or ''):
        fam[basefam(e['imagePath'])].append(e['id'])

# same source image wrapped by >1 element (cross-window soft redundancy)
byimg = collections.defaultdict(list)
for e in ELS:
    if e.get('type') == 'image' and e.get('imagePath'):
        byimg[e['imagePath']].append(e['id'])
shared_img = {p: v for p, v in byimg.items() if len(v) > 1}

print('=' * 68)
print('UI STRUCTURAL AUDIT')
print('=' * 68)
print(f'widgets: {len(WD)} ({len(seen)} live / {len(not_drawn)} not-drawn)')
print(f'elements: {len(ELS)} ({len(live_els)} used by live widgets, '
      f'{len(excl_dead)} exclusive to not-drawn, {len(orphan_els)} orphan)')
print()
print('-- NOT-DRAWN widgets (dead / import-reference; archive or delete) --')
for i in sorted(not_drawn, key=lambda i: -len(WMAP[i].get('children', []))):
    print(f'   {i:24} {len(WMAP[i].get("children", [])):>3} children   code-ref={code_ref(i)}')
print()
print(f'-- per-size-baked families (each -> ONE nine-slice); * = used by a LIVE widget --')
for f, ids in sorted(fam.items(), key=lambda kv: -len(kv[1])):
    live = any(set(elwidgets.get(i, ())) & seen for i in ids)
    print(f'   {"*" if live else " "} {len(ids):>2}x {f:32} {ids[:4]}')
print()
print(f'-- source images wrapped by >1 element ({len(shared_img)} images) --')
for p, ids in sorted(shared_img.items(), key=lambda kv: -len(kv[1]))[:12]:
    print(f'   {len(ids)}x {p.split("/")[-1]:30} {ids}')
print()
print(f'-- orphan elements (no widget/code ref) -- {orphan_els}')
print()
print('-- flat live widgets (>=12 element children, no layout, no sub-widgets) --')
for w in WD:
    if w['id'] not in seen: continue
    ch = w.get('children', [])
    elem = sum(1 for c in ch if c.get('element'))
    sub = sum(1 for c in ch if c.get('widget'))
    if elem >= 12 and not w.get('layout') and sub == 0:
        print(f'   {w["id"]:24} {elem} flat element children')
