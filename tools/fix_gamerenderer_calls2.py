#!/usr/bin/env python3
"""Third pass: promote the 4 remaining Game1 helpers GameRenderer calls to
`internal`, and switch the 2 *static* render methods' external call sites from
the instance ref `_gameRenderer.` to the type name `GameRenderer.`."""
import os, re

ROOT = r"C:\Users\Johan\source\repos\Lucid\NecrokingMG\Necroking"

PROMOTE = {
    "Game1.Animation.cs": ["UpdateWadingSinkOffsets"],
    "Game1.Dev.cs": ["CompletePendingDevScreenshot"],
    "Game1.cs": ["EnsureInventoryUIsInitialized", "GetItemTextureByPath"],
}
STATIC_METHODS = ["PickIdleFrames", "NearestAggroNode"]
HOST_FILES = ["Game1.cs", "Game1.Dev.cs", "Game1.Animation.cs"]

for fn, names in PROMOTE.items():
    p = os.path.join(ROOT, fn)
    t = open(p, encoding="utf-8").read()
    for m in names:
        pat = re.compile(r'^(?P<i>[ \t]*)private(?P<rest>\s+[^;{}\n]*?\b' + re.escape(m) + r'\s*\()',
                         re.MULTILINE)
        t, c = pat.subn(lambda mo: mo.group('i') + 'internal' + mo.group('rest'), t)
        print(f"promote {m} in {fn}: {c}")
    open(p, "w", encoding="utf-8").write(t)

for fn in HOST_FILES:
    p = os.path.join(ROOT, fn)
    t = open(p, encoding="utf-8").read()
    for m in STATIC_METHODS:
        t, c = re.subn(r'_gameRenderer\.' + re.escape(m) + r'\b', "GameRenderer." + m, t)
        if c:
            print(f"static-fix {m} in {fn}: {c}")
    open(p, "w", encoding="utf-8").write(t)
