# Combat — melee/ranged attack resolution & range gating

How an attack goes from "intent" to "damage applied", and where distance is
checked. The pipeline is **stamp a `PendingAttack` target → animation reaches its
hit frame → resolve**. There is **one canonical melee-range formula** shared by the
sim and the AI so engage/attack range can't drift.

```
intent (AI combat loop OR player click)  → sets Unit.PendingAttack (+ Target)
   → anim plays; JustHitEffectFrame fires → Simulation.ResolvePendingAttack(i)
      → ResolveMeleeAttack(...) applies damage   (NO distance check here)
```

**Key invariant / trap:** `ResolvePendingAttack` / `ResolveMeleeAttack` do **not**
re-check distance for melee — they trust that whoever stamped `PendingAttack`
already gated on range. So the range check must happen **at stamp time**. AI paths do
this; a player click that stamps `PendingAttack` directly must do it too or melee
lands at any range.

## Files

### `Necroking/Game/Combat/MeleeRangeUtil.cs`
**Single source of truth for melee engage/attack range.** `MeleeRangeUtil.Compute(units,
attackerIdx, targetIdx, gd)` = `Settings.Combat.MeleeRange` (fallback `MeleeRangeBase =
0.8f` when `gd == null`) `+ attacker.Stats.Length*0.15f + attacker.Radius + target.Radius`.
**Use this anywhere you gate "am I close enough to melee".** `SubroutineSteps.GetMeleeRange`
forwards to it so AI and sim share one formula (they previously drifted — 1.5f vs 0.8f).

### `Necroking/Game/Simulation.cs` — resolution + AI-side gating
- **`ResolvePendingAttack(int unitIdx)`** — consumes `Unit.PendingAttack`: ranged branch
  spawns an arrow; melee branch resolves `ResolveMeleeAttack` (or `ResolveMeleeSweep` for
  sweep weapons). **No melee distance check** — refunds only when the target vanished.
- **`ResolveMeleeAttack` / `ResolveMeleeAttackExternal`** — the actual hit/damage resolver
  (also used by TrampleSystem/SweepSystem which bypass `PendingAttack`).
- **Attack-selection loop** (`~line 2384`, the "Attack cooldowns and queuing" block) — the
  AI/general path that STAMPS `PendingAttack` from `EngagedTarget`. It is gated by range:
  the engage/`InCombat` update just above (`~line 2374`, `dist <= MeleeRangeUtil.Compute(...)`)
  and per-weapon range checks. **This is why AI melee respects range** and the player click
  path (below) must replicate it.
- Other `MeleeRangeUtil.Compute` call sites: `~1469`, `~1550` (engage transitions), `~2375`
  (InCombat set), `~3634` (a secondary engage check).
- **Special weapon archetypes are dispatched inside the attack-selection loop** by
  `WeaponStats.Archetype`: `WeaponArchetype.Pounce` → **`InitiatePounceWithWeapon(i, ti,
  w, roundDuration)`**; `Trample` → `TrampleSystem.BeginCharge`; `Sweep` → stamps a
  sweep `PendingAttack`. Range gate for pounce is the **unled** center-to-center distance
  in `[PounceMinRange, PounceMaxRange]` (weapon fields, `Data/CombatTypes.cs` /
  `weapons.json` `pounceMinRange`/`pounceMaxRange`/`pounceArcPeak`).
- **Pounce intercept lead + landing point** (`InitiatePounceWithWeapon`,
  `Necroking/Game/Simulation.cs`): the landing spot is computed **once, at pounce
  initiation** (before the JumpTakeoff/crouch anim even starts) and never updated:
  1. `InterceptUtil.PredictPosition(myPos, targetPos, target.Velocity, pounceSpeed)`
     (`Necroking/Game/Combat/InterceptUtil.cs` — 2-iteration linear lead; assumes
     constant target velocity, `pounceSpeed = Locomotion.SprintTopSpeed`).
  2. `InterceptUtil.ClampLeadOvershoot(myPos, predicted, PounceMaxRange)` — lead may
     stretch +30% past PounceMaxRange, then pulls back onto that circle.
  3. `landingPos = predicted − dir(toPredicted) * target.Radius` — the pouncer's center
     lands on the predicted target's **collision edge** (standoff = target radius,
     along the approach to the PREDICTED point). Slack at landing = attackerR + 0.5u.
  Then `JumpSystem.BeginPounce(units, i, landingPos, targetId, animMeta, sprite,
  weapon.PounceArcPeak, speedOverride: pounceSpeed)` and stamps `PendingAttack` so the
  landing resolves melee. This is the ONLY `BeginPounce` caller — wild-wolf and other AI
  handlers don't pounce themselves (see `WolfPackHandler.cs` comment: pounce is central).
- **Pounce timeline** (`Necroking/Game/JumpSystem.cs`, phases on `Unit.JumpPhase`):
  1=TakeoffApproach (on ground, JumpTakeoff anim; handlers `break` on `JumpPhase != 0`
  so `PreferredVel` is whatever was last set) → **liftoff at JumpTakeoff's
  `effect_time_ms`** (`ctrl.JustHitEffectFrame` in `TickTakeoffApproach`; only
  `JumpStartPos` is recaptured there — `JumpEndPos` stays locked from initiation) →
  2=Airborne (scripted lerp + parabolic Z) → 3=Landing (JumpLand starts `effect_time`
  before touchdown) → 4=Recovery. Anim playback is uniformly compressed so total time
  = `dist/speed`. **Landing hit check** (`FireLandingCallback`): if
  `dist(lander, target) > attackerR + targetR + PounceLandingHitMargin (0.5u const)`
  the pounce is a clean miss — `PendingAttack` cleared, `PostAttackTimer` refunded;
  else `sim.ResolvePendingAttack(idx)`. Anim `effect_time_ms` comes from the sprite's
  meta JSON via `Render/AnimationMeta.cs` `AnimMetaLoader` (missing effect_time on
  JumpTakeoff/JumpLand = silent timing bugs; loader logs a warning).
- **Voluntary (non-pounce) jump — `JumpSystem.BeginJumpAttack(units, idx, endPos, arcPeak)`**
  (`Necroking/Game/JumpSystem.cs`): a stationary leap that skips TakeoffApproach and starts
  **airborne** (`Kind.NecromancerAttack`), scripted-lerps to `endPos` on a parabolic arc and
  resolves a melee `PendingAttack` at landing. Already wired to the **necromancer Space key**
  in `Game1.cs` (`~line 3596`; leaps `FacingUtil.ForwardDir*4f`, guards `JumpPhase==0 &&
  !Incap.IsLocked && !_pendingSpell.Active`). **This is the template for a new voluntary
  "jump/leap" ability** that reuses the jump anim states without a pounce target — pick a
  `Vec2` destination (no pounce lead/target needed). All phases tick per-unit via
  **`JumpSystem.TickUnit(dt, units, idx, ctrl, sim)`**, called from `Game1.Animation.cs`
  (`~line 423`); it returns true to make the anim loop skip normal anim/movement that frame.
  Jump state fields on `Movement/UnitModel.cs`: `JumpPhase`/`Jumping`/`JumpKind`/`JumpStartPos`/
  `JumpEndPos`/`JumpArcPeak`/`JumpTimer`/`JumpDuration`/`JumpPlaybackSpeed`.
- **Fly height / vertical draw offset = `Unit.Z`** (world units; `Movement/UnitModel.cs`) — the
  `SetFlyHeightTmp` equivalent. `JumpSystem.ApplyArc` writes `Z = arcPeak*4*t*(1-t)` each
  airborne frame and zeroes it at landing/`EndJump`. Rendering lifts the sprite by it:
  `GameRenderer.Units.cs` (`~line 1062`) draws at `WorldToScreen(u.RenderPos, u.Z, camera)`,
  and `Camera25D.WorldToScreen` subtracts `worldHeight*Zoom*YRatio` from screen-Y. It is a
  **persistent field written every frame by the scripted-motion owner** (JumpSystem/
  PhysicsSystem), NOT a self-resetting "temp" — the owner MUST zero it on exit or the unit
  stays floating (`EndJump`/`TickRecovery` do). Corpses use `Corpse.Z` identically.

### `Necroking/Game1.WorldClicks.cs` — the player click → melee order (regression lives here)
- **`TryAttackClick(mouseWorld, necroIdx)`** — LMB/RMB on an enemy orders the necromancer to
  melee it. Picks the enemy with `FindClosestEnemyToPoint(mouseWorld, Settings.Tooltips.
  HoverPickRadius)` (**anchored on the CURSOR**, radius 1.5f), then stamps `Target` +
  `PendingAttack` + `AttackCooldown = 2f` **directly, with no attacker↔target distance
  check.** Because `ResolvePendingAttack` also doesn't check, **the necromancer melees at
  any range** — this is the "melee works at any range" regression.
  - **Root cause (regression introduced in commit `6db6c90`, "mouse-for-world rework"):** the
    OLD LMB melee fallback (removed from `Game1.cs`) searched
    `FindClosestEnemyToPoint(_sim.Units[necroIdx].Position, 2f)` — anchored on the
    **necromancer**, so it only found a target within 2u of the caster, an *implicit* range
    gate. The rework re-anchored the pick to the cursor and dropped that implicit gate.
  - **Fix goes in `TryAttackClick`:** after `enemyIdx` is found, gate the stamp on
    `dist(necro, enemy) <= MeleeRangeUtil.Compute(_sim.Units, necroIdx, enemyIdx, _gameData)`
    (mirror the AI path). On too-far, either don't stamp (return unconsumed, old behavior) or
    give feedback (`SpawnCastFailText(necroIdx, "Too Far")`, as `TryPileGatherClick` does).
- `FindClosestEnemyToPoint(worldPos, maxRange)` lives in `Game1.cs` (`~line 4097`) — nearest
  non-Undead unit within `maxRange` of `worldPos`; squared-distance, no path/LoS.

### Ranged / projectiles — `Necroking/Game/Projectile.cs` (namespace `Necroking.GameSystems`)
**`ProjectileManager`** owns all projectiles (arrows, spell shots, potion lobs): spawn API +
per-frame `Update(dt, units, qt, corpses)` (physics, homing/swirl, collision, produces
`Hits`/`Impacts` lists consumed by `Simulation`).
- **ONE spawn entry point**: **`Spawn(from, target, faction, owner, type, damage, speed,
  lob, aoeRadius, precision, weaponName, spawnHeight, gravityScale, preferHighArc)`**
  returns the spawned `Projectile` for post-configuration (SpellID, flipbooks, homing,
  potion payload). `ProjectileType` is behavior-named: **`RegularHit`** strikes the first unit
  it touches along its flight path (arrows, magic darts), **`Explosive`** bursts on
  proximity/ground with AoE, **`Potion`** delivers a potion payload to the closest
  unit/corpse. **`lob` vs direct:** BOTH now solve the launch angle from the aim distance
  via `SolveLobTheta` (θ = ½·asin(dist·g/v²)) — `lob:false` ("direct fire") = the exact
  min-energy arc to the aim point, NO scatter; `lob:true` adds precision-scaled scatter
  around the target and (RegularHit only) a 10°–45° arc clamp; `preferHighArc` (spell
  `Trajectory` `"HighLob"`) takes the mirrored mortar arc. **`DirectFireTheta` (5°) is DEAD
  in the sim** — its only remaining consumers are `Editor/SpellPreview.cs` arcs, so the
  editor preview diverges from actual direct-fire flight. Consequence: a direct shot aimed
  PAST a unit rises on a taller arc and can exceed the in-flight hit window (below) over
  the unit's head; aimed short, it grounds before reaching it.
  `NoFriendlyFire` (public field on `Projectile`, settable post-spawn) is defaulted by
  `Spawn` to `type == RegularHit` (RegularHit skips the owner's faction via
  `FactionMaskExt.AllExcept(OwnerFaction)`; Explosive/Potion hit everyone). It gates
  **every** unit query on the projectile: RegularHit in-flight collision, Explosive
  proximity-trigger AND ground-burst AoE, RegularHit ground-hit pick — one flag, all phases.
  **In-flight unit collision has NO per-unit hitbox height**: RegularHit hits only while
  `0 < Height < UnitHitHeight` (const 2.0), 2D quadtree query at `HitRadius` 0.6, point
  test per frame (no segment sweep). Explosive proximity triggers below
  `ExplosiveHitHeight` 5.0 after `ExplosiveArmTime`. Arc choice is made by the CALLER
  (`Simulation.FireArrowAt` uses
  `lob = !(dist <= directRange && IsFireLaneClear)`). `spawnHeight` should be the
  attacker's `Unit.EffectSpawnHeight` (bow-tip anim point). `DetonateAtTarget` bursts
  exactly at the aimed point instead of overshooting.
- **Target leading exists in ONE place**: `Simulation.FireArrowAt(attackerIdx, defenderIdx,
  weaponIdx)` (the single arrow-ballistics chokepoint called by both the
  `ResolvePendingAttack` ranged branch and legacy `ArcherAttack`) does a one-iteration
  linear lead — `aim += defender.Velocity * (dist / ProjectileManager.ArrowSpeed)` — inline,
  not via any shared helper. It also picks direct-vs-lob (`IsFireLaneClear`) and passes
  precision to `ProjectileManager.Spawn`. **Pounce now leads too** via the shared
  `Necroking/Game/Combat/InterceptUtil.cs` (`PredictPosition`/`ClampLeadOvershoot` —
  the single source for target leading; new launched-at-moving-unit abilities should
  call it). Trample (straight per-frame homing in `TrampleSystem.TickCharge`) and
  spell target points do NOT lead.
- **Projectiles do NOT collide with walls/env objects** — only units (quadtree radius
  query, arrows hit while `0 < Height < 2`), corpses (potions), and the ground. There is
  **no line-of-sight utility in the codebase** (no LoS/raycast helper) — a "lob over
  blockers" feature needs both a new LoS query and (optionally) arrow-vs-wall collision.
- **Ranged fire path (archetype units)**: `RangedUnitHandler.UpdateCombat` stamps
  `PendingAttack` + `PendingWeaponIdx/PendingWeaponIsRanged/PendingRangedTarget` →
  anim hit frame → `ResolvePendingAttack` ranged branch (`isRanged ||
  Archetype == ArcherUnit`): re-resolves the target by stored ID, reads
  `Stats.RangedDmg/RangedRange[weaponIdx]`, calls `ProjectileManager.Spawn` (via
  `FireArrowAt`). **No range/LoS re-check at resolve** (same stamp-time-gating rule as melee).
- **Legacy path**: `AIBehavior.ArcherAttack` in `Simulation.UpdateAI` (`~line 1046`)
  spawns arrows directly (no anim sync) for archetype-less units — don't extend it; new
  ranged behavior belongs in `RangedUnitHandler`.
- **Hit consumption**: `ProjectileManager.Update` fills per-frame `Hits`
  (`ProjectileHit`: UnitIdx, Damage, OwnerID/Faction, SpellID, AoeRadius, ImpactPos,
  PotionID, HitLocation…) and `Impacts` (`ImpactEvent`, ground/visual). **The single
  consumer is the `foreach (var hit in _projectiles.Hits)` loop in `Simulation.Tick`**
  (the "Projectiles" phase): potions → `PotionSystem.ApplyPotionEffect`/corpse raise;
  spell projectiles → resolve `SpellDef` from `hit.SpellID`, apply **physics knockback
  BEFORE damage** (so a killed unit's corpse inherits the arc) via
  `_physics.ApplyRadialImpulse` when `spellDef.KnockbackForce > 0` (radius =
  `KnockbackRadius` or `hit.AoeRadius`), then MR gate (`SpellPenetration`) and
  `DealDamage` (plain arrows go through `ResolveArrowHit` instead). `ProjectileHit` now
  carries `FlightDir` (= `proj.Velocity`), `ImpactForce`, `ImpactUpward` at every
  `_hits.Add(...)` site — the directional-impulse fields added with the `test_impact` spell
  (`SpellDef.ImpactForce/ImpactUpward` set on the projectile in
  `SpellEffectSystem.SpawnProjectile`).
- **Projectile travel distance / expiry / detonation point — there is NO "range" on a
  projectile.** `SpellDef.Range` is consumed ONLY at cast time (`SpellCasting.cs`
  `dist > spell.Range → CastResult.OutOfRange` checks); once spawned, flight is pure
  ballistics in `ProjectileManager.Update`: `Position += Velocity*dt; Height += VelocityZ*dt;
  VelocityZ -= Gravity*dt` (Gravity=13.89, MagicSpeed=28.29, ArrowSpeed=23.58 consts at top
  of `Projectile.cs`). A projectile dies by (a) in-flight unit collision, (b)
  `DetonateAtTarget` overshoot check (set for `spell.Category == "Blight"` OR the
  `SpellDef.DetonateAtTarget` opt-in field, in `SpawnProjectile`), (c) **ground impact**
  (`Height <= 0 && VelocityZ < 0` — detonates wherever the arc lands), or (d)
  `MaxAge = 10s`. ALL trajectories (incl. `Trajectory.DirectFire/Swirly/Homing/HomingSwirly`
  in `SpellEffectSystem.SpawnProjectile`) now solve the launch angle from the aim distance
  via `SolveLobTheta` (`½·asin(dist·Gravity/speed²)`, silently clamped when
  `dist > speed²/Gravity` — past that the shot still lands short; `HighLob` takes the
  mirrored high arc; `Lob` is the trajectory default). The old fixed-5° direct launch (and
  its ~19–20u max-travel trap) is GONE from the sim.
- **Spell-projectile spawn/config chokepoint**: `SpellEffectSystem.SpawnProjectile(spell,
  projectiles, origin, target, ownerUid, spawnHeight, casterFaction)` — calls
  `ProjectileManager.Spawn` (Explosive when `AoeRadius > 0`, else RegularHit) and
  post-configures the returned projectile from the `SpellDef` (SpellID tag, homing/swirl
  trajectories, DetonateAtTarget for Blight, flipbooks).
  Called from `SpellEffectSystem` cast paths and `Game1.Spells.cs`
  (`TickPendingProjectiles` staggered Quantity>1 shots) — a new per-projectile field set
  here covers every shot.
- **Over-time volley state (`Quantity>1` spells)**: `PendingProjectileGroup`
  (struct in `Necroking/Game/SpellEffectSystem.cs`: `SpellID`, `CasterUid`, `Origin`,
  **`Target`** = the fixed aim point captured at cast, `Remaining`, `Timer`, `Interval`)
  is added to `Game1._pendingProjectiles` by `SpellEffectSystem.Execute` (Projectile +
  Blight-bomb cases) and ticked by **`Game1.Spells.cs` `TickPendingProjectiles(dt)`**
  (called from `Game1.cs` Update ~line 3709 on `WorldDt`, inside the sim gate). Each tick
  re-resolves the caster by stable uid (`_sim.ResolveUnitID(pg.CasterUid)`; group dropped if
  dead) and fires from the live `EffectSpawnPos2D` but **at the frozen `pg.Target`** — the
  aim does NOT update between shots. **To retarget each shot to the cursor:** the group is
  caster-agnostic, so gate on the player — `casterIdx == FindNecromancer()` (or
  `_sim.Units[casterIdx].AI == AIBehavior.PlayerControlled`) — then overwrite `pg.Target`
  with the current cursor world before `SpawnProjectile`. Cursor world = `_camera.ScreenToWorld(_input.MousePos, vpW, vpH)`; **validity** = the `cursorOutside`
  test in `Game1.Update` (`mouse.X<0 || mouse.Y<0 || mouse.X>=vpW || mouse.Y>=vpH`, ~line
  2825) — that local isn't visible from `TickPendingProjectiles`, so cache a
  `_lastValidCursorWorld` Game1 field each focused/in-window frame and only update `pg.Target`
  when valid (leaving `pg.Target` = last valid / initial cast target = the fallback for free).

### Projectile RENDERING — `Necroking/GameRenderer.World.cs` (two passes, branches on `Projectile.Type`)
Where each in-flight projectile is drawn. **Two separate draw methods, split by type:**
- **`DrawProjectiles()`** (normal alpha pass) handles **Arrow** and **Potion**; **skips
  Fireball** (`continue`). Both branches are fog-of-war gated (`_fogOfWar.IsVisible`).
  - **Arrow branch is HARDCODED** — a two-quad `_pixel` draw: an oriented shaft
    (`new Color(200,180,120)`, oriented by `Atan2(Velocity.Y*YRatio, Velocity.X)`) + a
    darker arrowhead. **It reads NONE of the per-projectile visual fields** — not
    `FlipbookID`, not `ParticleColor`, not `IconTexturePath`. So a spell-launched
    arrow-type projectile (see below) that carries a `FlipbookID` still renders as the
    generic pixel arrow. **This is the gap to fix for per-spell arrow visuals.**
  - **Potion branch** draws `_g.GetItemTextureByPath(proj.IconTexturePath)` (tumbling item
    icon), fallback colored dot — the one type that already keys visuals off a per-projectile
    field.
  - Unknown types → `DebugLog.Log("render", ...)` and skip (never throws from the draw loop).
- **`DrawProjectilesHdr()`** (additive HDR pass) renders `proj.FlipbookID` (via
  `_g._flipbooks.TryGetValue(fbId, ...)`, animated by `fb.GetFrameAtTime(proj.Age)`), tinted
  by `proj.ParticleColor` (`HdrColor.ToHdrVertex`), scaled by `proj.ParticleScale`, with a
  2-frame trail (main + 2 fading previous frames). Pass membership = `RendersInHdrPass(proj)`
  (Explosive always, or a **RegularHit** projectile carrying a loaded flipbook — this is the path
  spell "magic dart"/barrage projectiles take).
  - **TRAIL / TAIL ORIENTATION does NOT follow the trajectory** (the "trail doesn't face
    travel direction" bug). Two separate defects, both in this method's flipbook branch
    (`Necroking/GameRenderer.World.cs`, ~lines 486–505):
    1. **Sprite rotation is a constant age-spin, not velocity-facing** — every trail frame is
       drawn with rotation `proj.Age * 2f`, so the dart just spins regardless of heading.
    2. **The trail offset is XY-only, ignoring the vertical (Z) arc** — `velDir =
       proj.Velocity.Normalized()` (world XY), and the trail sits at
       `sp.Y - velDir.Y * trailOffset * YRatio`. On a lobbed/`HighLob` arc most of the
       on-screen vertical motion comes from **`proj.VelocityZ`** (height), which this omits.
  - **Fix:** compute the SCREEN-space travel direction including Z. From `Camera25D.WorldToScreen`
    (`sy = worldY*Zoom*YRatio − worldHeight*Zoom*YRatio`), screen velocity is
    `dx = Velocity.X`, `dy = (Velocity.Y − VelocityZ) * YRatio`. So
    `faceAngle = Atan2((proj.Velocity.Y − proj.VelocityZ) * _g._camera.YRatio, proj.Velocity.X)` —
    use it as the draw rotation and normalize `(dx,dy)` for the trail-offset direction. The
    hardcoded arrow-shaft angle in `DrawProjectiles()` (~line 365,
    `Atan2(Velocity.Y*YRatio, Velocity.X)`) has the **same Z-ignoring bug** for flipbook-less
    RegularHit arrows on arcs.

**How the visual fields get ONTO the projectile:** `SpellEffectSystem.SpawnProjectile`
(`Necroking/Game/SpellEffectSystem.cs`, the chokepoint) copies `spell.ProjectileFlipbook`
→ `proj.FlipbookID/ParticleScale/ParticleColor` and `spell.HitEffectFlipbook` → the
`HitEffect*` fields. AoE spells (`AoeRadius>0`) spawn as **Fireball** (so their flipbook
shows); **single-target/zero-AoE spells spawn as Arrow** (the "fire single-target spells as
arrows" commit) — and the Arrow draw branch ignores `FlipbookID`, so their configured
`ProjectileFlipbook` is silently dropped. `FlipbookRef` (`SpellRegistry.cs`) fields:
`FlipbookID`, `FPS`, `Scale`, `Color` (HdrColor), `Rotation`, `BlendMode` (Alpha/Additive),
`Alignment` (Ground/Upright), `Duration`.

**Where the fix goes:** the Arrow branch of `DrawProjectiles()` in
`Necroking/GameRenderer.World.cs`. When `proj.FlipbookID` is set and loaded, render the
flipbook oriented along `proj.Velocity` (reuse the `DrawProjectilesHdr` flipbook lookup +
`ParticleColor`/`ParticleScale` logic, honoring `FlipbookRef.Alignment`/`BlendMode`) and
fall back to the hardcoded pixel arrow only when no flipbook is set. If Additive/HDR is
wanted for the dart, route flipbook-carrying arrows through `DrawProjectilesHdr` instead
(mind the `Type != Fireball` guards in BOTH methods). No sim/spawn change is needed — the
data already reaches the projectile; only the Arrow draw branch consumes too little of it.

### `Necroking/Game/PhysicsSystem.cs` — impulse knockback (units only)
The 2.5D "popcorn" physics: a unit hit by an impulse enters `InPhysics` (AI/ORCA
suspended, `AIControl.Interrupt` fired, Fall anim forced), flies with gravity/drag,
chains into standing units via inelastic mass³ momentum transfer, and lands with the
`buff_knockdown` buff.
- **`ApplyImpulse(units, unitIdx, direction, force, upwardForce, bypassResistance,
  bypassMinZ, gravityMul, dragMul)`** — the single-unit directional launch (size-based
  resistance: `Size * ResistanceMultiplier` subtracts from force; charging trample units
  are immune). A successful launch cancels competing scripted motion: zeroes `DodgeTimer`
  (trample-dodge hop) and calls `JumpSystem.CancelJump` — those systems write Position/Z
  absolutely and would otherwise fight the physics body.
- **`ApplyRadialImpulse(units, center, radius, force, upwardForce)`** —
  explosion knockback with linear falloff; **hits everyone incl. friendlies** (no
  faction parameter — knockback is deliberately faction-blind).
- Bodies are keyed by stable `UnitId`, not index. Ticked from `Simulation.Tick`.
- Corpses inherit the body's velocity at death (`TryGetBodyVelocity`/`TryGetBodyTuning`
  → corpse arc integrated in `Simulation.UpdateCorpses`). **Environment objects/props
  have no physics** — `EnvironmentSystem` placed objects are static; pushing props would
  be new work.
- Spell data hooks: `SpellDef.KnockbackForce/KnockbackUpward/KnockbackRadius`
  (`Data/Registries/SpellRegistry.cs`, PHYSICS editor group) consumed in the Simulation
  hits loop; weapons have `TrampleKnockbackForce` (TrampleSystem).

### `Necroking/Game1.Animation.cs` — the resolve trigger
The per-unit anim tick (`~line 752`): when `animData.Ctrl.JustHitEffectFrame` fires and the
unit has a non-none `PendingAttack`, it calls `_sim.ResolvePendingAttack(i)`. (A pending
*spell* cast on the necromancer takes precedence via `ExecuteSpellEffect`.) So melee damage
lands at the swing's hit frame, not at click time. `JumpSystem.cs` (`~line 392`) resolves a
pounce's `PendingAttack` at landing.

### `Necroking/AI/SubroutineSteps.cs` / combat handlers
`GetMeleeRange(ref ctx, targetIdx)` → `MeleeRangeUtil.Compute`. Handlers that gate melee on
it: `CombatTransitions.cs`, `HordeMinionHandler.cs`, `CombatUnitHandler.cs`,
`WolfPackHandler.cs`, `RatPackHandler.cs`, `DeerHerdHandler.cs`. Edit these for AI-side
engage/attack-distance behavior, not the resolver.

## Whiffs vs fleeing targets — mid-swing cancellation & phantom windups

Why a chase-attack "plays the anim but doesn't connect". A **stamped** melee swing always
resolves at ANY distance (no resolve-time range check), so distance itself never whiffs a
queued swing. The whiff mechanisms are upstream:

- **AI handler cancels the queued swing before its effect frame.**
  `SubroutineSteps.Disengage` (`Necroking/AI/SubroutineSteps.cs`) force-clears
  `PendingAttack` + `PostAttackTimer` **every tick**. `AI/SoloPredatorHandler.cs`
  (SoloPredator = DireWolf/JuvWolf, AmbushPredator = bear) transitions SubAttacking →
  SubDisengage on `AttackCooldown > 0 && SubroutineTimer > 0.2f` — but **AttackCooldown
  starts at STAMP time, not at the hit**, so the swing gets cancelled ~0.2s after queueing,
  usually before the attack anim's effect_time fires → anim plays, damage never rolls.
  `WolfPackHandler.cs` FightExecuteAttack and `RatPackHandler.cs` were both fixed to wait
  for `PostAttackTimer <= 0` first ("phantom no-damage retreat" comment); SoloPredator was
  not. `SubroutineSteps.AttackTarget_CooldownStarted` encodes the same flawed condition.
- **Pre-roll windup anims with no queued attack.** `Game1.Animation.cs` (~line 602) plays
  the attack anim purely visually when `InCombat && AttackCooldown > 0` near cooldown end —
  no `PendingAttack` exists. If the target breaks melee range before the stamp gates align
  (cooldown 0 + InCombat + facing + PostAttackTimer 0), the windup plays and nothing fires.
- **Plant oscillation shrinks the stamp window.** `Simulation.UpdateMovement` zeroes the
  attacker's Velocity while `PendingAttack`/`PostAttackTimer`/`InCombat` (non-player,
  non-fleeing) — but **fleeing/routing targets are exempt from the InCombat plant**, so a
  chaser brakes on contact while the target keeps sprinting; melee-range contact lasts only
  a few frames per catch-up cycle, and the weapon cooldown (`CooldownRounds ×
  Settings.Combat.RoundDuration`, default 3s) must be ready inside that window.
- **Zero-damage hits are legal** (glancing: `netDmg < 0 → 0`, logged as Hit with
  NetDamage=0). Misses log `Outcome=Miss` + bump defender Harassment. Note the DRN math:
  with `drn:1` (d3) both sides, wolf bite (atk 6 + bonus 2) vs deer (def 6) can never
  roll a miss — so absent log entries mean the swing was cancelled/never stamped, not
  that the dice failed.
- **Movement during the swing is a hard plant, no physical lunge** —
  `Render/LungeSystem.cs` only writes a cosmetic `RenderOffset` from
  `Unit.CurrentAttackLungeDist` (weapon `lungeDist`, stamped in the attack-selection loop);
  Position doesn't move.

## Pitfalls / gotchas
- **Range is gated at stamp time, not at resolve time.** Any new code that sets
  `PendingAttack` directly (player orders, scripted attacks) must do its own
  `MeleeRangeUtil.Compute` check — `ResolvePendingAttack` will happily apply damage across
  the whole map otherwise.
- **Two "find enemy" anchors.** `FindClosestEnemyToPoint(necroPos, r)` = "enemy near me"
  (implicit range gate); `FindClosestEnemyToPoint(mouseWorld, r)` = "enemy under cursor" (a
  targeting pick, NOT a range gate). Don't conflate them.
- **Don't add a distance check inside `ResolveMeleeAttack`** without care — it's also called
  by Trample/Sweep external dispatchers that intentionally hit at their own ranges.
- Use `MeleeRangeUtil.Compute`, never a hardcoded literal (the 1.5f/0.8f drift is exactly the
  bug this util was created to kill).

## Related areas
- [ai.md](ai.md) — AI archetype handlers that decide when to engage/attack (they call
  `GetMeleeRange`); `SubroutineSteps.AttackTarget`/`Disengage`.
- game1-partials.md — `Game1.WorldClicks.cs` world-click dispatch, `Game1.Animation.cs`
  hit-frame tick, `Game1.cs` `FindClosestEnemyToPoint`.

## Consolidation update (2026-07-07)

- **DamageSystem.Kill** is the only sanctioned way to kill a unit (HP=0,
  Alive=false, death anim, prone-snap, attribution) — used by Apply/ApplyDirect,
  limb-chop, poison ticks, trigger kills. StampAttacker = shared attribution tail.
- **SpellEffectSystem.ExecuteStrikeFrom** = the single Strike executor for
  player/AI/trap sources (traps pass casterIdx=-1: base MR penetration, no kill
  credit). Trap fire no longer hand-rolls zaps.
- **WeaponBonusEffectSystem** ticks timed on-hit effects; potion weapon coats
  are BonusEffects entries (300s), the old coat timer fields are gone.
- Ballistic arcs: `ProjectileManager.SolveLobTheta/BallisticVelocity/
  DirectFireTheta` (+Gravity/MagicSpeed) — shared with the editor preview.
