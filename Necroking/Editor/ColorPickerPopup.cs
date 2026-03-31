using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using Necroking.Core;

namespace Necroking.Editor;

/// <summary>
/// Full HSV color picker popup with hue bar, saturation/brightness square,
/// RGB+A+Intensity sliders, preview swatches, eyedropper, value box editing,
/// and OK/Cancel buttons.
/// </summary>
public class ColorPickerPopup
{
    // Drawing context (set externally via SetContext)
    private SpriteBatch _sb = null!;
    private Texture2D _pixel = null!;
    private SpriteFont? _font;
    private SpriteFont? _smallFont;

    // Input state
    private MouseState _mouse;
    private MouseState _prevMouse;
    private KeyboardState _kb;
    private KeyboardState _prevKb;
    private int _screenW, _screenH;

    // Popup state
    private bool _isOpen;
    private string _activeId = "";
    private bool _hideIntensity;
    private HdrColor _currentColor;
    private HdrColor _originalColor;

    // HSV working values (0-360, 0-1, 0-1)
    private float _hue;
    private float _sat;
    private float _val;

    // Popup geometry
    private int _popupX, _popupY;
    private const int PopupW = 310;
    private const int PopupBaseH = 608;
    private const int TitleBarH = 28;

    // Drag state for title bar
    private bool _dragging;
    private int _dragOffX, _dragOffY;

    // Slider drag tracking
    private enum DragTarget { None, HueBar, SVSquare, SliderR, SliderG, SliderB, SliderA, SliderIntensity }
    private DragTarget _activeDrag = DragTarget.None;

    // Result state
    private bool _confirmed;  // true when OK is pressed (user accepted the change)
    private bool _cancelled;  // true when Cancel is pressed (user rejected the change)
    private static string? _hexClipboard; // internal hex clipboard fallback
    private bool _hexSelectAll;           // select-all on first click into hex field

    // === Eyedropper state (CP01) ===
    private bool _dropperActive;
    private bool _dropperFromPopup;     // true = return to popup after pick
    private Color[] _backBufferData = Array.Empty<Color>();
    private int _backBufferW, _backBufferH;

    // === Value box editing state (CP02) ===
    // -1=none, 0=R, 1=G, 2=B, 3=A, 4=Intensity
    private int _editingField = -1;
    private string _editBuffer = "";
    private float _editCursorBlink;

    public bool IsOpen => _isOpen || _dropperActive;
    public bool IsDropperActive => _dropperActive;

    public void SetContext(SpriteBatch sb, Texture2D pixel, SpriteFont? font, SpriteFont? smallFont)
    {
        _sb = sb;
        _pixel = pixel;
        _font = font;
        _smallFont = smallFont;
    }

    public void UpdateInput(MouseState mouse, MouseState prevMouse, KeyboardState kb, KeyboardState prevKb,
        int screenW, int screenH, float deltaTime)
    {
        _mouse = mouse;
        _prevMouse = prevMouse;
        _kb = kb;
        _prevKb = prevKb;
        _screenW = screenW;
        _screenH = screenH;
        _editCursorBlink += deltaTime;

        if (_dropperActive)
            HandleDropperInput();
        else if (_isOpen)
            HandlePopupInput();
    }

    // Overload for backward compatibility (no keyboard)
    public void UpdateInput(MouseState mouse, MouseState prevMouse, int screenW, int screenH)
    {
        UpdateInput(mouse, prevMouse, default, default, screenW, screenH, 0f);
    }

    // === HSV Conversion ===

    public static (float h, float s, float v) RgbToHsv(byte r, byte g, byte b)
    {
        float rf = r / 255f;
        float gf = g / 255f;
        float bf = b / 255f;

        float max = MathF.Max(rf, MathF.Max(gf, bf));
        float min = MathF.Min(rf, MathF.Min(gf, bf));
        float delta = max - min;

        float h = 0f;
        if (delta > 0.0001f)
        {
            if (max == rf)
                h = 60f * (((gf - bf) / delta) % 6f);
            else if (max == gf)
                h = 60f * (((bf - rf) / delta) + 2f);
            else
                h = 60f * (((rf - gf) / delta) + 4f);
        }
        if (h < 0f) h += 360f;

        float s = max > 0.0001f ? delta / max : 0f;
        float v = max;

        return (h, s, v);
    }

    public static (byte r, byte g, byte b) HsvToRgb(float h, float s, float v)
    {
        h = ((h % 360f) + 360f) % 360f;
        float c = v * s;
        float x = c * (1f - MathF.Abs((h / 60f) % 2f - 1f));
        float m = v - c;

        float rf, gf, bf;
        if (h < 60f) { rf = c; gf = x; bf = 0; }
        else if (h < 120f) { rf = x; gf = c; bf = 0; }
        else if (h < 180f) { rf = 0; gf = c; bf = x; }
        else if (h < 240f) { rf = 0; gf = x; bf = c; }
        else if (h < 300f) { rf = x; gf = 0; bf = c; }
        else { rf = c; gf = 0; bf = x; }

        return (
            (byte)Math.Clamp((int)((rf + m) * 255f + 0.5f), 0, 255),
            (byte)Math.Clamp((int)((gf + m) * 255f + 0.5f), 0, 255),
            (byte)Math.Clamp((int)((bf + m) * 255f + 0.5f), 0, 255)
        );
    }

    // === Open/Close ===

    public void Open(string id, HdrColor color, bool hideIntensity = false)
    {
        _activeId = id;
        _isOpen = true;
        _hideIntensity = hideIntensity;
        _currentColor = color;
        _originalColor = color;
        _confirmed = false;
        _cancelled = false;
        _activeDrag = DragTarget.None;
        _dragging = false;
        _editingField = -1;
        _editBuffer = "";

        // Convert initial RGB to HSV
        (_hue, _sat, _val) = RgbToHsv(color.R, color.G, color.B);

        // Center popup on screen
        int popupH = _hideIntensity ? PopupBaseH - 24 : PopupBaseH;
        _popupX = (_screenW - PopupW) / 2;
        _popupY = (_screenH - popupH) / 2;
    }

    public void Close()
    {
        _isOpen = false;
        _activeDrag = DragTarget.None;
        _dragging = false;
        _dropperActive = false;
        _editingField = -1;
    }

    /// <summary>
    /// Activate eyedropper mode. Call CaptureBackBuffer() before drawing the overlay.
    /// </summary>
    public void OpenDropper()
    {
        _dropperActive = true;
        _dropperFromPopup = _isOpen;
    }

    /// <summary>
    /// Capture the back buffer for eyedropper pixel sampling.
    /// Must be called while the back buffer is available (after Present or via GetBackBufferData).
    /// </summary>
    public void CaptureBackBuffer(GraphicsDevice device)
    {
        if (!_dropperActive) return;

        int w = device.PresentationParameters.BackBufferWidth;
        int h = device.PresentationParameters.BackBufferHeight;

        if (_backBufferData.Length != w * h)
            _backBufferData = new Color[w * h];

        _backBufferW = w;
        _backBufferH = h;

        device.GetBackBufferData(_backBufferData);
    }

    // === Color Swatch (integration method) ===

    /// <summary>
    /// Draws a clickable color preview swatch at the given position.
    /// When clicked, opens the popup for that color.
    /// Returns true when OK is pressed (color was changed).
    /// </summary>
    public bool ColorSwatch(string id, int x, int y, int w, int h, ref HdrColor color)
    {
        // Draw swatch background (checkerboard for alpha)
        DrawRect(new Rectangle(x, y, w, h), new Color(40, 40, 40));

        // Draw the color
        var displayColor = color.ToScaledColor();
        DrawRect(new Rectangle(x, y, w, h), displayColor);
        DrawBorder(new Rectangle(x, y, w, h), EditorBase.InputBorder);

        // Check click
        var swatchRect = new Rectangle(x, y, w, h);
        bool hovered = swatchRect.Contains(_mouse.X, _mouse.Y);
        bool clicked = hovered && _mouse.LeftButton == ButtonState.Released &&
                       _prevMouse.LeftButton == ButtonState.Pressed;

        if (clicked && !_isOpen)
        {
            Open(id, color);
        }

        // If this swatch's popup is open, sync the result
        if (_isOpen && _activeId == id)
        {
            if (_confirmed)
            {
                // OK pressed: write back the (possibly edited) color
                color = _currentColor;
                Close();
                return true;
            }
            if (_cancelled)
            {
                // Cancel pressed: restore original color, do not report as confirmed
                color = _originalColor;
                Close();
                return false;
            }
            // Live preview: update color while popup is open
            color = _currentColor;
        }

        return false;
    }

    // === Eyedropper Input Handling (CP01) ===

    private void HandleDropperInput()
    {
        bool leftJustPressed = _mouse.LeftButton == ButtonState.Pressed &&
                               _prevMouse.LeftButton == ButtonState.Released;
        bool rightJustPressed = _mouse.RightButton == ButtonState.Pressed &&
                                _prevMouse.RightButton == ButtonState.Released;
        bool escPressed = _kb.IsKeyDown(Keys.Escape) && _prevKb.IsKeyUp(Keys.Escape);

        // Click to pick
        if (leftJustPressed)
        {
            Color picked = SampleBackBuffer(_mouse.X, _mouse.Y);
            _currentColor.R = picked.R;
            _currentColor.G = picked.G;
            _currentColor.B = picked.B;
            (_hue, _sat, _val) = RgbToHsv(picked.R, picked.G, picked.B);
            _dropperActive = false;
            // If came from popup, popup stays open
        }

        // Cancel on ESC or right-click
        if (escPressed || rightJustPressed)
        {
            _dropperActive = false;
        }
    }

    private Color SampleBackBuffer(int mx, int my)
    {
        mx = Math.Clamp(mx, 0, _backBufferW - 1);
        my = Math.Clamp(my, 0, _backBufferH - 1);
        if (_backBufferData.Length == 0) return Color.Black;
        int idx = my * _backBufferW + mx;
        if (idx < 0 || idx >= _backBufferData.Length) return Color.Black;
        return _backBufferData[idx];
    }

    // === Value Box Editing (CP02) ===

    private bool HandleValueBox(Rectangle bounds, string displayText, int fieldId)
    {
        bool isEditing = _editingField == fieldId;
        bool confirmed = false;

        bool leftJustPressed = _mouse.LeftButton == ButtonState.Pressed &&
                               _prevMouse.LeftButton == ButtonState.Released;

        // Background
        DrawRect(bounds, isEditing ? new Color(55, 55, 75) : new Color(35, 35, 50));
        DrawBorder(bounds, isEditing ? new Color(140, 170, 255) : new Color(65, 65, 85));

        if (isEditing)
        {
            // Draw editable text
            DrawText(_editBuffer, new Vector2(bounds.X + 3, bounds.Y + 2), EditorBase.TextBright);

            // Blinking cursor
            if ((int)(_editCursorBlink * 3) % 2 == 0)
            {
                float cursorXPos = bounds.X + 3 + MeasureText(_editBuffer).X;
                DrawRect(new Rectangle((int)cursorXPos, bounds.Y + 2, 1, bounds.Height - 4), Color.White);
            }

            // Key input
            foreach (var key in _kb.GetPressedKeys())
            {
                if (_prevKb.IsKeyUp(key))
                {
                    if (key == Keys.Back && _editBuffer.Length > 0)
                    {
                        _editBuffer = _editBuffer[..^1];
                        _editCursorBlink = 0;
                    }
                    else if (key == Keys.Enter || key == Keys.Tab)
                    {
                        _editingField = -1;
                        confirmed = true;
                    }
                    else if (key == Keys.Escape)
                    {
                        _editingField = -1;
                    }
                    else
                    {
                        char? c = KeyToNumericChar(key);
                        if (c.HasValue && _editBuffer.Length < 14)
                        {
                            _editBuffer += c.Value;
                            _editCursorBlink = 0;
                        }
                    }
                }
            }

            // Click outside value box to confirm
            if (leftJustPressed && !bounds.Contains(_mouse.X, _mouse.Y))
            {
                _editingField = -1;
                confirmed = true;
            }
        }
        else
        {
            // Draw static value
            DrawText(displayText, new Vector2(bounds.X + 3, bounds.Y + 2), EditorBase.TextColor);

            // Click to start editing
            if (leftJustPressed && bounds.Contains(_mouse.X, _mouse.Y))
            {
                _editingField = fieldId;
                _editBuffer = displayText;
                _editCursorBlink = 0;
            }
        }

        return confirmed;
    }

    private static char? KeyToHexChar(Keys key)
    {
        if (key >= Keys.D0 && key <= Keys.D9) return (char)('0' + (key - Keys.D0));
        if (key >= Keys.NumPad0 && key <= Keys.NumPad9) return (char)('0' + (key - Keys.NumPad0));
        if (key >= Keys.A && key <= Keys.F) return (char)('A' + (key - Keys.A));
        if (key == Keys.OemPeriod) return null; // block period in hex
        return null;
    }

    private static char? KeyToNumericChar(Keys key)
    {
        if (key >= Keys.D0 && key <= Keys.D9)
            return (char)('0' + (key - Keys.D0));
        if (key >= Keys.NumPad0 && key <= Keys.NumPad9)
            return (char)('0' + (key - Keys.NumPad0));
        if (key == Keys.OemPeriod || key == Keys.Decimal)
            return '.';
        if (key == Keys.OemMinus || key == Keys.Subtract)
            return '-';
        return null;
    }

    // === Popup Input Handling ===

    private void HandlePopupInput()
    {
        bool leftDown = _mouse.LeftButton == ButtonState.Pressed;
        bool leftJustPressed = leftDown && _prevMouse.LeftButton == ButtonState.Released;
        bool leftJustReleased = !leftDown && _prevMouse.LeftButton == ButtonState.Pressed;

        int popupH = _hideIntensity ? PopupBaseH - 24 : PopupBaseH;

        // If a value box is being edited, don't process slider drags
        if (_editingField >= 0)
            return;

        // Title bar dragging
        var titleRect = new Rectangle(_popupX, _popupY, PopupW, TitleBarH);

        // Check Pick button bounds (right side of title bar) to avoid starting drag
        int pickBtnW = 50, pickBtnH = 20;
        int pickBtnX = _popupX + PopupW - pickBtnW - 8;
        int pickBtnY2 = _popupY + (TitleBarH - pickBtnH) / 2;
        var pickBtnRect = new Rectangle(pickBtnX, pickBtnY2, pickBtnW, pickBtnH);

        if (leftJustPressed && titleRect.Contains(_mouse.X, _mouse.Y) &&
            !pickBtnRect.Contains(_mouse.X, _mouse.Y) && _activeDrag == DragTarget.None)
        {
            _dragging = true;
            _dragOffX = _mouse.X - _popupX;
            _dragOffY = _mouse.Y - _popupY;
        }
        if (_dragging)
        {
            if (leftDown)
            {
                _popupX = _mouse.X - _dragOffX;
                _popupY = _mouse.Y - _dragOffY;
                // Clamp to screen
                _popupX = Math.Clamp(_popupX, 0, _screenW - PopupW);
                _popupY = Math.Clamp(_popupY, 0, _screenH - popupH);
            }
            else
            {
                _dragging = false;
            }
        }

        // Layout positions (must match Draw)
        int pad = 10;
        int cx = _popupX + pad;
        int cy = _popupY + TitleBarH + 8;
        int innerW = PopupW - pad * 2;

        // RI02: Hue bar before SV square (matching draw order)
        // RI04: Hue label offset
        cy += 14;

        // Hue bar
        int hueBarH = 18;
        var hueRect = new Rectangle(cx, cy, innerW, hueBarH);
        cy += hueBarH + 8;

        // RI03: SV label offset
        cy += 14;

        // SV square
        int svSize = innerW;
        var svRect = new Rectangle(cx, cy, svSize, svSize);
        cy += svSize + 10;

        // Sliders
        int sliderH = 18;
        int labelW = 20;
        int sliderX = cx + labelW + 4;
        int sliderW = innerW - labelW - 50;

        var sliderRRect = new Rectangle(sliderX, cy, sliderW, sliderH);
        cy += sliderH + 6;
        var sliderGRect = new Rectangle(sliderX, cy, sliderW, sliderH);
        cy += sliderH + 6;
        var sliderBRect = new Rectangle(sliderX, cy, sliderW, sliderH);
        cy += sliderH + 6;
        var sliderARect = new Rectangle(sliderX, cy, sliderW, sliderH);
        cy += sliderH + 6;

        Rectangle sliderIRect = default;
        if (!_hideIntensity)
        {
            sliderIRect = new Rectangle(sliderX, cy, sliderW, sliderH);
            cy += sliderH + 6;
        }

        // Handle drag starts (RI02: hue bar checked before SV square)
        if (leftJustPressed && !_dragging)
        {
            if (hueRect.Contains(_mouse.X, _mouse.Y))
                _activeDrag = DragTarget.HueBar;
            else if (svRect.Contains(_mouse.X, _mouse.Y))
                _activeDrag = DragTarget.SVSquare;
            else if (sliderRRect.Contains(_mouse.X, _mouse.Y))
                _activeDrag = DragTarget.SliderR;
            else if (sliderGRect.Contains(_mouse.X, _mouse.Y))
                _activeDrag = DragTarget.SliderG;
            else if (sliderBRect.Contains(_mouse.X, _mouse.Y))
                _activeDrag = DragTarget.SliderB;
            else if (sliderARect.Contains(_mouse.X, _mouse.Y))
                _activeDrag = DragTarget.SliderA;
            else if (!_hideIntensity && sliderIRect.Contains(_mouse.X, _mouse.Y))
                _activeDrag = DragTarget.SliderIntensity;
        }

        // Handle drag updates
        if (leftDown && _activeDrag != DragTarget.None)
        {
            switch (_activeDrag)
            {
                case DragTarget.SVSquare:
                {
                    float sx = Math.Clamp((_mouse.X - svRect.X) / (float)svRect.Width, 0f, 1f);
                    float sy = Math.Clamp((_mouse.Y - svRect.Y) / (float)svRect.Height, 0f, 1f);
                    _sat = sx;
                    _val = 1f - sy;
                    UpdateRgbFromHsv();
                    break;
                }
                case DragTarget.HueBar:
                {
                    float hx = Math.Clamp((_mouse.X - hueRect.X) / (float)hueRect.Width, 0f, 1f);
                    _hue = hx * 360f;
                    UpdateRgbFromHsv();
                    break;
                }
                case DragTarget.SliderR:
                {
                    float t = Math.Clamp((_mouse.X - sliderRRect.X) / (float)sliderRRect.Width, 0f, 1f);
                    _currentColor.R = (byte)(t * 255f + 0.5f);
                    UpdateHsvFromRgb();
                    break;
                }
                case DragTarget.SliderG:
                {
                    float t = Math.Clamp((_mouse.X - sliderGRect.X) / (float)sliderGRect.Width, 0f, 1f);
                    _currentColor.G = (byte)(t * 255f + 0.5f);
                    UpdateHsvFromRgb();
                    break;
                }
                case DragTarget.SliderB:
                {
                    float t = Math.Clamp((_mouse.X - sliderBRect.X) / (float)sliderBRect.Width, 0f, 1f);
                    _currentColor.B = (byte)(t * 255f + 0.5f);
                    UpdateHsvFromRgb();
                    break;
                }
                case DragTarget.SliderA:
                {
                    float t = Math.Clamp((_mouse.X - sliderARect.X) / (float)sliderARect.Width, 0f, 1f);
                    _currentColor.A = (byte)(t * 255f + 0.5f);
                    break;
                }
                case DragTarget.SliderIntensity:
                {
                    float t = Math.Clamp((_mouse.X - sliderIRect.X) / (float)sliderIRect.Width, 0f, 1f);
                    _currentColor.Intensity = t * 15f;  // RI05: range 0-15
                    break;
                }
            }
        }

        // Release drag
        if (leftJustReleased)
        {
            _activeDrag = DragTarget.None;
        }
    }

    private void UpdateRgbFromHsv()
    {
        var (r, g, b) = HsvToRgb(_hue, _sat, _val);
        _currentColor.R = r;
        _currentColor.G = g;
        _currentColor.B = b;
    }

    private void UpdateHsvFromRgb()
    {
        (_hue, _sat, _val) = RgbToHsv(_currentColor.R, _currentColor.G, _currentColor.B);
    }

    // === Drawing ===

    /// <summary>
    /// Draw the popup overlay and all controls. Call this after all other editor drawing.
    /// </summary>
    public void Draw()
    {
        // Eyedropper overlay takes priority
        if (_dropperActive)
        {
            DrawDropperOverlay();
            return;
        }

        if (!_isOpen) return;

        // Escape cancels the popup (restores original color)
        if (_kb.IsKeyDown(Keys.Escape) && _prevKb.IsKeyUp(Keys.Escape) && _editingField < 0)
        {
            _currentColor = _originalColor;
            _cancelled = true;
            return;
        }

        int popupH = _hideIntensity ? PopupBaseH - 24 : PopupBaseH;

        // Clamp popup position to screen bounds
        _popupX = Math.Clamp(_popupX, 0, Math.Max(0, _screenW - PopupW));
        _popupY = Math.Clamp(_popupY, 0, Math.Max(0, _screenH - popupH));

        // Dark overlay behind popup
        DrawRect(new Rectangle(0, 0, _screenW, _screenH), new Color(0, 0, 0, 160));

        // Popup background
        var popupRect = new Rectangle(_popupX, _popupY, PopupW, popupH);
        DrawRect(popupRect, EditorBase.PanelBg);
        DrawBorder(popupRect, EditorBase.PanelBorder, 2);

        // Title bar
        var titleRect = new Rectangle(_popupX, _popupY, PopupW, TitleBarH);
        DrawRect(titleRect, EditorBase.PanelHeader);
        DrawRect(new Rectangle(_popupX, _popupY + TitleBarH, PopupW, 1), EditorBase.PanelBorder);
        DrawText("COLOR PICKER", new Vector2(_popupX + 10, _popupY + 5), EditorBase.TextBright, _font);

        // === Eyedropper "Pick" button in title bar (CP01) ===
        int pickBtnW = 50, pickBtnH = 20;
        int pickBtnX = _popupX + PopupW - pickBtnW - 8;
        int pickBtnY2 = _popupY + (TitleBarH - pickBtnH) / 2;
        if (DrawButton("Pick", pickBtnX, pickBtnY2, pickBtnW, pickBtnH, new Color(40, 45, 65)))
        {
            _dropperActive = true;
            _dropperFromPopup = true;
        }

        int pad = 10;
        int cx = _popupX + pad;
        int cy = _popupY + TitleBarH + 8;
        int innerW = PopupW - pad * 2;

        // RI02: Draw hue bar first, then SV square (matching C++ reference)
        // RI04: "Hue" label above the hue bar
        DrawText("Hue", new Vector2(cx, cy), EditorBase.TextDim);
        cy += 14;

        // === Hue bar ===
        int hueBarH = 18;
        DrawHueBar(cx, cy, innerW, hueBarH);

        // Hue cursor
        int hueCursorX = cx + (int)((_hue / 360f) * innerW);
        DrawRect(new Rectangle(hueCursorX - 1, cy - 1, 3, hueBarH + 2), Color.White);
        DrawBorder(new Rectangle(hueCursorX - 2, cy - 2, 5, hueBarH + 4), Color.Black);

        cy += hueBarH + 8;

        // RI03: "Saturation / Brightness" label above the SV square
        DrawText("Saturation / Brightness", new Vector2(cx, cy), EditorBase.TextDim);
        cy += 14;

        // === Saturation/Value square ===
        int svSize = innerW;
        DrawSVSquare(cx, cy, svSize, svSize);

        // SV cursor (crosshair)
        int cursorX = cx + (int)(_sat * svSize);
        int cursorY = cy + (int)((1f - _val) * svSize);
        DrawRect(new Rectangle(cursorX - 5, cursorY, 11, 1), Color.White);
        DrawRect(new Rectangle(cursorX, cursorY - 5, 1, 11), Color.White);
        DrawRect(new Rectangle(cursorX - 6, cursorY - 1, 13, 3), new Color(0, 0, 0, 128));
        DrawRect(new Rectangle(cursorX - 1, cursorY - 6, 3, 13), new Color(0, 0, 0, 128));
        // Redraw center white cross on top of shadow
        DrawRect(new Rectangle(cursorX - 5, cursorY, 11, 1), Color.White);
        DrawRect(new Rectangle(cursorX, cursorY - 5, 1, 11), Color.White);

        cy += svSize + 10;

        // === RGB + A + Intensity sliders with value boxes ===
        int sliderH = 18;
        int labelW = 20;
        int sliderX = cx + labelW + 4;
        int sliderW = innerW - labelW - 50;
        int valueX = sliderX + sliderW + 4;
        int valueBoxW = 42;

        // R slider
        DrawText("R", new Vector2(cx, cy + 1), new Color(255, 100, 100));
        DrawGradientSliderH(sliderX, cy, sliderW, sliderH,
            new Color(0, (int)_currentColor.G, (int)_currentColor.B),
            new Color(255, (int)_currentColor.G, (int)_currentColor.B));
        DrawSliderCursor(sliderX, cy, sliderW, sliderH, _currentColor.R / 255f);
        // Value box (CP02)
        if (HandleValueBox(new Rectangle(valueX, cy, valueBoxW, sliderH), _currentColor.R.ToString(), 0))
        {
            if (int.TryParse(_editBuffer, out int parsed))
            {
                _currentColor.R = (byte)Math.Clamp(parsed, 0, 255);
                UpdateHsvFromRgb();
            }
        }
        cy += sliderH + 6;

        // G slider
        DrawText("G", new Vector2(cx, cy + 1), new Color(100, 255, 100));
        DrawGradientSliderH(sliderX, cy, sliderW, sliderH,
            new Color((int)_currentColor.R, 0, (int)_currentColor.B),
            new Color((int)_currentColor.R, 255, (int)_currentColor.B));
        DrawSliderCursor(sliderX, cy, sliderW, sliderH, _currentColor.G / 255f);
        if (HandleValueBox(new Rectangle(valueX, cy, valueBoxW, sliderH), _currentColor.G.ToString(), 1))
        {
            if (int.TryParse(_editBuffer, out int parsed))
            {
                _currentColor.G = (byte)Math.Clamp(parsed, 0, 255);
                UpdateHsvFromRgb();
            }
        }
        cy += sliderH + 6;

        // B slider
        DrawText("B", new Vector2(cx, cy + 1), new Color(100, 100, 255));
        DrawGradientSliderH(sliderX, cy, sliderW, sliderH,
            new Color((int)_currentColor.R, (int)_currentColor.G, 0),
            new Color((int)_currentColor.R, (int)_currentColor.G, 255));
        DrawSliderCursor(sliderX, cy, sliderW, sliderH, _currentColor.B / 255f);
        if (HandleValueBox(new Rectangle(valueX, cy, valueBoxW, sliderH), _currentColor.B.ToString(), 2))
        {
            if (int.TryParse(_editBuffer, out int parsed))
            {
                _currentColor.B = (byte)Math.Clamp(parsed, 0, 255);
                UpdateHsvFromRgb();
            }
        }
        cy += sliderH + 6;

        // A slider (RI07: checkerboard background to visualize transparency)
        DrawText("A", new Vector2(cx, cy + 1), EditorBase.TextDim);
        DrawCheckerboard(sliderX, cy, sliderW, sliderH, 6);
        DrawGradientSliderH(sliderX, cy, sliderW, sliderH,
            new Color(0, 0, 0, 0),
            new Color((int)_currentColor.R, (int)_currentColor.G, (int)_currentColor.B, 255));
        DrawSliderCursor(sliderX, cy, sliderW, sliderH, _currentColor.A / 255f);
        if (HandleValueBox(new Rectangle(valueX, cy, valueBoxW, sliderH), _currentColor.A.ToString(), 3))
        {
            if (int.TryParse(_editBuffer, out int parsed))
                _currentColor.A = (byte)Math.Clamp(parsed, 0, 255);
        }
        cy += sliderH + 6;

        // Intensity slider (HDR)
        if (!_hideIntensity)
        {
            DrawText("I", new Vector2(cx, cy + 1), new Color(255, 220, 100));
            var baseCol = new Color((int)_currentColor.R, (int)_currentColor.G, (int)_currentColor.B);
            DrawIntensitySlider(sliderX, cy, sliderW, sliderH, baseCol);
            DrawSliderCursor(sliderX, cy, sliderW, sliderH, _currentColor.Intensity / 15f);  // RI05: range 0-15
            if (HandleValueBox(new Rectangle(valueX, cy, valueBoxW, sliderH), _currentColor.Intensity.ToString("F1"), 4))
            {
                if (float.TryParse(_editBuffer, out float parsed))
                    _currentColor.Intensity = Math.Clamp(parsed, 0f, 15f);  // RI05: range 0-15
            }
            cy += sliderH + 6;
        }

        // === Preview swatches (RI08: "Preview:" / "Raw:" with smaller side-by-side swatches) ===
        int swatchH = 20;
        int labelWPrev = (int)MeasureText("Preview:").X + 4;
        int swatchW = 36;
        int rawLabelW = (int)MeasureText("Raw:").X + 4;

        // Preview: label + current color swatch
        DrawText("Preview:", new Vector2(cx, cy + 2), EditorBase.TextDim);
        int prevSwX = cx + labelWPrev;
        DrawRect(new Rectangle(prevSwX, cy, swatchW, swatchH), new Color(40, 40, 40));
        DrawRect(new Rectangle(prevSwX, cy, swatchW, swatchH), _currentColor.ToScaledColor());
        DrawBorder(new Rectangle(prevSwX, cy, swatchW, swatchH), EditorBase.InputBorder);

        // Raw: label + original color swatch (side-by-side)
        int rawStartX = prevSwX + swatchW + 12;
        DrawText("Raw:", new Vector2(rawStartX, cy + 2), EditorBase.TextDim);
        int rawSwX = rawStartX + rawLabelW;
        DrawRect(new Rectangle(rawSwX, cy, swatchW, swatchH), new Color(40, 40, 40));
        DrawRect(new Rectangle(rawSwX, cy, swatchW, swatchH), _originalColor.ToScaledColor());
        DrawBorder(new Rectangle(rawSwX, cy, swatchW, swatchH), EditorBase.InputBorder);

        // Raw base color info
        cy += swatchH + 2;
        string rawInfo = $"({_currentColor.R},{_currentColor.G},{_currentColor.B},{_currentColor.A}) x{_currentColor.Intensity:F1}";
        DrawText(rawInfo, new Vector2(cx, cy), new Color(100, 100, 120));
        cy += 16;

        // Hex code field (all color pickers)
        {
            string hexVal = $"#{_currentColor.R:X2}{_currentColor.G:X2}{_currentColor.B:X2}";
            DrawText("Hex:", new Vector2(cx, cy + 2), new Color(100, 100, 120));
            int hexFieldX = cx + 32;
            int hexFieldW = 80;
            var hexRect = new Rectangle(hexFieldX, cy, hexFieldW, 18);
            bool hexActive = _editingField == 100; // field ID 100 = hex
            bool hexHovered = hexRect.Contains(_mouse.X, _mouse.Y);

            DrawRect(hexRect, hexActive ? new Color(55, 55, 75) : new Color(35, 35, 50));
            DrawBorder(hexRect, hexActive ? new Color(140, 170, 255) : new Color(65, 65, 85));

            if (hexActive)
            {
                // Selection highlight when select-all is active
                if (_hexSelectAll)
                {
                    float selW = MeasureText(_editBuffer).X;
                    DrawRect(new Rectangle(hexFieldX + 3, cy + 2, (int)selW, 14), new Color(80, 120, 200, 140));
                }
                DrawText(_editBuffer, new Vector2(hexFieldX + 3, cy + 2), Color.White);
                if (!_hexSelectAll && (int)(_editCursorBlink * 3) % 2 == 0)
                {
                    float curXPos = hexFieldX + 3 + MeasureText(_editBuffer).X;
                    DrawRect(new Rectangle((int)curXPos, cy + 2, 1, 14), Color.White);
                }

                // Handle hex input
                bool ctrl = _kb.IsKeyDown(Keys.LeftControl) || _kb.IsKeyDown(Keys.RightControl);
                foreach (var key in _kb.GetPressedKeys())
                {
                    if (_prevKb.IsKeyUp(key))
                    {
                        if (key == Keys.Enter || key == Keys.Tab)
                        {
                            // Parse hex and apply
                            string hex = _editBuffer.TrimStart('#');
                            if (hex.Length == 6 && int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out int rgb))
                            {
                                _currentColor.R = (byte)((rgb >> 16) & 0xFF);
                                _currentColor.G = (byte)((rgb >> 8) & 0xFF);
                                _currentColor.B = (byte)(rgb & 0xFF);
                                (_hue, _sat, _val) = RgbToHsv(_currentColor.R, _currentColor.G, _currentColor.B);
                            }
                            _editingField = -1;
                        }
                        else if (key == Keys.Escape)
                        {
                            _editingField = -1;
                        }
                        else if (key == Keys.Back)
                        {
                            if (_hexSelectAll) { _editBuffer = "#"; _hexSelectAll = false; }
                            else if (_editBuffer.Length > 0) _editBuffer = _editBuffer[..^1];
                        }
                        else if (ctrl && key == Keys.V)
                        {
                            // Paste from clipboard
                            try
                            {
                                string clip = _hexClipboard ?? "";
                                // Try system clipboard via MonoGame's SDL
                                try { clip = TextCopy.ClipboardService.GetText() ?? clip; } catch { }
                                clip = clip.Trim().TrimStart('#');
                                if (clip.Length <= 8) _editBuffer = "#" + clip;
                                _hexSelectAll = false;
                            }
                            catch { }
                        }
                        else
                        {
                            char? c = KeyToHexChar(key);
                            if (c.HasValue)
                            {
                                if (_hexSelectAll) { _editBuffer = "#"; _hexSelectAll = false; }
                                if (_editBuffer.Length < 7)
                                    _editBuffer += c.Value;
                            }
                        }
                    }
                }
            }
            else
            {
                DrawText(hexVal, new Vector2(hexFieldX + 3, cy + 2), Color.White);

                if (hexHovered && _mouse.LeftButton == ButtonState.Pressed && _prevMouse.LeftButton == ButtonState.Released)
                {
                    _editingField = 100;
                    _editBuffer = hexVal;
                    _editCursorBlink = 0;
                    _hexSelectAll = true;
                }

                // Ctrl+C copies hex when hovering
                if (hexHovered && (_kb.IsKeyDown(Keys.LeftControl) || _kb.IsKeyDown(Keys.RightControl))
                    && _kb.IsKeyDown(Keys.C) && _prevKb.IsKeyUp(Keys.C))
                {
                    _hexClipboard = hexVal;
                    try { TextCopy.ClipboardService.SetText(hexVal); } catch { }
                }
            }
            cy += 22;
        }
        // === OK / Cancel buttons ===
        int btnW = 80;
        int btnH = 26;
        int btnY = _popupY + popupH - btnH - 10;
        int okX = _popupX + PopupW / 2 - btnW - 6;
        int cancelX = _popupX + PopupW / 2 + 6;

        if (DrawButton("OK", okX, btnY, btnW, btnH, EditorBase.AccentColor))
        {
            _confirmed = true;
        }
        if (DrawButton("Cancel", cancelX, btnY, btnW, btnH, EditorBase.DangerColor))
        {
            _currentColor = _originalColor;
            _cancelled = true;
        }
    }

    // === Eyedropper Overlay Drawing (CP01) ===

    private void DrawDropperOverlay()
    {
        int mx = Math.Clamp(_mouse.X, 0, _screenW - 1);
        int my = Math.Clamp(_mouse.Y, 0, _screenH - 1);
        Color pixelColor = SampleBackBuffer(mx, my);

        // Magnifier parameters
        const int magRadius = 4;      // 9x9 pixel sample
        const int magPx = 6;          // each pixel drawn as 6x6
        int magSize = (magRadius * 2 + 1) * magPx; // 54
        int prevW = magSize + 8;
        int prevH = magSize + 36;

        // Position preview near cursor, flip if near edges
        int prevX = mx + 24;
        int prevY = my - prevH - 8;
        if (prevX + prevW > _screenW) prevX = mx - prevW - 24;
        if (prevY < 0) prevY = my + 24;

        // Background panel
        DrawRect(new Rectangle(prevX, prevY, prevW, prevH), new Color(20, 20, 30, 230));
        DrawBorder(new Rectangle(prevX, prevY, prevW, prevH), new Color(100, 140, 200));

        // Magnified pixels
        int magX = prevX + 4, magY = prevY + 4;
        for (int dy = -magRadius; dy <= magRadius; dy++)
        {
            for (int dx = -magRadius; dx <= magRadius; dx++)
            {
                int sx = Math.Clamp(mx + dx, 0, _backBufferW - 1);
                int sy = Math.Clamp(my + dy, 0, _backBufferH - 1);
                Color pc = SampleBackBuffer(sx, sy);
                int bx = (dx + magRadius) * magPx;
                int by = (dy + magRadius) * magPx;
                DrawRect(new Rectangle(magX + bx, magY + by, magPx, magPx), pc);
            }
        }

        // Border around magnifier
        DrawBorder(new Rectangle(magX, magY, magSize, magSize), new Color(80, 80, 100));

        // Highlight center pixel
        int centerX = magX + magRadius * magPx;
        int centerY = magY + magRadius * magPx;
        DrawBorder(new Rectangle(centerX, centerY, magPx, magPx), Color.White);

        // Sampled color swatch + RGB text below magnifier
        int swY = magY + magSize + 4;
        DrawRect(new Rectangle(magX, swY, 22, 22), pixelColor);
        DrawBorder(new Rectangle(magX, swY, 22, 22), Color.White);
        DrawText($"({pixelColor.R},{pixelColor.G},{pixelColor.B})",
            new Vector2(magX + 26, swY + 4), Color.White);

        // Hint text at top of screen
        string hint = "Click to pick color  |  ESC / Right-click to cancel";
        var hintSize = MeasureText(hint);
        int hintX = (int)((_screenW - hintSize.X) / 2);
        DrawRect(new Rectangle(hintX - 8, 4, (int)hintSize.X + 16, 22), new Color(20, 20, 30, 220));
        DrawText(hint, new Vector2(hintX, 6), new Color(200, 220, 255));
    }

    // === Gradient / Specialized Drawing ===

    private void DrawSVSquare(int x, int y, int w, int h)
    {
        int step = 2;
        for (int row = 0; row < h; row += step)
        {
            float v = 1f - (float)row / h;
            for (int col = 0; col < w; col += step)
            {
                float s = (float)col / w;
                var (r, g, b) = HsvToRgb(_hue, s, v);
                DrawRect(new Rectangle(x + col, y + row, step, step), new Color(r, g, b));
            }
        }
        DrawBorder(new Rectangle(x, y, w, h), EditorBase.InputBorder);
    }

    private void DrawHueBar(int x, int y, int w, int h)
    {
        for (int col = 0; col < w; col++)
        {
            float hue = (float)col / w * 360f;
            var (r, g, b) = HsvToRgb(hue, 1f, 1f);
            DrawRect(new Rectangle(x + col, y, 1, h), new Color(r, g, b));
        }
        DrawBorder(new Rectangle(x, y, w, h), EditorBase.InputBorder);
    }

    private void DrawGradientSliderH(int x, int y, int w, int h, Color left, Color right)
    {
        DrawRect(new Rectangle(x, y, w, h), EditorBase.InputBg);

        int step = 2;
        for (int col = 0; col < w; col += step)
        {
            float t = (float)col / w;
            var c = Color.Lerp(left, right, t);
            DrawRect(new Rectangle(x + col, y, step, h), c);
        }
        DrawBorder(new Rectangle(x, y, w, h), new Color(50, 50, 70));
    }

    private void DrawIntensitySlider(int x, int y, int w, int h, Color baseCol)
    {
        DrawRect(new Rectangle(x, y, w, h), EditorBase.InputBg);

        int midX = w / 2;
        int step = 2;
        for (int col = 0; col < w; col += step)
        {
            Color c;
            if (col < midX)
            {
                float localT = (float)col / midX;
                c = Color.Lerp(Color.Black, baseCol, localT);
            }
            else
            {
                float localT = (float)(col - midX) / (w - midX);
                c = Color.Lerp(baseCol, Color.White, localT);
            }
            DrawRect(new Rectangle(x + col, y, step, h), c);
        }
        DrawBorder(new Rectangle(x, y, w, h), new Color(50, 50, 70));
    }

    private void DrawCheckerboard(int x, int y, int w, int h, int cellSize)
    {
        for (int row = 0; row < h; row += cellSize)
        {
            for (int col = 0; col < w; col += cellSize)
            {
                bool dark = ((col / cellSize) + (row / cellSize)) % 2 == 0;
                int cw = Math.Min(cellSize, w - col);
                int ch = Math.Min(cellSize, h - row);
                DrawRect(new Rectangle(x + col, y + row, cw, ch),
                    dark ? new Color(60, 60, 60) : new Color(120, 120, 120));
            }
        }
    }

    private void DrawSliderCursor(int x, int y, int w, int h, float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        int cx = x + (int)(t * w);
        DrawRect(new Rectangle(cx - 1, y - 1, 3, h + 2), Color.White);
        DrawBorder(new Rectangle(cx - 2, y - 2, 5, h + 4), Color.Black);
    }

    // === Drawing primitives (mirrors EditorBase) ===

    private void DrawRect(Rectangle rect, Color color)
    {
        _sb.Draw(_pixel, rect, color);
    }

    private void DrawBorder(Rectangle rect, Color color, int thickness = 1)
    {
        DrawRect(new Rectangle(rect.X, rect.Y, rect.Width, thickness), color);
        DrawRect(new Rectangle(rect.X, rect.Y + rect.Height - thickness, rect.Width, thickness), color);
        DrawRect(new Rectangle(rect.X, rect.Y, thickness, rect.Height), color);
        DrawRect(new Rectangle(rect.X + rect.Width - thickness, rect.Y, thickness, rect.Height), color);
    }

    private void DrawText(string text, Vector2 pos, Color color, SpriteFont? font = null)
    {
        var f = font ?? _smallFont ?? _font;
        if (f != null)
            _sb.DrawString(f, text, pos, color);
    }

    private Vector2 MeasureText(string text, SpriteFont? font = null)
    {
        var f = font ?? _smallFont ?? _font;
        return f?.MeasureString(text) ?? Vector2.Zero;
    }

    private bool DrawButton(string text, int x, int y, int w, int h, Color? bgOverride = null)
    {
        var rect = new Rectangle(x, y, w, h);
        bool hovered = rect.Contains(_mouse.X, _mouse.Y);
        bool pressed = hovered && _mouse.LeftButton == ButtonState.Pressed;
        bool clicked = hovered && _mouse.LeftButton == ButtonState.Released &&
                       _prevMouse.LeftButton == ButtonState.Pressed;

        Color bg = bgOverride ?? EditorBase.ButtonBg;
        if (pressed) bg = EditorBase.ButtonPress;
        else if (hovered)
        {
            bg = new Color(
                Math.Min(255, bg.R + 25),
                Math.Min(255, bg.G + 25),
                Math.Min(255, bg.B + 25),
                bg.A);
        }

        DrawRect(rect, bg);
        DrawBorder(rect, EditorBase.PanelBorder);
        var textSize = MeasureText(text);
        DrawText(text, new Vector2(x + (w - textSize.X) / 2, y + (h - textSize.Y) / 2), EditorBase.TextBright);
        return clicked;
    }

    /// <summary>
    /// Returns true if the popup is consuming input (so the caller can suppress other interactions).
    /// </summary>
    public bool ConsumesInput => _isOpen || _dropperActive;
}
