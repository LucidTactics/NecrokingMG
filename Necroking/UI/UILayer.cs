using Microsoft.Xna.Framework.Input;
using Necroking.Core;

namespace Necroking.UI;

/// <summary>
/// Z-band a <see cref="UILayer"/> lives in. One list orders BOTH input and
/// drawing: input dispatches top-down (descending band), drawing iterates
/// bottom-up (ascending band) — so "drawn on top" and "clicked first" can
/// never disagree. Moving a widget above/below something else is a band
/// change here, not a moved draw call plus a duplicated hit-test.
/// Gaps between values are deliberate room for future bands.
/// </summary>
public enum UIBand
{
    World   = 0,    // world clicks + camera scroll-zoom — the floor, nothing below
    Hud     = 200,  // spell bar, time controls, aggression bar
    Panels  = 300,  // inventory, job board, grave roster, crafting, grimoire, stats…
    Overlay = 400,  // blocking full overlays (skill book)
    HudTop  = 450,  // core-menu + editor-launcher rows — above panels/overlays, below toasts
    Toast   = 500,  // skill-learn corner toasts
    Menu    = 600,  // game-over, pause menu, settings, multiplayer
    Editor  = 700,  // full-screen editors (one opaque blocking layer)
    Popup   = 800,  // transient popups: dropdowns, color picker, file browser
    Tooltip = 900,  // draw-only: cursor tooltips, ghost previews — never takes input
}

/// <summary>Per-dispatch frame context handed to every layer — the screen size
/// and clock values the wrapped draw/hit-test code needs, so layers don't
/// reach for GraphicsDevice or GameTime themselves.</summary>
public readonly struct UICtx
{
    public readonly int ScreenW;
    public readonly int ScreenH;
    public readonly double TimeSec;
    /// <summary>Frame GameTime — populated for the draw pass (editor Draw calls
    /// need it); may be null on input-only or hit-rect dispatches.</summary>
    public readonly Microsoft.Xna.Framework.GameTime? GameTime;

    public UICtx(int screenW, int screenH, double timeSec,
        Microsoft.Xna.Framework.GameTime? gameTime = null)
    {
        ScreenW = screenW;
        ScreenH = screenH;
        TimeSec = timeSec;
        GameTime = gameTime;
    }
}

/// <summary>
/// One entry in the <see cref="UIRouter"/>'s z-ordered list — a UI surface that
/// can take input and (from the draw-unification phase on) draw itself.
///
/// Input contract: the router walks layers top-down and calls
/// <see cref="HandleInput"/> on each VISIBLE layer. Nothing is pre-consumed on
/// a layer's behalf: when its turn comes, the input state holds exactly what
/// the layers ABOVE it left over, and the standard template consumes what the
/// layer uses (inside-click, light-dismiss, blocking blanket). A layer's
/// <see cref="OnPointer"/> therefore only ever fires with a click that is
/// genuinely its to handle — no "was it consumed for me?" re-checking, which
/// is what retired the old ConsumeMouse(consumer)/MouseConsumer re-grant hack.
///
/// Sub-menus INSIDE a layer use the same pattern recursively: either private
/// first-match-wins routing over child rects inside <see cref="OnPointer"/>,
/// or a real <see cref="UIBand.Popup"/>-band layer for transients that must
/// outrank everything (dropdowns).
/// </summary>
public abstract class UILayer
{
    /// <summary>Stable dotted id, matching the UIHitRegistry convention
    /// ("hud.spell_bar", "popup.SkillBookOverlay") — used for hit-rect
    /// registration and the ui_rects dev command.</summary>
    public abstract string Id { get; }

    /// <summary>Z-band. Within a band, <see cref="SubOrder"/> (assigned by the
    /// router at registration/push time) breaks ties — later = on top.</summary>
    public UIBand Band;
    public int SubOrder;

    /// <summary>Live visibility — forwards to the wrapped widget's existing
    /// IsVisible / menu-state check. Invisible layers are skipped entirely.</summary>
    public abstract bool Visible { get; }

    /// <summary>Visibility for the DRAW pass. Defaults to <see cref="Visible"/>;
    /// override when drawing and input diverge (draw-only chrome that takes no
    /// input, or the transient unit sheet that draws without claiming hits).</summary>
    public virtual bool VisibleForDraw => Visible;

    /// <summary>Stamped by the router each dispatch: true when input reached
    /// this layer un-consumed (no higher layer claimed the mouse). Layers that
    /// process input during Draw (immediate-mode editors, stats panel) read
    /// this instead of the global consumption flags.</summary>
    public bool InputGranted;

    /// <summary>Stamped by the router each dispatch: this layer is the hover
    /// owner — the layer a click at the current cursor position would land in.
    /// THE hover/click-sync guarantee: a widget may only draw its hover
    /// highlight when its layer IsHovered, so a button never lights up when a
    /// click would actually hit a panel covering it.</summary>
    public bool IsHovered;

    /// <summary>Stamped by the router each dispatch: some OTHER layer owns the
    /// cursor (it is hovering that layer, or a blocking/light-dismiss layer
    /// above this one would swallow the click). When true, this layer must not
    /// hover-react at the cursor position — adapters mask MousePos off-screen
    /// so wrapped panels' internal hover tests fail automatically. False when
    /// the cursor is over open world: the layer sees the real position (it
    /// just isn't under the cursor).</summary>
    public bool HoverStolen;

    /// <summary>Blocking modal: clicks outside the layer are swallowed so
    /// nothing below (panels, world) sees them. The layer stays open.</summary>
    public virtual bool Blocking => false;

    /// <summary>Dropdown/popover semantics: a click outside closes the layer
    /// AND is swallowed (the click only dismisses).</summary>
    public virtual bool LightDismiss => false;

    /// <summary>Click-away for non-blocking side panels: a click outside closes
    /// the layer but is NOT swallowed — the same click still acts on the world.</summary>
    public virtual bool CloseOnOutsideClick => false;

    /// <summary>Participates in ESC routing: the topmost visible closable layer
    /// gets <see cref="OnCancel"/> and the key is consumed.</summary>
    public virtual bool Closable => false;

    /// <summary>Close/cancel reaction (ESC, light-dismiss outside click).</summary>
    public virtual void OnCancel() { }

    /// <summary>True when the pixel is inside the layer's interactive surface.
    /// Must use the same layout math as drawing so input can't desync from
    /// visuals.</summary>
    public abstract bool ContainsMouse(int mx, int my, in UICtx ctx);

    /// <summary>Catalogue the layer's footprint into the central hit registry
    /// (drives MouseOverUI + the ui_rects dev command). Default: blocking →
    /// full screen; otherwise a ContainsMouse probe under <see cref="Id"/>.</summary>
    public virtual void AppendHitRects(UIHitRegistry reg, in UICtx ctx)
    {
        if (Blocking) { reg.AddFullScreen(Id); return; }
        var c = this; // capture for the probe delegate
        var ctxCopy = ctx;
        reg.AddProbe(Id, (mx, my) => c.ContainsMouse(mx, my, in ctxCopy));
    }

    /// <summary>The standard input template — the old PopupManager.RouteInput
    /// rules, applied by every layer when its turn in the top-down walk comes
    /// instead of pre-judged for the top of a stack. Override only for floor
    /// layers with non-surface semantics (the world).</summary>
    public virtual void HandleInput(InputState input, in UICtx ctx)
    {
        int mx = (int)input.MousePos.X, my = (int)input.MousePos.Y;

        // ESC: first closable layer in the walk wins; consume so nothing below
        // double-closes on the same press.
        if (Closable && input.WasKeyPressedUnhandled(Keys.Escape))
        {
            OnCancel();
            input.ConsumeKey(Keys.Escape);
        }

        if ((input.LeftPressed || input.RightPressed) && !input.IsMouseConsumed)
        {
            if (ContainsMouse(mx, my, in ctx))
            {
                OnPointer(input, in ctx);
                input.ConsumeMouse(); // panel surface always swallows its own clicks
            }
            else if (LightDismiss)
            {
                OnCancel();
                input.ConsumeMouse();
            }
            else if (Blocking)
            {
                input.ConsumeMouse();
            }
            else if (CloseOnOutsideClick && !input.MouseOverUI)
            {
                // Click-away onto the WORLD closes the layer without consuming,
                // so the same click still acts on what was clicked. Clicks on
                // OTHER UI (e.g. the inventory, to deposit into the bench) must
                // not dismiss — hence the MouseOverUI gate.
                OnCancel();
            }
        }

        if (input.ScrollDelta != 0 && !input.IsScrollConsumed && ContainsMouse(mx, my, in ctx))
        {
            OnScroll(input);
            input.ConsumeScroll();
        }

        OnFrame(input, in ctx);
    }

    /// <summary>A press (left or right) landed inside the layer. The template
    /// consumes the mouse after this returns.</summary>
    protected virtual void OnPointer(InputState input, in UICtx ctx) { }

    /// <summary>Unconsumed scroll with the cursor inside the layer. The template
    /// consumes the scroll after this returns.</summary>
    protected virtual void OnScroll(InputState input) { }

    /// <summary>Unconditional per-dispatch tick (hover tracking, timers, drag
    /// continuation) — runs every frame the layer is visible, consumed or not.</summary>
    protected virtual void OnFrame(InputState input, in UICtx ctx) { }

    /// <summary>Draw the layer. Used once drawing iterates the router's list
    /// (draw-unification phase); until then existing draw sites remain.</summary>
    public virtual void Draw(in UICtx ctx) { }
}
