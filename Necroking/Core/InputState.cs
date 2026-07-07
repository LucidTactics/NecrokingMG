using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;

namespace Necroking.Core;

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

    /// <summary>Capture raw input state at the top of each frame. Resets all consumption flags.</summary>
    public void Capture(MouseState mouse, MouseState prevMouse,
                        KeyboardState kb, KeyboardState prevKb)
    {
        Mouse = mouse;
        PrevMouse = prevMouse;
        Kb = kb;
        PrevKb = prevKb;

        MousePos = new Vector2(mouse.X, mouse.Y);
        PrevMousePos = new Vector2(prevMouse.X, prevMouse.Y);
        ScrollDelta = mouse.ScrollWheelValue - prevMouse.ScrollWheelValue;

        LeftPressed = mouse.LeftButton == ButtonState.Pressed && prevMouse.LeftButton == ButtonState.Released;
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

    // Keyboard helpers
    public bool WasKeyPressed(Keys key) => Kb.IsKeyDown(key) && PrevKb.IsKeyUp(key);
    public bool IsKeyDown(Keys key) => Kb.IsKeyDown(key);
    public bool IsKeyUp(Keys key) => Kb.IsKeyUp(key);
    public bool WasKeyReleased(Keys key) => Kb.IsKeyUp(key) && PrevKb.IsKeyDown(key);
}
