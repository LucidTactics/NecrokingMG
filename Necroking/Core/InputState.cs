using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;

namespace Necroking.Core;

/// <summary>Result of a UI click gesture (see <see cref="InputState.ClickOn"/>).</summary>
public enum ClickKind { None, Click, DoubleClick }

/// <summary>
/// Centralized input state captured once per frame. All systems read from this
/// instead of calling Mouse.GetState() / Keyboard.GetState() directly.
/// UI processes input first and consumes events to prevent pass-through to game world.
/// </summary>
public class InputState
{
    // Raw state (read-only after Capture)
    public MouseState Mouse;
    public MouseState PrevMouse;
    public KeyboardState Kb;
    public KeyboardState PrevKb;

    // Mouse state at the end of the previous Draw. Immediate-mode editor widgets
    // (EditorBase) measure their press/release edges against this rather than
    // PrevMouse, so a click survives fixed-timestep catch-up (several Updates per
    // Draw would collapse the one-frame edge before the Draw pass reads it).
    // Snapshotted once per Draw by SnapshotDrawFrame() — unconditionally, unlike
    // the per-editor copy this replaces, so it can never go stale while no
    // editor is open.
    public MouseState DrawPrevMouse;

    // Screen position where the current left press began; (-1,-1) when no press
    // is in flight or the gesture was consumed. Release-fired widgets require
    // the press to have STARTED inside their rect, so clearing this kills the
    // whole press→release gesture in one place.
    public Point PressStartPos = new(-1, -1);

    // Wall-clock seconds at the last Capture (framework total time, NOT world
    // time — double-click timing must keep working while the world is paused
    // or a menu is up). Stays put on frames where Capture doesn't run (focus
    // freeze), which absolute timestamps tolerate.
    public double Time;

    // Max gap between two clicks on the SAME object to count as a double-click.
    // Mirrored from GeneralSettings.DoubleClickMs by Game1 each frame.
    public double DoubleClickWindow = 0.5;

    // Double-click chain: the one most-recent completed click. A click on a
    // different object simply restarts the chain (two clicks on different
    // objects are two separate clicks, never a double).
    private string? _lastClickId;
    private double _lastClickTime = double.NegativeInfinity;

    // Derived helpers
    public Vector2 MousePos;
    public Vector2 PrevMousePos;
    public int ScrollDelta;
    public bool LeftPressed;   // just pressed this frame
    public bool LeftDown;      // held
    public bool LeftReleased;  // just released this frame
    public bool RightPressed;
    public bool RightDown;
    public bool RightReleased;

    // Consumption flags — once consumed, lower-priority systems should not act.
    // "Who consumed it" is no longer tracked here: the UIRouter dispatch walks
    // layers top-down and stamps UILayer.InputGranted per layer, so a layer
    // knows whether the click was its to take without identity bookkeeping.
    private bool _mouseConsumed;
    private bool _scrollConsumed;
    private bool _kbConsumed;

    // Per-key consumption — lets a top-of-stack popup eat ESC for itself without
    // blocking unrelated keys (movement, hotkeys) on the same frame. The kb-wide
    // _kbConsumed flag is kept for text-input scenarios where a popup wants to
    // swallow every key for that frame (text fields, hex-edit boxes, etc.).
    private readonly HashSet<Keys> _consumedKeys = new();

    public bool IsMouseConsumed => _mouseConsumed;
    public bool IsScrollConsumed => _scrollConsumed;
    public bool IsKbConsumed => _kbConsumed;

    public void ConsumeMouse() { _mouseConsumed = true; MouseOverUI = true; }
    public void ConsumeScroll() => _scrollConsumed = true;
    public void ConsumeKb() => _kbConsumed = true;
    public void ConsumeKey(Keys key) => _consumedKeys.Add(key);
    public bool IsKeyConsumed(Keys key) => _kbConsumed || _consumedKeys.Contains(key);

    /// <summary>WasKeyPressed gated by per-key consumption. Use this in any
    /// site that should respect a higher-layer's claim on the key. The original
    /// <see cref="WasKeyPressed"/> ignores consumption for cases (text-input
    /// fields, debug toggles) that genuinely want raw access.</summary>
    public bool WasKeyPressedUnhandled(Keys key)
        => WasKeyPressed(key) && !IsKeyConsumed(key);

    /// <summary>
    /// True when the mouse is hovering over any UI element (even without clicking).
    /// Game-world interactions (spell cast, unit select, etc.) should check this.
    /// </summary>
    public bool MouseOverUI;

    /// <summary>Capture raw input state at the top of each frame. Resets all consumption flags.
    /// <paramref name="time"/> is wall-clock seconds (gameTime.TotalGameTime) for click timing.</summary>
    public void Capture(MouseState mouse, MouseState prevMouse,
                        KeyboardState kb, KeyboardState prevKb, double time)
    {
        Time = time;
        Mouse = mouse;
        PrevMouse = prevMouse;
        Kb = kb;
        PrevKb = prevKb;

        MousePos = new Vector2(mouse.X, mouse.Y);
        PrevMousePos = new Vector2(prevMouse.X, prevMouse.Y);
        ScrollDelta = mouse.ScrollWheelValue - prevMouse.ScrollWheelValue;

        LeftPressed = mouse.LeftButton == ButtonState.Pressed && prevMouse.LeftButton == ButtonState.Released;
        if (LeftPressed) PressStartPos = new Point(mouse.X, mouse.Y);
        LeftDown = mouse.LeftButton == ButtonState.Pressed;
        LeftReleased = mouse.LeftButton == ButtonState.Released && prevMouse.LeftButton == ButtonState.Pressed;
        RightPressed = mouse.RightButton == ButtonState.Pressed && prevMouse.RightButton == ButtonState.Released;
        RightDown = mouse.RightButton == ButtonState.Pressed;
        RightReleased = mouse.RightButton == ButtonState.Released && prevMouse.RightButton == ButtonState.Pressed;

        // Reset per-frame flags
        _mouseConsumed = false;
        _scrollConsumed = false;
        _kbConsumed = false;
        _consumedKeys.Clear();
        MouseOverUI = false;
    }

    /// <summary>Snapshot the current mouse state as the edge reference for the
    /// next Draw's immediate-mode widgets. Call exactly once per Draw, after all
    /// editor widgets have drawn, regardless of whether an editor is open.</summary>
    public void SnapshotDrawFrame() => DrawPrevMouse = Mouse;

    /// <summary>Invalidate the in-flight press gesture. A press that caused a UI
    /// mode change (opened an editor, toggled a menu) belongs to the widget it
    /// started on and must not also fire a widget that appeared under the cursor
    /// in the new mode — e.g. the spell editor's [X] eating the launcher click
    /// that opened it. The next physical press re-stamps PressStartPos. Also
    /// kills the double-click chain — a click on the old screen must not pair
    /// with a click on whatever the new mode put under the cursor.</summary>
    public void ConsumeGesture() { PressStartPos = new Point(-1, -1); _lastClickId = null; }

    /// <summary>Feed one completed click on a UI object into the double-click
    /// chain. Returns true when it pairs with the previous click — same id,
    /// within <see cref="DoubleClickWindow"/> — in which case the chain resets
    /// (a triple click is a double + a fresh single). Any other click just
    /// (re)starts the chain. Use a stable, globally unique id per object
    /// (e.g. "menu:Play", "combo:ch_elem").</summary>
    public bool RegisterClick(string id)
    {
        bool isDouble = id == _lastClickId && Time - _lastClickTime <= DoubleClickWindow;
        if (isDouble)
        {
            _lastClickId = null;
        }
        else
        {
            _lastClickId = id;
            _lastClickTime = Time;
        }
        return isDouble;
    }

    /// <summary>Forget the pending double-click chain without touching the
    /// press gesture (e.g. a drag started, or the clicked object vanished).</summary>
    public void InvalidateDoubleClick() => _lastClickId = null;

    /// <summary>UI click gesture for release-fired widgets, evaluated against the
    /// Update-side edges. Fires only on release, and only when the press STARTED
    /// inside <paramref name="rect"/> and was released inside it — press-in/
    /// release-out and press-out/release-in both cancel. Returns DoubleClick when
    /// this click is the second on the same <paramref name="id"/> within the
    /// window. World-object clicks should stay on the eager LeftPressed edge
    /// instead, so time-critical clicks (targeting a moving unit) aren't delayed.</summary>
    public ClickKind ClickOn(string id, Rectangle rect)
    {
        if (!LeftReleased || _mouseConsumed) return ClickKind.None;
        if (!rect.Contains(Mouse.X, Mouse.Y) || !rect.Contains(PressStartPos)) return ClickKind.None;
        return RegisterClick(id) ? ClickKind.DoubleClick : ClickKind.Click;
    }

    // Keyboard helpers
    public bool WasKeyPressed(Keys key) => Kb.IsKeyDown(key) && PrevKb.IsKeyUp(key);
    public bool IsKeyDown(Keys key) => Kb.IsKeyDown(key);
    public bool IsKeyUp(Keys key) => Kb.IsKeyUp(key);
    public bool WasKeyReleased(Keys key) => Kb.IsKeyUp(key) && PrevKb.IsKeyDown(key);
}
