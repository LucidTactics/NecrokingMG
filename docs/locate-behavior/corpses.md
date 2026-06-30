# Corpses — data model, pickup/carry, and player interaction

Covers how dead bodies are represented and how the player picks them up / carries
them. There is **no separate "pile of corpses" object** — a pile is just many
`Corpse` instances sitting at nearby positions in the flat `Simulation` corpse list.

## Data model

### `Necroking/Game/Simulation.cs` — `Corpse` + the corpse list
- **`class Corpse`** (top of file) — the body record. Key fields:
  `Position` (Vec2), `CorpseID` (stable int id), `UnitType`, `UnitDefID`,
  `Dissolving`, `ConsumedBySummon`, `Bagged`/`BaggedByUnitID`,
  `DraggedByUnitID` (`GameConstants.InvalidUnit` when free, else the carrier's
  unit id), `LerpStartPos` (pickup/putdown anim anchor), `ReanimInstanceId`,
  physics arc fields (`InPhysics`, `Z`, `VelocityXY`…).
- **`Simulation`** owns `List<Corpse> _corpses`. Public accessors:
  `Corpses` (read-only), `CorpsesMut` (mutable list), `FindCorpseByID(id)`,
  `FindCorpseIndexByID(id)`. Corpses are created in the unit death path (in
  `UpdateCombat`) and for editor-placed bodies via a convert-to-corpse helper.
- **Carry state lives on the *unit*, not the corpse**: `Unit.CarryingCorpseID`,
  `Unit.CorpseInteractPhase` (0=idle, 1=WorkStart, 4=Pickup, 5=PutDown),
  `Unit.BaggingCorpseID`/`BaggingTimer`. See `Movement/UnitSystem.cs` &
  `Game1.Animation.cs` for how the phases advance.
- **Look/edit here when**: changing what a corpse stores, finding a corpse by id,
  enumerating bodies near a point, or marking a corpse as taken.

### `Necroking/Data/Registries/CorpseSettings.cs`
- Tuning loaded from `data/corpse.json` — currently only per-angle body-bag
  sprite pivots (`CorpseAnglePivot`, `GetPivot`, `ApplyToAtlas`). New corpse-wide
  tunables (pickup radius, etc.) belong here.

## Pickup / carry logic (THE reuse target)

### `Necroking/Game/CorpseInteractionManager.cs`
- **`static void TryInteract(Simulation sim, int necroIdx)`** — the canonical
  "pick up the nearest free corpse near the necromancer" routine. Priority:
  if carrying → start PutDown (phase 5); else find nearest free corpse within
  `searchRange = 3f*3f` (squared) skipping `Dissolving`/`ConsumedBySummon`/already
  dragged, set `CarryingCorpseID`, `CorpseInteractPhase = 4` (Pickup), and mark
  `c.DraggedByUnitID = nu.Id`. (A mothballed body-bag path behind
  `GameConstants.UseBodyBag` adds a bag/pickup two-step; currently off.)
- **This is the function to call for "pick up a corpse" — do not reimplement it.**
  It already does the proximity gate. New work only needs to (a) decide *which*
  corpse and (b) get the necromancer adjacent first.

### `Necroking/Game1.Animation.cs`
- Advances `CorpseInteractPhase`/`BaggingTimer`, lerps the corpse to the hand on
  Pickup, drops it on PutDown, removes the corpse from `CorpsesMut` when loaded
  onto a table. **Look here when** the pickup animation timing/lerp is wrong.

## Player input — clicks and the F-key (all in `Necroking/Game1.cs`, `Update`)

- **Cursor→world**: `_camera.ScreenToWorld(new Vector2(mouse.X, mouse.Y), screenW, screenH)`
  → `mouseWorld` (a `Vec2`). Computed once at the top of the player-input block.
- **F-key corpse interaction** (`if (_input.WasKeyPressed(Keys.F))`): first tries
  table load-in (`TableSystem.FindTableUnderCursorInRange`), otherwise calls
  `Game.CorpseInteractionManager.TryInteract(_sim, necroIdx)`. This is the existing
  pick-up trigger.
- **Corpse-under-cursor hover** (`_hoveredCorpseIdx`): right after the F-block, a
  loop picks the nearest corpse to `mouseWorld` within
  `Settings.Tooltips.GroundPickRadius` (squared), skipping consumed/dissolving/
  bagged/dragged bodies. **This is the exact hit-test you need for "click a corpse"**
  — it already gives you the clicked corpse index. (Used today only for the
  reanim tooltip.)
- **World left-click dispatch**: `if (!_input.MouseOverUI && _input.LeftPressed && !_input.IsMouseConsumed …)`
  handles click-on-table (`TableSystem.FindTableUnderCursor` → open craft menu) and
  click-on-grave (`FindGraveUnderCursor` → roster). New "click a corpse" handling
  slots in next to these, calling `_input.ConsumeMouse()` when it claims the click.
- **Walk-to-a-point mechanism**: `_devWalkTarget` (a `Vec2?`, field ~line 442).
  When set, the WASD block (`if (_devWalkTarget.HasValue)`) steers the necromancer
  toward it via `moveDir = toGoal.Normalized()` and clears it on arrival
  (`toGoal.LengthSq() <= 0.25f`) or any manual WASD. **This is the move-to-target
  primitive to reuse** for "walk adjacent to the clicked corpse."

## Proximity-gather prior art (the pattern to mirror)

### `Necroking/Game/ForagableSystem.cs`
- `FindNearest(Vec2 fromPos, float maxDist)`, `StartCollection(int objIdx)`
  (spawns an arc-to-hand pickup), `TickAutoPickup(dt, enabled)`. Wired in
  `Game1.cs` as `_foragables.*` and in `Game1.Crafting.cs`
  (`FindNearestForagable`, `StartForagableCollection`). The right-click-to-forage
  path (`_input.RightPressed` → `FindNearest(..., 2f)` → `StartCollection`) is the
  closest existing "click + in-range → gather" flow; corpse pickup should follow
  the same shape but route to `CorpseInteractionManager` for the actual pickup.

### Worker corpse-hauling (auto, not player)
- `Necroking/Game/Jobs/WorkerSystem.cs` `FindNearestCorpseObj(from)` /
  `FindNearestSource` and `AI/WorkerHandler.cs` already make NPC workers path to a
  corpse id and carry it. Reference for the "walk to corpse, then act on arrival"
  state machine if you want a more robust path than `_devWalkTarget`.

## Where new "click corpse → walk adjacent → pick up" code goes

1. **`Necroking/Game1.cs` (`Update`)** — in the world-left-click block (next to
   the table/grave clicks, ~lines 3120-3144): reuse the `_hoveredCorpseIdx`
   hit-test (or a parallel loop over `_sim.Corpses` against `mouseWorld`) to get
   the clicked corpse. If in range already, call
   `CorpseInteractionManager.TryInteract`. If not, stash the target (corpse id +
   set `_devWalkTarget` to a point ~1 unit short of the corpse so the necromancer
   stops adjacent) and remember a "pending pickup" corpse id. `_input.ConsumeMouse()`.
2. **A new pending-pickup field on `Game1`** (e.g. `private int _pendingPickupCorpseID = -1;`
   near `_devWalkTarget` ~line 442) and a check in the `_devWalkTarget` arrival
   branch (~line 2682): on arrival, if a pending corpse is set, clear it and call
   `CorpseInteractionManager.TryInteract(_sim, necroIdx)` (or a new overload that
   targets a specific corpse id — see #3). Re-validate the corpse still exists /
   isn't dragged before picking up.
3. **Optional, in `Necroking/Game/CorpseInteractionManager.cs`** — add an overload
   `TryInteract(sim, necroIdx, int targetCorpseID)` that skips the nearest-search
   and picks up that specific corpse (still gated on range + free), so a clicked
   pile yields a *deterministic* body rather than "whatever's nearest." Keep the
   existing parameterless behavior for the F-key.

## Pitfalls / gotchas

- **No "pile" entity** — a pile is just adjacent `Corpse`s. "Pick up one corpse
  from a pile" = pick the topmost/nearest among them; don't look for a pile object.
- **Range gate is squared** — `CorpseInteractionManager` uses `3f*3f` on
  `LengthSq()`. If you pre-check range yourself, compare squared distances or you'll
  be off by the sqrt.
- **Mark `DraggedByUnitID` / honor it** — a corpse with
  `DraggedByUnitID != GameConstants.InvalidUnit` is already claimed; skip it in any
  pick loop, and `TryInteract` sets it on pickup. Forgetting this lets two carriers
  fight over one body.
- **Skip non-pickable bodies** — always exclude `Dissolving`, `ConsumedBySummon`,
  and (in body-bag mode) `Bagged` corpses, matching the existing loops.
- **Carry state is on the Unit, corpse just stores `LerpStartPos`/`DraggedByUnitID`** —
  set `CarryingCorpseID` + `CorpseInteractPhase` on `UnitsMut[necroIdx]`, not on the
  corpse.
- **Don't fight movement locks** — while `CorpseInteractPhase != 0` the unit
  `IsLockedByAction()`; mouse-facing and some input are gated. Start the walk before
  setting any interact phase.
- **Consume the click** — call `_input.ConsumeMouse()` so the same left-click doesn't
  also trigger table/grave/menu handlers downstream.

## Related areas
- `game1-partials.md` — `Game1.Animation.cs` (phase advance), `Game1.Crafting.cs`
  (foragable glue), `Game1.cs` Update input block.
- `jobs-workers.md` — NPC corpse hauling (`WorkerSystem`, `WorkerHandler`).
