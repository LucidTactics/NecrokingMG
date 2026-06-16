"""GMW clarity refactor.
1. Rename the live GrimoireDyn child names (gmw_N_Suffix) to self-documenting
   path/school-keyed names (scoped to GrimoireDyn only; the static GrimoireWindow
   import reference keeps its cryptic child names, like UnitTooltipWindow).
2. Rename the 60 tab/icon GMW_* elements used by the live grimoire (GMW_0..57 +
   GMW_171/172) to Grim_* globally (propagates to GrimoireWindow's refs too, so
   it stays valid). GMW_58..170 (static spell-tile bakes) are left untouched.

The child<->element index relation (verified): element index = child N - 1.
Path tab i (1..12): child backing gmw_{8+(i-1)*3}, frame +1, icon +2.
School tab j (1..5): child backing gmw_{44+(j-1)*3}, text +1, frame +2."""
import json, re, os

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
WJSON = os.path.join(ROOT, "assets/UI/definitions/widgets.json")
EJSON = os.path.join(ROOT, "assets/UI/definitions/elements.json")

PATHS = ["Shock", "Fire", "Metal", "Water", "Heavens", "Order",
         "Earth", "Chaos", "Spirit", "Nature", "Body", "Death"]
SCHOOLS = ["All", "Conjuration", "Alteration", "Evocation", "Construction"]

child_map, elem_map = {}, {}
def add(child, elem_n, role):
    child_map[child] = role
    elem_map[f"GMW_{elem_n}"] = "Grim_" + role

# specials (child N, element N-1)
add("gmw_1_SpellOverlay", 0, "SpellListOverlay")
add("gmw_2_WindowBorder", 1, "WindowBorder")
add("gmw_3_Row1", 2, "HeaderDivider")
add("gmw_4_Ribbon", 3, "TabStripRibbon")
add("gmw_5_Tab-Backing", 4, "PathTab_All_Backing")
add("gmw_6_ALLText", 5, "PathTab_All_Text")
add("gmw_7_Tab-Frame", 6, "PathTab_All_Frame")
for i, p in enumerate(PATHS, start=1):
    b = 8 + (i - 1) * 3
    add(f"gmw_{b}_Tab-Backing", b - 1, f"PathTab_{p}_Backing")
    add(f"gmw_{b+1}_Tab-Frame", b,     f"PathTab_{p}_Frame")
    add(f"gmw_{b+2}_PathIcon",  b + 1, f"PathTab_{p}_Icon")
for j, s in enumerate(SCHOOLS, start=1):
    b = 44 + (j - 1) * 3
    add(f"gmw_{b}_Tab-Backing", b - 1, f"SchoolTab_{s}_Backing")
    add(f"gmw_{b+1}_TabText",   b,     f"SchoolTab_{s}_Text")
    add(f"gmw_{b+2}_Tab-Frame", b + 1, f"SchoolTab_{s}_Frame")
add("gmw_172_Ribbon", 171, "TitleRibbon")
add("gmw_173_TitleText", 172, "TitleText")

wtext = open(WJSON, encoding="utf-8").read()
etext = open(EJSON, encoding="utf-8").read()

# --- validate target uniqueness + no pre-existing collisions ---
all_new = list(child_map.values()) + list(elem_map.values())
both = wtext + "\n" + etext
for new in set(elem_map.values()):
    if re.search(r'"' + re.escape(new) + r'"', both):
        raise SystemExit(f"ERROR: element target '{new}' already exists")

# --- isolate GrimoireDyn and rename its children only ---
def isolate(text, wid):
    m = re.search(r'"id"\s*:\s*"' + re.escape(wid) + r'"', text)
    start = text.rfind("{", 0, m.start())
    depth = 0
    for i in range(start, len(text)):
        if text[i] == "{": depth += 1
        elif text[i] == "}":
            depth -= 1
            if depth == 0: return start, i + 1
    raise SystemExit("unbalanced")

gs, ge = isolate(wtext, "GrimoireDyn")
section = wtext[gs:ge]
for old, new in child_map.items():
    section, n = re.subn(r'"' + re.escape(old) + r'"', '"' + new + '"', section)
    if n != 1:
        raise SystemExit(f"ERROR: child '{old}' matched {n} times in GrimoireDyn (expected 1)")
# every gmw_ child should now be renamed
leftover = re.findall(r'"(gmw_[0-9A-Za-z_-]+)"', section)
if leftover:
    raise SystemExit(f"ERROR: unmapped gmw_ children remain in GrimoireDyn: {set(leftover)}")
wtext = wtext[:gs] + section + wtext[ge:]

# --- global element rename (both files) ---
for old, new in elem_map.items():
    wtext = re.sub(r'"' + re.escape(old) + r'"', '"' + new + '"', wtext)
    etext = re.sub(r'"' + re.escape(old) + r'"', '"' + new + '"', etext)

# --- validate JSON + no stray renamed elements remain ---
for label, t in (("widgets", wtext), ("elements", etext)):
    try:
        json.loads(t)
    except Exception as e:
        raise SystemExit(f"ERROR: {label} invalid: {e}")
for old in elem_map:
    if re.search(r'"' + re.escape(old) + r'"', wtext + etext):
        raise SystemExit(f"ERROR: element '{old}' still present")
# GMW_58..170 must SURVIVE (static spell-tile bakes left alone)
survivors = [f"GMW_{n}" for n in range(58, 171)]
missing = [g for g in survivors if not re.search(r'"' + g + r'"', etext)]
if missing:
    raise SystemExit(f"ERROR: should-survive elements vanished: {missing[:5]}...")

open(WJSON, "w", encoding="utf-8", newline="\n").write(wtext)
open(EJSON, "w", encoding="utf-8", newline="\n").write(etext)
print(f"OK: renamed {len(child_map)} GrimoireDyn children + {len(elem_map)} elements.")
print(f"  GMW_58..170 ({len(survivors)} static bakes) left untouched. JSON valid.")
