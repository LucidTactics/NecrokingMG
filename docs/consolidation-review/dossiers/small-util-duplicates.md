# Dossier: Small math/util exact duplicates

Concept review for the semantic-duplication architecture pass. Every claim from the
labeling batch was read in source. Verdict summary: **4 CONSOLIDATE, 7 KEEP_SEPARATE.**
The labeler over-matched on several "exact duplicate" claims — two pairs differ
semantically on purpose, one is a layered wrapper, one is a documented coordinate-space
distinction. One genuine latent **bug** was found in the angle-math family
(HordeSystem.LerpAngle long-way rotation).

Canonical math home that already exists: `Necroking/Core/Vec2.cs:75-79` —
`public static class MathUtil { Lerp, Clamp }` (namespace `Necroking.Core`, referenced
by WeatherRenderer, PoisonCloudSystem, Locomotion, Camera25D). All scalar-math
consolidations below land there.

---

## Finding 1 — Scalar `Lerp(a,b,t)` reimplemented 4x — CONSOLIDATE (low)

Canonical: `MathUtil.Lerp` at `Necroking/Core/Vec2.cs:77`
(`a + (b - a) * t`), already used in 2 files (5 call sites).

Byte-identical private copies:
- `Necroking/Game/DayNightSystem.cs:216` — `private static float Lerp(float a, float b, float t) => a + (b - a) * t;`
- `Necroking/Game/HordeSystem.cs:101` — same
- `Necroking/Editor/ColorHarmonizer.cs:44` — same (note: `LerpHue` at :46 is hue-wrapping, NOT a duplicate — leave it)
- `Necroking/Scenario/Scenarios/AggressionRadiusScenario.cs:60` — local static in a scenario (optional; test code, lowest priority)

**Migration:** delete each private copy, replace call sites with `MathUtil.Lerp`
(namespace `Necroking.Core` already visible everywhere — Vec2 is used in all these files).
**Effort S, risk ~zero** (identical expression, no semantic delta).

---

## Finding 2 — Signed-shortest-angle math, 4 implementations, 2 conventions, 1 real bug — CONSOLIDATE (medium)

Same intent (shortest signed angular delta / angle lerp), four bodies:

| Impl | File:line | Units | Method | Correct? |
|---|---|---|---|---|
| `FacingUtil.AngleDiff(target, current)` | `Necroking/Movement/Locomotion.cs:550` | deg | while-loop wrap, (-180,180] | yes |
| `AnimController.SignedAngleDelta(a, b)` | `Necroking/Render/AnimController.cs:898` | deg | `%360` + branch, (-180,180] | yes |
| `RemotePlayer.LerpAngleDeg(a, b, f)` | `Necroking/Net/RemotePlayer.cs:92` | deg | `((b-a)%360+540)%360-180` | yes |
| `HordeSystem.LerpAngle(from, to, t)` | `Necroking/Game/HordeSystem.cs:571` | **rad** | `((to-from+PI)%(2PI))-PI` | **NO — bug** |

**The bug:** C# `%` preserves the dividend's sign. In `HordeSystem.LerpAngle`, when
`to - from < -π` (reachable: `_circleFacing` accumulates un-wrapped at
`HordeSystem.cs:294`, `_movementAngle` is atan2-range), `to-from+π` is negative, the
modulo stays negative, and the result is the raw long-way delta instead of the wrapped
short-way one — the horde circle facing rotates the long way around. Example:
`to-from = -4 rad` → returns `-4` instead of `+2.283`. The Net version handles this
correctly via the `+540` bias; the Horde version does not. This is exactly the
divergence risk this concept review exists to catch.

**Canonical design** (in `MathUtil`, `Necroking/Core/Vec2.cs`):
```csharp
/// Shortest signed delta from `from` to `to`, degrees, in (-180, 180].
public static float AngleDeltaDeg(float from, float to)
    => ((to - from) % 360f + 540f) % 360f - 180f;
public static float LerpAngleDeg(float from, float to, float t)
    => from + AngleDeltaDeg(from, to) * t;
public static float AngleDeltaRad(float from, float to)
    => ((to - from) % MathF.Tau + 3f * MathF.PI) % MathF.Tau - MathF.PI;
public static float LerpAngleRad(float from, float to, float t)
    => from + AngleDeltaRad(from, to) * t;
```
(Watch the argument-order/sign convention: `FacingUtil.AngleDiff(target, current)` =
`AngleDeltaDeg(current, target)`; `SignedAngleDelta(a, b)` = `AngleDeltaDeg(b, a)`.
Edge behavior at exactly ±180 differs by at most which endpoint of the closed interval
is returned — irrelevant to all four call sites, which feed lerp/abs/clamp.)

**Call sites to migrate:**
- `FacingUtil.AngleDiff` — keep the public method (it's documented in
  `docs/locate-behavior/movement.md:121` as the shared rotation helper) but make its
  body delegate to `MathUtil.AngleDeltaDeg`. Callers: `Locomotion.cs:571`,
  `Simulation.cs:2020`.
- `AnimController.SignedAngleDelta` (`AnimController.cs:871`) — replace body or inline.
- `HordeSystem.LerpAngle` (`HordeSystem.cs:294`) — replace with `MathUtil.LerpAngleRad`
  (**fixes the long-way bug**; the behavior change is the fix).
- `Necroking/Net/RemotePlayer.cs:92` — **DO NOT TOUCH.** Net/ is do-not-touch outside
  explicit networking tasks, and its README forbids game-system references from that
  folder (referencing even Core from Net is a judgment call the Net owner should make).
  Its copy is correct; leave it as the one sanctioned exception, optionally with a
  comment pointing at MathUtil.

**Effort S–M, risk low** (pure functions; HordeSystem change is a deliberate bugfix —
verify horde circle rotation visually via drive-game after).

---

## Finding 3 — `Quadtree.QueryRadius` vs `QueryRadiusByFaction` copied traversal — CONSOLIDATE (medium)

`Necroking/Spatial/Quadtree.cs:91-129` and `:136-176`. The 35-line stack-based
traversal is byte-identical except QueryRadiusByFaction adds one leaf-level line
(`if ((e.FactionBit & mb) == 0) continue;`) and the `mask == FactionMask.None` early-out.
Any future traversal change (stack depth 64 cap, bounds test, entry layout) must be
mirrored by hand — classic drift surface.

**Canonical design:** keep `QueryRadiusByFaction` as the single traversal; make
`QueryRadius` a one-line delegate:
```csharp
public int QueryRadius(Vec2 center, float radius, List<uint> results)
    => QueryRadiusByFaction(center, radius, FactionMask.All, results);
```
`FactionMask.All` exists (`Necroking/Data/Enums.cs`, used by `FactionMaskExt.AllExcept`).
Safety check done: the only `Build` overload (`Quadtree.cs:67`) always takes factions,
so `FactionBit` is always populated — the "built without factions returns nothing"
caveat in the :131 doc comment cannot occur for the unfiltered path. The other
`.QueryRadius` call sites in Simulation (`_envIndex.QueryRadius`) belong to
`EnvSpatialIndex`, a different class — out of scope.

Perf note: adds one byte-AND per leaf entry to unit queries at `Simulation.cs:1368`,
`LightningSystem.cs:232`, `PhysicsSystem.cs:274`, `TrampleSystem.cs:579` — noise next
to the LengthSq test. If a perf pass ever disputes that, revert; keep the perf.log
baseline handy.

**Call sites:** none change — only the Quadtree internals. **Effort S, risk low.**

---

## Finding 4 — Editor `IndexOf(IReadOnlyList<string>, string)` x4 — CONSOLIDATE (low)

Byte-identical private helper (linear scan, -1 on miss) in:
- `Necroking/Editor/ItemEditorWindow.cs:617`
- `Necroking/Editor/SpellEditorWindow.cs:1763`
- `Necroking/Editor/UnitEditorWindow.cs:3812`
- `Necroking/Editor/ReflectionPropertyRenderer.cs:565` (named `IndexOfId`)

Labeler nit: this is *not* "reimplementing List.IndexOf" — `IReadOnlyList<T>` has no
`IndexOf`, and the LINQ alternative allocates. The helper is legitimate; having four
copies is not. (`SkillBookData.cs:212` `IndexOf` is a dictionary lookup — different
thing, not part of this finding.)

**Canonical home:** one `protected static int IndexOf(IReadOnlyList<string>, string)`
on `EditorBase` (the shared editor base class per CLAUDE.md's DrawText note), or a tiny
`EditorUtil` if ReflectionPropertyRenderer doesn't derive from EditorBase — check its
base before choosing. **Effort S, risk zero.**

---

## Finding 5 — `WeatherRenderer.Init` / `Resize` identical bodies — CONSOLIDATE (low)

`Necroking/Render/WeatherRenderer.cs:32-42` — both assign `_screenW/_screenH` and
nothing else. Make `Init` delegate to `Resize` (or delete `Init` and rename call sites).
Cost of leaving it: someone adds buffer reallocation to `Resize` and forgets `Init`
(or vice versa). **Effort S, risk zero.**

---

## Finding 6 — `AnimController.IsLocomotionState` vs `Locomotion.IsLocoClass` — KEEP_SEPARATE

Evidence claimed "duplicates exactly." **False.** They differ on `Idle`, deliberately:
- `Locomotion.cs:320` `IsLocoClass`: `Idle || Walk || Jog || Run` — gait *selection*
  treats Idle as a locomotion-channel state the gait picker may steer out of.
- `AnimController.cs:529` `IsLocomotionState`: `Walk || Jog || Run` — foot-phase
  *carryover*, whose own comment (:545) says "Idle↔Walk … still reset to 0 (a fresh
  standing cycle has no phase to inherit)."

Merging them would either break phase carryover (Idle in) or break gait steering
(Idle out). This is data-level semantic variance, not drift. At most, a one-line
comment cross-referencing the two ("differs from X on Idle — intentional") would
inoculate against a future "cleanup."

## Finding 7 — `UnitArrays.TryGetIndex` vs `UnitUtil.ResolveUnitIndex` — KEEP_SEPARATE

`Necroking/Movement/UnitModel.cs:580` and `:624`. Not duplicates — layered:
`ResolveUnitIndex` **calls** `TryGetIndex` and adds the `InvalidUnit` sentinel check +
`Alive` filter. The codebase uses them with distinct, consistent intent
(TryGetIndex = raw slot lookup incl. dead units, e.g. squad-member iteration;
ResolveUnitIndex = "give me a live target index or -1", ~60 call sites).
This is exactly the single-source-of-truth shape CLAUDE.md asks for.

## Finding 8 — `Camera25D.WorldToScreen` vs `WorldToScreenPx` — KEEP_SEPARATE

`Necroking/Render/Camera25D.cs:33` / `:43` (mirrored by `Renderer.cs:45/:54` pass-throughs).
The bodies share two lines but the third differs semantically: world-unit height
(`worldHeight * Zoom * YRatio`, for physical things — jumps, projectile altitude) vs
literal pixel height (zoom-independent screen effects — rain, lightning, overlays).
Each carries a doc comment stating its use case; `GameRenderer.Corpses.cs:457` even
documents a bug that came from confusing them. The two named entry points ARE the API's
value — collapsing to one with a "convert height yourself" contract reintroduces that
bug class. (Internally one could delegate to the other; saves 2 lines, not worth churn.)

## Finding 9 — Row-major `y * width + x` per grid system — KEEP_SEPARATE

`Necroking/World/TileGrid.cs:70` (`Index`), `Necroking/World/WallSystem.cs:68` (`Idx`),
`Necroking/World/Pathfinder.cs:634` (`SectorIdx`; evidence said GroundSystem — wrong,
GroundSystem has no sector math). Each is a one-expression helper over that system's
*own* private dimensions (tile grid vs wall grid vs sector grid — different sizes,
different data). A shared `Grid2D` abstraction would be a framework around a
one-liner — structural variance, per CLAUDE.md's consolidation-design rule. The
associated bounds-checked getters ("raw array getters family") are each system's own
accessors over its own arrays, same reasoning.

## Finding 10 — `Core/ColorUtils` vs `Core/HdrColor` — KEEP_SEPARATE

`Necroking/Core/ColorUtils.cs` = straight-alpha `Color` conveniences (ByteColor,
Multiply, Scale, Premultiply, Fade) serving the SpriteScope/premultiply pipeline rules.
`Necroking/Core/HdrColor.cs` = an HDR color *type* whose methods (`ToHdrVertex`,
`ToHdrVertexAlpha`) are shader-specific wire encodings for `HdrSprite.fx` (intensity
packed into the alpha byte / RGB — must match the shader's decode). No overlapping
bodies exist; the two even interoperate (`ColorUtils.BytesToHdr/HdrToBytes`). The
labeler pattern-matched on "both convert colors." Merging would couple general color
math to a shader wire format.

## Finding 11 — `SquadSystem.TryGet` vs `Get` — KEEP_SEPARATE

`Necroking/AI/SquadSystem.cs:98,100`. Two single-expression accessors over the same
dictionary — the idiomatic C# Try-pattern / nullable-return pair, each with exactly one
caller (`Game1.Zones.cs:250`, `AIContext.cs:127`). Zero drift surface (neither contains
logic). Deleting one saves one line and churns a caller; not worth it.

---

## Suggested execution order
1. Finding 2 (angle math) — carries the actual bugfix; verify horde rotation via drive-game.
2. Finding 3 (Quadtree) — internal-only, no caller churn.
3. Findings 1, 4, 5 — mechanical, can ride along with any nearby commit.
All are `dotnet build`-safe pure refactors; none touch Net/ wire format, renderer
submit-sort-batch structure, or map JSON.
