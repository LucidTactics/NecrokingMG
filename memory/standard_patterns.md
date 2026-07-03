# Standard Patterns

Canonical implementations of common patterns. Consult before writing new code
that might overlap; update when a new standard is established. (Referenced by
CLAUDE.md → "Standard Patterns Reference".)

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
