# json — `data/buffs.json` (BuffDef registry)

Schema = `BuffDef` in `Necroking/Data/Registries/BuffRegistry.cs` (id-keyed list, loaded by
`RegistryBase`). Runtime apply/tick/expiry = `Necroking/Game/BuffSystem.cs`; per-buff visuals =
`Necroking/Render/BuffVisualSystem.cs` (see [render.md](render.md) "Buff visuals & the casting
glow"). Edit via the `edit-game-data` skill + `--roundtrip-data`, not raw hand edits.

## Fields that matter most

- **`duration`** (float, default **10.0**) — seconds. **Semantics at apply time
  (`BuffSystem.ApplyBuffWithDuration`): `duration <= 0` (0, -1, …) ⇒ `ActiveBuff.Permanent = true`
  — the buff NEVER expires on its own; something must explicitly remove it**
  (`BuffSystem.RemoveBuff`, `Game1.RemoveCastingBuffAll`, …). Positive durations tick down in
  `TickBuffs` (sim clock) and remove the buff at 0 — **no on-expire hook exists** beyond
  weapon-strip/HP-clamp/incap-recovery specials.
  - **Refresh-path gotcha:** re-applying a buff that's already on the unit only overwrites
    `RemainingDuration`, it does **not** recompute `Permanent` (BuffSystem ~line 51). So
    permanent→timed or timed→permanent transitions via re-apply are silently wrong (a re-apply
    with `duration:-1` onto a non-permanent instance is removed next tick because
    `RemainingDuration <= 0`).
- **`effects`** — list of `{type: "Add"|"Multiply"|"Set", stat, value}`; read on-the-fly by
  `GetModifiedStat`, never baked into `Unit.Stats`.
- **`maxStacks`** — refresh increments `StackCount` up to this; also resets duration.
- **`grantedWeapons`** — weapon ids layered while the buff is up (stripped on expiry).
- **`incapacitating`** + `incapHoldAnim`/`incapRecoverAnim`/`incapRecoverTime`/`incapHoldAtEnd` —
  knockdown/stun family.
- **Visual pairs** — each visual is `hasX: true` + config object; forgetting the `hasX` flag makes
  the config object a silent no-op: `hasOrbital`/`orbital`, `hasGroundAura`/`groundAura`,
  `hasBehindEffect`/`hasFrontEffect` (`UprightEffectVisual`, `pinToEffectSpawn`),
  `hasLightningAura`, `hasImageBehind`, `hasPulsingOutline`, `hasWeaponParticle`/`weaponParticle`,
  `unitTint`. Flipbook ids reference `data/flipbooks.json`.
- **`weaponParticle.attachedFlame: true`** — switches to the one-persistent-flame mode: the flame
  is repinned to the live weapon hilt→tip every frame at t=`rangeMax`; in this mode
  `spawnRate`/`particleLifetime`/`moveSpeed`/`moveDir*`/`rangeMin`/`renderBehind`/`color.a` are
  **ignored** (the buff_4* entries still carry dead values). Draws via its own HdrAdditive queue
  items, not the phase-0/1 DrawUnit path.

## The casting-effect buffs (buff_4 family)

`buff_4` (purple) / `buff_4_copy` (green, also the craft-table channel glow
`TableChannelBuffId`) / `buff_4_copy_copy` (yellow, "CastingEffect Lightning" — Lightning Beam's
`castingBuffID`). A buff is treated as a casting effect iff some spell's `castingBuffID`
references it (`Game1.Spells.cs` `IsCastingBuff` — registry scan, NOT the hasWeaponParticle flag).

Lifecycle asymmetry (matters when tuning `duration` on these):
- **Player casts**: applied in `DispatchSpellCast`, explicitly removed by `RemoveCastingBuffAll`
  at every cast-end path (`Game1.Animation.cs` channel machine + Spell1-end + hard interrupt) and
  by `CancelPlayerChannel` when a Beam/Drain hold-channel ends (`Game1.cs` channel-hold block).
  ⇒ for the player, `duration` only matters if it's SHORTER than the cast/channel (buff pops
  mid-cast via TickBuffs).
- **AI casts** (`DrainAISpellCasts` in `Game1.Spells.cs`): applied with the def duration and
  **relies on expiry** — nothing removes it (`CasterUnitHandler.CancelChannel` does NOT touch
  buffs). ⇒ `duration <= 0` on a casting buff leaks a permanent glow on AI casters unless the AI
  path is changed to apply a finite override duration (`ApplyBuffWithDuration`, e.g. mirroring
  `SpellEffectSystem.StartChannel`'s channel length: DrainMaxDuration → CastTime → 4f).

No fade-out exists: buff visuals vanish the frame the buff is removed (only weapon-particle
spawn-mode particles fade individually via their own lifetime).

Related: [render.md](render.md) buff-visual internals; overview.md "Buffs" index entry;
[json-spells.md](json-spells.md) for `castingBuffID` on spells.
