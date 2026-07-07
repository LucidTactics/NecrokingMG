# Standard Patterns

Canonical implementations of common patterns. Consult before writing new code
that might overlap; update when a new standard is established. (Referenced by
CLAUDE.md → "Standard Patterns Reference".)

## Draw colors (straight alpha everywhere — 2026-07-04)

- **Author draw colors as STRAIGHT alpha** (`new Color(r,g,b,128)` means "that hue
  at 50%"). The draw surface encodes per the open material — never call
  `Color.FromNonPremultiplied`, `ColorUtils.Premultiply`, or hand-scale RGB by A
  for a draw tint (that now double-converts → dimmer).
- **Draw through a `SpriteScope`**: queue callbacks get one; immediate-mode code
  uses `Game1.Scope`, `EditorBase.Scope`, or a per-class `Scope => _batch;`
  accessor (implicit `SpriteBatch → SpriteScope` conversion exists and is always
  color-correct). Raw `scope.Batch` is the escape hatch for native encodings
  (HDR vertex pack, additive-via-A=0 trick) and third-party extension draws
  (FontStashSharp text: `scope.Batch.DrawString(..., scope.EncodeTint(color))`).
- **Fades scale A only**: `ColorUtils.Fade(c, t)` — never `color * t` on a color
  headed into a converting draw.
- **All batch opens go through `Material.Begin`** (it stamps `Materials.Open`,
  which the conversion consults). Special raw batches call
  `Materials.NoteAdHocBatch()`. Full map: docs/locate-behavior/render.md
  ("Premultiplied alpha" section).

## Editor UI (EditorBase)

- **Overlay contract** — any popup/dialog drawn over an editor wraps its draw in
  `ui.BeginOverlay(layer)` … `ui.EndOverlay()` (EditorBase.cs). This blocks the
  host's widgets (immediately + next-frame pre-raise) and makes the popup's own
  widgets interact at `layer`. Layers: 1 = sub-editor/manager popup, 2 =
  dropdown / texture browser, 3 = confirm dialog / color picker / topmost.
  NEVER set `ui.InputLayer` manually or use the old save/force-0/restore idiom.
  Hand-rolled raw-mouse handlers inside an overlay gate on
  `ui.IsInputBlocked(ui.EffectiveLayer(0))`.
- **Clip-aware hit-testing** — all widget hover/click checks go through
  `IsHovered`/`HitTest`, which respect the active `BeginClip` scissor rect;
  content scrolled out of a clipped panel is inert. Hand-rolled hit-tests must
  use `ui.HitTest(rect)`, not `rect.Contains(mouse)`.
- **Buttons fire on release with press-origin capture** — `DrawButton` only
  clicks if the press started inside it. Lists/fields activate on press.
- **Field focus** — every single-line field select-alls on focus
  (`FocusTextField`). Field ids must embed object identity when the same field
  is reused across selectable objects (ReflectionPropertyRenderer does this
  automatically via object hash); otherwise call `ClearActiveField()` on every
  selection-change path.
- **Hotkey gating** — gate editor hotkeys / Game1 F-keys on
  `ui.IsKeyboardCaptured` (text field + open combo filter + color-picker box),
  not `IsTextInputActive` alone. Check both Ctrl keys.
- **Draw order at end of an editor's Draw**: panels → own popups →
  `DrawDropdownOverlays()` → `DrawColorPickerPopup()` last. Both are
  double-draw-guarded for nested editors.

## Combat / gameplay math

- **Target leading (intercept prediction)** — `GameSystems.Combat.InterceptUtil`
  (Necroking/Game/Combat/InterceptUtil.cs). `PredictPosition(from, targetPos,
  targetVel, travelSpeed)` = where a moving target will be when your
  projectile/leap arrives (iterative linear intercept, default 2 passes);
  `ClampLeadOvershoot(from, ledPoint, maxRange, fraction=0.3)` = WC3-style
  cap letting the lead stretch +30% past ability range. Used by arrow
  ballistics (FireArrowAt) and pounce (InitiatePounceWithWeapon). Any spell
  launching something at a moving unit calls this — never re-derive the
  intercept inline. Leading happens in the CALLER; position consumers
  (SpawnArrow, BeginPounce) take the already-led point.
- **Melee engage range** — `GameSystems.Combat.MeleeRangeUtil.Compute`
  (single source for "am I close enough to melee", sim + AI handlers).
- **World queries (nearest / under-cursor / in-radius) — `_sim.Query`**
  (`WorldQuery`, Necroking/Game/WorldQuery.cs, added 2026-07-06). NEVER write
  a new `for (...) { bestSq }` scan over units/env objects/corpses — call
  `_sim.Query.NearestEnemyToPoint / NearestEnvObject / NearestCorpse /
  UnitsInRadius / …` with a prebuilt filter (`EnvForagables`, `EnvWorkerHomes`,
  `EnvByDefIndex`, `CorpseExclude.Free`) or a small caller-side struct filter
  for odd gates (see `Game1.WorldClicks.StockedCorpsePiles`). Bounded unit
  queries on sim-tick paths use the quadtree; UI/paused code uses the linear
  methods (`UnitUnderCursor`, `NearestEnemyToPoint`) — the quadtree is stale
  outside `Simulation.Tick`. Returns are use-this-frame indices; never cache
  `_sim.Query` in a field (recreated with the session). Still-unmigrated
  linear scans (WorkerSystem finds, CorpseInteractionManager, SpellCasting/
  Projectile corpse scans, AI VillageThreat/BoarForageAI) should move here
  when touched.
- **Blocking / standability — `_sim.Query.IsSpotBlocked(pos, radius)`**
  (added 2026-07-07): walls + env collision circles, the one "can a unit
  stand here" for teleport/dodge landings, spawn spots, AI destinations;
  `IsWallBlocked` for walls/terrain only. NEVER hand-roll a
  `GetCost(...) == 255` footprint loop (that's `TileGrid.OverlapsImpassable`)
  or the env collision-circle math (that's
  `EnvironmentSystem.GetCollisionCircle` — `es = def.Scale*obj.Scale`, offset
  and radius both scale). Placement spacing = `EnvironmentSystem.
  CanPlaceObject` (same circle + `PlacementRadius`). Full blocking map:
  docs/locate-behavior/blocking.md.

## Data / registries

- **Cloning defs** — `registry.CloneDef(src, newId)` (RegistryBase): JSON
  round-trip with the registry's own serializer options, so clone fidelity ==
  save/load fidelity. Non-registry POCOs: `Core.JsonClone.Deep(src)`. NEVER
  write field-by-field clone functions — they silently drop fields added later.
- **Unique ids for "+ New"** — loop `while (registry.Get(id) != null)`;
  `RegistryBase.Add` is an upsert and will silently overwrite on collision.
- **Editor auto-save** — `JsonFile.SaveIfChanged` / `WriteStringIfChanged`
  (used by GameSettings.Save and RegistryBase.Save): serializes and skips the
  disk write when content is unchanged. Safe to call from liberal dirty flags.

## Assets

- Textures edited and saved back to disk must round-trip STRAIGHT alpha
  (Texture2D.FromStream), never `TextureUtil.LoadPremultiplied` data — saving
  premultiplied pixels re-darkens soft edges every cycle. Premultiply a copy
  for display only (see EnvObjectEditorWindow.PremultiplyCopy).
- All file paths through `GamePaths.Resolve`; maps through `GamePaths.MapsDir`.

## UI layers & click routing (UIRouter — 2026-07-07)

- **Every clickable/drawable UI surface is a `UILayer`** in the single z-ordered
  list owned by `Game1._uiRouter` (`UI/UIRouter.cs`, bands in `UI/UILayer.cs`:
  World → Hud → Panels → Overlay → HudTop → Toast → Menu → Editor → Popup →
  Tooltip). Input walks it top-down (`DispatchInput`, one call in Game1.Update);
  drawing walks the SAME list bottom-up (`Draw`, called from DrawHudBlock) — so
  "drawn on top ⇔ clicked first" is structural. Never add an ad-hoc
  `_input.LeftPressed` check in Game1.Update or a positional draw call for UI.
- **New panel** → give it a `PanelLayer` seat in the Game1 ctor (visibility
  getter, update delegate, `.WithDraw(...)`, optional drag provider for mouse
  capture). It gets masked input for free: press edges only when `InputGranted`,
  cursor parked off-screen when `HoverStolen`.
- **Hover/click sync**: a widget may only hover-highlight when its layer
  `IsHovered` (the router's hover owner = the layer a click would land in).
- `Game1.Popups` (`PopupManager`) is EDITOR-INTERNAL sub-popups only, seated via
  `ModalStackLayer`. Game panels must not push to it.
- Headless verification: dev verbs `ui_click <x> <y>`, `ui_key escape`, `ui_rects`.
