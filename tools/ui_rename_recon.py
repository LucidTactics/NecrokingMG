"""Recon for the UI naming/redundancy refactor. Read-only.
Enumerates widget locations, the grimoire gmw_* child map, and cross-file
reference counts for the in-scope IDs. Output is a report to stdout."""
import json, re, os, glob

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
WJSON = os.path.join(ROOT, "assets/UI/definitions/widgets.json")
EJSON = os.path.join(ROOT, "assets/UI/definitions/elements.json")
CS = glob.glob(os.path.join(ROOT, "Necroking/**/*.cs"), recursive=True)

wtext = open(WJSON, encoding="utf-8").read()
etext = open(EJSON, encoding="utf-8").read()
wdoc = json.loads(wtext)
edoc = json.loads(etext)

# --- 1. Top-level widget line ranges for the panels we touch ---
print("=== widget 'id' line numbers (widgets.json) ===")
for m in re.finditer(r'"id"\s*:\s*"([A-Za-z0-9_]+)"', wtext):
    wid = m.group(1)
    if re.match(r"(UTD_|RTD_|GM_Tile|GrimoireDyn|UnitSheetDyn|ResourceTooltipDyn|UD_|AB_)", wid):
        ln = wtext.count("\n", 0, m.start()) + 1
        print(f"  L{ln:<6} {wid}")

# --- 2. Grimoire gmw_* child map (name -> element) ---
def find_widget(doc, wid):
    for w in doc["widgets"]:
        if w.get("id") == wid:
            return w
    return None

print("\n=== grimoire children (gmw_* and tile*) name -> element ===")
grim = find_widget(wdoc, "GrimoireDyn")
if grim:
    for c in grim.get("children", []):
        nm = c.get("name", "")
        if nm.startswith("gmw_") or nm.startswith("tile"):
            el = c.get("element") or c.get("widget") or c.get("background") or ""
            txt = c.get("text", "")
            print(f"  {nm:<24} el={el:<28} text={txt!r}")

# --- 3. Reference counts for in-scope IDs across all files ---
IDS = [
    "GMT_5", "SBSlotBg", "GM_Tile",
    "RTD_Row", "RTD_Header", "RTD_TabBox", "RTD_RowLabel", "RTD_RowValue",
    "UTD_eqRow", "UTD_atRow", "UTD_eqBox", "UTD_atBox",
    "UTD_eqSection", "UTD_atSection", "UTD_eqTitle", "UTD_atTitle",
    "UTD_StatsSection", "UTD_DescSection", "UTD_AbilitiesSection",
    "UD_Box", "UD_PortraitFrame", "UD_PortraitFrameShadow", "UD_BoxPattern",
    "AB_StatBox", "AB_BoxPattern", "AB_TitleSwatch",
]
def count_quoted(text, ident):
    return len(re.findall(r'"' + re.escape(ident) + r'"', text))
print("\n=== quoted-token reference counts ('\"ID\"') ===")
print(f"  {'ID':<26} widgets elements   code(files)")
cs_join = {p: open(p, encoding="utf-8", errors="ignore").read() for p in CS}
for ident in IDS:
    wc = count_quoted(wtext, ident)
    ec = count_quoted(etext, ident)
    files = [os.path.relpath(p, ROOT) for p, t in cs_join.items() if count_quoted(t, ident)]
    print(f"  {ident:<26} {wc:<7} {ec:<10} {','.join(files) if files else '-'}")

# --- 4. Any element 'id' in elements.json matching in-scope element names ---
print("\n=== element ids in elements.json (in-scope) ===")
for el in edoc.get("elements", edoc if isinstance(edoc, list) else []):
    if isinstance(el, dict):
        eid = el.get("id", "")
        if eid in ("GMT_5", "SBSlotBg", "GMW_4") or eid.startswith("AB_") or eid.startswith("UD_") or eid.startswith("EQ_") or eid.startswith("AT_"):
            print(f"  {eid:<22} path={el.get('imagePath','')}")
