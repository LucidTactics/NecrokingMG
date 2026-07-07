"""Format the judgment workflow's structured result into a readable verdicts file.

Usage: python tools/format_verdicts.py <taskOutputFile> <outMd>
"""
import json
import re
import sys

src, dst = sys.argv[1], sys.argv[2]
data = json.load(open(src, encoding="utf-8", errors="replace"))["result"]

order = {"CONSOLIDATE": 0, "INVESTIGATE": 1, "KEEP_SEPARATE": 2}
sev = {"high": 0, "medium": 1, "low": 2}
lines = [f"judged={data['judged']}/{data['total']} failed={data['failed']}"]
counts = {"CONSOLIDATE": 0, "INVESTIGATE": 1 - 1, "KEEP_SEPARATE": 0}
counts = {"CONSOLIDATE": 0, "INVESTIGATE": 0, "KEEP_SEPARATE": 0}
for u in data["units"]:
    lines.append(f"\n## {u['unit']}")
    lines.append(f"HEADLINE: {u['headline']}")
    for f in sorted(u["findings"], key=lambda f: (order.get(f["verdict"], 9), sev.get(f["severity"], 9))):
        counts[f["verdict"]] = counts.get(f["verdict"], 0) + 1
        lines.append(f"- [{f['verdict']}/{f['severity']}] {f['title']}")
        lines.append(f"    {f['one_liner']}")
lines.insert(1, f"verdict counts: {counts}")
open(dst, "w", encoding="utf-8").write("\n".join(lines))
print(f"wrote {dst}; counts={counts}")
