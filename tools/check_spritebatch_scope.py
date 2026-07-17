#!/usr/bin/env python3
"""SpriteScope lockdown regression checker.

Outside sanctioned render plumbing, no C# file may even NAME the raw
`SpriteBatch` type: draw code goes through `SpriteScope` (Game1.Scope /
EditorBase.Scope), and the raw batch is only reachable via the explicit
`Scope.Batch` hatch (whose call sites use `var`, so they never spell the type).

Any `SpriteBatch` token outside the allowlist therefore means the old
"store/receive a raw batch" template is creeping back in — the template that
re-derived the premultiplied-color bug in MinimapHUD (2026-07-16). See
docs/locate-behavior/render.md ("Premultiplied alpha") and ui.md.

Usage:  python tools/check_spritebatch_scope.py
Exit 0 = clean; exit 1 = violations listed on stdout.
"""

import re
import sys
from pathlib import Path

REPO = Path(__file__).resolve().parent.parent
SRC = REPO / "Necroking"

# Files/dirs where raw SpriteBatch is sanctioned (render plumbing + scenarios).
ALLOW = [
    "Render/",           # the render layer itself (materials, passes, renderers)
    "Scenario/",         # test scenarios drive their own batches deliberately
    "Game1.cs",          # owns the one shared batch (private field + Scope)
    "GameRenderer.",     # pass orchestration (GameRenderer.*.cs partials)
    "Editor/BuffPreview.cs",   # own private batch for RT preview passes
    "Editor/SpellPreview.cs",  # own private batch for RT preview passes (+ bloom)
]

TOKEN = re.compile(r"\bSpriteBatch\b")
LINE_COMMENT = re.compile(r"//.*")
BLOCK_COMMENT = re.compile(r"/\*.*?\*/", re.DOTALL)
STRING_LIT = re.compile(r'"(?:[^"\\]|\\.)*"')


def strip_noise(source: str) -> str:
    """Remove comments and string literals, preserving line structure so
    reported line numbers stay accurate."""
    def blank_keep_newlines(m):
        return re.sub(r"[^\n]", " ", m.group(0))
    source = BLOCK_COMMENT.sub(blank_keep_newlines, source)
    source = STRING_LIT.sub(" ", source)
    source = LINE_COMMENT.sub(" ", source)
    return source


def main() -> int:
    violations = []
    for path in sorted(SRC.rglob("*.cs")):
        rel = path.relative_to(SRC).as_posix()
        if any(rel.startswith(a) or rel == a for a in ALLOW):
            continue
        text = strip_noise(path.read_text(encoding="utf-8", errors="replace"))
        for i, line in enumerate(text.splitlines(), 1):
            if TOKEN.search(line):
                violations.append((rel, i, line.strip()))

    if violations:
        print(f"SpriteScope lockdown: {len(violations)} violation(s) — raw "
              f"SpriteBatch named outside sanctioned render plumbing:\n")
        for rel, line_no, snippet in violations:
            print(f"  Necroking/{rel}:{line_no}: {snippet}")
        print("\nFix: draw through SpriteScope (Game1.Instance.Scope / _g.Scope "
              "property — never a cached field); use Scope.Batch (via var) only "
              "for sanctioned Begin/End or FontStash hatch sites. If a file is "
              "genuinely new render plumbing, extend ALLOW with a comment.")
        return 1
    print("SpriteScope lockdown: clean (no raw SpriteBatch outside sanctioned files).")
    return 0


if __name__ == "__main__":
    sys.exit(main())
