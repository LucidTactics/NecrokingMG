using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;

namespace Necroking.Editor;

/// <summary>
/// Mid-level base class for full-screen editor windows. Sits above
/// <see cref="EditorBase"/> (which is the low-level drawing + input toolkit)
/// and owns the chrome that every editor was independently re-implementing:
///   • Status message + fade timer
///   • Dirty / unsaved-changes flag (with title asterisk)
///   • Top-bar title and the Ctrl+S keyboard shortcut
///   • WantsClose request flag
///
/// Subclasses MUST:
///   • Pass an <see cref="EditorBase"/> instance to the constructor
///   • Override <see cref="Title"/> to provide the window title
///   • Override <see cref="OnSave"/> to do the actual save work
///   • Call <see cref="TickChrome"/> once per frame from Draw before drawing UI
///   • Call <see cref="HandleStandardShortcuts"/> to opt into Ctrl+S handling
///
/// Subclasses SHOULD:
///   • Use <see cref="SetStatus"/> after Save / Delete / Copy / Paste events
///   • Use <see cref="MarkDirty"/> whenever the user edits a field
///   • Call <see cref="DrawTopBar"/> to render the title bar with X/Save buttons
///
/// New editors should always inherit from this class. Existing editors are
/// being migrated incrementally — SpellEditor is the first reference migration.
/// </summary>
public abstract class EditorWindow
{
    protected readonly EditorBase _ui;

    /// <summary>Display title for the editor's top bar. Subclass-defined.</summary>
    public abstract string Title { get; }

    /// <summary>Set true when the user clicks the [X] close button. The host
    /// (Game1) polls this each frame and closes the editor.</summary>
    public bool WantsClose { get; set; }

    /// <summary>True when the editor has user changes that haven't been saved
    /// yet. Drives the asterisk in the title bar.</summary>
    protected bool IsDirty { get; private set; }

    /// <summary>Current toast message. Faded out by <see cref="TickChrome"/>.</summary>
    protected string StatusMessage { get; private set; } = "";

    /// <summary>Remaining seconds before the status message fully fades. 0 = no
    /// message visible.</summary>
    protected float StatusTimer { get; private set; }

    protected EditorWindow(EditorBase ui)
    {
        _ui = ui ?? throw new ArgumentNullException(nameof(ui));
    }

    /// <summary>Set a toast message that fades out over <paramref name="durationSec"/>.
    /// FAIL-prefixed messages render red; everything else renders green.</summary>
    protected void SetStatus(string message, float durationSec = 2.0f)
    {
        StatusMessage = message ?? "";
        StatusTimer = durationSec;
    }

    /// <summary>Mark the editor as having unsaved changes. Virtual so subclasses
    /// can extend (e.g. SpellEditor also invalidates its preview cache).</summary>
    protected virtual void MarkDirty() => IsDirty = true;
    protected void ClearDirty() => IsDirty = false;

    /// <summary>Tick chrome timers (status fade). Call once per Draw, before
    /// rendering. Pass elapsed seconds for this frame.</summary>
    protected void TickChrome(float dt)
    {
        if (StatusTimer > 0) StatusTimer -= dt;
    }

    /// <summary>Subclass override — do the work of saving. Called from
    /// <see cref="HandleStandardShortcuts"/> when Ctrl+S is pressed.</summary>
    protected virtual void OnSave() { }

    /// <summary>Standard editor shortcuts: Ctrl+S → OnSave + ClearDirty + status,
    /// Escape → WantsClose. Skipped if a text input is active (so typing Ctrl+S
    /// in a label field doesn't trigger save). Subclasses can call this from
    /// their Draw / Update; for editor-specific shortcuts (Ctrl+C/V, F-key
    /// popups), the subclass adds its own checks.</summary>
    protected void HandleStandardShortcuts()
    {
        if (_ui.IsTextInputActive) return;
        bool ctrl = _ui._kb.IsKeyDown(Keys.LeftControl) || _ui._kb.IsKeyDown(Keys.RightControl);

        if (ctrl && _ui._kb.IsKeyDown(Keys.S) && !_ui._prevKb.IsKeyDown(Keys.S))
        {
            OnSave();
            ClearDirty();
            SetStatus("Saved!");
        }
    }

    /// <summary>Render the top bar — title with optional dirty asterisk, Save
    /// and Close buttons, status message (if active). Returns the height
    /// consumed so the caller can place the rest of the editor below it.</summary>
    protected int DrawTopBar(int x, int y, int w, int height = 50)
    {
        // Top bar background
        _ui.DrawRect(new Rectangle(x, y, w, height), new Color(45, 45, 55));
        _ui.DrawRect(new Rectangle(x, y + height - 1, w, 1), new Color(80, 80, 100));

        // Title (with asterisk when dirty)
        string title = IsDirty ? $"{Title} *" : Title;
        _ui.DrawText(title, new Vector2(x + 20, y + 14), Color.White);

        // Save button (right side)
        int saveW = 80, saveH = 28;
        int saveX = x + w - 200;
        int saveY = y + (height - saveH) / 2;
        if (_ui.DrawButton("Save", saveX, saveY, saveW, saveH))
        {
            OnSave();
            ClearDirty();
            SetStatus("Saved!");
        }

        // Close button [X]
        int closeW = 28, closeH = 28;
        int closeX = x + w - closeW - 10;
        int closeY = y + (height - closeH) / 2;
        if (_ui.DrawButton("X", closeX, closeY, closeW, closeH))
            WantsClose = true;

        // Status message — drawn in the gap between save button and right
        // edge of title area. Color encodes success vs failure.
        if (StatusTimer > 0 && !string.IsNullOrEmpty(StatusMessage))
        {
            float alpha = Math.Min(1f, StatusTimer);
            byte a = (byte)(alpha * 255);
            Color statusColor = StatusMessage.Contains("FAIL")
                ? new Color((byte)255, (byte)80, (byte)80, a)
                : new Color((byte)100, (byte)255, (byte)100, a);
            _ui.DrawText(StatusMessage, new Vector2(x + 20, y + height - 18), statusColor);
        }

        return height;
    }
}
