# Spells — how `Game1.Spells.cs` works

Reference for adding or changing spells. **Read this before implementing a spell.**
Most spells are pure data (a `SpellDef` in `data/spells.json`) and need no code at
all — code only changes when you add a new *category* or *behavior* the data model
can't express yet.

## The three layers

A spell flows through three files. Know which one you need to touch:

| Layer | File | Owns |
|-------|------|------|
| **Data / definition** | [`Necroking/Data/Registries/SpellRegistry.cs`](../Necroking/Data/Registries/SpellRegistry.cs) | `SpellDef` — every field of a spell, loaded from `data/spells.json`. Also the spell-editor schema (via `[EditorField]`/`[EditorVisible]` attrs) and the `Build*Style()` helpers shared by game + editor preview. |
| **Targeting / start** | [`Necroking/Game/SpellCasting.cs`](../Necroking/Game/SpellCasting.cs) | `SpellCaster.TryStartSpellCast` — validates a cast (mana, cooldown, path requirements, range), resolves the target (corpse / unit / point) per category, fills a `PendingSpellCast`, then deducts mana + starts cooldown. Returns a `CastResult`. |
| **Effect / payoff** | [`Necroking/Game/SpellEffectSystem.cs`](../Necroking/Game/SpellEffectSystem.cs) | `SpellEffectSystem.Execute(spell, game, casterIdx, target, slot)` — a static class holding the `switch (spell.Category)` that actually *does* the thing (spawn projectile, apply buff, fire beam, summon, blight, etc.). **All** category logic lives here, not in Game1. It takes `Game1` directly (same pattern as `GameRenderer`/`ForagableSystem`) and reaches Game1-owned state through it — `game._deathFog`, `game._channelingSlot`, `game._pendingProjectiles`, `game.QueueReanimRise(...)`, `game.SpawnUnit(...)`. |

[`Necroking/Game1.Spells.cs`](../Necroking/Game1.Spells.cs) is the **orchestrator /
glue**. It sits between input and those systems and owns the cast pipeline plus the
queues it ticks each frame (`_pendingProjectiles`, `_pendingReanimRises`) and small
effect-manager helpers. It does *not* contain category effect logic — that's
delegated to `SpellEffectSystem`.

## The cast pipeline (what happens on a click)

`Game1.Spells.cs` → `DispatchSpellCast(spellId, necroIdx, slot, mouseWorld, isSecondary)`
is the single entry point for both spell bars. In order:

1. **Built-in abilities** (`TryDispatchBuiltinAbility`) short-circuit first. These are
   hard-wired IDs *not* in `spells.json`: `melee_gather`, `command` (command
   horde), `regroup`, and the `poison_berries_*` abilities. They bypass `SpellCaster`
   entirely. If you're adding a hotkey-style action that isn't really a spell, add it
   here.
2. **Potion-spells** (`ConsumesItem` set on the def) route to `CastPotionSpell` →
   `PotionSystem` (drink if cursor is on the necromancer, else throw), consuming the
   inventory item. Not the normal pipeline.
3. **Real spells** call `SpellCaster.TryStartSpellCast` (the *targeting/start* layer).
   On failure it returns a `CastResult` (`OutOfRange`, `NotEnoughMana`,
   `HordeCapFull`, …) and we stop. On success, mana is spent and cooldown started by
   `SpellCaster`.
4. The slot flashes (`FlashSpellSlot`) and a `CastSpell` player-event is tallied (for
   skill-book milestones).
5. **When does the effect fire?** Three paths, decided by the def's `CastAnim` /
   `CastingBuffID`:
   - **Channeled** (`CastAnim` is `ImbueGround`/`Raise` → `IsChanneledCast`): a
     Start→Loop→Finish animation state machine runs in `Game1.Animation.cs`
     (`TickPendingCastAnim`); the effect fires at the **end of the loop**.
   - **Deferred** (has a `CastingBuffID`, normal `Spell1` anim): the casting buff is
     applied, the `Spell1` animation requested, and the effect fires at the anim's
     **effect frame** (also driven from `Game1.Animation.cs`, which calls back into
     `ExecuteSpellEffect`).
   - **Immediate** (no casting buff): `ExecuteSpellEffect` is called right away.
6. `ExecuteSpellEffect` (in `Game1.Spells.cs`) spawns the cast flipbook at the caster,
   then calls `SpellEffectSystem.Execute(spell, this, casterIdx, target, slot, pending)`
   (the *effect* layer). No callbacks, no result struct: the system writes Game1-owned
   results directly (`game._channelingSlot` for the player's Beam/Drain key-hold,
   `game._pendingProjectiles` for multi-shot groups) and enqueues rises via
   `game.QueueReanimRise`. `pending` is THIS cast's targeting record — the player's
   `_pendingSpell` (it must survive a multi-second cast anim), or an AI cast's scratch.

So: **targeting** is in `SpellCaster`, **all effects** are in `SpellEffectSystem`, and
`Game1.Spells.cs` wires them together + ticks the deferred queues.

## AI casters use the same pipeline

`SpellCaster.TryStartSpellCast`/`RefundSpellCast` take a `Movement.ICasterResources`
(mana + per-spell cooldowns): `NecromancerState` for the player, `UnitCasterResources`
(over `Unit.Mana` + the lazily-allocated `Unit.SpellCooldowns` dict) for AI units, and
all targeting is caster-faction-aware ("ally" = same faction as the caster). AI
archetype handlers (`AI/CasterUnitHandler.cs`) enqueue an `AISpellCastRequest`
(`AIContext.SpellCasts` → `Simulation.PendingAISpellCasts`) during the AI pass;
`Game1.DrainAISpellCasts` runs it through steps 3 and 6 above right after the tick
(slot = -1, immediate execution — no cast anims/plant, no slot flash, no player-event
tally, no horde caps). AI Beam/Drain channels are held by `Unit.ChannelTimer` (armed in
`SpellEffectSystem.StartChannel`, ticked/cancelled by the handler) since an AI has no
key to release. Test with the dev command `set_spell <selector> <spellID>`. A spell you
add per this doc is castable by any AI caster unit (set `spellID` + `maxMana` on its
UnitDef) with no extra code.

## What lives directly in `Game1.Spells.cs`

- **`TickPendingProjectiles`** — fires the staggered extra projectiles for
  multi-shot spells (`Quantity > 1`, `ProjectileDelay`), re-anchoring origin on the
  necromancer each shot (calls `SpellEffectSystem.SpawnProjectile` per shot).
- **`QueueReanimRise` / `TickPendingReanimRises`** — the deferred reanimation-rise
  queue (effect plays at the grave now, unit spawns after a delay). `internal` —
  `SpellEffectSystem`'s summon logic enqueues through it.
- **`ApplyBlightBombImpacts`** — reads projectile impacts each tick and applies the
  deferred fog change for thrown Blight bombs (calls `SpellEffectSystem.ApplyBlight`).
- **Cast/summon flipbook spawners** (`SpawnCastEffect`, `SpawnSummonEffect`),
  `FlashSpellSlot`, casting-buff cleanup (`RemoveCastingBuffAll`).
- **Built-in abilities**: `TryCommandHorde` (command), `TryRegroupHorde`,
  `TryStartPoisonBerries`, and the `PoisonBerryAbilities` table +
  `ValidatePotionAbilities` load-time guard.

## How to add a spell

**Most spells = data only.** If your spell fits an existing category, just add a
`SpellDef` to `data/spells.json` (or use the in-game spell editor — `panel
spell_editor` via the dev server). Fields are documented inline in `SpellRegistry.cs`.
The key field is **`category`**, one of:

`Projectile` · `Buff` · `Debuff` · `Summon` · `Strike` · `Beam` · `Drain` · `Cloud` ·
`Sacrifice` · `Blight` (mutate the death-fog field — Add dumps blight, Purify cleanses
a 5×5 kernel; applied through `game._deathFog` via `SpellEffectSystem.ApplyBlight`) ·
`Toggle` (internal)

Each category reads a different subset of fields (the editor's `[EditorVisible]` attrs
show which). Common fields: `range`, `manaCost`, `cooldown`, `damage`, `aoeRadius`,
`primaryPath`/`primaryLevel` (+ optional secondary) for path gating + mana-cost
mastery scaling, `castingBuffID`, `castAnim`, knockback (`knockbackForce/Up/Radius`),
and the various flipbook refs.

**A new category or behavior = code.** Touch all three layers in order:
1. `SpellRegistry.cs` — add the field(s) and, if a new category, the `[EditorCombo]`
   entry on `Category` + `[EditorVisible("Category", "…")]` on your fields.
2. `SpellCasting.cs` — add a `case "YourCategory":` in `TryStartSpellCast` to validate
   range/target and fill `PendingSpellCast`.
3. `SpellEffectSystem.cs` — add the matching `case "YourCategory":` in `Execute` to
   produce the effect. It has the `Game1` instance — reach Game1-owned state via
   `game.*` (widen the member to `internal` if it's private), the way Summon/Blight/
   Beam already do. No callbacks, no result structs.

Keep the **North Star** in mind (see `CLAUDE.md`): the effect must *show* its payoff —
visible, physical, audible — in proportion to the cast's wind-up.

## Verifying a spell

Use the dev server (`CLAUDE.md` → Dev Control Server). `cast <spellID> <x> <y>` runs
the full player pipeline (`set_mana necro 9999` first to dodge mana/cooldown), or
`panel spell_editor` + `select <name>` to inspect a def. Screenshot the impact to
confirm the payoff lands.

**Any visual effect: also run the zoom check** — pause mid-effect, screenshot at zoom
8/32/128; px-authored values scale by `Zoom/32`, offsets/anchors included. Checklist +
staging recipes: [vfx-zoom-audit.md](vfx-zoom-audit.md).
