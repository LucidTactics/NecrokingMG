namespace Necroking.Lib;

/// <summary>
/// Eager-dropdown interaction state machine: dropdowns that behave like native
/// OS menus, where press→drag→release selects in a single gesture instead of
/// the usual click-to-open, click-to-select two-gesture flow. Ported from a
/// proven Unity component; its contract:
///
///  1. On button DOWN: expand the dropdown, mark all items for selection —
///     releasing over an item selects it immediately.
///  2. On button DOWN while open: over an item marks it; on the open box marks
///     the dropdown to close (unless the release lands on an item); elsewhere
///     closes it.
///  3. On button UP: select the hovered item if marked.
///
/// Click-click still works as a degenerate case (open-click's release lands on
/// the box, the next press on an item arms it, release selects), so this is a
/// strict superset of a classic dropdown.
///
/// Unlike Unity there are no persistent per-widget objects here, so this is a
/// plain state machine owned by the panel that draws the dropdowns: the owner
/// keeps all rects, does its own hit-testing, and feeds hit results in. One
/// instance covers N sibling dropdowns (only one list is ever open) via an int
/// key — typically the row index. Engine-free on purpose (no XNA types).
///
/// Wiring expectations (see DebugSettingsPanel for the reference use):
///  - Presses granted to the owner go to <see cref="OnPress"/>; presses that
///    landed outside it go to <see cref="OnPressOutside"/>.
///  - Releases are polled every frame and fed to <see cref="OnRelease"/>;
///    pass gestureValid = false when the press→release gesture was consumed
///    (InputState.PressStartPos == (-1,-1)) so a UI-mode change can't
///    ghost-select on its release.
/// </summary>
public sealed class EagerDropdown
{
    /// <summary>Key of the expanded dropdown (caller-defined, e.g. row index);
    /// -1 = none open.</summary>
    public int OpenKey { get; private set; } = -1;

    public bool IsOpen => OpenKey >= 0;

    // Release-over-item selects. Set by the opening press ("mark all items")
    // and by any press over an item while open.
    private bool _armed;

    // Second press on the already-open box: close on release — unless the
    // release lands on an item, in which case selection wins.
    private bool _wantHide;

    public void Close()
    {
        OpenKey = -1;
        _armed = false;
        _wantHide = false;
    }

    /// <summary>A press granted to the owner. <paramref name="boxKey"/> is the
    /// dropdown box under the cursor (-1 = none); <paramref name="itemIndex"/>
    /// is the option under the cursor in the OPEN list (-1 = none).</summary>
    public void OnPress(int boxKey, int itemIndex)
    {
        if (!IsOpen)
        {
            if (boxKey >= 0) { OpenKey = boxKey; _armed = true; }
            return;
        }

        if (itemIndex >= 0) { _armed = true; return; }
        // Own box: close on release — but stay armed so a drag from the box
        // onto an item still selects on release (selection wins over hide,
        // matching the Unity OnPointerUp-before-OnPointerClick order).
        if (boxKey == OpenKey) { _wantHide = true; _armed = true; return; }
        if (boxKey >= 0) { OpenKey = boxKey; _armed = true; _wantHide = false; return; }
        Close(); // press inside the owner but on nothing interactive
    }

    /// <summary>A press that landed outside the owner entirely.</summary>
    public void OnPressOutside() => Close();

    /// <summary>Left-button release. <paramref name="itemIndex"/> is the option
    /// under the cursor in the open list (-1 = none). Returns the selected item
    /// index, or -1 if nothing was selected. The caller must snapshot
    /// <see cref="OpenKey"/> BEFORE calling — a selection closes the list.</summary>
    public int OnRelease(int itemIndex, bool gestureValid)
    {
        if (!IsOpen) return -1;

        if (!gestureValid)
        {
            // Gesture consumed mid-flight (UI-mode change) — abort, stay open.
            _armed = false;
            _wantHide = false;
            return -1;
        }

        if (_armed && itemIndex >= 0)
        {
            Close();
            return itemIndex;
        }

        if (_wantHide) { Close(); return -1; }
        _armed = false;
        return -1;
    }
}
