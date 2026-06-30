#!/usr/bin/env python3
"""Pilot refactor: extract the 5 Game1.Render*.cs partial files into a single
top-level `GameRenderer` class, reached from Game1 via a `_g` back-reference.

Mechanical bulk only:
  - rename file  Game1.Render.X.cs -> GameRenderer.X.cs
  - change `public partial class Game1` -> `partial class GameRenderer`
  - the Draw override becomes a plain method (a thin forwarder stays on Game1)
  - prefix every borrowed `_field` token with `_g.` (prefix-all-minus-keep, so
    no borrowed field can be missed), plus the Game base props GraphicsDevice /
    IsActive.

Reads from a one-time pristine snapshot in the scratchpad so it is re-runnable.
Emits the exact set of prefixed `_field` tokens so a follow-up step can promote
those Game1 members to `internal`.
"""
import os, re, shutil, sys, json

ROOT = r"C:\Users\Johan\source\repos\Lucid\NecrokingMG\Necroking"
SNAP = r"C:\Users\Johan\AppData\Local\Temp\claude\C--Users-Johan-source-repos-Lucid-NecrokingMG\ce76c2cf-00f4-4527-bb5d-d7b545cf9f1e\scratchpad\orig_render"

# original -> new file
FILES = {
    "Game1.Render.cs":         "GameRenderer.Draw.cs",
    "Game1.Render.World.cs":   "GameRenderer.World.cs",
    "Game1.Render.HUD.cs":     "GameRenderer.Hud.cs",
    "Game1.Render.Units.cs":   "GameRenderer.Units.cs",
    "Game1.Render.Corpses.cs": "GameRenderer.Corpses.cs",
}

# `_field` tokens that must stay unprefixed: the back-ref itself, the two static
# arrays defined in the render files, and the three render-only statics that
# move into GameRenderer (defined in GameRenderer.cs core).
KEEP = {"_g", "_hoverShapeNames", "_hoverStyleNames",
        "_outlineDirs", "_ghostColor1", "_ghostColor2"}

FIELD_RE = re.compile(r"(?<![\w.])(_[A-Za-z]\w*)\b")
BASEPROP_RE = re.compile(r"(?<![\w.])(GraphicsDevice|IsActive)\b")

def snapshot():
    os.makedirs(SNAP, exist_ok=True)
    for orig in FILES:
        dst = os.path.join(SNAP, orig)
        if not os.path.exists(dst):
            shutil.copyfile(os.path.join(ROOT, orig), dst)
            print(f"snapshot {orig}")

def transform(orig, newname):
    with open(os.path.join(SNAP, orig), encoding="utf-8") as f:
        text = f.read()

    prefixed = {}
    def repl_field(m):
        name = m.group(1)
        if name in KEEP:
            return name
        prefixed[name] = prefixed.get(name, 0) + 1
        return "_g." + name
    text = FIELD_RE.sub(repl_field, text)
    text = BASEPROP_RE.sub(lambda m: "_g." + m.group(1), text)

    # class declaration
    text = text.replace("public partial class Game1", "partial class GameRenderer")
    # Draw override -> plain method (only in the orchestrator file)
    text = text.replace("protected override void Draw(GameTime gameTime)",
                        "public void Draw(GameTime gameTime)")

    with open(os.path.join(ROOT, newname), "w", encoding="utf-8") as f:
        f.write(text)
    return prefixed

def main():
    snapshot()
    allp = {}
    for orig, newname in FILES.items():
        p = transform(orig, newname)
        for k, v in p.items():
            allp[k] = allp.get(k, 0) + v
        print(f"{orig} -> {newname}: {len(p)} distinct fields prefixed, {sum(p.values())} sites")
    names = sorted(allp)
    print("\n== distinct prefixed _field tokens ({}): ==".format(len(names)))
    print("\n".join(names))
    with open(os.path.join(SNAP, "prefixed_fields.json"), "w") as f:
        json.dump(names, f, indent=2)

if __name__ == "__main__":
    main()
