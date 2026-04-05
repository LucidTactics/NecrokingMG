# Unified Input System — Implementation Plan

## Problem
Input handling is scattered across 50+ direct `Mouse.GetState()`/`Keyboard.GetState()` calls in Game1, Camera25D, HUDRenderer, UIManager, MapEditorWindow, EnvObjectEditorWindow, Renderer, SettingsWindow, TextureFileBrowser, WallEditorWindow, UIEditorWindow, etc.

The `_mouseOverUI` flag in Game1 is a fragile ad-hoc system — each UI element must manually register itself, and new UI elements that forget to do so will let clicks bleed through to the game world. Scroll consumption is partially implemented in EditorBase but not in game UI. There's no hover highlight system for UI elements.

## Design

### Core: `InputState` (new class in `Core/`)

A single centralized input snapshot taken once per frame in Game1.Update(). All systems read from this instead of calling `Mouse.GetState()` / `Keyboard.GetState()` directly.

```csharp
public class InputState
{
    // Raw state (read-only after capture)
    public MouseState Mouse;
    public MouseState PrevMouse;
    public KeyboardState Kb;
    public KeyboardState PrevKb;

    // Derived helpers
    public Vector2 MousePos;
    public Vector2 PrevMousePos;
    public int ScrollDelta;
    public bool LeftPressed;   // just pressed this frame
    public bool LeftDown;      // held
    public bool LeftReleased;  // just released
    public bool RightPressed;
    public bool RightDown;
    public bool RightReleased;

    // Consumption system
    private bool _mouseConsumed;
    private bool _scrollConsumed;
    private bool _kbConsumed;

    public bool IsMouseConsumed => _mouseConsumed;
    public bool IsScrollConsumed => _scrollConsumed;
    public bool IsKbConsumed => _kbConsumed;

    public void ConsumeMouse() => _mouseConsumed = true;
    public void ConsumeScroll() => _scrollConsumed = true;
    public void ConsumeKb() => _kbConsumed = true;

    // Hover tracking
    public bool MouseOverUI;  // set by UI layer, read by game layer

    // Helpers
    public bool WasKeyPressed(Keys key) => Kb.IsKeyDown(key) && PrevKb.IsKeyUp(key);
    public bool IsKeyDown(Keys key) => Kb.IsKeyDown(key);

    // Call once per frame at top of Update()
    public void Capture(MouseState mouse, MouseState prevMouse,
                        KeyboardState kb, KeyboardState prevKb)
    {
        Mouse = mouse; PrevMouse = prevMouse;
        Kb = kb; PrevKb = prevKb;
        MousePos = new Vector2(mouse.X, mouse.Y);
        PrevMousePos = new Vector2(prevMouse.X, prevMouse.Y);
        ScrollDelta = mouse.ScrollWheelValue - prevMouse.ScrollWheelValue;
        LeftPressed = mouse.LeftButton == ButtonState.Pressed && prevMouse.LeftButton == ButtonState.Released;
        LeftDown = mouse.LeftButton == ButtonState.Pressed;
        LeftReleased = mouse.LeftButton == ButtonState.Released && prevMouse.LeftButton == ButtonState.Pressed;
        RightPressed = mouse.RightButton == ButtonState.Pressed && prevMouse.RightButton == ButtonState.Released;
        RightDown = mouse.RightButton == ButtonState.Pressed;
        RightReleased = mouse.RightButton == ButtonState.Released && prevMouse.RightButton == ButtonState.Pressed;
        // Reset consumption flags each frame
        _mouseConsumed = false;
        _scrollConsumed = false;
        _kbConsumed = false;
        MouseOverUI = false;
    }
}
```

### Update Order (enforced in Game1.Update)

```
1. Capture input state
2. UI layer processes input (top-to-bottom z-order)
   - Each UI element checks hit, consumes mouse/scroll if handled
   - Sets MouseOverUI = true if mouse is within any UI bounds
   - Sets hover state on hovered elements
3. Editor layer (if active) processes remaining input
4. Game world processes remaining unconsumed input
5. Camera processes remaining unconsumed input
```

### Hover System

Add to existing UI elements (spell bar, inventory, building menu, crafting menu, HUD buttons):
- Each element's update checks if mouse is within bounds
- If hovered: set a `Hovered` flag, render with highlight tint/outline
- Standard hover visual: slight brighten (`Color.Lerp(base, Color.White, 0.15f)`) or scale pulse

For the editor UI (EditorBase), hover is already partially handled via input layers — extend with visual feedback.

---

## Implementation Steps (ordered by dependency)

### Phase 1: Core InputState class
- [ ] **1.1** Create `Necroking/Core/InputState.cs` with the design above
- [ ] **1.2** Add `InputState` field to Game1, capture it at top of `Update()`
- [ ] **1.3** Replace `_prevKb`, `_prevMouse`, `_prevScrollValue` fields in Game1 with InputState equivalents
- [ ] **1.4** Replace `WasKeyPressed()` helper in Game1 with `_input.WasKeyPressed()`

### Phase 2: Migrate Game1 input consumers
- [ ] **2.1** Replace all `mouse.LeftButton == ButtonState.Pressed && prevMouse...` patterns in Game1 with `_input.LeftPressed` etc
- [ ] **2.2** Replace `_mouseOverUI` flag with `_input.MouseOverUI`
- [ ] **2.3** Migrate scroll delta calculations to `_input.ScrollDelta`
- [ ] **2.4** Replace `anyTextInputActive` keyboard guard with `_input.IsKbConsumed`
- [ ] **2.5** Add `_input.ConsumeMouse()` calls in UI update methods (spell bar, inventory, building menu, crafting menu) when they handle a click

### Phase 3: Migrate game UI classes
- [ ] **3.1** `InventoryUI.Update()` — accept `InputState` instead of raw `MouseState`/`KeyboardState`, call `ConsumeMouse()` on click
- [ ] **3.2** `BuildingMenuUI.Update()` — same migration, remove `ContainsMouse` in favor of consumption
- [ ] **3.3** `CraftingMenuUI.Update()` — same migration
- [ ] **3.4** `HUDRenderer` — stop calling `Mouse.GetState()`, accept `InputState`
- [ ] **3.5** `UIManager.Update()` — accept `InputState` instead of polling `Mouse.GetState()`

### Phase 4: Migrate Camera and Renderer
- [ ] **4.1** `Camera25D.HandleInput()` — accept `InputState`, respect `IsMouseConsumed`/`IsScrollConsumed` for zoom, respect `IsKbConsumed` for pan (still allow WASD when typing isn't active)
- [ ] **4.2** `Renderer` (debug keys) — accept `InputState`

### Phase 5: Migrate Editor system
- [ ] **5.1** `EditorBase` — replace internal mouse/kb tracking with `InputState`. Keep `InputLayer` system but wire it through InputState consumption
- [ ] **5.2** `MapEditorWindow` — eliminate all `Mouse.GetState()` calls (there are ~20+), use passed `InputState`
- [ ] **5.3** `EnvObjectEditorWindow` — same (~8 calls)
- [ ] **5.4** `WallEditorWindow`, `SettingsWindow`, `UIEditorWindow` — same
- [ ] **5.5** `TextureFileBrowser` — same

### Phase 6: Hover highlights
- [ ] **6.1** Add `bool Hovered` property to game UI elements (spell bar slots, inventory slots, building menu items, crafting items)
- [ ] **6.2** In each UI element's update: set `Hovered = bounds.Contains(input.MousePos) && !input.IsMouseConsumed`
- [ ] **6.3** In each UI element's draw: apply hover tint when `Hovered` (brighten or outline)
- [ ] **6.4** Add hover cursor change support (arrow → hand when over interactive UI)
- [ ] **6.5** Editor UI: add hover highlight to buttons, dropdowns, list items in EditorBase

### Phase 7: Cleanup & validation
- [ ] **7.1** Grep for remaining `Mouse.GetState()` / `Keyboard.GetState()` — should be zero outside of Game1.Update's initial capture
- [ ] **7.2** Test: click spell bar doesn't move units beneath it
- [ ] **7.3** Test: scroll over inventory doesn't zoom camera
- [ ] **7.4** Test: editor dropdown open blocks clicks on map beneath
- [ ] **7.5** Test: hover highlights appear on all interactive UI elements

---

## Files Affected (by phase)

**Phase 1-2:** `Core/InputState.cs` (new), `Game1.cs`
**Phase 3:** `Game/InventoryUI.cs`, `Game/BuildingMenuUI.cs`, `Game/CraftingMenuUI.cs`, `Render/HUDRenderer.cs`, `UI/UIManager.cs`
**Phase 4:** `Render/Camera25D.cs`, `Render/Renderer.cs`
**Phase 5:** `Editor/EditorBase.cs`, `Editor/MapEditorWindow.cs`, `Editor/EnvObjectEditorWindow.cs`, `Editor/WallEditorWindow.cs`, `Editor/SettingsWindow.cs`, `Editor/UIEditorWindow.cs`, `Editor/TextureFileBrowser.cs`
**Phase 6:** Same UI files from Phase 3 + EditorBase
**Phase 7:** All of the above (verification pass)

## Key Design Decisions

1. **Single `InputState` instance** — not a static/singleton, passed explicitly so dependencies are visible
2. **Consumption is one-way** — once consumed, lower-priority systems can't unconsume. This is simple and correct.
3. **`MouseOverUI` is separate from `ConsumeMouse`** — hovering over UI sets `MouseOverUI` (prevents world interaction) but doesn't consume the mouse (UI element hasn't been clicked). Clicking a UI element calls `ConsumeMouse()` which also implies `MouseOverUI`.
4. **Editor keeps its `InputLayer` system** — but layered on top of InputState. The editor has overlapping panels (dropdowns, popups) that need finer-grained layering than simple consumption. InputLayer gates editor-internal priority; InputState gates editor-vs-game priority.
5. **No hover on world entities yet** — this plan covers UI hover only. World entity hover (unit selection highlight etc.) is a separate system.

## Migration Strategy

Do it in phases so the game stays buildable and testable after each phase. Phase 1-2 can be done in one session (Game1 is the main consumer). Phase 3-5 can be parallelized across files. Phase 6 is cosmetic and can be done last.
