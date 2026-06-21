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
| **Effect / payoff** | [`Necroking/Game/SpellEffectSystem.cs`](../Necroking/Game/SpellEffectSystem.cs) | `SpellEffectSystem.Execute` — the `switch (spell.Category)` that actually *does* the thing (spawn projectile, apply buff, fire beam, etc.). Category logic lives **here**, not in Game1. |

[`Necroking/Game1.Spells.cs`](../Necroking/Game1.Spells.cs) is the **orchestrator /
glue**. It sits between input and those systems and owns only the parts that need
Game1's own state (animation controllers, the effect manager, the inventory,
pending-projectile queues). It does *not* contain category effect logic — that's
delegated to `SpellEffectSystem`.

## The cast pipeline (what happens on a click)

`Game1.Spells.cs` → `DispatchSpellCast(spellId, necroIdx, slot, mouseWorld, isSecondary)`
is the single entry point for both spell bars. In order:

1. **Built-in abilities** (`TryDispatchBuiltinAbility`) short-circuit first. These are
   hard-wired IDs *not* in `spells.json`: `melee_gather`, `order_attack` (command
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
   then delegates to `_spellEffects.Execute(...)` (the *effect* layer), passing
   callbacks for the bits that need Game1 state: `SpawnSpellProjectile` and
   `ExecuteSummonSpell`. It then applies the returned `SpellEffectResult` (channeling
   slot, pending multi-projectile group).

So: **targeting** is in `SpellCaster`, **effects** are in `SpellEffectSystem`, and
`Game1.Spells.cs` wires them together + owns the two effects too coupled to extract.

## What lives directly in `Game1.Spells.cs`

- **`ExecuteSummonSpell`** — the full summon/reanimation logic (kept in Game1 because
  it touches `SpawnUnit`, the horde, corpse consumption, unit anim rebuild). Handles
  every `SummonTargetReq` (`None`/`Corpse`/`UnitType`/`CorpseAOE`) and `SummonMode`
  (`Spawn`/`Transform`), zombie-type resolution from a corpse via
  `TableCraftingSystem.ResolveZombieUnitID`, and horde-cap clamping.
- **`SpawnSpellProjectile`** — spawns a fireball-style projectile and applies the
  def's `Trajectory` (Lob/DirectFire/Homing/Swirly/HomingSwirly), speed, and the
  projectile/hit-effect flipbooks. Tags the projectile with the spell id for impact
  knockback lookup.
- **`TickPendingProjectiles`** — fires the staggered extra projectiles for
  multi-shot spells (`Quantity > 1`, `ProjectileDelay`), re-anchoring origin on the
  necromancer each shot.
- **Cast/summon flipbook spawners** (`SpawnCastEffect`, `SpawnSummonEffect`),
  `FlashSpellSlot`, casting-buff cleanup (`RemoveCastingBuffAll`).
- **Built-in abilities**: `TryCommandHorde` (order_attack), `TryRegroupHorde`,
  `TryStartPoisonBerries`, and the `PoisonBerryAbilities` table +
  `ValidatePotionAbilities` load-time guard.

## How to add a spell

**Most spells = data only.** If your spell fits an existing category, just add a
`SpellDef` to `data/spells.json` (or use the in-game spell editor — `panel
spell_editor` via the dev server). Fields are documented inline in `SpellRegistry.cs`.
The key field is **`category`**, one of:

`Projectile` · `Buff` · `Debuff` · `Summon` · `Strike` · `Beam` · `Drain` · `Cloud` ·
`Sacrifice` · `Blight` (mutate the death-fog field — Add dumps blight, Purify cleanses
a 5×5 kernel; effect wired via the `applyBlight` callback into `DeathFogSystem`) ·
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
   produce the effect. If it needs Game1-only state, return it via `SpellEffectResult`
   or take a callback (the pattern used for projectiles/summons), rather than reaching
   into Game1.

Keep the **North Star** in mind (see `CLAUDE.md`): the effect must *show* its payoff —
visible, physical, audible — in proportion to the cast's wind-up.

## Verifying a spell

Use the dev server (`CLAUDE.md` → Dev Control Server). `cast <spellID> <x> <y>` runs
the full player pipeline (`set_mana necro 9999` first to dodge mana/cooldown), or
`panel spell_editor` + `select <name>` to inspect a def. Screenshot the impact to
confirm the payoff lands.
