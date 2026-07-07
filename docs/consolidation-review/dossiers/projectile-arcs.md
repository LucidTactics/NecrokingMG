# Dossier: Projectile ballistic spawn math

Concept judged: duplicated ballistic-arc velocity computation (dir / dist / theta / speed → `Velocity`, `VelocityZ`) across projectile spawn paths.

Verdict summary: the labeler's evidence is **confirmed and understated**. The arc solver exists in **8 in-sim copies** across two files, plus a **9th full reimplementation** (with duplicated physics constants) in the spell editor preview — and the preview copy has **already diverged** from the game on homing trajectories.

---

## Finding 1 — Arc/direct-fire velocity solve duplicated 7x in sim code (CONSOLIDATE, severity: medium)

### Evidence

**A. The lob solve** (identical 6-line block, 3 copies in `Necroking/Game/Projectile.cs`):

- `SpawnArrow` (volley branch), `Necroking/Game/Projectile.cs:146-149` — adds a theta clamp `[10°, 45°]`:
  ```csharp
  float sinTwoTheta = MathF.Min(dist * Gravity / (speed * speed), 1f);
  float theta = MathUtil.Clamp(0.5f * MathF.Asin(sinTwoTheta), 10f * Deg2Rad, 45f * Deg2Rad);
  p.Velocity = dir * speed * MathF.Cos(theta);
  p.VelocityZ = speed * MathF.Sin(theta);
  ```
- `SpawnFireball`, `Necroking/Game/Projectile.cs:173-174, 182-183` — same, no clamp.
- `SpawnPotionLob`, `Necroking/Game/Projectile.cs:199-200, 209-210` — same, no clamp, `speed = MagicSpeed * 0.7f`.

All three also repeat the preamble (`Projectile.cs:124-127, 168-171, 194-197`):
```csharp
var dir = (target - from).Normalized();
float dist = (target - from).Length();
if (dist < 0.1f) dist = 0.1f;
```

**B. The direct-fire solve** (identical 3-line block, 5 copies):

- `SpawnArrow` (non-volley branch), `Projectile.cs:153-155`: `theta = 5° → Velocity = dir*speed*cos(theta); VelocityZ = speed*sin(theta)`.
- `SpellEffectSystem.SpawnProjectile`, `Necroking/Game/SpellEffectSystem.cs:397-400` (DirectFire), `406-408` (Swirly), `417-419` (Homing), `427-429` (HomingSwirly) — the exact same 3 lines pasted into all four switch cases; the cases differ only in which *extra fields* they set afterward (swirl params, homing target).

**C. Structural smell in the same code**: `SpellEffectSystem.SpawnProjectile` (`SpellEffectSystem.cs:373-440`) calls `projectiles.SpawnFireball(...)` — which computes the full lob solve — then grabs `projs[projs.Count - 1]` and **overwrites** `Velocity`/`VelocityZ` for any non-Lob trajectory. Wasted solve + a fragile "configure the last element of someone else's list" pattern. Also duplicated swirl-param randomization (`3f + rng*5f`, `0.5f + rng*1.5f`, phase) appears twice (`SpellEffectSystem.cs:411-413` and `434-436`).

### Why CONSOLIDATE

This is exactly the CLAUDE.md "shared component owns mechanics, caller owns data" case: the variance between call sites is pure **data** (speed, theta clamp, extra fields), not control flow. The physics formula (`theta = ½·asin(min(d·g/v², 1))`) is single-purpose gameplay math that must stay in lockstep across arrows, fireballs, potions, and spells — if gravity or the launch model is ever tuned, today that's 7 edit sites.

### Proposed canonical design

Canonical home: static helpers on `ProjectileManager` (it already owns `ArrowSpeed`/`MagicSpeed`/`Gravity`):

```csharp
/// theta for a ballistic lob reaching `dist` at `speed` under Gravity; optional clamp.
public static float SolveLobTheta(float dist, float speed,
                                  float minTheta = 0f, float maxTheta = Pi / 2f);
/// Fill (Velocity, VelocityZ) from dir/speed/theta — used by both lob and direct (5°) shots.
public static (Vec2 vel, float velZ) BallisticVelocity(Vec2 dir, float speed, float theta);
```

Plus a tiny private `PrepAim(from, target, out dir, out dist)` for the clamped-min-dist preamble.

### Call sites to migrate

1. `ProjectileManager.SpawnArrow` volley branch (uses `SolveLobTheta(dist, speed, 10°, 45°)`).
2. `ProjectileManager.SpawnArrow` direct branch (`BallisticVelocity(dir, speed, 5°)`).
3. `ProjectileManager.SpawnFireball`.
4. `ProjectileManager.SpawnPotionLob`.
5. `SpellEffectSystem.SpawnProjectile` — collapse the four switch cases' velocity lines into one shared call before the switch; cases then only set trajectory-specific fields (swirl/homing). Optionally factor the twice-repeated swirl randomization into a local helper. (Bigger cleanup — passing trajectory into the spawn instead of overwriting post-hoc — is optional and higher-risk; the velocity-solve dedup alone does not require it.)

### Effort / risk

- **Effort: S** (one file pair, pure-math extraction, no signature changes to public spawn API).
- **Risk: low.** Bit-identical math is trivially preserved. Verify with existing scenarios that fire projectiles: `BloomTestScenario`, `SpellKillTallyScenario`, `SpellVisualTestScenario`, `KnockbackCorpseScenario`, plus an archer volley via drive-game.

---

## Finding 2 — SpellPreview editor reimplements the trajectory solver and has already diverged (INVESTIGATE, severity: medium)

### Evidence

`Necroking/Editor/SpellPreview.cs` re-derives the whole thing independently:

- Duplicated constants, `SpellPreview.cs:22-23`: `ProjGravity = 13.89f`, `DefaultSpeed = 28.29f` — magic-number copies of `ProjectileManager.Gravity` / `ProjectileManager.MagicSpeed`. If the game constants are retuned, the editor preview silently lies.
- Lob solve, `SpellPreview.cs:985-989` — same formula, 4th lob copy.
- Direct/Swirly/Homing/HomingSwirly cases, `SpellPreview.cs:992-1029` — 4 more copies of the 5° direct solve + duplicated swirl randomization.
- **Confirmed divergence**: preview Homing/HomingSwirly use `p.VelocityZ = speed * MathF.Sin(theta) * 0.5f` (`SpellPreview.cs:1003, 1022`); the game's `SpellEffectSystem.cs:419, 429` uses full `sin(theta)`. The preview arc for homing spells is *not* what ships in-game. Homing strength (5f) and swirl ranges match today — by copy-paste luck.

### Why INVESTIGATE rather than CONSOLIDATE outright

There is one genuine structural variance and one open decision:

1. **Type variance**: preview uses XNA `Microsoft.Xna.Framework.Vector2` in a side-view 1-D layout; the sim uses `Vec2`. A scalar-level shared solver (`SolveLobTheta(dist, speed)` returns theta; caller applies cos/sin to its own vector type) sidesteps this cleanly — so this alone doesn't block consolidation.
2. **The decision**: is the preview's `* 0.5f` on homing VelocityZ **intentional preview framing** (keep the arc inside the small preview panel) or **drift**? If intentional, the shared solver should expose it as an explicit parameter with a comment; if drift, the preview should be corrected to match the game. Someone who knows the editor's intent should decide before flattening it.

Regardless of that decision, the **constants** (`ProjGravity`, `DefaultSpeed`) should reference `ProjectileManager.Gravity` / `ProjectileManager.MagicSpeed` — that part is an unambiguous S-effort, zero-risk fix and should ride along with Finding 1.

### Effort / risk

- **Effort: S** (constants + scalar solver reuse), plus one design question to answer.
- **Risk: low** — editor-only rendering; verify by opening the spell editor preview (drive-game) for a Lob and a Homing spell.

---

## Constraint check

- `Necroking/Net/` untouched — no networking code involved.
- Renderer untouched — this is sim/editor math only.
- No map-content implications.

## Bottom line

One shared arc solver in `ProjectileManager` (theta solve + velocity fill) eliminates 8-9 copies across `Projectile.cs`, `SpellEffectSystem.cs`, and `SpellPreview.cs`. The preview file is the proof the duplication already bit: its homing arc no longer matches the game's.
