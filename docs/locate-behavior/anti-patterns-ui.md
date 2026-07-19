# UI / Editor / Input Anti-Patterns
*UI-, editor-, and input-specific anti patterns to avoid and principles to follow. The generic
(everywhere) anti patterns live in [anti-patterns.md](anti-patterns.md); this is the
UI/input counterpart (the rendering counterpart is
[anti-patterns-rendering.md](anti-patterns-rendering.md)). Same discipline: egregious ones get
refactored on sight and told to the main claude; regular ones get logged in
[anti-patterns-list.md](anti-patterns-list.md) and raised as fix candidates when relevant to
the caller's request.*

Each entry below was paid for by a real bug. Deep references: [ui.md](ui.md) (panels, router,
tooltips, menus, minimap), [editor.md](editor.md) (EditorBase fields/focus, map editor, the
ghost-replay writeup), [../standard_patterns.md](../standard_patterns.md) (Editor UI + UI layers
& click routing = the canonical implementations). This file is the "what not to do" index.

> **NOT here (covered elsewhere):** *draw geometry and click/hit-test geometry computed by two
> different implementations* is the cross-discipline egregious anti-pattern in
> [anti-patterns.md](anti-patterns.md) ("Rendering and click handling on different
> implementations") — it shows up in UI constantly (`Build<X>MenuLayout`, `TimeControlLayout`,
> the spellbar layout) but it is not UI-specific, so it stays in the main file. #6 below is a
> *different* failure (bad hit-test math), not the skew.

---

## Layout & lifecycle

### **Anti Pattern**: hand-rolled scroll / content-height math (double-counting the scroll offset)
`9c4dc38`: the unit-editor's `_maxPropHeight` added `_propScrollOffset` on top of a
`drawY - startDrawY` delta whose `startDrawY` *already* subtracted the scroll — so the content
height inflated with the scroll, and the draggable scrollbar became a feedback loop (dragging
grew the range it was dragging within; the thumb crawled down and kept resizing). An
indicator-only bar merely looked "slightly off"; making it interactive turned the latent
double-count into a runaway.
**Instead:** never re-derive `contentH = curY + scroll - y` or hand-write a
`HandlePanelScroll`+`BeginClip`+`SetPanelContentHeight` sandwich — build the panel with the
canonical `EditorBase.BeginScrollPanel(id, rect, topPad, bottomPad)` / `ScrollPanel.End(curY)`
(the `f4b6422` scope), which owns wheel scrolling, scissor clip, content-height measurement,
and the draggable scrollbar. Scroll state lives in EditorBase keyed by panel id. See
[../standard_patterns.md](../standard_patterns.md) "Editor UI".

### **Anti Pattern**: layout that reads a size from — and mutates — shared/live def state, then breaks on the degenerate open
`63c203b`: the build menu read its item-cell size from the def's *first template child*, then
removed ALL item children and re-added clones. Once the construction gate let the menu open
with **zero** items, that empty open **deleted the 70×70 template out of the shared widget
def**, so every later open fell back to the hardcoded crafting-row size and the panel wrapped
to one column. A degenerate (empty / zero-unlock / no-selection) open corrupted shared state
that later opens depended on.
**Instead:** cache the template/size in instance fields the first time it's seen so an empty
open can't destroy it; make the zero-item / empty-selection path non-destructive. Whenever a
layout mutates a shared/registry def in place, ask "what does the *empty* case do to it?"

### **Egregious Anti Pattern**: caching session-scoped systems at one-shot Init (stale-session refs)
`8caa74f` / `2069375`: `BuildingMenuUI` and `TableCraftMenuUI` snapshotted `_envSystem` / `_sim`
/ `_glyphs` / `_resources` in a one-shot `Init` (that never re-runs). `StartGame` disposes the
old `GameSession` (which `ClearDefs()`s its env system), so after exiting to the main menu and
re-entering, both menus still pointed at the **dead** session — empty building list, and
glyph/table actions mutating the discarded world.
**Instead:** a UI class must not hold a *field* to a session-owned system. Read it **live**
through an expression-bodied property over `Game1.Instance` (e.g.
`private EnvironmentSystem _envSystem => Game1.Instance._envSystem;`). This is the UI face of the
"direct over inject" principle and the GameSession-recreate trap — anything the session owns
(`_sim`, env defs, glyphs, resources, `_sim.Query`, AnimMeta) is recreated on
`StartGame`/`StartScenario` and must never be captured across that boundary. See
[game1-partials.md](game1-partials.md) (GameSession inverse trap) and the sim-side twin
`SetAnimMeta`-lost bug in [anti-patterns-list.md](anti-patterns-list.md).

---

## Mouse / click routing

### **Egregious Anti Pattern**: a UI class keeping its OWN mouse / press-gesture history
`e183bc1` (+`81ae17b`): `EditorBase` held private `_mouse` / `_prevMouse` / `_pressStartPos`.
Closing an editor via `[X]` flips `_menuState` **mid-Draw**, which skips the gated
`EndDrawFrame`, freezing that private state as a complete **ghost of the closing click**. The
next mouse-open's first Draw ran before `UpdateInput` (menuState still `None` at the gate) and
**replayed the old `[X]` click**, insta-closing the editor on the 2nd+ open.
**Two anti-patterns to avoid:**
1. **Duplicating input state.** Don't keep a private copy of mouse / prev-mouse / press-start —
   read through the shared `Core/InputState.cs` (`DrawPrevMouse` snapshotted unconditionally
   every Draw so it can't go stale, `PressStartPos` stamped in `Capture`, `ConsumeGesture()`).
2. **A press gesture surviving a UI-mode switch.** Any `_menuState` transition must invalidate
   the in-flight press gesture, so a click that switches UI modes can't *also* fire a widget
   that only just appeared under the cursor. (The retracted "SuppressClicksUntilRelease"
   band-aid is NOT the fix — invalidate the gesture at the source.)

### **Egregious Anti Pattern**: ad-hoc raw input reads / positional UI draws that bypass the router
The UIRouter campaign (`f5eb8e7`, `3b90265`, `de432e3`, `fca9178`) exists to kill this: an
`_input.LeftPressed` / `Mouse.GetState()` check in `Game1.Update`, or a positional UI draw
outside the layered list, leaks the click to whatever is drawn beneath and lets a widget
hover-react where a click would never land.
**Instead:** every clickable/drawable UI surface is a `UILayer` seat in `Game1._uiRouter`
(registered in the Game1 ctor). Input walks the z-ordered list top-down; drawing walks the
SAME list bottom-up — so "drawn on top ⇔ clicked first" is **structural**, and moving a widget
above/below something is a band change, not a moved draw call. Panels get a router-*masked*
input for free (press edges only when `InputGranted`; cursor parked off-screen via
`HoverStolen` when another layer owns it), and a widget may only hover-highlight when its layer
`IsHovered`. Never re-check "who consumed the click" by hand. See
[../standard_patterns.md](../standard_patterns.md) "UI layers & click routing" and [ui.md](ui.md)
"THE UIRouter".

### **Anti Pattern**: hit-test math on an unbounded / wrong-sourced relative coordinate
`284b626`: a sidebar tab handler was gated on `overPanel` (which folds in the HUD rows drawn
over the editor). The menu-button row overlaps tab-row 1's y-band, so a click there reached the
tab math with a **negative** `relX`; C# integer division truncates toward zero, so up to one
tab-width left of the panel edge resolved to **column 0** and silently selected the Ground tab.
The gate also read the override-aware `MousePos` while the math used the raw mouse — two
coordinate sources for one decision.
**Instead:** (a) gate the hit-test on the **actual rect** the widgets occupy (`IsPanelAt`), and
bound `relX`/`relY` to that rect before any `/cellW` column/row math — negative or
past-the-edge coordinates must not fall through to index 0/last; (b) the gate and the math must
read the **same** coordinate source. (This is *bad hit-test math*, distinct from the draw-vs-
hit-test skew in the main file.)

---

## Keyboard

### **Anti Pattern**: scattered `if (KeyPressed(X))` instead of the central HotkeySystem
`16796ea`: ~30 edge-press `if`-sites in `Game1.Update` collapsed into
`Necroking/Game/HotkeySystem.cs` — a hotkey = context (Global/Session/Hud/Gameplay/GameOver) +
key combo (per-modifier Ignore/Down/Up) + delegate, registered in `Game1.Hotkeys.cs`. The
dispatcher centrally applies the **text-input gate** and **UI-layer key claims**
(`WasKeyPressedUnhandled` + consume on fire) and resolves most-specific-mods-wins on shared
keys. The bug this class of scatter caused: typing in an unpaused map-editor field used to fire
gameplay hotkeys (cast spells).
**Instead:** register a new hotkey in the `Game1.Hotkeys.cs` table, don't add a raw key check.
Gate editor/field keys on `ui.IsKeyboardCaptured` (text field + open combo filter + color
picker), not `IsTextInputActive` alone, and check BOTH Ctrl keys. This is the keyboard twin of
the router click-ownership rule above.

---

## Timing & feedback

### **Egregious Anti Pattern**: transient / dev / diagnostic state written into persisted settings
`905fce3`: the dev `hover` / `hover_obj` commands wrote `Tooltips.ShowHoverHighlight = true`
into the **live** `Settings` object so the highlight would draw during a diagnostic — but
Game1's `Exiting` handler saves `Settings` to `user settings/settings.json` on every clean
exit, so **one** diagnostic `hover necro` permanently flipped the player's saved preference.
**Instead:** a transient/diagnostic override must NOT touch the serialized settings object.
Expose a derived gate — `persistentSetting || transientActive` (the `Game1.HoverHighlightOn`
pattern: draw gates read the derived value; the dev command flips a transient flag and `hover
clear` restores the player's own preference automatically). Distinguish this from a control
that *is* the real setting (the `depthfog`/`H` toggle flips a genuine `Performance` setting on
purpose — persistence there is correct).

### **Anti Pattern**: per-window feedback timers copy-pasted instead of the shared base
Censused in [anti-patterns-list.md](anti-patterns-list.md): a `_statusTimer` field duplicated
across `ItemEditorWindow` / `MapEditorWindow` / `UnitEditorWindow` / `WallEditorWindow` even
though `EditorBase` already exposes `StatusTimer`. Each copy is a single-source-of-truth
violation and (per the delayed-execution principle) a hand-ticked countdown that should live on
the base.
**Instead:** use `EditorBase.StatusTimer`; add UI feedback timers to the shared base, not per
window. (For non-render "do X after N seconds" logic, prefer the `ScheduledTasks` framework —
see [anti-patterns.md](anti-patterns.md).)

---

## Tooltips

### **Anti Pattern**: drawing a tooltip / topmost overlay inline where it's requested
`52a022a` / `477c799` (cursor tooltips drawn inside `HudChromeLayer`, at the bottom of the Hud
band, so the `AggressionBarLayer` covered them) and `74216a7` (map-editor Units/Objects grid
tooltips were **hardware-scissored** to the tab's `BeginClip` rect and clipped at the panel
edge). A tooltip drawn where it's computed gets clipped by an enclosing scissor or covered by a
higher layer.
**Instead:** request it through the global `Game1.Tooltips` (`TooltipSystem`) frame-scoped
queue — `RequestLines` (cursor-anchored) / `RequestText` (rect-anchored) / `RequestCustom`
(defer any draw callback for RichTip / warm-panel visuals). `TooltipHostLayer` drains it at
`UIBand.Tooltip`, **after** every band has drawn and every editor scissor clip has closed, so a
tooltip can never be clipped or covered. Grid/hover requests are gated while a
popup/dropdown/color-picker is open (Tooltip band > Popup). See [ui.md](ui.md) "cursor
tooltips" + the z-order trap.

### **Anti Pattern**: resolving a hover / tooltip target from the neutral cursor state
`9e8bf21`: a neutral / default mouse position resolved to a hover target and popped a tooltip in
the middle of the screen. A "nothing is really hovered" state must not be treated as a hit.
**Instead:** guard hover/tooltip resolution against the neutral/default position (no hit → no
request), the same way the router parks a stolen cursor off-screen rather than at a real
coordinate.

---

## Related
- [anti-patterns.md](anti-patterns.md) — the generic (everywhere) anti patterns + the
  cross-discipline draw-vs-hit-test skew.
- [anti-patterns-rendering.md](anti-patterns-rendering.md) — the draw-layer counterpart.
- [anti-patterns-list.md](anti-patterns-list.md) — known live instances in the code.
- [ui.md](ui.md) — panels, the UIRouter, tooltips, menu screens, MouseOverUI, the minimap.
- [editor.md](editor.md) — EditorBase fields/focus, map editor, the ghost-replay writeup.
- [../standard_patterns.md](../standard_patterns.md) — Editor UI + UI layers & click routing (the
  canonical implementations these anti patterns point back to).
