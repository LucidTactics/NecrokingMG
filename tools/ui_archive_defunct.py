"""Move widgets (and the elements/nine-slices they leave orphaned) out of
data/ui into the gitignored defunct/ui archive, for reference without bloating
the live definitions. Git history is the durable archive; this is local convenience.

    python tools/ui_archive_defunct.py SummonWolvesTip ShadowBoltTip SpiritFormTip

Safe: never archives anything still referenced by a remaining widget or by C# code.
Cascades to sub-widgets/elements/nine-slices that become unreferenced. Re-runnable
(accumulates into defunct/ui/archive.json).
"""
import json, os, re, sys

WDIR = 'data/ui'
DEFUNCT_DIR = 'defunct/ui'
ARCHIVE_FILE = os.path.join(DEFUNCT_DIR, 'archive.json')
# Drawn via a non-string-id path (HUDRenderer) — never auto-archive.
SPECIAL_LIVE = {'SpellSlot'}

targets = sys.argv[1:]
if not targets:
    print('usage: ui_archive_defunct.py <widgetId> [<widgetId> ...]'); sys.exit(1)

W = json.load(open(f'{WDIR}/widgets.json', encoding='utf-8'))
E = json.load(open(f'{WDIR}/elements.json', encoding='utf-8'))
N = json.load(open(f'{WDIR}/nine_slices.json', encoding='utf-8'))
widgets, elements, nineslices = W['widgets'], E['elements'], N['nineSlices']

code = ''
for r, _, fs in os.walk('Necroking'):
    for f in fs:
        if f.endswith('.cs'):
            code += open(os.path.join(r, f), encoding='utf-8', errors='ignore').read()
def code_ref(i): return ('"' + i + '"') in code or ("'" + i + "'") in code

def nested_widget_refs(w):
    out = []
    def walk(o):
        if isinstance(o, dict):
            for k, v in o.items():
                if k == 'widget' and isinstance(v, str): out.append(v)
                walk(v)
        elif isinstance(o, list):
            for x in o: walk(x)
    walk(w); return out

def all_string_values(obj):
    """Every string value anywhere in obj — catches element refs (element/
    overrideElement) AND nine-slice refs in layer fields (background/frame/
    stencil/...) without having to know each field name."""
    out = set()
    def walk(o):
        if isinstance(o, dict):
            for v in o.values():
                if isinstance(v, str): out.add(v)
                else: walk(v)
        elif isinstance(o, list):
            for x in o: walk(x)
    walk(obj); return out

# 1. cascade orphaned widgets: a widget becomes archivable if no REMAINING widget
#    nests it and it isn't code-referenced (live top-levels are code-referenced).
arch_w = set(targets)
while True:
    # A remaining widget that WAS nested (by some widget) but whose every nester is
    # now archived, and which isn't code-referenced or special-live, is now orphan.
    remaining = [w for w in widgets if w['id'] not in arch_w]
    new = set()
    for w in remaining:
        if w['id'] in SPECIAL_LIVE or code_ref(w['id']): continue
        nesters = [o['id'] for o in widgets if w['id'] in nested_widget_refs(o)]
        if nesters and all(n in arch_w for n in nesters):
            new.add(w['id'])
    if not new: break
    arch_w |= new

# 2. orphaned elements: id appears as no string value in any remaining widget,
#    and not code-referenced.
remaining = [w for w in widgets if w['id'] not in arch_w]
rem_strings = set()
for w in remaining: rem_strings |= all_string_values(w)
arch_e = {e['id'] for e in elements
          if e['id'] not in rem_strings and not code_ref(e['id'])}

# 3. orphaned nine-slices: id appears as no string value in any remaining widget
#    OR kept element (catches background/frame/stencil layer fields), not code-ref.
keep_e = [e for e in elements if e['id'] not in arch_e]
all_strings = set(rem_strings)
for e in keep_e: all_strings |= all_string_values(e)
arch_ns = {n['id'] for n in nineslices if n['id'] not in all_strings and not code_ref(n['id'])}

# 4. split + write archive
moved_w = [w for w in widgets if w['id'] in arch_w]
moved_e = [e for e in elements if e['id'] in arch_e]
moved_ns = [n for n in nineslices if n['id'] in arch_ns]

os.makedirs(DEFUNCT_DIR, exist_ok=True)
archive = {'widgets': [], 'elements': [], 'nineSlices': [], '_note':
    'Defunct UI defs removed from data/ui. Reference only; git history is canonical.'}
if os.path.exists(ARCHIVE_FILE):
    archive = json.load(open(ARCHIVE_FILE, encoding='utf-8'))
have_w = {w['id'] for w in archive['widgets']}
have_e = {e['id'] for e in archive['elements']}
have_n = {n['id'] for n in archive['nineSlices']}
archive['widgets'] += [w for w in moved_w if w['id'] not in have_w]
archive['elements'] += [e for e in moved_e if e['id'] not in have_e]
archive['nineSlices'] += [n for n in moved_ns if n['id'] not in have_n]
json.dump(archive, open(ARCHIVE_FILE, 'w', encoding='utf-8'), indent=2)

# 5. rewrite live defs
W['widgets'] = [w for w in widgets if w['id'] not in arch_w]
E['elements'] = [e for e in elements if e['id'] not in arch_e]
N['nineSlices'] = [n for n in nineslices if n['id'] not in arch_ns]
json.dump(W, open(f'{WDIR}/widgets.json', 'w', encoding='utf-8'), indent=2)
json.dump(E, open(f'{WDIR}/elements.json', 'w', encoding='utf-8'), indent=2)
json.dump(N, open(f'{WDIR}/nine_slices.json', 'w', encoding='utf-8'), indent=2)

print(f'archived {len(arch_w)} widgets, {len(arch_e)} elements, {len(arch_ns)} nine-slices -> {ARCHIVE_FILE}')
print(f'  widgets: {sorted(arch_w)}')
print(f'  data/ui now: {len(W["widgets"])} widgets, {len(E["elements"])} elements, {len(N["nineSlices"])} nine-slices')
