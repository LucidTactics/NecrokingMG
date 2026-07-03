---
name: add-spell
description: Add a new spell, change an existing one, or add a new spell category/behavior in the Necroking MonoGame game, covering the data model, the three-layer code split, the cast pipeline, and how to test casts. Use when / Triggers — "add a spell", "change/tweak a spell", "new spell category/behavior", "how do spells work", "implement a spell effect", or any work touching SpellRegistry/SpellCasting/SpellEffectSystem/Game1.Spells.cs.
---

# Add or change a spell

Most spells are **pure data** — a `SpellDef` in `data/spells.json`, no code at all.
Code only changes when you add a new *category* or *behavior* the data model can't
express yet.

## The three layers

A spell flows through three files. Know which one you need to touch:

| Layer | File | Owns |
|-------|------|------|
| **Data / definition** | `Necroking/Data/Registries/SpellRegistry.cs` | `SpellDef` — every field of a spell, loaded from `data/spells.json`. Also the spell-editor schema (`[EditorField]`/`[EditorVisible]` attrs) and the `Build*Style()` helpers shared by game + editor preview. |
| **Targeting / start** | `Necroking/Game/SpellCasting.cs` | `SpellCaster.TryStartSpellCast` — validates a cast (mana, cooldown, path reqs, range), resolves the target (corpse / unit / point) per category, fills a `PendingSpellCast`, then deducts mana + starts cooldown. Returns a `CastResult`. |
| **Effect / payoff** | `Necroking/Game/SpellEffectSystem.cs` | `SpellEffectSystem.Execute` — the `switch (spell.Category)` that actually *does* the thing (spawn projectile, apply buff, fire beam, etc.). Category logic lives **here**, not in Game1. |

`Necroking/Game1.Spells.cs` is the **orchestrator / glue** between input and those
systems. It owns only the parts that need Game1's own state (animation controllers,
the effect manager, inventory, pending-projectile queues). It does *not* contain
category effect logic — that's delegated to `SpellEffectSystem`.

## The cast pipeline (what happens on a click)

`Game1.Spells.cs` → `DispatchSpellCast(spellId, necroIdx, slot, mouseWorld, isSecondary)`
is the single entry point for both spell bars. In order:

1. **Built-in abilities** (`TryDispatchBuiltinAbility`) short-circuit first — hard-wired
   IDs *not* in `spells.json`: `melee_gather`, `order_attack`, `regroup`, the
   `poison_berries_*` abilities. They bypass `SpellCaster`. Add hotkey-style actions
   that aren't really spells here.
2. **Potion-spells** (`ConsumesItem` set on the def) route to `CastPotionSpell` →
   `PotionSystem` (drink if cursor is on the necromancer, else throw), consuming the
   inventory item.
3. **Real spells** call `SpellCaster.TryStartSpellCast`. On failure it returns a
   `CastResult` (`OutOfRange`, `NotEnoughMana`, `HordeCapFull`, …) and we stop. On
   success, mana is spent and cooldown started by `SpellCaster`.
4. The slot flashes (`FlashSpellSlot`) and a `CastSpell` player-event is tallied.
5. **When the effect fires** — three paths, decided by the def's `CastAnim` /
   `CastingBuffID`:
   - **Channeled** (`CastAnim` is `ImbueGround`/`Raise` → `IsChanneledCast`): a
     Start→Loop→Finish state machine runs in `Game1.Animation.cs`
     (`TickPendingCastAnim`); effect fires at the **end of the loop**.
   - **Deferred** (has a `CastingBuffID`, normal `Spell1` anim): casting buff applied,
     `Spell1` anim requested, effect fires at the anim's **effect frame** (also driven
     from `Game1.Animation.cs`, which calls back into `ExecuteSpellEffect`).
   - **Immediate** (no casting buff): `ExecuteSpellEffect` is called right away.
6. `ExecuteSpellEffect` (in `Game1.Spells.cs`) spawns the cast flipbook at the caster,
   then delegates to `_spellEffects.Execute(...)`, passing callbacks for Game1-coupled
   bits: `SpawnSpellProjectile` and `ExecuteSummonSpell`. It then applies the returned
   `SpellEffectResult` (channeling slot, pending multi-projectile group).

So: **targeting** is in `SpellCaster`, **effects** in `SpellEffectSystem`,
`Game1.Spells.cs` wires them together + owns the two effects too coupled to extract.

### What lives directly in `Game1.Spells.cs`

- **`ExecuteSummonSpell`** — full summon/reanimation (touches `SpawnUnit`, the horde,
  corpse consumption, unit anim rebuild). Handles every `SummonTargetReq`
  (`None`/`Corpse`/`UnitType`/`CorpseAOE`) and `SummonMode` (`Spawn`/`Transform`),
  zombie-type resolution via `TableCraftingSystem.ResolveZombieUnitID`, horde-cap clamp.
- **`SpawnSpellProjectile`** — fireball-style projectile + the def's `Trajectory`
  (Lob/DirectFire/Homing/Swirly/HomingSwirly), speed, projectile/hit flipbooks; tags
  with spell id for impact knockback lookup.
- **`TickPendingProjectiles`** — staggered extra shots for multi-shot spells
  (`Quantity > 1`, `ProjectileDelay`), re-anchoring origin on the necromancer each shot.
- **Cast/summon flipbook spawners** (`SpawnCastEffect`, `SpawnSummonEffect`),
  `FlashSpellSlot`, casting-buff cleanup (`RemoveCastingBuffAll`).
- **Built-in abilities**: `TryCommandHorde`, `TryRegroupHorde`, `TryStartPoisonBerries`,
  the `PoisonBerryAbilities` table + `ValidatePotionAbilities` load-time guard.

## How to add a spell

### Data-only (the common case)

If your spell fits an existing category, just add a `SpellDef` to `data/spells.json`
— use the **edit-game-data skill** (`/edit-game-data`) to create/clone the struct by
id, or the in-game spell editor (`panel spell_editor` via the dev server). Fields are
documented inline in `SpellRegistry.cs`.

The key field is **`category`**, one of:

`Projectile` · `Buff` · `Debuff` · `Summon` · `Strike` · `Beam` · `Drain` · `Cloud` ·
`Sacrifice` · `Blight` (mutate the death-fog field — Add dumps blight, Purify cleanses
a 5×5 kernel; wired via the `applyBlight` callback into `DeathFogSystem`) · `Toggle`
(internal)

Each category reads a different subset of fields (the editor's `[EditorVisible]` attrs
show which). Common fields: `range`, `manaCost`, `cooldown`, `damage`, `aoeRadius`,
`primaryPath`/`primaryLevel` (+ optional secondary) for path gating + mana-cost mastery
scaling, `castingBuffID`, `castAnim`, knockback (`knockbackForce/Up/Radius`), and the
various flipbook refs.

### New category or behavior (touch all three layers, in order)

1. **`SpellRegistry.cs`** — add the field(s) and, for a new category, the
   `[EditorCombo]` entry on `Category` + `[EditorVisible("Category", "…")]` on your
   fields.
2. **`SpellCasting.cs`** — add a `case "YourCategory":` in `TryStartSpellCast` to
   validate range/target and fill `PendingSpellCast`.
3. **`SpellEffectSystem.cs`** — add the matching `case "YourCategory":` in `Execute`.
   If it needs Game1-only state, return it via `SpellEffectResult` or take a callback
   (the projectile/summon pattern) rather than reaching into Game1.

**North Star:** the effect must *show* its payoff — visible, physical, audible — in
proportion to the cast's wind-up. See `docs/north-star.md`.

## Testing a spell

Drive the running game via the dev server (see the **drive-game skill**).

**Launch the EMPTY test map for technical behavior**, not the regular map or
`testmap`:

```js
window.dev('start_game')   // no arg → the empty_test map
```

This synthesizes a grass-only 64×64 grid in code (no JSON file) and spawns a
**`necromancer_debug`** chassis with every magic path at level 9 and `maxMana=999` —
so any spell is castable and no map content gets in the way. The primary spellbar is
pre-seeded with the hidden no-path `test_projectile` (range 15, 5 mana, 0.5s CD) for
exercising OutOfRange / NotEnoughMana / OnCooldown feedback. Use
`set_necro_type <unitDefId>` to swap chassis (e.g. `wretched`, `wight`) for
chassis-specific checks.

Regular maps ship with an empty spellbar by design (pre-seeding would break the
intended new-game experience). The older `testmap` (populated, normal `necromancer`,
loaded via `start_game testmap`) is useful when you need realistic terrain/enemies —
pick the empty test map for isolation, `testmap` for context.

**If a test needs more starter spells**, add another hidden no-path spell to
`spells.json` and slot it in the same
`if (mapName == "testmap" || mapName == "empty_test")` block in `StartGame`. Don't
touch `data/spellbar.json` — it's per-machine and gitignored.

**Casting a spell:**
- `cast <spellID> <x> <y>` runs the full player pipeline (`set_mana necro 9999` first
  to dodge mana/cooldown).
- `panel spell_editor` + `select <name>` inspects a def's preview.
- Screenshot the impact to confirm the payoff lands.
