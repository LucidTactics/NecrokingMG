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
  w, roundDuration)`** (computes `landingPos` = target position minus a
  radius-sum+0.2 standoff along the current attacker→target line — **no velocity lead** —
  then `JumpSystem.BeginPounce(units, i, landingPos, targetId, animMeta, sprite,
  arcPeak, speedOverride: CombatSpeed*SprintMult)` and stamps `PendingAttack` so the
  landing resolves melee); `Trample` → `TrampleSystem.BeginCharge`; `Sweep` → stamps a
  sweep `PendingAttack`. This is the ONLY `BeginPounce` caller — wild-wolf and other AI
  handlers don't pounce themselves (see `WolfPackHandler.cs` comment: pounce is central).
  `BeginPounce` (`Necroking/Game/JumpSystem.cs`) derives travel duration itself
  from `dist / speed` + anim compression.

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
  precision to `SpawnArrow`. Pounce (`InitiatePounceWithWeapon`), trample (straight
  per-frame homing in `TrampleSystem.TickCharge`), and spell target points do NOT lead.
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
