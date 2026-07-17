# SpriteScope lockdown — make raw SpriteBatch hard to reach (one big run)

**Goal:** outside `Necroking/Render/`, no class stores, receives, or exposes a raw
`SpriteBatch`. All draw code authors STRAIGHT-alpha colors through `SpriteScope`;
the raw premult-native API is reachable only via the explicit `Scope.Batch` hatch at
documented sanctioned sites. Plus: fix the doc-routing gap that let this regress, and
add a checker so it can't regress silently again.

## Context (diagnosed 2026-07-17, session research — trust these findings, don't re-derive)

- The straight-alpha migration landed **2026-07-04 (`09df1a6`)**: `SpriteScope`
  (`Necroking/Render/SpriteQueue.cs:63`) is THE draw surface; `Material.Tint` /
  `ColorUtils.Premultiply` (`Necroking/Core/ColorUtils.cs:55`) convert straight→premult
  in one place. All blend states stayed premultiplied (`BlendState.AlphaBlend`);
  textures are premultiplied at load.
- **MinimapHUD.cs was created 12 days LATER (`8f2f56e`, 2026-07-16) already wrong**: it
  cloned the pre-migration `Init(GraphicsDevice, SpriteBatch, Texture2D pixel)` template
  and draws everything on the raw batch (`FillRect => _batch.Draw(_pixel, r, c)`,
  `MinimapHUD.cs:446`). The mistake is **silent while colors are opaque** (premultiply is
  identity at A=255); it surfaced at the first translucent color and that session adapted
  (`ViewportColor = Color.White * 0.5f; // premultiplied 50% white`, :65) instead of
  switching surface. Johan then hit it tuning the border (`// This is premultiplied for
  some reason!!`, :138).
- **Root causes:** (1) the raw batch circulates freely — `Game1._spriteBatch` is
  `internal` (`Game1.cs:52`), UI Init signatures pass it, `EditorBase` exposes
  `public SpriteBatch SpriteBatch => _sb` (`EditorBase.cs:73`); once a class holds the
  object, C# can't hide the premult-native `Draw`. (2) The convention is documented only
  in `docs/locate-behavior/render.md` ("Premultiplied alpha", ~line 137) — a UI/HUD task
  routes to `ui.md`, which never states the rule.
- **Audit result (full sweep of 112 draw calls, 25 files):** MinimapHUD is the **sole**
  production offender (class d: raw + premult-authored). No latent color bugs elsewhere
  (class c: none). Everything else is scope-typed (params *named* `batch` but *typed*
  `SpriteScope`) or on the sanctioned native-encoding island list. Opaque-only raw draws
  to convert while at it: `Game/ForagableSystem.cs:204`, `Editor/WadingEditorPopup.cs:424`.

## Target end state

1. **No stored raw batches outside `Render/`.** The pattern to eliminate
   (currently in ~20 classes) and its replacement:

   ```csharp
   // BEFORE — the template that keeps re-deriving the bug
   private SpriteBatch _batch = null!;
   private Render.SpriteScope Scope => _batch;  // implicit conversion
   public void Init(GraphicsDevice device, SpriteBatch batch, Texture2D pixel) { _batch = batch; ... }

   // AFTER — direct-over-inject (CLAUDE.md policy); no batch field, no batch param
   private SpriteScope Scope => Game1.Instance.Scope;   // or _g.Scope where a Game1 ref exists
   ```

   `Game1.Scope` (`Game1.cs:63`) computes the resume material from `Materials.Open` at
   access time, so a property is always correct. **Never cache a `SpriteScope` in a
   field** — that would freeze the resume material at Init time.

2. **`Game1._spriteBatch` becomes `private`.** Render plumbing keeps its own references
   (`RenderPipeline.Batch`, `ctx.Batch`, `EffectBatch` — all sanctioned). Everything else
   goes through `Game1.Scope`. Known external toucher to fix: `Editor/MapEditorWindow.cs:92`
   (`private SpriteBatch _spriteBatch => _game._spriteBatch;`). Grep `_spriteBatch\b`
   for the rest during the run.

3. **`EditorBase` stops leaking.** `_sb` (`EditorBase.cs:72`) goes private; delete
   `public SpriteBatch SpriteBatch => _sb;` (:73). Find its consumers and route them via
   `Scope` / `Scope.Batch`. EditorBase runs its own Begin/End cycles — examine those
   sites individually; `SpriteScope.Begin/End/PushMaterial/Suspend/Resume`
   (`SpriteQueue.cs:104-139`) cover the legitimate needs.

4. **Third-party extension methods** (FontStashSharp `DrawString` on SpriteBatch) use the
   established hatch: `Scope.Batch` + `Scope.EncodeTint(color)` — precedent at
   `UI/RuntimeWidgetRenderer.cs:491,719`, `UI/WidgetLayoutUtils.cs:94-102`,
   `Editor/UIEditorWindow.cs:1533`.

5. **Naming:** params/locals typed `SpriteScope` but named `batch`/`sb` get renamed to
   `scope` (e.g. `Render/DrawUtils.cs`, `Render/DebugDraw.cs`, `UI/NineSlice.cs:54`,
   `Game/DeathFogSystem.cs:422`) so the name stops suggesting the raw API.

## Work list

### A. MinimapHUD conversion (`Necroking/UI/MinimapHUD.cs`)
- `Init(GraphicsDevice, SpriteBatch, Texture2D)` → `Init(GraphicsDevice, Texture2D)`;
  drop `_batch`; add `private SpriteScope Scope => Game1.Instance.Scope;`. Call site:
  `Game1.cs:2546`.
- `FillRect` (:446) and the terrain/fog texture draws (:141, :152) → `Scope.Draw`.
- Re-author translucent TINT constants premult→straight. `ColorUtils.Premultiply`
  truncates (`(byte)(R*A/255)`), so use these exact round-tripping values:
  | Site | Today (premult) | New (straight) |
  |---|---|---|
  | border glow :139 | `new(80, 80, 70, 80)` | `new(255, 255, 224, 80)` |
  | `BorderColor` :63 | `new(20, 20, 30, 220)` | `new(24, 24, 35, 220)` |
  | `ViewportColor` :65 | `Color.White * 0.5f` | `ColorUtils.Fade(Color.White, 0.5f)` |
  General recipe: straight = smallest s with `trunc(s*A/255) == premultValue`.
- **Leave texel constants alone**: `FogUnexplored`/`FogExplored` (:68-69) are `SetData`
  texture data, which is premult-encoded by convention (same as loaded PNGs) — clarify
  the comment, don't convert. Opaque constants (markers, terrain colors) unchanged.
- Delete the `// This is premultiplied for some reason!!` comment.

### B. De-batch the UI/Editor/Game classes (mechanical, per-file)
Remove batch field + Init param, add/replace `Scope => Game1.Instance.Scope`, route any
direct `_batch.` use through `Scope` (or `Scope.Batch` for FontStash/Begin-End sites).
Fields found (also catch nullable `SpriteBatch?` variants the grep missed — e.g.
`UI/InventoryUI.cs`):
- UI: `HUDRenderer.cs:130`, `LogPanel.cs:20`, `GraveRosterUI.cs:16`,
  `CharacterStatsUI.cs:118`, `JobBoardUI.cs:23`, `SkillBookOverlay.cs:40`,
  `TableCraftMenuUI.cs:77`, `SideListMenu.cs:30` (protected — check subclasses),
  `RuntimeWidgetRenderer.cs:23`, `InventoryUI.cs`, `MinimapHUD.cs:76` (part A)
- Editor: `EditorBase.cs:72-73` (part 3 above), `BuffPreview.cs:23`,
  `ColorPickerPopup.cs:17`, `SpellPreview.cs:37`, `EnvObjectEditorWindow.cs:29`,
  `MapEditorWindow.cs:92`, `WadingEditorPopup.cs` (:424 raw draw)
- Game: `ForagableSystem.cs:59` (:204 raw draw)
- Update every `.Init(...)` call site (cluster around `Game1.cs:2543-2547` and editor setup).

### C. Sanctioned islands — allowlist, do NOT convert their colors
Everything in `Render/` stays as-is (native encodings are by design): `BuffVisualSystem`
(`EncodeColor` :667), `LightningRenderer`, `PoisonCloudRenderer`, `DeathFogRenderer`,
`ReanimEffectSystem`, `GroundFogSystem`, `MagicGlyphRenderer` (Immediate per-draw
uniforms), `Bloom`, `UIShaders`, `FogOfWarSystem`, `ShadowRenderer`, `GodRayRenderer`,
`GrassTuftRenderer`, `ScatterGlowSystem`, `HdrStripBatch`, `WeatherRenderer`,
`EffectBatch`, `Material`, `SpriteQueue`, `RenderPipeline`, plus `Game1` itself.
`Scenario/` files (`ScenarioBase`, `ScenarioScreenshot`, `BlendTestScenario` — GPU truth
test, `StrideDebugScenario`, UI test scenarios) keep raw batches; allowlist them.

### D. Regression guard
New `tools/check_spritebatch_scope.py`: flags any `SpriteBatch` (incl. `SpriteBatch?`)
field/param/property/local declaration outside `Necroking/Render/`, `Necroking/Scenario/`,
`Game1.cs`, plus an inline allowlist for the `.Batch`-hatch call sites. Zero-violation
gate at the end of the run; document the invocation in `docs/locate-behavior/render.md`
so future audits/refactors run it.

### E. Docs (the process-gap fix)
- `docs/locate-behavior/ui.md` — top-of-file conventions + "The minimap" section: *"All
  UI draw code goes through a `SpriteScope` (`Game1.Scope` / `EditorBase.Scope`) and
  authors STRAIGHT-alpha colors — never a raw `SpriteBatch.Draw` (colors would hit the
  premult AlphaBlend state unconverted). Raw batch only via `Scope.Batch` +
  `Scope.EncodeTint` for FontStash extensions."*
- `docs/locate-behavior/overview.md` — symptom-routing entry: *"translucent color renders
  too bright / 'premultiplied for some reason' / raw SpriteBatch vs Scope"* →
  render.md "Premultiplied alpha" + the ui.md convention line.
- `docs/locate-behavior/render.md` — refresh the island list (MinimapHUD fixed, not an
  island) + mention the checker script.
- `memory/standard_patterns.md` — add the `Scope => Game1.Instance.Scope` pattern as the
  canonical draw-surface access for non-Render classes.

## Execution shape (one big run)

**Read `docs/big-refactors.md` first and follow it** — no git ops inside subagents,
verify on disk, build gates between phases.

1. **Pre-flight:** clean tree, baseline `dotnet build Necroking/Necroking.csproj`,
   capture baseline screenshots via `/drive-game` (minimap in play mode with fog on +
   camera viewport rect visible, minimap in map editor, character stats, job board, log
   panel, skill book, crafting menus, spell/env editors open, weather active).
2. **Sweep:** parts A + B (+ C allowlist untouched). Parallelize per file; every touched
   file compiles against the same new pattern, so run a build gate after.
3. **Lock:** `Game1._spriteBatch` → private, `EditorBase` accessor deleted, param renames,
   checker script added and passing.
4. **Verify:** build; re-screenshot every state from pre-flight and compare — everything
   must be pixel-identical (the MinimapHUD constants above round-trip exactly);
   `--scenario` regression suite incl. the blend/GPU-truth scenario and
   `map_reload_consistency`; checker at zero violations.
5. **Docs + commit:** part E; stage everything together (new files included) per
   git-discipline; build before offering to push.

## Risks / gotchas

- **Scope must be a property, never a cached field** (resume-material capture — see
  Target end state 1).
- **Begin/End owners** (EditorBase, possibly RuntimeWidgetRenderer, scenario harnesses):
  don't blind-sed `_batch.` → `Scope.` there; map each Begin/End to
  `SpriteScope.Begin/End/Push/Pop/Suspend/Resume` or leave via `Scope.Batch` deliberately.
- **FontStash extension methods** won't compile against `SpriteScope` — they need
  `Scope.Batch` + `EncodeTint` (established pattern, see Target end state 4).
- **Don't touch translucent colors in Render/ islands** — premult/HDR/A=0 encodings there
  are intentional.
- **Texel buffers (`SetData`) stay premult** — only TINT constants convert.
- **`Necroking/Net/` is out of scope** (no drawing there; don't wander in).
