# Consolidation Opportunities

Actionable queue of duplicate-implementation cleanups. Source: the 2026-07-06 full-codebase
semantic-duplication review — details, evidence, and migration sketches per item in
[docs/consolidation-review/](../docs/consolidation-review/README.md) (README lists the
shipped bugs; `dossiers/<unit>.md` has file:line evidence and the proposed canonical design).

Items are grouped by unit. Effort S/M/L; ⚠ = consolidating also fixes a confirmed live bug.
Remove items when done or judged not worthwhile.

## Fixes a shipped bug (do first)

- ⚠ **unit-spawning** (M): `Game1.SpawnUnit` re-implements `Simulation.SpawnUnitByID` — summons/map/dev spawns miss skill-tree intrinsic buffs. Make SpawnUnit delegate. Also: extract one `BuildUnitAnimData` factory (3 copies, 2 drop `SetAnimTimings` → editor timing overrides ignored).
- ⚠ **casting-pipelines** (M): Strike execution triplicated (SpellEffectSystem.ExecuteStrike / CasterUnitHandler.TryCast / ProcessTrapFireEvents) with diverged MR gate, damage flags, kill attribution. Merge into a caster-agnostic `SpellEffectSystem.ExecuteStrike`.
- ⚠ **ai-handler-boilerplate** (M): Sentry skeleton (Idle/Alert/Combat/Return) quadruplicated in CombatUnit/RangedUnit/CasterUnit/SoloPredator handlers; frenzy handling only in one copy. Extract `SentryTransitions` static helper (CombatTransitions style).
- ⚠ **registry-json-io** (M): Map sidecars (triggers/zones/roads) split loader (MapData.cs) / saver (MapEditorWindow.cs) — circle trigger regions revert to rects on load; road junctions never restored. Consolidate onto attribute DTOs + Core.JsonFile. Also: main map save non-atomic (`File.Create`) — add AtomicFile stream API.
- ⚠ **buff-effect-application** (M): Potion weapon coats duplicate `WeaponBonusEffect`, whose expiry ticker doesn't exist (never-expires bug). Canonical: `Unit.BonusEffects` + a real expiry tick. Also (S): `GetModifiedStat`/`GetModifiedExtra` identical modifier math → one private `ModifyCore`.
- ⚠ **small-util-duplicates** (S): Angle delta/lerp ×4 — `HordeSystem.LerpAngle` has a negative-modulo bug (horde rotates the long way). Consolidate into MathUtil (leave Net/RemotePlayer copy alone — Net/ is do-not-touch).
- ⚠ **under-cursor-picking** (S): `TryMeleeOrGather` vs `TryAttackClick` — two diverged click-to-melee range/cooldown formulas. Extract one `TryOrderMeleeAtCursor` on `MeleeRangeUtil.Compute`.
- ⚠ **nearest-envobject-queries** (M): WorkerSystem's six private find-scans duplicate WorldQuery; `FindNearestCorpseObj` already misses the bagged-corpse exclusion. Migrate to `_sim.Query` with caller-side `IEnvQueryFilter` structs.

## Cheap S-effort wins

- **under-cursor-picking**: `FindBerryBushNear` → existing (unused!) `EnvBerryBushes` filter; `TryPickTetherEnd` → two WorldQuery calls.
- **nearest-unit-queries**: Quadtree nearest-enemy scan ×3 outside `WorldQuery.NearestEnemyOf` (Simulation forwarder + `WorldQuery?` on AIContext); `VillageThreat.FindNearestUndead` → `NearestUnitLinear` alias; trap targeting → `NearestEnemyToPoint`.
- **nearest-envobject-queries**: stragglers `FindNearestMushroom` (BoarForageAI), `FindClosestObject` (MapEditor) → `_sim.Query.NearestEnvObject`.
- **projectile-arcs**: ballistic lob/direct-fire solve copied 7-8× (Projectile.cs ×3, SpellEffectSystem ×4-5) → static `SolveLobTheta`/`BallisticVelocity` on ProjectileManager.
- **unit-spawning**: `ScatterSpot`/`InRect`/`Near` trio → one helper with region overloads (keep rng streams identical).
- **damage-application**: death-finalization block ×4 (limb-chop misses prone-snap, poison misses death anim, trigger kill is raw `Alive=false`) → `DamageSystem.Kill`; attacker-attribution tail duplicated in Apply/ApplyDirect → `StampAttacker`.
- **movement-steering-helpers**: legacy `Simulation.MoveTowardPosition` + inline MoveToPoint duplicate `SubroutineSteps.MoveToward` (already diverged); StrideCalibration formula ×2 → `CycleDistanceWorld`; `SoloPredatorHandler.SubDisengage` → `SubroutineSteps.Disengage`.
- **registry-json-io**: `WadingDefaultsFile` → Core.JsonFile (JsonFile's own doc names it as an intended user); `SkillBookData` File.WriteAllText → AtomicFile.
- **mapeditor-paint-undo**: delete dead `PaintObjects` (superseded by PaintObjectsBatch); merge `EraseWalls` into `PaintWalls(wallType)`; extract `FinalizeBatchPlaceStroke` (×2) and `FinalizeWallStroke` (×2); delete dead `UndoObjectRemove`.
- **editor-parallel-subeditors**: Flipbook Copy button hand-clones fields → `Flipbooks.CloneDef` (one line, prevents silent field loss); SpellPreview five age/expire loops → generic `AgeAndExpire<T>`.
- **small-util-duplicates**: `Quadtree.QueryRadius` → delegate to `QueryRadiusByFaction(All)`; scalar `Lerp` ×4 → MathUtil.Lerp; editor `IndexOf` ×4 → EditorBase static; `WeatherRenderer.Init` → delegate to `Resize`.
- **texture-asset-caching**: delete dead `Game1.GetItemTexture`; TextureUtil premultiply loop ×3 → one helper.
- **vfx-floating-text**: DamageNumber spawned inline ×6 with 4 height conventions → `FloatingText` helper next to the struct; WadingWakeSystem entry/exit splash bookkeeping → `SplashSession` struct.
- **wildcard-sweep**: 2.5D projection copied into Renderer + GrassTuftRenderer → delegate to Camera25D; radial glow texture ×3 → `TextureUtil.GetRadialGlow`; `RuntimeWidgetRenderer.Set*Override` ×7 → generic `SetOverride<TK,TV>`; EnvObjectEditor pivot-drag pair → `TrackPivotDrag`.
- **equipment-name-lookups**: `RegistryBase.NameOf(id)` (~16 hand-rolled `?.DisplayName ?? id` sites, half render blank names); UnitEditor dropdown builders ×4 → one generic.
- **editor-widget-toolkit**: dead `SettingsGeneralTab.Draw` overloads ×2 — delete.

## Editor/UI mechanics extractions (M, editor-only risk)

- **editor-parallel-subeditors**: generic `RegistryCrudPanel<TDef>` for the 5× list+detail+CRUD scaffold (weapon/armor/shield sub-editors + buff/flipbook popups); detail forms stay caller-owned.
- **editor-widget-toolkit**: finish DrawUtils migration (~10 line + 7 circle/ellipse private copies despite DrawUtils being documented canonical); `FieldCore` for DrawTextField/Int/Float/Search (cursor-positioning only works in one); `DrawSectionHeader` ×6 → one styled helper; brush-size steppers ×2 → `DrawStepperRow`; env category/group distinct-scans ×5 → on EnvironmentSystem.
- **ui-panel-boilerplate**: `SideListMenu` base for BuildingMenuUI/CraftingMenuUI (layout math can desync hit rects); `UI/RichTip.cs` for the tripled rich tooltip (3× WrapText, hand-synced palette); HUDRenderer ButtonRow for menu/editor button triples.
- **texture-asset-caching**: `Render/TextureCache` for the 6× get-or-load idiom; `WidgetResourceCache` shared editor↔runtime (~120 mirrored lines); SpriteAtlas sync pipeline reimplemented on split-phase primitives.
- **registry-json-io**: UI widget defs parsed twice (UIEditorWindow + RuntimeWidgetRenderer, ~600 lines TryGetProperty boilerplate, already drifting on harmonize) → RegistryBase subclasses or shared UIDefsIO.

## Design decisions needed (INVESTIGATE)

- **Unit resource model**: NecroState vs per-unit Mana — the blocker for a true single "unit casts spell" gate (also: SpellEffectSystem hardcodes Faction.Undead + necromancer re-anchor; NPC TryCast ignores spell.Category). The user's original priest-vs-necromancer observation lands here.
- **Trap strikes & magic resistance**: should casterless trap zaps respect MR/kill credit? Gate for extracting `ExecuteStrikeFrom`.
- **Paralysis**: fold the stun phase into BuffSystem's Incapacitating machinery so there's one writer to `Unit.Incap`?
- **Poison DoT**: route only death through `DamageSystem.Kill`, or add a silent `DamageType.DoT` entry (if burn/bleed are coming)?
- **Villages vs zones**: deprecate the legacy `_villages.json` population path (SpawnGroup) in favor of zones instead of merging the mirrored loops?
- **env_defs.json**: ~80-field reader (MapData) / writer (EnvironmentSystem) in different files — DTO consolidation needs custom converters and changes on-disk shape of a Drive-synced file.
- **SpellPreview trajectory drift**: editor `VelocityZ*0.5f` vs game full `sin(theta)` — intentional preview framing or drift? (Constant dedup is unambiguous either way.)
- **Y-sorted puff layers**: DeathFog/PoisonCloud/GrassTuft deliberate mirroring vs minimal `DepthSpriteLayer` extract (move the byte-identical hash helpers regardless).
- **Editor preview shadow**: should `DrawPreviewShadow` read live ShadowSettings instead of hardcoded copies of settings.json defaults?
