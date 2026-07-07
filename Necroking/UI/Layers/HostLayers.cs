using Necroking.Core;

namespace Necroking.UI;

/// <summary>
/// Router seat for the editor-internal modal stack (<see cref="PopupManager"/>).
/// The full-screen editors keep using Game1.Popups for their transient
/// sub-popups — dropdowns, text fields, confirm dialogs, color picker, texture
/// browser, env/wall editors — because those are immediate-mode components that
/// push/pop dynamically from editor code. This layer gives that whole stack one
/// slot at the top of the layer order and forwards input to the classic
/// top-of-stack routing. Game panels do NOT go through here anymore; they are
/// individual <see cref="PanelLayer"/>s.
/// </summary>
public sealed class ModalStackLayer : UILayer
{
    private readonly PopupManager _popups;
    public ModalStackLayer(PopupManager popups) { _popups = popups; Band = UIBand.Popup; }

    public override string Id => "popup.stack";
    public override bool Visible => !_popups.IsEmpty;

    public override bool ContainsMouse(int mx, int my, in UICtx ctx)
        => _popups.Top?.ContainsMouse(mx, my) ?? false;

    // The stack keeps its historical semantics wholesale (top layer decides,
    // ESC → CancelTop) — RouteInput consumes for itself, so lower router
    // layers see the leftovers exactly like every other layer's template.
    public override void HandleInput(InputState input, in UICtx ctx)
        => _popups.RouteInput(input);

    public override void AppendHitRects(UIHitRegistry reg, in UICtx ctx)
        => _popups.AppendHitRects(reg, ctx.ScreenW, ctx.ScreenH);
}

/// <summary>
/// The full-screen editors (unit/spell/map/UI/item) as ONE opaque blocking
/// layer. Editor internals (immediate-mode input during Draw, EditorBase
/// machinery) are untouched — this seat only provides the blocking blanket,
/// ESC-to-close, and the z-position above menus and below editor sub-popups.
/// Replaces the five per-editor ActionModalLayers + ReconcileTopLevelEditorLayers.
/// </summary>
public sealed class EditorHostLayer : UILayer
{
    private readonly Game1 _g;
    public EditorHostLayer(Game1 g) { _g = g; Band = UIBand.Editor; }

    public override string Id => "editor.host";
    public override bool Visible => _g.EditorActive;
    public override bool Blocking => true;
    public override bool Closable => true;
    public override bool ContainsMouse(int mx, int my, in UICtx ctx) => true;

    public override void OnCancel()
    {
        // Same cleanup the old per-editor OnCancelActions did: reset the
        // owning EditorBase (the UI editor IS its own EditorBase instance).
        if (_g._menuState == MenuState.UIEditor) _g._uiEditor?.ResetAllState();
        else _g._editorUi?.ResetAllState();
        _g._menuState = MenuState.None;
    }
}

/// <summary>
/// The pause-menu family (pause / settings / multiplayer) as one blocking
/// layer. ESC walks back the way the old ActionModalLayers did: settings and
/// multiplayer return to the pause menu; the pause menu unpauses to gameplay.
/// Button clicks are still handled by the existing raw-mouse blocks in
/// Game1.Update — this seat provides the blocking blanket + ESC routing.
/// </summary>
public sealed class MenuHostLayer : UILayer
{
    private readonly Game1 _g;
    public MenuHostLayer(Game1 g) { _g = g; Band = UIBand.Menu; }

    public override string Id => "menu.host";
    public override bool Visible => _g._menuState == MenuState.PauseMenu
        || _g._menuState == MenuState.Settings
        || _g._menuState == MenuState.Multiplayer;
    public override bool Blocking => true;
    public override bool Closable => true;
    public override bool ContainsMouse(int mx, int my, in UICtx ctx) => true;

    public override void OnCancel()
    {
        switch (_g._menuState)
        {
            case MenuState.Settings:
            case MenuState.Multiplayer:
                _g._editorUi?.ResetAllState();
                _g._menuState = MenuState.PauseMenu;
                break;
            case MenuState.PauseMenu:
                _g._menuState = MenuState.None;
                _g._clock.ClearAllPauses();
                break;
        }
    }
}
