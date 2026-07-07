judged=20/20 failed=[]
verdict counts: {'CONSOLIDATE': 58, 'INVESTIGATE': 9, 'KEEP_SEPARATE': 49}

## casting-pipelines
HEADLINE: The player pipeline (SpellCaster → SpellEffectSystem) is the documented canonical three-layer design, but NPC casters and traps each re-implement Strike execution with real, already-shipped divergences (MR gate, damage flags, kill attribution), and the NPC handler ignores spell.Category entirely. Consolidate strike execution into SpellEffectSystem now; the deeper "one unit-casts-spell gate" merge needs a resource-model decision (NecroState vs per-unit mana) first.
- [CONSOLIDATE/high] Strike-spell execution triplicated (SpellEffectSystem.ExecuteStrike vs CasterUnitHandler.TryCast vs ProcessTrapFireEvents)
    Three hand-rolled zap/strike executors have already diverged on gameplay rules — MR penetration only in the player path, unconditional ArmorNegating only in the NPC path, no kill attribution and dropped GodRay/TargetFilter params in the trap path; merge into a public caster-agnostic SpellEffectSystem.ExecuteStrike (effort M, medium risk since three balance behaviors must be reconciled deliberately).
- [INVESTIGATE/medium] Player vs NPC cast gates: same validation pipeline on two resource models
    SpellCaster.TryStartSpellCast and CasterUnitHandler.TryCast both do cooldown→path-gate→effective-mana→deduct→casting-buff but on NecroState vs Unit.Mana/SpellCooldownTimer; the decision needed is whether to move necromancer resources onto the unit record (one model, the real 'unit casts spell' fix) or extract a shared gate over two stores — plus SpellEffectSystem must lose its hardcoded Faction.Undead and FindNecromancer projectile re-anchor before NPCs can cast non-Strike categories at all (today a priest with a Cloud def sky-strikes, since TryCast never reads spell.Category).
- [KEEP_SEPARATE/low] SkillEffectRegistry.Apply is not a casting pipeline
    It is a one-time on-learn unlock dispatcher (add spell to bar, unlock potion, morph, grant path) that feeds the spell pipeline's inputs — labeler over-match, no shared cast-time intent.
- [KEEP_SEPARATE/low] Channeled-cast anim machine and built-in abilities are player-presentation stages, not duplicates
    UpdateChanneledCast is the only channel phase machine (NPCs/traps have no wind-up) and TryDispatchBuiltinAbility is a documented intentional bypass for hotkey actions that aren't spells — structural variance per CLAUDE.md, abstracting it would create a framework.

## under-cursor-picking
HEADLINE: The labeler's evidence is largely stale: WorldQuery (_sim.Query) already IS the one world-pick API (consolidated 2026-07-06) and most cited finders are thin wrappers over it — but three pre-migration stragglers remain, including a genuinely diverged duplicate of click-to-melee range logic (TryMeleeOrGather vs TryAttackClick).
- [CONSOLIDATE/high] Click-to-melee resolved twice with divergent range formulas
    TryAttackClick (WorldClicks.cs:167, SSOT MeleeRangeUtil.Compute, hardcoded 2f cooldown) and TryMeleeOrGather (Game1.cs:4084, hand-rolled scan with 1.0+weaponLen*0.15 range and def cooldown) implement the same 'melee the enemy at the cursor' intent with already-diverged reach and cooldown; extract one TryOrderMeleeAtCursor helper (effort S, risk low).
- [CONSOLIDATE/low] FindBerryBushNear duplicates the unused EnvBerryBushes WorldQuery filter
    Game1.cs:4133 hand-rolls a scan semantically identical to _sim.Query.NearestEnvObject(pos, r, new EnvBerryBushes()) — the filter exists at WorldQuery.cs:89 with zero callers; one-line body swap (effort S, risk minimal).
- [CONSOLIDATE/low] TryPickTetherEnd hand-rolls corpse+unit nearest scans
    Game1.cs:150's corpse gate equals CorpseExclude.Free and its unit gate equals UnitUnderCursor; replace with two WorldQuery calls plus a nearest-of-either compare (sibling rope code at Game1.cs:272 already migrated) — effort S, risk low.
- [KEEP_SEPARATE/low] Single ranked world-pick API (kind-mask) mega-proposal
    The click chain in Game1.WorldClicks.cs is a priority chain whose scan mechanics already live in the canonical WorldQuery (_sim.Query, established 2026-07-06); per-kind radii, consume/feedback semantics, and gating are structural variance, and WorldClicks.cs:53-59 already documents the right future design trigger.

## nearest-unit-queries
HEADLINE: WorldQuery is the confirmed canonical home and most callers (Simulation, SpellEffectSystem, Game1) already route through it; the real duplication is three copies of the quadtree nearest-enemy scan plus two small linear scans, all cheaply routable by giving AIContext a Query handle. Do NOT force the quadtree everywhere — WorldQuery's quadtree-for-sim-tick / linear-for-UI-safe split is a deliberate, documented contract (the tree is stale while paused or in the editor).
- [CONSOLIDATE/medium] Quadtree nearest-enemy scan re-implemented 3x outside WorldQuery.NearestEnemyOf
    Simulation.FindNearestEnemyIndex (Simulation.cs:3172), SubroutineSteps.FindClosestEnemy (SubroutineSteps.cs:478, 12 archetype call sites), and SpellVisualTestScenario.cs:680 duplicate WorldQuery.NearestEnemyOf's scan; fix is a one-line forwarder in Simulation plus adding a WorldQuery? field to AIContext (set in BuildAIContext, null fallback kept for minimal contexts) — effort S-M, low risk.
- [CONSOLIDATE/low] VillageThreat.FindNearestUndead re-implements WorldQuery's faction-masked linear scan
    VillageThreat.cs:16 is exactly WorldQuery.NearestUnitLinear(pos, range, Faction.Undead.Bit()); add an honestly-named WorldQuery.NearestOfFaction alias, keep the VillageThreat wrapper for the villages-fear-undead-only policy, and it becomes the best future candidate for the quadtree mask path at horde scale.
- [CONSOLIDATE/low] Trap targeting (EnvironmentSystem.FindTrapTarget) re-implements NearestEnemyToPoint
    EnvironmentSystem.cs:1120 duplicates Query.NearestEnemyToPoint(trapPos, 2.5f, trapFaction); Game1.cs:3688 already has _sim in hand, so pass _sim.Query into UpdateTraps (owner-to-faction mapping stays with the caller) — effort S.
- [KEEP_SEPARATE/low] AwarenessSystem.FindClosestThreat uses a per-candidate variable detection range
    The distance bound itself varies per target (sneak x0.5, run x1.5, query widened x1.5) — a variable-radius nearest that WorldQuery's IUnitQueryFilter cannot express without hiding the modifier logic inside Match; structural variance per CLAUDE.md, don't abstract.
- [KEEP_SEPARATE/low] Squad-scoped picks (WolfPackHuntAI.NearestQuarry, RatPackHandler.PickGangUpTarget) are not world queries
    NearestQuarry scans a squad-member ID list and PickGangUpTarget is join-a-packmate's-victim selection policy (its nearest-enemy fallback already delegates to SubroutineSteps.FindClosestEnemy) — labeler over-match; a 'nearest among these IDs' API for two 8-line loops would be a framework, not a utility.
- [KEEP_SEPARATE/low] UnitModel.FindAliveNecromancerIndex is a liveness scan, not a distance query
    UnitModel.cs:572 is a first-match alive+PlayerControlled scan with no position math, already documented as distinct from the Simulation.NecromancerIndex HUD cache — labeler over-match.

## nearest-envobject-queries
HEADLINE: The canonical API already exists and is documented as such (WorldQuery / _sim.Query, docs/locate-behavior/world.md:62) — this is a migration in flight, not a missing abstraction; the real work is migrating WorkerSystem's six hand-rolled scans (one of which has already diverged on bagged-corpse handling) plus three one-off stragglers, while the deer's scored bush search and the collision-only EnvSpatialIndex stay separate. Labeler over-matched: ForagableSystem.FindNearest and Game1.Crafting already delegate to WorldQuery, and WadingEditorPopup.FindNearestForagable does not exist.
- [CONSOLIDATE/high] WorkerSystem's six private find scans duplicate WorldQuery
    FindDeposit/Withdraw/HostBuilding, FindNearestForagable/BerryBush/CorpseObj (WorkerSystem.cs:344-460) re-write the canonical _sim.Query scan — FindNearestBerryBush exactly duplicates the EnvBerryBushes filter and FindNearestCorpseObj has already diverged (misses the Bagged-corpse exclusion); migrate via caller-side IEnvQueryFilter structs capturing `this`, effort M.
- [CONSOLIDATE/medium] Straggler one-off scans: FindBerryBushNear, FindNearestMushroom, FindClosestObject
    Game1.cs:4133 FindBerryBushNear is byte-for-byte the existing EnvBerryBushes gate, BoarForageAI.cs:121 needs only a caller-side EnvMushrooms struct (world.md explicitly lists it as unmigrated), MapEditorWindow.cs:6990 is a match-all pick needing a trivial EnvAny filter — all S-effort one-liners onto _sim.Query.NearestEnvObject.
- [KEEP_SEPARATE/low] DeerHerdHandler.FindNearbyBush is a scored search, not a nearest query
    DeerHerdHandler.cs:936 uses poison-scent distance biasing, dual-center constraints (spawn radius + minDist from current pos), and a semi-random iteration offset for herd de-clumping — structural variance that would turn WorldQuery into a scoring framework.
- [KEEP_SEPARATE/low] EnvSpatialIndex should NOT back WorldQuery env scans (yet)
    EnvSpatialIndex.cs is a collision-only ORCA index that drops CollisionRadius<=0 objects (most foragables) and Collected objects, so it cannot answer these queries correctly; WorldQuery's facade contract (WorldQuery.cs:119-121) already reserves a zero-call-site-change drop-in slot if profiling ever demands it.

## unit-spawning
HEADLINE: The canonical pipeline (ApplyDefRuntimeFields) exists and all def-based paths use it, but Game1.SpawnUnit copies SpawnUnitByID's core instead of calling it — and has already diverged (spell summons miss skill-tree intrinsic buffs), plus a triplicated anim-init block where two copies drop per-unit AnimTimings.
- [CONSOLIDATE/high] Game1.SpawnUnit re-implements Simulation.SpawnUnitByID's core instead of delegating
    Game1.cs:2209 duplicates AddUnit+BuildStats+ApplyDefRuntimeFields and has already diverged: skill-tree intrinsic buffs apply only in SpawnUnitByID (Simulation.cs:3748), so spell summons (SpellEffectSystem.cs:346), map-placed and dev spawns silently miss them; fix is SpawnUnit calling _sim.SpawnUnitByID then doing necro-index/horde/anim extras (effort S, medium risk from the intended behavior change).
- [CONSOLIDATE/medium] Three copies of build-AnimController-from-def, two drop AnimTimings overrides
    Game1.cs:2235 (full), RebuildUnitAnim (Game1.Animation.cs:29) and the lazy per-frame init (Game1.Animation.cs:341) are the same ~30-line block, but the latter two skip SetAnimTimings — so unit-editor timing overrides (incl. EffectTimeMs that times attack damage) never apply to trigger/potion/craft-spawned or transformed units; extract one BuildUnitAnimData factory (effort S, low risk).
- [CONSOLIDATE/low] ScatterSpot / ScatterSpotInRect / ScatterSpotNear walkable-point search trio
    Game1.Villages.cs:226 and Game1.Zones.cs:147/:287 share identical mechanics (24-try LCG, IsPointWalkable radius 0.5f, center fallback) and differ only in sampled region — data-level variance; one shared helper with three region overloads removes the triplicated magic numbers (effort S, keep rng streams identical for determinism).
- [INVESTIGATE/low] Legacy SpawnGroup (Villages) vs SpawnZoneGroup (Zones)
    Game1.Zones.cs:131 is a self-described mirror of the legacy villages-json SpawnGroup (Game1.Villages.cs:178); the real decision is whether to deprecate the _villages.json population path entirely (zones are the editor-driven successor) rather than merge two 12-line loops that may soon be one.
- [KEEP_SEPARATE/low] Spawn-stack layering (AddUnit / SpawnUnitByID / SpawnZombieMinion / SpawnNetGhost / SpawnReanimated)
    These are intentional layers, not duplicates — allocation core, def-applied sim spawn, raise-into-horde policy (with the cap-category lint deliberately at that choke point), net-ghost fixups, and a decoupling delegate that routes to QueueReanimRise->SpawnZombieMinion; ai.md's claim that all def-based paths share ApplyDefRuntimeFields is verified TRUE.

## damage-application
HEADLINE: Convergence is far better than the evidence suggested — trample, sweep, clouds, spells, glyphs, lightning, and sacrifice all route through DamageSystem or ResolveMeleeAttack, and kill tallies/corpses are centralized in RemoveDeadUnits. The one real duplication is the small death-finalization block (HP=0/Alive=false/Death anim/prone-snap), re-implemented inconsistently at 4 sites and worth extracting as DamageSystem.Kill.
- [CONSOLIDATE/medium] Unit-death finalization duplicated at 4 sites
    The HP=0/Alive=false/Death-anim/MarkDeathFromProne block is re-implemented inconsistently in DamageSystem.Apply+ApplyDirect, TryApplyLimbChop decapitation (Simulation.cs:2985, misses prone-snap), PotionSystem poison tick (:346, misses death anim + prone-snap), and TriggerSystem EffKillUnits (:239, raw Alive=false) — extract DamageSystem.Kill(units, idx) (S effort, low risk).
- [CONSOLIDATE/medium] Apply vs ApplyDirect share attribution/auto-engage tail
    LastAttackerID + auto-EngagedTarget block with the DeerHerd exemption is duplicated verbatim (DamageSystem.cs:188-203 vs :232-244, comment even says 'same as Apply') — extract a StampAttacker helper, keeping the flinch difference (Apply flinches, ApplyDirect deliberately doesn't) out of it.
- [INVESTIGATE/low] Poison DoT HP drain bypasses DamageSystem
    PotionSystem.cs:344-353 is the only per-tick HP subtraction outside DamageSystem; the bypass is intentional and commented (no flinch/auto-engage, green number) — decide between routing only its death through the Kill helper vs adding a silent DamageType.DoT entry point if more DoTs (burn/bleed) are coming.
- [KEEP_SEPARATE/low] Melee vs ranged attack resolvers (ResolveMeleeAttack / ResolveArrowHit)
    Different Dominions-ported opposed-roll formulas with different mitigation stacks and side effects (fatigue/shields/limb-chop/coats vs precision/parry/arc hit-location); both already funnel HP changes into DamageSystem — merging would be structural-variance flag soup.

## buff-effect-application
HEADLINE: BuffSystem is genuinely the single home for data-driven timed stat modifiers (all 40+ call sites funnel through ApplyBuffWithDuration), but two real duplications sit at its edges: potion weapon coats duplicate the WeaponBonusEffect on-hit system (whose expiry ticker turns out to be nonexistent dead code), and paralysis runs a parallel incapacitation machine with a second writer to Unit.Incap.
- [CONSOLIDATE/high] Two timed on-hit weapon-effect systems: potion coats vs WeaponBonusEffect
    Potion weapon coats (WeaponPoisonCoatTimer/WeaponZombieCoatTimer fields, Simulation.cs:2790-2799) duplicate the WeaponBonusEffect list consumed literally one statement later (Simulation.cs:2805), and WeaponBonusEffect's documented expiry ticker (WeaponBonusEffectSystem.Tick) does not exist — a latent never-expires bug; canonical home is Unit.BonusEffects with a real expiry tick.
- [CONSOLIDATE/medium] Duplicate modifier math: GetModifiedStat (enum) vs GetModifiedExtra (string)
    BuffSystem.cs:457-475 and :538-560 contain the identical (base+ΣAdd·stacks)×∏Multiply^stacks / last-Set-wins loop twice; GetModifiedStat already converts enum→string, so both should delegate to one private ModifyCore — zero call-site changes, effort S.
- [INVESTIGATE/medium] Paralysis hand-rolls a second incapacitation state machine
    PotionSystem.cs:301-325 builds its own IncapState + Hold(Stunned) override and clears Incap=default directly, paralleling BuffSystem's Incapacitating-buff machinery (BuffSystem.cs:84-122, 204-253) with two writers to Unit.Incap; the slow-phase speed curve is genuinely structural, but the decision is whether the stun phase becomes an Incapacitating buff so BuffSystem is the sole Incap owner (recommended).
- [KEEP_SEPARATE/low] ApplyBuff wrapper chain and ApplyParalysis overloads
    ApplyBuff/ApplyBuffById/ApplyBuffLogged are thin conveniences funneling into one core (ApplyBuffWithDuration) — healthy single-choke-point design, not duplication; only micro-nit is PotionSystem.ApplyFrenzy's manual Permanent-flag loop, replaceable by ApplyBuffWithDuration(...,0f).
- [KEEP_SEPARATE/low] Skill passives and hit-react/dodge appliers already route through canonical choke points
    Labeler over-match: every SkillEffects gameplay effect already calls BuffSystem.ApplyBuff (with PassiveBuffMap as explicit single source of truth), and ApplyHitReactAnim/ApplyDodgeAnim share the ReactionAllowed gate with genuinely different bodies — cosmetic reactions, not buff application.

## ai-handler-boilerplate
HEADLINE: The blanket "all handlers duplicate everything" claim is an over-match — most handlers are genuinely distinct state machines and the steering-math claim is false — but it hides one high-value consolidation: CombatUnit/RangedUnit/CasterUnit/SoloPredator share a near-identical alert-driven sentry skeleton that has already diverged (frenzy logic in only one copy) and should move into a CombatTransitions-style static helper.
- [CONSOLIDATE/high] Sentry skeleton (Idle/Alert/Combat/Return) quadruplicated in CombatUnit/RangedUnit/CasterUnit/SoloPredator handlers
    Four handlers copy the same EvaluateRoutine ladder, UpdateAlert, UpdateReturn, and OnSpawn nearly byte-for-byte and have already diverged (Frenzied handling exists only in CombatUnitHandler, so frenzied archers/casters/predators calmly walk home); fix with a CombatTransitions-style static helper (SentryTransitions: EvaluateSentryRoutine + UpdateReturn + SpawnAtIdle taking a SentryConfig), migrating 4 files, effort M, moderate risk with existing scenario coverage.
- [KEEP_SEPARATE/low] CombatTransitions StandardEngaged/ChasingExits adopted only by HordeMinion
    WolfPack/DeerHerd/RatPack fighting exits are structurally different state machines (time-of-day return with standup, Stance/Charge to Calming, gang-up Scurry — no chase/return routine split), so forcing the leash-pattern helpers on them would be a framework; only real fix is the stale doc comment at CombatTransitions.cs:6-8 that falsely names WolfPack/DeerHerd as users.
- [KEEP_SEPARATE/low] Steering and resolve helper duplication claims (AIForageMove/AIWolfHuntMove vs MoveToward; ResolveTarget vs ResolveAlertTarget)
    Labeler over-match: AIForageMove/AIWolfHuntMove (Simulation.cs:3273/3284) contain no seek math — they are 3-line bridges that deliberately reuse the canonical SubroutineSteps.MoveToward from the sweep-style AIs, and ResolveTarget/ResolveAlertTarget are one-line forwarders to the same UnitUtil.ResolveUnitIndex on different fields.
- [KEEP_SEPARATE/low] OnSpawn and GetRoutineName/GetSubroutineName boilerplate across all 13 handlers
    The 4-line OnSpawn carries real per-handler variance (IsNight routine pick, MoveTarget seed, horde enrollment, WorkerPhase, no-ops) and the name switches are debug-only labels over each handler's own private byte constants — abstracting either would cost more than the drift risk; the sentry four get a shared SpawnAtIdle via finding 1.

## projectile-arcs
HEADLINE: Evidence confirmed and understated: the ballistic-arc velocity math is copy-pasted 8-9 times across Projectile.cs, SpellEffectSystem.cs, and the editor's SpellPreview.cs, and the preview copy has already silently diverged from in-game homing arcs — a small static arc-solver on ProjectileManager fixes all of it at S effort.
- [CONSOLIDATE/medium] Ballistic arc/direct-fire velocity solve duplicated 7x across ProjectileManager and SpellEffectSystem
    The theta=0.5*asin(min(d*g/v^2,1)) lob solve appears 3x in Projectile.cs (SpawnArrow volley/SpawnFireball/SpawnPotionLob) and the 5-degree direct-fire solve 5x (SpawnArrow + all four SpellEffectSystem.SpawnProjectile trajectory cases, which also wastefully overwrite SpawnFireball's lob solve); extract static SolveLobTheta/BallisticVelocity helpers on ProjectileManager — variance is pure data (speed, clamp), effort S, risk low.
- [INVESTIGATE/medium] SpellPreview editor reimplements the trajectory solver with copied constants and has already diverged from the game
    SpellPreview.cs:22-23 hardcodes ProjGravity=13.89/DefaultSpeed=28.29 (copies of ProjectileManager.Gravity/MagicSpeed) and re-pastes all five trajectory solves; its Homing/HomingSwirly arcs use VelocityZ*0.5f (lines 1003/1022) while the game uses full sin(theta) — decide whether that 0.5f is intentional preview framing or drift before folding it onto the shared solver (the constant dedup is unambiguous either way).

## registry-json-io
HEADLINE: The claimed settings-vs-RegistryBase duplication is mostly already consolidated onto Core.JsonFile; the real problem is split JSON reader/writer pairs, where verification found two confirmed round-trip bugs in the map sidecars (circle trigger regions revert to rectangles on load, and saved road junctions are parsed but never restored) plus a duplicated UI-def parser pair that is already drifting.
- [CONSOLIDATE/high] Map sidecars (triggers/zones/roads): loader in MapData.cs, saver in MapEditorWindow.cs
    Split reader/writer pairs have already diverged twice — SaveTriggers writes region "shape" (MapEditorWindow.cs:6355) that LoadTriggers never reads (circle regions silently become rectangles, changing ContainsPoint gameplay), and LoadRoads parses junctions but never calls SetJunctions (RoadSystem.cs:77 has zero callers) so saved junctions vanish on load; consolidate onto attribute DTOs + Core.JsonFile (atomic, if-changed) — files are small, effort M.
- [CONSOLIDATE/medium] UI widget defs: two parallel hand-rolled parsers plus a manual Utf8JsonWriter saver
    UIEditorWindow (:420-:870) and RuntimeWidgetRenderer (:792+) each hand-parse the same nine_slices/elements/widgets.json into the same shared def classes (~600 lines of TryGetProperty boilerplate), with latent drift already present (runtime reads nine-slice harmonize the editor neither reads nor writes); canonical home: RegistryBase<T> subclasses (files are exactly {rootKey:[{id,...}]}) or a shared UIDefsIO, effort M.
- [CONSOLIDATE/low] WadingDefaultsFile hand-rolls the I/O Core.JsonFile was built for
    JsonFile.cs's own doc comment (:10) names WadingDefaultsFile as an intended user but it never migrated — today it uses non-atomic File.WriteAllText and a private options instance; drop-in swap to JsonFile.Load/Save, effort S, trivial risk.
- [INVESTIGATE/medium] env_defs.json: ~80-field ParseEnvDef (MapData.cs:569) vs WriteJson (EnvironmentSystem.cs:330) in different files
    Same drift class as the confirmed sidecar bugs (field list duplicated as reads and writes in separate files, currently in sync), but consolidation needs a decision: attribute-based DTO requires custom converters (HdrColor, HarmonizeSettings, category-dependent randomFlip default) and RegistryBase's prune-defaults would change the on-disk shape of a Drive-synced shared file.
- [KEEP_SEPARATE/medium] Main map file (default.json) streaming Utf8JsonWriter
    The 55 MB heterogeneous document with base64 blobs legitimately needs streaming (RegistryBase/JsonFile string writes would balloon memory) — structural variance per CLAUDE.md; the one real gap is non-atomic File.Create (MapEditorWindow.cs:6018) risking map corruption on crash mid-save, fixable with an AtomicFile stream API, not consolidation.
- [KEEP_SEPARATE/low] CorpseSettings / GameSettings Load/Save
    Labeling evidence is stale: both already delegate to Core.JsonFile (CorpseSettings.cs:74/:82, GameSettings.cs:388/:416, the latter using SaveIfChanged for the per-frame auto-save loop), and single-object per-machine settings are the wrong shape for RegistryBase — nothing to do.
- [KEEP_SEPARATE/low] SkillBookData load + SaveLayout
    Tab files carry non-registry metadata (displayName/order/unlockRequirement) and SaveLayout (:80) is deliberately a surgical read-modify-write that patches only x/y while preserving unknown hand-authored fields — a guarantee full-object serialization would destroy; only nit is a one-line File.WriteAllText → AtomicFile swap.

## editor-parallel-subeditors
HEADLINE: The real duplication is one thing repeated five times: a registry-backed list+detail+CRUD sub-editor scaffold (weapon/armor/shield in UnitEditorWindow, buff/flipbook popups in SpellEditorWindow) that is already diverging and should become a generic RegistryCrudPanel<TDef> owning mechanics while per-type detail forms stay caller-owned. Most other labeled 'parallels' are either already consolidated (CloneDef wrappers, SpellDef style builders) or intentional structural variance, with one concrete latent bug: the flipbook Copy button hand-clones fields in violation of the repo's own CloneDef standard.
- [CONSOLIDATE/medium] Registry list+detail+CRUD sub-editor scaffolding x5 (weapon/armor/shield sub-editors + buff/flipbook manager popups)
    Five byte-parallel instantiations of the same browse/select/New/Copy/Delete/Save flow over RegistryBase<TDef> registries, already drifting (SetMouseOverUI hover, ClearActiveField guard, hand-rolled vs shared scroll list present in only some copies) — extract a generic RegistryCrudPanel<TDef> that owns list/CRUD/clipboard mechanics while the per-type Detail forms stay caller-owned (effort M, editor-only risk).
- [CONSOLIDATE/medium] Flipbook Copy hand-clones fields instead of registry CloneDef
    SpellEditorWindow.cs:747-751 builds a FlipbookDef field-by-field, violating the repo's own 'NEVER hand-clone' standard that all sibling Copy buttons follow — any future FlipbookDef field silently stops surviving Copy; one-line fix to _gameData.Flipbooks.CloneDef.
- [CONSOLIDATE/low] SpellPreview five age/expire/remove loops (UpdateZaps/Beams/Drains/Effects/HitEffects)
    SpellPreview.cs:1150-1223 repeats an identical 12-line lifetime loop five times (each with a redundant second removal pass), varying only in list and timer field names — one generic AgeAndExpire<T> helper over a tiny ILifetimed shape, S effort, preview-only.
- [KEEP_SEPARATE/low] Spell-vs-buff preview plumbing pairs (Ensure/Update/RenderToTarget)
    The Update pair is deliberately different state machines (spell re-triggers and live-updates every frame; buff is dirty-flag-only because every-frame sync froze the orbit animation, per in-code comments), and the Ensure/Render wrappers are 7-10 lines over two different preview classes — structural variance, not worth an interface.
- [KEEP_SEPARATE/low] UIEditorWindow per-tab list/detail triples (nine-slice/element/widget)
    List mechanics already live in the shared DrawScrollableList; the per-tab residue is genuinely different behavior (thumbnails + cache invalidation, [NS] prefix, Copy button + child-selection reset + harmonize rebake), the tabs edit plain List<T> working copies not registries, and Detail panels are caller-owned field forms.
- [KEEP_SEPARATE/low] Editor Clone* wrapper 'triples' (CloneWeapon/Armor/Shield/Spell/Buff)
    Labeler over-match: these are already one-line delegations to the canonical RegistryBase.CloneDef JSON round-trip, with a comment documenting the past consolidation that fixed the drift.
- [KEEP_SEPARATE/low] UnitRegistry CountUnitsWithWeapon/Armor/Shield + Remove*FromAll sextet
    Six 4-6-line registry queries where the weapon pair operates on List<WeaponEntry> (.Id match) and armor/shield on List<string> — selector-delegate abstraction would cost more lines than it saves.
- [KEEP_SEPARATE/low] SpellDef BuildStrikeStyle/BuildBeamStyle/BuildDrainVisuals/BuildGodRayParams
    These builders ARE the documented single source of truth shared by game (SpellEffectSystem) and editor (SpellPreview); Strike vs Beam map independently-tunable JSON field groups into one style struct, and merging them is a data-schema decision with no bug-risk reduction.

## editor-widget-toolkit
HEADLINE: The big win is finishing a consolidation the project already started: Render/DrawUtils is the documented canonical for 2D primitives yet ~17 private line/circle/ellipse copies remain, and EditorBase's four single-line field widgets have measurably drifted (cursor positioning works only in DrawTextField). Three of the labeler's nine claims dissolved on inspection (delegating overloads twice, and a nonexistent RgbToHsv duplicate).
- [CONSOLIDATE/medium] 2D shape primitives (line/thick-line/circle/ellipse) re-implemented ~17x despite Render/DrawUtils being the stated canonical
    DrawUtils.DrawLine/DrawCircleOutline exist and are documented as shared, but 10 line copies (EditorBase:509, MapEditorWindow:6764, EnvObjectEditorWindow:1999, WadingEditorPopup:473, SpellPreview:907, BuffPreview:702, GameRenderer.Units:1439/1488, BuffVisualSystem:669, DebugDraw:385) and 7 circle/ellipse copies remain — migrate via the same one-line delegating wrappers already used for DrawRectBorder, adding a DrawEllipseOutline/DrawFilledEllipse to DrawUtils (SpellPreview keeps its CameraYRatio at the call site).
- [CONSOLIDATE/medium] EditorBase single-line field family (DrawTextField/DrawIntField/DrawFloatField/DrawSearchField) duplicates focus/deactivate/draw boilerplate with user-visible drift
    Four widgets plus DrawSliderFloat's value box hand-roll the same activate/outside-click-deactivate/render sequence around shared FocusTextField, and click-to-position-cursor + drag-selection exist ONLY in DrawTextField (:979-1002) — extract a private FieldCore owning click/drag/commit; wrappers keep parse-type and stepper variance.
- [CONSOLIDATE/low] DrawSectionHeader implemented 6x across editor windows (two copies byte-identical)
    SettingsGeneralTab:204 and SettingsHordeTab:75 are identical statics; ItemEditorWindow:576, UnitEditorWindow:3585, MapEditorWindow:6727, SettingsWindow:755 vary only in style (rule vs bar vs bare label) — one EditorBase.DrawSectionHeader with a 3-value style enum + optional color covers all ~49 call sites mechanically.
- [CONSOLIDATE/low] DrawBrushSizeControl vs DrawAutoGroundSizeControl: line-for-line identical +/- stepper in MapEditorWindow
    MapEditorWindow:6577 and :6602 differ only in label and bound value while duplicating the 0..20 clamp constants — fold into a private DrawStepperRow(label, value, min, max) with 5 call sites in the same file.
- [CONSOLIDATE/low] Env def category/group distinct-queries written 5x across MapEditorWindow and EnvObjectEditorWindow
    GetEnvCategories/GetEnvGroups (MapEditorWindow:6901/6936) and GetCategories/GetExistingGroups/GetExistingCategories (EnvObjectEditorWindow:2014/2027/2043) all re-scan env defs for distinct values — put DistinctCategories()/DistinctGroups() on the environment system and keep the All/Groups/Misc sentinels, sorting, and ordering caller-side.
- [KEEP_SEPARATE/low] HandlePanelScroll overload pair in EditorBase
    The id-keyed overload (EditorBase:138) computes maxScroll from the cached content height and delegates to the base overload (:143) — proper layering with a documented reason, not duplication; labeler over-matched.
- [KEEP_SEPARATE/low] SettingsGeneralTab.Draw three overloads
    Lines 20/25/30 form a delegation chain with zero duplicated logic; the two shorter overloads are merely dead (only the 3-arg form is called from SettingsWindow:418) — optional 5-minute dead-code deletion, not a consolidation.
- [KEEP_SEPARATE/low] RgbToHsv/HsvToRgb allegedly duplicated in ColorHarmonizer and ColorPickerPopup
    Evidence is false: exactly one implementation exists (ColorPickerPopup.cs:114/142, public static) and ColorHarmonizer:60-67 calls it; at most a placement nit (could live in Core.ColorUtils), no dedup needed.
- [KEEP_SEPARATE/low] KeyToHexChar vs KeyToNumericChar switches in ColorPickerPopup
    Two ~8-line tables with genuinely different charsets (hex A-F/no period vs digits/'.'/'-') and different semantics from the general EditorBase.KeyToChar — a mode-flagged merge would trade readable tables for a conditional; overlap is 4 lines.

## ui-panel-boilerplate
HEADLINE: The panel lifecycle layer is already well-consolidated (IModalLayer/PopupManager/UIHitRegistry, shared DrawCursorTooltip), but three real duplications survive verification: the BuildingMenu/CraftingMenu side-list skeleton (with divergence-prone layout math), a tripled rich-tooltip renderer with 3x WrapText and a hand-synced palette, and HUDRenderer's copy-pasted button-row triples. Full dossier: scratchpad/dossiers/ui-panel-boilerplate.md.
- [CONSOLIDATE/medium] BuildingMenuUI vs CraftingMenuUI parallel side-list menu skeleton
    Two near-verbatim copies of Open/EnsureItemChildren/SyncItems/ComputeItemRects/click-loop/overlay-draw (incl. hand-synced colors and re-derived widget layout math that can desync hit rects); extract a SideListMenu base owning mechanics, callers keep placement/craft logic — TableCraftMenuUI is NOT a third copy (world-anchored zoom-scaled popover, structurally different).
- [CONSOLIDATE/medium] Rich cursor tooltip skeleton tripled (Inventory/Crafting/CharacterStats)
    DrawItemTooltip/DrawPotionTooltip/DrawStatTooltip share the same title+wrapped-desc+divider+colored-rows layout with 3 copies of greedy WrapText (2 byte-identical), 4 copies of the +16/+20 edge-flip placement, and a 3x duplicated Tip* palette (InventoryUI:43 even comments 'matches crafting menu / character stats') — extract UI/RichTip.cs with a 2-method font adapter.
- [CONSOLIDATE/low] HUDRenderer menu-row vs editor-row button triples
    Layout/HitTest/Draw{Menu,Editor}Buttons (HUDRenderer.cs:253-360) are ~55-line copies differing only in labels, top-Y, rect array, and palette — fold into one private ButtonRow helper in place, keeping the public HitTest wrappers so Game1 call sites are untouched.
- [KEEP_SEPARATE/low] HUDRenderer four cursor-tooltip builders
    Over-matched: the shared skeleton already exists as DrawCursorTooltip (measure/flip/clamp/draw); the Object/Belly/Corpse/Unit builders are pure per-domain line construction — exactly the caller-owns-data split CLAUDE.md prescribes.
- [KEEP_SEPARATE/low] Panel lifecycle cluster (Open/Close/Toggle/ContainsMouse, 18 files)
    Already consolidated by design: IModalLayer + PopupManager (ESC/click routing, light-dismiss/blocking) + UIHitRegistry (central MouseOverUI) leave only ~10-15 lines of visibility glue per panel, and PopupManager's docs deliberately keep the interface small and draw sites distributed — a shared panel base would abstract structural variance.
- [KEEP_SEPARATE/low] SkillBookOverlay SpellName/ItemName/UnitName/BuffName quartet
    Four one-line adapters over four distinct registry types (BuffName even has a different Humanize fallback); consolidation would need an IHasDisplayName interface across def types to save three lines.
- [KEEP_SEPARATE/low] CharacterStatsUI MakeBuffedRow / MakeBuffedRowF int/float twins
    The twins already delegate shared logic to BuffSystem.GetModifiedStat and AddBuffLines(isFloat); the remaining bodies differ line-by-line in formatting/rounding/epsilon, so an isFloat merge saves ~10 lines in one file at readability cost.

## small-util-duplicates
HEADLINE: Of the labeler's eleven claims, only five survive as real consolidations (all landing in existing homes: MathUtil, Quadtree, EditorBase) — and the angle-math family hides an actual reachable bug, HordeSystem.LerpAngle's unwrapped negative-modulo delta rotating the horde facing the long way. The other six claims are over-matches: deliberately different semantics (Idle in loco classes, world vs pixel height), layered wrappers, or structural variance that CLAUDE.md says not to abstract.
- [CONSOLIDATE/medium] Signed-angle delta/lerp math x4 (FacingUtil.AngleDiff, AnimController.SignedAngleDelta, HordeSystem.LerpAngle, Net/RemotePlayer.LerpAngleDeg)
    Four wrap implementations, two unit conventions, and HordeSystem.LerpAngle (HordeSystem.cs:571) has a real C#-modulo sign bug that rotates the horde circle the long way when delta < -pi; consolidate into MathUtil.AngleDeltaDeg/Rad + LerpAngle helpers, but leave the correct Net/RemotePlayer copy untouched per the Net/ do-not-touch rule.
- [CONSOLIDATE/medium] Quadtree.QueryRadius vs QueryRadiusByFaction copied 35-line traversal
    Traversals are byte-identical except one faction-filter line; make QueryRadius delegate to QueryRadiusByFaction(center, radius, FactionMask.All, results) — safe because the only Build overload always populates FactionBit, and no callers change.
- [CONSOLIDATE/low] Scalar Lerp(a,b,t) reimplemented in DayNightSystem, HordeSystem, ColorHarmonizer, AggressionRadiusScenario
    Four byte-identical private copies of the one-liner that already exists as MathUtil.Lerp (Core/Vec2.cs:77); delete and redirect, zero risk.
- [CONSOLIDATE/low] Editor IndexOf(IReadOnlyList<string>, string) x4 (Item/Spell/UnitEditorWindow + ReflectionPropertyRenderer)
    Four identical linear-scan helpers (legitimate since IReadOnlyList lacks IndexOf — not 'reimplementing List.IndexOf' as claimed); hoist one protected static onto EditorBase or a small EditorUtil.
- [CONSOLIDATE/low] WeatherRenderer.Init and Resize identical bodies
    Both just assign _screenW/_screenH (WeatherRenderer.cs:32-42); make Init delegate to Resize so future buffer-reallocation logic can't be added to only one.
- [KEEP_SEPARATE/low] AnimController.IsLocomotionState vs Locomotion.IsLocoClass
    Labeler's 'duplicates exactly' is false — IsLocoClass includes Idle for gait selection while IsLocomotionState deliberately excludes it for foot-phase carryover (its comment says Idle-to-Walk must reset to frame 0); merging would break one or the other.
- [KEEP_SEPARATE/low] UnitArrays.TryGetIndex vs UnitUtil.ResolveUnitIndex
    Not duplicates — ResolveUnitIndex calls TryGetIndex and layers on the InvalidUnit sentinel + Alive filter; the codebase uses the pair with consistent distinct intent (raw slot vs live-target resolve) across ~60 call sites.
- [KEEP_SEPARATE/low] Camera25D.WorldToScreen vs WorldToScreenPx
    Intentional, documented coordinate-space distinction (zoom-scaled world-unit height vs zoom-independent pixel height); GameRenderer.Corpses.cs:457 documents a past bug caused by conflating them, so the two named entry points are the value.
- [KEEP_SEPARATE/low] Row-major y*width+x index helpers (TileGrid.Index, WallSystem.Idx, Pathfinder.SectorIdx)
    Each is a one-expression helper over that system's own private grid dimensions (evidence also misplaced SectorIdx in GroundSystem — it's Pathfinder.cs:634); a shared Grid2D abstraction would be a framework around a one-liner, i.e. structural variance per CLAUDE.md.
- [KEEP_SEPARATE/low] Core/ColorUtils vs Core/HdrColor
    No overlapping bodies — ColorUtils is straight-alpha/premultiply Color math for the SpriteScope pipeline while HdrColor's methods are HdrSprite.fx-specific vertex wire encodings; they interoperate via BytesToHdr/HdrToBytes rather than duplicate.
- [KEEP_SEPARATE/low] SquadSystem.TryGet vs Get accessor pair
    Idiomatic C# Try-pattern / nullable-return pair, each a single logic-free expression with exactly one caller; zero drift surface, consolidation would be churn for one saved line.

## texture-asset-caching
HEADLINE: TextureUtil.LoadPremultiplied is already the documented canonical loader; the real duplication sits one layer up, where the get-or-load memoization idiom is hand-rolled six times and the widget texture/nine-slice cache stack is mirrored verbatim between the runtime renderer and the UI editor. Five of six evidence items confirmed as consolidatable (mostly S/M effort); the AtlasDefs file-probe pair is structural variance and should stay separate.
- [CONSOLIDATE/medium] Path->Texture2D get-or-load idiom hand-rolled 6x
    Six copies of dict->Resolve->File.Exists->LoadPremultiplied->store (Game1:4270/4283, RuntimeWidgetRenderer:752, UIEditorWindow:1025, TextureFileBrowser:394, GrassTuftRenderer:106, EnvironmentSystem:1577) have already drifted on negative-caching, logging, and resolved-vs-raw paths; one small Render/TextureCache instance class (effort M, low risk) fixes all of it.
- [CONSOLIDATE/medium] Widget texture/nine-slice/harmonized cache mirrored editor<->runtime
    UIEditorWindow duplicates RuntimeWidgetRenderer's GetOrLoadTexture/GetOrLoadNineSlice/GetTexture/GetNineSlice plus four backing dicts (~120 lines, already diverging on null-checks); extract a shared WidgetResourceCache owning the cache mechanics while each side keeps its structurally-different harmonize bake (batch parallel vs live incremental) — effort M, medium risk.
- [CONSOLIDATE/low] Game1.GetItemTexture(itemId) is dead code sharing a mixed-key cache
    The labeler's GetItemTexture-vs-GetItemTextureByPath duplicate is real but one side has zero call sites — delete GetItemTexture (Game1.cs:4283) and fold GetItemTextureByPath into the shared TextureCache; effort S, no risk.
- [CONSOLIDATE/low] SpriteAtlas sync vs split-phase load pipelines
    Load/LoadExtension (editor) and ParseMetaOnly+SetTextureAndFinalize / ParseExtensionMeta+AttachExtensionTexture (threaded startup) duplicate only ~35 lines of TextureIndex list bookkeeping — the heavy internals are already shared — so reimplement the sync pair on top of the split-phase primitives (effort S, medium risk: verify multi-sheet __N atlases in editor and startup).
- [CONSOLIDATE/low] TextureUtil premultiply loop written 3x in one file
    DecodePngPremultipliedTimed inlines a verbatim copy of DecodePngPremultipliedStb purely for a now-concluded decode-vs-pma benchmark split; extract PremultiplyBytesToColors (or have Timed call Stb) — effort S, benchmark-only behavior change.
- [KEEP_SEPARATE/low] AtlasDefs FindExtensionSheets vs FindExtensionAnimMeta probe loops
    Both probe numbered __N files but yield different contracts (png+meta pair requiring both files vs single animmeta path); the shared part is a 6-line File.Exists loop and a predicate-callback abstraction would be longer than the two concrete iterators — labeler over-matched structural variance.

## vfx-floating-text
HEADLINE: Two of the four labeled duplications are already consolidated (the wrappers are the desired pattern); the real wins are a small FloatingText helper to kill the four divergent DamageNumber height conventions, and a decision-gated merge of the trap-fire Strike path that has already drifted from ExecuteStrike by skipping magic resistance.
- [CONSOLIDATE/medium] DamageNumber floating text: 6 inline spawn sites with 4 height-anchor conventions
    The documented head-height formula (spriteH*Scale/YRatio, Game1.cs:4231-4256) lives in exactly one of six raw `new DamageNumber{...}` sites — add a FloatingText helper (HeadHeight/AddDamage/AddText) next to the struct in SpellEffectSystem.cs and migrate all six; effort S, risk low.
- [CONSOLIDATE/low] WadingWakeSystem entry/exit splash session bookkeeping (Start*/Trickle* pairs)
    TrickleEntrySplash (1292) and TrickleExitSplash (1495) are line-for-line the same scheduler over parallel field sets — fold into a SplashSession struct + shared Start/Trickle in-file, but keep the Emit* physics and SpawnTrail/SpawnBowWave separate (genuinely different geometry/ballistics).
- [INVESTIGATE/medium] Trap-fire Strike path re-implements ExecuteStrike's zap branch minus the MR gate
    Game1.cs:3971-3992 duplicates SpellEffectSystem.ExecuteStrike (471-514) and has already diverged — trap zaps skip SpellPenetration and kill credit; extract an origin-based ExecuteStrikeFrom once someone decides whether casterless trap strikes should respect magic resistance.
- [KEEP_SEPARATE/low] Cast-fail text wrappers and Cast/Summon flipbook wrappers
    Labeler over-match: SpawnHordeCapText/SpawnMissingPathText are one-liners over the canonical SpawnCastFailText, and SpawnCastEffect/SpawnSummonEffect both delegate to shared SpawnFlipbookEffect → EffectManager.SpawnSpellImpact — already single-source-of-truth.
- [KEEP_SEPARATE/low] EffectManager vs ReanimEffectSystem vs PoisonCloudRenderer vs GroundFogSystem
    Structural variance, not duplication: one-shot flipbook list vs 4-layer per-unit standup composite vs sim-field renderers with different render passes and ownership — a common abstraction would be a framework; the legitimate shared seam (one-shot flipbook VFX) already exists in EffectManager.

## mapeditor-paint-undo
HEADLINE: Real wins here are deletions and tiny extractions, not abstractions: PaintObjects is dead code, EraseWalls is a one-branch clone of PaintWalls, and two stroke-finalize blocks are copy-pasted — while the undo class family and the three cost-field rebuilds are correctly-separated structural variance the labeler over-matched.
- [CONSOLIDATE/medium] PaintObjects is dead legacy code superseded by PaintObjectsBatch
    MapEditorWindow.cs:2757 PaintObjects has zero call sites and is a fully-diverged old copy of PaintObjectsBatch (no undo, O(N) too-close scan) — delete it outright.
- [CONSOLIDATE/low] PaintWalls vs EraseWalls: identical brush loop, one branch differs
    EraseWalls (3163) is PaintWalls (3131) with the type forced to 0 — a branch PaintWalls already has; merge to PaintWalls(wallType) and extract the verbatim-duplicated mouse-up FinalizeWallStroke block (3081-3100 vs 3110-3128).
- [CONSOLIDATE/low] Batch-place stroke finalization duplicated between Objects paint and ProcGen tab
    The leftUp blocks at 2352-2381 and 5699-5727 (build UndoObjectBatchPlace, composite with UndoGroundStroke, reset fields) are near-verbatim copies — extract FinalizeBatchPlaceStroke(); the PaintProcGen brush itself is structurally different and stays separate.
- [KEEP_SEPARATE/low] Undo action classes: base class already exists, restore logic is structural variance
    All 14 undo classes already share abstract UndoAction (line 331) and each Undo() targets a structurally different system (SetVertex vs byte[] vs parallel arrays vs by-Id zone fixes) — only micro-cleanups: delete dead UndoObjectRemove (397) and optionally fold UndoObjectPlace into the batch class.
- [KEEP_SEPARATE/low] Cost-field rebuild trio is complementary pipeline stages, not overlapping paths
    RebuildCostField (terrain→base, heavily used — not legacy/dead), RebuildTieredCostFields (base→tier copies), and RebuildTieredCostFieldsRegion (documented dirty-region perf variant used by RebakeCollisionRegion) are distinct stages of one pipeline; labeler over-matched.

## equipment-name-lookups
HEADLINE: The proposed INamedRegistry facade is mostly unnecessary — clone is already consolidated on RegistryBase.CloneDef and ref-counting is structural variance; the two real wins are a small RegistryBase.NameOf(id) helper (fixing an existing blank-name display divergence across ~16 sites) and one generic dropdown-list builder in UnitEditorWindow.
- [CONSOLIDATE/low] id->DisplayName fallback one-liner re-rolled at ~16 sites
    All 11 registry def types have DisplayName but no shared NameOf(id); half the call sites use `?.DisplayName ?? id` (renders blank for empty names) while the other half check IsNullOrEmpty — add INamedDef + RegistryBase.NameOf(id), effort S, display-only risk.
- [CONSOLIDATE/low] UnitEditorWindow parallel dropdown-list builders (4 near-identical copies)
    BuildWeapon/Armor/ShieldDropdownLists are byte-identical except the registry (Unit variant differs only by a blank entry and [id] vs (id) brackets); collapse into one generic BuildDropdownLists<TDef> helper in the same file — MapDisplayToId/MapIdToDisplay are already single shared implementations, not offenders.
- [KEEP_SEPARATE/low] UnitRegistry CountUnitsWith*/Remove*FromAll equipment triples
    Weapons is a list of slot objects matched by .Id while Armors/Shields are List<string> — structural variance where a generic would just move the loop behind caller-supplied selectors; six adjacent 4-line methods with one consumer (editor delete-confirm) aren't worth a framework.
- [KEEP_SEPARATE/low] Registry clone / DeepClone JSON round-trips
    Evidence is stale: consolidation already happened — RegistryBase.CloneDef is the documented standard (memory/standard_patterns.md), editor Clone* methods are 1-line adapters adding DisplayName suffixes, and JsonClone.Deep vs UIEditor DeepClone use deliberately different serializer options (save-fidelity vs undo-fidelity) that are load-bearing.

## movement-steering-helpers
HEADLINE: The labeler's central claim is backwards — the forage/wolf-hunt helpers already route through SubroutineSteps — but verification surfaced the real duplicate it missed: the legacy AIBehavior path in Simulation.cs carries two private, already-diverged copies of the canonical MoveToward seek math, plus two small verbatim duplications (StrideCalibration formula, SoloPredator disengage) worth S-effort folds.
- [CONSOLIDATE/medium] Legacy Simulation seek helpers duplicate SubroutineSteps.MoveToward
    Simulation.MoveTowardPosition (Simulation.cs:3374) and the inline MoveToPoint case (Simulation.cs:1107) are byte-level copies of SubroutineSteps.MoveToward's pathfind-or-direct seek math, already diverged on anim handling and near-distance behavior; fold them into MoveToward via BuildAIContext exactly as AIForageMove already does (effort S, 6 call sites all inside UpdateAI).
- [CONSOLIDATE/low] StrideCalibration ResolveSuggestedCombatSpeed vs ResolveAnimVel share verbatim stride-to-velocity formula
    StrideCalibration.cs:433-473 duplicates the 4-line pixels-to-world cycle-distance formula, differing only in the time divisor; extract a private CycleDistanceWorld helper so the editor's suggested CombatSpeed can never silently drift from the runtime feet-lock velocity (effort S, same file, no caller changes).
- [CONSOLIDATE/low] SoloPredatorHandler re-implements SubroutineSteps.Disengage back-off
    SoloPredatorHandler.cs:232-252 SubDisengage is functionally identical to SubroutineSteps.Disengage+Disengage_Complete (same three field clears, same awayDir math — RatPackHandler already composes it this way); migrate that subroutine, but keep the WaitCooldown circling branch handler-owned as structural variance.
- [KEEP_SEPARATE/low] BoarForageAI/WolfPackHuntAI move helpers (labeler's core claim)
    Evidence debunked: AIForageMove/AIWolfHuntMove (Simulation.cs:3273-3289) are 3-line bridges that call SubroutineSteps.SetEffort+MoveToward — they ARE the consolidated state, and the bridge exists because sweep AIs need a Simulation handle that AIContext deliberately doesn't expose (documented in ai.md).
- [KEEP_SEPARATE/low] SteerRout and rat jitter/strafe direct steering
    Simulation.SteerRout's non-pathfound flee (per-frame pathfinding for a routed army is a documented perf trap; ORCA handles avoidance) and RatPackHandler's jitter/safety post-processing are deliberate direct steering layered on the primitives, not parallel implementations.

## wildcard-sweep
HEADLINE: The wildcard sweep yields six small clean consolidations (the only medium-severity one being the 2.5D projection formula copied from Camera25D into Renderer and inlined in GrassTuftRenderer) plus two design decisions, while several labeler claims - AnimRequest factories, SpriteScope overloads, and the 'exact' loco-predicate duplicate that actually differs by Idle handling - are intentional patterns or over-matches that should stay as-is. Full dossier at scratchpad/dossiers/wildcard-sweep.md.
- [CONSOLIDATE/medium] 2.5D world-to-screen projection re-implemented outside Camera25D
    Renderer.cs:45-59 copies Camera25D.cs:33-48's projection formula verbatim and GrassTuftRenderer.cs:319 inlines it again (plus duplicated view-bounds culling math in DeathFog/GrassTuft) - delegate to Camera25D as the single home, effort S.
- [CONSOLIDATE/low] Radial glow texture generated byte-for-byte in 3 places
    Game1.cs:2308, EnvObjectEditorWindow.cs:833, and SpellPreview.cs:228 each build the identical 64x64 quadratic-falloff glow; add TextureUtil.GetRadialGlow mirroring the existing GetWhitePixel cache, effort S.
- [CONSOLIDATE/low] WadingWakeSystem entry/exit splash session accounting duplicated
    Start/Trickle pairs (WadingWakeSystem.cs:1261/1292 vs 1460/1495) copy-paste the burst-split and accumulator loop on parallel state fields; extract a SplashSession struct while keeping the genuinely different Emit* physics separate, effort S.
- [CONSOLIDATE/low] RuntimeWidgetRenderer Set*Override family drifting
    Seven get-or-create-dict setters (RuntimeWidgetRenderer.cs:101-194) repeat the same idiom and have already drifted (only Width/Height gained clear-on-<=0, with two different shapes); collapse behind a generic private SetOverride<TK,TV>, effort S.
- [CONSOLIDATE/low] Premultiply-alpha loop tripled inside TextureUtil
    The RGB*=A/255 loop appears in DecodePngPremultipliedStb, the Timed benchmark's fallback branch, and PremultiplyAlpha (TextureUtil.cs:78/117/207); one private helper removes two copies with zero external changes, effort S.
- [CONSOLIDATE/low] EnvObjectEditorWindow pivot-drag pair duplicates interaction while sharing state
    HandlePivotDrag (867) and HandleCorpsePivotDrag (2558) duplicate the right-click drag mechanics and already share the _draggingPivot field; factor a TrackPivotDrag helper, callers keep their Y-flip and write target, effort S.
- [INVESTIGATE/low] Y-sorted puff-layer pattern mirrored across DeathFog/PoisonCloud/GrassTuft renderers
    PuffData + AddToDepthList + DrawSingle is deliberately mirrored (doc comments say so) but generation logic is structural variance; decide between a minimal DepthSpriteLayer extract vs accepting documented mirroring - the byte-identical CellHash/TileHash/HashToFloat helpers should move to a shared file either way.
- [INVESTIGATE/low] Editor preview shadow re-derives in-game shadow model with copied constants
    DrawPreviewShadow (EnvObjectEditorWindow.cs:759) hardcodes settings.json shadow defaults and approximates the BasicEffect skew for a documented reason (scissor-state constraint); decide whether it should at least read live ShadowSettings and share the lean-angle formula.
- [KEEP_SEPARATE/low] AnimRequest factory family
    Locomotion/Action/Reaction/Combat/Forced/Hold (AnimController.cs:159-196) are documented named-policy factories encoding distinct priority/interrupt/lifecycle combos - they ARE the consolidation; a parameterized constructor would reopen wrong-combo bugs.
- [KEEP_SEPARATE/low] SpriteScope Draw/DrawString overload family
    SpriteQueue.cs:145-180 deliberately mirrors SpriteBatch's overload surface to funnel colors through Material.Tint - the sanctioned renderer-migration pattern; do not touch.
- [KEEP_SEPARATE/low] Labeler over-matches: loco predicates, SkillBookState stores, modal Close, ContainsPoint
    IsLocoClass vs IsLocomotionState differ semantically (Idle included in one - merging would introduce a bug); SkillBook unlock setters are typed domain APIs with per-domain guards; modal Close mechanics already centralized in PopupManager; TriggerRegion/MapZone containment is one line each on independent structs.