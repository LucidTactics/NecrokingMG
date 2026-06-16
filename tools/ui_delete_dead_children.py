"""Remove dead UI child blocks from widgets.json by child name.
Each child is a FLAT JSON object (no nested braces), so for a given
"name": "<x>" we expand to the enclosing {...} and drop it plus one
adjacent comma. Validates the file still parses and that kept names
survive. Idempotent-ish: errors out if a target name isn't found."""
import json, re, sys, os

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
WJSON = os.path.join(ROOT, "assets/UI/definitions/widgets.json")

DEAD = ["ab_box", "ab_boxpattern"] + [f"ab_r{i}_icon" for i in range(12)]
KEEP_MUST_SURVIVE = ["ab_title_swatch", "ab_title_heraldry", "ab_title"]

text = open(WJSON, encoding="utf-8").read()
orig = text

def isolate_widget(text, wid):
    """Return (start, end) covering the {...} object whose id is wid,
    brace-matched (the object contains nested children with braces)."""
    m = re.search(r'"id"\s*:\s*"' + re.escape(wid) + r'"', text)
    if not m:
        raise SystemExit(f"ERROR: widget '{wid}' not found")
    start = text.rfind("{", 0, m.start())
    depth = 0
    for i in range(start, len(text)):
        if text[i] == "{":
            depth += 1
        elif text[i] == "}":
            depth -= 1
            if depth == 0:
                return start, i + 1
    raise SystemExit(f"ERROR: unbalanced braces for '{wid}'")

def remove_child(text, name):
    m = re.search(r'"name"\s*:\s*"' + re.escape(name) + r'"', text)
    if not m:
        raise SystemExit(f"ERROR: child '{name}' not found")
    # enclosing flat object: nearest '{' before, nearest '}' after
    lo = text.rfind("{", 0, m.start())
    hi = text.find("}", m.end())
    if lo == -1 or hi == -1:
        raise SystemExit(f"ERROR: braces for '{name}' not found")
    block_start, block_end = lo, hi + 1
    # absorb a trailing comma (this element is followed by a sibling)...
    rest = text[block_end:]
    tm = re.match(r"\s*,", rest)
    if tm:
        block_end += tm.end()
    else:
        # ...or a preceding comma (this was the last sibling)
        pm = re.search(r",\s*$", text[:block_start])
        if pm:
            block_start = pm.start()
    return text[:block_start] + text[block_end:]

# Operate ONLY within UTD_AbilitiesSection — the same child names also
# exist in the static UnitTooltipWindow import reference, which must NOT change.
sec_start, sec_end = isolate_widget(text, "UTD_AbilitiesSection")
section = text[sec_start:sec_end]
for nm in DEAD:
    section = remove_child(section, nm)

# sanity within the section: dead gone, kept present
for nm in DEAD:
    if re.search(r'"name"\s*:\s*"' + re.escape(nm) + r'"', section):
        raise SystemExit(f"ERROR: '{nm}' still present in section")
for nm in KEEP_MUST_SURVIVE:
    if not re.search(r'"name"\s*:\s*"' + re.escape(nm) + r'"', section):
        raise SystemExit(f"ERROR: kept child '{nm}' vanished!")

text = text[:sec_start] + section + text[sec_end:]
try:
    json.loads(text)
except Exception as e:
    raise SystemExit(f"ERROR: result is not valid JSON: {e}")

open(WJSON, "w", encoding="utf-8", newline="\n").write(text)
print(f"OK: removed {len(DEAD)} dead children; file still valid JSON.")
print(f"  lines {orig.count(chr(10))+1} -> {text.count(chr(10))+1}")
