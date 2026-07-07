"""Extract the dup_hints array from the labeling workflow's task output file
and write it as readable text for the wildcard-sweep agent.

Usage: python tools/extract_dup_hints.py <taskOutputFile> <outTxt>
"""
import json
import re
import sys

src, dst = sys.argv[1], sys.argv[2]
text = open(src, encoding="utf-8", errors="replace").read()

# The result JSON is embedded in the output; find the dup_hints array.
m = re.search(r'"dup_hints"\s*:\s*\[', text)
if not m:
    print("dup_hints not found")
    sys.exit(1)

# Parse the JSON array by brace matching from the '['.
start = m.end() - 1
depth = 0
in_str = False
esc = False
for i in range(start, len(text)):
    c = text[i]
    if in_str:
        if esc:
            esc = False
        elif c == "\\":
            esc = True
        elif c == '"':
            in_str = False
    else:
        if c == '"':
            in_str = True
        elif c == "[":
            depth += 1
        elif c == "]":
            depth -= 1
            if depth == 0:
                arr = json.loads(text[start:i + 1])
                out = "\n\n".join(arr)
                open(dst, "w", encoding="utf-8").write(out)
                print(f"wrote {len(arr)} hints to {dst}")
                sys.exit(0)
print("unterminated array")
sys.exit(1)
