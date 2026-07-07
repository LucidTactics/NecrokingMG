# Dossier: Spell/ability casting pipelines (player vs NPC vs traps vs skills)

Judge pass over the labeling evidence. All line refs verified on disk 2026-07-06.

## Pipelines mapped end-to-end

### A. Player (necromancer) — the documented canonical pipeline
Per `docs/spells.md` ("three layers"), the intended architecture is:

1. **Entry** — `Game1.Spells.cs:237 DispatchSpellCast` (spell bar + dev `cast`).
   Short-circuits: built-ins (`:351 TryDispatchBuiltinAbility` — `melee_gather`,
   `command`, `regroup`, `poison_berries_*`) and potion-spells (`ConsumesItem` →
   `CastPotionSpell` → PotionSystem).
2. **Targeting/start** — `Game/SpellCasting.cs:43 SpellCaster.TryStartSpellCast`
   (413L): cooldown (NecroState.SpellCooldowns dict), path gate
   (`SpellDef.MeetsPathRequirements` via `BuffSystem.EffectivePathLevel`), scaled
   mana (`SpellDef.EffectiveManaCost` vs `NecroState.Mana`), per-category
   cursor-driven target resolution (closest-to-cursor unit/corpse), horde-cap
   pre-check, then deducts mana + starts cooldown. Refund path
   (`RefundSpellCast`) for hard interrupts.
3. **Timing** — immediate, deferred-to-anim-frame, or channeled
   (`Game1.Animation.cs:153 UpdateChanneledCast`, Start→Loop→Finish phase machine
   + `TickCastPlant` brake/plant + interrupt-refund).
4. **Effect** — `Game1.Spells.cs:165 ExecuteSpellEffect` →
   `Game/SpellEffectSystem.cs:55 Execute` — the single switch-on-category payoff
   layer (Projectile/Buff/Strike/Summon/Beam/Drain/Sacrifice/Cloud/WolfHunt/
   Blight/Toggle). Writes Game1-owned state directly (`_pendingProjectiles`,
   `_channelingSlot`, `QueueReanimRise`, `_deathFog`, `_damageNumbers`).

### B. NPC caster — `AI/CasterUnitHandler.cs:209 TryCast` (priest etc.)
Fully parallel mini-pipeline inside the AI handler:
- **Gates duplicated from SpellCaster**: cooldown (but `Unit.SpellCooldownTimer`
  float, not the dict), path gate + `EffectiveManaCost` (same SpellDef helpers,
  duplicated `casterLevel` lambda), mana (but `Unit.Mana` pool, not NecroState).
- **Effect duplicated from SpellEffectSystem.ExecuteStrike**: builds strike
  style/visual/filter itself and calls `Lightning.SpawnZap` / `SpawnStrike`
  directly. **Never reads `spell.Category`** — every NPC spell is executed as a
  Strike; a priest given a Cloud/Projectile/Buff def would sky-strike.

### C. Traps — `Game1.cs:3956 ProcessTrapFireEvents`
Third parallel effect dispatch: hand-rolled `Strike` zap and ground-strike
branches, plus a `Cloud` branch that (correctly) delegates to
`SpellEffectSystem.ExecuteCloud` — proving the delegation direction works.

### D. Skill effects — `Game/SkillEffects/SkillEffects.cs:61 SkillEffectRegistry.Apply`
NOT a casting pipeline. One-time on-learn unlock dispatch (add spell to bar,
unlock potion, morph, grant path...). Labeler over-match.

---

## Finding 1 — Strike-spell execution is triplicated and has ALREADY diverged
**Verdict: CONSOLIDATE · Severity: high**

Three implementations of "execute a Strike-category spell (zap or sky-strike)":

| Behavior | Player `SpellEffectSystem.ExecuteStrike` (SpellEffectSystem.cs:471) | NPC `CasterUnitHandler.TryCast` (CasterUnitHandler.cs:209) | Trap `ProcessTrapFireEvents` (Game1.cs:3956) |
|---|---|---|---|
| Magic-resist gate | `SpellPenetration.Affects` (:496) | **none** | **none** |
| Damage call (zap) | `sim.DealDamage(enemy, dmg, casterIdx)` | `DamageSystem.Apply(..., DamageType.Physical, DamageFlags.ArmorNegating, ..., i)` — **always armor-negating** | `sim.DealDamage(idx, dmg)` — **no attacker attribution** (trap owner never credited; evt.TrapOwner exists but is unused) |
| Damage number | yes | **no** | yes |
| Zap target height | `SpriteWorldHeight * 0.5` (**ignores SpriteScale**) | `SpriteWorldHeight * SpriteScale * 0.5` | `SpriteWorldHeight * 0.5` |
| ZapDuration fallback | `spell.StrikeDuration` | none (raw ZapDuration) | `0.2f` |
| SpawnStrike extras | GodRay params + TargetFilter + caster uid | GodRay params + TargetFilter + caster uid | **omits GodRay params and TargetFilter** — a filtered/god-ray strike def fired by a trap loses its filter and visual |

These are not cosmetic: MR penetration, armor negation, kill attribution, and
target filtering are gameplay rules that currently depend on *who* cast the
spell, not on the spell def. Every future fix to strike behavior must be made
three times or it silently diverges further (it already has).

**Canonical home**: `SpellEffectSystem.ExecuteStrike` — make it `public` and
caster-source-agnostic. It already takes `(spell, sim, gameData, casterIdx,
target, effectOrigin, damageNumbers)`; the only player-specific inputs are
`effectOrigin` and `casterIdx`. Sketch:

```csharp
// SpellEffectSystem — one strike executor for player / NPC / trap
public static void ExecuteStrike(SpellDef spell, Simulation sim, GameData gameData,
    int casterIdx,                 // -1 for casterless sources (traps)
    uint attackerUid,              // attribution: unit id or trap owner
    Vec2 effectOrigin, float originHeight,   // hand pos / trap pos
    Vec2 target, Faction casterFaction,
    List<DamageNumber> damageNumbers)
```
- Zap branch: one target-height formula (include SpriteScale — the NPC version
  is the correct one), one MR gate (`SpellPenetration.Affects`; casterIdx<0 →
  base penetration), one attributed damage call, one damage number.
- Strike branch: always pass GodRay params + TargetFilter + attacker uid.

**Call sites to migrate** (3 categories):
1. `SpellEffectSystem.Execute` case "Strike" / "Blight"-GodRay (internal, trivial).
2. `CasterUnitHandler.TryCast` — replace its zap/strike body with the call;
   keep its gates (see Finding 2). Decide deliberately: NPC zaps gain the MR
   gate and lose unconditional ArmorNegating (flags should come from the def
   via `SpellDamageFlags`, which already exists at SpellEffectSystem.cs:538).
3. `Game1.ProcessTrapFireEvents` Strike branches — pass `evt.TrapOwner` as
   attribution and the trap's faction; Cloud branch already delegates.

**Effort: M** (one afternoon incl. behavior reconciliation + test-scenario runs).
**Risk: medium** — three balance-relevant behavior changes (NPC MR gate, NPC
damage flags, trap attribution) must each be an explicit decision, not an
accident of the merge; verify with `/drive-game` (priest vs skeleton, trap
zap, player zap spell).

## Finding 2 — Player vs NPC cast validation: same gates, two resource models
**Verdict: INVESTIGATE · Severity: medium**

`SpellCaster.TryStartSpellCast` (SpellCasting.cs:43) and
`CasterUnitHandler.TryCast` (CasterUnitHandler.cs:209) both implement
cooldown-check → path-gate → effective-mana-check → deduct → start-cooldown →
apply CastingBuffID. The formulas are already shared (SpellDef.
MeetsPathRequirements / EffectiveManaCost, BuffSystem.EffectivePathLevel), so
the duplicated *code* is thin — but the duplicated *pipeline stage* is real,
and it is exactly the user's example: the priest and the necromancer do not
share a "unit casts spell" gate.

Why this is INVESTIGATE and not a straight CONSOLIDATE: the two casters store
resources differently, and that's a design decision someone must make first:
- Necromancer: `NecroState.Mana` + `NecroState.SpellCooldowns` (per-spell dict,
  multi-spell bar) + refund path + horde-cap pre-check + cursor targeting.
- NPC unit: `Unit.Mana`/`MaxMana`/`ManaRegen` + single `SpellCooldownTimer`
  float (one spell per unit) + AI-chosen target (no cursor resolution).

**The decision to name**: either (a) move the necromancer's mana/cooldowns onto
its unit record so there is one resource model and one
`SpellGate.TryPay(units, idx, spell, gameData)` used by both, or (b) keep two
stores and extract only the shared gate/pay/refund into a helper taking a small
resource interface. (a) is the real fix ("casting is an effect, not a
command") but touches HUD, save format, and NecroState consumers; (b) is cheap
but leaves two cooldown models. Also prerequisite for full sharing: making
`SpellEffectSystem.Execute` caster-agnostic — today it hardcodes
`Faction.Undead` in `SpawnProjectile` (:374) and `ExecuteCloud` (:518), and
`TickPendingProjectiles` re-anchors staggered shots on `FindNecromancer()`
(Game1.Spells.cs:506), so a priest could not currently cast Projectile/Cloud
through it correctly. Cursor-vs-AI target *resolution* should stay split —
that part is structural variance (resolve-intent-from-cursor vs
target-already-chosen), per CLAUDE.md's "shared component owns mechanics,
caller owns data".

Effort if pursued: L for option (a), S–M for option (b).

## Finding 3 — SkillEffectRegistry is not a casting pipeline
**Verdict: KEEP_SEPARATE · Severity: low**

`SkillEffects.cs:61 Apply` dispatches one-time unlock effects when a skill-book
node is learned (unlock_potion, add_spell to bar, morph_necromancer,
grant_path, ...). It never validates mana/targets or produces combat effects at
cast time; its `add_spell`/`grant_path` effects *feed* the spell pipeline's
inputs. Labeler over-match — no shared intent with casting dispatch.

## Finding 4 — Channeled-cast anim machine and built-in abilities are player-presentation, not duplication
**Verdict: KEEP_SEPARATE · Severity: low**

- `Game1.Animation.cs:153 UpdateChanneledCast` (+ `TickCastPlant`) is the only
  channel phase machine in the codebase; NPCs and traps have no wind-up
  channel. It is a stage of pipeline A, not a parallel copy of anything.
- `Game1.Spells.cs:351 TryDispatchBuiltinAbility` is a documented, intentional
  bypass (docs/spells.md §1: "hotkey-style actions that aren't really spells")
  — hardcoded ids like `command`/`regroup` issue horde orders, not spell
  effects. Same for the potion-spell branch (routes to PotionSystem).
  Structural variance (different control flow, no SpellDef effect semantics);
  abstracting it would create a framework, not a utility.

---

## Suggested sequencing
1. Finding 1 (ExecuteStrike unification) — standalone, highest payoff, no
   design decision blocked on it beyond the three named behavior choices.
2. Finding 2 — decide resource-model question first (option a vs b); the
   Faction/pending-projectile parameterization of SpellEffectSystem can ride
   along and unlocks NPC casters for all categories (fixes the "priest with a
   Cloud spell sky-strikes" latent bug).

Constraints respected: nothing here touches `Necroking/Net/`, the renderer, or
map JSON content.
