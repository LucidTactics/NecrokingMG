# Consolidation Opportunities

Reconciled after the 2026-07-07 implementation pass: **41 consolidation commits landed**
(everything from categories A, B, and most of C/D in the 2026-07-06 review). Evidence and
history: [docs/consolidation-review/](../docs/consolidation-review/README.md). Canonical
homes now recorded in [standard_patterns.md](standard_patterns.md).

## Remaining (deferred with reasons — awaiting user decision or future need)

- **C8 paralysis → Incapacitating buff** (S once unblocked): needs a `buff_paralysis_stun`
  entry in data/buffs.json (edit-game-data) + an "IncapRecoverTime ≤ 0 ⇒ instant recovery"
  case in BuffSystem. Batch-1 agent's proposal; user approval pending.
- **C13 preview trajectory intent**: SpellPreview Homing/HomingSwirly use half lob height
  (`*0.5f`) vs the game's full sin(theta). Comment markers in place at both sites; needs a
  design call (match game vs keep preview framing).
- **C1 (second half)**: spell-editor buff/flipbook manager POPUPS stay off RegistryCrudPanel —
  structural variance (modal chrome, apply-on-close vs immediate save). Only revisit as a
  `RegistryManagerPopup` if editors grow.
- **C6 (second half)**: SpriteAtlas.LoadExtension stays separate from the split-phase
  primitives (placeholder-list bookend in Game1's two-loop flow makes composition unsafe).
- **D1 editor preview shadow**: reading live ShadowSettings needs Game1→editor injection
  plumbing; low value.
- **C9 DoT damage entry**: add a silent `DamageType.DoT` path only if burn/bleed get built
  (poison's bypass is intentional; its death already routes through DamageSystem.Kill).
- **HarmonizeSettings JSON codecs ×2** (new, from batch 3): UIDefsIO's harmonize read/write
  should delegate to `HarmonizeSettings.Read/WriteValue` (tolerating short gradColor arrays).
- **ViewBounds/frustum-cull math** duplicated in DeathFog/GrassTuft renderers (noted batch 5).
- **B7 style question**: editor section headers now share one method with 3 styles + the map
  editor's variant — unify visually to ONE style? (user-facing consistency call).
- Necromancer-as-normal-unit (Q9 direction) — its own project; casting half done via
  ICasterResources; HP/stats/HUD/save/ghost migration needs the agreed sub-plan first.

## Process notes
- Stale scenario: craft_table asserts the zombie at craft-completion instant but spawns are
  deferred behind the rise effect (since f7da4bc) — update the scenario to poll.
- Pre-existing failures (not from this pass): trample_kill (corpse fling), spell_visual_test
  (Strike/Zap damage=0 — MR gate/data-driven; batch code verified equivalent).
