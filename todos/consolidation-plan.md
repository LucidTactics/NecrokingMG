# Consolidation Implementation Plan

Executes the findings from [docs/consolidation-review/](../docs/consolidation-review/README.md)
(116 findings; queue in [docs/consolidation_opportunities.md](../docs/consolidation_opportunities.md)).
Check off items here as they land; delete this file when the queue is drained.

**Status (2026-07-07 evening): EXECUTED. 41 consolidation commits landed across 6 sequential
agent batches (sim/combat core, Game1 glue, data/IO, editor toolkit, render/core/texture, UI
panels). All A+B items done; C/D done except principled deferrals — see the pruned
docs/consolidation_opportunities.md for what remains + open questions. Canonical homes
recorded in docs/standard_patterns.md + locate-behavior docs. This file can be deleted once
the deferred questions are answered and the /dup-review + label-store tooling decision is made
(design: docs/consolidation-review/operationalizing.md).**

Design north star from the user (2026-07-06): attacks are processed by ONE tag-driven pipeline
(tags on the weapon/spell def decide armor interaction etc.; traps get a weapon/spell def), and
the necromancer trends toward being a NORMAL unit that the engine merely knows is player-controlled.

## Ground rules (every batch)

- One commit per item or per small coherent batch; message names the dossier it implements.
- `dotnet build` green before every commit; run the targeted scenarios named in the batch;
  `dotnet publish` at the end of a session so the user can test (CLAUDE.local.md).
- Behavior-visible changes get a regression scenario or a drive-game verification first
  (reproduce → fix → re-verify), so "bug fixed" claims are grounded.
- After each green batch: offer to push (Drive-sync collaborator; never leave half-committed work).
- When a consolidation establishes a canonical home, update `docs/standard_patterns.md`
  and the relevant `docs/locate-behavior/<area>.md` in the same commit.
- Net/ untouched throughout.

## Phase 0 — Reproduce the headline bugs (0.5 session)

Cheap confirmations before touching code, so fixes are verifiable:
- [ ] Circle trigger region: save map with circle trigger → reload → inspect region type (dev cmd or scenario).
- [ ] Weapon coat/bonus never expires: apply coat via potion, advance clock, inspect unit state.
- [ ] Summon missing intrinsic buffs: learn a skill with intrinsic buff → summon → compare buffs vs SpawnUnitByID path.
- [ ] Frenzy gap: frenzy an archer, verify it disengages home while frenzied.
- [ ] Horde LerpAngle: visual check via drive-game (horde reforms rotating long way).
Skip any that costs more than ~15 min to stage; code-reading evidence is already strong.

## Phase 1 — Zero-question mechanical wins (1–2 sessions, no design input needed)

No behavior change intended (or pure bug fix with unambiguous correct behavior). Batchable.

Dead code deletions:
- [ ] MapEditorWindow.PaintObjects; UndoObjectRemove; Game1.GetItemTexture; SettingsGeneralTab.Draw dead overloads ×2.

Pure-bug fixes (no design question):
- [ ] HordeSystem.LerpAngle sign bug → MathUtil.AngleDelta/LerpAngle consolidation (leave Net/ copy).
- [ ] Atomic writes: main map save (AtomicFile stream API), SkillBookData.SaveLayout.

Trivial dedups / delegations:
- [ ] Scalar Lerp ×4 → MathUtil.Lerp; editor IndexOf ×4 → EditorBase; WeatherRenderer.Init → Resize;
      Quadtree.QueryRadius → QueryRadiusByFaction(All); TextureUtil premultiply ×3 → one helper.
- [ ] Projectile arc solver: SolveLobTheta/BallisticVelocity on ProjectileManager; migrate the 7–8 game copies.
      (SpellPreview constants dedup too; leave its 0.5f question for Phase 5.)
- [ ] ScatterSpot trio → one helper with region overloads (keep rng streams identical — verify with a map-load diff).
- [ ] DamageSystem: StampAttacker extraction (Apply/ApplyDirect verbatim tail).
- [ ] WadingDefaultsFile → Core.JsonFile.
- [ ] WorldQuery rewires (no policy change): FindBerryBushNear → EnvBerryBushes filter; TryPickTetherEnd → 2 queries;
      FindNearestMushroom, MapEditor FindClosestObject → NearestEnvObject; VillageThreat + trap targeting reroutes;
      Simulation.FindNearestEnemyIndex forwarder + AIContext WorldQuery handle (SubroutineSteps.FindClosestEnemy).
- [ ] Movement folds: Simulation.MoveTowardPosition + inline MoveToPoint → SubroutineSteps.MoveToward;
      StrideCalibration CycleDistanceWorld; SoloPredator SubDisengage → SubroutineSteps.Disengage.
- [ ] Flipbook Copy → CloneDef (one line); SpellPreview AgeAndExpire<T>.
- [ ] Wildcard smalls: Camera25D projection delegation (Renderer + GrassTuftRenderer); TextureUtil.GetRadialGlow ×3;
      RuntimeWidgetRenderer SetOverride<TK,TV>; EnvObjectEditor TrackPivotDrag; WadingWake SplashSession;
      RegistryBase.NameOf(id) + UnitEditor generic dropdown builder.

Verification: build + `combat_test`, `pathfinding_test`, `spell_test`, horde + AI scenarios;
drive-game screenshots for editor-visible changes. Perf pass (perf cmd) after WorldQuery reroutes.

## Phase 2 — Bug-fixing consolidations (needs Key Question answers; 2–3 sessions)

- [ ] **Spawn pipeline** (Q1): Game1.SpawnUnit → delegate to Simulation.SpawnUnitByID; one BuildUnitAnimData factory
      (fixes intrinsic-buffs and anim-timing bugs). Scenario: summon + trigger-spawn timing checks.
- [ ] **SentryTransitions** (Q2): extract shared sentry skeleton for CombatUnit/RangedUnit/CasterUnit/SoloPredator
      (fixes frenzy gap). Existing AI scenarios (wolf_retarget, neutral_fightback, patrol_encounter...) as harness.
- [ ] **One strike executor** (Q3): caster-agnostic SpellEffectSystem.ExecuteStrike(From); migrate NPC + trap paths.
- [ ] **Weapon on-hit effects** (Q4): potion coats → Unit.BonusEffects + real expiry tick.
- [ ] **Map sidecar I/O** (Q5): triggers/zones/roads → attribute DTOs + JsonFile; fixes circle-trigger + junction
      round-trips. Regression scenario: save/load round-trip equality.
- [ ] **DamageSystem.Kill** (mild Q6): unify 4 death-finalization sites.
- [ ] BuffSystem ModifyCore (GetModifiedStat/Extra) — no question, rides with Q4 batch.

## Phase 3 — WorkerSystem + remaining query migrations (1 session)

- [ ] WorkerSystem six finders → _sim.Query + IEnvQueryFilter structs (fixes bagged-corpse gap — confirm intended, Q7).
      Scenario: corpse_worker, craft_table, job flows.

## Phase 4 — Editor/UI mechanics extractions (3–4 sessions, editor-only risk)

Order by payoff; verify each via editor scenarios (editor_ui_test, editor_screenshot) + drive-game screenshots.
- [ ] DrawUtils migration (~17 primitive copies) + DrawEllipse additions.
- [ ] EditorBase FieldCore (text/int/float/search fields — fixes cursor-positioning inconsistency);
      DrawSectionHeader ×6; DrawStepperRow; env category/group distinct-scans → EnvironmentSystem.
- [ ] RegistryCrudPanel<TDef> (5 sub-editor scaffolds).
- [ ] UI: SideListMenu (Building/Crafting menus); RichTip (tripled tooltip + WrapText); HUDRenderer ButtonRow.
- [ ] Render/TextureCache (6× get-or-load); WidgetResourceCache (editor↔runtime, ~120 lines); SpriteAtlas sync-on-split-phase.
- [ ] UI widget defs single parser (UIEditorWindow + RuntimeWidgetRenderer → RegistryBase subclasses or UIDefsIO) (Q8 shape note).

## Phase 5 — Design-decision items (after Q answers; size varies)

- [ ] Unit resource model unification (Q9 — the big one; unlocks true single casting pipeline).
- [ ] Paralysis stun phase → Incapacitating buff (Q10).
- [ ] Poison DoT entry point (Q11).
- [ ] env_defs.json DTO consolidation (Q12 — coordinate with collaborator; changes Drive-synced file shape).
- [ ] Villages json deprecation vs SpawnGroup merge (Q13).
- [ ] SpellPreview 0.5f trajectory (Q14); editor preview shadow live-settings (minor); puff-layer hash helpers move (minor).

## Key Questions — ANSWERED 2026-07-06 (user); implementation NOT yet greenlit (awaiting colleague feedback)

- **Q1 intrinsic buffs on summons: YES** — summons should get skill-tree buffs. Fix as planned.
- **Q2 frenzy: YES, applies to everyone** — archers/casters/predators rampage like melee when frenzied.
- **Q3 strike rules: SINGLE PIPELINE, TAG-DRIVEN** — per-attack tags (ArmorNegating, ArmorPiercing, MR
  interaction, ...) live on the weapon/spell def and the one pipeline processes them. Traps must
  reference a weapon or spell def (they already fire a spell via ProcessTrapFireEvents — formalize that).
  NPC hardcoded ArmorNegating moves into the def. Kill attribution handled uniformly by the pipeline.
- **Q4 coat/bonus durations: NO DECISION NEEDED (resolved on closer read)** — the timed half of
  WeaponBonusEffect is unused today (all factories Permanent=true; the documented Tick doesn't exist),
  so nothing is player-visible. Fix: implement the expiry tick, convert potion coats to BonusEffects
  entries keeping their existing 300s, delete the four coat fields. Behavior unchanged.
- **Q5 circle triggers: FIX THE ROUND-TRIP** — save/load must be lossless; circles come back as circles.
- **Q12 env_defs reshaping (user's #6): PROCEED** — when the on-disk shape changes, tell the user so the
  colleague can be told to pull env_defs before editing.
- **Q8 UI def files (user's #7): PROCEED, same notify-to-pull protocol.**
- **Q9 resource model (user's #8): DIRECTION CONFIRMED — necromancer becomes a normal unit.** HP/mana/
  cooldowns etc. live on the unit record like any caster; the engine merely knows WHICH unit is
  player-controlled (e.g. HUD reads hp from that unit). No special necro-only stat values. BUT: risk of
  unintended consequences acknowledged — when this item comes up (Phase 5), write a detailed sub-plan,
  identify major obstacles (save format, NecroState consumers, HUD, multiplayer ghosts), and present it
  to the user BEFORE implementing.
- Q6 poison death anim, Q7 bagged corpses, Q10 paralysis, Q11 DoT, Q13 villages, Q14 preview trajectory:
  low-stakes — Claude makes the sensible call in-context (user OK'd this), flagging each in the batch summary.
