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
/// The map editor as its own NON-BLOCKING router layer — unlike the other
/// editors it is panel-like: its interactive surface is the right side panel
/// (plus the Zones tab's left village panel), while the world stays visible
/// and paintable underneath. Its footprint in the hit registry is that panel
/// rect, not a fullscreen blanket, so MouseOverUI / hover ownership over the
/// world area behave like they do next to any docked panel. Painting itself
/// stays immediate-mode inside MapEditorWindow (raw mouse during Draw), gated
/// by OverGameplayHud + its own popup checks as before.
/// </summary>
public sealed class MapEditorLayer : UILayer
{
    private readonly Game1 _g;
    private readonly PopupManager _popups;
    public MapEditorLayer(Game1 g, PopupManager popups)
    {
        _g = g;
        _popups = popups;
        Band = UIBand.Editor;
    }

    public override string Id => "editor.map";
    public override bool Visible => _g._menuState == MenuState.MapEditor && _g._gameWorldLoaded;
    public override bool Closable => true;

    public override void OnCancel()
    {
        _g._editorUi?.ResetAllState();
        _g._menuState = MenuState.None;
    }

    public override bool ContainsMouse(int mx, int my, in UICtx ctx)
        => _g._mapEditor.IsPanelAt(mx, my, ctx.ScreenW, ctx.ScreenH);

    protected override void OnFrame(InputState input, in UICtx ctx)
    {
        // Camera zoom over the world area (moved out of Game1.Update's special
        // block): only while no sub-popup sits above the editor — a texture
        // browser / color picker / env editor owns the wheel then — and the
        // cursor is off the side panel (the panel's own per-tab scroll reads
        // raw mouse in the immediate-mode pass).
        if (input.ScrollDelta != 0 && !input.IsScrollConsumed
            && _popups.IsEmpty
            && !ContainsMouse((int)input.MousePos.X, (int)input.MousePos.Y, in ctx))
        {
            _g._camera.ZoomBy(input.ScrollDelta / 120f);
            input.ConsumeScroll();
        }
    }

    public override void Draw(in UICtx ctx)
        => _g._mapEditor.Draw(ctx.ScreenW, ctx.ScreenH);
}

/// <summary>
/// The UI editor as its own router layer. Unlike the map editor it IS modal —
/// a full-screen dim with a large centered window — so it stays BLOCKING, but
/// its ContainsMouse is the actual window rect (via
/// <see cref="Editor.UIEditorWindow.IsPanelAt"/>) so hover ownership over the
/// 30px dim margin is "blocked", not "inside the editor". Editor internals
/// (its own EditorBase input, immediate-mode draw) are untouched.
/// </summary>
public sealed class UIEditorLayer : UILayer
{
    private readonly Game1 _g;
    public UIEditorLayer(Game1 g) { _g = g; Band = UIBand.Editor; }

    public override string Id => "editor.ui";
    public override bool Visible => _g._menuState == MenuState.UIEditor;
    public override bool Blocking => true;
    public override bool Closable => true;

    public override bool ContainsMouse(int mx, int my, in UICtx ctx)
        => _g._uiEditor.IsPanelAt(mx, my, ctx.ScreenW, ctx.ScreenH);

    public override void OnCancel()
    {
        // The UI editor IS its own EditorBase instance (_uiEditor, not
        // _editorUi) — resetting the wrong one left stale focus across
        // close/reopen (the original per-layer OnCancelAction comment).
        _g._uiEditor?.ResetAllState();
        _g._menuState = MenuState.None;
    }

    public override void Draw(in UICtx ctx)
    {
        var gt = ctx.GameTime;
        if (gt != null) _g._uiEditor.Draw(ctx.ScreenW, ctx.ScreenH, gt);
        else _g._uiEditor.Draw(ctx.ScreenW, ctx.ScreenH);
    }
}

/// <summary>
/// The remaining full-screen editors (unit/spell/item) as ONE opaque blocking
/// layer. Editor internals (immediate-mode input during Draw, EditorBase
/// machinery) are untouched — this seat only provides the blocking blanket,
/// ESC-to-close, and the z-position above menus and below editor sub-popups.
/// Replaces the per-editor ActionModalLayers + ReconcileTopLevelEditorLayers.
/// The map editor (<see cref="MapEditorLayer"/>) and UI editor
/// (<see cref="UIEditorLayer"/>) have their own seats.
/// </summary>
public sealed class EditorHostLayer : UILayer
{
    private readonly Game1 _g;
    public EditorHostLayer(Game1 g) { _g = g; Band = UIBand.Editor; }

    public override string Id => "editor.host";
    public override bool Visible => _g.EditorActive
        && _g._menuState != MenuState.MapEditor
        && _g._menuState != MenuState.UIEditor;
    public override bool Blocking => true;
    public override bool Closable => true;
    public override bool ContainsMouse(int mx, int my, in UICtx ctx) => true;

    public override void OnCancel()
    {
        // Same cleanup the old per-editor OnCancelActions did.
        _g._editorUi?.ResetAllState();
        _g._menuState = MenuState.None;
    }

    public override void Draw(in UICtx ctx)
    {
        var gt = ctx.GameTime;
        switch (_g._menuState)
        {
            case MenuState.UnitEditor:
                if (gt == null) break;
                _g._unitEditor.Draw(ctx.ScreenW, ctx.ScreenH, gt);
                // Close request from the editor's [X] button.
                if (_g._unitEditor.WantsClose)
                {
                    _g._unitEditor.WantsClose = false;
                    _g._editorUi.ResetAllState();
                    _g._menuState = MenuState.None;
                }
                break;
            case MenuState.SpellEditor:
                if (gt == null) break;
                _g._spellEditor.Draw(ctx.ScreenW, ctx.ScreenH, gt);
                if (_g._spellEditor.WantsClose)
                {
                    _g._spellEditor.WantsClose = false;
                    _g._editorUi.ResetAllState();
                    _g._menuState = MenuState.None;
                }
                break;
            case MenuState.ItemEditor:
                if (gt == null) break;
                _g._itemEditor.Draw(ctx.ScreenW, ctx.ScreenH, gt);
                if (_g._itemEditor.WantsClose)
                {
                    _g._itemEditor.WantsClose = false;
                    _g._editorUi.ResetAllState();
                    _g._menuState = MenuState.None;
                }
                break;
        }
    }
}

/// <summary>
/// The pause-menu family (pause / settings / multiplayer) as one blocking
/// layer. ESC walks back the way the old ActionModalLayers did: settings and
/// multiplayer return to the pause menu; the pause menu unpauses to gameplay.
/// Button clicks are still handled from Game1.Update (PauseMenuScreen
/// .HandleClick) — this seat provides the blocking blanket + ESC routing.
/// </summary>
public sealed class MenuHostLayer : UILayer
{
    private readonly Game1 _g;
    public MenuHostLayer(Game1 g) { _g = g; Band = UIBand.Menu; }

    public override string Id => "menu.host";
    public override bool Visible => _g._menuState == MenuState.PauseMenu
        || _g._menuState == MenuState.Settings
        || _g._menuState == MenuState.Multiplayer
        || _g._menuState == MenuState.SaveMenu;
    public override bool Blocking => true;
    public override bool Closable => true;
    public override bool ContainsMouse(int mx, int my, in UICtx ctx) => true;

    public override void OnCancel()
    {
        switch (_g._menuState)
        {
            case MenuState.Settings:
                // Reachable from both root menus — return to whichever opened it.
                _g._editorUi?.ResetAllState();
                _g._menuState = _g._backMenuState;
                break;
            case MenuState.Multiplayer:
            case MenuState.SaveMenu:
                _g._editorUi?.ResetAllState();
                _g._menuState = MenuState.PauseMenu;
                break;
            case MenuState.PauseMenu:
                _g._menuState = MenuState.None;
                _g._clock.ClearAllPauses();
                break;
        }
    }

    // The game-over overlay draws from this seat too (same screen position in
    // the old DrawHudBlock order); it never takes router input — its only
    // control is the R-restart key handled in Game1.Update.
    public override bool VisibleForDraw => Visible || (_g._gameOver && _g.ShowUIForDraw);

    public override void Draw(in UICtx ctx)
    {
        if (_g._gameOver && _g.ShowUIForDraw)
        {
            _g._gameRenderer.DrawGameOver(ctx.ScreenW, ctx.ScreenH);
            return;
        }
        switch (_g._menuState)
        {
            case MenuState.PauseMenu:
                _g._pauseMenu.Draw(ctx.ScreenW, ctx.ScreenH);
                break;
            case MenuState.Settings:
                // Opened from the main menu there's no world behind the window —
                // draw the menu backdrop instead of the empty dark world.
                if (!_g._gameWorldLoaded) MenuDraw.Backdrop(ctx.ScreenW, ctx.ScreenH);
                _g._settingsWindow.Draw(ctx.ScreenW, ctx.ScreenH);
                break;
            case MenuState.Multiplayer:
                _g._multiplayerWindow.Draw(ctx.ScreenW, ctx.ScreenH);
                break;
            case MenuState.SaveMenu:
                _g._saveGameWindow.Draw(ctx.ScreenW, ctx.ScreenH);
                break;
        }
    }
}
