#!/usr/bin/env python3
"""Promote the Game1 instance fields that GameRenderer now reaches via `_g.` from
`private` to `internal`, and delete the 5 original Game1.Render*.cs files (their
transformed copies are GameRenderer.*.cs).

Reads the exact field set emitted by extract_gamerenderer.py. Only swaps the
leading `private` token on a field *declaration* line (type ... _name ... ;|=);
method signatures / property bodies (which contain `(` ... `{`) are not matched.
"""
import os, re, json

ROOT = r"C:\Users\Johan\source\repos\Lucid\NecrokingMG\Necroking"
SNAP = r"C:\Users\Johan\AppData\Local\Temp\claude\C--Users-Johan-source-repos-Lucid-NecrokingMG\ce76c2cf-00f4-4527-bb5d-d7b545cf9f1e\scratchpad\orig_render"

# Files that may declare the borrowed fields (all non-render Game1 partials).
HOST_FILES = [
    "Game1.cs", "Game1.Animation.cs", "Game1.Spells.cs",
    "Game1.Crafting.cs", "Game1.Dev.cs", "Game1.DevData.cs",
]
ORIGINALS = [
    "Game1.Render.cs", "Game1.Render.World.cs", "Game1.Render.HUD.cs",
    "Game1.Render.Units.cs", "Game1.Render.Corpses.cs",
]

def main():
    with open(os.path.join(SNAP, "prefixed_fields.json")) as f:
        names = json.load(f)

    promoted = {n: 0 for n in names}
    for fn in HOST_FILES:
        path = os.path.join(ROOT, fn)
        with open(path, encoding="utf-8") as f:
            text = f.read()
        for n in names:
            pat = re.compile(
                r'^(?P<i>[ \t]*)private(?P<rest>\s+[^;{}\n]*?\b' + re.escape(n) + r'\b[^;{}\n]*?[;=])',
                re.MULTILINE)
            text, cnt = pat.subn(lambda m: m.group('i') + 'internal' + m.group('rest'), text)
            promoted[n] += cnt
        with open(path, "w", encoding="utf-8") as f:
            f.write(text)
        print(f"processed {fn}")

    missing = [n for n, c in promoted.items() if c == 0]
    print(f"\npromoted {sum(1 for c in promoted.values() if c)}/{len(names)} fields")
    if missing:
        print(f"NOT FOUND ({len(missing)}) — verify by hand or these are static/props:")
        print("\n".join("  " + m for m in missing))

    for fn in ORIGINALS:
        p = os.path.join(ROOT, fn)
        if os.path.exists(p):
            os.remove(p)
            print(f"removed {fn}")

if __name__ == "__main__":
    main()
