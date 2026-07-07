# Consolidation Opportunities

Categorized queue (reconciled 2026-07-07 against Johan's 22 pulled commits). Source review:
[docs/consolidation-review/](../docs/consolidation-review/README.md) (dossiers have file:line
evidence per item). ⚠ = also fixes a confirmed live bug. Effort S/M/L.

**Awaiting user selection of which categories/items to execute.** Item IDs (A1, B3, ...) are
stable — reference them when choosing. Remove items when done or declined.

## Done by Johan (pulled 2026-07-07 — removed from queue)

- NPC caster unification: `ICasterResources` gate, caster-agnostic `TryStartSpellCast`,
  category-aware AI casting, faction un-hardcoded, per-spell cooldowns. (was: casting-pipelines
  #1 NPC half + #2; supersedes the "shared gate" half of the resource-model question)
- `AwarenessSystem.FindClosestThreat` → WorldQuery via new `DetectableFrom` filter (supersedes
  our KEEP_SEPARATE — the variable radius fit the filter API after all; pattern now blessed).
- Horde `LerpAngle` short-way bug fixed in place (HordeSystem.cs:589). Consolidation of the
  4 angle-math implementations remains as hygiene (→ C10).
- Blocking/collision queries consolidated behind WorldQuery facade + shared circle math.
- UIRouter phases 1–4 + exclusive panel docking (re-scopes the ui-panel items; panel lifecycle
  even more settled as KEEP_SEPARATE).

## A — Certain (mechanical, verified, no design judgment; behavior changes already sanctioned)

- A1 ⚠ (S) `Game1.SpawnUnit` → delegate to `Simulation.SpawnUnitByID` (summons/map/dev spawns
  gain intrinsic buffs — Q1 yes); one `BuildUnitAnimData` factory (2 of 3 copies drop
  `SetAnimTimings`). [unit-spawning]
- A2 ⚠ (M) Map sidecar round-trips: circle trigger regions + road junctions (loader/saver split)
  → attribute DTOs + JsonFile; atomic main-map save (AtomicFile stream) + SkillBookData atomic
  write. (Q5 lossless) [registry-json-io]
- A3 ⚠ (S) `WeaponBonusEffect` expiry tick (documented Tick doesn't exist) + fold potion coats
  into BonusEffects at their existing 300s; delete 4 coat fields. (Q4 resolved) [buff-effect-application]
- A4 ⚠ (S) `TryMeleeOrGather` vs `TryAttackClick`: one `TryOrderMeleeAtCursor` on
  MeleeRangeUtil. [under-cursor-picking]
- A5 ⚠ (M) Traps through the shared cast pipeline (the remaining Q3 half — Johan did NPCs):
  `ProcessTrapFireEvents` strike → `SpellEffectSystem` executor; MR/attribution from def tags. [casting-pipelines]
- A6 ⚠ (M) `SentryTransitions` for CombatUnit/RangedUnit/CasterUnit/SoloPredator (frenzy applies
  to everyone — Q2 yes). [ai-handler-boilerplate]
- A7 ⚠ (S) Flipbook Copy → `Flipbooks.CloneDef` (violates repo clone standard; silent field loss). [editor-parallel-subeditors]
- A8 (S) `DamageSystem.Kill` (4 inconsistent death-finalization sites) + `StampAttacker`
  (verbatim attribution tail ×2). [damage-application]
- A9 (S) BuffSystem `ModifyCore` (GetModifiedStat/GetModifiedExtra identical math, zero
  call-site change). [buff-effect-application]
- A10 (S) Projectile arc solver: `SolveLobTheta`/`BallisticVelocity` on ProjectileManager
  (7–8 game copies) + SpellPreview constants dedup. [projectile-arcs]
- A11 (S) Dead code: `PaintObjects`, `UndoObjectRemove` (build even warns), `Game1.GetItemTexture`,
  SettingsGeneralTab dead Draw overloads ×2.
- A12 (S) Trivial dedups: scalar Lerp ×4 → MathUtil; editor IndexOf ×4 → EditorBase;
  WeatherRenderer.Init → Resize; Quadtree.QueryRadius → delegate; TextureUtil premultiply ×3.
- A13 (S) WorldQuery rewires with existing/trivial filters: FindBerryBushNear (unused filter!),
  TryPickTetherEnd, FindNearestMushroom, MapEditor FindClosestObject, VillageThreat alias,
  trap targeting. [nearest-*-queries]
- A14 (S) Legacy `Simulation.MoveTowardPosition` + inline MoveToPoint → `SubroutineSteps.MoveToward`
  (byte-copies, already diverged). [movement-steering-helpers]
- A15 (S) `WadingDefaultsFile` → Core.JsonFile (JsonFile's docs name it as intended user). [registry-json-io]

## B — High confidence (clear duplication; small new-API shape or many call sites)

- B1 ⚠ (M) WorkerSystem six finders → `_sim.Query` + IEnvQueryFilter structs (fixes bagged-corpse
  gap). [nearest-envobject-queries]
- B2 (S-M) `Simulation.FindNearestEnemyIndex` forwarder + WorldQuery handle on AIContext
  (SubroutineSteps.FindClosestEnemy, 12 call sites). [nearest-unit-queries]
- B3 (S) ScatterSpot/InRect/Near trio → one helper with region overloads (keep rng streams
  identical). [unit-spawning]
- B4 (M) UI widget defs single parser (UIEditorWindow + RuntimeWidgetRenderer ~600 lines →
  RegistryBase subclasses or UIDefsIO). Notify-to-pull protocol on file-shape change (Q8 OK'd). [registry-json-io]
- B5 (M) DrawUtils primitive migration (~10 line + 7 circle/ellipse private copies; canonical
  documented). [editor-widget-toolkit]
- B6 (M) EditorBase `FieldCore` (text/int/float/search fields; cursor-positioning drift is
  user-visible). [editor-widget-toolkit]
- B7 (S) DrawSectionHeader ×6 → one styled helper; DrawStepperRow ×2; env category/group
  distinct-scans ×5 → EnvironmentSystem. [editor-widget-toolkit]
- B8 (M) `Render/TextureCache` for the 6× get-or-load idiom (drifted on negative-caching/paths). [texture-asset-caching]
- B9 (S) `FloatingText` helper for DamageNumber (6 spawn sites, 4 height conventions). [vfx-floating-text]
- B10 (S) StrideCalibration `CycleDistanceWorld` (editor suggestion vs runtime formula);
  SoloPredator SubDisengage → SubroutineSteps.Disengage. [movement-steering-helpers]
- B11 (S) PaintWalls/EraseWalls merge + FinalizeWallStroke / FinalizeBatchPlaceStroke extracts
  (each duplicated ×2). [mapeditor-paint-undo]
- B12 (S) `RegistryBase.NameOf(id)` (~16 sites, half render blank names) + UnitEditor generic
  dropdown builder ×4. [equipment-name-lookups]
- B13 (S each) Wildcard smalls: Camera25D projection delegation (Renderer + GrassTuftRenderer);
  TextureUtil.GetRadialGlow ×3; RuntimeWidgetRenderer SetOverride<TK,TV> ×7; EnvObjectEditor
  TrackPivotDrag; SpellPreview AgeAndExpire<T> ×5; WadingWake SplashSession. [wildcard-sweep]

## C — Moderate confidence (real duplication; scope/design tradeoff — review individually)

- C1 (M) `RegistryCrudPanel<TDef>` for the 5× list+detail+CRUD editor scaffolds. Real drift, but
  it's a mini-framework — worth it only if editors keep growing. [editor-parallel-subeditors]
- C2 (M) `SideListMenu` base for BuildingMenuUI/CraftingMenuUI (re-check scope against UIRouter
  before starting). [ui-panel-boilerplate]
- C3 (S-M) `UI/RichTip` shared rich-tooltip (3 copies, 3× WrapText, hand-synced palette). [ui-panel-boilerplate]
- C4 (S) HUDRenderer ButtonRow (menu/editor triples verified still present post-UIRouter — but
  UI layer is actively moving; coordinate with Johan). [ui-panel-boilerplate]
- C5 (M) `WidgetResourceCache` editor↔runtime (~120 mirrored lines; keep the two harmonize bakes
  separate). [texture-asset-caching]
- C6 (S, medium risk) SpriteAtlas sync pipeline reimplemented on split-phase primitives
  (threading — needs multi-sheet verification). [texture-asset-caching]
- C7 (M) env_defs.json DTO consolidation (~80-field reader/writer in different files; needs
  custom converters; changes Drive-synced file shape — notify-to-pull, Q12 OK'd). [registry-json-io]
- C8 (M) Paralysis stun phase → Incapacitating buff (single writer to Unit.Incap; judge
  recommends yes; gameplay-feel check needed). [buff-effect-application]
- C9 (S) Poison DoT entry point in DamageSystem (route death through Kill at minimum — that part
  is A8; the DamageType.DoT entry depends on burn/bleed plans). [damage-application]
- C10 (S) Angle-math hygiene: 4 implementations (deg/rad conventions) → MathUtil (bug already
  fixed in place by Johan; remaining value is drift prevention; Net/ copy stays). [small-util-duplicates]
- C11 (S) Villages SpawnGroup vs zones SpawnZoneGroup: deprecate `_villages.json` path or merge
  loops (deprecation decision). [unit-spawning]
- C12 (L, own project) Necromancer-as-normal-unit (Q9 direction confirmed; Johan's
  ICasterResources did the casting half — remaining: HP/stats/HUD/save/ghosts. Requires the
  agreed sub-plan + obstacles report BEFORE implementation). [casting-pipelines]
- C13 (S) SpellPreview 0.5f trajectory vs game full sin(theta) — visual side-by-side first,
  then either fix or comment-document. [projectile-arcs]
- C14 (S) Puff-layer shared hash helpers move (byte-identical ×3); full DepthSpriteLayer extract
  probably not worth it (documented mirroring). [wildcard-sweep]

## D — Low value / opportunistic only

- D1 Editor preview shadow reading live ShadowSettings instead of copied constants. [wildcard-sweep]
- D2 ApplyFrenzy manual Permanent loop → ApplyBuffWithDuration(...,0f); CombatTransitions stale
  doc comment (names non-users); fold UndoObjectPlace into batch class. [misc dossier nits]
- D3 MakeBuffedRow/MakeBuffedRowF int/float twins (judge leaned keep; ~10 lines saved at
  readability cost). [ui-panel-boilerplate]
