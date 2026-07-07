using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using Necroking.Core;

namespace Necroking.UI;

/// <summary>
/// A layer that can sit on the modal stack — color picker, dropdown, dialog,
/// editor window, in-game panel, etc. The layer's lifetime is owned by whatever
/// opened it; <see cref="PopupManager"/> only tracks z-order and routes input.
///
/// Migration pattern: existing popups that already have an <c>IsVisible</c> /
/// <c>Open()</c> / <c>Close()</c> pair just need to:
///   1. Implement this interface (or wrap themselves in an adapter).
///   2. Call <see cref="PopupManager.Push"/> when opened, <see cref="PopupManager.Pop"/> on close.
///   3. Delete their own ESC handling (the manager calls <see cref="OnCancel"/>) and their
///      own ConsumeMouse spam on every control (the manager swallows clicks at panel level).
///
/// The interface stays small on purpose. Anything bigger (text input routing,
/// focus, animation states) belongs on the popup itself, not in this contract.
/// </summary>
public interface IModalLayer
{
    /// <summary>True when the given screen-space pixel falls inside the popup's
    /// interactive surface (its panel, not its drop-shadow / outside dim). When
    /// the user clicks here, the layer's own Update will handle it; the manager
    /// just consumes the click so it doesn't leak through to layers below.</summary>
    bool ContainsMouse(int mx, int my);

    /// <summary>The popup's reaction to ESC, an outside click while
    /// <see cref="LightDismiss"/> is true, or any other "close me" signal. Most
    /// popups just call their existing Close() here; some (with unsaved-changes
    /// prompts) intercept and spawn a confirm dialog instead.</summary>
    void OnCancel();

    /// <summary>True for dropdowns, popovers, context menus — clicks outside
    /// the panel close the popup AND are swallowed (don't fall through). False
    /// for dialogs, editor windows, in-game panels — outside clicks are eaten
    /// (or passed through, per <see cref="IsBlocking"/>) but the popup stays
    /// open. Matches the HTML <c>popover="auto"</c> / Dear ImGui
    /// <c>BeginPopupModal</c> distinction.</summary>
    bool LightDismiss { get; }

    /// <summary>True for dialogs / dropdowns / sub-popups — clicks outside the
    /// panel are consumed so the layers / gameplay underneath don't fire. False
    /// for non-modal side panels (inventory, building menu, crafting menu) —
    /// the layer wants its OWN panel clicks consumed but lets the rest of the
    /// screen stay interactive (e.g. you can shoot spells with the inventory
    /// open). Implicitly true when <see cref="LightDismiss"/> is true — a
    /// dropdown that doesn't swallow the outside click would let through the
    /// click that's meant to close it.</summary>
    bool IsBlocking { get; }

    /// <summary>For non-blocking side panels that should close when the user
    /// clicks the world outside them, but WITHOUT swallowing that click — so the
    /// same click still hovers / hits world objects. The difference from
    /// <see cref="LightDismiss"/>: light-dismiss closes AND consumes (the click
    /// only dismisses); this closes and lets the click through (click-away that
    /// also acts on whatever you clicked). Only meaningful for a non-blocking,
    /// non-light-dismiss layer. Default false. Opt-in via a default-interface
    /// member so existing layers need no change.</summary>
    bool CloseOnOutsideClick => false;

    /// <summary>The layer's interactive surface as a concrete rect, for the
    /// central <see cref="UIHitRegistry"/> — should cover the same area
    /// <see cref="ContainsMouse"/> tests. Layers that can't cheaply compute a
    /// rect return null (the default) and get registered as a ContainsMouse
    /// probe instead; implement this where possible so the region is
    /// inspectable (ui_rects dev command) and stays hover-accurate even before
    /// the layer's first Draw.</summary>
    Rectangle? HitBounds(int screenW, int screenH) => null;
}

/// <summary>
/// Owns the EDITOR-INTERNAL modal stack and routes input to the topmost open
/// popup. Since the UIRouter migration this stack only ever holds the
/// full-screen editors' transient sub-popups (dropdowns, text fields, confirm
/// dialogs, color picker, texture browser, env/wall editors) — game panels are
/// individual UILayer seats in the router, and the stack itself participates
/// via <see cref="ModalStackLayer"/> at the top of the layer order.
///
/// Behavior (modeled after Dear ImGui's popup stack + the HTML &lt;dialog&gt;
/// "top layer" rules, since both target the same single-threaded-game-loop /
/// single-swapchain shape Necroking has):
///
///   1. While the stack is non-empty, no mouse / ESC reaches gameplay or
///      lower-stack popups. The top layer either handles the click (if it's
///      inside <see cref="IModalLayer.ContainsMouse"/>) or — for light-dismiss
///      popups — gets closed by the click. Either way the mouse is consumed.
///   2. ESC always goes to the top of the stack via <see cref="IModalLayer.OnCancel"/>,
///      and is consumed via <see cref="InputState.ConsumeKey"/> so nothing below
///      sees it on the same frame. This is the fix for the "two layers close on
///      one ESC" bug class.
///   3. When a light-dismiss popup is pushed while another light-dismiss popup
///      is already on top, the existing one is popped first. Mirrors how
///      dropdowns/context-menus replace each other in every native toolkit.
///
/// Draw order is bottom-up — callers iterate <see cref="OpenLayers"/> and draw
/// in order. Drawing is NOT done by the manager itself; popups own their visuals
/// and the existing draw code stays as-is. The manager only orchestrates input.
///
/// Why not have the manager draw too? Two reasons:
///   - Popups currently draw at different sites (CharacterStatsUI in the HUD
///     pass, editors after world, dropdowns post-everything). Forcing a single
///     draw site would be invasive and offer no benefit — input routing is the
///     part that benefits from centralization, drawing isn't.
///   - It keeps the interface tiny. Adding a Draw() to IModalLayer would force
///     a wrapper around every popup that already draws itself; this way an
///     existing popup adopts the interface with two methods + a flag.
/// </summary>
public sealed class PopupManager
{
    private readonly List<IModalLayer> _stack = new();

    public int Count => _stack.Count;
    public bool IsEmpty => _stack.Count == 0;
    public IModalLayer? Top => _stack.Count > 0 ? _stack[_stack.Count - 1] : null;
    public IReadOnlyList<IModalLayer> OpenLayers => _stack;

    /// <summary>Is <paramref name="layer"/> currently on the stack? Used by
    /// popups whose <c>IsVisible</c> getter just forwards to "am I open?" — saves
    /// duplicating an _isOpen bool on the popup itself.</summary>
    public bool Contains(IModalLayer layer) => _stack.Contains(layer);

    /// <summary>Add a layer to the top of the stack. If it's a light-dismiss
    /// layer (dropdown, popover) and the current top is also light-dismiss,
    /// the existing top is closed first — only one transient popup at a time,
    /// matching how every native toolkit treats menus.</summary>
    public void Push(IModalLayer layer)
    {
        if (layer == null) return;
        if (_stack.Contains(layer)) return; // already open — idempotent

        // Replace, don't stack, when both are light-dismiss. Opening dropdown
        // B while dropdown A is open closes A — single dropdown invariant.
        if (layer.LightDismiss && _stack.Count > 0 && _stack[_stack.Count - 1].LightDismiss)
        {
            var prev = _stack[_stack.Count - 1];
            _stack.RemoveAt(_stack.Count - 1);
            prev.OnCancel(); // give it a chance to clean up
        }
        _stack.Add(layer);
    }

    /// <summary>Remove a layer from the stack. Safe to call on a layer that's
    /// not present (no-op). Used by popups in their own Close() path so the
    /// manager learns about the close even though it didn't initiate it.
    /// Does NOT call <see cref="IModalLayer.OnCancel"/> — the caller already
    /// decided to close.</summary>
    public void Pop(IModalLayer layer)
    {
        if (layer == null) return;
        _stack.Remove(layer);
    }

    /// <summary>Pop and cancel the topmost layer. Used by the ESC routing path
    /// and by light-dismiss outside-click. Equivalent to <c>layer.OnCancel()</c>
    /// followed by <see cref="Pop"/>; if OnCancel itself removes the layer
    /// (the common case), the manager's Pop is a no-op.</summary>
    public void CancelTop()
    {
        if (_stack.Count == 0) return;
        var top = _stack[_stack.Count - 1];
        top.OnCancel();
        // Safe-pop: OnCancel almost always removes itself, but if it didn't
        // (e.g. unsaved-changes interception that opened a confirm dialog), we
        // leave the stack as the OnCancel handler left it. Confirm dialog
        // pushes a new layer on top, this layer stays — correct behavior.
    }

    /// <summary>Per-frame input routing. Must run BEFORE any other system
    /// reads input (gameplay, editors, panels). When the stack is non-empty:
    ///   - Mouse: consumed unconditionally. If light-dismiss + outside the
    ///     top's panel, the top is cancelled.
    ///   - ESC: cancels the top, consumes the key so nothing below sees it.
    /// When the stack is empty, this is a no-op and input flows normally.</summary>
    public void RouteInput(InputState input)
    {
        if (_stack.Count == 0) return;
        var top = _stack[_stack.Count - 1];

        int mx = (int)input.MousePos.X;
        int my = (int)input.MousePos.Y;

        // Click handling. The rules:
        //   inside the panel:  always consume — the layer's own Update handles
        //                      what to do with the click.
        //   outside + light-dismiss: cancel the layer AND consume the click.
        //   outside + blocking (modal): consume — dialog stays open, layers
        //                      below don't see the click.
        //   outside + non-blocking (side panel): let the click fall through.
        //                      Side panels coexist with gameplay (inventory
        //                      open while you spellcast).
        if (input.LeftPressed || input.RightPressed)
        {
            bool inside = top.ContainsMouse(mx, my);
            if (inside)
            {
                input.ConsumeMouse();
            }
            else if (top.LightDismiss)
            {
                top.OnCancel();
                input.ConsumeMouse();
            }
            else if (top.IsBlocking)
            {
                input.ConsumeMouse();
            }
            else if (top.CloseOnOutsideClick)
            {
                // Close-on-click-away, but DON'T consume — the click still reaches
                // the world (hover/select/act on whatever was clicked).
                top.OnCancel();
            }
            // else: non-blocking + not-light-dismiss — let it through.
        }

        // Scroll-wheel: only swallowed when the cursor is over the layer.
        // For the TOP layer's own handlers (eg map editor's sidebar tab
        // scroll, env editor's HandlePanelScroll), we deliberately do NOT
        // preemptively consume — the top layer reads raw mouse and is the
        // legitimate consumer. Lower layers gate themselves on InputLayer
        // (set by blocking sub-popups like the texture file browser) or on
        // their own popupBlocking flags. Side panels with their own
        // scrollable contents (inventory list) consume their own scroll
        // inside Update.
        if (input.ScrollDelta != 0 && top.ContainsMouse(mx, my)) input.ConsumeScroll();

        // ESC routing — the canonical bug fix. Top layer cancels; key is
        // consumed so the Game1 ESC handler downstream sees IsKeyConsumed and
        // skips its own close logic.
        if (input.WasKeyPressed(Keys.Escape) && !input.IsKeyConsumed(Keys.Escape))
        {
            CancelTop();
            input.ConsumeKey(Keys.Escape);
        }

        // NOTE: MouseOverUI is no longer set here. Every open layer's footprint
        // is catalogued into the central UIHitRegistry via AppendHitRects below
        // (blocking modal = whole screen, side panel = its own rect), and
        // Game1.RebuildUIHitRects derives MouseOverUI from that in one place.
    }

    /// <summary>Catalogue every open layer's footprint into the central
    /// <see cref="UIHitRegistry"/>: a BLOCKING modal covers the whole screen
    /// (same semantics RouteInput's click-swallowing gives it); a non-blocking
    /// side panel covers its own rect via <see cref="IModalLayer.HitBounds"/>,
    /// falling back to a ContainsMouse probe for layers without one. The whole
    /// stack is walked, not just the top, so a panel under another open panel
    /// still blocks its own footprint.</summary>
    public void AppendHitRects(UIHitRegistry reg, int screenW, int screenH)
    {
        foreach (var layer in _stack)
        {
            string id = "popup." + layer.GetType().Name;
            if (layer.IsBlocking)
            {
                reg.AddFullScreen(id);
                continue;
            }
            var bounds = layer.HitBounds(screenW, screenH);
            if (bounds.HasValue) reg.Add(id, bounds.Value);
            else reg.AddProbe(id, layer.ContainsMouse);
        }
    }
}

/// <summary>
/// Common adapter for the dozens of popups that have the exact same shape:
///   - "Am I open?" → boolean flag
///   - "Close me" → flip flag to false, possibly fire a callback
///   - Panel rectangle for the hit-test
/// Use this when you don't want to add IModalLayer plumbing to a class that
/// already has its own visibility state — instantiate one of these next to
/// the popup and let the manager treat the adapter as the layer.
/// </summary>
public sealed class ActionModalLayer : IModalLayer
{
    public Rectangle Panel;
    public System.Action? OnCancelAction;
    public bool LightDismiss { get; init; }
    public bool IsBlocking { get; init; } = true;

    public bool ContainsMouse(int mx, int my) => Panel.Contains(mx, my);
    public Rectangle? HitBounds(int screenW, int screenH) => Panel;
    public void OnCancel() => OnCancelAction?.Invoke();
}
