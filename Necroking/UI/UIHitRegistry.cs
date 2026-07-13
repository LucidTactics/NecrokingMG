using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;

namespace Necroking.UI;

/// <summary>
/// Central per-frame catalogue of every screen region that belongs to UI and
/// should block mouse interaction with the world underneath. Rebuilt once per
/// Update (Game1.RebuildUIHitRects) by asking each UI object for its footprint
/// — popup layers via <see cref="IModalLayer.HitBounds"/> / PopupManager, HUD
/// elements via HUDRenderer.AppendHitRects, plus Game1-level extras (toasts,
/// aggression bar). InputState.MouseOverUI is then derived from
/// <see cref="Hit(int,int)"/> in ONE place instead of ad-hoc hit tests scattered
/// through the update loop.
///
/// Entry kinds:
///   rect        — a concrete rectangle (the normal case; inspectable via ui_rects)
///   fullscreen  — blankets the whole screen (blocking modals, open editor dropdown)
///   probe       — a ContainsMouse delegate for layers that can't cheaply expose
///                 a rect yet (immediate-mode editor popups); still centrally
///                 registered and enumerable, just without dimensions.
///
/// Id convention: dotted lowercase paths ("hud.menu_row.2", "popup.GrimoireOverlay",
/// "toast.skill_learn.0"). Prefix queries (<see cref="Hit(int,int,string)"/>) let
/// callers test a category — e.g. the map editor asks "is the cursor on hud.*"
/// without being fooled by its own full-screen popup layer entry.
/// </summary>
public sealed class UIHitRegistry
{
    public readonly struct Entry
    {
        public readonly string Id;
        public readonly Rectangle Rect;
        public readonly bool FullScreen;
        public readonly Func<int, int, bool>? Probe;

        public Entry(string id, Rectangle rect) { Id = id; Rect = rect; FullScreen = false; Probe = null; }
        public Entry(string id) { Id = id; Rect = default; FullScreen = true; Probe = null; }
        public Entry(string id, Func<int, int, bool> probe) { Id = id; Rect = default; FullScreen = false; Probe = probe; }

        public bool Test(int mx, int my)
            => FullScreen || (Probe != null ? Probe(mx, my) : Rect.Contains(mx, my));
    }

    private readonly List<Entry> _entries = new();

    public IReadOnlyList<Entry> Entries => _entries;

    public void Clear() => _entries.Clear();

    public void Add(string id, Rectangle rect) => _entries.Add(new Entry(id, rect));

    /// <summary>Blanket entry — covers the whole screen (blocking modal semantics:
    /// "a dialog is up, the cursor is over UI no matter where it is").</summary>
    public void AddFullScreen(string id) => _entries.Add(new Entry(id));

    /// <summary>Delegate entry for layers that can't expose a rect (yet). Prefer
    /// implementing <see cref="IModalLayer.HitBounds"/> so the region shows up
    /// with real dimensions in the ui_rects dev command.</summary>
    public void AddProbe(string id, Func<int, int, bool> probe) => _entries.Add(new Entry(id, probe));

    /// <summary>Is the point over any registered UI region?</summary>
    public bool Hit(int mx, int my)
    {
        for (int i = 0; i < _entries.Count; i++)
            if (_entries[i].Test(mx, my)) return true;
        return false;
    }

    /// <summary>Is the point over a registered UI region whose id starts with
    /// <paramref name="idPrefix"/>? Lets callers scope the test to a category
    /// (e.g. "hud.") and ignore blanket popup entries.</summary>
    public bool Hit(int mx, int my, string idPrefix)
    {
        for (int i = 0; i < _entries.Count; i++)
            if (_entries[i].Id.StartsWith(idPrefix, StringComparison.Ordinal) && _entries[i].Test(mx, my))
                return true;
        return false;
    }

    /// <summary>Id of the first region containing the point, or null. Debug/dev aid.</summary>
    public string? HitId(int mx, int my)
    {
        for (int i = 0; i < _entries.Count; i++)
            if (_entries[i].Test(mx, my)) return _entries[i].Id;
        return null;
    }
}
