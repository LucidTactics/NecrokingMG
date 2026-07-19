# Migrate editor-window hotkeys to HotkeySystem

## Context
A central hotkey system now exists (2026-07-19):
- `Necroking/Game/HotkeySystem.cs` — the dispatcher: context + key combo (per-modifier
  Ignore/Down/Up sensitivity) + delegate; central text-input gate; UI-layer consumption
  respected (`WasKeyPressedUnhandled`); most-specific-mods-wins on shared keys.
- `Necroking/Game1.Hotkeys.cs` — the game's registration table (all non-editor keys:
  debug/editor F-keys, HUD panel toggles, ESC layering, spell casts, gameplay keys).
- Dispatched from `Game1.Update` at four phase points: `Always` (pre-menu, Alt+Enter),
  `PreUI` (before hit-rect rebuild/router), `PostUI` (after router — ESC lives here),
  `World` (inside the world-running gate, after the channel-hold release check).

## What's left (deliberately not migrated)
- **Editor windows**: `Editor/EditorBase.cs` (~59 key-check sites) and
  `Editor/MapEditorWindow.cs` (~20). Most are immediate-mode *widget-internal* keys
  (text fields, list nav WASD/arrows, combo filters) that should probably NOT become
  hotkeys — only editor-level shortcuts (save, tool switching, etc.) are candidates.
  Likely needs a new `HotkeyContext` per editor (e.g. MapEditor) and care with the
  map editor's WASD camera-pan ownership (`CameraInputEnabled` / `AllowWasdListNav`).
- **UI overlay one-offs**: `UI/ScenarioListScreen.cs` ESC-back (runs before the PreUI
  dispatch — would need its own context/phase), `UI/GrimoireOverlay.cs` Delete during
  assign-pick.
- **Leave alone**: the layered ESC-close claim mechanism in `UI/UILayer.cs` /
  `UI/PopupManager.cs` (`ConsumeKey(Escape)` during router dispatch) — that IS the
  consumption layer HotkeySystem builds on, not a scattered if-statement.
- **Holds stay holds**: WASD movement / arrow camera pan / Shift sprint in Game1.Update
  are `IsKeyDown` holds, not press edges — out of scope unless HotkeySystem grows a
  held-mode.

## Gotchas for the migration
- `ui_key` dev-server verb dispatches synthetic input only through `_uiRouter` —
  synthetic keys never reach HotkeySystem (so hotkeys can't be driven via devctl).
- A fired hotkey consumes its key; editor code reading the same key raw
  (`WasKeyPressed`) will still see it — use `WasKeyPressedUnhandled` in migrated code.
