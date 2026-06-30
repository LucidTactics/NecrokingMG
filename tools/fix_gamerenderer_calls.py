#!/usr/bin/env python3
"""Second pass: wire the cross-boundary calls the compiler flagged.

  A) In GameRenderer*.cs — calls to non-render Game1 members:
       _g.<method> for instance helpers, Game1.WorldSize for the const,
       base.Draw -> _g.BaseDraw.
  B) Render methods now living in GameRenderer that Game1/Dev/Animation call:
       promote their definition to `internal`, and prefix the call sites with
       `_gameRenderer.`.
"""
import os, re

ROOT = r"C:\Users\Johan\source\repos\Lucid\NecrokingMG\Necroking"

RENDER_FILES = ["GameRenderer.Draw.cs", "GameRenderer.World.cs", "GameRenderer.Hud.cs",
                "GameRenderer.Units.cs", "GameRenderer.Corpses.cs"]
HOST_FILES = ["Game1.cs", "Game1.Dev.cs", "Game1.Animation.cs"]

# A) non-render Game1 instance helpers called from render -> _g.
G_METHODS = ["CompletePendingDevScreenshot", "UpdateWadingSinkOffsets",
             "EnsureInventoryUIsInitialized", "GetItemTextureByPath"]

# B) render methods that external (Game1/Dev/Animation) code calls.
RENDER_METHODS = ["PickIdleFrames", "BakeAllCorpseCentroids", "DrawUnitIdleSprite",
                  "PickHoveredObject", "UpdateSkillLearnToasts", "UpdateSkillLearnToastInput",
                  "GetAggressionBarLayout", "NearestAggroNode", "GetScenarioGridLayout",
                  "ToggleCoreMenu"]

def prefix_calls(text, names, prefix):
    n = 0
    for m in names:
        pat = re.compile(r'(?<![\w.])' + re.escape(m) + r'(?=\s*\()')
        text, c = pat.subn(prefix + m, text)
        n += c
    return text, n

def main():
    # A) render files
    for fn in RENDER_FILES:
        p = os.path.join(ROOT, fn)
        t = open(p, encoding="utf-8").read()
        t, a = prefix_calls(t, G_METHODS, "_g.")
        t2 = re.sub(r'(?<![\w.])WorldSize\b', "Game1.WorldSize", t)
        wc = t.count("WorldSize") and (t2.count("Game1.WorldSize") - t.count("Game1.WorldSize"))
        t = t2
        t, bc = re.subn(r'\bbase\.Draw\(gameTime\)', "_g.BaseDraw(gameTime)", t)
        open(p, "w", encoding="utf-8").write(t)
        print(f"A {fn}: {a} _g.calls, {wc} WorldSize, {bc} base.Draw")

    # B) promote render-method definitions to internal in render files
    for fn in RENDER_FILES:
        p = os.path.join(ROOT, fn)
        t = open(p, encoding="utf-8").read()
        promoted = 0
        for m in RENDER_METHODS:
            pat = re.compile(r'^(?P<i>[ \t]*)private(?P<rest>\s+[^;{}\n]*?\b' + re.escape(m) + r'\s*\()',
                             re.MULTILINE)
            t, c = pat.subn(lambda mo: mo.group('i') + 'internal' + mo.group('rest'), t)
            promoted += c
        open(p, "w", encoding="utf-8").write(t)
        if promoted:
            print(f"B {fn}: promoted {promoted} method defs to internal")

    # B) prefix external call sites with _gameRenderer.
    for fn in HOST_FILES:
        p = os.path.join(ROOT, fn)
        t = open(p, encoding="utf-8").read()
        t, n = prefix_calls(t, RENDER_METHODS, "_gameRenderer.")
        open(p, "w", encoding="utf-8").write(t)
        print(f"B {fn}: {n} call sites prefixed _gameRenderer.")

if __name__ == "__main__":
    main()
