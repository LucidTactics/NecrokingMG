# Dossier: VFX + floating-text spawn wrappers

Unit: `vfx-floating-text` — final-judge verdicts for the VFX/floating-text concept.
Repo: c:/Nightfall/NecrokingMG. All line numbers verified against current working tree (2026-07-06).

## Summary
The labeling pass over-matched on two of its four claims: the cast-fail text wrappers and the
Cast/Summon flipbook wrappers are ALREADY consolidated (thin named entry points over one shared
implementation — the desired pattern, not duplication). The real findings are (a) six inline
`new DamageNumber { ... }` spawn sites with four different height-anchor conventions and the
correct head formula living in exactly one of them, and (b) `ProcessTrapFireEvents` in Game1.cs
re-implementing the Strike zap branch of `SpellEffectSystem.ExecuteStrike` minus the
magic-resistance gate and kill credit — genuine gameplay drift. The WadingWakeSystem splash
session bookkeeping is a small honest in-file consolidation; the emitters and the four particle
render systems are structural variance and should stay separate.

---

## Finding 1 — Floating-text (DamageNumber) spawning: 6 inline sites, 4 height conventions — CONSOLIDATE (severity: medium)

`struct DamageNumber` (defined in `Necroking/Game/SpellEffectSystem.cs`) is spawned by raw
`list.Add(new DamageNumber { ... })` at six sites, each choosing its own `Height` anchor:

| Site | Height convention |
|---|---|
| `Necroking/Game1.cs:4246` (`SpawnCastFailText`) | **correct head formula**: `unit.Z + SpriteWorldHeight * SpriteScale / _camera.YRatio` (comment at 4236-4239 explains the YRatio trap; "the old constant 2f landed mid-sprite") |
| `Necroking/Game1.Crafting.cs:108` (`OnForagablePickedUp`) | hardcoded `Height = 2f` |
| `Necroking/Game1.cs:3979` (`ProcessTrapFireEvents`, zap damage) | `SpriteWorldHeight * 0.5f` — no SpriteScale, no YRatio (Game1.cs:3967-3969) |
| `Necroking/Game/SpellEffectSystem.cs:499` (`ExecuteStrike` zap damage) | same `SpriteWorldHeight * 0.5f` recipe (487-489) |
| `Necroking/Game/SpellEffectSystem.cs:568` (sacrifice "+N" souls) | `units[casterIdx].EffectSpawnHeight` (weapon-tip anchor, per docs NOT head height) |
| `Necroking/Game1.cs:3780` (sim damage events) | `dmg.Height` from the sim event (fine — sim owns it) |

`docs/locate-behavior/render.md:402-409` documents this exact "Height trap" (`WorldToScreen`
lifts by `Height * Zoom * YRatio` while sprites draw `SpriteWorldHeight * SpriteScale * Zoom`
tall, no YRatio) — i.e. the project already knows every new caller must rediscover the formula,
and it has already bitten once (the fixed `2f` constant, still live in Game1.Crafting.cs:113,
though for a ground foragable a fixed lift is arguably intended).

**Verdict: CONSOLIDATE.** Canonical home: alongside the struct in
`Necroking/Game/SpellEffectSystem.cs` (or a small `Necroking/Game/FloatingText.cs`):

```csharp
static class FloatingText {
    // the one place the YRatio head formula lives
    public static float HeadHeight(in Unit u, UnitDef? def, float yRatio);
    public static void AddDamage(List<DamageNumber> list, Vec2 pos, int dmg,
                                 float height, bool poison = false, bool fatigue = false);
    public static void AddText(List<DamageNumber> list, Vec2 pos, string text,
                               float height, bool alert = false);
}
```

Call-site categories to migrate: (1) `SpawnCastFailText` keeps its name but delegates its
height math to `HeadHeight`; (2) the two zap-damage sites (Game1.cs:3979,
SpellEffectSystem.cs:499); (3) pickup text (Game1.Crafting.cs:108); (4) sacrifice gain text
(SpellEffectSystem.cs:568); (5) sim damage-event loop (Game1.cs:3780). Update
`docs/locate-behavior/render.md` "Spawn" bullet afterwards.

Effort: **S** (one static class, mechanical call-site swaps). Risk: low (cosmetic placement;
verify visually via `/drive-game`). Severity **medium**: not gameplay-breaking, but the trap is
documented, has already caused one fixed bug, and every convention divergence is invisible
until someone notices text floating from a knee.

---

## Finding 2 — Trap-fire Strike path re-implements ExecuteStrike's zap branch — INVESTIGATE (severity: medium)

`Game1.cs::ProcessTrapFireEvents` (3956-4000) duplicates the Strike logic of
`SpellEffectSystem.ExecuteStrike` (SpellEffectSystem.cs:471-514):

- Zap-target-unit branch: identical targetH recipe + `Lightning.SpawnZap` + `DealDamage` +
  DamageNumber (Game1.cs:3971-3982 vs SpellEffectSystem.cs:480-505). **Divergence already
  present:** the trap path skips the `SpellPenetration.Affects` magic-resistance gate
  (SpellEffectSystem.cs:496) and calls `DealDamage(idx, dmg)` without `casterIdx` (no kill
  credit). Trap zaps therefore ignore MR that caster zaps respect.
- Ground-strike branch: trap `SpawnStrike` call (Game1.cs:3989-3991) omits the GodRay params,
  target filter, and caster-id args the canonical call passes (SpellEffectSystem.cs:509-512).
- The Cloud branch already does it right: Game1.cs:3997 calls
  `GameSystems.SpellEffectSystem.ExecuteCloud(...)` with the comment "Same code path as a
  caster casting the spell" — proving the intended pattern.

**Verdict: INVESTIGATE.** The consolidation itself is easy — extract an origin-based
`ExecuteStrikeFrom(spell, sim, gameData, Vec2 origin, float originHeight, int? casterIdx,
Vec2 target, List<DamageNumber> dmgNums)` in SpellEffectSystem that both `ExecuteStrike`
(origin = caster effect anchor) and the trap path (origin = `evt.TrapPos`, height 0.3f,
casterIdx = null) call. The blocker is a **design decision**: traps have no caster unit, so
someone must decide (a) do trap strikes respect target magic resistance (`SpellPenetration`
needs a caster to compare against today), and (b) do trap kills grant credit/souls. Whatever
the answer, it should be encoded once in SpellEffectSystem, not implied by a fork in Game1.
Effort after decision: **S-M**. Severity **medium** — this is gameplay logic that has already
diverged, and any future Strike feature (MR changes, new visuals) will be added to one path
and missed in the other.

---

## Finding 3 — Cast-fail text wrappers + Cast/Summon flipbook wrappers — KEEP_SEPARATE (severity: low)

Labeler over-match; both groups are already consolidated:

- `SpawnHordeCapText` (Game1.cs:4258) is a one-line `=> SpawnCastFailText(necroIdx, "Horde Full")`;
  `SpawnMissingPathText` (Game1.cs:4263-4268) computes the path-requirement string then calls
  `SpawnCastFailText`. One canonical implementation (`SpawnCastFailText`, Game1.cs:4231),
  wrappers are named entry points — exactly the "single source of truth" pattern.
- `SpawnCastEffect` (Game1.Spells.cs:145-147) and `SpawnSummonEffect` (Game1.Spells.cs:202-204)
  are both one-line delegations to the shared `SpawnFlipbookEffect(FlipbookRef?, Vec2)`
  (Game1.Spells.cs:149-159), differing only in which `SpellDef` field (`CastFlipbook` vs
  `SummonFlipbook`) they pass. `SpawnFlipbookEffect` in turn funnels into
  `EffectManager.SpawnSpellImpact` — one spawn path.

Nothing to do. Collapsing the wrappers would trade readable call sites for nothing.

---

## Finding 4 — WadingWakeSystem entry/exit splash session bookkeeping — CONSOLIDATE (severity: low)

`Necroking/Render/WadingWakeSystem.cs`:

- `TrickleEntrySplash` (1292-1328) and `TrickleExitSplash` (1495-1523) are **line-for-line the
  same control flow** (end-of-session reset, `rate = remainingCount / remainingDuration`,
  accumulator, emit, decrement) over two parallel field sets in `WakeEmitterState`
  (`SplashRemaining*`/`SplashSpawnAccum`/`SplashEntrySpeed`/`BodyHalfAtStart` at 126-133 vs
  `ExitRemaining*`/`ExitSpawnAccum`/`ExitSpeedAtStart`/`ExitBodyHalf` at 139-147).
- `StartEntrySplash` (1261-1285) and `StartExitSplash` (1460-1488) share the same
  count-from-speed + initial-burst-fraction + session-setup skeleton with different constants.

This is data-level duplication (identical control flow, parallel state) — per CLAUDE.md the
shared component should own the mechanics (session scheduling) while the caller owns the data
(counts, durations, which emit routine). Canonical design, all in-file:

```csharp
struct SplashSession { public float RemainingDuration; public int RemainingCount;
                       public float SpawnAccum; public float SpeedAtStart; public Vec2 BodyHalf; }
// WakeEmitterState holds: SplashSession Entry; SplashSession Exit;
static void StartSession(ref SplashSession s, float speed, int baseCount, float countPerSpeed,
                         int maxCount, float burstFraction, float durationSec, Vec2 bodyHalf,
                         out int initialBurst);
static void TrickleSession(ref SplashSession s, float dt, out int toSpawn);
```

Callers then invoke `EmitSplashParticles` / `EmitExitSplashParticles` with the returned counts.
**Do NOT merge the Emit functions**: their physics genuinely differ (entry = omni burst +
forward inheritance + upward velocity with a speed knee, lands at the waterline; exit = zero-up
drips with downward kick + height-biased spawn along the body, lands at ground 0). Same for
`SpawnTrail` (1103) vs `SpawnBowWave` (1171): they share only the ~15-line rate-accumulator
prologue; anchor (rear vs front), spread geometry, drift model, and IsFront layering all
differ — that prologue could ride along in the same cleanup but is not worth abstracting alone.

Effort: **S** (single file, private mechanics, no external callers). Risk: low — verify with a
unit wading in/out of water via `/drive-game`. Severity **low**: cosmetic VFX in one
well-documented file; the cost is only that a scheduling tweak must be applied twice.

---

## Finding 5 — EffectManager / ReanimEffectSystem / PoisonCloudRenderer / GroundFogSystem — KEEP_SEPARATE (severity: low)

The "vfx|particle cluster n=23/8 files" claim conflates systems whose variance is structural,
not data-level:

- `Render/EffectManager.cs` (24-90): a flat list of one-shot flipbook `Effect`s with
  Bezier alpha/scale curves; three tiny typed spawn presets (`SpawnExplosion`, `SpawnDustPuff`)
  over the parameterized `SpawnSpellImpact`. Already the generic "spawn a flipbook VFX" home —
  Finding 3's `SpawnFlipbookEffect` funnels into it.
- `Render/ReanimEffectSystem.cs`: a 3-second, four-layer per-unit composite (unit outline hook +
  additive HDR light + additive cloud puffs + Y-sorted opaque dust contributed to the depth
  list) driven by a `ReanimConfig`; its lifecycle is bound to the corpse→unit standup.
- `Render/PoisonCloudRenderer.cs` / `Render/GroundFogSystem.cs`: renderers over sim-owned /
  ambient field state, not spawn-and-forget particle lists.

Different state machines, different render passes (Y-sort vs additive HDR vs field render),
different ownership (sim vs render). Abstracting a common "particle spawn" over these would be
a framework, not a utility — exactly what CLAUDE.md's consolidation-design section forbids.
The one legitimate shared seam (one-shot flipbook effects) already exists: EffectManager.

---

## Constraint check
- No `Necroking/Net/` involvement.
- All proposals are CPU-side spawn/bookkeeping; no changes to the submit→sort→batch renderer,
  Materials, or SpriteScope usage.
- No map-content implications.
