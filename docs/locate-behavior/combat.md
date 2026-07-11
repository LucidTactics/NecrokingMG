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
**`ProjectileManager`** owns all projectiles (arrows, fireballs, potion lobs): spawn API +
per-frame `Update(dt, units, qt, corpses)` (physics, homing/swirl, collision, produces
`Hits`/`Impacts` lists consumed by `Simulation`).
- **`SpawnArrow(from, target, faction, owner, damage, volley, precision, weaponName,
  spawnHeight)`** — `volley:false` = near-flat 5° direct shot; **`volley:true` = ballistic
  lob**: solves launch angle from `dist*Gravity/speed²`, clamped 10°–45°, plus
  precision-scaled scatter (`Projectile.IsLob`). So **arcing shots already exist** — the
  choice is made by the CALLER (`Simulation.ResolvePendingAttack` ranged branch uses
  `volley = dist > maxRange*0.4f`). `spawnHeight` should be the attacker's
  `Unit.EffectSpawnHeight` (bow-tip anim point).
- `SpawnFireball` / `SpawnPotionLob` — always-lobbed variants; `DetonateAtTarget` bursts
  exactly at the aimed point instead of overshooting.
- **Target leading exists in ONE place**: `Simulation.FireArrowAt(attackerIdx, defenderIdx,
  weaponIdx)` (the single arrow-ballistics chokepoint called by both the
  `ResolvePendingAttack` ranged branch and legacy `ArcherAttack`) does a one-iteration
  linear lead — `aim += defender.Velocity * (dist / ProjectileManager.ArrowSpeed)` — inline,
  not via any shared helper. It also picks direct-vs-lob (`IsFireLaneClear`) and passes
  precision to `SpawnArrow`. **Pounce now leads too** via the shared
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
  `Stats.RangedDmg/RangedRange[weaponIdx]`, calls `SpawnArrow`. **No range/LoS re-check at
  resolve** (same stamp-time-gating rule as melee).
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
  `DetonateAtTarget` overshoot check (set ONLY for `spell.Category == "Blight"` in
  `SpawnProjectile`), (c) **ground impact** (`Height <= 0 && VelocityZ < 0` — detonates
  wherever the arc lands), or (d) `MaxAge = 10s`. **Trap:** the non-Lob trajectories
  (`Trajectory.DirectFire/Swirly/Homing/HomingSwirly` in `SpellEffectSystem.SpawnProjectile`)
  all launch at a fixed 5° and still take full gravity, so their max flat-ground travel is
  ≈ `speed²·sin(10°)/Gravity` + spawn-height bonus — ~19–20u at speed 28.3. Raising a
  spell's `range` past that makes the cast legal but the projectile lands/detonates midway.
  Only `Lob` (the `SpawnFireball` default) solves the launch angle from the actual distance
  (`asin(dist·Gravity/speed²)`, silently clamped when `dist > speed²/Gravity`).
- **Spell-projectile spawn/config chokepoint**: `SpellEffectSystem.SpawnProjectile(spell,
  projectiles, origin, target, ownerUid, spawnHeight, casterFaction)` — calls
  `SpawnFireball` then post-configures the last projectile from the `SpellDef` (SpellID
  tag, Trajectory Direct/Swirly/Homing/Lob, DetonateAtTarget for Blight, flipbooks).
  Called from `SpellEffectSystem` cast paths and `Game1.Spells.cs`
  (`TickPendingProjectiles` staggered Quantity>1 shots) — a new per-projectile field set
  here covers every shot.

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
