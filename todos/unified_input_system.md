# Unified Input System — Status

Most of the original plan has landed. Keeping this note around to track the remaining loose ends and to record the shape of what shipped.

## What's Done

### Phase 1 — Core InputState (DONE)
- [Necroking/Core/InputState.cs](Necroking/Core/InputState.cs) implements the full design: raw + prev mouse/kb, derived `LeftPressed/Down/Released` (+ right), `ScrollDelta`, `MousePos`, keyboard helpers (`WasKeyPressed`, `WasKeyReleased`, `IsKeyDown/Up`), and consumption flags (`ConsumeMouse`, `ConsumeScroll`, `ConsumeKb`, `MouseOverUI`).
- `ConsumeMouse()` also sets `MouseOverUI = true` — clicking a UI element implies hovering.
- `Capture()` is called once per frame; resets consumption flags.

### Phase 2 — Game1 migration (DONE)
- Single `Mouse.GetState()` / `Keyboard.GetState()` call remains in [Necroking/Game1.cs:1334](Necroking/Game1.cs:1334) (the intended capture point).
- `_mouseOverUI` flag fully replaced with `_input.MouseOverUI`.
- World-interaction checks (spell cast, unit select, potion targeting) gated on `!_input.MouseOverUI`.

### Phase 3 — Game UI migration (DONE)
All take `InputState` parameter now:
- [InventoryUI.Update(InputState)](Necroking/Game/InventoryUI.cs:141)
- [BuildingMenuUI.Update(InputState, ...)](Necroking/Game/BuildingMenuUI.cs:251)
- [CraftingMenuUI.Update(InputState, ...)](Necroking/Game/CraftingMenuUI.cs:231)
- [UIManager.Update(InputState)](Necroking/UI/UIManager.cs:58)
- [TrapPlacementManager.Update(InputState, ...)](Necroking/Game/TrapPlacementManager.cs:19)
- HUDRenderer migrated off `Mouse.GetState()`.

### Phase 4 — Camera & Renderer (DONE)
- [Camera25D.HandleInput(InputState, float)](Necroking/Render/Camera25D.cs:19)
- No `Mouse.GetState()` / `Keyboard.GetState()` in `Necroking/Render/` anymore.

### Phase 5 — Editor migration (DONE)
- [EditorBase](Necroking/Editor/EditorBase.cs:48) owns an internal `InputState _input`; `Update(..., InputState? input = null)` propagates from the caller.
- Zero `Mouse.GetState()` / `Keyboard.GetState()` calls in `Necroking/Editor/`.
- Original editor `InputLayer` priority system is still in place on top of `InputState` (as planned).

### Phase 6 — Hover highlights (LARGELY DONE)
- Inventory slot hover highlight in [InventoryUI.cs:209](Necroking/Game/InventoryUI.cs:209).
- Hover logic present in HUDRenderer (34 refs — spell bar slots), BuildingMenuUI, CraftingMenuUI, all editor windows, `UIElement`, `ColorPickerPopup`.
- Cursor swap hand/arrow over interactive UI: [Game1.cs:2405](Necroking/Game1.cs:2405) `Mouse.SetCursor(overInteractiveUI ? Hand : Arrow)`.

### Phase 7 — Cleanup (DONE)
- Grep for `Mouse.GetState` / `Keyboard.GetState` across `Necroking/` → only the single capture in Game1 + a `GamePad`/Esc fallback in the standalone `Necroking.Editor` app (different project, not a concern).

## What's Left

Small residuals worth a look when someone next touches this:

- **Manual audit of the Phase 7 test list.** The grep passed, but the behavioral tests were never explicitly exercised:
  - Clicking a spell-bar slot shouldn't move units beneath it.
  - Scrolling over the inventory shouldn't zoom the camera.
  - Opening an editor dropdown should block clicks on the map beneath.
  - Confirm hover highlights actually appear on every interactive UI element (not just that the code path exists).
- **Hover styling consistency.** Hover code paths exist in many places but they were each added independently — worth a visual pass to check that the tint/brighten amount is uniform across game UI and editor UI. Candidate for a small shared helper if it's inconsistent.
- **No hover for world entities yet** (unit selection highlight on mouseover, etc.) — this was explicitly out of scope in the original plan and still is. Mentioned only so the next reader doesn't think it was forgotten.
