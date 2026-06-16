"""Rename cryptic UI widget/element IDs to clear names.
Whole quoted-token replacement ("OldId" -> "NewId") across widgets.json,
elements.json, and Necroking/**/*.cs. Validates: map values unique, no new
id already exists, JSON still parses. Reports per-id counts.

Scope: the reusable semantic IDs of the grimoire/HUD + unit-panel/tooltip
families. NOT the machine-baked numbered families (GMT_0..22, SBT_*, GMW_*)."""
import json, re, os, glob

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
WJSON = os.path.join(ROOT, "assets/UI/definitions/widgets.json")
EJSON = os.path.join(ROOT, "assets/UI/definitions/elements.json")
CS = glob.glob(os.path.join(ROOT, "Necroking/**/*.cs"), recursive=True)

RENAME = {
    # grimoire / HUD reusable elements + tile
    "GMT_5": "SpiderFrameBorder",
    "SBSlotBg": "SpellSlotBg",
    "GM_Tile": "GrimoireSpellTile",
    # resource / stat tooltip
    "RTD_Row": "TooltipStatRow",
    "RTD_Header": "TooltipStatHeader",
    "RTD_TabBox": "TooltipStatList",
    "RTD_RowLabel": "TooltipStatRowLabel",
    "RTD_RowValue": "TooltipStatRowValue",
    # unit sheet: sections / boxes / rows / titles
    "UTD_eqRow": "UnitStatRow",
    "UTD_eqBox": "UnitEquipmentBox",
    "UTD_atBox": "UnitAttackBox",
    "UTD_eqSection": "UnitEquipmentSection",
    "UTD_atSection": "UnitAttackSection",
    "UTD_eqTitle": "UnitEquipmentTitle",
    "UTD_atTitle": "UnitAttackTitle",
    "UTD_StatsSection": "UnitStatsGrid",
    "UTD_DescSection": "UnitDescSection",
    "UTD_AbilitiesSection": "UnitAbilitiesSection",
    # unit description (UD_) elements/widgets
    "UD_Box": "UnitDescBox",
    "UD_BoxPattern": "UnitDescPattern",
    "UD_Description": "UnitDescText",
    "UD_PortraitFrame": "UnitPortraitFrame",
    "UD_PortraitFrameShadow": "UnitPortraitFrameShadow",
    "UD_PortraitParchment": "UnitPortraitParchment",
    "UD_PortraitStencil": "UnitPortraitStencil",
    "UD_TitleHeraldry": "UnitTitleHeraldry",
    "UD_TitleSwatchImg": "UnitTitleSwatch",
    "UD_TitleText": "UnitTitleText",
    "UD_TitleTextShadow": "UnitTitleTextShadow",
    "UD_UnitPortrait": "UnitPortrait",
    # abilities row (AB_)
    "AB_StatBox": "AbilitiesBox",
    "AB_BoxPattern": "AbilitiesPattern",
    "AB_Title": "AbilitiesTitle",
    "AB_TitleHeraldry": "AbilitiesTitleHeraldry",
    "AB_TitleSwatch": "AbilitiesTitleSwatch",
    "AB_Icon_Flying": "AbilityIcon_Flying",
}

files = {WJSON: open(WJSON, encoding="utf-8").read(),
         EJSON: open(EJSON, encoding="utf-8").read()}
for p in CS:
    files[p] = open(p, encoding="utf-8", errors="ignore").read()
alltext = "\n".join(files.values())

# --- validation ---
vals = list(RENAME.values())
if len(vals) != len(set(vals)):
    raise SystemExit("ERROR: duplicate target names in RENAME map")
for old, new in RENAME.items():
    if re.search(r'"' + re.escape(new) + r'"', alltext):
        raise SystemExit(f"ERROR: target '{new}' already exists as a quoted token")
for old in RENAME:
    if not re.search(r'"' + re.escape(old) + r'"', alltext):
        print(f"  WARN: source '{old}' not found anywhere (already renamed?)")

# --- apply ---
report = {}
for path, text in files.items():
    total = 0
    for old, new in RENAME.items():
        text, n = re.subn(r'"' + re.escape(old) + r'"', '"' + new + '"', text)
        if n:
            report.setdefault(old, {})[os.path.relpath(path, ROOT)] = n
            total += n
    files[path] = text

# --- validate JSON ---
for jp in (WJSON, EJSON):
    try:
        json.loads(files[jp])
    except Exception as e:
        raise SystemExit(f"ERROR: {os.path.basename(jp)} invalid after rename: {e}")
# no stray old ids remain anywhere
after = "\n".join(files.values())
for old in RENAME:
    if re.search(r'"' + re.escape(old) + r'"', after):
        raise SystemExit(f"ERROR: '{old}' still present after rename")

# --- write ---
for path, text in files.items():
    open(path, "w", encoding="utf-8", newline="\n").write(text)

print(f"OK: renamed {len(RENAME)} ids; both JSON files valid.")
for old in RENAME:
    locs = report.get(old, {})
    print(f"  {old:<24} -> {RENAME[old]:<26} " +
          ", ".join(f"{k.split(chr(92))[-1].split('/')[-1]}:{v}" for k, v in locs.items()))
