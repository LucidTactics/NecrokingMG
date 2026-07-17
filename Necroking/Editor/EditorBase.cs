using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using Necroking.Core;
using Necroking.Render;

namespace Necroking.Editor;

/// <summary>
/// Shared SpriteBatch-based editor UI infrastructure.
/// Provides panel drawing, buttons, text fields, scrolling, and input handling.
/// </summary>
public class EditorBase
{
    public EditorBase()
    {
        // OnCancel fires when the manager light-dismisses the dropdown
        // (outside click) or routes ESC to it. Clearing _activeFieldId is the
        // existing "close dropdown" signal; SyncDropdownModalState below pops
        // the layer when it sees the state change next frame.
        _dropdownLayer.OnCancelAction = () =>
        {
            _activeFieldId = null;
            Necroking.Game1.Popups.Pop(_dropdownLayer);
        };
        // ESC while typing should only defocus the field, not close the whole
        // editor. While a text field is active this non-blocking layer sits on
        // the modal stack so PopupManager routes ESC here (and consumes it)
        // instead of the editor layer below. Non-blocking: world clicks still
        // fall through (clicking the world defocuses via the widgets' own
        // outside-click path).
        _textFieldLayer.OnCancelAction = () =>
        {
            if (IsTextInputActive) _activeFieldId = null;
        };
    }

    /// <summary>Linear index-of over an IReadOnlyList&lt;string&gt; (which lacks
    /// List.IndexOf), -1 on miss. Shared by the editor windows' option-list lookups.
    /// Public rather than protected because the callers compose EditorBase, not derive
    /// from it.</summary>
    public static int IndexOf(IReadOnlyList<string> list, string value)
    {
        for (int i = 0; i < list.Count; i++)
            if (list[i] == value) return i;
        return -1;
    }

    // Colors
    public static readonly Color PanelBg = new(25, 25, 40, 240);
    public static readonly Color PanelHeader = new(40, 40, 65, 250);
    public static readonly Color PanelBorder = new(80, 80, 120);
    public static readonly Color ItemBg = new(35, 35, 55, 220);
    public static readonly Color ItemHover = new(50, 50, 80, 230);
    public static readonly Color ItemSelected = new(60, 60, 110, 240);
    public static readonly Color ButtonBg = new(55, 55, 85, 240);
    public static readonly Color ButtonHover = new(70, 70, 110, 250);
    public static readonly Color ButtonPress = new(40, 80, 140, 255);
    public static readonly Color InputBg = new(15, 15, 25, 230);
    public static readonly Color InputBorder = new(70, 70, 100);
    public static readonly Color InputActive = new(80, 100, 160);
    public static readonly Color TextColor = new(200, 200, 220);
    public static readonly Color TextDim = new(140, 140, 165);
    public static readonly Color TextBright = new(240, 240, 255);
    public static readonly Color AccentColor = new(100, 140, 220);
    public static readonly Color DangerColor = new(200, 80, 80);
    public static readonly Color SuccessColor = new(80, 200, 100);

    // State
    /// <summary>Draw surface over the shared batch — colors are straight alpha,
    /// encoded by the open material. THE way editor code draws; the raw batch is
    /// only reachable via <c>Scope.Batch</c> for the sanctioned Begin/End sites
    /// below (scissor clip, dropper capture).</summary>
    public SpriteScope Scope => Necroking.Game1.Instance.Scope;
    internal Texture2D _pixel = null!;
    /// <summary>1×1 white pixel texture, exposed for editor consumers
    /// that draw their own primitives (e.g. tilted lines via rotated
    /// quad). Same instance the built-in DrawRect uses, so we don't
    /// allocate a duplicate.</summary>
    public Texture2D PixelTexture => _pixel;
    protected SpriteFont? _font;
    protected SpriteFont? _smallFont;
    protected SpriteFont? _largeFont;
    internal GraphicsDevice _gd = null!;
    // Live views over the centralized InputState — EditorBase deliberately keeps
    // NO private mouse history of its own. A private copy only updated while an
    // editor is open goes stale across close/open; it once replayed the ghost of
    // the closing click into the freshly opened editor, insta-closing it.
    // _prevMouse is the previous DRAW's state rather than the previous Update's,
    // so a click survives fixed-timestep catch-up (several Updates per Draw would
    // collapse the one-frame edge before the Draw pass reads it — without this
    // ~6/7 of clicks are dropped on slow frames).
    internal MouseState _mouse => _input.Mouse;
    internal MouseState _prevMouse => _input.DrawPrevMouse;
    internal KeyboardState _kb;
    internal KeyboardState _prevKb;
    internal InputState _input = new();
    /// <summary>Current mouse position from the centralized input state.</summary>
    public Vector2 MousePos => _input.MousePos;
    protected int _screenW, _screenH;

    // INF02: Global scroll consumed flag
    private bool _scrollConsumed;
    public bool IsScrollConsumed => _scrollConsumed;
    public void ConsumeScroll() => _scrollConsumed = true;

    /// <summary>
    /// Standard per-panel mouse-wheel scroll handler. Mutates <paramref name="scroll"/> in place.
    /// Skips when input is blocked for <paramref name="layer"/>, when scroll was already consumed
    /// this frame, or when the mouse is outside <paramref name="rect"/>. On success it marks the
    /// scroll consumed so dropdowns/popups above don't bubble into panels below.
    /// </summary>
    /// <param name="rect">Hit-test rect (typically the panel's clip rect).</param>
    /// <param name="scroll">Current scroll offset; clamped to [0, maxScroll].</param>
    /// <param name="maxScroll">Upper bound for scroll. Pass float.MaxValue for unbounded.</param>
    /// <param name="sensitivity">Pixels scrolled per wheel notch (default 0.3).</param>
    /// <param name="layer">Input layer (0=main, 1=popup, 2=dropdown).</param>
    /// <returns>true if the panel scrolled this frame.</returns>
    public bool HandlePanelScroll(Rectangle rect, ref float scroll, float maxScroll = float.MaxValue,
        float sensitivity = 0.3f, int layer = 0)
    {
        if (IsInputBlocked(EffectiveLayer(layer))) return false;
        if (_scrollConsumed) return false;
        if (!HitTest(rect)) return false;
        int delta = _mouse.ScrollWheelValue - _prevMouse.ScrollWheelValue;
        if (delta == 0) return false;
        scroll = Math.Clamp(scroll - delta * sensitivity, 0f, maxScroll);
        SetMouseOverUI();
        ConsumeScroll();
        return true;
    }

    // Per-panel content height measured last frame, keyed by panelId. Feeds the
    // id-keyed HandlePanelScroll overload so it can clamp to the end BEFORE the
    // content is laid out — without this, maxScroll is only known after drawing
    // and a wheel notch at the bottom overshoots for one frame, then snaps back
    // (the jarring effect this avoids).
    private readonly Dictionary<string, float> _panelContentHeights = new();

    /// <summary>
    /// Mouse-wheel scroll handler that clamps to the end using the content height
    /// recorded on the previous frame (keyed by <paramref name="panelId"/>), so a
    /// wheel notch at the bottom stops at the edge instead of overshooting and
    /// snapping back. Pair with <see cref="SetPanelContentHeight"/> — call it once
    /// the panel's content height is known (after layout) to feed next frame's
    /// clamp. <paramref name="viewH"/> is the visible panel height.
    /// </summary>
    public bool HandlePanelScroll(Rectangle rect, ref float scroll, string panelId, int viewH,
        float sensitivity = 0.3f, int layer = 0)
    {
        float cached = _panelContentHeights.TryGetValue(panelId, out float h) ? h : 0f;
        float maxScroll = Math.Max(0f, cached - viewH);
        return HandlePanelScroll(rect, ref scroll, maxScroll, sensitivity, layer);
    }

    /// <summary>Record a panel's laid-out content height for next frame's id-keyed
    /// <see cref="HandlePanelScroll(Rectangle, ref float, string, int, float, int)"/>
    /// clamp. Call once per frame after the content height is measured.</summary>
    public void SetPanelContentHeight(string panelId, float contentHeight)
        => _panelContentHeights[panelId] = contentHeight;

    // INF03: Global mouse-over-UI flag
    private bool _mouseOverEditorUI;
    public bool IsMouseOverUI => _mouseOverEditorUI;
    public void SetMouseOverUI() => _mouseOverEditorUI = true;

    // Modal stack integration. The combo dropdown is a light-dismiss popup —
    // outside click closes it and ESC closes it. The layer is shared across
    // every combo in this EditorBase instance (only one can be open at a time
    // because _activeFieldId is single-valued) and its Panel rect updates each
    // frame as the dropdown draws. SyncDropdownModalState reconciles "is the
    // dropdown open?" with "is the layer on Game1.Popups?" once per frame so
    // we don't have to manually Push/Pop at every state-change site.
    private readonly Necroking.UI.ActionModalLayer _dropdownLayer = new() { LightDismiss = true };
    /// <summary>Hot-path setter the combo's draw code uses each frame to
    /// publish its current screen rect. ContainsMouse on the layer reads this
    /// directly, so a click anywhere outside it counts as "outside the
    /// dropdown" for the manager's light-dismiss logic.</summary>
    protected void SetDropdownRect(Microsoft.Xna.Framework.Rectangle r) => _dropdownLayer.Panel = r;

    // Text input state
    protected string? _activeFieldId;
    private string _inputBuffer = "";
    private float _cursorBlink;
    private double _keyRepeatTimer;
    private Keys _lastRepeatingKey;

    // Cursor and selection state (shared by single-line and text area fields)
    private int _cursorPos;           // cursor position within _inputBuffer
    private int _selectionStart = -1; // start of selection (-1 = no selection)
    private bool _selectAll;          // select-all flag set on focus
    private bool _draggingSelection;  // mouse drag in progress for selection
    private int _activeFieldInputX;   // left edge of the active input rect (for click-to-position)
    private int _activeFieldInputW;   // width of the active input rect

    // INF09: Multi-line text area state
    private int _textAreaScrollOffset;
    private int _textAreaCursorPos;

    // Scroll state (per-panel, keyed by panel ID)
    private readonly Dictionary<string, float> _scrollOffsets = new();

    /// <summary>
    /// Get the current scroll offset for a given panel, or 0 if not found.
    /// </summary>
    public float GetScrollOffset(string panelId) =>
        _scrollOffsets.TryGetValue(panelId, out float v) ? v : 0f;

    /// <summary>Set/reset a panel's stored scroll offset — e.g. scroll a
    /// detail panel back to the top when the selection changes.</summary>
    public void SetScrollOffset(string panelId, float value) => _scrollOffsets[panelId] = value;

    // Scissor clipping stack
    private readonly Stack<Rectangle> _scissorStack = new();
    // CPU-side mirror of the active clip so hit-testing can respect it: a widget
    // scrolled out of its clipped panel is invisible and must not be clickable.
    private Rectangle? _activeClip;
    private readonly Stack<Rectangle?> _clipRectStack = new();

    /// <summary>Central mouse hit-test: inside <paramref name="rect"/> AND inside
    /// the active scissor clip (if any). All widget hover/click checks must go
    /// through this (or <see cref="IsHovered"/>) so that content scrolled out of a
    /// clipped panel can't be invisibly interacted with.</summary>
    public bool HitTest(Rectangle rect)
        => rect.Contains(_mouse.X, _mouse.Y)
           && (_activeClip == null || _activeClip.Value.Contains(_mouse.X, _mouse.Y));

    /// <summary>Like <see cref="HitTest"/> but against the logical cursor
    /// (<c>_input.MousePos</c>) instead of the raw MouseState. MousePos honours
    /// the router's cursor-parking and the `mousepos` dev override, so use this
    /// for hover-only UI (tooltips) that headless dev runs need to exercise.</summary>
    public bool HitTestCursor(Rectangle rect)
    {
        int mx = (int)_input.MousePos.X, my = (int)_input.MousePos.Y;
        return rect.Contains(mx, my)
            && (_activeClip == null || _activeClip.Value.Contains(mx, my));
    }

    /// <summary>Hover tooltip for an editor field row, via the global tooltip
    /// service (draws topmost, after all scissor clips close). Anchor the whole
    /// row so hovering either the label or the control shows it. Gated on the
    /// input-layer system: a covering overlay (sub-editor popup, dropdown,
    /// color picker, confirm dialog) suppresses the tip — the tooltip band
    /// draws above popups, so an ungated tip would paint over them. Calls made
    /// inside a popup's own BeginOverlay scope still show their tips.</summary>
    public void RowTip(int x, int y, int w, int h, params string[] lines)
    {
        if (!HitTestCursor(new Rectangle(x, y, w, h))) return;
        if (IsInputBlocked(EffectiveLayer(0))) return;
        if (IsColorPickerOpen || IsDropdownOpen) return;
        Necroking.Game1.Tooltips.RequestLines(lines);
    }

    // Input layer system (0=main, 1=popup, 2=dropdown, 3=confirm dialog)
    private int _inputLayer;
    public int InputLayer { get => _inputLayer; set => _inputLayer = value; }
    // The color picker is a topmost modal (layer 3): while it consumes input it
    // blocks widgets at every layer below 3 — including popup widgets at 1/2.
    public bool IsInputBlocked(int layer) => _inputLayer > layer || (layer < 3 && _colorPicker.ConsumesInput);

    // === Overlay / widget-layer contract ===
    // Problem this solves: widgets process input during Draw against a per-frame
    // _inputLayer, so a popup that raises the layer inside its OWN draw call only
    // blocks widgets drawn AFTER it — everything the host drew earlier that frame
    // already took the click. The old workaround ("save layer, force 0, restore"
    // inside popups) additionally defeated the dropdown/color-picker pre-raises.
    //
    // Contract: a popup wraps its draw in BeginOverlay(layer)/EndOverlay().
    //   • NotifyOverlay records the level; UpdateInput pre-raises _inputLayer to
    //     it next frame (same trick the dropdown uses), blocking every widget the
    //     host draws before the popup. It also raises immediately for widgets
    //     drawn after the popup in the same frame.
    //   • The widget base layer makes every widget drawn inside the scope
    //     hit-test at `layer` instead of the hardcoded 0, so the popup's own
    //     widgets work without any per-call layer args or layer forcing.
    // Layer conventions: 1 = sub-editor/manager popup, 2 = dropdown, 3 = confirm
    // dialog / topmost modal.
    private readonly Stack<int> _widgetLayerStack = new();
    private int _widgetLayer;
    private int _overlayLevelThisFrame;
    private int _overlayLevelLastFrame;

    /// <summary>Effective layer a widget interacts at: the max of its explicit
    /// layer argument and the current overlay widget base layer.</summary>
    public int EffectiveLayer(int layer = 0) => Math.Max(layer, _widgetLayer);

    /// <summary>Current widget base layer (0 outside overlays). Hand-rolled
    /// raw-mouse handlers inside an overlay should gate on
    /// <c>IsInputBlocked(EffectiveLayer())</c>.</summary>
    public int CurrentWidgetLayer => _widgetLayer;

    /// <summary>Declare that an overlay at <paramref name="layer"/> is open this
    /// frame WITHOUT entering a widget scope. UpdateInput pre-raises the input
    /// layer next frame so host widgets drawn before the overlay are blocked.
    /// Prefer <see cref="BeginOverlay"/>; use this alone for overlays that draw
    /// no EditorBase widgets.</summary>
    public void NotifyOverlay(int layer)
    {
        _overlayLevelThisFrame = Math.Max(_overlayLevelThisFrame, layer);
        if (_inputLayer < layer) _inputLayer = layer;
    }

    /// <summary>Enter an overlay scope: notifies the overlay level (see
    /// <see cref="NotifyOverlay"/>) and makes all widgets drawn until
    /// <see cref="EndOverlay"/> interact at <paramref name="layer"/>. Call at the
    /// top of a popup's draw, every frame it is open.</summary>
    public void BeginOverlay(int layer)
    {
        NotifyOverlay(layer);
        _widgetLayerStack.Push(_widgetLayer);
        _widgetLayer = layer;
    }

    /// <summary>Leave the innermost <see cref="BeginOverlay"/> scope.</summary>
    public void EndOverlay()
    {
        _widgetLayer = _widgetLayerStack.Count > 0 ? _widgetLayerStack.Pop() : 0;
    }

    /// <summary>Current mouse state for editor consumers that need raw
    /// position / button data without reading the OS again. Same instance
    /// the built-in widgets (DrawButton etc.) use, so input-layer checks
    /// remain consistent.</summary>
    public MouseState GetMouseState() => _mouse;
    public MouseState GetPrevMouseState() => _prevMouse;

    /// <summary>Check if a rect is hovered, respecting input layers (the max of
    /// <paramref name="layer"/> and the current overlay widget layer), and the
    /// active scissor clip. Also sets MouseOverUI flag when true.</summary>
    protected bool IsHovered(Rectangle rect, int layer = 0)
    {
        if (IsInputBlocked(EffectiveLayer(layer))) return false;
        bool hit = HitTest(rect);
        if (hit) SetMouseOverUI();
        return hit;
    }

    // Combo dropdown scroll state (keyed by fieldId)
    private readonly Dictionary<string, int> _comboScrollOffsets = new();

    // Combo filter text per-dropdown (keyed by fieldId)
    private readonly Dictionary<string, string> _comboFilterText = new();

    // Combo keyboard highlight index (-1 = no highlight)
    private int _comboHighlightIdx = -1;

    // Deferred dropdown overlay data
    private struct PendingDropdown
    {
        public string FieldId;
        public string[] Options;        // full option list (may include "(none)")
        public string[] FilteredOptions; // after applying search filter
        public int[] FilteredIndices;    // indices into Options for each filtered item
        public string CurrentValue;
        public Rectangle InputRect;
        public int ScrollOffset;
        public int MaxScroll;
        public bool NeedsScroll;
        public int VisibleCount;
        public int DropY;
        public bool FlippedUp; // INF11: dropdown flipped upward
        public bool ShowFilter; // whether the filter text box is shown
        public string FilterText; // current filter string
        public int HighlightIdx; // keyboard highlight index into FilteredOptions
        public string?[]? Tooltips; // per-option hover tooltips, indexed like Options (null = none)
        public Func<int, string?>? TooltipFor; // lazy tooltip provider, invoked with the hovered item's Options index only

        // Pre-computed item layout — single source of truth for both click and draw
        public Rectangle[] ItemRects;
        public Rectangle DropRect;
        public int ItemsY; // Y where items start (below filter if present)
    }
    private PendingDropdown? _pendingDropdown;
    private bool _dropdownOverlayConsumedClick;
    // When a dropdown consumes a mouse press, keep input blocked through the release
    // so release-based click detection (DrawButton) doesn't fire on widgets beneath.
    private bool _dropdownHoldingMousePress;
    // Eager press→drag→release gesture state for the open combo (Lib/EagerDropdown).
    // WHICH combo is open stays in _activeFieldId (the single source of truth,
    // shared with all the existing close paths); the machine only tracks the
    // gesture, so its key is always 0. Presses on a different combo's button
    // are blocked by the input-layer pre-raise and land as "outside" here.
    private readonly Necroking.Lib.EagerDropdown _comboEager = new();
    // Tracks the color picker's consume state across frames so we can swallow the
    // click that dismissed it (otherwise it falls through to a widget behind).
    private bool _colorPickerWasConsuming;
    // Slider drag capture: the slider grabbed on mouse-down owns the drag until
    // the button is released, so vertical moves don't hijack a neighbour.
    private string? _activeSliderId;

    // Screen position where the current left press began — owned by InputState
    // (stamped in Capture, invalidated by ConsumeGesture on UI mode changes).
    private Point _pressStartPos => _input.PressStartPos;
    // Guards against a shared color picker being drawn twice per frame when
    // nested editors (wall/env inside map editor) each call DrawColorPickerPopup.
    private bool _colorPickerDrawnThisFrame;
    // Set by DrawCombo while its dropdown is open; EndDrawFrame closes the
    // dropdown if the owning combo stopped being drawn (e.g. its tab was
    // switched away by a hotkey) — otherwise the pre-raised layer 2 would
    // block the whole editor with an invisible open dropdown.
    private bool _openComboDrawnThisFrame;
    // Non-blocking modal layer present while a text field is focused, so ESC
    // routes to "defocus the field" instead of closing the editor (see ctor).
    private readonly Necroking.UI.ActionModalLayer _textFieldLayer = new() { LightDismiss = false, IsBlocking = false };

    // Color picker popup (shared instance)
    private readonly ColorPickerPopup _colorPicker = new();

    public void SetContext(Texture2D pixel, SpriteFont? font, SpriteFont? smallFont, SpriteFont? largeFont)
    {
        _pixel = pixel;
        _font = font;
        _smallFont = smallFont;
        _largeFont = largeFont;
        _gd = Necroking.Game1.Instance.GraphicsDevice;
        _colorPicker.SetContext(pixel, font, smallFont);
    }

    public void UpdateInput(MouseState mouse, MouseState prevMouse, KeyboardState kb, KeyboardState prevKb,
        int screenW, int screenH, GameTime gameTime, InputState? input = null)
    {
        // Mouse state and press/release edges live in InputState now — _mouse /
        // _prevMouse / _pressStartPos are read-through properties over _input.
        // (The mouse/prevMouse params are still used below for the dropdown-hold /
        // color-picker dismiss logic, which is per-Update by design.)
        _kb = kb;
        _prevKb = prevKb;
        if (input != null) _input = input;
        _screenW = screenW;
        _screenH = screenH;
        _cursorBlink += (float)gameTime.ElapsedGameTime.TotalSeconds;

        // Propagate last frame's mouse-over-UI flag to InputState before resetting.
        // UI controls set _mouseOverEditorUI during Draw; we propagate here at the start
        // of the next frame so that game-world click handlers see it.
        if (_mouseOverEditorUI && _input != null)
            _input.MouseOverUI = true;

        // Reset per-frame flags
        // If a dropdown is open from last frame, pre-set input layer to block all widgets
        // This prevents widgets drawn BEFORE the combo from stealing clicks
        bool dropdownWasOpen = _activeFieldId != null && _activeFieldId.EndsWith("_combo");
        // Keep the eager-gesture machine in lockstep with _activeFieldId: any
        // external close (ESC, Enter-select, EndDrawFrame auto-close,
        // ResetAllState, CloseActiveDropdown) resets it here — single sync
        // point instead of chasing every close site.
        if (!dropdownWasOpen) _comboEager.Close();
        // Clear the held-press flag once the mouse has been idle for a full frame
        // (both current and previous state released), so the release event itself is still blocked.
        if (_dropdownHoldingMousePress
            && mouse.LeftButton == ButtonState.Released
            && prevMouse.LeftButton == ButtonState.Released)
            _dropdownHoldingMousePress = false;
        _inputLayer = (dropdownWasOpen || _dropdownHoldingMousePress) ? 2 : 0;
        // Overlay pre-raise: popups that drew last frame (BeginOverlay/NotifyOverlay)
        // block all lower-layer widgets this frame, exactly like the dropdown pre-raise.
        _overlayLevelLastFrame = _overlayLevelThisFrame;
        _overlayLevelThisFrame = 0;
        if (_inputLayer < _overlayLevelLastFrame) _inputLayer = _overlayLevelLastFrame;
        _scrollConsumed = false;
        _mouseOverEditorUI = false;
        _pendingDropdown = null;
        _dropdownOverlayConsumedClick = false;
        _colorPickerDrawnThisFrame = false;
        // Defensive: if a widget scope leaked (exception between Begin/EndOverlay),
        // don't let it poison every later frame.
        if (_widgetLayerStack.Count > 0) { _widgetLayerStack.Clear(); _widgetLayer = 0; }

        // Release ends any in-progress slider drag capture (robust even if the
        // captured slider isn't drawn this frame).
        if (mouse.LeftButton == ButtonState.Released)
            _activeSliderId = null;

        // Update color picker popup input (with keyboard for value box editing)
        float dt = (float)gameTime.ElapsedGameTime.TotalSeconds;
        _colorPicker.UpdateInput(mouse, prevMouse, kb, prevKb, screenW, screenH, dt);

        // When the color picker just closed (OK/ESC), swallow the dismissing
        // click so it doesn't fall through to a widget behind — same mechanism
        // the dropdowns use. The flag clears once the mouse is fully released.
        bool pickerConsuming = _colorPicker.ConsumesInput;
        if (_colorPickerWasConsuming && !pickerConsuming)
            _dropdownHoldingMousePress = true;
        _colorPickerWasConsuming = pickerConsuming;

        // Color picker is a topmost modal — block all editor input when open,
        // including popup widgets running at overlay layers 1/2.
        if (pickerConsuming && _inputLayer < 3)
            _inputLayer = 3;
        if (_dropdownHoldingMousePress && _inputLayer < 2)
            _inputLayer = 2;

        // Handle key repeat for text input
        if (_activeFieldId != null)
            HandleTextInput(gameTime);

        // Reconcile the dropdown's IModalLayer presence with its open state.
        // Single sync point means we don't have to chase every place that
        // assigns _activeFieldId to keep Push/Pop in lockstep.
        SyncDropdownModalState();
    }

    /// <summary>
    /// End-of-Draw housekeeping. The mouse edge snapshot itself moved to
    /// <see cref="Necroking.Core.InputState.SnapshotDrawFrame"/>, which
    /// GameRenderer calls unconditionally every Draw.
    /// </summary>
    public void EndDrawFrame()
    {
        // An open dropdown whose combo wasn't drawn this frame is unreachable
        // (its tab/panel went away) — close it so the pre-raised layer doesn't
        // invisibly block the editor.
        if (IsDropdownOpen && !_openComboDrawnThisFrame)
            CloseActiveDropdown();
        _openComboDrawnThisFrame = false;
    }

    /// <summary>Push or pop <see cref="_dropdownLayer"/> / <see cref="_textFieldLayer"/>
    /// on <see cref="Game1.Popups"/> to mirror whether a combo dropdown / text field
    /// is open. Cheap to call every frame — hash-lookup inside the manager.</summary>
    private void SyncDropdownModalState()
    {
        bool open = IsDropdownOpen;
        bool onStack = Necroking.Game1.Popups.Contains(_dropdownLayer);
        if (open && !onStack) Necroking.Game1.Popups.Push(_dropdownLayer);
        else if (!open && onStack) Necroking.Game1.Popups.Pop(_dropdownLayer);

        // Text-field focus layer: routes ESC to "defocus" while typing.
        bool textOpen = IsTextInputActive;
        bool textOnStack = Necroking.Game1.Popups.Contains(_textFieldLayer);
        if (textOpen && !textOnStack) Necroking.Game1.Popups.Push(_textFieldLayer);
        else if (!textOpen && textOnStack) Necroking.Game1.Popups.Pop(_textFieldLayer);
    }

    // === Drawing primitives ===

    public void DrawRect(Rectangle rect, Color color)
    {
        Scope.Draw(_pixel, rect, color);
    }

    /// <summary>
    /// Draw a line between two screen-space points using a rotated 1-pixel texture.
    /// </summary>
    public void DrawLine(Vector2 a, Vector2 b, Color color, int thickness = 1)
    {
        var diff = b - a;
        float length = diff.Length();
        if (length < 0.5f) return;
        float angle = MathF.Atan2(diff.Y, diff.X);
        Scope.Draw(_pixel, a, null, color, angle, Vector2.Zero, new Vector2(length, thickness), SpriteEffects.None, 0f);
    }

    public void DrawBorder(Rectangle rect, Color color, int thickness = 1)
    {
        Necroking.Render.DrawUtils.DrawRectBorder(Scope, _pixel, rect, color, thickness);
    }

    public void DrawText(string text, Vector2 pos, Color color, SpriteFont? font = null)
    {
        if (string.IsNullOrEmpty(text)) return;
        var f = font ?? _smallFont ?? _font;
        if (f != null)
        {
            // Round to integer pixels to avoid sub-pixel artifacts with PointClamp sampling
            pos = new Vector2((int)pos.X, (int)pos.Y);
            try { Scope.DrawString(f, text, pos, color); }
            catch { Scope.DrawString(f, SanitizeForFont(text, f), pos, color); }
        }
    }

    public Vector2 MeasureText(string text, SpriteFont? font = null)
    {
        if (string.IsNullOrEmpty(text)) return Vector2.Zero;
        var f = font ?? _smallFont ?? _font;
        if (f == null) return Vector2.Zero;
        try { return f.MeasureString(text); }
        catch { return f.MeasureString(SanitizeForFont(text, f)); }
    }

    /// <summary>Remove characters not supported by the font.</summary>
    private static string SanitizeForFont(string text, SpriteFont font)
    {
        var sb = new System.Text.StringBuilder(text.Length);
        foreach (char c in text)
        {
            if (font.Characters.Contains(c) || c == '\n' || c == '\r')
                sb.Append(c);
            else
                sb.Append('?');
        }
        return sb.ToString();
    }

    /// <summary>Find character index from pixel X position within a text string.</summary>
    private int CharIndexFromPixelX(string text, int pixelX)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        for (int i = 1; i <= text.Length; i++)
        {
            float w = MeasureText(text[..i]).X;
            if (pixelX < w - MeasureText(text[(i-1)..i]).X * 0.5f)
                return i - 1;
        }
        return text.Length;
    }

    /// <summary>Check if there's an active text selection.</summary>
    private bool HasSelection => _selectionStart >= 0 && _selectionStart != _cursorPos;

    /// <summary>Get ordered selection range (min, max).</summary>
    private (int start, int end) GetSelectionRange()
    {
        int a = Math.Min(_selectionStart, _cursorPos);
        int b = Math.Max(_selectionStart, _cursorPos);
        return (a, b);
    }

    /// <summary>Delete selected text and collapse cursor.</summary>
    private void DeleteSelection()
    {
        if (!HasSelection) return;
        var (start, end) = GetSelectionRange();
        _inputBuffer = _inputBuffer[..start] + _inputBuffer[end..];
        _cursorPos = start;
        _selectionStart = -1;
        _selectAll = false;
    }

    /// <summary>Text currently selected in the active single-line field: the whole
    /// buffer under select-all, the highlighted range otherwise, else empty.</summary>
    private string SelectedText()
    {
        if (_selectAll) return _inputBuffer;
        if (!HasSelection) return "";
        var (start, end) = GetSelectionRange();
        return _inputBuffer[start..end];
    }

    /// <summary>Drop control chars (newlines, tabs, etc.) from pasted text so a
    /// single-line field can't get a stray line break.</summary>
    private static string StripControlChars(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (char ch in s)
            if (!char.IsControl(ch)) sb.Append(ch);
        return sb.ToString();
    }

    public void DrawTexture(Texture2D texture, Vector2 position, Rectangle sourceRect,
        Color color, float rotation, Vector2 origin, float scale, SpriteEffects effects)
    {
        Scope.Draw(texture, position, sourceRect, color, rotation, origin, scale, effects, 0f);
    }

    // === Scissor Clipping ===

    /// <summary>
    /// Begin a scissor-clipped region. Ends the current SpriteBatch, sets the scissor rectangle,
    /// and begins a new SpriteBatch with ScissorTestEnable. Supports nesting via a stack.
    /// </summary>
    public void BeginClip(Rectangle clipRect)
    {
        // End the current SpriteBatch pass. Raw-batch cycle via the Scope.Batch
        // hatch: the scissor stack needs a conditional resume material, which
        // Push/PopMaterial's fixed pairing can't express.
        Scope.Batch.End();

        // Push the current scissor rect onto the stack (for nesting)
        _scissorStack.Push(_gd.ScissorRectangle);
        _clipRectStack.Push(_activeClip);
        // Nested clips intersect with the parent — an inner region can never
        // draw (or hit-test) outside the panel that contains it.
        if (_activeClip != null)
            clipRect = Rectangle.Intersect(clipRect, _activeClip.Value);
        _activeClip = clipRect;

        // Set the new scissor rectangle
        _gd.ScissorRectangle = clipRect;

        // Begin a new SpriteBatch with scissor test enabled (via Materials so
        // Materials.Open stays accurate for color encoding)
        Materials.HudScissor.Begin(Scope.Batch);
    }

    /// <summary>
    /// End a scissor-clipped region. Restores the previous scissor rectangle from the stack.
    /// </summary>
    public void EndClip()
    {
        // End the scissor-enabled SpriteBatch pass
        Scope.Batch.End();

        // Restore the previous scissor rectangle
        if (_scissorStack.Count > 0)
            _gd.ScissorRectangle = _scissorStack.Pop();
        _activeClip = _clipRectStack.Count > 0 ? _clipRectStack.Pop() : null;

        // Resume the normal SpriteBatch (matching the HUD pass in Game1.Draw)
        if (_scissorStack.Count > 0)
        {
            // Still nested - re-enable scissor with the parent clip rect
            Materials.HudScissor.Begin(Scope.Batch);
        }
        else
        {
            // Back to normal - no scissor test
            Materials.Hud.Begin(Scope.Batch);
        }
    }

    // === Panel ===

    public Rectangle DrawPanel(int x, int y, int w, int h, string title)
    {
        var rect = new Rectangle(x, y, w, h);
        DrawRect(rect, PanelBg);
        DrawRect(new Rectangle(x, y, w, 28), PanelHeader);
        DrawRect(new Rectangle(x, y + 28, w, 1), PanelBorder);
        DrawText(title, new Vector2(x + 8, y + 5), TextBright, _font);
        return rect;
    }

    // === Button ===

    public bool DrawButton(string text, int x, int y, int w, int h, Color? bgOverride = null, int layer = 0)
    {
        var rect = new Rectangle(x, y, w, h);
        bool hovered = IsHovered(rect, layer);
        bool pressed = hovered && _mouse.LeftButton == ButtonState.Pressed;
        // Fire on release, but only if the press also STARTED on this button —
        // pressing elsewhere and dragging onto the button must not click it.
        bool clicked = hovered && _mouse.LeftButton == ButtonState.Released && _prevMouse.LeftButton == ButtonState.Pressed
            && rect.Contains(_pressStartPos);

        Color bg = bgOverride ?? ButtonBg;
        if (pressed) bg = ButtonPress;
        else if (hovered) bg = ButtonHover;

        DrawRect(rect, bg);
        var textSize = MeasureText(text);
        DrawText(text, new Vector2(x + (w - textSize.X) / 2, y + (h - textSize.Y) / 2), TextBright);
        return clicked;
    }

    // === Scrollable List ===

    // Interactive-scrollbar drag state: which bar (by id) owns the current
    // left-button drag, and the Y offset within the thumb where it was grabbed.
    private string? _vscrollDragId;
    private float _vscrollDragGrabOffset;

    /// <summary>The canonical thin vertical scrollbar (indicator-only, 5px wide
    /// with its left edge at x): a faint track spanning the viewport with a
    /// proportional thumb. No-op when the content fits. Pure draw — pair it
    /// with whatever wheel handler owns the scroll value. Geometry/palette come
    /// from <see cref="Necroking.UI.VScrollbar"/> (shared with the scenario menu).</summary>
    public void DrawVScrollbar(int x, int y, int viewH, float contentH, float scroll)
    {
        if (Necroking.UI.VScrollbar.Fits(viewH, contentH)) return;
        DrawRect(Necroking.UI.VScrollbar.TrackRect(x, y, viewH), Necroking.UI.VScrollbar.TrackColor);
        DrawRect(Necroking.UI.VScrollbar.ThumbRect(x, y, viewH, contentH, scroll), Necroking.UI.VScrollbar.ThumbColor);
    }

    /// <summary>Hit zone of an interactive scrollbar at (x, y, viewH): the 5px
    /// bar plus padding so the thumb is easy to grab. Callers whose clickable
    /// rows extend under the bar column must exclude this rect from their own
    /// row hit tests (DrawScrollableList does this internally).</summary>
    public static Rectangle VScrollbarHitRect(int x, int y, int viewH) => Necroking.UI.VScrollbar.HitRect(x, y, viewH);

    /// <summary>Interactive variant of <see cref="DrawVScrollbar(int,int,int,float,float)"/>:
    /// same canonical look, plus thumb-drag and track-click-jump (thumb centres
    /// on the cursor) with a generous hit zone (<see cref="VScrollbarHitRect"/>).
    /// Pass a stable unique id per bar. Returns the updated scroll value clamped
    /// to [0, contentH - viewH]; returns 0 (drawing nothing) when the content
    /// fits. Inert while an overlay owns input on the given layer (e.g. an open
    /// combo dropdown overlapping the bar column).</summary>
    public float DrawVScrollbar(string id, int x, int y, int viewH, float contentH, float scroll, int layer = 0)
    {
        // A mouse-up anywhere ends an in-progress drag (covers releasing off
        // the bar, or the owning panel disappearing mid-drag).
        if (_vscrollDragId != null && _mouse.LeftButton != ButtonState.Pressed) _vscrollDragId = null;
        if (Necroking.UI.VScrollbar.Fits(viewH, contentH)) return 0f;

        scroll = Math.Clamp(scroll, 0f, contentH - viewH);

        var thumbRect = Necroking.UI.VScrollbar.ThumbRect(x, y, viewH, contentH, scroll);
        var trackHit = Necroking.UI.VScrollbar.HitRect(x, y, viewH);
        bool overTrack = !IsInputBlocked(EffectiveLayer(layer)) && trackHit.Contains(_mouse.X, _mouse.Y);

        // Grab the thumb, or click the track to jump.
        if (overTrack && _mouse.LeftButton == ButtonState.Pressed && _prevMouse.LeftButton == ButtonState.Released)
        {
            _vscrollDragId = id;
            _vscrollDragGrabOffset = thumbRect.Contains(_mouse.X, _mouse.Y) ? _mouse.Y - thumbRect.Y : thumbRect.Height / 2f;
        }

        // Apply drag: map the thumb-top position back to a scroll offset.
        if (_vscrollDragId == id)
        {
            scroll = Necroking.UI.VScrollbar.ScrollFromDrag(_mouse.Y, _vscrollDragGrabOffset, y, viewH, contentH);
            thumbRect = Necroking.UI.VScrollbar.ThumbRect(x, y, viewH, contentH, scroll);
        }
        if (_vscrollDragId == id || overTrack) SetMouseOverUI();

        bool hot = _vscrollDragId == id || (overTrack && thumbRect.Contains(_mouse.X, _mouse.Y));
        DrawRect(Necroking.UI.VScrollbar.TrackRect(x, y, viewH), Necroking.UI.VScrollbar.TrackColor);
        DrawRect(thumbRect, hot ? Necroking.UI.VScrollbar.ThumbHotColor : Necroking.UI.VScrollbar.ThumbColor);
        return scroll;
    }

    // === Scroll panel scope ===

    /// <summary>
    /// Scope for the standard scrollable detail/properties panel: wheel
    /// scrolling (clamped by last frame's content height), scissor clip,
    /// content-height measurement, and the canonical draggable scrollbar —
    /// with the scroll state owned by EditorBase (keyed by panelId, same store
    /// as DrawScrollableList). Usage:
    /// <code>
    ///   var sp = _ui.BeginScrollPanel("env_props", new Rectangle(x, y, w, h), topPad: 8);
    ///   int curY = sp.ContentY;   // first row Y — scroll already applied
    ///   ...draw rows, advancing curY...
    ///   sp.End(curY);             // measure + record + clamp + scrollbar + EndClip
    /// </code>
    /// Callers never touch the offset or the height arithmetic (hand-rolled
    /// copies of that math have double-counted the scroll offset before, which
    /// turns the draggable bar into a feedback loop). Reset to the top on
    /// selection change with <see cref="SetScrollOffset"/>(panelId, 0).
    /// </summary>
    /// <param name="topPad">Gap above the first row, inside the rect.</param>
    /// <param name="bottomPad">Extra scrollable space below the last row.</param>
    /// <param name="wheelEnabled">Pass false to suppress wheel scrolling this
    /// frame (e.g. while a popup that doesn't block input layers is open above
    /// this panel — the panel would otherwise consume the wheel first).</param>
    /// <param name="clip">Scissor-clip the rect until End. Pass false when an
    /// enclosing scope already clips this exact area (e.g. RegistryCrudPanel's
    /// detail callback).</param>
    public ScrollPanel BeginScrollPanel(string panelId, Rectangle rect, int topPad = 4,
        int bottomPad = 0, int layer = 0, bool wheelEnabled = true, bool clip = true)
    {
        if (wheelEnabled)
        {
            float scroll = GetScrollOffset(panelId);
            HandlePanelScroll(rect, ref scroll, panelId, rect.Height, 0.3f, layer);
            _scrollOffsets[panelId] = scroll;
        }
        if (clip) BeginClip(rect);
        return new ScrollPanel(this, panelId, rect, topPad, bottomPad, layer, clip);
    }

    /// <summary>Scope returned by <see cref="BeginScrollPanel"/>: draw content
    /// from <see cref="ContentY"/> downward, then call <see cref="End"/> with
    /// the final layout cursor.</summary>
    public readonly struct ScrollPanel
    {
        private readonly EditorBase _ui;
        private readonly string _id;
        private readonly Rectangle _rect;
        private readonly int _topPad, _bottomPad, _layer;
        private readonly bool _clip;

        /// <summary>Y of the first content row (top padding and scroll applied).</summary>
        public int ContentY { get; }

        internal ScrollPanel(EditorBase ui, string id, Rectangle rect,
            int topPad, int bottomPad, int layer, bool clip)
        {
            _ui = ui; _id = id; _rect = rect;
            _topPad = topPad; _bottomPad = bottomPad; _layer = layer; _clip = clip;
            ContentY = rect.Y + topPad - (int)ui.GetScrollOffset(id);
        }

        /// <summary>Close the scope: ends the clip, records the measured
        /// content height for next frame's wheel clamp, and draws the
        /// draggable scrollbar (which also clamps the offset, so a shrinking
        /// panel can't strand it past the end). <paramref name="contentBottomY"/>
        /// is the layout cursor after the last row — screen space, i.e. still
        /// offset by the scroll like <see cref="ContentY"/> was.</summary>
        public void End(int contentBottomY)
        {
            if (_clip) _ui.EndClip();
            // ContentY already includes -scroll, so this difference is the
            // pure content height — the ONE place this arithmetic lives.
            float contentH = (contentBottomY - ContentY) + _topPad + _bottomPad;
            _ui.SetPanelContentHeight(_id, contentH);
            _ui._scrollOffsets[_id] = _ui.DrawVScrollbar(_id, _rect.Right - 6, _rect.Y,
                _rect.Height, contentH, _ui.GetScrollOffset(_id), _layer);
        }
    }

    /// <summary>
    /// Optional callback for custom item rendering in DrawScrollableList.
    /// Called with (itemIndex, itemRect, isSelected) after the background is drawn.
    /// If provided, the default text rendering is skipped — the callback owns all drawing.
    /// </summary>
    public delegate void ListItemRenderer(int itemIndex, Rectangle itemRect, bool isSelected);

    // Row height used by DrawScrollableList (kept in one place so ScrollListToItem
    // computes the same layout).
    private const int ScrollableListItemH = 22;

    /// <summary>
    /// Scroll a <see cref="DrawScrollableList"/> (identified by panelId) so the
    /// given visible item index is centred in the view. itemCount is the number
    /// of visible (post-filter) rows, viewH the list height. Use to reveal an
    /// item selected programmatically (e.g. jumping to a def from another editor).
    /// </summary>
    public void ScrollListToItem(string panelId, int itemIndex, int itemCount, int viewH)
    {
        if (itemIndex < 0) return;
        float target = itemIndex * ScrollableListItemH - (viewH - ScrollableListItemH) / 2f;
        float maxScroll = Math.Max(0, itemCount * ScrollableListItemH - viewH);
        _scrollOffsets[panelId] = Math.Clamp(target, 0, maxScroll);
    }

    // Keyboard list navigation. _focusedListId is the list that last received a
    // click (focus-follows-click); only it responds to arrow/WASD nav. The host
    // sets AllowWasdListNav=false while the bare map editor owns WASD for camera
    // panning — arrows still navigate lists there.
    private string? _focusedListId;
    public bool AllowWasdListNav = true;

    /// <summary>
    /// Draws a scrollable list of items. Returns the index of the clicked item, or -1.
    /// Also handles keyboard navigation (arrows / WASD) when the list is focused.
    /// </summary>
    /// <param name="customRenderer">Optional callback for custom per-item rendering (e.g. category dots, icons).</param>
    public int DrawScrollableList(string panelId, IReadOnlyList<string> items, int selectedIdx,
        int x, int y, int w, int h, string? searchFilter = null,
        ListItemRenderer? customRenderer = null)
    {
        bool inputBlocked = IsInputBlocked(EffectiveLayer(0));

        // Clip region
        var clipRect = new Rectangle(x, y, w, h);
        DrawRect(clipRect, new Color(20, 20, 35, 200));

        // Focus-follows-click: clicking anywhere in this list makes it the keyboard
        // nav target (until another list or a text field takes focus).
        if (!inputBlocked && HitTest(clipRect)
            && _mouse.LeftButton == ButtonState.Pressed && _prevMouse.LeftButton == ButtonState.Released)
            _focusedListId = panelId;

        if (!_scrollOffsets.ContainsKey(panelId))
            _scrollOffsets[panelId] = 0;

        float scroll = _scrollOffsets[panelId];
        int itemH = ScrollableListItemH;
        int clicked = -1;

        // Visible (post-filter) row count — drives the wheel clamp and the bar.
        int totalItems = 0;
        for (int i = 0; i < items.Count; i++)
        {
            if (searchFilter == null || items[i].Contains(searchFilter, StringComparison.OrdinalIgnoreCase))
                totalItems++;
        }
        float contentH = totalItems * (float)itemH;
        float maxScroll = Math.Max(0, contentH - h);
        // Rows under the scrollbar column must not react to clicks meant for the bar.
        var sbHit = contentH > h ? VScrollbarHitRect(x + w - 6, y, h) : Rectangle.Empty;

        // Handle scroll wheel when hovering
        if (!inputBlocked && !_scrollConsumed && HitTest(clipRect))
        {
            SetMouseOverUI();
            int scrollDelta = _mouse.ScrollWheelValue - _prevMouse.ScrollWheelValue;
            if (scrollDelta != 0)
            {
                scroll = Math.Clamp(scroll - scrollDelta * 0.15f, 0, maxScroll);
                _scrollOffsets[panelId] = scroll;
                ConsumeScroll();
            }
        }

        // Keyboard navigation when this list has focus (focus-follows-click) and
        // input isn't blocked / being typed into a text field. Up/Down always
        // navigate; W/S navigate too unless the host gave WASD to the map camera
        // (AllowWasdListNav=false). The moved selection is returned exactly like a
        // click, and we scroll to keep it visible.
        if (_focusedListId == panelId && !inputBlocked && !IsTextInputActive)
        {
            bool navUp = (_kb.IsKeyDown(Keys.Up) && _prevKb.IsKeyUp(Keys.Up))
                || (AllowWasdListNav && _kb.IsKeyDown(Keys.W) && _prevKb.IsKeyUp(Keys.W));
            bool navDown = (_kb.IsKeyDown(Keys.Down) && _prevKb.IsKeyUp(Keys.Down))
                || (AllowWasdListNav && _kb.IsKeyDown(Keys.S) && _prevKb.IsKeyUp(Keys.S));
            if (navUp ^ navDown)
            {
                int target = NextVisibleListIndex(items, searchFilter, selectedIdx, navDown ? 1 : -1);
                if (target >= 0 && target != selectedIdx)
                {
                    clicked = target;
                    // Scroll to keep the new selection visible (by its visible row).
                    int visPos = 0;
                    for (int i = 0; i < target; i++)
                        if (searchFilter == null || items[i].Contains(searchFilter, StringComparison.OrdinalIgnoreCase))
                            visPos++;
                    int itemTop = visPos * itemH;
                    if (itemTop < scroll) scroll = itemTop;
                    else if (itemTop + itemH > scroll + h) scroll = itemTop + itemH - h;
                    scroll = Math.Max(0, scroll);
                    _scrollOffsets[panelId] = scroll;
                }
            }
        }

        // Scissor-clip everything drawn by the item loop so partially-
        // scrolled top/bottom rows can't spill out — covers the row
        // background, the default text label, AND anything a custom
        // renderer draws at the unclipped itemRect.
        BeginClip(clipRect);

        // Draw items — use integer Y so hitbox and draw positions are identical
        int scrollInt = (int)scroll;
        int visIdx = 0; // visible item counter for alternating row colors
        for (int i = 0; i < items.Count; i++)
        {
            if (searchFilter != null && !items[i].Contains(searchFilter, StringComparison.OrdinalIgnoreCase))
                continue;

            int iy = y + visIdx * itemH - scrollInt;
            visIdx++;
            if (iy + itemH < y) continue;
            if (iy >= y + h) break;

            var itemRect = new Rectangle(x, iy, w, itemH);
            bool hovered = !inputBlocked && itemRect.Contains(_mouse.X, _mouse.Y) && clipRect.Contains(_mouse.X, _mouse.Y)
                && !sbHit.Contains(_mouse.X, _mouse.Y);
            if (hovered) SetMouseOverUI();

            Color bg;
            if (i == selectedIdx) bg = ItemSelected;
            else if (hovered) bg = ItemHover;
            else bg = (i % 2 == 0) ? new Color(30, 30, 48, 200) : new Color(25, 25, 40, 200);

            DrawRect(itemRect, bg);
            if (customRenderer != null)
                customRenderer(i, itemRect, i == selectedIdx);
            else
                DrawText(items[i], new Vector2(itemRect.X + 4, itemRect.Y + 2), i == selectedIdx ? TextBright : TextColor);

            if (hovered && _mouse.LeftButton == ButtonState.Pressed && _prevMouse.LeftButton == ButtonState.Released)
                clicked = i;
        }

        EndClip();

        // Scrollbar (interactive: draggable thumb + track-click jump)
        scroll = DrawVScrollbar(panelId, x + w - 6, y, h, contentH, scroll);
        _scrollOffsets[panelId] = scroll;

        // Selecting a different object abandons any in-progress text-field edit.
        // The edit buffer (_inputBuffer) is keyed by a static field id ("wd_id",
        // "unit_id", …) reused for every object, NOT by the object's identity — so
        // without this, a field left active when selection changes would, on the
        // same click, run its deactivate-on-outside-click path and write the old
        // object's buffered text into the NEWLY selected object, clobbering it.
        // Clearing here (the shared choke point for object-list selection) fixes it
        // for every editor at once. The previously selected object already received
        // the edit — the caller commits the buffer every frame while it's active.
        if (clicked >= 0 && clicked != selectedIdx)
            ClearActiveField();

        return clicked;
    }

    /// <summary>Index of the next visible (filter-passing) item from <paramref name="from"/>
    /// in direction <paramref name="dir"/> (+1 down / -1 up), or -1 if none. When
    /// nothing is selected (from &lt; 0), returns the first/last visible item.</summary>
    private static int NextVisibleListIndex(IReadOnlyList<string> items, string? filter, int from, int dir)
    {
        bool Vis(int i) => filter == null || items[i].Contains(filter, StringComparison.OrdinalIgnoreCase);
        if (from < 0)
        {
            if (dir > 0) { for (int i = 0; i < items.Count; i++) if (Vis(i)) return i; }
            else { for (int i = items.Count - 1; i >= 0; i--) if (Vis(i)) return i; }
            return -1;
        }
        if (dir > 0) { for (int i = from + 1; i < items.Count; i++) if (Vis(i)) return i; }
        else { for (int i = from - 1; i >= 0; i--) if (Vis(i)) return i; }
        return -1;
    }

    // === Text Input Field ===

    /// <summary>Shared renderer for single-line field interiors: selection
    /// highlight, text, and blinking cursor at the tracked cursor position.
    /// Used by text/int/float/search fields and the slider value box so
    /// select-all and cursor visuals are identical everywhere.</summary>
    private void DrawFieldContent(Rectangle inputRect, string displayText, bool isActive)
    {
        int tx = inputRect.X + 3, ty = inputRect.Y + 2;
        if (isActive && HasSelection)
        {
            var (selStart, selEnd) = GetSelectionRange();
            int cs = Math.Clamp(selStart, 0, displayText.Length);
            int ce = Math.Clamp(selEnd, 0, displayText.Length);
            float x1 = MeasureText(displayText[..cs]).X;
            float x2 = MeasureText(displayText[..ce]).X;
            DrawRect(new Rectangle(tx + (int)x1, ty, (int)(x2 - x1), inputRect.Height - 4),
                new Color(80, 120, 200, 140));
        }
        DrawText(displayText, new Vector2(tx, ty), isActive ? TextBright : TextColor);
        if (isActive && !HasSelection && ((int)(_cursorBlink * 2) % 2 == 0))
        {
            int cp = Math.Clamp(_cursorPos, 0, displayText.Length);
            float cursorX = tx + MeasureText(displayText[..cp]).X;
            DrawRect(new Rectangle((int)cursorX, ty, 1, inputRect.Height - 4), TextBright);
        }
    }

    /// <summary>Titled section header that advances <paramref name="y"/> — THE one
    /// section-header look shared by every editor window (consolidation B7): a filled
    /// PanelHeader bar with bright text. The former Rule/Bar/Label style enum (and
    /// MapEditorWindow's private small-font bar variant) collapsed into this.</summary>
    public void DrawSectionHeader(string text, int x, ref int y, int w)
    {
        DrawRect(new Rectangle(x, y, w, 22), PanelHeader);
        DrawText(text, new Vector2(x + 6, y + 3), TextBright);
        y += 24;
    }

    /// <summary>Common focus bookkeeping when a single-line field is clicked:
    /// select-all buffer init + cursor reset + text-field modal layer rect.</summary>
    private void FocusTextField(string fieldId, string value, Rectangle inputRect)
    {
        _activeFieldId = fieldId;
        _inputBuffer = value;
        _cursorPos = value.Length;
        _selectionStart = 0;
        _selectAll = true;
        _cursorBlink = 0;
        _activeFieldInputX = inputRect.X + 3;
        _activeFieldInputW = inputRect.Width - 6;
        _textFieldLayer.Panel = inputRect;
    }

    /// <summary>
    /// Shared click/drag/focus/deactivate + render core for every single-line field
    /// (text, int, float, search, slider value box). Owns: activate-on-click,
    /// click-to-position-cursor + drag-selection, outside-click-deactivate, and the
    /// box render (with optional placeholder). Wrappers layer parse-type + steppers on
    /// top — that's the only per-field variance.
    ///
    /// Returns: <c>text</c> = the edit buffer while the field is active or on the commit
    /// frame, otherwise <paramref name="display"/>; <c>active</c> = field is focused;
    /// <c>committed</c> = the field just deactivated this frame via an outside click, so
    /// a numeric wrapper should parse <c>text</c> and return it (and skip its steppers
    /// that frame, matching the pre-refactor early-return behavior).
    /// </summary>
    private (string text, bool active, bool committed) FieldCore(
        string fieldId, string display, Rectangle inputRect, string? placeholder = null)
    {
        bool isActive = _activeFieldId == fieldId;
        bool hovered = IsHovered(inputRect);
        bool committed = false;
        int inputX = inputRect.X;

        // Click to activate (or reposition cursor if already active)
        if (hovered && _mouse.LeftButton == ButtonState.Pressed && _prevMouse.LeftButton == ButtonState.Released)
        {
            if (!isActive)
            {
                // First click: activate with select-all
                FocusTextField(fieldId, display, inputRect);
                isActive = true;
            }
            else
            {
                // Click in already-active field: position cursor, start drag selection
                int clickPos = CharIndexFromPixelX(_inputBuffer, _mouse.X - inputX - 3);
                _cursorPos = clickPos;
                _selectionStart = clickPos;
                _selectAll = false;
                _draggingSelection = true;
                _cursorBlink = 0;
            }
        }
        // Drag to extend selection
        else if (isActive && _draggingSelection && _mouse.LeftButton == ButtonState.Pressed)
        {
            int dragPos = CharIndexFromPixelX(_inputBuffer, _mouse.X - inputX - 3);
            _cursorPos = dragPos;
            _cursorBlink = 0;
        }
        // Release drag
        else if (_draggingSelection && _mouse.LeftButton == ButtonState.Released)
        {
            _draggingSelection = false;
            // If start == cursor, clear selection
            if (_selectionStart == _cursorPos)
                _selectionStart = -1;
        }
        // Click elsewhere to deactivate
        else if (isActive && _mouse.LeftButton == ButtonState.Pressed && _prevMouse.LeftButton == ButtonState.Released && !hovered)
        {
            _activeFieldId = null;
            _selectAll = false;
            _selectionStart = -1;
            _draggingSelection = false;
            isActive = false;
            committed = true;
        }

        DrawRect(inputRect, InputBg);
        DrawBorder(inputRect, isActive ? InputActive : InputBorder);

        if (placeholder != null && string.IsNullOrEmpty(display) && !isActive)
            DrawText(placeholder, new Vector2(inputRect.X + 3, inputRect.Y + 3), new Color(80, 80, 100));
        else
            DrawFieldContent(inputRect, isActive ? _inputBuffer : display, isActive);

        string text = (isActive || committed) ? _inputBuffer : display;
        return (text, isActive, committed);
    }

    public string DrawTextField(string fieldId, string label, string value, int x, int y, int w,
        int labelW = 120)
    {
        DrawText(label, new Vector2(x, y + 2), TextDim);

        int inputX = x + labelW;
        int inputW = w - labelW;
        var inputRect = new Rectangle(inputX, y, inputW, 20);

        var (text, _, _) = FieldCore(fieldId, value, inputRect);
        return text;
    }

    /// <summary>
    /// Draw an int input field with +/- buttons
    /// </summary>
    public int DrawIntField(string fieldId, string label, int value, int x, int y, int w)
    {
        int labelW = 120;
        DrawText(label, new Vector2(x, y + 2), TextDim);

        int inputX = x + labelW;
        int inputW = w - labelW - 46; // room for +/- buttons
        int inputH = 20;

        var inputRect = new Rectangle(inputX, y, inputW, inputH);
        var (text, active, committed) = FieldCore(fieldId, value.ToString(), inputRect);
        if (committed)
            return int.TryParse(text, out int parsed) ? parsed : value;

        // +/- buttons
        int btnX = inputX + inputW + 2;
        if (DrawButton("-", btnX, y, 20, inputH))
        {
            if (active && int.TryParse(_inputBuffer, out int v)) { _inputBuffer = (v - 1).ToString(); return v - 1; }
            return value - 1;
        }
        if (DrawButton("+", btnX + 22, y, 20, inputH))
        {
            if (active && int.TryParse(_inputBuffer, out int v)) { _inputBuffer = (v + 1).ToString(); return v + 1; }
            return value + 1;
        }

        if (active && int.TryParse(_inputBuffer, out int current))
            return current;
        return value;
    }

    /// <summary>
    /// Draw a float input field with drag behavior
    /// </summary>
    public float DrawFloatField(string fieldId, string label, float value, int x, int y, int w, float step = 0.1f, int labelW = 120)
    {
        DrawText(label, new Vector2(x, y + 2), TextDim);

        int inputX = x + labelW;
        int inputW = w - labelW - 46;
        int inputH = 20;

        var inputRect = new Rectangle(inputX, y, inputW, inputH);
        var (text, active, committed) = FieldCore(fieldId, value.ToString("F2"), inputRect);
        if (committed)
            return float.TryParse(text, out float parsed) ? parsed : value;

        // +/- buttons
        int btnX = inputX + inputW + 2;
        if (DrawButton("-", btnX, y, 20, inputH))
        {
            float newVal = MathF.Round((value - step) / step) * step;
            if (active) _inputBuffer = newVal.ToString("F2");
            return newVal;
        }
        if (DrawButton("+", btnX + 22, y, 20, inputH))
        {
            float newVal = MathF.Round((value + step) / step) * step;
            if (active) _inputBuffer = newVal.ToString("F2");
            return newVal;
        }

        if (active && float.TryParse(_inputBuffer, out float cur))
            return cur;
        return value;
    }

    /// <summary>
    /// Draw a checkbox/boolean toggle
    /// </summary>
    /// <summary>
    /// Draw a checkbox. The clickable area covers both the box and its label, so
    /// clicking the text toggles too. In tight multi-column layouts pass
    /// <paramref name="hitWidth"/> (the column width) to clamp the clickable area
    /// and avoid overlapping the next column's checkbox.
    /// </summary>
    public bool DrawCheckbox(string label, bool value, int x, int y, int hitWidth = 0)
    {
        int boxSize = 16;
        var boxRect = new Rectangle(x, y + 2, boxSize, boxSize);

        int labelW = (int)MathF.Ceiling(MeasureText(label).X);
        int fullW = boxSize + 6 + labelW;
        int areaW = hitWidth > 0 ? Math.Min(hitWidth, fullW) : fullW;
        var hitRect = new Rectangle(x, y + 2, areaW, boxSize);
        bool hovered = IsHovered(hitRect);
        bool clicked = hovered && _mouse.LeftButton == ButtonState.Pressed && _prevMouse.LeftButton == ButtonState.Released;

        DrawUtils.DrawCheckbox(Scope, _pixel, boxRect, value, hovered,
            InputBg, InputBorder, InputActive, AccentColor);
        DrawText(label, new Vector2(x + boxSize + 6, y + 2), TextColor);

        return clicked ? !value : value;
    }

    /// <summary>
    /// Draw the inline harmonizer controls (target swatch, HSV/HCL mode toggle and
    /// the 3 strength sliders) bound directly to a persistent <see cref="HarmonizeSettings"/>.
    /// Returns true if any value changed this frame (caller should re-bake the texture).
    /// Canonical implementation shared by the UI editor and the env-object editor.
    /// </summary>
    public bool DrawHarmonizeSliders(string id, HarmonizeSettings settings, int x, ref int curY, int w)
    {
        bool changed = false;
        int labelW = 80;
        int fieldX = x + labelW;

        // Target color (written live each frame so the preview updates while picking).
        DrawText("Target:", new Vector2(x, curY + 2), TextDim);
        byte ta = settings.TargetColor.Length > 3 ? settings.TargetColor[3] : (byte)255;
        var targHdr = new HdrColor(settings.TargetColor[0], settings.TargetColor[1], settings.TargetColor[2], ta, 1f);
        DrawColorSwatch(id + "_tgt", fieldX, curY, 40, 18, ref targHdr, hideIntensity: false);
        if (targHdr.R != settings.TargetColor[0] || targHdr.G != settings.TargetColor[1]
            || targHdr.B != settings.TargetColor[2] || targHdr.A != ta)
        {
            settings.TargetColor = new[] { targHdr.R, targHdr.G, targHdr.B, targHdr.A };
            changed = true;
        }
        curY += 22;

        // HSV / HCL mode toggle
        DrawText("Mode:", new Vector2(x, curY + 2), TextDim);
        if (DrawButton(settings.UseHcl ? "HCL" : "HSV", fieldX, curY, 48, 18))
        {
            settings.UseHcl = !settings.UseHcl;
            changed = true;
        }
        curY += 22;

        // Per-channel strength sliders
        string[] labels = settings.UseHcl ? new[] { "Hue:", "Chroma:", "Lum:" } : new[] { "Hue:", "Sat:", "Value:" };
        float[] vals = { settings.HueStrength, settings.SatStrength, settings.ValStrength };
        for (int i = 0; i < 3; i++)
        {
            float newVal = DrawSliderFloat($"{id}_s{i}", labels[i], vals[i], 0f, 1f, x, curY, w);
            if (MathF.Abs(newVal - vals[i]) > 0.001f) { vals[i] = newVal; changed = true; }
            curY += 22;
        }
        settings.HueStrength = vals[0];
        settings.SatStrength = vals[1];
        settings.ValStrength = vals[2];

        // Vertical gradient (top -> bottom blend toward GradColor; alpha = max blend)
        DrawText("Grad:", new Vector2(x, curY + 2), TextDim);
        var gradBytes = settings.GradColor ?? new byte[] { 0, 0, 0, 112 };
        var gradHdr = new HdrColor(gradBytes[0], gradBytes[1], gradBytes[2], gradBytes.Length > 3 ? gradBytes[3] : (byte)255, 1f);
        DrawColorSwatch(id + "_grad", fieldX, curY, 40, 18, ref gradHdr, hideIntensity: true);
        if (gradHdr.R != gradBytes[0] || gradHdr.G != gradBytes[1] || gradHdr.B != gradBytes[2]
            || gradHdr.A != (gradBytes.Length > 3 ? gradBytes[3] : (byte)255))
        {
            settings.GradColor = new[] { gradHdr.R, gradHdr.G, gradHdr.B, gradHdr.A };
            changed = true;
        }
        curY += 22;
        float newGradStr = DrawSliderFloat(id + "_gradstr", "Grad Str:", settings.GradStrength, 0f, 1f, x, curY, w);
        if (MathF.Abs(newGradStr - settings.GradStrength) > 0.001f)
        {
            settings.GradStrength = newGradStr;
            settings.GradColor ??= new byte[] { 0, 0, 0, 112 };
            changed = true;
        }
        curY += 22;

        // Silhouette outline (baked around the alpha edge, thickness in texture px)
        DrawText("Outline:", new Vector2(x, curY + 2), TextDim);
        var outBytes = settings.OutlineColor ?? new byte[] { 0, 0, 0, 255 };
        var outHdr = new HdrColor(outBytes[0], outBytes[1], outBytes[2], outBytes.Length > 3 ? outBytes[3] : (byte)255, 1f);
        DrawColorSwatch(id + "_outl", fieldX, curY, 40, 18, ref outHdr, hideIntensity: true);
        if (outHdr.R != outBytes[0] || outHdr.G != outBytes[1] || outHdr.B != outBytes[2]
            || outHdr.A != (outBytes.Length > 3 ? outBytes[3] : (byte)255))
        {
            settings.OutlineColor = new[] { outHdr.R, outHdr.G, outHdr.B, outHdr.A };
            changed = true;
        }
        curY += 22;
        float newOutTh = DrawSliderFloat(id + "_outth", "Thickness:", settings.OutlineThickness, 0f, 6f, x, curY, w);
        if (MathF.Abs(newOutTh - settings.OutlineThickness) > 0.001f)
        {
            settings.OutlineThickness = newOutTh;
            settings.OutlineColor ??= new byte[] { 0, 0, 0, 255 };
            changed = true;
        }
        curY += 22;
        float newOutOp = DrawSliderFloat(id + "_outop", "Opacity:", settings.OutlineOpacity, 0f, 1f, x, curY, w);
        if (MathF.Abs(newOutOp - settings.OutlineOpacity) > 0.001f) { settings.OutlineOpacity = newOutOp; changed = true; }
        curY += 22;

        return changed;
    }

    /// <summary>
    /// Draw a dropdown/combo for string enum values.
    /// Supports scrolling, search filtering (auto for 15+ items), keyboard navigation,
    /// max-height capping (40% screen), and an optional "(none)" entry.
    /// Interaction is EAGER (Lib/EagerDropdown): press on the button opens the
    /// list and releasing over an item selects it, so press→drag→release picks
    /// in one gesture; plain click-click still works.
    /// Pass <paramref name="optionTooltips"/> (parallel to <paramref name="options"/>,
    /// null/empty entries = no tip, '\n' = line break) to show a hover tooltip
    /// per option in the expanded list. For large data-driven lists prefer
    /// <paramref name="optionTooltipFor"/>: a lazy provider called only with the
    /// hovered option's index into <paramref name="options"/> (so nothing is
    /// built for the hundreds of rows nobody hovers).
    /// </summary>
    private const int ComboItemH = 20;
    private const int ComboFilterH = 22; // height of the filter text box row

    // Highlight color for keyboard navigation (distinct from hover and selected)
    private static readonly Color ComboHighlight = new(70, 90, 140, 240);

    public string DrawCombo(string fieldId, string label, string value, string[] options,
        int x, int y, int w, bool allowNone = false, string?[]? optionTooltips = null,
        Func<int, string?>? optionTooltipFor = null)
    {
        int labelW = 120;
        DrawText(label, new Vector2(x, y + 2), TextDim);

        int inputX = x + labelW;
        int inputW = w - labelW;
        int inputH = 20;
        var inputRect = new Rectangle(inputX, y, inputW, inputH);

        // Build effective option list (prepend "(none)" when allowed)
        string[] effectiveOptions;
        string?[]? effectiveTooltips = optionTooltips;
        Func<int, string?>? effectiveTooltipFor = optionTooltipFor;
        if (allowNone)
        {
            effectiveOptions = new string[options.Length + 1];
            effectiveOptions[0] = "(none)";
            Array.Copy(options, 0, effectiveOptions, 1, options.Length);
            if (optionTooltips != null)
            {
                // Keep tooltip indices aligned with effectiveOptions ("(none)" gets no tip)
                effectiveTooltips = new string?[optionTooltips.Length + 1];
                Array.Copy(optionTooltips, 0, effectiveTooltips, 1, optionTooltips.Length);
            }
            if (optionTooltipFor != null)
                // Same alignment for the lazy provider: shift past "(none)" so the
                // delegate always receives an index into the caller's options array.
                effectiveTooltipFor = ei => ei == 0 ? null : optionTooltipFor(ei - 1);
        }
        else
        {
            effectiveOptions = options;
        }

        // Map display value: if allowNone and value is empty, show "(none)"
        string displayValue = value;
        if (allowNone && string.IsNullOrEmpty(value))
            displayValue = "(none)";

        // If a dropdown overlay consumed the click this frame, block combo button interaction
        string comboId = fieldId + "_combo";
        bool isOpen = _activeFieldId == comboId;
        // The combo button itself is always clickable when it's the open dropdown (to allow closing)
        bool hovered = !_dropdownOverlayConsumedClick && (isOpen || !IsInputBlocked(EffectiveLayer(0))) && HitTest(inputRect);
        if (hovered) SetMouseOverUI();
        if (isOpen) _openComboDrawnThisFrame = true;

        // Draw the current value
        DrawRect(inputRect, hovered || isOpen ? ItemHover : InputBg);
        DrawBorder(inputRect, isOpen ? InputActive : InputBorder);
        DrawText(displayValue, new Vector2(inputX + 3, y + 2), TextColor);
        DrawText("v", new Vector2(inputX + inputW - 14, y + 2), TextDim);

        if (hovered && _mouse.LeftButton == ButtonState.Pressed && _prevMouse.LeftButton == ButtonState.Released)
        {
            if (isOpen)
            {
                // Eager: a second press on the open button closes on RELEASE
                // (handled in the open branch below) — unless the release lands
                // on an item, in which case selection wins (drag from button
                // onto the list). No early return: the open branch must still
                // run this frame so the overlay renders (the press is on the
                // button, so the item/outside handlers there won't also fire).
                _comboEager.OnPress(0, -1);
            }
            else
            {
                _comboEager.OnPress(0, -1); // open + arm: release over an item selects
                _activeFieldId = comboId;
                // Mark the combo as drawn this frame right as it opens. Otherwise
                // EndDrawFrame sees IsDropdownOpen==true but _openComboDrawnThisFrame
                // still false (it's only set at the top of DrawCombo, and only when
                // isOpen was ALREADY true) and immediately closes the just-opened
                // dropdown — the list would never appear, only the hover highlight.
                _openComboDrawnThisFrame = true;
                _comboFilterText[comboId] = "";
                _comboHighlightIdx = -1;

                // Compute max visible based on 40% screen height
                int maxDropH = (int)(_screenH * 0.4f);
                int maxVisible = Math.Max(1, maxDropH / ComboItemH);
                int visCount = Math.Min(effectiveOptions.Length, maxVisible);

                // Reset scroll to center the selected item
                int selectedIdx = Array.IndexOf(effectiveOptions, displayValue);
                if (selectedIdx >= 0 && effectiveOptions.Length > visCount)
                    _comboScrollOffsets[comboId] = Math.Max(0, Math.Min(selectedIdx - visCount / 2, effectiveOptions.Length - visCount));
                else
                    _comboScrollOffsets[comboId] = 0;
                return value;
            }
        }

        // When open: handle input inline (so DrawCombo can return the selection),
        // but defer visual rendering to DrawDropdownOverlays()
        if (isOpen)
        {
            // Set input layer to prevent click-through to widgets behind the dropdown
            if (_inputLayer < 2) _inputLayer = 2;

            // Filter support: auto-enable when 15+ items
            bool showFilter = effectiveOptions.Length >= 15;
            if (!_comboFilterText.ContainsKey(comboId))
                _comboFilterText[comboId] = "";
            string filterText = _comboFilterText[comboId];

            // Build filtered list
            string[] filteredOptions;
            int[] filteredIndices;
            if (showFilter && !string.IsNullOrEmpty(filterText))
            {
                var fOpts = new List<string>();
                var fIdxs = new List<int>();
                for (int oi = 0; oi < effectiveOptions.Length; oi++)
                {
                    if (effectiveOptions[oi].Contains(filterText, StringComparison.OrdinalIgnoreCase))
                    {
                        fOpts.Add(effectiveOptions[oi]);
                        fIdxs.Add(oi);
                    }
                }
                filteredOptions = fOpts.ToArray();
                filteredIndices = fIdxs.ToArray();
            }
            else
            {
                filteredOptions = effectiveOptions;
                filteredIndices = new int[effectiveOptions.Length];
                for (int oi = 0; oi < effectiveOptions.Length; oi++) filteredIndices[oi] = oi;
            }

            // Compute max visible based on 40% screen height
            int maxDropH = (int)(_screenH * 0.4f);
            int filterRowH = showFilter ? ComboFilterH : 0;
            int maxVisibleItems = Math.Max(1, (maxDropH - filterRowH) / ComboItemH);
            int visibleCount = Math.Min(filteredOptions.Length, maxVisibleItems);
            bool needsScroll = filteredOptions.Length > maxVisibleItems;

            // Get or init scroll offset
            if (!_comboScrollOffsets.ContainsKey(comboId))
                _comboScrollOffsets[comboId] = 0;
            int scrollOffset = _comboScrollOffsets[comboId];
            int maxScroll = Math.Max(0, filteredOptions.Length - visibleCount);
            scrollOffset = Math.Clamp(scrollOffset, 0, maxScroll);

            int dropH = visibleCount * ComboItemH + filterRowH;

            // INF11: Flip dropdown upward if it would extend below the screen
            int dropY = y + inputH;
            bool flippedUp = false;
            if (dropY + dropH > _screenH)
            {
                dropY = y - dropH;
                flippedUp = true;
                if (dropY < 0) dropY = 0;
            }
            var dropRect = new Rectangle(inputX, dropY, inputW, dropH);

            // Keyboard navigation (when the dropdown is open)
            {
                bool arrowDown = _kb.IsKeyDown(Keys.Down) && _prevKb.IsKeyUp(Keys.Down);
                bool arrowUp = _kb.IsKeyDown(Keys.Up) && _prevKb.IsKeyUp(Keys.Up);
                bool enterKey = _kb.IsKeyDown(Keys.Enter) && _prevKb.IsKeyUp(Keys.Enter);
                bool escapeKey = _kb.IsKeyDown(Keys.Escape) && _prevKb.IsKeyUp(Keys.Escape);

                if (escapeKey)
                {
                    _activeFieldId = null;
                    _comboFilterText.Remove(comboId);
                    _comboHighlightIdx = -1;
                    _dropdownOverlayConsumedClick = true;
                    return value;
                }

                if (arrowDown)
                {
                    _comboHighlightIdx++;
                    if (_comboHighlightIdx >= filteredOptions.Length) _comboHighlightIdx = 0;
                    // Scroll to keep highlight visible
                    if (_comboHighlightIdx >= scrollOffset + visibleCount)
                        scrollOffset = _comboHighlightIdx - visibleCount + 1;
                    else if (_comboHighlightIdx < scrollOffset)
                        scrollOffset = _comboHighlightIdx;
                    scrollOffset = Math.Clamp(scrollOffset, 0, maxScroll);
                    _comboScrollOffsets[comboId] = scrollOffset;
                }
                if (arrowUp)
                {
                    _comboHighlightIdx--;
                    if (_comboHighlightIdx < 0) _comboHighlightIdx = filteredOptions.Length - 1;
                    if (_comboHighlightIdx < scrollOffset)
                        scrollOffset = _comboHighlightIdx;
                    else if (_comboHighlightIdx >= scrollOffset + visibleCount)
                        scrollOffset = _comboHighlightIdx - visibleCount + 1;
                    scrollOffset = Math.Clamp(scrollOffset, 0, maxScroll);
                    _comboScrollOffsets[comboId] = scrollOffset;
                }
                if (enterKey && _comboHighlightIdx >= 0 && _comboHighlightIdx < filteredOptions.Length)
                {
                    string picked = filteredOptions[_comboHighlightIdx];
                    _activeFieldId = null;
                    _comboFilterText.Remove(comboId);
                    _comboHighlightIdx = -1;
                    _dropdownOverlayConsumedClick = true;
                    if (allowNone && picked == "(none)") return "";
                    return picked;
                }

                // Filter text input via keyboard (only when filter is shown)
                if (showFilter)
                {
                    bool shift = _kb.IsKeyDown(Keys.LeftShift) || _kb.IsKeyDown(Keys.RightShift);
                    foreach (var key in _kb.GetPressedKeys())
                    {
                        if (_prevKb.IsKeyUp(key))
                        {
                            if (key == Keys.Back && filterText.Length > 0)
                            {
                                filterText = filterText[..^1];
                                _comboFilterText[comboId] = filterText;
                                _comboHighlightIdx = -1;
                                // Re-filter will happen next frame; reset scroll
                                _comboScrollOffsets[comboId] = 0;
                            }
                            else
                            {
                                char? c = KeyToChar(key, shift);
                                if (c.HasValue)
                                {
                                    filterText += c.Value;
                                    _comboFilterText[comboId] = filterText;
                                    _comboHighlightIdx = -1;
                                    _comboScrollOffsets[comboId] = 0;
                                }
                            }
                        }
                    }
                }
            }

            // Compute layout once — single source of truth for click detection AND rendering
            int itemsY = dropY + filterRowH;
            var itemRects = new Rectangle[visibleCount];
            for (int vi = 0; vi < visibleCount; vi++)
                itemRects[vi] = new Rectangle(inputX, itemsY + vi * ComboItemH, inputW, ComboItemH);

            // Handle mouse wheel scrolling within the dropdown + consume scroll
            if (dropRect.Contains(_mouse.X, _mouse.Y))
            {
                ConsumeScroll();

                if (needsScroll)
                {
                    int scrollDelta = _mouse.ScrollWheelValue - _prevMouse.ScrollWheelValue;
                    if (scrollDelta != 0)
                    {
                        scrollOffset -= scrollDelta > 0 ? 1 : -1;
                        scrollOffset = Math.Clamp(scrollOffset, 0, maxScroll);
                        _comboScrollOffsets[comboId] = scrollOffset;
                    }
                }
            }

            // Eager gesture: a press on an option only ARMS the machine — the
            // actual selection fires on release over an item (which also covers
            // press-on-button → drag → release-on-item in one gesture). A press
            // inside the dropdown but on no item (filter row, scrollbar) feeds
            // nothing and keeps the list open.
            bool pressEdge = _mouse.LeftButton == ButtonState.Pressed
                && _prevMouse.LeftButton == ButtonState.Released;
            bool releaseEdge = _mouse.LeftButton == ButtonState.Released
                && _prevMouse.LeftButton == ButtonState.Pressed;

            // Hovered option (filtered index), shared by press-arm and release-select.
            int hoverFi = -1;
            for (int vi = 0; vi < visibleCount; vi++)
            {
                int fi = vi + scrollOffset;
                if (fi >= filteredOptions.Length) break;
                if (itemRects[vi].Contains(_mouse.X, _mouse.Y)) { hoverFi = fi; break; }
            }

            if (pressEdge && hoverFi >= 0)
            {
                _comboEager.OnPress(-1, hoverFi);
                _dropdownOverlayConsumedClick = true;
            }

            // Close dropdown if clicking outside both the combo button and dropdown
            if (pressEdge
                && !dropRect.Contains(_mouse.X, _mouse.Y) && !inputRect.Contains(_mouse.X, _mouse.Y))
            {
                _comboEager.OnPressOutside();
                _activeFieldId = null;
                _comboFilterText.Remove(comboId);
                _comboHighlightIdx = -1;
                _dropdownOverlayConsumedClick = true;
                _dropdownHoldingMousePress = true;
            }

            // Release: select the hovered item if the gesture armed it, or close
            // when a press on the open button flagged close-on-release. Gated on
            // PressStartPos so a gesture consumed by a UI mode change can't
            // ghost-select on its release.
            if (releaseEdge && _comboEager.IsOpen)
            {
                int sel = _comboEager.OnRelease(hoverFi, _pressStartPos.X >= 0);
                if (sel >= 0 && sel < filteredOptions.Length)
                {
                    string picked = filteredOptions[sel];
                    _activeFieldId = null;
                    _comboFilterText.Remove(comboId);
                    _comboHighlightIdx = -1;
                    _dropdownOverlayConsumedClick = true;
                    _dropdownHoldingMousePress = true;
                    if (allowNone && picked == "(none)") return "";
                    return picked;
                }
                if (!_comboEager.IsOpen) // close-on-release (second click on button)
                {
                    _activeFieldId = null;
                    _comboFilterText.Remove(comboId);
                    _comboHighlightIdx = -1;
                    _dropdownOverlayConsumedClick = true;
                    _dropdownHoldingMousePress = true;
                    return value;
                }
            }

            // Save layout for deferred rendering — renderer uses these exact rects
            _pendingDropdown = new PendingDropdown
            {
                FieldId = comboId,
                Options = effectiveOptions,
                FilteredOptions = filteredOptions,
                FilteredIndices = filteredIndices,
                CurrentValue = displayValue,
                InputRect = inputRect,
                ScrollOffset = scrollOffset,
                MaxScroll = maxScroll,
                NeedsScroll = needsScroll,
                VisibleCount = visibleCount,
                DropY = dropY,
                FlippedUp = flippedUp,
                ShowFilter = showFilter,
                FilterText = filterText,
                HighlightIdx = _comboHighlightIdx,
                Tooltips = effectiveTooltips,
                TooltipFor = effectiveTooltipFor,
                ItemRects = itemRects,
                DropRect = dropRect,
                ItemsY = itemsY,
            };
        }

        return value;
    }

    /// <summary>
    /// Draw all pending dropdown overlays on top of everything.
    /// Must be called at the very end of an editor's Draw method,
    /// after all panels, popups, etc., so the dropdown renders at the highest z-level.
    /// </summary>
    public void DrawDropdownOverlays()
    {
        if (_pendingDropdown == null) return;

        var dd = _pendingDropdown.Value;
        int inputX = dd.InputRect.X;
        int inputW = dd.InputRect.Width;
        int scrollOffset = dd.ScrollOffset;

        // Use pre-computed DropRect from layout (same rect used for click detection)
        var dropRect = dd.DropRect;

        // Publish the dropdown's screen rect so PopupManager's outside-click
        // light-dismiss uses the correct hit area this frame. Combined panel:
        // input row above + the dropdown list below = whole "active" area.
        SetDropdownRect(Microsoft.Xna.Framework.Rectangle.Union(dd.InputRect, dropRect));

        // INF11: Shadow behind the dropdown for visual separation
        var shadowRect = new Rectangle(dropRect.X + 3, dropRect.Y + 3, dropRect.Width, dropRect.Height);
        DrawRect(shadowRect, new Color(0, 0, 0, 100));

        // Background
        DrawRect(dropRect, PanelBg);
        // INF11: Stronger border (2px) to distinguish from content behind
        DrawBorder(dropRect, AccentColor, 2);

        // Draw filter text box at the top if applicable
        if (dd.ShowFilter)
        {
            var filterRect = new Rectangle(inputX + 2, dd.DropY + 1, inputW - 4, ComboFilterH - 2);
            DrawRect(filterRect, InputBg);
            DrawBorder(filterRect, InputActive);
            string filterDisplay = string.IsNullOrEmpty(dd.FilterText) ? "Type to filter..." : dd.FilterText;
            Color filterColor = string.IsNullOrEmpty(dd.FilterText) ? TextDim : TextColor;
            DrawText(filterDisplay, new Vector2(inputX + 5, dd.DropY + 3), filterColor);
            // Blinking cursor
            if (!string.IsNullOrEmpty(dd.FilterText) || _cursorBlink % 1.0f < 0.5f)
            {
                float cursorX = inputX + 5 + MeasureText(dd.FilterText ?? "").X;
                if (_cursorBlink % 1.0f < 0.5f)
                    DrawRect(new Rectangle((int)cursorX, dd.DropY + 3, 1, 14), TextBright);
            }
        }

        // Use scissor clipping for clean edges on the items area
        int itemsDropH = dd.VisibleCount * ComboItemH;
        var itemsClip = new Rectangle(inputX, dd.ItemsY, inputW, itemsDropH);
        BeginClip(itemsClip);

        // Draw items using the SAME pre-computed rects that click detection used
        for (int vi = 0; vi < dd.VisibleCount; vi++)
        {
            int fi = vi + scrollOffset;
            if (fi >= dd.FilteredOptions.Length) break;

            var optRect = dd.ItemRects[vi]; // Same rect as click detection
            bool optHovered = optRect.Contains(_mouse.X, _mouse.Y);
            bool isSelected = dd.FilteredOptions[fi] == dd.CurrentValue;
            bool isHighlighted = fi == dd.HighlightIdx;

            // Per-option hover tooltip — routed through the global service so it
            // draws on the Tooltip band, above this scissor clip and the list.
            if (optHovered && (dd.Tooltips != null || dd.TooltipFor != null))
            {
                int oi = dd.FilteredIndices[fi]; // filtered index -> effective option index
                string? tip = dd.Tooltips != null && oi < dd.Tooltips.Length ? dd.Tooltips[oi] : null;
                tip ??= dd.TooltipFor?.Invoke(oi);
                if (!string.IsNullOrEmpty(tip))
                    Game1.Tooltips.RequestLines(tip.Split('\n'));
            }

            if (isHighlighted)
                DrawRect(optRect, ComboHighlight);
            else if (optHovered || isSelected)
                DrawRect(optRect, optHovered ? ItemHover : ItemSelected);

            // Left gutter: a checkmark flags the currently selected option so
            // it reads at a glance; all items indent past it to stay aligned.
            if (isSelected)
                DrawUtils.DrawCheckmark(Scope, _pixel,
                    new Rectangle(optRect.X + 4, optRect.Y + 5, 10, 10), AccentColor);

            Color textCol = isSelected ? TextBright : (isHighlighted ? TextBright : TextColor);
            DrawText(dd.FilteredOptions[fi], new Vector2(optRect.X + 18, optRect.Y + 2), textCol);
        }

        EndClip();

        // Scrollbar indicator if scrollable (item-based scroll converted to pixels)
        if (dd.NeedsScroll)
            DrawVScrollbar(inputX + inputW - 6, dd.ItemsY, itemsDropH,
                dd.FilteredOptions.Length * ComboItemH, scrollOffset * ComboItemH);

        // Consume the pending overlay so nested editors that BOTH call
        // DrawDropdownOverlays (wall/env editor inside the map editor) don't
        // render the same dropdown twice per frame.
        _pendingDropdown = null;
    }

    /// <summary>
    /// Draw an HdrColor editor (R,G,B,A sliders + intensity)
    /// </summary>
    public (Core.HdrColor color, int height) DrawHdrColorField(string fieldId, string label, Core.HdrColor color, int x, int y, int w)
    {
        DrawText(label, new Vector2(x, y + 2), TextDim);
        int rowH = 22;
        int curY = y + rowH;

        // Color preview swatch
        var previewColor = color.ToScaledColor();
        DrawRect(new Rectangle(x + 120, y, 40, 18), previewColor);
        DrawBorder(new Rectangle(x + 120, y, 40, 18), InputBorder);

        // R, G, B, A sliders
        int newR = DrawIntField(fieldId + "_r", "  R", color.R, x, curY, w);
        color.R = (byte)Math.Clamp(newR, 0, 255);
        curY += rowH;

        int newG = DrawIntField(fieldId + "_g", "  G", color.G, x, curY, w);
        color.G = (byte)Math.Clamp(newG, 0, 255);
        curY += rowH;

        int newB = DrawIntField(fieldId + "_b", "  B", color.B, x, curY, w);
        color.B = (byte)Math.Clamp(newB, 0, 255);
        curY += rowH;

        int newA = DrawIntField(fieldId + "_a", "  A", color.A, x, curY, w);
        color.A = (byte)Math.Clamp(newA, 0, 255);
        curY += rowH;

        float newI = DrawFloatField(fieldId + "_i", "  Intensity", color.Intensity, x, curY, w, 0.1f);
        color.Intensity = Math.Max(0, newI);
        curY += rowH;

        return (color, curY - y);
    }

    /// <summary>
    /// Draw a clickable color swatch that opens the HSV color picker popup.
    /// Returns true when OK is pressed (color was changed).
    /// </summary>
    public bool DrawColorSwatch(string id, int x, int y, int w, int h, ref HdrColor color, bool hideIntensity = false)
    {
        // If the color picker is open for this id, pass hideIntensity on open (already handled)
        // The ColorSwatch method handles drawing, click detection, and popup open/sync.
        // Pass the input-block state so a click on an open dropdown overlapping this
        // swatch doesn't fall through and spuriously pop the picker. Also treat a
        // swatch scrolled outside the active clip as blocked (invisible ⇒ inert).
        return _colorPicker.ColorSwatch(id, x, y, w, h, ref color,
            IsInputBlocked(EffectiveLayer(0)) || !(_activeClip == null || _activeClip.Value.Contains(_mouse.X, _mouse.Y)));
    }

    /// <summary>
    /// Open the color picker popup for a specific color (alternative to swatch-based opening).
    /// </summary>
    public void OpenColorPicker(string id, HdrColor color, bool hideIntensity = false)
    {
        _colorPicker.Open(id, color, hideIntensity);
    }

    /// <summary>
    /// Draw the color picker popup overlay. Call this AFTER all other editor drawing
    /// so it renders on top of everything.
    /// </summary>
    public void DrawColorPickerPopup()
    {
        // Once per frame — nested editors (wall/env inside map editor) may both
        // call this; the second call would re-run the picker's click-edge logic.
        if (_colorPickerDrawnThisFrame) return;
        _colorPickerDrawnThisFrame = true;

        // Capture back buffer for eyedropper if dropper is active. Flush the
        // in-flight HUD batch first — GetBackBufferData only sees what has been
        // RENDERED, and with the batch un-flushed the dropper sampled the bare
        // world behind the editor panels instead of what's on screen.
        if (_colorPicker.IsDropperActive)
        {
            // Same captured scope for the whole Suspend/Resume cycle — a re-read
            // mid-cycle would compute the wrong resume material.
            var scope = Scope;
            scope.Suspend();
            _colorPicker.CaptureBackBuffer(_gd);
            scope.Resume();
        }
        _colorPicker.Draw();
    }

    /// <summary>
    /// Returns true if the color picker popup is currently open (to suppress other interactions).
    /// </summary>
    public bool IsColorPickerOpen => _colorPicker.IsOpen;
    public bool IsDropdownOpen => _activeFieldId != null && _activeFieldId.EndsWith("_combo");

    /// <summary>
    /// Returns true if the eyedropper mode is active.
    /// </summary>
    public bool IsDropperActive => _colorPicker.IsDropperActive;

    /// <summary>
    /// Activate the eyedropper tool on the color picker.
    /// </summary>
    public void OpenDropper()
    {
        _colorPicker.OpenDropper();
    }

    /// <summary>
    /// Draw a search/filter text field (no label, full width)
    /// </summary>
    public string DrawSearchField(string fieldId, string value, int x, int y, int w)
    {
        var inputRect = new Rectangle(x, y, w, 22);
        var (text, _, _) = FieldCore(fieldId, value, inputRect, placeholder: "Search...");
        return text;
    }

    // === INF08: Slider + value box combo widget ===

    /// <summary>
    /// Draw a horizontal slider with a numeric value box to the right.
    /// Click the value box to type exact values.
    /// Returns the new float value.
    /// </summary>
    public float DrawSliderFloat(string id, string label, float value, float min, float max, int x, int y, int w)
    {
        int labelW = 120;
        DrawText(label, new Vector2(x, y + 2), TextDim);

        int sliderX = x + labelW;
        int valueBoxW = 50;
        int sliderW = w - labelW - valueBoxW - 4;
        int sliderH = 16;
        int trackY = y + 2;

        // -- Slider track --
        var trackRect = new Rectangle(sliderX, trackY, sliderW, sliderH);
        DrawRect(trackRect, InputBg);
        DrawBorder(trackRect, InputBorder);

        // Filled portion
        float t = (max > min) ? Math.Clamp((value - min) / (max - min), 0f, 1f) : 0f;
        int fillW = (int)(t * (sliderW - 4));
        if (fillW > 0)
            DrawRect(new Rectangle(sliderX + 2, trackY + 2, fillW, sliderH - 4), AccentColor);

        // Thumb
        int thumbX = sliderX + 2 + (int)(t * (sliderW - 4)) - 4;
        DrawRect(new Rectangle(thumbX, trackY, 8, sliderH), TextBright);

        // Slider interaction with drag capture: the slider grabbed on mouse-down
        // owns the drag until release. Without this, dragging vertically off the
        // track onto a neighbouring slider's hit area would hijack that slider.
        string sliderDragId = id + "_slider";
        bool overTrack = !IsInputBlocked(EffectiveLayer(0))
            && HitTest(new Rectangle(sliderX - 4, trackY - 4, sliderW + 8, sliderH + 8));
        if (_mouse.LeftButton == ButtonState.Pressed && _prevMouse.LeftButton == ButtonState.Released && overTrack)
            _activeSliderId = sliderDragId;

        if (_activeSliderId == sliderDragId && _mouse.LeftButton == ButtonState.Pressed)
        {
            // Follow only horizontal movement; ignore Y so leaving the track
            // vertically keeps dragging this slider.
            float newT = Math.Clamp((float)(_mouse.X - sliderX - 2) / (sliderW - 4), 0f, 1f);
            value = min + newT * (max - min);
            string valueFieldId = id + "_val";
            if (_activeFieldId == valueFieldId)
                _inputBuffer = value.ToString("F2");
        }

        // -- Value box (clickable text field) --
        string valueFieldId2 = id + "_val";
        int vbX = sliderX + sliderW + 4;
        var vbRect = new Rectangle(vbX, y, valueBoxW, 20);

        var (vbText, vbActive, vbCommitted) = FieldCore(valueFieldId2, value.ToString("F2"), vbRect);
        if (vbCommitted)
            return float.TryParse(vbText, out float parsed) ? Math.Clamp(parsed, min, max) : value;

        if (vbActive && float.TryParse(_inputBuffer, out float cur))
            return Math.Clamp(cur, min, max);

        return Math.Clamp(value, min, max);
    }

    // === INF09: Multi-line text area widget ===

    /// <summary>
    /// Draw a multi-line text area with word wrapping and scrolling.
    /// Click to edit, supports Enter for new lines.
    /// Returns the updated string.
    /// </summary>
    public string DrawTextArea(string id, string value, int x, int y, int w, int h)
    {
        var areaRect = new Rectangle(x, y, w, h);
        string areaFieldId = id + "_textarea";
        bool isActive = _activeFieldId == areaFieldId;
        bool hovered = IsHovered(areaRect);

        // Click to activate
        if (hovered && _mouse.LeftButton == ButtonState.Pressed && _prevMouse.LeftButton == ButtonState.Released)
        {
            _activeFieldId = areaFieldId;
            _inputBuffer = value ?? "";
            _textAreaCursorPos = _inputBuffer.Length;
            _textAreaScrollOffset = 0;
            _cursorBlink = 0;
            _textFieldLayer.Panel = areaRect;
            isActive = true;
        }
        // Click outside to deactivate
        else if (isActive && _mouse.LeftButton == ButtonState.Pressed && _prevMouse.LeftButton == ButtonState.Released && !hovered)
        {
            _activeFieldId = null;
            return _inputBuffer;
        }

        // Background
        DrawRect(areaRect, InputBg);
        DrawBorder(areaRect, isActive ? InputActive : InputBorder);

        string text = isActive ? _inputBuffer : (value ?? "");

        // Word-wrap the text into lines
        var lines = WrapText(text, w - 8);

        // Scroll handling
        int lineH = 16;
        int visibleLines = Math.Max(1, (h - 4) / lineH);
        int totalLines = lines.Count;

        if (isActive && !_scrollConsumed && hovered)
        {
            int scrollDelta = _mouse.ScrollWheelValue - _prevMouse.ScrollWheelValue;
            if (scrollDelta != 0)
            {
                _textAreaScrollOffset -= scrollDelta > 0 ? 1 : -1;
                _textAreaScrollOffset = Math.Clamp(_textAreaScrollOffset, 0, Math.Max(0, totalLines - visibleLines));
                ConsumeScroll();
            }
        }
        _textAreaScrollOffset = Math.Clamp(_textAreaScrollOffset, 0, Math.Max(0, totalLines - visibleLines));

        // Draw text lines
        int drawY = y + 2;
        for (int i = _textAreaScrollOffset; i < lines.Count && drawY < y + h - 2; i++)
        {
            DrawText(lines[i], new Vector2(x + 4, drawY), isActive ? TextBright : TextColor);
            drawY += lineH;
        }

        // Draw cursor
        if (isActive && ((int)(_cursorBlink * 2) % 2 == 0))
        {
            // Find cursor line and column
            int charCount = 0;
            int curLine = 0;
            int curCol = 0;
            for (int i = 0; i < lines.Count; i++)
            {
                if (charCount + lines[i].Length >= _textAreaCursorPos)
                {
                    curLine = i;
                    curCol = _textAreaCursorPos - charCount;
                    break;
                }
                charCount += lines[i].Length;
                if (i < lines.Count - 1) charCount++; // newline char
            }

            if (curLine >= _textAreaScrollOffset && curLine < _textAreaScrollOffset + visibleLines)
            {
                string beforeCursor = curCol <= lines[curLine].Length ? lines[curLine][..curCol] : lines[curLine];
                float cxPos = x + 4 + MeasureText(beforeCursor).X;
                int cyPos = y + 2 + (curLine - _textAreaScrollOffset) * lineH;
                DrawRect(new Rectangle((int)cxPos, cyPos, 1, lineH), TextBright);
            }
        }

        // Scrollbar indicator (line-based scroll converted to pixels)
        DrawVScrollbar(x + w - 6, y + 2, h - 4, totalLines * lineH, _textAreaScrollOffset * lineH);

        return isActive ? _inputBuffer : (value ?? "");
    }

    /// <summary>
    /// Word-wrap text to fit within the given pixel width.
    /// </summary>
    private List<string> WrapText(string text, int maxWidth)
    {
        var result = new List<string>();
        if (string.IsNullOrEmpty(text)) { result.Add(""); return result; }

        // Split on explicit newlines first
        var paragraphs = text.Split('\n');
        foreach (var para in paragraphs)
        {
            if (string.IsNullOrEmpty(para)) { result.Add(""); continue; }

            var words = para.Split(' ');
            string currentLine = "";
            foreach (var word in words)
            {
                string candidate = string.IsNullOrEmpty(currentLine) ? word : currentLine + " " + word;
                if (MeasureText(candidate).X > maxWidth && !string.IsNullOrEmpty(currentLine))
                {
                    result.Add(currentLine);
                    currentLine = word;
                }
                else
                {
                    currentLine = candidate;
                }
            }
            result.Add(currentLine);
        }
        return result;
    }

    // === INF14: Status message alpha fade helper ===

    /// <summary>
    /// Get an alpha-faded color for status messages.
    /// Timer counts down from a positive value; the last 1.0 second fades out.
    /// </summary>
    public static Color FadeStatusColor(Color baseColor, float timer)
    {
        if (timer <= 0) return Color.Transparent;
        float alpha = timer < 1f ? timer : 1f;
        // Straight-alpha fade (A only) - the draw surface premultiplies.
        return ColorUtils.Fade(baseColor, alpha);
    }

    // === Text Input Handling ===

    private void HandleTextInput(GameTime gameTime)
    {
        if (_activeFieldId == null) return;
        // Skip combo dropdowns
        if (_activeFieldId.EndsWith("_combo")) return;

        bool isTextArea = _activeFieldId.EndsWith("_textarea");

        var pressed = _kb.GetPressedKeys();
        double dt = gameTime.ElapsedGameTime.TotalSeconds;
        bool shift = _kb.IsKeyDown(Keys.LeftShift) || _kb.IsKeyDown(Keys.RightShift);
        bool ctrl = _kb.IsKeyDown(Keys.LeftControl) || _kb.IsKeyDown(Keys.RightControl);

        foreach (var key in pressed)
        {
            bool justPressed = _prevKb.IsKeyUp(key);
            bool repeat = false;

            if (!justPressed && key == _lastRepeatingKey)
            {
                _keyRepeatTimer += dt;
                if (_keyRepeatTimer > 0.4)
                {
                    _keyRepeatTimer -= 0.03;
                    repeat = true;
                }
            }

            if (!justPressed && !repeat) continue;

            if (justPressed)
            {
                _lastRepeatingKey = key;
                _keyRepeatTimer = 0;
            }

            if (isTextArea)
            {
                // Multi-line text area input handling
                _textAreaCursorPos = Math.Clamp(_textAreaCursorPos, 0, _inputBuffer.Length);

                if (key == Keys.Back && _textAreaCursorPos > 0)
                {
                    _inputBuffer = _inputBuffer[..(_textAreaCursorPos - 1)] + _inputBuffer[_textAreaCursorPos..];
                    _textAreaCursorPos--;
                    _cursorBlink = 0;
                }
                else if (key == Keys.Delete && _textAreaCursorPos < _inputBuffer.Length)
                {
                    _inputBuffer = _inputBuffer[.._textAreaCursorPos] + _inputBuffer[(_textAreaCursorPos + 1)..];
                    _cursorBlink = 0;
                }
                else if (key == Keys.Enter)
                {
                    _inputBuffer = _inputBuffer[.._textAreaCursorPos] + "\n" + _inputBuffer[_textAreaCursorPos..];
                    _textAreaCursorPos++;
                    _cursorBlink = 0;
                }
                else if (key == Keys.Left && _textAreaCursorPos > 0)
                {
                    _textAreaCursorPos--;
                    _cursorBlink = 0;
                }
                else if (key == Keys.Right && _textAreaCursorPos < _inputBuffer.Length)
                {
                    _textAreaCursorPos++;
                    _cursorBlink = 0;
                }
                else if (key == Keys.Home)
                {
                    _textAreaCursorPos = 0;
                    _cursorBlink = 0;
                }
                else if (key == Keys.End)
                {
                    _textAreaCursorPos = _inputBuffer.Length;
                    _cursorBlink = 0;
                }
                else if (key == Keys.Tab)
                {
                    // Tab closes text area
                    _activeFieldId = null;
                    return;
                }
                else if (key == Keys.Escape)
                {
                    _activeFieldId = null;
                    return;
                }
                else
                {
                    char? c = KeyToChar(key, shift);
                    if (c.HasValue)
                    {
                        _inputBuffer = _inputBuffer[.._textAreaCursorPos] + c.Value + _inputBuffer[_textAreaCursorPos..];
                        _textAreaCursorPos++;
                        _cursorBlink = 0;
                    }
                }
            }
            else
            {
                // Single-line text input with cursor positioning and selection
                _cursorPos = Math.Clamp(_cursorPos, 0, _inputBuffer.Length);

                if (key == Keys.Enter || key == Keys.Tab)
                {
                    _activeFieldId = null;
                    _selectAll = false;
                    _selectionStart = -1;
                    return;
                }
                else if (key == Keys.Escape)
                {
                    _activeFieldId = null;
                    _selectAll = false;
                    _selectionStart = -1;
                    return;
                }
                else if (key == Keys.Back)
                {
                    if (_selectAll || HasSelection)
                    {
                        DeleteSelection();
                        _selectAll = false;
                    }
                    else if (_cursorPos > 0)
                    {
                        _inputBuffer = _inputBuffer[..(_cursorPos - 1)] + _inputBuffer[_cursorPos..];
                        _cursorPos--;
                    }
                    _cursorBlink = 0;
                }
                else if (key == Keys.Delete)
                {
                    if (_selectAll || HasSelection)
                    {
                        DeleteSelection();
                        _selectAll = false;
                    }
                    else if (_cursorPos < _inputBuffer.Length)
                    {
                        _inputBuffer = _inputBuffer[.._cursorPos] + _inputBuffer[(_cursorPos + 1)..];
                    }
                    _cursorBlink = 0;
                }
                else if (key == Keys.Left)
                {
                    if (HasSelection && !shift)
                    {
                        _cursorPos = Math.Min(_selectionStart, _cursorPos);
                        _selectionStart = -1;
                    }
                    else
                    {
                        if (shift && _selectionStart < 0) _selectionStart = _cursorPos;
                        if (_cursorPos > 0) _cursorPos--;
                        if (!shift) _selectionStart = -1;
                    }
                    _selectAll = false;
                    _cursorBlink = 0;
                }
                else if (key == Keys.Right)
                {
                    if (HasSelection && !shift)
                    {
                        _cursorPos = Math.Max(_selectionStart, _cursorPos);
                        _selectionStart = -1;
                    }
                    else
                    {
                        if (shift && _selectionStart < 0) _selectionStart = _cursorPos;
                        if (_cursorPos < _inputBuffer.Length) _cursorPos++;
                        if (!shift) _selectionStart = -1;
                    }
                    _selectAll = false;
                    _cursorBlink = 0;
                }
                else if (key == Keys.Home)
                {
                    if (shift && _selectionStart < 0) _selectionStart = _cursorPos;
                    _cursorPos = 0;
                    if (!shift) _selectionStart = -1;
                    _selectAll = false;
                    _cursorBlink = 0;
                }
                else if (key == Keys.End)
                {
                    if (shift && _selectionStart < 0) _selectionStart = _cursorPos;
                    _cursorPos = _inputBuffer.Length;
                    if (!shift) _selectionStart = -1;
                    _selectAll = false;
                    _cursorBlink = 0;
                }
                else if (key == Keys.A && ctrl)
                {
                    // Ctrl+A: select all
                    _selectionStart = 0;
                    _cursorPos = _inputBuffer.Length;
                    _selectAll = false;
                    _cursorBlink = 0;
                }
                else if (ctrl && key == Keys.C)
                {
                    // Ctrl+C: copy selection (or the whole field if nothing selected).
                    string sel = SelectedText();
                    if (sel.Length == 0) sel = _inputBuffer;
                    if (sel.Length > 0) { try { TextCopy.ClipboardService.SetText(sel); } catch { } }
                }
                else if (ctrl && key == Keys.X)
                {
                    // Ctrl+X: cut selection to clipboard.
                    string sel = SelectedText();
                    if (sel.Length > 0)
                    {
                        try { TextCopy.ClipboardService.SetText(sel); } catch { }
                        if (_selectAll) { _inputBuffer = ""; _cursorPos = 0; _selectionStart = -1; _selectAll = false; }
                        else DeleteSelection();
                        _cursorBlink = 0;
                    }
                }
                else if (ctrl && key == Keys.V)
                {
                    // Ctrl+V: paste, replacing any selection. Single-line field, so
                    // strip control chars (newlines/tabs) from the clipboard text.
                    string paste = "";
                    try { paste = TextCopy.ClipboardService.GetText() ?? ""; } catch { }
                    paste = StripControlChars(paste);
                    if (paste.Length > 0)
                    {
                        if (_selectAll) { _inputBuffer = ""; _cursorPos = 0; _selectionStart = -1; _selectAll = false; }
                        else if (HasSelection) DeleteSelection();
                        _inputBuffer = _inputBuffer[.._cursorPos] + paste + _inputBuffer[_cursorPos..];
                        _cursorPos += paste.Length;
                        _cursorBlink = 0;
                    }
                }
                else if (ctrl && KeyToChar(key, shift).HasValue)
                {
                    // Other Ctrl+<letter> chords (Ctrl+S, etc.) are not text — swallow
                    // them so they don't insert a stray character into the field.
                }
                else
                {
                    char? c = KeyToChar(key, shift);
                    if (c.HasValue)
                    {
                        // If select-all or has selection, replace selected text
                        if (_selectAll)
                        {
                            _inputBuffer = c.Value.ToString();
                            _cursorPos = 1;
                            _selectionStart = -1;
                            _selectAll = false;
                        }
                        else if (HasSelection)
                        {
                            DeleteSelection();
                            _inputBuffer = _inputBuffer[.._cursorPos] + c.Value + _inputBuffer[_cursorPos..];
                            _cursorPos++;
                        }
                        else
                        {
                            _inputBuffer = _inputBuffer[.._cursorPos] + c.Value + _inputBuffer[_cursorPos..];
                            _cursorPos++;
                        }
                        _cursorBlink = 0;
                    }
                }
            }
        }
    }

    private static char? KeyToChar(Keys key, bool shift)
    {
        if (key >= Keys.A && key <= Keys.Z)
        {
            char c = (char)('a' + (key - Keys.A));
            return shift ? char.ToUpper(c) : c;
        }
        if (key >= Keys.D0 && key <= Keys.D9)
        {
            if (shift)
            {
                return (key - Keys.D0) switch
                {
                    1 => '!', 2 => '@', 3 => '#', 4 => '$', 5 => '%',
                    6 => '^', 7 => '&', 8 => '*', 9 => '(', 0 => ')',
                    _ => null
                };
            }
            return (char)('0' + (key - Keys.D0));
        }
        if (key >= Keys.NumPad0 && key <= Keys.NumPad9)
            return (char)('0' + (key - Keys.NumPad0));

        return key switch
        {
            Keys.Space => ' ',
            Keys.OemPeriod => shift ? '>' : '.',
            Keys.OemComma => shift ? '<' : ',',
            Keys.OemMinus => shift ? '_' : '-',
            Keys.OemPlus => shift ? '+' : '=',
            Keys.OemQuestion => shift ? '?' : '/',
            Keys.OemSemicolon => shift ? ':' : ';',
            Keys.OemQuotes => shift ? '"' : '\'',
            Keys.OemOpenBrackets => shift ? '{' : '[',
            Keys.OemCloseBrackets => shift ? '}' : ']',
            Keys.OemBackslash => shift ? '|' : '\\',
            Keys.OemTilde => shift ? '~' : '`',
            Keys.Decimal => '.',
            Keys.Divide => '/',
            Keys.Multiply => '*',
            Keys.Subtract => '-',
            Keys.Add => '+',
            _ => null
        };
    }

    /// <summary>
    /// Returns true if any text input field is currently active (to suppress game input)
    /// </summary>
    public bool IsTextInputActive => _activeFieldId != null && !_activeFieldId.EndsWith("_combo");

    /// <summary>
    /// True whenever typed keys belong to a UI element rather than hotkeys: an
    /// active text field, an open combo dropdown (its filter box eats keys), or
    /// the color picker's value/hex boxes. Gate editor hotkeys / Game1 F-key
    /// toggles on this, not on <see cref="IsTextInputActive"/> alone.
    /// </summary>
    public bool IsKeyboardCaptured => IsTextInputActive || IsDropdownOpen || _colorPicker.IsEditingText;

    /// <summary>
    /// Returns true if a specific field (by fieldId) is currently the active text input.
    /// </summary>
    public bool IsFieldActive(string fieldId) => _activeFieldId == fieldId;

    /// <summary>
    /// Deactivate any active text field
    /// </summary>
    public void ClearActiveField() { _activeFieldId = null; }

    /// <summary>
    /// Replace the active field's edit buffer. For fields that display a
    /// truncated value while idle but must edit the full one: call right after
    /// the field activates to swap the truncated display text for the real value.
    /// </summary>
    public void SetActiveFieldText(string fieldId, string text)
    {
        if (_activeFieldId != fieldId) return;
        _inputBuffer = text ?? "";
        _cursorPos = _inputBuffer.Length;
        _selectionStart = 0;
        _selectAll = true;
    }

    /// <summary>
    /// Reset all interactive state (active field, dropdown, color picker).
    /// Call when closing an editor to prevent stale state from blocking input.
    /// </summary>
    public void ResetAllState()
    {
        _activeFieldId = null;
        _comboHighlightIdx = -1;
        _colorPicker.Close();
    }

    /// <summary>
    /// If an active combo dropdown is open, close it and return true.
    /// Editors should call this at the top of their Escape cascade so that
    /// Escape closes the dropdown first before closing sub-editors/popups.
    /// </summary>
    public bool CloseActiveDropdown()
    {
        if (_activeFieldId != null && _activeFieldId.EndsWith("_combo"))
        {
            _comboFilterText.Remove(_activeFieldId);
            _comboHighlightIdx = -1;
            _activeFieldId = null;
            return true;
        }
        return false;
    }

    // === Confirmation Dialog ===

    // Singleton layer for confirm dialogs — only one can be open per editor.
    // Push on entry when isOpen=true, pop when buttons / ESC flip it to false.
    private readonly Necroking.UI.ActionModalLayer _confirmDialogLayer = new() { LightDismiss = false };

    /// <summary>
    /// Draw a centered confirmation dialog with dark overlay.
    /// Returns true if Confirm is clicked. Sets isOpen to false on either button.
    /// Call this AFTER all other editor drawing so it renders on top.
    /// </summary>
    public bool DrawConfirmDialog(string title, string message, ref bool isOpen)
    {
        if (!isOpen)
        {
            // Caller decided to close between frames — make sure the modal
            // stack doesn't think we're still here.
            if (Necroking.Game1.Popups.Contains(_confirmDialogLayer))
                Necroking.Game1.Popups.Pop(_confirmDialogLayer);
            return false;
        }

        // Overlay contract: blocks every widget the host drew earlier this frame
        // (via next-frame pre-raise) and lets our own buttons interact at layer 3.
        BeginOverlay(3);

        // Dark overlay
        DrawRect(new Rectangle(0, 0, _screenW, _screenH), new Color(0, 0, 0, 150));

        // Dialog dimensions
        int dialogW = 360;
        int dialogH = 160;
        int dx = (_screenW - dialogW) / 2;
        int dy = (_screenH - dialogH) / 2;

        // Publish rect + register with the modal stack so PopupManager routes
        // ESC here (consuming the key before Game1's ESC chain runs) and eats
        // clicks outside the dialog.
        _confirmDialogLayer.Panel = new Rectangle(dx, dy, dialogW, dialogH);
        if (!Necroking.Game1.Popups.Contains(_confirmDialogLayer))
            Necroking.Game1.Popups.Push(_confirmDialogLayer);

        // Dialog background
        DrawRect(new Rectangle(dx, dy, dialogW, dialogH), PanelBg);
        DrawBorder(new Rectangle(dx, dy, dialogW, dialogH), PanelBorder, 2);

        // Title bar
        DrawRect(new Rectangle(dx, dy, dialogW, 28), PanelHeader);
        DrawRect(new Rectangle(dx, dy + 28, dialogW, 1), PanelBorder);
        DrawText(title, new Vector2(dx + 10, dy + 5), TextBright, _font);

        // Message text
        DrawText(message, new Vector2(dx + 16, dy + 44), TextColor);

        // Buttons
        int btnW = 90;
        int btnH = 28;
        int btnY = dy + dialogH - btnH - 16;
        int confirmX = dx + dialogW / 2 - btnW - 10;
        int cancelX = dx + dialogW / 2 + 10;

        bool confirmed = false;

        if (DrawButton("Confirm", confirmX, btnY, btnW, btnH, DangerColor))
        {
            confirmed = true;
            isOpen = false;
            Necroking.Game1.Popups.Pop(_confirmDialogLayer);
        }

        if (DrawButton("Cancel", cancelX, btnY, btnW, btnH))
        {
            isOpen = false;
            Necroking.Game1.Popups.Pop(_confirmDialogLayer);
        }

        // Also close on Escape — PopupManager has already consumed ESC for
        // the manager-aware path. This direct read remains so the dialog
        // closes its caller's ref-bool in the same frame instead of waiting
        // a tick.
        if (_kb.IsKeyDown(Keys.Escape) && _prevKb.IsKeyUp(Keys.Escape))
        {
            isOpen = false;
            Necroking.Game1.Popups.Pop(_confirmDialogLayer);
        }

        EndOverlay();
        return confirmed;
    }
}
