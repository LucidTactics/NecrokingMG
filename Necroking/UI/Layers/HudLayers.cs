using Necroking.Core;

namespace Necroking.UI;

// The gameplay HUD's clickable widgets as UIRouter layers — thin wrappers over
// the existing HUDRenderer/GameRenderer layout + hit-test code (single source
// of truth stays there; these classes only route). Each replaces an ad-hoc
// `if (_input.LeftPressed …)` block that used to live in Game1.Update at a
// position unrelated to draw order.
//
// Hit-rect note: these HUD layers deliberately do NOT self-append to the
// UIHitRegistry — Game1.RebuildUIHitRects still catalogues the HUD through
// HUDRenderer.AppendHitRects (+ toast/aggro extras) under the fine-grained
// per-button ids ("hud.menu_row.2") the ui_rects dev command and the map
// editor's OverGameplayHud prefix checks rely on.

/// <summary>The world floor — the layer everything else sits above. Owns world
/// clicks (Game1.WorldClicks.cs dispatch) and camera scroll-zoom. Overrides the
/// standard template: it has no "surface" (it IS everywhere), never blanket-
/// consumes, and its handlers consume per-branch like they always did.</summary>
public sealed class WorldInputLayer : UILayer
{
    private readonly Game1 _g;
    public WorldInputLayer(Game1 g) { _g = g; Band = UIBand.World; }

    public override string Id => "world";
    public override bool Visible => _g._gameWorldLoaded && _g._menuState == MenuState.None;
    public override bool ContainsMouse(int mx, int my, in UICtx ctx) => true;

    // The world never registers a hit rect — MouseOverUI means "over something
    // that ISN'T the world".
    public override void AppendHitRects(UIHitRegistry reg, in UICtx ctx) { }

    public override void HandleInput(InputState input, in UICtx ctx)
    {
        // World clicks only while gameplay runs (paused world takes no orders);
        // per-branch ConsumeMouse + the !MouseOverUI gate live in the dispatch.
        if ((input.LeftPressed || input.RightPressed) && !input.IsMouseConsumed
            && _g._clock.WorldRunning)
        {
            var mouseWorld = _g._camera.ScreenToWorld(input.MousePos, ctx.ScreenW, ctx.ScreenH);
            _g.HandleWorldClicks(ctx.ScreenW, ctx.ScreenH,
                (int)input.MousePos.X, (int)input.MousePos.Y,
                mouseWorld, _g.FindNecromancer());
        }

        // Scroll-zoom: cursor over open sky (not any UI rect). Works while
        // paused, matching the old block outside the WorldRunning gate.
        if (input.ScrollDelta != 0 && !input.IsScrollConsumed && !input.MouseOverUI)
        {
            _g._camera.ZoomBy(input.ScrollDelta / 120f);
            input.ConsumeScroll();
        }
    }
}

/// <summary>Draw-only seat for the persistent HUD chrome (status bars, spell
/// bar, time controls, cursor tooltips, horde caps, combat log, world hover
/// info) at the bottom of the Hud band. Takes no input — the interactive HUD
/// widgets have their own layers. While a layer above the Hud band owns the
/// cursor, the shared cursor position is parked off-screen for the duration of
/// the draw so HUD-internal hover effects (spell-slot tooltip, hover-anchored
/// tooltips) can't light up under a covering panel.</summary>
public sealed class HudChromeLayer : UILayer
{
    private readonly Game1 _g;
    public HudChromeLayer(Game1 g) { _g = g; Band = UIBand.Hud; }

    public override string Id => "hud.chrome";
    public override bool Visible => false;                    // draw-only
    public override bool VisibleForDraw => _g.ShowUIForDraw;  // same gate the old inline DrawHUD call had
    public override bool ContainsMouse(int mx, int my, in UICtx ctx) => false;
    public override void AppendHitRects(UIHitRegistry reg, in UICtx ctx) { }

    public override void Draw(in UICtx ctx)
    {
        var hover = _g._uiRouter.HoverLayer;
        bool hudOwnsCursor = hover == null || hover.Band <= UIBand.Hud;
        var input = _g._input;
        var pos = input.MousePos;
        if (!hudOwnsCursor) input.MousePos = new Microsoft.Xna.Framework.Vector2(-10000, -10000);
        _g._gameRenderer.DrawHUD(ctx.ScreenW, ctx.ScreenH);
        _g._gameRenderer.DrawWorldHoverInfo(ctx.ScreenW, ctx.ScreenH);
        if (!hudOwnsCursor) input.MousePos = pos;
    }
}

/// <summary>Spell-bar slots: clicking a slot opens the grimoire in assign mode
/// for it. (Casting itself is keyboard-only — see SpellBarBindings.)</summary>
public sealed class SpellBarLayer : UILayer
{
    private readonly Game1 _g;
    public SpellBarLayer(Game1 g) { _g = g; Band = UIBand.Hud; }

    public override string Id => "hud.spell_bar";
    public override void AppendHitRects(UIHitRegistry reg, in UICtx ctx) { } // HUD rects catalogued by HUDRenderer.AppendHitRects (see file header)
    public override bool Visible => _g.HudVisible && _g._menuState == MenuState.None;

    private int HitSlot(int mx, int my, in UICtx ctx)
    {
        var bar = _g._hudRenderer.GetSpellBarLayout(ctx.ScreenH);
        return _g._hudRenderer.HitTestBarSlot(ctx.ScreenW,
            bar.barY, bar.slotW, bar.slotH, bar.centerOffset, mx, my);
    }

    public override bool ContainsMouse(int mx, int my, in UICtx ctx)
        => HitSlot(mx, my, in ctx) >= 0;

    protected override void OnPointer(InputState input, in UICtx ctx)
    {
        if (!input.LeftPressed) return;
        int slot = HitSlot((int)input.MousePos.X, (int)input.MousePos.Y, in ctx);
        if (slot >= 0) _g.OpenSpellAssignForSlot(slot);
    }
}

/// <summary>Time-control strip (pause + speed presets). Deliberately NOT gated
/// on WorldRunning: the old inline check lived inside the `if (WorldRunning)`
/// block, which made the pause button one-way (once paused the handler never
/// ran again to unpause).</summary>
public sealed class TimeControlsLayer : UILayer
{
    private readonly Game1 _g;
    public TimeControlsLayer(Game1 g) { _g = g; Band = UIBand.Hud; }

    public override string Id => "hud.time_controls";
    public override void AppendHitRects(UIHitRegistry reg, in UICtx ctx) { } // HUD rects catalogued by HUDRenderer.AppendHitRects (see file header)
    public override bool Visible => _g.HudVisible && _g._menuState == MenuState.None
        && _g._gameData.Settings.General.ShowTimeControls;

    public override bool ContainsMouse(int mx, int my, in UICtx ctx)
        => _g._hudRenderer.HitTestTimeControls(ctx.ScreenW, ctx.ScreenH, mx, my) != -1;

    protected override void OnPointer(InputState input, in UICtx ctx)
    {
        if (!input.LeftPressed) return;
        int hit = _g._hudRenderer.HitTestTimeControls(ctx.ScreenW, ctx.ScreenH,
            (int)input.MousePos.X, (int)input.MousePos.Y);
        if (hit == -2)
        {
            _g._clock.TogglePause(GameClock.PauseSource.User);
        }
        else if (hit >= 0)
        {
            _g._timeScale = HUDRenderer.TimeControlSpeeds[hit];
            _g._clock.ClearAllPauses();
        }
    }
}

/// <summary>Aggression bar: a click snaps the horde aggression to the nearest
/// node (same control as Shift+Q / Shift+E). Hover slack matches the old
/// inflated registry rect.</summary>
public sealed class AggressionBarLayer : UILayer
{
    private readonly Game1 _g;
    public AggressionBarLayer(Game1 g) { _g = g; Band = UIBand.Hud; }

    public override string Id => "hud.aggression_bar";
    public override void AppendHitRects(UIHitRegistry reg, in UICtx ctx) { } // HUD rects catalogued by HUDRenderer.AppendHitRects (see file header)
    public override bool Visible => _g.HudVisible && _g._menuState == MenuState.None;

    public override bool ContainsMouse(int mx, int my, in UICtx ctx)
    {
        if (!_g._gameRenderer.GetAggressionBarLayout(ctx.ScreenW, ctx.ScreenH, out var bar, out _))
            return false;
        bar.Inflate(0, 8); // same vertical slack as the hover tooltip
        return bar.Contains(mx, my);
    }

    protected override void OnPointer(InputState input, in UICtx ctx)
    {
        if (!input.LeftPressed) return;
        if (!_g._gameRenderer.GetAggressionBarLayout(ctx.ScreenW, ctx.ScreenH, out _, out var nodes))
            return;
        _g._sim.Horde.AggressionLevel = GameRenderer.NearestAggroNode(nodes, (int)input.MousePos.X);
    }

    public override bool VisibleForDraw => _g.ShowUIForDraw; // DrawAggressionBar self-gates on MenuState

    public override void Draw(in UICtx ctx)
    {
        // Hover tooltip inside DrawAggressionBar reads the shared cursor — park
        // it off-screen unless this layer owns the hover, so the tooltip can't
        // appear under a covering panel.
        var input = _g._input;
        var pos = input.MousePos;
        if (!IsHovered) input.MousePos = new Microsoft.Xna.Framework.Vector2(-10000, -10000);
        _g._gameRenderer.DrawAggressionBar(ctx.ScreenW, ctx.ScreenH);
        if (!IsHovered) input.MousePos = pos;
    }
}

/// <summary>Left-side debug settings panel (under the player-data status bars):
/// a dropdown per debug-mode F-key (F2/F3/F5/F6/F7/F8), the click twin of those
/// toggles. Draw + hit-test + open-state live in UI/DebugSettingsPanel.cs; this
/// layer only routes, matching the other thin HUD layers. Dropdowns are eager
/// (press→drag→release selects), so presses route via OnPointer and the
/// release-select + outside-press dismiss are polled in OnFrame.</summary>
public sealed class DebugSettingsPanelLayer : UILayer
{
    private readonly Game1 _g;
    public DebugSettingsPanelLayer(Game1 g) { _g = g; Band = UIBand.Hud; }

    public override string Id => "hud.debug_panel";
    public override void AppendHitRects(UIHitRegistry reg, in UICtx ctx) { }
    public override bool Visible => _g._debugPanel.IsVisible && _g.HudVisible && _g._menuState == MenuState.None;

    public override bool ContainsMouse(int mx, int my, in UICtx ctx)
        => _g._debugPanel.ContainsMouse(mx, my);

    protected override void OnPointer(InputState input, in UICtx ctx)
    {
        if (!input.LeftPressed) return;
        _g._debugPanel.HandlePress((int)input.MousePos.X, (int)input.MousePos.Y);
    }

    protected override void OnFrame(InputState input, in UICtx ctx)
        => _g._debugPanel.HandleFrame(input);

    public override bool VisibleForDraw => _g._debugPanel.IsVisible && _g.ShowUIForDraw && _g._menuState == MenuState.None;

    public override void Draw(in UICtx ctx)
    {
        var input = _g._input;
        // Hover highlights only when this layer owns the cursor.
        int mx = IsHovered ? (int)input.MousePos.X : -10000;
        int my = IsHovered ? (int)input.MousePos.Y : -10000;
        _g._debugPanel.Draw(mx, my);
    }
}

/// <summary>The one topmost tooltip seat (Tooltip band). Two jobs each frame:
/// run the HUD cursor-tooltip funnel (spell-bar slot, world object, belly,
/// corpse, unit — hover state captured during the Hud-band draw, fresh because
/// bands draw bottom-up), then drain the global <see cref="Game1.Tooltips"/>
/// request queue that any layer (panels, editors — even inside a scissor clip)
/// filled during its own Update/Draw. Being last in the band order is what
/// makes every tooltip render unclipped and over everything (the aggression
/// bar used to draw over the spell-slot tooltip when both sat in the Hud band).
/// Same cursor parking as HudChromeLayer: cursor-anchored tooltips must not
/// pop up under (or over) a covering panel.</summary>
public sealed class TooltipHostLayer : UILayer
{
    private readonly Game1 _g;
    public TooltipHostLayer(Game1 g) { _g = g; Band = UIBand.Tooltip; }

    public override string Id => "tooltip.host";
    public override bool Visible => false;   // draw-only
    // Always drawn (unlike HudChromeLayer's ShowUIForDraw gate): the global
    // queue must be drained-or-cleared EVERY frame or requests would go stale
    // and bleed into later frames / no-UI screenshots.
    public override bool VisibleForDraw => true;
    public override bool ContainsMouse(int mx, int my, in UICtx ctx) => false;
    public override void AppendHitRects(UIHitRegistry reg, in UICtx ctx) { }

    public override void Draw(in UICtx ctx)
    {
        if (!_g.ShowUIForDraw) { Game1.Tooltips.Clear(); return; }

        var hover = _g._uiRouter.HoverLayer;
        bool hudOwnsCursor = hover == null || hover.Band <= UIBand.Hud;
        var input = _g._input;
        var pos = input.MousePos;
        if (!hudOwnsCursor) input.MousePos = new Microsoft.Xna.Framework.Vector2(-10000, -10000);
        _g._gameRenderer.DrawHudTooltips(ctx.ScreenW, ctx.ScreenH);
        if (!hudOwnsCursor) input.MousePos = pos;

        // Drain the global queue last — later requests draw on top.
        Game1.Tooltips.DrawAndClear(in ctx);
    }
}

/// <summary>Top-right core-menu button row (inventory/crafting/building/
/// grimoire/skills/character). Lives in the HudTop band — above panels and
/// blocking overlays like the skill book, below toasts — which is the
/// declarative version of the old "redraw the buttons inside
/// SkillBookOverlay.Draw and re-grant the click" workaround.</summary>
public sealed class CoreMenuButtonsLayer : UILayer
{
    private readonly Game1 _g;
    public CoreMenuButtonsLayer(Game1 g) { _g = g; Band = UIBand.HudTop; }

    public override string Id => "hud.menu_row";
    public override void AppendHitRects(UIHitRegistry reg, in UICtx ctx) { } // HUD rects catalogued by HUDRenderer.AppendHitRects (see file header)
    // Input only during normal play: the row also DRAWS over the map editor
    // (VisibleForDraw), but opening gameplay panels from inside the editor
    // would layer them under the editor's overlays — keep it inert there, as
    // it has always been.
    public override bool Visible => _g.HudVisible && _g._menuState == MenuState.None;

    public override bool ContainsMouse(int mx, int my, in UICtx ctx)
        => _g._hudRenderer.HitTestMenuButtons(ctx.ScreenW, mx, my) >= 0;

    protected override void OnPointer(InputState input, in UICtx ctx)
    {
        if (!input.LeftPressed) return;
        int hit = _g._hudRenderer.HitTestMenuButtons(ctx.ScreenW,
            (int)input.MousePos.X, (int)input.MousePos.Y);
        if (hit >= 0) _g._gameRenderer.ToggleCoreMenu(hit, ctx.ScreenW, ctx.ScreenH);
    }

    public override bool VisibleForDraw => _g.ShowUIForDraw;

    public override void Draw(in UICtx ctx)
    {
        // Hover highlight only when this row owns the cursor — a button never
        // lights up when a click would land in a layer covering it.
        int mx = IsHovered ? (int)_g._input.MousePos.X : -10000;
        int my = IsHovered ? (int)_g._input.MousePos.Y : -10000;
        _g._gameRenderer.DrawMenuButtonsRow(ctx.ScreenW, mx, my);
    }
}

/// <summary>Top-right editor-launcher row — the click mirror of F9-F12.</summary>
public sealed class EditorLauncherLayer : UILayer
{
    private readonly Game1 _g;
    public EditorLauncherLayer(Game1 g) { _g = g; Band = UIBand.HudTop; }

    public override string Id => "hud.editor_row";
    public override void AppendHitRects(UIHitRegistry reg, in UICtx ctx) { } // HUD rects catalogued by HUDRenderer.AppendHitRects (see file header)
    public override bool Visible => _g.HudVisible;

    public override bool ContainsMouse(int mx, int my, in UICtx ctx)
        => _g._hudRenderer.HitTestEditorButtons(ctx.ScreenW, mx, my) >= 0;

    protected override void OnPointer(InputState input, in UICtx ctx)
    {
        if (!input.LeftPressed || _g.AnyTextInputActive) return;
        int hit = _g._hudRenderer.HitTestEditorButtons(ctx.ScreenW,
            (int)input.MousePos.X, (int)input.MousePos.Y);
        if (hit >= 0) _g.ToggleEditorWindow(hit);
    }

    public override bool VisibleForDraw => _g.ShowUIForDraw;

    public override void Draw(in UICtx ctx)
    {
        int mx = IsHovered ? (int)_g._input.MousePos.X : -10000;
        int my = IsHovered ? (int)_g._input.MousePos.Y : -10000;
        _g._gameRenderer.DrawEditorButtonsRow(ctx.ScreenW, mx, my);
    }
}

/// <summary>Bottom-right skill-learn toasts — clickable to jump to the relevant
/// skill-book tab. Toast band: above HudTop, so a toast overlapping the button
/// rows wins the click, matching its draw position.</summary>
public sealed class SkillToastLayer : UILayer
{
    private readonly Game1 _g;
    public SkillToastLayer(Game1 g) { _g = g; Band = UIBand.Toast; }

    public override string Id => "toast.skill_learn";
    public override void AppendHitRects(UIHitRegistry reg, in UICtx ctx) { } // HUD rects catalogued by HUDRenderer.AppendHitRects (see file header)
    public override bool Visible => _g.HudVisible && _g._menuState == MenuState.None
        && _g._skillLearnToasts.Count > 0;

    public override bool ContainsMouse(int mx, int my, in UICtx ctx)
        => _g._gameRenderer.SkillToastIndexAt(ctx.ScreenW, ctx.ScreenH, mx, my) >= 0;

    protected override void OnPointer(InputState input, in UICtx ctx)
    {
        if (!input.LeftPressed) return;
        int idx = _g._gameRenderer.SkillToastIndexAt(ctx.ScreenW, ctx.ScreenH,
            (int)input.MousePos.X, (int)input.MousePos.Y);
        if (idx >= 0) _g._gameRenderer.ActivateSkillToast(idx);
    }

    public override bool VisibleForDraw => _g.ShowUIForDraw; // DrawSkillLearnToasts self-guards on count

    public override void Draw(in UICtx ctx)
        => _g._gameRenderer.DrawSkillLearnToasts(ctx.ScreenW, ctx.ScreenH);
}
