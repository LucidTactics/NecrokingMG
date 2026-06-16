"""Merge UTD_atRow into UTD_eqRow (the shared unit stat-row template).
1. Repoint UTD_atBox's rows from UTD_atRow -> UTD_eqRow.
2. Delete the now-orphan UTD_atRow widget definition.
Validates JSON parses, UTD_atRow id is gone, UTD_eqRow survives, and the
3 atBox rows now reference UTD_eqRow."""
import json, re, os

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
WJSON = os.path.join(ROOT, "assets/UI/definitions/widgets.json")
text = open(WJSON, encoding="utf-8").read()

# 1. Repoint child widget refs (only the 3 atBox rows use this).
n_ref = len(re.findall(r'"widget"\s*:\s*"UTD_atRow"', text))
if n_ref != 3:
    raise SystemExit(f"ERROR: expected 3 'widget: UTD_atRow' refs, found {n_ref}")
text = re.sub(r'("widget"\s*:\s*")UTD_atRow(")', r"\1UTD_eqRow\2", text)

# 2. Delete the UTD_atRow widget def (brace-matched object) + adjacent comma.
m = re.search(r'"id"\s*:\s*"UTD_atRow"', text)
if not m:
    raise SystemExit("ERROR: UTD_atRow definition not found")
start = text.rfind("{", 0, m.start())
depth = 0
end = None
for i in range(start, len(text)):
    if text[i] == "{":
        depth += 1
    elif text[i] == "}":
        depth -= 1
        if depth == 0:
            end = i + 1
            break
if end is None:
    raise SystemExit("ERROR: unbalanced braces for UTD_atRow")
# absorb a following comma, else a preceding one
tm = re.match(r"\s*,", text[end:])
if tm:
    end += tm.end()
else:
    pm = re.search(r",\s*$", text[:start])
    if pm:
        start = pm.start()
text = text[:start] + text[end:]

# validate
if re.search(r'"id"\s*:\s*"UTD_atRow"', text):
    raise SystemExit("ERROR: UTD_atRow id still present")
if re.search(r'UTD_atRow', text):
    raise SystemExit("ERROR: stray UTD_atRow reference remains")
if not re.search(r'"id"\s*:\s*"UTD_eqRow"', text):
    raise SystemExit("ERROR: UTD_eqRow definition vanished!")
if len(re.findall(r'"widget"\s*:\s*"UTD_eqRow"', text)) < 3:
    raise SystemExit("ERROR: atBox rows not repointed to UTD_eqRow")
try:
    json.loads(text)
except Exception as e:
    raise SystemExit(f"ERROR: invalid JSON: {e}")

open(WJSON, "w", encoding="utf-8", newline="\n").write(text)
print("OK: merged UTD_atRow -> UTD_eqRow; atBox repointed; file valid JSON.")
