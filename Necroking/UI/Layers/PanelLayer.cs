using System;
using Microsoft.Xna.Framework;
using Necroking.Core;

namespace Necroking.UI;

/// <summary>
/// Router adapter for the in-game panels/overlays that implement
/// <see cref="IModalLayer"/> (inventory, job board, grimoire, skill book, …).
/// The panel keeps its own visibility, layout, and Update logic; this wrapper
/// gives it a place in the z-ordered layer list and forwards the modal flags.
///
/// The input contract panels get through this adapter (the fix for the old
/// "PopupManager pre-consumed my click" hack):
///  - Press edges are masked off while a HIGHER layer already claimed the
///    click, so a panel never acts on a stolen press — no consumed-by-whom
///    re-checking needed inside the panel.
///  - MousePos is parked off-screen while another layer owns the cursor
///    (<see cref="UILayer.HoverStolen"/>), so the panel's internal hover
///    highlights can only light up when a click would actually reach it.
///  - A panel that reports a drag in progress gets router mouse capture until
///    the button releases, so drags never leak into layers underneath.
/// </summary>
public sealed class PanelLayer : UILayer
{
    public delegate void PanelUpdate(InputState input, in UICtx ctx);

    private readonly UIRouter _router;
    private readonly IModalLayer _panel;
    private readonly string _id;
    private readonly Func<bool> _visible;
    private readonly PanelUpdate? _update;
    private readonly Func<bool>? _dragging;

    public PanelLayer(UIRouter router, IModalLayer panel, UIBand band, string id,
        Func<bool> visible, PanelUpdate? update = null, Func<bool>? dragging = null)
    {
        _router = router;
        _panel = panel;
        _id = id;
        _visible = visible;
        _update = update;
        _dragging = dragging;
        Band = band;
    }

    public override string Id => _id;
    public override bool Visible => _visible();
    public override bool Blocking => _panel.IsBlocking;
    public override bool LightDismiss => _panel.LightDismiss;
    public override bool CloseOnOutsideClick => _panel.CloseOnOutsideClick;
    public override bool Closable => true;
    public override void OnCancel() => _panel.OnCancel();

    public override bool ContainsMouse(int mx, int my, in UICtx ctx)
        => _panel.ContainsMouse(mx, my);

    /// <summary>Same footprint rules PopupManager.AppendHitRects applied:
    /// blocking → whole screen, else concrete bounds, else a probe.</summary>
    public override void AppendHitRects(UIHitRegistry reg, in UICtx ctx)
    {
        if (_panel.IsBlocking) { reg.AddFullScreen(_id); return; }
        var bounds = _panel.HitBounds(ctx.ScreenW, ctx.ScreenH);
        if (bounds.HasValue) reg.Add(_id, bounds.Value);
        else reg.AddProbe(_id, _panel.ContainsMouse);
    }

    protected override void OnFrame(InputState input, in UICtx ctx)
    {
        if (_dragging != null && _dragging() && input.LeftDown)
            _router.SetCapture(this);

        if (_update == null) return;

        // Mask what this panel must not see: presses a higher layer claimed,
        // and the cursor while another layer owns it. Restored after the call —
        // the InputState instance is shared with the rest of the frame.
        bool maskPress = !InputGranted;
        bool maskPos = HoverStolen;
        bool lp = input.LeftPressed, rp = input.RightPressed;
        var pos = input.MousePos;
        if (maskPress) { input.LeftPressed = false; input.RightPressed = false; }
        if (maskPos) input.MousePos = new Vector2(-10000, -10000);
        _update(input, in ctx);
        if (maskPress) { input.LeftPressed = lp; input.RightPressed = rp; }
        if (maskPos) input.MousePos = pos;
    }
}
