# Importing UI Designs from CSS / HTML / JSX mocks

When asked to reproduce a design from Claude Design (or any HTML/CSS mock) inside MonoGame, expect a fundamental mismatch: the design relies on browser primitives that SpriteBatch does not have — linear/radial gradients, box-shadow, drop-shadow, filter blur, repeating gradients, text-shadow stacks, SVG filters. Stacking SpriteBatch `Draw(pixel, ...)` calls to fake these effects produces banding, halos, and ghost-print artifacts. **Do not try to reproduce CSS effects with stacked primitives.** That path has been tried and the user has rejected the result.

## What translates cleanly

- **Layout**: rectangles, columns, gutters, paddings, anchored positioning. Translate the design's geometry directly.
- **State machinery**: hit-testing, tooltips, click/hover, modal lifecycle, scroll. Translate the JSX state directly.
- **Colors**: copy the palette verbatim. CSS variables → `Color` constants.
- **Typography**: font choice, text alignment, spacing. Use the available `SpriteFont`s; cast positions to `int`.
- **Borders**: solid 1-2px borders. `DrawBorder` / nested rectangles.
- **Flat fills**: solid color rectangles. `_batch.Draw(_pixel, rect, color)`.

## What does NOT translate cleanly (do not attempt without a shader)

- Linear / radial / 3-stop gradients (any non-trivial size — banding is visible)
- `box-shadow` outer glow (halos on small rects)
- `box-shadow inset` soft inner shadow (reads as a hard darker border)
- `filter: drop-shadow(blur)` or `filter: blur(...)`
- `text-shadow` with offsets > 1px (reads as ghost-printed glyphs)
- Repeating gradient textures (leather, paper grain — pixel-stippling reads as a dot grid)
- SVG `<filter>` effects (gaussian blur, feMerge, etc.)

## Existing failed attempt — do NOT reuse

`Necroking/Render/UIGfx.cs` contains a set of CSS-style helpers (`FillVerticalGradient`, `FillRadialGradient`, `DrawInsetShadow`, `DrawCircleOuterGlow`, `DrawRectOuterGlow`, `DrawRepeatingDiagonal`, `DrawTextEmbossed`, `DrawTextShadow`, etc.) that were written for the SkillTreePanel design and judged not-good-enough by the user. Every function ships with a `[UNVERIFIED]` docstring explaining what's wrong with it. **Do not import those helpers into new code without re-evaluating in isolation.** They are kept as a reference of "what was tried and why it isn't enough", not as a standard utility.

`Necroking/Render/SkillTreePanel.cs` is similarly flagged — its layout, K-toggle, scenario hooks, and prerequisite logic are good and worth copying; its visual effects are known imperfect placeholders.

## Recommended workflow when importing a design

1. **Read the design source thoroughly first** — fetch the HTML/CSS/JSX, list every effect that doesn't have a SpriteBatch equivalent. Don't assume, enumerate.
2. **Build the layout and state machinery first** with flat fills and 1px borders. Get the panel positioning, hit-testing, modal flow, and clicks working. Verify with a scenario.
3. **Show the user the flat-fill version** and ask whether (a) this is good enough, (b) you should attempt approximations and accept the artifacts, or (c) the missing effects need a real shader (.fx) — which is a separate task with a much bigger scope.
4. **Never quietly add a new effect helper** to ape CSS behavior. If the user wants the effect, the answer is a custom shader, not stacked SpriteBatch primitives.
5. **Always verify with a scenario screenshot** before reporting the work as done. UI rendering bugs are silent in build/test output; they only show up visually.

## When the user asks for "the same effect"

If the user points at a CSS effect and says "do this in MonoGame", treat it as a clarifying question, not a directive to write more `UIGfx`-style helpers. Possible answers depending on the effect:

- **Gradient on a small element** (button, label plate ≤ 40px): banding may be acceptable; ask first.
- **Gradient on a large element** (panel background): needs a shader.
- **Outer glow / drop shadow**: needs a shader. Don't fake it with stacked outlines.
- **Blur**: needs a shader (the bloom pipeline at `Necroking/Render/Bloom*` is the closest existing reference for what real blur looks like).
- **Text emboss / glow**: not solvable with SpriteFont alone. Either accept flat text with a 1px shadow, or look at SDF font rendering as a separate project.

## If a technique requires a shader, write the shader

When the right answer is "this needs a shader (.fx)", the user expects you to *write* the shader, not just say "this would need a shader" and stop. Stacked SpriteBatch fakes are explicitly off the table — that's the whole point of this document. So if you've concluded a real shader is required, that's the work. Plan and implement it.

- New shaders go in `Necroking/assets/shaders/` (HLSL/GLSL via `.fx`); see existing entries like the bloom pipeline shaders for the load/build pattern.
- Look at `Necroking/Render/Bloom*` and the .fx files it loads as the canonical reference for: how an effect shader is loaded, how parameters are pushed, how a SpriteBatch pass uses a custom Effect.
- `memory/mgfx_shader_gotchas.md` has known MonoGame/MGFX pitfalls (default uniforms are 0, SpriteBatch needs pixel-shader-only effects). Read it before writing the shader.
- Verify the shader visually with a scenario screenshot, just like any other UI work.

If the shader is a much larger scope than the rest of the task (e.g. user asked for a small UI tweak and the only real solution is a full new effect pipeline), flag that to the user *before* committing to it — but the default expectation is that you do the work, not punt on it.

## Status

This is a permanent reference, not a task to complete. Don't delete it. If the codebase ever gets a proper shader-based UI effect path, update this file to point at it (and revisit whether `UIGfx.cs` should still ship at all).
