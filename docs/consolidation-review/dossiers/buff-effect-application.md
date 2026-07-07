# Dossier: Buff/status/modifier application

Concept unit: `buff-effect-application`
Judge pass over: `Necroking/Game/BuffSystem.cs`, `Necroking/Game/PotionSystem.cs`,
`Necroking/Game/SkillEffects/SkillEffects.cs`, `Necroking/Game/WeaponBonusEffect.cs`,
`Necroking/Game/DamageSystem.cs`, `Necroking/Game/Simulation.cs` (melee-hit block),
`Necroking/Game/TableCraftingSystem.cs`, `Necroking/Movement/UnitModel.cs`.

## Overall answer to the question

BuffSystem **is** the single home for *data-driven, stat-modifying* timed effects, and
skills/potions/spells all correctly express through it when the effect is a stat modifier
(42 `ApplyBuff*` call sites across 19 files all funnel into `ApplyBuffWithDuration`).
The real duplication is *around* the edges:

1. Inside BuffSystem itself, the modifier math is written twice (enum vs string key).
2. There are **two** attacker-side "timed on-hit weapon effect" systems (potion coats vs
   `WeaponBonusEffect`), and the newer one's expiry path is dead code.
3. Paralysis hand-rolls a second incapacitation state machine parallel to BuffSystem's
   `Incapacitating` buff path, with two independent writers to `Unit.Incap`.

The wrapper chain and the hit-react/dodge appliers flagged by the labeler are *healthy
single-choke-point design*, not duplication.

---

## Finding 1 — Duplicate modifier math: `GetModifiedStat` vs `GetModifiedExtra`
**Verdict: CONSOLIDATE — severity medium — effort S — risk low**

Evidence:
- `Necroking/Game/BuffSystem.cs:457-475` (`GetModifiedExtra`, string-keyed) and
  `Necroking/Game/BuffSystem.cs:538-560` (`GetModifiedStat` core, enum-keyed) contain the
  *identical* combination formula: `setValue ?? (base + ΣAdd·stacks) × ∏Multiply^stacks`,
  including the `MathF.Pow(value, stackCount)` stacking rule and last-Set-wins semantics.
- `GetModifiedStat(BuffStat, …)` already does `stat.ToString()` at line 540 before running
  the same loop — the enum is purely a compile-time-safety veneer over the same string
  match (`eff.Stat != statName`).

Why it matters: this is core gameplay math. If Set semantics, stacking rules, or
clamping ever change in one loop and not the other, unit stats (enum path: Attack,
Defense, MaxHP, CombatSpeed…) and necromancer resources (string path: MaxMana,
CooldownRate, MaxAcceleration…) silently diverge.

Canonical design (internal-only refactor, zero call-site changes):
```csharp
// private shared core in BuffSystem
private static float ModifyCore(IReadOnlyList<ActiveBuff> buffs, string statName, float baseValue) { …existing loop… }
public static float GetModifiedStat(IReadOnlyList<ActiveBuff> buffs, BuffStat stat, float baseValue)
    => ModifyCore(buffs, stat.ToString(), baseValue);
public static float GetModifiedExtra(UnitArrays units, int unitIdx, string stat, float baseValue)
    => (unitIdx < 0 || unitIdx >= units.Count) ? baseValue : ModifyCore(units[unitIdx].ActiveBuffs, stat, baseValue);
```
Call sites to migrate: none (public signatures unchanged). Keep both public names — the
enum/string *keying* split is a reasonable design (enum = unit stats, string = extras like
`MaxMana`/`PathShock`); only the math should be shared.

---

## Finding 2 — Two timed on-hit weapon-effect systems (potion coats vs WeaponBonusEffect)
**Verdict: CONSOLIDATE — severity high — effort M — risk medium**

Two implementations of "this unit's melee hits carry an extra effect":

A. **Potion coats** (older, ad-hoc unit fields):
   - Applied: `PotionSystem.ApplyZombie` (`PotionSystem.cs:214` — `WeaponZombieCoatTimer = 300f`)
     and `ApplyPoison` (`PotionSystem.cs:244-245` — `WeaponPoisonCoatTimer = 300f`,
     `WeaponPoisonAmount = 5`).
   - Consumed: inline in the melee-hit block, `Simulation.cs:2790-2799` (poison damage via
     `DamageSystem.Apply`; `ZombieOnDeath = true`).
   - Ticked: `PotionSystem.TickPotionEffects` (`PotionSystem.cs:361-376`).

B. **`WeaponBonusEffect`** (newer, per-unit list `Unit.BonusEffects`, `UnitModel.cs:506`):
   - Kinds `BonusDamage` / `ZombieOnDeath` with chance %, `Permanent`, `ExpiryTimer`
     (`WeaponBonusEffect.cs:25-40`) — **exactly expressive enough to represent both coats**.
   - Applied: `TableCraftingSystem.CompleteCraft` (`TableCraftingSystem.cs:136-144`).
   - Consumed: `Simulation.ApplyBonusEffectsOnHit` (`Simulation.cs:2815-2854`), literally
     the next statement after the coat block (`Simulation.cs:2805`).

**Confirmed latent bug:** `WeaponBonusEffect.cs:23` documents "`ExpiryTimer` decrements
each tick via `WeaponBonusEffectSystem.Tick`" — **that system does not exist anywhere in
the codebase** (grep hits only the comment). Any `Permanent=false` effect would never
expire. Only `Permanent=true` factory entries are used today, so it is dormant, but the
first person to add a timed bonus effect ships a bug.

Canonical home: `Unit.BonusEffects` (`WeaponBonusEffect`). Merged API sketch:
```csharp
// PotionSystem.ApplyPoison (friendly branch):
AddBonusEffect(units, idx, WeaponBonusEffect.Damage(DamageType.Poison, 5) with { Permanent = false, ExpiryTimer = 300f });
// PotionSystem.ApplyZombie (friendly branch):
AddBonusEffect(units, idx, WeaponBonusEffect.ZombieOnDeath(100) with { Permanent = false, ExpiryTimer = 300f });
// New: tick expiry (in TickPotionEffects or a tiny WeaponBonusEffectSystem):
for each unit: BonusEffects?.RemoveAll(e => !e.Permanent && (e.ExpiryTimer -= dt) <= 0f);
```
Call-site categories to migrate:
1. `PotionSystem.ApplyZombie` / `ApplyPoison` friendly branches (writers).
2. `Simulation.cs:2790-2799` inline coat consumption — delete (subsumed by
   `ApplyBonusEffectsOnHit`).
3. `PotionSystem.TickPotionEffects:361-376` coat-timer ticking — replace with the
   generic expiry tick.
4. `UnitModel.cs` — remove `WeaponPoisonCoatTimer`, `WeaponPoisonAmount`,
   `WeaponZombieCoatTimer` fields (only 3 files touch them: UnitModel, Simulation,
   PotionSystem — verified by grep).

Behavior-parity notes: coat poison currently uses `DamageFlags.None` (goes through
armor) — expressible as-is. Re-drinking a potion should *refresh* the 300 s timer, so
`AddBonusEffect` needs a merge-or-add rule (match on Kind+DmgType). Save/load: check
whether unit serialization persists the coat fields and/or `BonusEffects` and keep parity.

---

## Finding 3 — Paralysis hand-rolls a second incapacitation machine
**Verdict: INVESTIGATE — severity medium**

BuffSystem owns a full incapacitation lifecycle for `Incapacitating` buffs: hold anim +
`IncapState` setup (`BuffSystem.cs:84-122`), early-recovery + expiry handling in
`TickBuffs` (`BuffSystem.cs:204-253`), with documented sentinel conventions
(`RecoverTimer = -1` means "AnimResolver fills real clip duration").

`PotionSystem` paralysis re-implements the same shape by hand:
- `TickPotionEffects` slow→stun transition constructs its own `IncapState` and calls
  `AnimResolver.SetOverride(Hold(Stunned, priority 3))` (`PotionSystem.cs:301-312`) —
  parallel to `BuffSystem.cs:104-121`.
- Stun end clears `Incap = default` directly (`PotionSystem.cs:325`), bypassing
  BuffSystem's recovery-anim path, and uses `RecoverTimer = 0` where BuffSystem uses the
  `-1` sentinel (already-diverged convention).
- Two independent writers to `Unit.Incap`: a unit that is knocked down (buff-driven
  incap) and then paralysis-stunned has both machines mutating the same struct — the
  paralysis clear at `PotionSystem.cs:325` can wipe an active knockdown's recovery state.

Why not a straight CONSOLIDATE: the *slow phase* is genuinely inexpressible as a BuffDef —
it is a time-lerped speed curve (0.7x → 0 over 8 s) applied by `Locomotion.UpdateSpeeds`
from the timer, not a constant multiplier. That part is structural variance. But the
*stun phase* is a textbook `Incapacitating` buff (hold `Stunned`, duration 6 s,
`Set Attack/Defense 0` could even replace `GetParalysisFraction`).

Decision needed: (a) keep paralysis fully bespoke and accept dual `Incap` writers
(document the interaction), (b) convert only the stun phase to an `Incapacitating`
`buff_paralysis_stun` so BuffSystem is the sole `Incap` owner, or (c) extend BuffDef with
curve/phase support (likely over-engineering for one status). Recommend (b).

---

## Finding 4 — ApplyBuff wrapper chain (+ ApplyParalysis overloads)
**Verdict: KEEP_SEPARATE — severity low**

`ApplyBuff` → `ApplyBuffWithDuration` is one core implementation with thin conveniences:
`ApplyBuff` (default duration, `BuffSystem.cs:13`), `ApplyBuffById` (registry lookup +
null-guard, `:22`), `ApplyBuffLogged` (combat-log wrapper, `:562`). This is exactly the
"shared component owns mechanics, caller owns data" pattern — a funnel, not duplication.
The two `ApplyParalysis` overloads (`PotionSystem.cs:169` private potion-flavored →
`:180` public core) are the same healthy layering; the public core exists for
`ApplyParalysisAoE` and cloud ticks.

Two micro-notes (not worth separate findings):
- `ApplyBuffLogged` re-implements the existing-stack scan for log wording
  (`BuffSystem.cs:566-571`) — cosmetic only.
- `PotionSystem.ApplyFrenzy` (`PotionSystem.cs:148-163`) applies the buff then manually
  loops `ActiveBuffs` to flip `Permanent = true`. `ApplyBuffWithDuration(units, idx, def, 0f)`
  already produces `Permanent = true` (duration ≤ 0 rule, `BuffSystem.cs:68-75`) — the
  manual loop is a one-line replacement. Suggest fixing opportunistically.

---

## Finding 5 — Skill passives and hit-react/dodge appliers: already canonical
**Verdict: KEEP_SEPARATE — severity low**

The labeler over-matched here:
- Every gameplay-affecting skill effect in `SkillEffects.cs` routes through
  `BuffSystem.ApplyBuff` (`PassiveStatEffect:130`, `GrantIntrinsicBuffEffect:315`,
  `CapBuffEffect:418`, `GrantPathEffect:479`, `MorphNecromancerEffect` reapply `:226`),
  and `PassiveBuffMap` (`SkillEffects.cs:148`) is an explicit single source of truth for
  flag→buff pairings. Skills already "express through" BuffSystem.
- `DamageSystem.ApplyHitReactAnim` (`DamageSystem.cs:84`) and `ApplyDodgeAnim` (`:110`)
  share the `ReactionAllowed` gate (`:64`) and are documented as the single choke points
  for reaction anims ("never SetOverride a reaction directly" — `AnimController.cs:30`).
  Their bodies differ for real reasons (hit flash + `HitReactTimer` on hits only).
  Merging them into one parameterized method would save ~6 lines and cost clarity.
  These are also cosmetic reactions, not buff application at all.
