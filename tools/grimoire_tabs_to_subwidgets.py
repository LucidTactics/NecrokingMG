"""One-shot: convert the grimoire's flat path/school tab elements in GrimoireDyn
into horizontal-layout bars of sub-widget instances (GrimPathTab / GrimSchoolTab),
whose Backing/Frame fill the tab and whose Icon/Text is per-instance via the new
childOverride 'overrideElement'. Mirrors the SkillBookTab/TabBar pattern.

Run once:  python tools/grimoire_tabs_to_subwidgets.py
"""
import json, re, collections

WJSON = 'data/ui/widgets.json'
PATH_ORDER = ['All', 'Shock', 'Fire', 'Metal', 'Water', 'Heavens', 'Order',
              'Earth', 'Chaos', 'Spirit', 'Nature', 'Body', 'Death']
SCHOOL_ORDER = ['All', 'Conjuration', 'Alteration', 'Evocation', 'Construction']

wd = json.load(open(WJSON, encoding='utf-8'))
g = next(w for w in wd['widgets'] if w['id'] == 'GrimoireDyn')

# --- collect tab parts grouped by key ---
paths, schools = collections.defaultdict(dict), collections.defaultdict(dict)
tab_names = set()
for c in g['children']:
    m = re.match(r'(PathTab|SchoolTab)_([A-Za-z0-9]+)_(\w+)', c['name'])
    if not m:
        continue
    kind, key, part = m.groups()
    (paths if kind == 'PathTab' else schools)[key][part] = c
    tab_names.add(c['name'])

# --- sub-widget definitions (children inherit tab size via SizeMode fill) ---
def fill(name, element, ns=False):
    d = dict(name=name, element=element, x=0, y=0, width=0, height=0,
             anchor=0, sizeMode='fill')
    if ns: d['nineSliceScale'] = 0.8
    return d

grim_path_tab = dict(id='GrimPathTab', width=51, height=45, backgroundScale=1, modal=False,
    children=[
        fill('Backing', 'Grim_TabBacking'),
        fill('Frame', 'SkillTab_Frame', ns=True),
        # Icon: fixed 24x24, centred (anchor 4) so it stays centred at any tab width.
        dict(name='Icon', element='Grim_PathTab_Shock_Icon', x=-12, y=-12,
             width=24, height=24, anchor=4),
    ])
grim_school_tab = dict(id='GrimSchoolTab', width=144, height=35, backgroundScale=1, modal=False,
    children=[
        fill('Backing', 'Grim_TabBacking'),
        fill('Frame', 'SkillTab_Frame', ns=True),
        fill('Text', 'Grim_SchoolTab_All_Text'),   # element swapped per instance
    ])

# --- build the two horizontal bars ---
def path_icon_elem(key):
    return 'Grim_PathTab_All_Text' if key == 'All' else f'Grim_PathTab_{key}_Icon'

# Path bar: uniform 51-wide tabs (icons stay centred), -1 spacing so frames abut.
path_children = []
for i, key in enumerate(PATH_ORDER):
    path_children.append(dict(name=f'tab{i}', widget='GrimPathTab', x=0, y=0,
        width=51, height=45, anchor=0,
        childOverrides=[dict(childIndex=2, overrideElement=path_icon_elem(key))]))
path_bar = dict(id='PathTabBar', width=680, height=45, backgroundScale=1, modal=False,
    layout='horizontal', layoutPadding=0, layoutSpacing=-1,
    layoutPadTop=0, layoutPadBottom=0, layoutPadLeft=0, layoutPadRight=0,
    layoutSpacingX=0, layoutSpacingY=0, children=path_children)

# School bar: per-tab widths from the original frame, -1 spacing.
school_children = []
for i, key in enumerate(SCHOOL_ORDER):
    fr = schools[key].get('Frame') or schools[key].get('Backing')
    w = fr['width']
    school_children.append(dict(name=f'tab{i}', widget='GrimSchoolTab', x=0, y=0,
        width=w, height=35, anchor=0,
        childOverrides=[dict(childIndex=2, overrideElement=f'Grim_SchoolTab_{key}_Text')]))
school_bar = dict(id='SchoolTabBar', width=680, height=35, backgroundScale=1, modal=False,
    layout='horizontal', layoutPadding=0, layoutSpacing=-1,
    layoutPadTop=0, layoutPadBottom=0, layoutPadLeft=0, layoutPadRight=0,
    layoutSpacingX=0, layoutSpacingY=0, children=school_children)

# --- bar placement = first tab's frame top-left ---
def origin(group, first):
    fr = group[first].get('Frame') or group[first].get('Backing')
    return fr['x'], fr['y']
sx, sy = origin(schools, 'All')
px, py = origin(paths, 'All')
school_inst = dict(name='SchoolTabBar', widget='SchoolTabBar', x=sx, y=sy, width=680, height=35, anchor=0)
path_inst   = dict(name='PathTabBar',   widget='PathTabBar',   x=px, y=py, width=680, height=45, anchor=0)

# --- rebuild GrimoireDyn children: drop flat tabs, insert the two bars where the
#     first tab child was (preserve draw order relative to the rest of the chrome) ---
new_children, inserted = [], False
for c in g['children']:
    if c['name'] in tab_names:
        if not inserted:
            new_children += [school_inst, path_inst]
            inserted = True
        continue
    new_children.append(c)
g['children'] = new_children

# --- register the sub-widgets AND the two bar container widgets ---
for sub in (grim_path_tab, grim_school_tab, path_bar, school_bar):
    wd['widgets'] = [w for w in wd['widgets'] if w['id'] != sub['id']]
    wd['widgets'].insert(0, sub)

json.dump(wd, open(WJSON, 'w', encoding='utf-8'), indent=2)
print(f'GrimoireDyn: now {len(new_children)} children (removed {len(tab_names)} flat tab parts)')
print(f'PathTabBar: {len(path_children)} tabs @ ({px},{py}); SchoolTabBar: {len(school_children)} tabs @ ({sx},{sy})')
print('School widths:', [c["width"] for c in school_children])
