"""Insert a `regroup` spell def into data/spells.json by cloning the `order_attack`
object verbatim (preserving the file's exact formatting) and overriding a few
header fields. Idempotent: bails if `regroup` already exists."""
import json, re, sys, pathlib

path = pathlib.Path("data/spells.json")
text = path.read_text(encoding="utf-8")

if '"id": "regroup"' in text:
    print("regroup already present — nothing to do")
    sys.exit(0)

# Locate the order_attack object and extract it by brace-matching.
anchor = text.index('"id": "order_attack"')
start = text.rindex("{", 0, anchor)
depth = 0
end = None
for i in range(start, len(text)):
    c = text[i]
    if c == "{":
        depth += 1
    elif c == "}":
        depth -= 1
        if depth == 0:
            end = i + 1
            break
assert end is not None, "could not brace-match order_attack object"
obj_text = text[start:end]

# Build the clone with overridden header fields. Each pattern is unique within
# the object, so these replacements are unambiguous.
clone = obj_text
clone = clone.replace('"name": "Command"', '"name": "Regroup"', 1)
clone = clone.replace('"id": "order_attack"', '"id": "regroup"', 1)
clone = clone.replace('"range": 999', '"range": 0', 1)
clone = clone.replace('"cooldown": 5', '"cooldown": 0', 1)
assert clone.count('"id": "regroup"') == 1, "override sanity check failed"

# Insert the clone immediately after the order_attack object. The original object
# is followed by a comma in the array; we splice ",\n<indent><clone>" in so the
# clone gets that trailing comma and order_attack keeps a comma too.
indent = text[text.rindex("\n", 0, start) + 1 : start]  # leading whitespace of the object
new_text = text[:end] + ",\n" + indent + clone + text[end:]

# Validate it still parses.
json.loads(new_text)
path.write_text(new_text, encoding="utf-8")
print("inserted regroup spell def after order_attack")
