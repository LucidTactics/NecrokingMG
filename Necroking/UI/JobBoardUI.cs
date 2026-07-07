using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Necroking.Core;
using Necroking.Game.Jobs;

namespace Necroking.UI;

/// <summary>
/// The job board: priority-ordered tiles (hidden until their building exists).
/// Everything is inline on the tile — no pop-up sub-panels:
///   • workers/cap badge with a small [-]/[+] stepper directly beneath it,
///   • a storage/population fill bar + count,
///   • ▲▼ reorder buttons, and drag-to-reorder (the tile lifts and the list opens
///     a gap where it will drop),
///   • for multi-output jobs (potions) the maintain-stock targets render as extra
///     rows on the same tile.
/// Text goes through the widget renderer (FontStashSharp) so it stays crisp.
/// Toggle with the 'O' key.
/// </summary>
public class JobBoardUI : IModalLayer
{
    private SpriteBatch _batch = null!;
    private Render.SpriteScope Scope => _batch;  // straight-alpha draw surface (implicit conversion)
    private Texture2D _pixel = null!;
    private RuntimeWidgetRenderer _r = null!;
    private WorkerSystem _ws = null!;

    private bool _visible;
    private int _x, _y, _w, _h;

    // drag state
    private bool _dragging;
    private string _dragJobId = "";
    private int _dragGrabDy;
    private int _dragMouseY;

    private const int Pad = 14;
    private const int TitleH = 30;
    private const int TileBaseH = 56;
    private const int Gap = 8;
    private const int SlotH = TileBaseH + Gap; // uniform height used while dragging

    private const int FsTitle = 18, FsName = 15, FsSmall = 12, FsBtn = 13;

    public bool IsVisible => _visible;
    /// <summary>Job-row drag in progress — the router captures the mouse for us.</summary>
    public bool IsDragging => _dragging;

    public void Init(SpriteBatch batch, Texture2D pixel, RuntimeWidgetRenderer renderer, WorkerSystem ws)
    { _batch = batch; _pixel = pixel; _r = renderer; _ws = ws; }

    public void Toggle(int screenW, int screenH)
    {
        if (_visible) { Close(); return; }
        _visible = true; _w = 410; _x = 40; _y = 70;
    }

    public void Close() { _visible = false; _dragging = false; _dragJobId = ""; }

    private int ListTop => _y + TitleH + Pad;

    // "Re-run auto-assignment" button in the title bar.
    private Rectangle ReassignRect => new Rectangle(_x + 64, _y + 8, 112, 18);

    private List<JobState> Visible()
    {
        var list = new List<JobState>();
        foreach (var js in _ws.Jobs) if (_ws.DerivedMax(js.Def) > 0) list.Add(js);
        return list;
    }

    private int TargetsHeight(JobDef def) => def.OutputChoice ? 22 + def.Outputs.Count * 24 + 8 : 0;
    private int TileHeight(JobDef def) => TileBaseH + TargetsHeight(def);

    // ── per-tile control geometry ──
    private struct Geo
    {
        public Rectangle Box, Up, Down, Badge, CapMinus, CapPlus, Bar;
    }
    private struct OutGeo { public string Id; public Rectangle Minus, Plus, Up, Down; public int RowY; }

    private Geo TileGeo(int x, int y, int innerW, int fullH)
    {
        var g = new Geo();
        g.Box = new Rectangle(x, y, innerW, fullH);
        int badgeW = 66;
        int badgeX = x + innerW - 8 - badgeW;
        g.Badge = new Rectangle(badgeX, y + 8, badgeW, 20);
        g.CapMinus = new Rectangle(badgeX, y + 31, 30, 18);
        g.CapPlus = new Rectangle(badgeX + 36, y + 31, 30, 18);
        int reorderLeft = badgeX - 6 - 18;
        g.Up = new Rectangle(reorderLeft, y + 8, 18, 18);
        g.Down = new Rectangle(reorderLeft, y + 31, 18, 18);
        int barRight = reorderLeft - 12 - 48;
        g.Bar = new Rectangle(x + 24, y + 40, System.Math.Max(40, barRight - (x + 24)), 7);
        return g;
    }

    private List<OutGeo> TargetGeos(JobState js, int x, int y, int innerW)
    {
        var outs = new List<OutGeo>();
        if (!js.Def.OutputChoice) return outs;
        int oy = y + TileBaseH + 22;
        foreach (var oid in OrderedOutputs(js))
        {
            outs.Add(new OutGeo
            {
                Id = oid, RowY = oy,
                Minus = new Rectangle(x + innerW - 168, oy, 20, 20),
                Plus = new Rectangle(x + innerW - 116, oy, 20, 20),
                Up = new Rectangle(x + innerW - 80, oy, 18, 20),
                Down = new Rectangle(x + innerW - 58, oy, 18, 20),
            });
            oy += 24;
        }
        return outs;
    }

    private List<string> OrderedOutputs(JobState js)
    {
        var list = new List<string>();
        foreach (var o in js.Def.Outputs) list.Add(o.Id);
        list.Sort((a, b) =>
        {
            int pa = js.OutputTargets.TryGetValue(a, out var ta) ? ta.Priority : 0;
            int pb = js.OutputTargets.TryGetValue(b, out var tb) ? tb.Priority : 0;
            return pa.CompareTo(pb);
        });
        return list;
    }

    // ───────────────────────────────────────── update
    public void Update(InputState input)
    {
        if (!_visible) return;
        int mx = (int)input.MousePos.X, my = (int)input.MousePos.Y;
        var vis = Visible();

        if (_dragging) { UpdateDrag(input, my, vis); return; }

        if (input.LeftPressed && new Rectangle(_x + _w - 26, _y + 7, 18, 18).Contains(mx, my)) { Close(); return; }
        if (!input.LeftPressed) return;
        if (ReassignRect.Contains(mx, my)) { _ws.AutoAssignWorkers(); return; }

        int innerX = _x + Pad, innerW = _w - 2 * Pad;
        int y = ListTop;
        for (int i = 0; i < vis.Count; i++)
        {
            var js = vis[i];
            int fullH = TileHeight(js.Def);
            var g = TileGeo(innerX, y, innerW, fullH);
            int eff = _ws.EffectiveCap(js), dmax = _ws.DerivedMax(js.Def);

            if (g.Up.Contains(mx, my)) { _ws.MoveJobBefore(js, i > 0 ? vis[i - 1] : js); return; }
            if (g.Down.Contains(mx, my)) { _ws.MoveJobBefore(js, i + 2 < vis.Count ? vis[i + 2] : null); return; }
            if (g.CapMinus.Contains(mx, my)) { _ws.SetCap(js, System.Math.Max(0, eff - 1)); return; }
            if (g.CapPlus.Contains(mx, my)) { _ws.SetCap(js, System.Math.Min(dmax, eff + 1)); return; }

            foreach (var o in TargetGeos(js, innerX, y, innerW))
            {
                var cur = js.OutputTargets.TryGetValue(o.Id, out var ot) ? ot : new OutputTarget();
                if (o.Minus.Contains(mx, my)) { ot.TargetStock = System.Math.Max(0, cur.TargetStock - 1); js.OutputTargets[o.Id] = ot; return; }
                if (o.Plus.Contains(mx, my)) { ot.TargetStock = System.Math.Min(99, cur.TargetStock + 1); js.OutputTargets[o.Id] = ot; return; }
                if (o.Up.Contains(mx, my)) { SwapOutputPriority(js, o.Id, -1); return; }
                if (o.Down.Contains(mx, my)) { SwapOutputPriority(js, o.Id, +1); return; }
            }

            // Start drag on the tile's top strip (name area), away from the controls.
            if (g.Box.Contains(mx, my) && my < g.Box.Y + 30 && mx < g.Up.X - 4)
            {
                _dragging = true; _dragJobId = js.Def.Id; _dragGrabDy = my - g.Box.Y; _dragMouseY = my;
                return;
            }
            y += fullH + Gap;
        }
    }

    private void UpdateDrag(InputState input, int my, List<JobState> vis)
    {
        _dragMouseY = my;
        if (input.LeftDown) return;
        var others = new List<JobState>();
        JobState? dragged = null;
        foreach (var js in vis) { if (js.Def.Id == _dragJobId) dragged = js; else others.Add(js); }
        int insertIdx = InsertIndex(my, others.Count);
        if (dragged != null)
            _ws.MoveJobBefore(dragged, insertIdx < others.Count ? others[insertIdx] : null);
        _dragging = false; _dragJobId = "";
    }

    private int InsertIndex(int my, int otherCount)
    {
        int rel = my - _dragGrabDy - ListTop;
        int idx = (int)System.Math.Round(rel / (float)SlotH);
        return System.Math.Clamp(idx, 0, otherCount);
    }

    private void SwapOutputPriority(JobState js, string id, int dir)
    {
        var ordered = OrderedOutputs(js);
        int p = ordered.IndexOf(id), q = p + dir;
        if (q < 0 || q >= ordered.Count) return;
        string other = ordered[q];
        var a = js.OutputTargets.TryGetValue(id, out var ta) ? ta : new OutputTarget();
        var b = js.OutputTargets.TryGetValue(other, out var tb) ? tb : new OutputTarget();
        (a.Priority, b.Priority) = (b.Priority, a.Priority);
        js.OutputTargets[id] = a; js.OutputTargets[other] = b;
    }

    // ───────────────────────────────────────── draw
    public void Draw()
    {
        if (!_visible) return;
        var vis = Visible();
        int innerX = _x + Pad, innerW = _w - 2 * Pad;

        int contentH;
        if (_dragging) contentH = System.Math.Max(1, vis.Count) * SlotH;
        else { int h = 0; foreach (var js in vis) h += TileHeight(js.Def) + Gap; contentH = System.Math.Max(0, h - Gap); }
        _h = TitleH + Pad + System.Math.Max(TileBaseH, contentH) + Pad;

        var panel = new Rectangle(_x, _y, _w, _h);
        Scope.Draw(_pixel, panel, new Color(24, 22, 28, 240));
        Border(panel, new Color(150, 140, 162, 220), 2);
        Text("Jobs", _x + Pad, _y + 7, FsTitle, new Color(230, 224, 234));
        DrawButton(ReassignRect, "Auto-assign", new Color(54, 74, 92, 235));
        Text("top = highest priority", _x + _w - 158, _y + 12, FsSmall, new Color(150, 144, 158));
        DrawButton(new Rectangle(_x + _w - 26, _y + 7, 18, 18), "x", new Color(86, 54, 54, 235));

        if (vis.Count == 0)
        {
            Text("No job buildings yet — build a pile or table.", innerX, ListTop, FsName, new Color(155, 150, 162));
            return;
        }

        if (_dragging) { DrawDragging(vis, innerX, innerW); return; }

        int y = ListTop;
        for (int i = 0; i < vis.Count; i++)
        {
            DrawTile(vis[i], innerX, y, innerW, i + 1, false);
            y += TileHeight(vis[i].Def) + Gap;
        }
    }

    private void DrawDragging(List<JobState> vis, int innerX, int innerW)
    {
        var others = new List<JobState>();
        JobState? dragged = null;
        foreach (var js in vis) { if (js.Def.Id == _dragJobId) dragged = js; else others.Add(js); }
        int insertIdx = InsertIndex(_dragMouseY, others.Count);

        int gapY = ListTop + insertIdx * SlotH;
        var gapRect = new Rectangle(innerX, gapY, innerW, TileBaseH);
        Scope.Draw(_pixel, gapRect, new Color(60, 70, 96, 90));
        Border(gapRect, new Color(120, 140, 190, 160), 1);

        int slot = 0;
        for (int k = 0; k < others.Count; k++)
        {
            if (slot == insertIdx) slot++;
            DrawTile(others[k], innerX, ListTop + slot * SlotH, innerW, slot + 1, false, collapsed: true);
            slot++;
        }

        if (dragged != null)
        {
            int fy = System.Math.Clamp(_dragMouseY - _dragGrabDy, ListTop, _y + _h - TileBaseH - Pad);
            Scope.Draw(_pixel, new Rectangle(innerX + 4, fy + 5, innerW, TileBaseH), new Color(0, 0, 0, 70));
            DrawTile(dragged, innerX, fy, innerW, insertIdx + 1, true, collapsed: true);
        }
    }

    private void DrawTile(JobState js, int x, int y, int innerW, int number, bool lifted, bool collapsed = false)
    {
        var def = js.Def;
        int fullH = collapsed ? TileBaseH : TileHeight(def);
        var g = TileGeo(x, y, innerW, fullH);
        bool full = _ws.IsStorageFull(def);
        int assigned = js.AssignedWorkers.Count;
        int eff = _ws.EffectiveCap(js);

        Scope.Draw(_pixel, g.Box, lifted ? new Color(58, 56, 72, 245) : new Color(36, 34, 42, 235));
        Border(g.Box, lifted ? new Color(170, 180, 220, 240) : new Color(92, 86, 104, 205), lifted ? 2 : 1);

        Text(":::", g.Box.X + 5, g.Box.Y + 10, FsName, new Color(120, 114, 132));
        Text($"{number}.", g.Box.X + 22, g.Box.Y + 9, FsName, new Color(150, 146, 160));
        Text(def.DisplayName, g.Box.X + 44, g.Box.Y + 9, FsName, new Color(228, 224, 234));

        // fill bar + count
        var (cur, max) = _ws.JobStorage(def);
        Scope.Draw(_pixel, g.Bar, new Color(15, 14, 18, 235));
        if (max > 0)
        {
            float f = System.Math.Clamp(cur / (float)max, 0f, 1f);
            var fillCol = full ? new Color(200, 84, 72) : (def.SpawnsUnit ? new Color(150, 112, 200) : new Color(92, 172, 122));
            if (f > 0f) Scope.Draw(_pixel, new Rectangle(g.Bar.X, g.Bar.Y, (int)(g.Bar.Width * f), g.Bar.Height), fillCol);
        }
        string cap = max > 0 ? max.ToString() : "-";
        Text($"{cur}/{cap}{(full ? "  full" : "")}", g.Bar.Right + 6, g.Bar.Y - 4, FsSmall,
            full ? new Color(222, 124, 112) : new Color(160, 156, 170));

        // reorder
        DrawButton(g.Up, "^", new Color(52, 50, 62, 235));
        DrawButton(g.Down, "v", new Color(52, 50, 62, 235));
        // workers/cap badge + inline stepper
        DrawButton(g.Badge, $"{assigned} / {eff}", assigned > 0 ? new Color(58, 82, 64, 240) : new Color(50, 48, 60, 235));
        DrawButton(g.CapMinus, "-", new Color(64, 52, 52, 235));
        DrawButton(g.CapPlus, "+", new Color(52, 64, 52, 235));

        // inline maintain-stock targets (potions)
        if (!collapsed && def.OutputChoice)
        {
            int dividerY = g.Box.Y + TileBaseH;
            Scope.Draw(_pixel, new Rectangle(g.Box.X + 10, dividerY, innerW - 20, 1), new Color(80, 76, 92, 180));
            Text("Keep in stock:", g.Box.X + 14, dividerY + 4, FsSmall, new Color(172, 168, 182));
            int host = _ws.FindHostBuilding(def, default);
            foreach (var o in TargetGeos(js, x, y, innerW))
            {
                var outDef = def.Outputs.Find(z => z.Id == o.Id);
                string name = string.IsNullOrEmpty(outDef.DisplayName) ? o.Id : outDef.DisplayName;
                int have = host >= 0 ? _ws.StoredOf(host, o.Id) : 0;
                int tgt = js.OutputTargets.TryGetValue(o.Id, out var ot) ? ot.TargetStock : 0;
                Text(name, g.Box.X + 18, o.RowY + 1, FsSmall, new Color(220, 218, 226));
                Text($"have {have}", g.Box.X + 150, o.RowY + 2, FsSmall, new Color(150, 146, 160));
                DrawButton(o.Minus, "-", new Color(64, 52, 52, 235));
                Text($"{tgt}", o.Minus.Right + 6, o.RowY + 1, FsSmall, new Color(228, 228, 232));
                DrawButton(o.Plus, "+", new Color(52, 64, 52, 235));
                DrawButton(o.Up, "^", new Color(52, 50, 62, 235));
                DrawButton(o.Down, "v", new Color(52, 50, 62, 235));
            }
        }
    }

    // IModalLayer
    public bool ContainsMouse(int mx, int my) => _visible && new Rectangle(_x, _y, _w, _h).Contains(mx, my);
    public Rectangle? HitBounds(int screenW, int screenH) => _visible ? new Rectangle(_x, _y, _w, _h) : null;
    public void OnCancel() => Close();
    public bool LightDismiss => false;
    public bool IsBlocking => false;

    private void Border(Rectangle r, Color c, int t) => Render.DrawUtils.DrawRectBorder(_batch, _pixel, r, c, t);
    private void Text(string s, int x, int y, int size, Color c) => _r.DrawText(s, x, y, size, c);

    private void DrawButton(Rectangle r, string label, Color fill)
    {
        Scope.Draw(_pixel, r, fill);
        Border(r, new Color(198, 198, 206, 195), 1);
        var sz = _r.MeasureText(label, FsBtn);
        _r.DrawText(label, (int)(r.X + (r.Width - sz.X) / 2f), (int)(r.Y + (r.Height - sz.Y) / 2f), FsBtn, new Color(232, 232, 236));
    }
}
