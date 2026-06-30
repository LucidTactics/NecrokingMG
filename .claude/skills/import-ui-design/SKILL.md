---
name: import-ui-design
description: Reproduce an HTML/CSS/JSX UI mock (Claude Design or otherwise) inside MonoGame/SpriteBatch, knowing which CSS effects translate and which need a shader. Use when — "import this design", "turn this CSS/HTML/JSX mock into the game UI", "reproduce this UI design", "build this UI from a screenshot/mockup".
---

# Importing UI Designs from CSS / HTML / JSX mocks

When reproducing a design from Claude Design (or any HTML/CSS mock) inside MonoGame, expect a fundamental mismatch: the design relies on browser primitives SpriteBatch lacks — linear/radial gradients, box-shadow, drop-shadow, filter blur, repeating gradients, text-shadow stacks, SVG filters. Stacking SpriteBatch `Draw(pixel, ...)` calls to fake these produces banding, halos, and ghost-print artifacts. **Do not reproduce CSS effects with stacked primitives** — that path has been tried and the user rejected the result.

## What translates cleanly

- **Layout**: rectangles, columns, gutters, paddings, anchored positioning. Translate geometry directly.
- **State machinery**: hit-testing, tooltips, click/hover, modal lifecycle, scroll. Translate the JSX state directly.
- **Colors**: copy the palette verbatim. CSS variables → `Color` constants.
- **Typography**: font choice, alignment, spacing. Use available `SpriteFont`s; cast positions to `int` (see Text rounding below).
- **Borders**: solid 1-2px borders → `DrawBorder` / nested rectangles.
- **Flat fills**: solid color rectangles → `_batch.Draw(_pixel, rect, color)`.

## What does NOT translate (do not attempt without a shader)

- Linear / radial / 3-stop gradients at any non-trivial size (banding is visible)
- `box-shadow` outer glow (halos on small rects)
- `box-shadow inset` soft inner shadow (reads as a hard darker border)
- `filter: drop-shadow(blur)` / `filter: blur(...)`
- `text-shadow` with offsets > 1px (reads as ghost-printed glyphs)
- Repeating gradient textures (leather, paper grain — pixel-stippling reads as a dot grid)
- SVG `<filter>` effects (gaussian blur, feMerge, etc.)

## Existing failed attempt — do NOT reuse

`Necroking/Render/UIGfx.cs` contains CSS-style helpers (`FillVerticalGradient`, `FillRadialGradient`, `DrawInsetShadow`, `DrawCircleOuterGlow`, `DrawRectOuterGlow`, `DrawRepeatingDiagonal`, `DrawTextEmbossed`, `DrawTextShadow`, etc.) written for the SkillTreePanel design and judged not-good-enough by the user. Each ships a `[UNVERIFIED]` docstring explaining what's wrong. **Do not import these into new code without re-evaluating in isolation.** They are a record of "what was tried and why it isn't enough", not a standard utility.

`Necroking/Render/SkillTreePanel.cs` is similarly flagged — its layout, K-toggle, scenario hooks, and prerequisite logic are good and worth copying; its visual effects are known imperfect placeholders.

## Recommended workflow

1. **Read the design source thoroughly first** — fetch the HTML/CSS/JSX, enumerate every effect lacking a SpriteBatch equivalent. Don't assume, list them.
2. **Build layout and state machinery first** with flat fills and 1px borders. Get positioning, hit-testing, modal flow, and clicks working. Verify with a scenario.
3. **Show the user the flat-fill version** and ask whether (a) it's good enough, (b) you should attempt approximations and accept artifacts, or (c) the missing effects need a real shader (.fx) — a separate, larger task.
4. **Never quietly add a new effect helper** to ape CSS. If the user wants the effect, the answer is a shader, not stacked primitives.
5. **Always verify with a scenario screenshot** before reporting done. UI rendering bugs are silent in build/test output; they only show up visually.

## When the user asks for "the same effect"

Treat "do this CSS effect in MonoGame" as a clarifying question, not a directive to write more `UIGfx`-style helpers:

- **Gradient on a small element** (button, label plate ≤ 40px): banding may be acceptable; ask first.
- **Gradient on a large element** (panel background): needs a shader.
- **Outer glow / drop shadow**: needs a shader. Don't fake with stacked outlines.
- **Blur**: needs a shader (`Necroking/Render/Bloom*` is the closest reference for real blur).
- **Text emboss / glow**: not solvable with SpriteFont alone. Either accept flat text with a 1px shadow, or look at SDF font rendering as a separate project.

## If a technique requires a shader, write the shader

When the right answer is "this needs a shader (.fx)", the user expects you to *write* it, not say "this would need a shader" and stop. Stacked SpriteBatch fakes are off the table.

- New shaders go in `Necroking/assets/shaders/` (HLSL/GLSL via `.fx`).
- `Necroking/Render/Bloom*` and its `.fx` files are the canonical reference for how an effect shader is loaded, how parameters are pushed, and how a SpriteBatch pass uses a custom Effect.
- Read `memory/mgfx_shader_gotchas.md` first (default uniforms are 0; SpriteBatch needs pixel-shader-only effects).
- Verify the shader visually with a scenario screenshot.

If the shader is much larger scope than the rest of the task, flag that before committing — but the default expectation is that you do the work, not punt.

## Text rendering gotcha (integer pixels)

SpriteBatch uses `SamplerState.PointClamp`; text drawn at sub-pixel positions aliases. **Always round text positions to integer pixels:** `new Vector2((int)x, (int)y)`. `EditorBase.DrawText` rounds internally, but direct `DrawString` calls (e.g. in `Game1.cs`) must round manually. When centering with `MeasureString`, the division yields floats — cast to `int` before drawing.
