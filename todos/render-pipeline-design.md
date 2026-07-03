# Render Pipeline Redesign — Design (IMPLEMENTED)

Status: **IMPLEMENTED 2026-07-03** — all migration steps (0–6) plus occlusion
fade landed the same day the design was approved. Commits `6d152d1..2ac1dca`
on master. Implementation notes / deviations from the design as written:

- `SpriteScope` exposes `PushMaterial/PopMaterial` (closure-free) instead of a
  `WithMaterial(callback)`; plus `Suspend()/Resume()` for raw-device work
  (shadow quads, reanim sorted particles).
- Callbacks are cached delegates with `(int a, int b)` payloads — zero
  per-item allocation; no closures in the hot path.
- `Material` gained `SortMode` (Immediate is the sanctioned exception for
  runs of same-effect per-draw-param draws — magic glyphs) and `Rasterizer`.
- `RenderItem` gained `LayerDepth` for depth-testing materials (fog wisps).
- Foragables/damage-numbers stayed a small CustomPass (a 2-item queue added
  nothing); block layers in the World pass are whole-layer callback slots —
  granular per-sprite submission can come later without order changes.
- Ground fog shipped as `GroundFogSystem` (banks → back blanket at
  WorldLayer.FogBack + wisps at FogWisps through Materials.FogWisp,
  depth-tested vs the occluder stamps). Look knobs are constants at the top
  of GroundFogSystem.cs. `groundfog` dev command; `ground_fog` scenario.
- Occlusion fade shipped as a submit-time system in the world collect loop
  (constants in GameRenderer.Units.cs); `occlusion_fade` scenario.
- Dev command `pass list|on|off`; perf readout shows world/fx items-vs-batches.

The sections below are the original design, kept for the rationale.

---

Design for replacing the monolithic
`GameRenderer.Draw()` with a submit → sort → batch pipeline (Unity-style, but much
simpler). Written 2026-07-03 after a full read of the current renderer.

---

## 1. Summary

Three new concepts, and only three:

| Concept | What it is | What it replaces |
|---|---|---|
| **Material** | Immutable bundle: effect + blend + sampler + depth-stencil, compared by reference | The arguments to every hand-written `spriteBatch.Begin(...)` |
| **RenderItem + SortKey** | A submitted draw: sprite args (or a callback) + a packed `(layer, depth, material, seq)` key | Literal line order in `Draw()`; the `_depthItems` switch |
| **Pass / Phase** | An ordered data list: phases own render targets, passes own work | The 600-line imperative sequence; `Bloom.BeginScene/EndScene` call placement |

The frame becomes: game code **submits** items into named passes → each pass
**sorts** by key → the executor walks the sorted list and emits `Begin/End`
**only when the material changes**. No call site ever touches batch state again;
`EffectBatch`'s suspend/resume dance (and its bug class) disappears.

This generalizes what `DrawUnitsAndObjects()` already does — collect, key, sort,
dispatch — rather than importing something alien. It also keeps SpriteBatch as the
draw backend: we are reorganizing *who decides state*, not rewriting sprite
submission.

---

## 2. What exists today (read summary)

The facts the design must respect:

- **One giant `Draw()`** ([GameRenderer.Draw.cs](../Necroking/GameRenderer.Draw.cs)):
  ~15 `Begin/End` blocks in hardcoded order: ground → roads → traps → glyphs →
  walls → shadows → corpses → Y-sorted units/env/particles → projectiles → HDR
  alpha → HDR additive → additive shapes → god rays → damage numbers → bloom
  composite → fog-of-war → debug → HUD.
- **`EffectBatch`** ([Render/EffectBatch.cs](../Necroking/Render/EffectBatch.cs))
  is already "pass state as data": `SceneBlend/SceneSampler`, `HudBlend/HudSampler`
  are the canonical states, and `BeginEffect/EndEffectResumeScene` centralize the
  flush-swap-restore move after two shipped wrong-restore bugs. This is the seam
  the new system grows out of.
- **`_depthItems`** ([GameRenderer.Units.cs](../Necroking/GameRenderer.Units.cs),
  `Game1.cs:4161`): `struct DepthItem { float Y; DepthItemType Type; int Index;
  int SubIndex; }` with a deterministic tiebreaker (introsort is unstable), sorted,
  dispatched via switch. Six item types already interleave: units, env objects,
  poison puffs, grass tufts, death-fog puffs, reanim dust.
- **Composite draws**: `DrawSingleUnit` is not one sprite — it's ~5–30 draws in
  strict relative order (buff visuals behind → outlines → carried corpse
  behind/front → sprite (possibly wading-shader) → wake particles → HP bar → status
  text), all sharing one depth slot. Any item model that assumes "1 item = 1 sprite"
  is wrong on day one.
- **Per-draw uniforms break batches**: wading (`DrawWadingSpriteFrame`) and
  dissolve (`DrawDissolvingTree`) each set params then pay `End/Begin/Draw/End/Begin`.
  Ground (`DrawGroundShader`) sets ~15 uniforms + 8 textures, one fullscreen quad,
  Opaque + PointClamp, via the same suspend mechanism.
- **HDR already has a batching-friendly per-sprite param channel**:
  `HdrColor.ToHdrVertex[Alpha]` packs intensity into vertex color, decoded by
  HdrSprite.fx. The only per-batch uniform is `AlphaMode` (alpha vs additive
  sub-pass) — i.e. two materials, not per-draw params.
- **Depth-stencil is in play**: `DrawFogDepthOccluders` stamps unit silhouettes
  (color-write off, depth-write on, cutout shader, `FogDepthForY` camera-relative
  mapping) so `ReanimEffectSystem.DrawSortedParticles` can depth-test fog puffs.
  The scene RT is `HalfVector4 + Depth24Stencil8` — depth exists mid-frame.
- **Render-target phases**: fog-of-war updates its RTs *before* the scene RT is
  bound; bloom brackets the scene (`BeginScene`/`EndScene` composite); fog-of-war
  overlay composites after bloom; HUD draws last, PointClamp, unaffected.
- **The particle `Effect` class already declares `BlendMode` (0=alpha, 1=additive)**
  — but it's read by an orchestrator filter (`DrawEffectsFiltered(0/1)` called once
  per hardcoded pass), not used as data. The declaration exists; nothing sorts on it.
- Camera position is pixel-snapped for the scene, restored for HUD.
- Sampler-slot-1 leakage is a recurring bug class (dissolve, bloom both hand-set
  `SamplerStates[1]` with comments explaining why).

### The two structural defects

1. **State is positional.** What shader/blend/sampler a draw gets is decided by
   which lexical block it sits in. Adding a visual = finding the right line in the
   monolith; moving a visual between blend modes = moving code.
2. **Low-level code mutates global state.** Deep draw functions flush and re-open
   the shared batch, guessing (via EffectBatch, now centrally, but still guessing
   *which* pass they interrupted) what to restore.

---

## 3. Prior art (research summary)

The convergent architecture across bgfx, Ericson, Nez, and Unity is exactly one
sentence: *retained submit list → one packed integer key per item → sort → walk
the sorted list, flushing the batcher only when the key's state prefix changes.*

- **Sort keys** ([Ericson, "Order your graphics draw calls around!"](https://realtimecollisiondetection.net/blog/?p=86);
  [bgfx internals](https://bkaradzic.github.io/bgfx/internals.html)): pack
  layer/viewport, translucency class, depth, and material/program id into one
  integer, MSB→LSB in sort priority; sort once; walk linearly. The key rule:
  **opaque sorts material-major** (state changes are the cost, front-to-back for
  early-Z), **transparent sorts depth-major** (back-to-front correctness; material
  only breaks ties at equal depth). bgfx institutionalizes this as three per-view
  64-bit key encodings (program-major / depth-major / sequence). A 2D alpha-blend
  game lives *entirely* in the transparent regime — we take only the depth-major
  layout, which simplifies the key (§4.3).
- **Nez** ([Renderer.cs](https://github.com/prime31/Nez/blob/master/Nez.Portable/Graphics/Renderers/Renderer.cs),
  [Material.cs](https://github.com/prime31/Nez/blob/master/Nez.Portable/Graphics/Material.cs)):
  the closest shipped model. Renderables carry `renderLayer` + `layerDepth` + a
  `Material` (effect/blend/sampler/depth-stencil, **compared by reference
  identity only** — shared static instances, no value equality); the renderer
  walks the sorted list and **flushes only when a renderable's material differs
  by reference from the open one**; null material = renderer default, no flush.
  That is precisely the loop in §6. Nez proves reference-identity materials are
  enough for a real framework; our sort key adds the equal-depth material
  grouping Nez lacks.
- **Unity, the parts worth stealing** ([2D sorting](https://docs.unity3d.com/Manual/2d-renderer-sorting.html)):
  render-queue bands (all 2D sprites live at Transparent=3000 — painter's
  algorithm, confirming the "no opaque path" simplification); Sorting Layer +
  Order-in-Layer (our layer byte + sequence); the **Transparency Sort Axis**
  (sort by world Y for top-down — our depth field *is* that); **Sorting Groups**
  (a multi-sprite character sorts as one atomic unit, internal order preserved —
  our Callback item is exactly this); `MaterialPropertyBlock` as the "per-draw
  params without a new material" concept, with the recorded lesson that MPBs
  *break* SRP batching — i.e. per-draw uniform sets are inherently batch-hostile
  everywhere, which is why §4.2's tier model pushes params into vertex data
  first. The parts *not* worth stealing are §12.
- **MonoGame internals** ([SpriteBatch.cs](https://github.com/MonoGame/MonoGame/blob/develop/MonoGame.Framework/Graphics/SpriteBatch.cs),
  [issue #8295](https://github.com/MonoGame/MonoGame/issues/8295)): `Deferred`
  skips SpriteBatch's internal sort entirely — external sort + Deferred gives
  identical GPU behavior with a better, *stable* policy (SpriteBatch's own
  `BackToFront` sort is unstable → equal-depth flicker, the same hazard the
  `DepthItem` tiebreaker fixed). A **texture change inside one batch is a cheap
  per-texture flush (one extra draw call), not a Begin/End** — so texture id does
  not need to live in the sort key while sprites are atlased. `Immediate`'s one
  unique power is per-draw `EffectParameter` changes, at one draw call per
  sprite. Max 5,461 sprites per internal flush — irrelevant at our counts.
- **Premultiplied alpha** ([Hargreaves](https://shawnhargreaves.com/blog/premultiplied-alpha-in-xna-game-studio-4-0.html)):
  `BlendState.AlphaBlend` = (One, InvSrcAlpha) since XNA4. Consequence worth
  knowing: under premult, **an additive sprite is just an alpha-blend sprite with
  vertex alpha = 0** — additive and alpha-blended sprites *can* share one blend
  state and batch. Noted in §4.1 as a possible later merge of the HDR materials;
  not required by the design.
- **Community render queues**: there is no mature drop-in package besides Nez
  (MonoGame.Extended's batcher work was repeatedly rewritten and stalled) —
  rolling a thin queue over stock SpriteBatch is the ecosystem-validated answer,
  with a Nez/FNA-style custom Batcher (own vertex format, extra float channel)
  as the documented escalation *if* per-sprite shader data ever becomes common
  (§12, last bullet).

---

## 4. Data model

### 4.1 Material

```csharp
/// <summary>Complete SpriteBatch state for one Begin(). Immutable, created once at
/// load, shared by reference — batching compares materials with ReferenceEquals.</summary>
public sealed class Material
{
    public readonly string Name;                 // debug HUD / logging
    public readonly ushort Id;                   // assigned by registry; packed into SortKey
    public readonly XnaEffect? Effect;           // null → SpriteBatch's default shader
    public readonly BlendState Blend;            // Opaque / AlphaBlend(premult) / Additive / custom
    public readonly SamplerState Sampler;        // PointClamp / LinearClamp
    public readonly DepthStencilState DepthStencil; // usually None; Default for depth-tested passes
    // Extra texture bindings (slot >= 1) WITH their sampler states, applied at
    // batch open. Kills the "s1 inherits whatever the last pass left" bug class
    // (dissolve, bloom both hand-fix this today).
    public readonly (int Slot, SamplerState Sampler)[] ExtraSamplerSlots;
    public readonly bool RequiresPerDrawParams;  // wading/dissolve: executor knows
                                                 // items of this material can't merge
}
```

Canonical instances live in a static registry that **is** the grown-up
`EffectBatch` (same role: the single place pass state is defined):

```csharp
public static class Materials
{
    public static Material Scene;         // null fx, AlphaBlend, LinearClamp  (EffectBatch.SceneBlend/Sampler)
    public static Material Hud;           // null fx, AlphaBlend, PointClamp   (EffectBatch.HudBlend/Sampler)
    public static Material HdrAlpha;      // HdrSprite clone w/ AlphaMode=1, AlphaBlend, LinearClamp
    public static Material HdrAdditive;   // HdrSprite clone w/ AlphaMode=0, Additive,  LinearClamp
    public static Material HdrAdditiveDepthTested; // + DepthStencilState.Default (reanim fog vs unit stamps)
    public static Material AdditiveShapes;// null fx, Additive, LinearClamp    (energy columns)
    public static Material Wading;        // Wading.fx, AlphaBlend, LinearClamp, RequiresPerDrawParams
    public static Material DissolveTree;  // DissolveTree.fx, + slot1 LinearClamp, RequiresPerDrawParams
    public static Material DepthStamp;    // cutout fx, ColorWriteChannels.None, DepthStencil Default
}
```

Note on `HdrAlpha`/`HdrAdditive`: today one effect instance has its `AlphaMode`
uniform flipped between passes. Under the new model these become **two materials
over two `Effect.Clone()`s** with the uniform set once at load — a uniform that
selects a sub-pass *is* a material distinction. No per-frame parameter sets at all.
(Possible later merge: under the premultiplied convention, additive = vertex
alpha 0 in an AlphaBlend batch — the two HDR materials could collapse into one.
That interacts with the HdrColor vertex encoding, so it's a follow-up experiment,
not part of the migration.)

### 4.2 Per-draw shader parameters

Four tiers, cheapest first — and call sites should be pushed down this list:

0. **Not per-draw at all** (free): if the value can be *derived in the shader*
   from position + global uniforms, it isn't a per-draw param. The research
   sharpened this: a ground-fog factor is `smoothstep(FogLine, ...)` of the
   transformed vertex position against two frame uniforms — every sprite in the
   batch fogs itself with zero per-sprite data. Ask "can the shader compute
   this?" before plumbing anything.
1. **Vertex channels** (free — no batch break): tint, alpha, HDR intensity via
   `Color`, like `HdrColor.ToHdrVertex` already does. First tool to reach for
   when the value genuinely varies per sprite.
2. **Material variant** (free — batches with its siblings): a uniform that takes
   one of a few values (AlphaMode, DebugMode) → N cloned effects / materials.
3. **`SetParams` callback on the item** (breaks the batch — same cost the
   hand-rolled `End/Begin` dance pays today, but centralized and restore-proof):

```csharp
public delegate void MaterialParamSetter(XnaEffect effect); // runs just before Begin
```

Wading and dissolve are tier 3 and stay tier 3 — they were batch breaks before,
they're batch breaks after, but now the *pipeline* owns the flush and the resume
is computed, not guessed. (Closure allocation: a handful of wading units per frame
is noise. If profiling ever disagrees, the escape hatch is a pooled param-block
struct list; don't build it speculatively.)

### 4.3 SortKey

One packed `ulong`, ordered so that plain integer comparison gives the right
draw order:

```csharp
public readonly struct SortKey : IComparable<SortKey>
{
    public readonly ulong Packed;
    // bits 63..56  Layer     (byte)  — coarse band; replaces block order in Draw()
    // bits 55..32  Depth     (24b)   — camera-relative quantized world Y; 0 for
    //                                  layers that don't depth-sort
    // bits 31..16  MaterialId(16b)   — groups same-state items *at equal depth*
    // bits 15..0   Sequence  (16b)   — per-pass submit counter; makes the unstable
    //                                  sort deterministic (replaces DepthItem's
    //                                  Type/Index/SubIndex tiebreaker)
    public int CompareTo(SortKey o) => Packed.CompareTo(o.Packed);
}
```

- **Layer** is the new home of "which block was I in": `Shadows`, `Corpses`,
  `YSort`, `Projectiles`, `EffectsHdrAlpha`, … (see §8 for the full list).
- **Depth** encodes the isometric Y-sort: `Quantize24(worldY - cameraY)` over a
  ±2048-unit window (same camera-relative idea as `FogDepthForY`, which fixed the
  absolute-mapping saturation bug — the design keeps that lesson). 24 bits over
  4096 units = 1/4096-unit precision, far finer than the float sort resolves today.
- **Material above Sequence** means items at *identical* depth cluster by material
  (50 grass tufts at one quantized Y batch together) but never reorder across
  different depths — correctness first, batching only where it's free.
- Layers that are order-of-submission (HUD-ish world overlays like damage numbers)
  just leave Depth = 0 and ride on Sequence.

### 4.4 RenderItem

```csharp
public struct RenderItem      // ~64–80 bytes, lives in a pooled per-pass List<RenderItem>
{
    public SortKey Key;
    public Material Material;

    // Sprite payload (the common case — mirrors SpriteBatch.Draw args, screen space):
    public Texture2D? Texture;
    public Rectangle? Source;
    public Vector2 Position;      // already projected via WorldToScreen at submit time
    public Vector2 Origin;
    public float Scale;           // uniform scale (current code never needs non-uniform on sprites)
    public float Rotation;
    public Color Color;
    public SpriteEffects Flip;

    // Escape hatches:
    public MaterialParamSetter? SetParams;     // tier-3 per-draw uniforms (§4.2)
    public Action<SpriteScope>? Callback;      // composite draw (unit + its garnish)
}
```

**Why screen-space positions:** submission happens inside the same frame with the
same (snapped) camera; projecting at submit keeps `RenderItem` free of camera
knowledge and matches every existing draw helper. The pipeline never re-projects.

**Why `Callback` is first-class, not a migration wart:** `DrawSingleUnit` is a
composite of ordered sub-draws sharing one depth slot. Exploding it into 30 items
per unit would bloat the sort for zero benefit — the sub-draws are *intentionally*
call-ordered. A callback item = "one sortable slot whose contents are imperative."
This is exactly what the `_depthItems` switch already expresses. The callback
receives a `SpriteScope`, not the raw batch:

```csharp
/// <summary>Handed to callbacks. Wraps the open batch; the ONLY way a callback may
/// deviate from the pass material is WithMaterial, which the executor implements as
/// flush → sub-batch → restore-current-material. Restore state is computed, never
/// guessed — this retires EffectBatch.BeginEffect and its bug class.</summary>
public readonly ref struct SpriteScope
{
    public SpriteBatch Batch { get; }                     // Draw/DrawString straight in
    public void WithMaterial(Material m, MaterialParamSetter? p, Action<SpriteBatch> draw);
}
```

So a wading unit inside a callback does
`scope.WithMaterial(Materials.Wading, SetWadingParams, DrawTheSprite)` — the same
GPU work as today, with the restore handled by the executor that *knows* what's open.

---

## 5. Submission API

One per-frame façade, `RenderQueue` (owned by `GameRenderer`, reset each frame),
routing to per-pass item lists. Layer defaults its material so common call sites
stay one-liners:

```csharp
var rq = _renderQueue;

// Plain sprite into a fixed layer (material = layer default, Materials.Scene):
rq.Sprite(Layer.Shadows, tex, screenPos, src, color, origin, scale, rot, flip);

// Y-sorted world item (the _depthItems replacement):
rq.YSprite(worldY, tex, screenPos, src, color, origin, scale, rot, flip);
rq.YSprite(worldY, Materials.HdrAdditive, ...);          // explicit material
rq.YCallback(worldY, scope => DrawSingleUnit(scope, i)); // composite slot

// Anything else:
rq.Submit(Layer.EffectsHdrAdditive, depth: 0, material, in spriteArgs);
rq.Callback(Layer.DamageNumbers, scope => DrawDamageNumbers(scope));
```

Text helpers (`rq.Text(...)` / inside callbacks `scope.Batch.DrawString`) round
positions to integer pixels **centrally**, enforcing the CLAUDE.md PointClamp rule
in one place instead of at ~40 call sites.

`RenderContext` (passed to passes and available to submitters) carries the shared
frame state that's currently threaded through `SetContext(...)` calls and `_g._*`
fields: `GraphicsDevice`, `SpriteBatch`, `Camera25D` (snapped), `Renderer`
(projection), ambient color, game time, and **one** computed `VisibleWorldBounds`
(the view-cull rectangle currently duplicated in four places).

---

## 6. Execution: the sort + batch loop

The heart of the system, ~50 lines, lives in `SpriteQueuePass.Execute`:

```
Execute(ctx):
    items.Sort()                                  # List<RenderItem>.Sort on SortKey; pooled list
    open = null                                   # currently-open material, null = no batch

    for item in items:
        needOwnBatch = item.SetParams != null or item.Material.RequiresPerDrawParams
        if item.Material != open or needOwnBatch:
            if open != null: batch.End()
            item.SetParams?.Invoke(item.Material.Effect)      # uniforms upload at Begin/Apply
            BeginWith(item.Material)                          # blend, sampler, depthstencil,
            open = needOwnBatch ? null : item.Material        #   effect, ExtraSamplerSlots
                                                              # (needOwnBatch → force break after)
        if item.Callback != null:
            item.Callback(new SpriteScope(batch, open, this)) # WithMaterial reopens `open` after
        else:
            batch.Draw(item.Texture, item.Position, item.Source, item.Color,
                       item.Rotation, item.Origin, item.Scale, item.Flip, 0f)

    if open != null: batch.End()
    stats.Record(items.Count, batchesOpened)      # perf HUD: items vs batches per pass
    items.Clear()
```

Properties worth stating:

- **Batch count is an output, not a hand-managed invariant.** Consecutive
  same-material items merge; a material change costs exactly one `End/Begin`.
  Today's frame in the same order produces the *same or fewer* breaks — nothing
  gets slower, and the per-pass `items vs batches` stat makes regressions visible.
- `SpriteSortMode` is always `Deferred`. Sorting is ours; MonoGame just buffers.
- A tier-3 item (SetParams) is its own batch — identical GPU cost to today's
  hand-rolled dance, minus the restore guessing. *(Optimization noted, not built:
  a run of consecutive same-material tier-3 items could share one
  `Immediate`-mode batch with params set between draws — relevant only if
  many wading units crowd one screen.)*
- Sort cost: `List.Sort` on a struct with a single `ulong` compare, few hundred to
  low thousands of items — microseconds. No radix sort, no parallelism. (Bit-packing
  the key is cheap to do up front and makes the comparer trivial; it is the one
  "optimization" worth taking on day one because it *simplifies* the code.)

---

## 7. Pass type hierarchy

Four pass kinds — deliberately not one flat abstraction, because the KINDS aren't
uniform:

```csharp
public abstract class RenderPass
{
    public string Name;          // shows up in perf HUD and pass-toggle dev commands
    public bool Enabled = true;  // scenario/dev toggles (WantsGround, _devShotNoUi, F-key overlays)
    public abstract void Execute(RenderContext ctx);
}

/// 1. State-batched sprite queue — the workhorse (§6). World sprites, HDR effects.
public sealed class SpriteQueuePass : RenderPass
{
    internal List<RenderItem> Items;             // pooled
    public Material DefaultMaterialFor(Layer l); // layer→material defaults
    // Submit routed here by RenderQueue via Layer→pass mapping.
}

/// 2. Custom draw — single-draw custom-shader or genuinely imperative work.
///    Ground shader (15 uniforms, 8 textures, one fullscreen quad), god rays,
///    bloom-debug shapes, and — permanently — the HUD/editor UI.
public sealed class CustomPass : RenderPass
{
    private readonly Action<RenderContext> _draw;
}

/// 3. Depth-utility pass — opens ONE fixed material over a submitted list
///    (the fog-occluder stamp: DepthStamp material, layerDepth from FogDepthForY).
///    Really a SpriteQueuePass with a forced material; kept nominal for clarity.
public sealed class DepthStampPass : RenderPass { ... }

/// 4. Target phases — the render-target structure, one level ABOVE passes.
public sealed class RenderPhase
{
    public string Name;
    public Func<RenderContext, RenderTarget2D?> Target;  // null = backbuffer
    public Action<RenderContext>? OnBegin;               // e.g. Clear, camera snap
    public List<RenderPass> Passes;
    public Action<RenderContext>? OnEnd;                 // e.g. bloom EndScene composite
}
```

The **frame is a `List<RenderPhase>`, built once at load** — this is the "fixed
sequence becomes data" deliverable. RT swaps only happen at phase boundaries
(plus inside self-contained phase bodies like fog-of-war prep and bloom's
internal mip chain, which keep their existing multi-RT logic behind their pass
boundary — no need to model bloom's 20 internal Begin/Ends as passes).

---

## 8. The target frame as data

```
Phase "Prep"      target: (own RTs, then releases)
    FogOfWarUpdatePass          — existing FogOfWar.Update, verbatim (RT jobs before scene binds)

Phase "Scene"     target: bloom scene RT (HalfVector4 + Depth24Stencil8) or backbuffer
                  OnBegin: clear, snap camera to pixel grid
    GroundPass        (CustomPass)      — DrawGroundShader / tile fallback; Opaque, PointClamp
    WorldPass         (SpriteQueuePass) — layers, in band order:
        Roads             (callback item today; sprite items later)
        Traps             (ground-layer env objects)
        GlyphsGround      (magic glyphs + build bars)
        Walls
        Shadows           (ShadowRenderer feeds items instead of owning a batch)
        HoverGroundMarkers
        Corpses
        YSort             ← depth-sorted: units, env objects, poison puffs, grass
                            tufts, death-fog puffs, reanim dust — the generalized
                            _depthItems; units are Callback items; wading/dissolve
                            via scope.WithMaterial
        Projectiles, SoulOrbs, Rope
        Rain
    FogOccluderPass   (DepthStampPass)  — unit silhouettes → depth buffer (perf-gated)
    HdrEffectsPass    (SpriteQueuePass) — layers:
        EffectsHdrAlpha    (Materials.HdrAlpha       — clouds, smoke)
        EffectsHdrAdditive (Materials.HdrAdditive    — fireballs, effects, reanim
                            light/clouds; depth-tested variant when DepthSortedFog;
                            lightning as a Callback item)
        AdditiveShapes     (Materials.AdditiveShapes — energy columns, bloom-debug)
    GodRayPass        (CustomPass)      — owns its shader+batch as today
    TopWorldPass      (SpriteQueuePass) — Foragables, DamageNumbers (Materials.Scene)

Phase "Post"      target: backbuffer
    OnBegin = bloom EndScene (mip chain + composite — stays a black box)
    FogOfWarOverlayPass (CustomPass)

Phase "Overlay"   target: backbuffer     (unsnapped camera restored)
    DebugOverlayPasses… (CustomPass each, Enabled = the F-key flags)
    HudPass           (CustomPass)      — weather fog, HUD, panels, editors, perf text
```

**Future systems slot in without surgery** (the extensibility test):

- *Local lighting*: new `Phase "Lights"` (additive accumulation into a light RT;
  torch/spell submissions via a `LightsPass` sprite queue) + one multiply-composite
  `CustomPass` at the head of `Post`, before bloom reads the scene. No existing pass
  moves.
- *Ground fog volume*: two passes appended inside `Scene` (§10).
- *Richer weather*: more layers in `WorldPass` (wind-bent grass is a material
  variant with a vertex-warp shader) or a new sprite pass before `TopWorld`.

### What stays imperative on purpose

The HUD/editor tree (immediate-mode UI, `UIShaders` with its injected-restore,
widget panels) is **call-order by nature** — retained sorting would add
ceremony and zero value. It stays a `CustomPass` forever. Same for the main-menu
early-outs and the bloom/fog-of-war internals.

---

## 9. Mapping to MonoGame's real constraints

| Constraint | Where it lives in the design |
|---|---|
| Premultiplied-alpha textures (TextureUtil.LoadPremultiplied) | `Materials.Scene/Hud` use `BlendState.AlphaBlend` (= One/InvSrcAlpha, the premult pair). The convention is now written in exactly one file. |
| Opaque vs AlphaBlend vs Additive | A `Material` field. "Additive is a first-class citizen" = `Materials.HdrAdditive` is just another material an item can carry — no dedicated code block. |
| PointClamp vs LinearClamp | A `Material` field. The shipped wrong-sampler-restore bug becomes unrepresentable: restores are computed from the open material. |
| Sampler slots ≥ 1 inherit stale state | `Material.ExtraSamplerSlots` applied at every batch open (dissolve s1, bloom s1 today hand-fix this). |
| Effect uniforms upload at Apply (Begin) | Tier model §4.2: vertex channels → material variants (Effect.Clone) → `SetParams` immediately before `Begin`. Ground's 15 uniforms run in its `CustomPass` exactly as now. |
| `SpriteSortMode` semantics | Always `Deferred`; ordering is the sort key's job. `Immediate` survives only inside bloom's black box (fullscreen quads, correct there). |
| Mid-frame RT swaps | Phase boundaries own `SetRenderTarget`. Passes cannot switch targets — fog-of-war prep and bloom internals are phase-scoped bodies. Scene RT's depth buffer usable by any Scene-phase pass (occluder stamp → depth-tested fog). |
| Depth buffer only on scene RT | `DepthStencilState` is a material field; materials that need depth (`DepthStamp`, `HdrAdditiveDepthTested`) are only submitted to Scene-phase passes. Debug-assert in submit if needed. |
| Text at sub-pixel = artifacts under PointClamp | `rq.Text`/scope helpers round centrally (§5). |
| GC pressure (60 fps, per-frame lists) | Struct items in pooled `List<RenderItem>`s (the `_depthItems` pattern, generalized); materials/passes allocated once at load; closures only on tier-3 items and long-lived callbacks cached where hot. |

---

## 10. The KINDS, expressed (the acceptance test)

1. **Opaque ground, custom shader** → `GroundPass : CustomPass` in Scene phase.
   The pass model explicitly does *not* force this into the sprite queue — one
   draw, own uniforms, own blend/sampler, done. ✔
2. **Transparent objects that Y-sort against units** → submit to `Layer.YSort`
   with `worldY`; a new translucent thing is one `rq.YSprite(...)` call — no switch
   to extend, no enum to grow (the `DepthItemType` enum dies; its cases become
   submission sites). ✔
3. **Additive/emissive HDR** → `Materials.HdrAlpha/HdrAdditive` items in the
   HdrEffects pass, inside the Scene phase whose target *is* the HalfVector4 RT;
   bloom composite is the Post phase's OnBegin. Intensity stays in vertex color
   (tier 1 — no batch cost). Emissive-vs-ambient interplay is untouched: ambient
   tints diffuse items at submit, HDR items skip it, exactly as now. ✔
4. **Ground fog units rise out of** (headline) — layered recipe, all pieces
   either proven in-repo or documented practice, all expressible as data:
   **RECOMMENDED CORE — volumetric interleave (depth-stamped wisps):**
   `FogOccluderPass` (exists today, `Performance.DepthSortedFog`) stamps unit
   silhouettes into the depth buffer; drifting ground-hugging fog *puffs*
   depth-test against them (`HdrAdditiveDepthTested`-style material). Per-PIXEL
   interaction: a wisp covers legs where it drifts in front, is occluded by the
   torso behind, no whole-quad popping — the only variant that produces genuine
   "in the volume" behavior. O(1) batches regardless of units-in-fog count; the
   stamp pass is shared with reanim fog; the perf knob is puff overdraw
   (tunable). This is the shipped reanim-fog mechanism, generalized.

   Supporting layers, cheapest first:
   - **Back blanket**: a soft fog strip layer just behind `YSort` for continuous
     coverage without overdraw — no unit interaction needed, it's behind them.
   - **Tier-0 haze on environment sprites**: `fog = smoothstep(...)` from
     position + global uniforms. *Caveat found in review:* in this projection,
     screen Y conflates ground Y and elevation (torso pixel at Y=100 ≈ ground
     pixel at Y≈101.5), so position-only shading CANNOT compute a correct
     per-unit fog line — that needs the sprite's foot Y (per-sprite data). Use
     tier 0 only as approximate ambient haze, not as the unit mechanism.
   - **Crisp cut as garnish, not core**: the `FogPokeThrough` re-draw (wading
     shader, waterline → fog line) is a tier-3 batch break *per unit in fog* —
     fine for the PLAYER only (one break), hostile to hordes (40 units ≈ 80
     transitions), and a hard cut reads as liquid/dry-ice rather than fog.
   Plain Y-sorted fog bands as the *front* interaction layer are REJECTED:
   whole-quad Y-popping when units cross a puff's Y is exactly the "it's an
   overlay of sprites" tell. ✔
5. **Occlusion fade** → a small `OcclusionFadeSystem` that runs at submit time
   (not a pass): the Y-sort items are *data before drawing*, so it can find env
   items whose screen AABB overlaps the player's and whose sort key says "in
   front," and modulate their `Color` alpha (per-object fade state lerped across
   frames). That's the shipped-2D-games default (plain alpha fade); if
   transparency stacking looks bad on dense trees, the documented upgrade is a
   **dither/screen-door cutout** material variant (Bayer-threshold `clip()`,
   binary pixels, no blend-order artifacts —
   [Ilett](https://danielilett.com/2020-04-19-tut5-5-urp-dither-transparency/)).
   Enabled precisely because submissions are inspectable data — impossible in
   the immediate model. ✔
6. **Screen-space overlays** → the Phase split *is* the separation: Scene (bloomed,
   graded, snapped camera) / Post (composites) / Overlay (fog-of-war done in Post,
   weather fog + HUD in Overlay, PointClamp, unsnapped). ✔
7. **(Future) local lighting & weather** → §8: a Lights phase + composite pass
   slot in; wind-bent grass is a material variant. No monolith rewrite. ✔

---

## 11. Incremental migration (no big bang)

Each step compiles, runs, and screenshots identically (drive-game screenshot
diffs per step; the scenario suite is the regression net). Old and new coexist
the whole way — a `CustomPass` wrapping an old block is a valid pass.

- **Step 0 — the frame becomes data (mechanical, zero behavior change).**
  Introduce `RenderPass`/`RenderPhase`/`RenderContext`. Slice `Draw()`'s existing
  blocks into `CustomPass`es wrapping the current code verbatim; `Draw()` becomes
  "execute the phase list." Bloom Begin/EndScene and fog-of-war Update become
  phase plumbing. *Value: pass toggles, per-pass timing, and the structure —
  before any behavioral risk.*
- **Step 1 — Materials replace EffectBatch's constants.** Add `Material` +
  `Materials`; `EffectBatch.SceneBlend/HudBlend` etc. become
  `Materials.Scene/Hud` (EffectBatch forwards to them during the transition —
  it's already the single source, so this is a rename-and-grow, not a replace).
  `BeginEffect/EndEffectResume*` remain for unconverted sites.
- **Step 2 — convert `_depthItems` → `SpriteQueuePass` (the beachhead, as the
  proposal guessed).** `DepthItem` becomes `RenderItem`; the six enum cases become
  submission calls (units/env as Callback items, puffs/tufts as plain sprites);
  the switch dies. Wading + dissolve become `scope.WithMaterial(...)` — first
  real payoff: the two remaining `BeginEffect` sites inside the Y-sort disappear.
- **Step 3 — fold the flat scene blocks into `WorldPass` layers.** Roads, traps,
  glyphs, walls, shadows, corpses, projectiles, rope, rain: each is "same batch
  state, earlier band" — pure `Layer` assignments. ShadowRenderer/GlyphRenderer
  stop owning batches and submit instead (their `SetContext` threading collapses
  into `RenderContext`).
- **Step 4 — HDR effects become materials.** Clone HdrSprite.fx → `HdrAlpha`/
  `HdrAdditive`; `Effect.BlendMode` (the field that already exists!) maps to the
  material at submit; `DrawEffectsFiltered(0/1)` and the AlphaMode flip die.
  Reanim sorted-particles + lightning become items/callbacks in the same pass;
  the mid-pass End/DrawSortedParticles/Begin dance dies.
- **Step 5 — overlays & polish.** Debug overlays → `Enabled`-gated CustomPasses;
  damage numbers/foragables → TopWorldPass; delete `EffectBatch.BeginEffect`
  (no remaining callers), leaving `Materials` as the lone state authority.
- **Step 6 — new capabilities** (only now): ground-fog volume passes (§10.4),
  occlusion fade (§10.5), then the Lights phase when wanted.

Rollback story: every step is a small PR; the pass list makes "render the old way
for block X" a one-line pass swap during any step.

---

## 12. Deliberately simpler than Unity (over-reach guardrails)

Dropped, with reasons — single viewport, one camera, 2D, sprite counts in the
hundreds:

- **No SRP/render-graph resource tracking** — three fixed phases and ~four RTs;
  a `List<RenderPhase>` built at load is the whole graph.
- **No command buffers / GPU abstraction under SpriteBatch** — SpriteBatch *is*
  the batcher; we only decide when it opens/closes. (Nez's custom Batcher exists
  to avoid Begin/End overhead at scales we don't have.)
- **No material-major sorting for opaque** — the only opaque draw is the ground
  quad. Transparent depth-major sorting is the whole game; Ericson's opaque
  half of the scheme is dead weight here.
- **No shader keywords/variants/permutation compiler** — `Effect.Clone()` per
  fixed variant; we have ~10 shaders, not 10,000.
- **No per-frame culling system in the pipeline** — submit sites keep culling
  (they have the domain knowledge); the pipeline just provides the shared bounds.
- **No multi-camera, no layers-as-collision-masks, no MaterialPropertyBlock
  object** — a delegate is enough at our item counts.
- **No multithreaded record/submit** — sort + walk is microseconds; the sim is
  the frame cost, not the draw walk.
- **Don't retro-fit the UI/editors** into items — call-order immediate-mode UI is
  the right tool there (see §8).
- **Don't chase draw-call counts** — ~20–40 batches/frame is nothing; the win is
  *architectural* (state ownership, expressiveness), with batching as a free
  side effect. The per-pass stats line exists to keep us honest, not to golf.
- **No custom Batcher / vertex format — yet.** Stock SpriteBatch under external
  sort is sufficient. The one thing it can't do is a per-sprite *float* channel
  beyond Color (e.g. a per-sprite fog cutoff that tier 0 can't derive). If that
  day comes, the documented escalation is porting the Nez/FNA Batcher and adding
  one float to the vertex declaration — a contained swap behind
  `SpriteQueuePass`, invisible to submit sites. Named here so nobody reaches for
  `Immediate` mode instead.

---

## 13. Executive-judgement additions (gaps in the original ask)

1. **Determinism guarantee**: the 16-bit sequence field bakes submission order
   into the key, so the unstable `List.Sort` can never flicker equal-Y items —
   the `DepthItem` tiebreaker lesson, kept by construction.
2. **Perf HUD line per pass** (`items / batches / ms`): the current perf readout
   gets a render-pipeline row; regressions in batch count become visible the
   frame they're introduced.
3. **Pass toggles as dev commands** (`pass off WorldPass.Rain` via
   ExecuteDevCommand): free once passes are named data — replaces the ad-hoc
   `_devShotNoGround`/`WantsUI`-style flags over time and makes screenshot
   bisection of visual bugs trivial.
4. **Centralized text rounding** (§5) — the PointClamp/sub-pixel rule enforced in
   one helper instead of by convention.
5. **`VisibleWorldBounds` on RenderContext** — the view-cull rect is computed four
   times today with slightly different margins; one computation, per-call margins.
6. **The `Effect.BlendMode` int and `HdrColor` vertex encoding are kept and
   promoted** — they're the two places the codebase already discovered the right
   pattern (declared state; batching-free per-sprite params). The design's tier
   model just names what they were groping toward.
7. **Camera snap ownership**: Scene phase `OnBegin` snaps, Overlay phase restores —
   the paired mutation currently spread across `Draw()` becomes phase structure.

## 14. Open questions for the user

- **Naming**: `RenderQueue`/`Layer`/`Material` vs something game-flavored — any preference?
- ~~Where the ground-fog look lands~~ **Decided 2026-07-03**: depth-stamped puff
  volume is the core (per-pixel interleave, proven in-repo, O(1) batches), back
  blanket behind YSort for coverage, poke-through cut reserved as player-only
  garnish. See §10.4.
- Migration Step 0 could land in an afternoon and is pure win — want it split
  out as its own task when implementation starts?
