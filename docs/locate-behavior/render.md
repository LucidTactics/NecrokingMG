# Render/ — rendering subsystems

> **Coverage: PARTIAL.** Documented: the **draw-dispatch pipeline** (top-level `Draw`,
> batch/shader/state model — see next section), the **rendering feature inventory**
> (every technique tried, with status), the **target feature set** (what we want the
> renderer to do), and the **visual-effect / flipbook** systems. Still undocumented:
> sprite atlases & frames internals, `FontManager`, `HUDRenderer` chrome, the spellbar
> widget. **HUD/UI icons + unit portraits now documented** (path→Texture2D via
> `RuntimeWidgetRenderer.DrawIcon`/`TextureCache`, and `DrawUnitIdleSprite`) — see the
> "HUD/UI icons & unit portraits" section. Extend this file when you touch the rest.

## The render pipeline (REDESIGNED 2026-07-03 — submit → sort → batch)

**The frame is data.** Rendering is a `RenderPipeline` — an ordered `List<RenderPhase>`
of named `RenderPass`es built once in `GameRenderer.Pipeline.cs::BuildPipeline()` and
executed by `GameRenderer.Draw.cs::Draw()`. Full design + rationale:
`todos/render-pipeline-design.md`. Core types (all in `Necroking/Render/`):

- **`RenderPipeline.cs`** — `RenderContext` (per-frame device/batch/screen/GameTime),
  `RenderPass` (name, `Enabled`, `LastMs`), `CustomPass` (imperative escape hatch),
  `RenderPhase` (RT scope: `OnBegin`/`OnEnd` own target binds), `RenderPipeline`
  (execute + `FindPass`). Dev command **`pass list|on <name>|off <name>`** toggles passes live.
- **`Material.cs`** — `Material` = immutable effect+blend+sampler+depthstencil+rasterizer+
  sortmode bundle, compared **by reference**; `ExtraSamplerSlots` declaratively fixes the
  s1/s2 stale-sampler bug class. `Materials` is the canonical registry: `Scene` (AlphaBlend/
  LinearClamp), `Hud` (PointClamp), `AdditiveShapes`, `FogWisp` (DepthRead), and effect-backed
  `Wading`, `DissolveTree`, `HdrAlpha`/`HdrAdditive` (HdrSprite.fx **clones** with AlphaMode
  baked at load — the per-frame flip is gone), `DepthStamp`, `MorphSdf`, `OutlineAlpha`/
  `OutlineAdditive`; `MagicGlyph` (Immediate sort mode — per-glyph uniforms in one batch) and
  `WeatherFog` register themselves in their renderers.
- **`SpriteQueue.cs`** — `WorldLayer` enum (the layer bands: Roads…Corpses, FogBack, YSort,
  Projectiles, Rain, FogWisps, EffectsHdrAlpha/Additive, AdditiveShapes…), packed ulong
  `SortKey` (`[layer 8|depth24|materialId 16|seq 16]`, camera-relative quantized worldY),
  `RenderItem` (sprite args or cached-delegate callback with int payloads + optional
  `SetParams` + `LayerDepth`), `SpriteScope` (**`PushMaterial`/`PopMaterial`/`Suspend`/
  `Resume`** — the ONLY sanctioned way to deviate from the pass material; restore state is
  computed, never guessed — this replaced `EffectBatch.BeginEffect`, now deleted), and
  `SpriteQueuePass` (Collect → sort → walk, `End/Begin` only on material change;
  `LastItemCount`/`LastBatchCount` feed the perf readout).

### The frame (phases/passes, in `GameRenderer.Pipeline.cs`)
`Prep` (weather context, fog-of-war RT update) → `Scene` (OnBegin: bloom BeginScene/clear +
ambient; passes: **Ground** CustomPass (self-contained batch, ground shader) →
WadingSinkOffsets → **World** SpriteQueuePass (roads→traps→glyphs→walls→shadows→hover→
corpses→**YSort**→projectiles/rope→rain as layer bands; collected in
`GameRenderer.Units.cs::CollectWorldItems`) → FogDepthOccluders (unit silhouettes →
depth buffer via `Materials.DepthStamp`; runs for DepthSortedFog OR active ground fog) →
SpawnImpactEffects → **HdrEffects** SpriteQueuePass (fog wisps, HDR alpha/additive,
additive shapes; `CollectFxItems`) → GodRays → ForagablesDamageNumbers; OnEnd: bloom
EndScene composite) → `Post` (fog-of-war overlay, debug overlays) → `Hud` (camera
restore, weather fog, HUD/editors).

### Submission — the Y-sort queue
Units/env objects submit as **cached-delegate callback items** (`_cbUnit` etc. — a unit is
~5–30 ordered sub-draws sharing one depth slot); puffs/tufts/dust renderers still fill the
legacy `_depthItems` list which transfers into the queue (their internals untouched).
Equal-depth determinism = the key's sequence bits (submission order). Composite draws that
need another shader use `scope.PushMaterial(Materials.X)` / `PopMaterial()` (wading,
dissolve, morph, outlines, glyphs) or `scope.Suspend()/Resume()` for raw-device work
(shadow quads, reanim sorted particles). Sprite blits still go through
`Renderer.DrawSprite`; `Renderer` never manages batch state.

### Ground fog & occlusion fade (new capabilities, built on the pipeline)
- **`Render/GroundFogSystem.cs`** — fog banks spawn ground-hugging wisps drawn through
  `Materials.FogWisp` (AlphaBlend + **DepthRead**): per-pixel depth test against the unit
  occluder stamps → wisps swallow legs, torsos occlude wisps behind. Back blanket at
  `WorldLayer.FogBack`. Spawn via `groundfog add/at_camera/clear` dev command or
  `GroundFogSystem.SpawnBank` (weather/map hookups TBD). Look knobs = constants at top of
  the file. Scenario: `ground_fog`.
- **Occlusion fade** — `GameRenderer.Units.cs::UpdateOcclusionFade` (submit-time): tall env
  objects in front of the necromancer whose screen box overlaps his fade to 40% alpha
  (exp lerp, per-object state in `_occlusionFade`). Scenario: `occlusion_fade`. Details:
  the **single** player box is precomputed once in `CollectWorldItems` (~lines 188-207)
  from `_g._sim.NecromancerIndex` only — `plLeft/Right/Top/Bottom/plY` (screen box from
  the necromancer's `WorldToScreen` + `SpriteWorldHeight*Zoom`). `UpdateOcclusionFade`
  gates on `worldH >= OcclusionMinWorldH (2.5)` and `obj.Y > plY` (object in front), tests
  AABB overlap, sets `target = OccludedAlpha (0.40)`. Constants `OccludedAlpha`,
  `OcclusionMinWorldH`, `OcclusionFadeRate` sit just above the method. The fade is
  **applied** in `DrawSingleEnvObject` (~line 761) via `ColorUtils.Fade(tint, occFade)`.
  To fade against ALL player units, replace the single-box precompute with a list of
  boxes for every alive `Faction.Undead` unit (player faction = `Faction.Undead`, see
  `Data/Enums.cs`), view-cull it, and make `UpdateOcclusionFade` early-out on first
  overlapping box — mind the O(objects×hordeUnits) cost (see Pitfalls in answer).

### Where a new full-res DepthRead additive pass goes (post-occluder, pre-bloom)
The seam for any effect that must depth-test against the unit silhouette stamps AND land
in the HDR scene RT before bloom extract: **inside the Scene phase, after the
`FogDepthOccluders` CustomPass, before `scene.OnEnd` (bloom `EndScene`)**. Two mechanisms,
both live in `GameRenderer.Pipeline.cs`:
1. **Submit into the existing `HdrEffects` SpriteQueuePass** (`CollectFxItems`) — the
   preferred route. Precedents: `GroundFogSystem.CollectWisps` submits per-wisp
   `SubmitSprite(WorldLayer.FogWisps, …, Materials.FogWisp, layerDepth:
   GameRenderer.FogDepthForY(sortY, camY))`; reanim's depth-tested particles ride the
   `_cbFxReanim` callback (`WorldLayer.EffectsHdrAdditive`) which `s.Suspend()`s the batch
   and calls `ReanimEffectSystem.DrawSortedParticles` (own batches). A DepthRead material
   MUST set `layerDepth` via `FogDepthForY(worldY, cameraY)` or the depth test against the
   stamps is garbage (stamps map larger Y → smaller depth).
2. **A new `scene.Add(new CustomPass(...))` between `FogDepthOccluders` and `_fxPass`**
   (or after it, before `LightningTris`) — for imperative work that owns its device state.
Materials: `Materials.FogWisp` = AlphaBlend + **DepthStencilState.DepthRead**;
`Materials.HdrAdditive` = Additive but NO DepthRead — an additive+DepthRead effect needs a
new `Materials.Register(name, effect, BlendState.Additive, LinearClamp,
DepthStencilState.DepthRead, RasterizerState.CullNone)` entry in `Render/Material.cs`.
Gotcha: the occluder stamps only exist when `Performance.DepthSortedFog` OR
`_groundFog.HasActiveBanks` — a new depth-testing consumer must OR its own "is active"
flag into the `FogDepthOccluders` gate.

### Adding a new .fx shader (the load chain)
1. Write `resources/YourShader.fx` (pixel-shader-only — SpriteBatch's built-in VS;
   MGFX-on-GL defaults every uniform to 0, set them all each draw —
   `memory/mgfx_shader_gotchas.md`).
2. Register it in `resources/Content.mgcb` (`#begin YourShader.fx` … `/build:YourShader.fx`
   block, copy an existing one). `dotnet build` compiles it — the csproj has
   `<MonoGameContentReference Include="..\resources\Content.mgcb" />`.
3. Load in `Game1.LoadContent` (`Game1.cs` ~ln 2590-2640):
   `Content.Load<Effect>("YourShader")` in a try/catch that null-fallbacks + logs to
   `DebugLog.Log("startup", …)` — every shader consumer must survive a null effect.
4. Bind: either `Materials.Register(...)` (batch material) or pass the Effect into a
   renderer's `LoadEffect(...)` (WeatherRenderer/MagicGlyphRenderer pattern).
Shared textures: `TextureUtil.GetRadialGlow(device, size=64)` = THE cached soft radial
glow (`Game1._glowTex`, shared by weather/lightning/poison/glyph renderers — never dispose
it). There is **no noise texture** — fog noise is procedural in-shader simplex
(`WeatherFog.fx` `snoise`, world-anchored via `worldPos * FogUvScale + scroll`); copy that
block for any world-anchored scrolling noise.

### Batch-state rules (post-redesign)
- `EffectBatch` is now a thin shim (`BeginScenePass`/`BeginHudPass` delegate to
  `Materials.Scene/Hud`); its suspend/resume helpers are **deleted**. Never hand-roll
  `End/Begin` restores — take a `SpriteScope`.
- `UIShaders` keeps its own injected-restore mechanism (deliberately separate, HUD-side).
- `BloomRenderer` internals (mip chain, `Immediate` fullscreen blits) stay a black box
  bracketed by the Scene phase; fog-of-war RT work runs in the Prep phase.
- Per-draw shader params, cheapest first: derive in shader from position+uniforms →
  vertex channels (HdrColor) → material variant (Effect.Clone) → item `SetParams`/
  `PushMaterial` (batch break) → Immediate-sort material for runs (glyphs).

## Premultiplied alpha — where it's handled (the full map)

**2026-07-04 — colors are now STRAIGHT ALPHA at every call site.** The draw layer
converts (once) per material:

- `Material.PremultiplyTint` (`Render/Material.cs`) says whether a material's batch
  expects premultiplied vertex tints (auto-derived: `blend == BlendState.AlphaBlend`;
  explicit `premultiplyTint: false` for HdrAlpha whose vertex color is an HDR pack).
  `Material.Tint(straight)` is THE conversion. `Material.Begin` stamps
  `Materials.Open` — the single globally-tracked "which material's batch is open"
  fact (single-threaded rendering). Raw special-state batches in swept files call
  `Materials.NoteAdHocBatch()` so conversion turns off inside them.
- `SpriteScope` (`Render/SpriteQueue.cs`) is THE draw surface everywhere (queue
  callbacks, HUD, editors, UI classes): it mirrors `SpriteBatch.Draw/DrawString` but
  encodes colors via `Materials.Open`. An **implicit conversion
  `SpriteBatch → SpriteScope`** exists (always color-correct because encoding follows
  the tracker); the reverse stays explicit via `scope.Batch` — the escape hatch for
  colors already in native encoding (HDR vertex pack, additive-via-A=0 trick) and
  for third-party extension draws (FontStashSharp text → `scope.Batch.DrawString`
  with `scope.EncodeTint(color)`). Immediate-mode surfaces: `Game1.Scope`,
  `EditorBase.Scope`, per-class `Scope => _batch;` accessors.
- The queue flush (`SpriteQueuePass.Execute`) applies `mat.Tint(item.Color)` to
  submitted sprites — `SubmitSprite` colors are straight alpha too.
- Fades: `ColorUtils.Fade(c, t)` scales A only (straight-alpha replacement for the
  premult-era `color * t`, which now double-dims if fed to a converting draw).
  `ColorUtils.ByteColor` returns straight alpha. **Never call
  `Color.FromNonPremultiplied` / hand-premultiply for a draw tint** — that's the
  old convention and now double-converts (dimmer, not washed out).
- Native-encoding islands (draw via raw batch, keep hand-encoded colors):
  `BuffVisualSystem` (`EncodeColor` — per-instance blendMode incl. the A=0 additive
  trick), `LightningRenderer`, `PoisonCloudRenderer`, `DeathFogRenderer`,
  `ReanimEffectSystem`, `Bloom`/`UIShaders`/`FogOfWarSystem` internals,
  `BlendTestScenario` (GPU truth test).

Underneath, the base convention (established by commit `d626422`) is unchanged:
**textures are premultiplied at load, batches blend with `BlendState.AlphaBlend`
(= One/InvSrcAlpha, the premult pair), and every scene shader outputs premultiplied
color.** The pieces:

- **Load-time premultiply (the ONLY texture entry points)** — `Render/TextureUtil.cs`:
  `LoadPremultiplied(device, path|stream)` (= `Texture2D.FromStream` +
  `PremultiplyAlpha(tex)` in-place RGB*=A) and the CPU decode path
  `DecodePngPremultiplied` (SkiaSharp asks for premultiplied RGBA directly; Stb
  fallback premultiplies manually). Used by every loader: `SpriteAtlas`,
  `AtlasCache` (its disk cache stores **premultiplied** RGBA), `EnvironmentSystem`,
  `GroundSystem`, `Flipbook`, `NineSlice`, `RuntimeWidgetRenderer`, `GrassTuftRenderer`,
  `MagicPathIcons`, editors. There is **no `Content.Load<Texture2D>` anywhere**; fonts
  are pipeline-built with `PremultiplyAlpha=True` (`resources/Content.mgcb`). The only
  straight-alpha load is deliberate: `Editor/EnvObjectEditorWindow.cs` edge tweaker
  (`Texture2D.FromStream` raw, because it saves pixels back to the PNG — premultiplying
  would darken edges every load→save cycle; `PremultiplyCopy` makes its preview).
- **CPU color helpers** — `Core/ColorUtils.cs`: `Premultiply(Color)` (used only by
  `Material.Tint`), `Fade(c,t)`, `ByteColor` (straight); `Core/HdrColor.cs`
  `ToHdrVertex/ToHdrVertexAlpha` for HDR vertex encoding (native, bypasses
  conversion via HdrAlpha/HdrAdditive's `PremultiplyTint=false`). A raw
  `new Color(r,g,b,128)` handed to a scope draw is now CORRECT by default.
  `Editor/ColorHarmonizer.cs` un-premultiplies → does color math →
  re-premultiplies (pixel-level texture ops, not draw tints — unchanged).
- **Blend states** — `Render/Material.cs` `Materials` registry: everything is
  `AlphaBlend` (Scene, Hud, FogWisp, Wading, DissolveTree, MorphSdf, HdrAlpha,
  MagicGlyph, DepthStamp) or `Additive` (AdditiveShapes, HdrAdditive, OutlineAdditive)
  — **except `OutlineAlpha` = `NonPremultiplied`**, matching OutlineFlat.fx's
  straight-alpha output (the one sanctioned exception). Ad-hoc `Begin`s outside the
  registry: editor UIs (`EditorBase`, previews) = AlphaBlend/PointClamp;
  `SpellPreview`/`BuffPreview` add `Additive` FX passes into their own RTs; `Bloom`
  internals = `Opaque` (alpha irrelevant); raw-device set: `GodRayRenderer` =
  Additive, `ShadowRenderer` = AlphaBlend; `UIShaders` restore hardcodes
  AlphaBlend+PointClamp.
- **Shaders (resources/*.fx)** — output **premultiplied**: Wading, DissolveTree,
  MorphSDF, HdrSprite (alpha mode; the additive mode is blend-agnostic), MagicCircle
  (`return float4(color*alpha, alpha)`), WeatherFog (composites haze/flash in premult
  space), FogSmooth (premult-output lerp trick), UIGradient/UIRectShadow/UICircleEffect
  (premultiply stops/inputs BEFORE interpolating — lerping straight colors gives dark
  fringes), FogComposite (RGB=0 so straight==premult). **Straight alpha**: OutlineFlat
  only (header warns; NonPremultiplied/Additive batches only). Blend-irrelevant:
  Bloom chain + GaussianBlur (Opaque blits), GroundShader (opaque quad), DepthCutout
  (depth-only), HdrIntensity (additive god rays).
- **Known bug history** (grep these commits when a new premult bug appears):
  `d626422` (fleet review: HdrSprite fade, WeatherFog haze, Wading ghosts 2.5× dark,
  UIGradient fringes), `fa4098a` (rain), `e457c47` (glow rectangles), `40329a3`
  (hover tint), `fddeb57` (faint hover), `ab34130`; `todos/unit_tooltip_panel.md`
  (HarmonizeTexture shifted premul RGB), `todos/editor-review-findings.md` E1 (edge
  tweaker re-premultiply data loss). Design rationale:
  `todos/render-pipeline-design.md` ("Premultiplied alpha" row — the convention is
  written in exactly one file, Material.cs).

## Rendering feature inventory — everything we've tried, and its status

One entry per feature: where it lives, how it works in a sentence, status
(**working / partial / known-weak / failed-reference / absent**), gotchas. Statuses as
of 2026-07-03; the deferred-issue list behind several of them is
`todos/shader-review-followups.md`.

### Fog & visibility
- **Death fog (ground blight fog)** — WORKING. Sim: `Game/DeathFogSystem.cs`
  (coarse 4-unit grid, heat-equation diffusion, sources/sinks from env-def
  `FogEmitRate`/`FogAbsorbRate`; also drives tree corruption and ground-vertex
  corruption). Render: `Render/DeathFogRenderer.cs` — **no shader**, one cloud-flipbook
  puff per active cell (density→alpha/scale, `MaxAlpha=0.20`), appended into the shared
  Y-sort depth list (`DepthItemType.DeathFogPuff`) so units occlude/are occluded by fog
  by ground Y. For the true "unit rises out of the fog" volume, see
  **`Render/GroundFogSystem.cs`** (2026-07-03, depth-stamped wisps — pipeline section above).
- **Fog of war** — WORKING (GPU, RT-based). `Render/FogOfWarSystem.cs`: visibility RT
  (vision circles) → temporal smooth via `FogSmooth.fx` → cumulative explored RT →
  packed combine; composited post-bloom by `FogComposite.fx`. CPU tile mirror for
  gameplay culling. Gotchas: full 2048² passes every frame even when vision is static;
  `FogSmooth` 8-bit RT stalls at uncapped FPS (grey veil).
- **Weather fog / haze / vignette** — WORKING. `Render/WeatherRenderer.cs` `DrawFog`
  via `WeatherFog.fx` in the HUD pass: world-anchored scrolling noise fog, haze,
  vignette, lightning screen-flash. Brightness/tint blocks in the shader are pinned
  neutral — scene darkening moved pre-bloom to ambient (see day/night) to avoid
  double-darkening.

### Weather & time of day
- **Weather presets** — PARTIAL. Data-driven `WeatherEffects` presets in
  `user settings/weather.json` (dreary_rain, thunderstorm, dawn, night, evil_night, …):
  brightness/contrast/saturation, tint, ambient, vignette, fog, haze, lightning
  intervals, rain params, **windStrength/windAngle (in data, consumed by nothing)**.
- **Rain** — WORKING. `WeatherRenderer.DrawRainParticles`: procedural hash-grid drop
  field, screen-space streaks + splash ellipses, zoom-scaled, wind-drifted, drawn in
  the scene pass. Cap `MAX_RAIN=6000`. **Snow: ABSENT. Wind (visual): ABSENT.**
- **Day/night cycle** — WORKING, but color-grading only. `Game/DayNightSystem.cs`:
  Dawn/Day/Dusk/Night phases each map to a weather preset, eased lerp between them,
  writes `RuntimeEffects` that `WeatherRenderer.GetEffectiveEffects` serves. Night =
  a global ambient multiply (GroundShader `AmbientColor` + sprite tints via
  `WeatherRenderer.GetAmbientColor()`), applied pre-bloom so HDR effects still glow
  against the darkened scene. **No local lighting of any kind** — see target #5.

### Partial submersion & transparency
- **Wading (units part-submerged in water)** — WORKING; the closest precedent for
  "units sticking up out of fog". `resources/Wading.fx` cuts the sprite along a
  waterline expressed in frame-local UV (center + slope, optional second cut for the
  submerged back of quadrupeds), fades below-line pixels to `UnderwaterAlpha`, draws a
  foam band on the line; fully premultiplied. Per-unit line resolution in
  `Render/WadingState.cs` (waterness sampling + per-facing profiles from
  `data/wading_defaults.json`); draw in `GameRenderer.Units.cs` `DrawWadingSpriteFrame`;
  `ShadowRenderer` reuses the same state so shadow and sprite share one waterline.
  Gotcha: wading units still stamp their full silhouette as fog depth-occluders.
- **Tree dissolve (corruption morph)** — WORKING. `DissolveTree.fx` noise-threshold
  dissolves the live sprite into the corrupted sprite per instance (Seed, EdgeSoftness).
  It is the death-fog corruption transition, **not** an occlusion fade — there is no
  "tree fades when it hides a unit" feature (target #3).
- **Depth cutout** — WORKING (narrow). `DepthCutout.fx` writes depth-only unit
  silhouettes (`DrawFogDepthOccluders`, behind the `DepthSortedFog` perf setting) so
  additive reanim smoke depth-tests against units. The only real depth-buffer use in
  the pipeline; C++ had a general depth-sprite pass (`building_depth`/`wall_depth`/
  `depth_sprite.fs`), C# does not.
- **General transparency sorting** — the Y-sort depth list (`_depthItems`) is the only
  mechanism; grass tufts, poison/fog puffs, reanim dust, ghost units (premultiplied
  tint + `DrawGhostOutline`), fading corpses all ride it.

### Emissive / additive / HDR
- **HDR sprites + additive passes** — WORKING. `HdrSprite.fx` (AlphaMode 0=additive
  /1=alpha, intensity encoded in vertex color via `Core/HdrColor.cs` `ToHdrVertex`/
  `ToHdrVertexAlpha`) and `HdrIntensity.fx` (god-ray trapezoid geometry). Scene RT is
  `HalfVector4` so additive exceeds 1.0 and bloom picks it up. **Never use
  `HdrColor.ToScaledColor()` outside the color picker** — it bleaches HDR
  (`todos/bloom_parity.md`).
- **Premultiplied-alpha convention** (commit `d626422`): scene pass = AlphaBlend +
  LinearClamp over premultiplied textures; every scene shader outputs premultiplied
  (Wading, DissolveTree, MorphSDF, MagicCircle, HdrSprite). Exception: `OutlineFlat.fx`
  outputs straight alpha (NonPremultiplied/Additive only — header warns).
- **God rays** — WORKING, known-weak parity. `Render/GodRayRenderer.cs`: layered
  trapezoid strips + edge sublayers + ground aura, per-sublayer HDR intensity. Renders
  brighter/whiter with less spread than C++ (see bloom).
- **Lightning** — WORKING. `Render/LightningRenderer.cs` + `Game/LightningSystem.cs`:
  telegraph circle, radial flash, HDR bolts and drains.
- **Bloom** — WORKING, KNOWN-WEAK vs C++. `Render/Bloom.cs` mip chain
  (`BloomExtract`/`BloomCombine`/`BloomUpsampleBicubic`). Full parity investigation in
  `todos/bloom_parity.md`; suspects: HLSL-vs-GLSL bicubic, a disabled Gaussian soften
  pass (`if (false &&` in Bloom.cs), mip sizing, `ToScaledColor` overuse. Related
  memory: C++ raylib ignored per-sublayer HDR intensities (batching bug), so C++-tuned
  intensity values in spells.json are inflated — retune down when porting
  (`todos/rendering_pitfalls.md`).

### Surfaces & world
- **Ground shader** — WORKING, feature-rich. `GroundShader.fx` (one opaque quad over
  the vertex-map texture): 8 texture slots + bit-packed type map, per-type
  `TintColors[16]` after 4-corner bilerp, animated shore foam for water types, noise UV
  warp, **corruption crossfade** (R=current/G=original/B=progress per vertex, fading
  texture AND tint AND water flag), global `AmbientColor`.
- **Shadows** — WORKING, two techniques. `Render/ShadowRenderer.cs`: ellipse mode (two
  soft glow ovals) or shader mode (sun-angle-skewed parallelogram quads sampling the
  current animation frame as silhouette); per-env-def `ShadowType`/scales, crops to
  wading waterline, crossfades reanimating corpses and corrupting trees. Wall shadows
  live in `WallSystem.DrawWalls`, not here.
- **Outlines** — WORKING. `OutlineFlat.fx` + `DrawSpriteOutline` (8 offset redraws):
  buff pulsing outlines, ghost outlines, reanim-rise attachment.
- **Magic glyphs** — WORKING, known-weak AA. `MagicCircle.fx` — fully procedural rings
  /runes/pentagram/energy; edge widths in normalized UV so it blurs large and aliases
  small; activation flare LDR-capped. Highest-payoff deferred shader rework.
- **Reanim morph** — WORKING. `MorphSDF.fx` interpolates death-pose/standup-pose SDFs
  with a mid-morph bulge + green energy fill (`Render/ReanimMorph.cs`). Gotcha: t=0
  re-threshold pop on soft-edged art.
- **Soul orbs** — WORKING but crude: two stacked non-premultiplied `_pixel` quads
  (`DrawSoulOrbs`).

### UI effects
- **`Render/UIShaders.cs`** — WORKING, the sanctioned path: `UIGradient.fx`
  (vertical/horizontal/3-stop/radial), `UIRectShadow.fx` (drop + inset), and
  `UICircleEffect.fx` (AA circle + glow ring), each with no-shader fallbacks. Policy:
  isolated test scenario per shader before real use (`todos/css_rendering.md`).
  Gotcha: the hardcoded resume `Begin` discards caller scissor/transform.
- **`Render/UIGfx.cs`** — FAILED-REFERENCE, since **removed**. The earlier
  stacked-SpriteBatch attempt at CSS effects (gradients/glows/emboss) produced
  banding/halos and was rejected; `UIShaders` replaced it. `Render/SkillTreePanel.cs`
  still carries placeholder visuals from that era.

### Absent entirely
- **Local lighting** — no point lights, no torch/lantern glow, no light-around-unit.
  "Lighting" today = global ambient multiply + emissive HDR/bloom. (Target #5.)
- **Custom vertex shaders** — C# is pixel-shader-only on SpriteBatch's built-in VS
  (see `memory/mgfx_shader_gotchas.md`); C++ had `grass.vs`/`road.vs`. Grass/roads are
  CPU-transformed sprites here.
- **Snow, visible wind** — data fields exist (`weather.json`), nothing renders them.

### Read-before-touching notes
- `todos/rendering_pitfalls.md` — C++ raylib → MonoGame porting gotchas (batched
  SetShaderValue, RT DiscardContents, Additive=SRC_ALPHA, HDR alpha accumulation).
- `memory/mgfx_shader_gotchas.md` — MGFX/OpenGL defaults all uniforms to 0: C# must
  set every uniform every draw.
- Rendering test scenarios: `blend_test`, `godray_render_test`, `BloomDebugScenario`
  (identical-parameter twins exist in C++ for pixel-readback comparison).

## Bloom & HDR intensity — the pass internals (where glow is thresholded/composited)

The scene→bloom→screen chain, concrete files:

- **`Render/Bloom.cs` (`BloomRenderer`)** — the C# pass that owns everything. `Init` loads
  the four shaders; `CreateTargets` allocates the **`SurfaceFormat.HalfVector4` scene RT**
  (LDR `Color` fallback) so additive draws exceed 1.0, plus a half-res mip chain (up to
  `MaxMips=8`, each ½ the previous). `BeginScene` binds+clears the scene
  RT (Scene phase `OnBegin`). **`EndScene` is the whole post pass**, four steps (Jimenez
  SIGGRAPH-2014 style since 2026-07-11):
  1. **Prefilter/extract** → `BloomExtract.fx`: 13-tap downsample with **Karis-weighted
     group averages** (firefly suppression) + soft-knee threshold, into mip[0].
  2. **Downsample** chain → `BloomDownsample.fx` (13-tap Jimenez filter, no Karis;
     `iters = clamp(Iterations,1,mipCount)`).
  3. **Upsample** chain — additive scatter blend (`ColorSourceBlend=BlendFactor`,
     `Dest=One`, `BlendFactor=scatter*255`), optional `BloomUpsampleBicubic.fx`.
     (The old extra 15-tap `GaussianBlur.fx` pass was removed — spread comes from
     iterations/scatter.)
  4. **Composite** → `BloomCombine.fx` binds bloom to `s1`, `outputTarget` = back buffer:
     `base + bloom*intensity`, then an optional **shoulder tonemap** — identity below
     `TonemapShoulder`, extended-Reinhard rolloff reaching white at `TonemapWhitePoint`,
     with `TonemapDesaturate` blending hue-preserving (max-channel) vs per-channel
     (film-like bleach) compression. Tonemap off = legacy hard clip at the back buffer.
- **Shaders (`resources/`)**: `BloomExtract.fx` = 13-tap Karis prefilter + **max-channel
  brightness** soft-knee quadratic threshold; `BloomDownsample.fx` = 13-tap chain filter;
  `BloomCombine.fx` = additive combine + shoulder tonemap (see above).
- **Knobs** = `BloomSettings` in `Data/Registries/GameSettings.cs` (persisted in
  `user settings/settings.json` under `bloom`): `threshold=0.8`, `softKnee=0.5`,
  `intensity=1.0`, `scatter=0.7`, `iterations=6`, `bicubicUpsampling=true`, plus
  `tonemap=true`, `tonemapShoulder=0.9`, `tonemapWhitePoint=6`, `tonemapDesaturate=0.4`.
  UI in `Editor/SettingsWindow.cs` `DrawBloomTab`. Wired in
  `GameRenderer.Pipeline.cs` (`_frameBloomSettings`, `BeginScene` at Scene `OnBegin`,
  `EndScene` at `OnEnd`).
- **HDR intensity encoding** = `Core/HdrColor.cs`, cap **`MaxHdrIntensity=16f`** (sqrt
  encoding since the sqrt-encode change — the alpha byte stores `sqrt(I/16)`, the shader
  squares it back; must match `HdrSprite.fx`'s decode. It was 4 before — old docs/comments
  saying 4 are stale). Two encoders: `ToHdrVertex` (additive — fade baked into RGB,
  encoded intensity in the alpha byte) and `ToHdrVertexAlpha` (alpha mode — encoded
  intensity baked into RGB, real fade in alpha). `HdrSprite.fx` decodes both (AlphaMode lerp); scene
  emissives thus write >1.0 into the HalfVector4 RT and feed bloom. `ToScaledColor` bleaches
  HDR — **color-picker only**. `HdrIntensity.fx` = god-ray VS/PS (`rgb*max(Intensity,0)`).

### Where each spell/buff glow layer is drawn (blend + intensity per source)
- **EffectManager one-shots (spell impacts, cast flares)** — `GameRenderer.World.cs`
  `DrawEffectsFiltered(blendMode)`: per-`Effect` uses `eff.HdrIntensity`/`eff.Tint`/
  `eff.BlendMode`, encodes via `ToHdrVertexAlpha` (blend 0) or `ToHdrVertex` (blend 1).
  Submitted in the **`HdrEffects` SpriteQueuePass** (`GameRenderer.Pipeline.cs`
  `CollectFxItems` → `WorldLayer.EffectsHdrAlpha`/`EffectsHdrAdditive`, materials
  `Materials.HdrAlpha`/`HdrAdditive`). These DO write true HDR into the scene RT.
- **BuffVisualSystem (auras/orbitals/lightning/weapon particles)** —
  `Render/BuffVisualSystem.cs`, a **native-encoding island drawn through the raw Scene
  batch (AlphaBlend), NOT the HDR additive pass**. Its `EncodeColor(hdr, alpha, blendMode)`
  additive path uses the A=0 premult trick and **CPU-clamps per channel to 255**
  (`hdr.R * min(Intensity,4) * alpha`). So buff glows are LDR-capped and only bloom if their
  clamped RGB clears `threshold` — they cannot overflow the HalfVector4 RT the way
  `HdrSprite` effects do (relevant if you want buff glows to bloom like spell effects).

### Layered-additive intensity — how stacking is tamed (updated 2026-07-11)
Overlapping additive layers from one spell each add independently into the scene RT
(`base + additive`, unbounded up); the only per-layer ceiling is `MaxHdrIntensity=4` at
encode time. Since 2026-07-11 the **shoulder tonemap in `BloomCombine.fx`** rolls the
summed result off smoothly (white only at `tonemapWhitePoint`) instead of hard-clipping
at 1.0, so stacked layers stay colored. There is still **no per-spell clamp** — if global
compression proves insufficient, the remaining options are **intensity budgeting** at
spawn (`Render/EffectManager.cs` spawn API + `SpellEffectSystem`/`Game1.Spells.cs`
callers) or a **Max-blend within-spell layer group**. Related notes:
`todos/rendering_pitfalls.md` (C++ ignored per-sublayer HDR intensities, so C++-tuned
spells.json values are inflated — retune down rather than compensating with clamps).

## Target feature set — what we want the renderer to do

The wishlist that the pass-based redesign (previous section) should be designed
against. Each item names the existing ingredient tech so a design can build on it
instead of starting fresh.

1. **Ground fog that units rise out of.** Fog should read as a volume: a unit standing
   in it shows torso/head above the fog with legs swallowed, not just "puff sprite in
   front of / behind the unit" (today's death fog is Y-sorted opaque puffs, item 1 of
   the inventory). Ingredients already built: **`Wading.fx`** proves per-sprite
   partial-height cutting with a soft band at the line (swap waterline → fog line,
   foam → fog wisp), and **`DepthCutout.fx`** proves depth-only unit silhouettes
   against a translucent layer. Likely shape: a fog layer pass whose alpha per pixel
   knows "how deep into the fog is this sprite pixel" — either the wading-style
   per-sprite cut driven by local fog density, or a screen-space fog quad
   depth-tested against sprite silhouettes.
2. **Proper transparent objects.** Anything translucent (ghosts, fog puffs, grass,
   smoke, glass-like effects) should sort correctly against units and each other. The
   Y-sort depth list is the seed; the redesign's transparent pass should generalize it
   rather than each feature hand-inserting item types.
3. **Occlusion fade.** A tree/building that hides a unit (especially the player)
   should go semi-transparent. Absent today — `DissolveTree.fx` is the corruption
   morph, not this. Needs a "what does this object occlude" test + per-object alpha,
   which the depth-list pass already has the data for.
4. **Additive/emissive objects as first-class citizens.** HDR + additive passes and
   bloom exist and work; keep them correct through any redesign (premultiplied
   convention, HalfVector4 scene RT, ambient applied pre-bloom so emissives still glow
   at night). Reach bloom parity with C++ (`todos/bloom_parity.md`) — softer, wider
   glow rather than bright-white cores.
5. **Local lighting.** Torches, lanterns, spell glows, windows at night — pools of
   light that punch through the night-time ambient darkening. Nothing exists.
   A cheap 2.5D take: a light-accumulation RT (additive glow sprites per light source)
   multiplied/screened over the darkened scene before bloom; full per-pixel normals
   are NOT the goal.
6. **Weather as a system, not one effect.** Rain works; want snow, drifting wind
   (bend grass/trees — the data fields already exist in `weather.json`), and storms
   that compose rain + wind + lightning flash + bolt strikes. Presets + day/night
   already interpolate; new effects should slot into `WeatherEffects` the same way.
7. **Day/night that changes more than color.** Keep the ambient grade, add the things
   ambient can't do: local lights mattering at night (#5), longer/softer shadows by
   sun angle (ShadowRenderer already has `SunAngle`/`LengthScale` — drive them from
   `DayNightSystem`), fog that thickens at dawn/dusk (fog density is currently
   deliberately not interpolated).
8. **Fog-of-war that scales.** Keep the RT approach but stop redrawing 2048² every
   static frame; fix the 8-bit smoothing stall (both flagged in
   `todos/shader-review-followups.md`).

## Visual effects (the "play a one-shot visual at a point" system)

### `Necroking/Render/EffectManager.cs` — general world visual-effect pool
What lives here: the lightweight visual-effect system. `class Effect` is one timed visual
(position, `Lifetime`, alpha/scale `BezierCurve`s, `Tint`, `HdrIntensity`, `FlipbookKey`,
`BlendMode` 0=alpha/1=additive, `Alignment` 0=ground/1=upright). `class EffectManager`
owns a `List<Effect>`, `Update(dt)` ages and culls them, and exposes the **spawn API**.
These are pure visuals — no gameplay/sim state.
Key members: `EffectManager.SpawnSpellImpact(pos, scale, tint, flipbookKey, hdrIntensity,
blendMode, alignment, duration)` — the general "flipbook at a world point" spawn;
`SpawnExplosion(pos, radius)`, `SpawnDustPuff(pos)` — preset one-liners; `Update`,
`Clear`, `Effects` (read-only list); the `Effect` fields above.
Look/edit here when: adding a **new kind** of generic visual effect, adding a new `SpawnX`
preset, or changing how effects fade/scale/age. **New effect-spawn methods go here.**
`SpawnDustPuff(Vec2 pos)` is the ready-made dust preset (0.5s life, brown tint, no
flipbook) — call `_effectManager.SpawnDustPuff(pos)` to kick up dust at a world point
(prior art: `Game/ForagableSystem.cs` calls it on pickup).
See also: spawned via `Game1.Spells.cs` helpers; drawn by **`GameRenderer.World.cs`**
(`DrawEffectsFiltered` iterates `_effectManager.Effects`) — NOTE: the render passes were
extracted from the old `Game1.Render.*` partials into a `GameRenderer` class
(`GameRenderer.{Draw,World,Units,Corpses,Hud}.cs`) that reaches back into `Game1` via a
`_g` field; the `_effectManager` field + its per-frame `Update` live in `Game1`
(see [game1-partials.md](game1-partials.md)).

## HUD/UI icons & unit portraits (path→Texture2D, UnitDef→portrait)

Two distinct "draw an image in the HUD/UI" paths, both reusable from **either** an
`EditorBase`-driven window OR raw `GameRenderer`/HUD code — because `EditorBase.Scope` and
`Game1.Scope`/`_g.Scope` wrap the **same `SpriteBatch`** (`_editorUi.SetContext(_spriteBatch, …)`
in `Game1.cs`), so a draw issued through `_g.Scope` lands correctly even while an EditorBase
window is drawing (as long as that shared batch is open — don't straddle an
`EditorBase.BeginClip`/`EndClip` re-Begin).

- **Icon by path (spell/item/potion/skill icons).** `SpellDef.Icon`/`ItemDef.Icon` are
  **relative PNG path strings**. The HUD spellbar draws them in
  `Necroking/UI/HUDRenderer.cs` `DrawSpellBar` (`_widgets.DrawIcon(spell.Icon, innerRect.X,
  innerRect.Y, innerRect.Width, innerRect.Height)`; `_hoverSlotSpell` feeds the slot
  tooltip). `_widgets` is a **`RuntimeWidgetRenderer`** — the SAME shared instance
  `Game1._widgetRenderer` (field in `Game1.cs`) used by every widget panel. Its
  `DrawIcon(iconPath, x, y, w, h)` (`Necroking/UI/RuntimeWidgetRenderer.cs`) does
  `GetOrLoadTexture(iconPath)` → `Scope.Draw`, silently no-ops on empty/missing path,
  and is backed by `UI/WidgetResourceCache` which wraps **`Render/TextureCache`**
  (`Necroking/Render/TextureCache.cs`) — the canonical path→`Texture2D` get-or-load
  (premultiplied via `TextureUtil.LoadPremultiplied`, negative-caches misses so a bad path
  is probed once). **To draw a spell icon by id anywhere: resolve the `SpellDef`, then call
  `Game1._widgetRenderer.DrawIcon(spell.Icon, …)`** — no new cache needed. `EditorBase` also
  has `DrawTexture(tex, pos, src, color, rot, origin, scale, fx)` and `DrawElementImage`, but
  those need a resolved `Texture2D`; prefer `DrawIcon` for path→draw.

- **Unit/necromancer portrait for a `UnitDef`.** `GameRenderer.DrawUnitIdleSprite(string
  unitDefId, Rectangle dest)` (`Necroking/GameRenderer.Units.cs`, `internal`) is THE existing
  UnitDef→portrait renderer: `gameData.Units.Get(id)` → `def.Sprite` → `AtlasDefs.ResolveAtlasName`
  → `_g._atlases[..]` → prefers the **"Icon" pose** (yaw 45 faces the viewer), falls back to
  **"Idle"** with an angle-preference list → first `Keyframe.Frame` → `atlas.GetTextureForFrame`
  → fit-inside centered draw to `_g.Scope`. It is exposed to panels via the
  **`DrawUnitIconCallback` (`Action<string,Rectangle>`) seam** — wired in `Game1.cs`
  (`_unitInfoPanel.DrawUnitIconCallback = (defId, rect) => _gameRenderer.DrawUnitIdleSprite(defId, rect)`,
  also `_tableMenuUI` and the scenario `DrawUnitSprite`). `UnitInfoPanel` calls it for the
  right-side sheet portrait (`ud_portrait` child rect). **Reuse this for a necromancer-form
  portrait**: pass the form's `UnitDef` id; hand EditorBase-driven windows a callback rather
  than a `GameRenderer` reference (matches precedent). Static-thumbnail alternative (no
  `GameRenderer`): `def.SpriteData.GetAnim("Idle")` → angle frames → `atlas.GetTextureForFrame`
  → `_eb.DrawTexture` (the `UnitEditorWindow.DrawPreviewSprite` pattern — see
  [editor.md](editor.md)).

- **Skill-tree morph icons ("Become Pale Acolyte" etc.) are NOT rendered portraits.** They
  are static pre-generated PNGs by **convention `assets/UI/Icons/Skills/{skill.Id}.png`**
  (generated by `tools/gen_skill_icons.py`), resolved in `UI/SkillBookOverlay.cs`
  `SkillIcon(def)` (fallback → a cost item's `.Icon` → `MagicPathHelpers.IconPath(Death, 24)`),
  drawn via `_renderer.SetImage(inst, "icon", …)`. `SkillDef` has **no** `Icon` field. So a
  form-portrait card should use `DrawUnitIdleSprite(formDefId, …)`, not the skill's PNG.

**Where a shared "save-preview card" helper goes:** a new method on `GameRenderer` (e.g.
`DrawSavePreviewCard(Rectangle dest, string necroFormDefId, IReadOnlyList<string> spellIconPaths)`)
reusing `DrawUnitIdleSprite` + `_g._widgetRenderer.DrawIcon`, drawing through `_g.Scope`. The
HUD Load-game list (`GameRenderer.Hud.cs`) calls it directly; `UI/SaveGameWindow.cs` (holds only
an `EditorBase`) gets it as a delegate wired in `Game1.cs` — mirroring the `DrawUnitIconCallback`
seam — so it needs no `GameRenderer` reference. The spellbar slot→`SpellDef` lookup (first 4
icons) mirrors `DrawSpellBar`'s per-slot resolution from `gameData`/`inventory`; spellbar
assignments persist in `user settings/spellbar.json` (see [user-settings.md](user-settings.md)),
but for a SAVE preview the form id + slot spells come from the save payload, not the live sim.

## Floating text / damage numbers ("+5", "Too Far", "Horde Full", pickup gains)
One system: `struct DamageNumber` in `Necroking/Game/SpellEffectSystem.cs`
(`WorldPos` (Vec2, world), `Damage` (int), `Timer`, `PickupText` (string → renders text
instead of the number), `Height` (**world-units height passed to `WorldToScreen`**),
`IsPoison`/`IsFatigue`/`IsAlert` (color: green/blue/red; alert also drops the "+" prefix)).
- **Spawn** — add to `Game1._damageNumbers`. Canonical alert channel:
  `Game1.cs::SpawnCastFailText(necroIdx, message)` (used by every cast-fail reason AND
  world-click feedback like "Too Far"/"Pile Empty" in `Game1.WorldClicks.cs`). Pickup gains:
  `Game1.Crafting.cs` and `SpellEffectSystem` push their own entries.
- **Update** — `Game1.cs` (~`UpdateDamageNumbers` block): `Timer += dt`,
  `Height += Settings.General.DamageNumberSpeed * dt` (the float-upward drift).
- **Draw** — `GameRenderer.World.cs::DrawDamageNumbers`: fades over `DamageNumberFadeTime`,
  fog-of-war culled, `sp = WorldToScreen(WorldPos, Height)` then centers the string on `sp`.
- **Height trap:** `WorldToScreen`'s height lift is `Height * Zoom * YRatio` (YRatio=0.5)
  but sprites are drawn `SpriteWorldHeight * SpriteScale * Zoom` pixels tall (no YRatio).
  So to place text at a unit's HEAD you need `Height ≈ spriteWorldH * SpriteScale / YRatio`
  (i.e. ~2× the sprite's world height), plus `unit.Z`. A `Height` equal to the sprite
  height lands mid-sprite. `Unit.EffectSpawnHeight` is the weapon-tip/cast anchor (~0.6
  fallback), NOT head height; `Unit.CollisionHeight` is a constant 1.0 (only used for the
  status ?/! anchor `sp_upper` in `GameRenderer.Units.cs::DrawSingleUnit`, which also
  subtracts the glyph's own text height so it clears the head).

## World-space overlay drawing (lines / bezier over the world)
- **`Necroking/Render/DrawUtils.cs`** — `DrawLine(SpriteBatch, Texture2D pixel, Vector2 a,
  Vector2 b, Color)` (a rotated-pixel segment) and `DrawCircleOutline(...)`. These take
  **screen-space** points. To draw a rope/bezier in the world, sample the curve in world
  coords, convert each point with `_g._renderer.WorldToScreen(worldPos, height, _g._camera)`
  (see `GameRenderer.World.cs`), and chain `DrawUtils.DrawLine` between consecutive screen
  points using the 1×1 white texture `_g._pixel`. `struct BezierCurve` in
  `Render/Flipbook.cs` is a 1-D 4-control-point curve (used for effect alpha/scale), not a
  2-D spatial curve — for a positional rope compute the bezier point yourself.
- **Where a new world overlay pass goes**: add a `DrawX` method in `GameRenderer.World.cs`
  (it already batches world-space primitives like `DrawProjectiles`/`DrawEffectsFiltered`
  inside the world `_g._spriteBatch.Begin(...)` block) and call it from the world section of
  `GameRenderer.Draw.cs`. Use `WorldToScreen` for every endpoint; do not draw in world units.

## Lightning / zap / beam & drain-tendril rasterization (ribbon strips — REWORKED)

**The old rotated-1×1-pixel sprite-quad path is RETIRED.** Bolts/beams/drains are now
**miter-joined triangle-list ribbons** collected into `Render/HdrStripBatch.cs` and flushed
by `DrawUserPrimitives` with `HdrIntensity.fx` — seamless bends, soft cross-section falloff.

- **Data/sim side** (`Necroking/Game/LightningSystem.cs`, ns `Necroking.GameSystems`):
  `LightningSystem` holds `_strikes`/`_zaps`/`_beams`/`_drains` lists; `Update` (called from
  `Simulation.Tick`) ages them and applies drain/beam damage-heal ticks (`ActiveDrain.Elapsed`,
  `LightningDamage` with `IsHeal`/`DrainHealTargetIdx` coupling). `LightningStyle` carries
  bolt-shape knobs (`Subdivisions`, `Displacement`, branch params, `CoreColor`/`GlowColor` as
  `HdrColor`, `CoreWidth`/`GlowWidth`, `WidthFade`, `FlickerHz`/`JitterHz`). Drains carry
  **`DrainVisualParams`** (`TendrilCount`, `ArcHeight`, `SwayAmplitude/Hz`, `CoreWidth`/
  `GlowWidth`, `PulseHz/Strength`, `CoreColor`/`GlowColor`), built per-cast from spell data by
  `SpellRegistry.cs` `SpellDef.BuildDrainVisuals()` (`drain*` fields in `data/spells.json`;
  spawn = `SpellEffectSystem.cs` `case "Drain"` → `Lightning.SpawnDrain`). **No point list is
  stored** — geometry is regenerated every frame at draw time from live endpoints.
- **Beam spells are fully data-driven the same way**: a `category:"Beam"` SpellDef (e.g.
  `lightning_beam`) → `SpellEffectSystem.Execute` `case "Beam"` → `Lightning.SpawnBeam(casterUid,
  targetUid, spellId, ScaledDamage, BeamTickRate, BeamRetargetRadius, spell.BuildBeamStyle())` +
  `StartChannel` (channel-hold). `SpellDef.BuildBeamStyle()` (`SpellRegistry.cs`) maps the
  `beam*` JSON fields (`beamCoreColor`/`beamGlowColor` = HdrColor incl. bloom intensity,
  `beamCoreWidth`/`beamGlowWidth` px-at-zoom-32, `beamDisplacement`, `beamBranches`,
  `beamFlicker*`, `beamJitterHz`) into a fresh per-cast `LightningStyle`; matching
  `BuildStrikeStyle()`/zap builders exist for the other categories. **Duplicating a beam spell
  is data-only** (no code) as long as the visual change is expressible in existing style knobs.
  **GOTCHA: the `beamChain*` / `strikeChain*` / `chainQuantity` fields are declared in
  SpellDef and persisted in spells.json but consumed by NOTHING** (planned chain lightning) —
  copying them changes nothing.
- **Draw flow**: `LightningRenderer.Draw()` (`Render/LightningRenderer.cs`) runs inside the
  `_cbFxLightning` callback (`GameRenderer.Pipeline.cs`, `WorldLayer.EffectsHdrAdditive`);
  it only *collects* — projects endpoints to screen (caster anchor = `EffectSpawnPos2D` +
  `EffectSpawnHeight * Zoom` via `WorldToScreenPx`, target = `WorldToScreen(pos, 1f)`), then
  `AddBoltStrips` / `AddDrainTendrilStrips` append vertices into the `HdrStripBatch _strips`.
  Flush = the `LightningTris` `CustomPass` right after (`_lightningRenderer.DrawTriangleEffects()`
  → `_strips.DrawAll()` + god rays). Vertices are bucketed per HDR intensity
  (`HdrStripBatch.GetBucket`) because `VertexPositionColor` can't carry >1.0 — each bucket is
  one `DrawUserPrimitives` with `HdrIntensity.fx`'s `Intensity` uniform. Additive → bloom fires.
- **The shared ribbon builder**: `PolylineStrip.Build(outVerts, points, tint, alphaStart,
  alphaEnd, widthStart, widthEnd, edgeSoft)` (bottom of `HdrStripBatch.cs`) — miter joins,
  adaptive point merging (points closer than ~60% of half-width merge so wide ribbons don't
  fold), tent-function edge alpha. **It already supports linear taper** (`widthStart` ≠
  `widthEnd`) and end-to-end alpha ramp — callers currently pass equal widths.
- **Bolt shape**: `ComputeBoltShape` (flicker + JitterHz-quantized reseed) →
  `GenerateBoltPoints` (recursive midpoint displacement) + `GenerateBranches`; rasterized by
  `AddBoltStripsStatic` (glow ribbon wide-first, then core), edge softness constants
  `CoreEdgeSoft=0.5` / `GlowEdgeSoft=0.9` at top of `LightningRenderer.cs`.
- **Drain tendrils**: `AddDrainTendrilStripsStatic(strips, start, end, DrainVisualParams,
  elapsed)` — THE single tendril rasterizer (in-game + `Editor/SpellPreview.cs` +
  `SpellVisualTestScenario` all call it). Per tendril: start fanned by `SwayAmplitude`, end
  swayed by sine, shape from `BuildTendrilPoints` (segments = length/20, perpendicular
  arc `sin(t·π)·20` + travelling wave `sin(time·4 + t·8)·5` — **note: arc/wave amplitudes are
  HARDCODED 20/5 px; `DrainVisualParams.ArcHeight` is currently NOT consumed**), then two
  `PolylineStrip.Build` calls (glow @ alpha 120/255, core @ 200/255), widths pulsed by
  `PulseHz`/`PulseStrength`.
- **Not the same as `DrawUtils.DrawLine`**: the rope/bezier overlay helper
  (`Render/DrawUtils.cs`) is a straight-alpha screen-space rotated-pixel segment, unrelated.

**Tapered beam / width changes** are a one-liner class of change: pass different
`widthStart`/`widthEnd` (and optionally `alphaStart`/`alphaEnd`) into the
`PolylineStrip.Build` calls in `AddDrainTendrilStripsStatic` / `AddBoltStripsStatic` —
point[0] is the CASTER end (drains: caster hand → target). Anything drawn along the beam
path can reuse `BuildTendrilPoints`' point list parametrically (regenerate per frame, no
stored state — same convention as the ribbons themselves).

### `Necroking/Render/Flipbook.cs` — flipbook (sprite-sheet frame sequence)
What lives here: `class Flipbook` — loads a sprite-sheet texture (cols×rows, FPS) and
maps a frame index to a source `Rectangle`. `LoadFromDef(device, FlipbookDef)` builds one
from a registry def; `GetFrameRect(i)` returns the frame. A `flipbookKey` on an `Effect`
resolves to one of these.
Key members: `Load`, `LoadFromDef`, `GetFrameRect`, `Texture`, `Cols`/`Rows`/`TotalFrames`/`FPS`.
Look/edit here when: a flipbook plays the wrong frames/speed, or you're wiring up new
flipbook **art**. The flipbook **data** (id → sheet path, cols/rows/fps) is a `FlipbookDef`
in `Data/Registries/` (not yet documented) — that's where you register a new effect's art.

### `Necroking/Render/ReanimEffectSystem.cs` — composite reanimation "rise" effect
What lives here: a preset-driven, multi-part effect played at a grave on reanimate; it's
handle-based (returns an `FxInstanceId`) so an outline can attach to the spawning unit.
Look/edit here when: the reanimate/raise visual is wrong. Driven from `Game1.Spells.cs`
(`QueueReanimRise`/`TickPendingReanimRises`) with a preset id from `SpellRegistry`.

### Particle systems
- `Necroking/Render/BuffVisualSystem.cs` (`WPParticle`) — buff-aura particles.
- `Necroking/Render/WadingWakeSystem.cs` (`WakeParticle`) — water-wake particles behind
  units in water.

## Debug F-key toggles & debug overlays

**All the debug F-key handlers live in one block in `Necroking/Game1.cs` `Update`** (gated
on `!anyTextInputActive` so typing into an editor field doesn't trip them). Each toggle
just flips an `internal` field on Game1 (declared ~lines 724-844); the overlays are then
drawn from `GameRenderer.Pipeline.cs` (world-space) and `GameRenderer.Draw.cs`
(screen-space label). Census:

| Key | Field (Game1) | Type / cycle | What it shows |
|-----|---------------|--------------|---------------|
| F2 | `_waterDebug` | `bool` | water debug (drawn `GameRenderer.Units.cs`/`Draw.cs`) |
| F3 | `_showPerfReadout` | `bool` | bottom-left perf/zoom readout (`GameRenderer.Draw.cs`) |
| F5 | `DeathFogSystem.DebugVisible` via `_deathFog.ToggleDebug()` | `bool` | death-fog overlay |
| F6 | `_windDebug` | `bool` | wind debug (self-clears in `Pipeline.cs`) |
| F7 | `_gameplayDebugMode` | `int`, `(x+1)%3` | Off → Horde → Unit Info |
| F8 | `_collisionDebugMode` | `CollisionDebugMode` enum, `%Count` | Off/All/Chunks/CostField/UnitORCA/Velocity/OccupiedTiles (`Render/DebugDraw.cs`) |
| F9-F12 | `_menuState` | `MenuState` | open/close the Unit / Spell / Map / UI editors (NOT overlays) |

`CollisionDebugMode` enum + `DebugDraw.GetModeLabel` are in `Necroking/Render/DebugDraw.cs`.
The dev-command mirror for F7 is `Game1.Dev.cs` (`gameplayDebugMode`). **A new debug-mode
dropdown panel should write these same fields directly** (e.g. cycle/set `_collisionDebugMode`,
`_gameplayDebugMode`, `_deathFog.ToggleDebug()`), reusing the existing input block's logic.

## The player-data HUD (top-left status bars) — where to anchor a panel under it

**`Necroking/UI/HUDRenderer.cs` `DrawStatusBars(necroIdx, sim)`** draws the top-left HP +
Mana bars (the "player-data HUD"). Geometry constants at the top of `HUDRenderer.cs`:
`BarX = 10`, `BarWidth = 200`, bars start at `y=30` with `barH=24`, mana bar at
`y = 30 + barH + 4`. **Bottom edge of the block ≈ `30 + 24*2 + 4 = 82`px** — anchor a
new left-side panel at roughly `(x=10, y≈88)`. (When `ShowDebugPanel` is set, `DrawDebugPanel`
replaces the bars in the same top-left slot — a precedent for a left-side panel there.)
`DrawStatusBars` is invoked from `HUDRenderer.Draw`, which `GameRenderer.Hud.cs` `DrawHUD`
(the file the caller is editing) forwards into via `_g._hudRenderer.Draw(...)`.

## Reusable dropdown / panel widgets

- **The dropdown interaction standard is EAGER: `Necroking/Lib/EagerDropdown.cs`**
  (`class EagerDropdown` — engine-free press→drag→release state machine, ported from a
  proven Unity component; `OnPress(boxKey, itemIndex)` / `OnPressOutside()` /
  `OnRelease(itemIndex, gestureValid)` / `OpenKey`/`IsOpen`/`Close`). Press opens+arms,
  release-over-item selects (single gesture); click-click still works as a degenerate case.
  One instance covers N sibling dropdowns via an int key. The owner keeps all rects and
  hit-testing and feeds hit results in. Selected option gets a **left-gutter checkmark**
  via `Render/DrawUtils.cs` `DrawCheckmark` (options indent past the gutter).
- **Two dropdown renderers exist, both already eager:**
  1. **`Necroking/Editor/EditorBase.cs` `DrawCombo(fieldId, label, value, options, x, y, w,
     allowNone)`** — the immediate-mode editor combo (used ~80× across
     Map/Unit/Spell/Env/UI/Item editor windows + Settings tabs + `ReflectionPropertyRenderer`).
     Internally uses `_comboEager` (an `EagerDropdown`, key always 0; the OPEN combo id stays
     in `_activeFieldId`); list overlay deferred to `DrawDropdownOverlays()` (draws checkmark
     gutter, filter box when ≥15 items, scrollbar, keyboard nav). Requires an EditorBase.
  2. **Raw-HUD panel-owned dropdowns — reference implementation `Necroking/UI/DebugSettingsPanel.cs`**
     (+ its thin router seat `DebugSettingsPanelLayer` in `UI/Layers/HudLayers.cs`): the panel
     owns rect math (`BoxRect`/`OptionRect`), `ContainsMouse` (panel + open list), and one
     `EagerDropdown _dd`; presses arrive via the layer's `OnPointer` → `HandlePress`, and
     release-select + outside-press dismiss are **polled in `OnFrame` → `HandleFrame(input)`**
     (releases are never routed by the UIRouter, and the layer only gets presses while
     hovered). Pass `gestureValid = input.PressStartPos.X >= 0` to `OnRelease`. Copy this
     pair for any new raw-HUD dropdown panel.
- `HUDRenderer`'s `DropdownItemH`/`DropdownWidth`/`Dropdown*` color constants are dead
  C++-port leftovers — nothing draws them; don't take them as an existing widget.
- **Per-option hover tooltips**: neither renderer has them built in; pair with the global
  `Game1.Tooltips` (`UI/TooltipSystem.cs`) — request inside the option-draw loop
  (`EditorBase.DrawDropdownOverlays` `optHovered`, or `DebugSettingsPanel.Draw`'s open-list
  loop); the Tooltip band drains after everything so the box draws above the expanded list.
  Note `SettingsWindow.RowTip`'s `IsDropdownOpen` guard suppresses *row* tips under an open
  overlay — option tips inside the overlay are the exception, not that pattern.
- Panel chrome helpers for a raw-HUD panel: `GameRenderer.Hud.cs` `DrawPanel(rect, fill,
  accent, …)`, `UI/NineSlice.cs`, and `UI/RuntimeWidgetRenderer.cs` (JSON widget defs).

**Look/edit here when:** adding a debug-settings panel, wiring an F-key debug mode to a UI
control, or positioning a new left-side HUD panel relative to the player status bars.

## Chain: how a visual effect reaches the screen
1. **Art/id** — a `FlipbookDef` (id → sheet) in `Data/Registries/` defines the flipbook.
2. **Spawn** — `_effectManager.SpawnSpellImpact(pos, scale, tint, flipbookID, …)` (or add a
   new `SpawnX` in `EffectManager`). Game1 wraps this in `SpawnFlipbookEffect`/`SpawnCastEffect`.
3. **Update** — `_effectManager.Update(dt)` each frame (from `Game1.Animation.cs`).
4. **Draw** — `Game1.Render.World.cs` `DrawEffectsFiltered` renders `_effectManager.Effects`.

## Related
- [game1-partials.md](game1-partials.md) — `_effectManager` field (`Game1.cs`), spawn
  helpers `SpawnFlipbookEffect`/`SpawnCastEffect` (`Game1.Spells.cs`), `SpawnImpactEffects`
  + effect drawing (`Game1.Render.World.cs`), and keyboard input (`Game1.cs` `Update`).
- `Game/` (not documented) — systems that *trigger* effects: `SpellEffectSystem`,
  `Projectile` (→ `SpawnImpactEffects`), `LightningSystem`, `PoisonCloudSystem`.
- `Data/Registries/` (not documented) — `FlipbookDef` registry (effect art/ids).

## Consolidation update (2026-07-07)

- **FloatingText** (in Game/SpellEffectSystem.cs, next to DamageNumber) is the
  one spawn helper for damage numbers / floating text — head-height formula
  lives there only.
- **Render/TextureCache** = path->Texture2D get-or-load (6 former hand-rolls);
  **UI/WidgetResourceCache** = widget texture/nine-slice caches (editor +
  runtime share it); **TextureUtil.GetRadialGlow** = the cached radial glow.
- **Render/HashNoise** = CellHash/HashToFloat scatter hashes (DeathFog +
  GrassTuft). 2.5D projection: Camera25D is the single home (Renderer and
  GrassTuftRenderer delegate). Angle math: MathUtil.
- SpriteAtlas sync `Load` is reimplemented on the split-phase primitives;
  `LoadExtension` deliberately is not (placeholder-list bookend — see
  docs/consolidation-review/dossiers/texture-asset-caching.md).
