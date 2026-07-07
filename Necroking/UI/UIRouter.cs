using System.Collections.Generic;
using Necroking.Core;

namespace Necroking.UI;

/// <summary>
/// Owns the single z-ordered list of <see cref="UILayer"/>s and dispatches
/// input through it top-down (reverse render order): the topmost visible layer
/// gets first claim on the mouse, and each layer receives the input state
/// exactly as the layers above it left it — no pre-consumption on anyone's
/// behalf. Drawing (once unified) walks the SAME list bottom-up, which is what
/// guarantees "drawn on top ⇔ clicked first".
///
/// Static layers (HUD widgets, panels, the world floor) are
/// <see cref="Register"/>ed once at startup and gate themselves via
/// <see cref="UILayer.Visible"/>. Transients (dropdowns) are
/// <see cref="Push"/>ed/<see cref="Pop"/>ped and get a fresh SubOrder each
/// push, so the newest sits on top of its band. No click-to-front: panel
/// order within a band is stable.
/// </summary>
public sealed class UIRouter
{
    private readonly List<UILayer> _layers = new();
    private UILayer? _capture;
    private int _nextSubOrder;
    private bool _sorted;

    /// <summary>All registered layers, sorted bottom-up (draw order) by the
    /// time anyone reads this. Exposed for the draw pass and dev inspection.</summary>
    public IReadOnlyList<UILayer> Layers
    {
        get { EnsureSorted(); return _layers; }
    }

    /// <summary>Add a permanent layer (startup wiring). Registration order is
    /// the within-band tiebreak: later = on top.</summary>
    public void Register(UILayer layer)
    {
        layer.SubOrder = ++_nextSubOrder;
        _layers.Add(layer);
        _sorted = false;
    }

    /// <summary>Add a transient layer (dropdown, popover) on top of its band.
    /// Idempotent while already present.</summary>
    public void Push(UILayer layer)
    {
        if (_layers.Contains(layer)) return;
        Register(layer);
    }

    /// <summary>Remove a transient layer. Safe on a layer that isn't present.</summary>
    public void Pop(UILayer layer)
    {
        if (_layers.Remove(layer)) _sorted = false;
        if (_capture == layer) _capture = null;
    }

    /// <summary>Grant the layer exclusive mouse ownership until the left button
    /// is released — for drags (window move, tile drag), so in-progress drags
    /// never leak LeftDown frames to layers underneath.</summary>
    public void SetCapture(UILayer layer) => _capture = layer;

    public void ReleaseCapture(UILayer layer)
    {
        if (_capture == layer) _capture = null;
    }

    private void EnsureSorted()
    {
        if (_sorted) return;
        // Stable ordering: band, then push/registration order within the band.
        _layers.Sort(static (a, b) =>
            a.Band != b.Band ? (int)a.Band - (int)b.Band : a.SubOrder - b.SubOrder);
        _sorted = true;
    }

    /// <summary>The layer a click at the current cursor position would land in
    /// (this frame's hover owner), or null when the cursor is over open world.
    /// Computed by <see cref="DispatchInput"/>.</summary>
    public UILayer? HoverLayer { get; private set; }

    /// <summary>Per-frame input dispatch. Call once per Update after the frame's
    /// InputState is captured and hit rects are rebuilt.</summary>
    public void DispatchInput(InputState input, in UICtx ctx)
    {
        EnsureSorted();

        // --- Hover pass: find the layer a click at the cursor would land in,
        // walking top-down with the same rules the click dispatch uses. A
        // blocking / light-dismiss layer that doesn't contain the cursor still
        // ENDS the walk (it would swallow the click), so nothing below it may
        // hover-react. This is what keeps "lights up on hover" and "activates
        // on click" in sync.
        int hmx = (int)input.MousePos.X, hmy = (int)input.MousePos.Y;
        UILayer? hover = null;
        bool hoverBlocked = false;
        if (_capture != null && _capture.Visible)
        {
            hover = _capture; // a drag owns the cursor outright
        }
        else
        {
            for (int i = _layers.Count - 1; i >= 0; i--)
            {
                var layer = _layers[i];
                if (!layer.Visible) continue;
                if (layer.ContainsMouse(hmx, hmy, in ctx)) { hover = layer; break; }
                if (layer.Blocking || layer.LightDismiss) { hoverBlocked = true; break; }
            }
        }
        HoverLayer = hover;
        for (int i = 0; i < _layers.Count; i++)
        {
            var layer = _layers[i];
            layer.IsHovered = layer == hover;
            // Cursor belongs to someone else (another layer, or a blanket above
            // this one). Below the hover owner / blanket, everything is stolen;
            // above it, layers simply don't contain the cursor and keep the
            // real position.
            layer.HoverStolen = layer != hover
                && (hoverBlocked || hover != null)
                && !IsAboveHover(layer, hover);
        }

        // A drag in progress owns the mouse outright — the capturing layer
        // handles input first and everything else sees it consumed.
        if (_capture != null)
        {
            var cap = _capture;
            if (cap.Visible)
            {
                cap.InputGranted = true;
                cap.HandleInput(input, in ctx);
                input.ConsumeMouse();
                input.ConsumeScroll();
                if (!input.LeftDown) _capture = null;
            }
            else
            {
                _capture = null; // layer closed mid-drag — drop the capture
            }
        }

        // Top-down walk (reverse render order): highest band/suborder first.
        for (int i = _layers.Count - 1; i >= 0; i--)
        {
            var layer = _layers[i];
            layer.InputGranted = !input.IsMouseConsumed;
            if (!layer.Visible || layer == _capture) continue;
            layer.HandleInput(input, in ctx);
        }
    }

    private static bool IsAboveHover(UILayer layer, UILayer? hover)
        => hover != null
           && (layer.Band > hover.Band
               || (layer.Band == hover.Band && layer.SubOrder > hover.SubOrder));

    /// <summary>Catalogue every visible layer's footprint into the central hit
    /// registry, top-down — append order is z-order, so UIHitRegistry.HitId
    /// returns the true topmost region.</summary>
    public void AppendHitRects(UIHitRegistry reg, in UICtx ctx)
    {
        EnsureSorted();
        for (int i = _layers.Count - 1; i >= 0; i--)
        {
            var layer = _layers[i];
            if (!layer.Visible) continue;
            layer.AppendHitRects(reg, in ctx);
        }
    }

    /// <summary>Draw all visible layers bottom-up — the SAME list input walks
    /// top-down, which is what makes "drawn on top ⇔ clicked first" structural
    /// rather than a coincidence of call ordering.</summary>
    public void Draw(in UICtx ctx)
    {
        EnsureSorted();
        for (int i = 0; i < _layers.Count; i++)
        {
            var layer = _layers[i];
            if (!layer.VisibleForDraw) continue;
            layer.Draw(in ctx);
        }
    }
}
