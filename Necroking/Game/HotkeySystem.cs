using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework.Input;
using Necroking.Core;

namespace Necroking;

/// <summary>Per-modifier requirement for a hotkey. <see cref="Ignore"/> = fire whether
/// or not the modifier is held; <see cref="Down"/> = only while held; <see cref="Up"/> =
/// only while NOT held. Up is what disambiguates "Q" from "Shift+Q": the plain binding
/// sets Shift=Up so the shifted press can't also trigger it.</summary>
public enum ModReq : byte { Ignore, Down, Up }

/// <summary>Game state a hotkey is active in. Each context implies a default dispatch
/// phase (see <see cref="HotkeyPhase"/>) — where in Game1.Update the key is evaluated.</summary>
public enum HotkeyContext
{
    /// <summary>Everywhere, including the main menu (e.g. Alt+Enter). Phase: Always.</summary>
    Global,
    /// <summary>Any in-session state past the full-screen menus — editors and the pause
    /// menu included (e.g. the F-key debug/editor toggles). Phase: PreUI.</summary>
    Session,
    /// <summary>Plain gameplay HUD — no editor or menu open (MenuState.None). Fires even
    /// while paused, so panel toggles keep working during pause. Phase: PreUI.</summary>
    Hud,
    /// <summary>World simulation running this frame (not paused, no world-suspending
    /// editor open). Phase: World.</summary>
    Gameplay,
    /// <summary>The game-over overlay is up. Phase: PostUI.</summary>
    GameOver,
}

/// <summary>Point in Game1.Update where a hotkey is evaluated. Usually derived from the
/// context; pass an explicit phase only for keys that must respect UI-layer key claims —
/// PostUI runs after the UI router dispatch, so a popup's ConsumeKey wins (ESC lives
/// there).</summary>
public enum HotkeyPhase
{
    /// <summary>Right after input capture, before the menu-state early-returns.</summary>
    Always,
    /// <summary>Past the full-screen-menu early-returns, before UI hit-rects are rebuilt
    /// and the UI router runs — a panel opened by key here still lands in this frame's
    /// hit-rect pass.</summary>
    PreUI,
    /// <summary>After the UI router dispatch — popups/panels have already claimed their
    /// keys via ConsumeKey.</summary>
    PostUI,
    /// <summary>Inside the world-running gate, after the channel-hold release check.</summary>
    World,
}

/// <summary>One registered hotkey: the context it works in, a key combo with per-modifier
/// sensitivity, and the delegate to run when it fires.</summary>
public sealed class Hotkey
{
    public string Name = "";
    public HotkeyContext Context;
    public HotkeyPhase Phase;
    public Keys Key;
    public ModReq Shift = ModReq.Ignore;
    public ModReq Ctrl = ModReq.Ignore;
    public ModReq Alt = ModReq.Ignore;
    /// <summary>Extra gate evaluated at press time (e.g. "a spell aim is armed"). A false
    /// here lets a less specific binding on the same key still fire this frame.</summary>
    public Func<bool>? When;
    public Action Action = () => { };
    /// <summary>Consume the key after firing so lower-priority bindings and remaining
    /// WasKeyPressedUnhandled sites don't also react to the same press.</summary>
    public bool ConsumeOnFire = true;

    internal int Seq; // registration order — stable tiebreak for the specificity sort
    internal int Specificity => (Shift != ModReq.Ignore ? 1 : 0)
        + (Ctrl != ModReq.Ignore ? 1 : 0) + (Alt != ModReq.Ignore ? 1 : 0);
}

/// <summary>
/// Central hotkey dispatch — replaces the per-key if-statements that used to be spread
/// through Game1.Update. Register each hotkey once (the game's table lives in
/// Game1.Hotkeys.cs); Game1.Update calls Dispatch once per phase. Rules, applied
/// centrally instead of at every site:
/// - Text-input gate: nothing fires while an editor text field / combo filter owns the
///   keyboard (the old per-site anyTextInputActive guard, now in one place).
/// - Consumption: presses claimed by a higher UI layer (popup ESC) are skipped
///   (WasKeyPressedUnhandled), and a fired hotkey consumes its key.
/// - Most-specific-wins: on the same key, bindings with more modifier requirements are
///   tried first (Shift+Q beats Q), registration order breaking ties.
/// Editor windows (EditorBase/MapEditorWindow widget keys) are NOT migrated —
/// see todos/migrate-editor-hotkeys.md.
/// </summary>
public static class HotkeySystem
{
    private static readonly List<Hotkey> _hotkeys = new();
    private static bool _sorted;

    public static Hotkey Register(HotkeyContext context, Keys key, Action action,
        ModReq shift = ModReq.Ignore, ModReq ctrl = ModReq.Ignore, ModReq alt = ModReq.Ignore,
        Func<bool>? when = null, string name = "", HotkeyPhase? phase = null)
    {
        var hk = new Hotkey
        {
            Name = name,
            Context = context,
            Key = key,
            Action = action,
            Shift = shift,
            Ctrl = ctrl,
            Alt = alt,
            When = when,
            Phase = phase ?? DefaultPhase(context),
            Seq = _hotkeys.Count,
        };
        _hotkeys.Add(hk);
        _sorted = false;
        return hk;
    }

    public static void Clear() { _hotkeys.Clear(); _sorted = true; }

    private static HotkeyPhase DefaultPhase(HotkeyContext c) => c switch
    {
        HotkeyContext.Global => HotkeyPhase.Always,
        HotkeyContext.Gameplay => HotkeyPhase.World,
        HotkeyContext.GameOver => HotkeyPhase.PostUI,
        _ => HotkeyPhase.PreUI, // Session, Hud
    };

    /// <summary>Run all hotkeys of one phase against this frame's input. Call sites live
    /// in Game1.Update, one per phase, at the point in the frame the phase names.</summary>
    public static void Dispatch(HotkeyPhase phase, InputState input)
    {
        var g = Game1.Instance;
        if (g == null || TextInputActive(g)) return;
        if (!_sorted)
        {
            // Most-specific-first; only per-key order matters (a fired hotkey consumes
            // its key, so the first match wins), a global sort is just the simplest way
            // to get it.
            _hotkeys.Sort((a, b) => b.Specificity != a.Specificity
                ? b.Specificity - a.Specificity : a.Seq - b.Seq);
            _sorted = true;
        }
        foreach (var hk in _hotkeys)
        {
            if (hk.Phase != phase) continue;
            if (!input.WasKeyPressedUnhandled(hk.Key)) continue;
            if (!ModMatches(hk.Shift, input, Keys.LeftShift, Keys.RightShift)) continue;
            if (!ModMatches(hk.Ctrl, input, Keys.LeftControl, Keys.RightControl)) continue;
            if (!ModMatches(hk.Alt, input, Keys.LeftAlt, Keys.RightAlt)) continue;
            if (!ContextActive(g, hk.Context)) continue;
            if (hk.When != null && !hk.When()) continue;
            hk.Action();
            if (hk.ConsumeOnFire) input.ConsumeKey(hk.Key);
        }
    }

    private static bool ModMatches(ModReq req, InputState input, Keys left, Keys right)
        => req == ModReq.Ignore
        || (req == ModReq.Down) == (input.IsKeyDown(left) || input.IsKeyDown(right));

    private static bool ContextActive(Game1 g, HotkeyContext c) => c switch
    {
        HotkeyContext.Global => true,
        // Session is structurally guaranteed by its phase: the PreUI/PostUI dispatch
        // calls only run past the main-menu/scenario-list early-returns.
        HotkeyContext.Session => true,
        HotkeyContext.Hud => g._menuState == MenuState.None,
        HotkeyContext.Gameplay => g._clock.WorldRunning,
        HotkeyContext.GameOver => g._gameOver,
        _ => false,
    };

    /// <summary>The one central text-input gate (was the per-site anyTextInputActive
    /// guard): typing into an editor text field / combo filter must not trip hotkeys.</summary>
    private static bool TextInputActive(Game1 g)
        => (g._editorUi != null && g._editorUi.IsKeyboardCaptured)
        || (g._menuState == MenuState.UIEditor && g._uiEditor.IsKeyboardCaptured);
}
