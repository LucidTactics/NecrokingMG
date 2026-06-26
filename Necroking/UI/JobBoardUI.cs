using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Necroking.Core;
using Necroking.Game.Jobs;

namespace Necroking.UI;

/// <summary>
/// The job board: priority-ordered tiles (hidden until their building exists),
/// each showing workers/cap + a storage fill bar. Drag a tile to reorder (the
/// tile lifts to follow the cursor and the list opens a gap where it will drop),
/// or use the ▲▼ buttons. Click the badge to expand a detail panel with the
/// worker-cap stepper and, for multi-output jobs, the maintain-stock targets.
/// Text is rendered through the widget renderer (FontStashSharp) so it stays
/// crisp at any size. Toggle with the 'O' key.
/// </summary>
public class JobBoardUI : IModalLayer
{
    private SpriteBatch _batch = null!;
    private Texture2D _pixel = null!;
    private RuntimeWidgetRenderer _r = null!;
    private WorkerSystem _ws = null!;

    private bool _visible;
    private int _x, _y, _w, _h;
    private string _expanded = "";

    // drag state
    private bool _dragging;
    private string _dragJobId = "";
    private int _dragGrabDy;
    private int _dragMouseY;
    private bool _debugDragHold; // screenshot hook: keep the drag rendered (no auto-drop)

    private const int Pad = 14;
    private const int TitleH = 30;
    private const int TileH = 52;
    private const int Gap = 8;
    private const int SlotH = TileH + Gap;

    // font sizes
    private const int FsTitle = 18, FsName = 15, FsSmall = 12, FsBtn = 13;

    public bool IsVisible => _visible;

    public void Init(SpriteBatch batch, Texture2D pixel, RuntimeWidgetRenderer renderer, WorkerSystem ws)
    { _batch = batch; _pixel = pixel; _r = renderer; _ws = ws; }

    public void Toggle(int screenW, int screenH)
    {
        if (_visible) { Close(); return; }
        _visible = true; _w = 410; _x = 40; _y = 70;
        Game1.Popups.Push(this);
    }

    public void Close() { _visible = false; _dragging = false; _debugDragHold = false; _dragJobId = ""; Game1.Popups.Pop(this); }
    public void Expand(string jobId) => _expanded = jobId;

    /// <summary>Dev/screenshot hook: force the board into a dragging state.</summary>
    public void DebugDrag(string jobId, int mouseY)
    { _dragging = true; _debugDragHold = true; _dragJobId = jobId; _dragGrabDy = TileH / 2; _dragMouseY = mouseY; _expanded = ""; }

    private int ListTop => _y + TitleH + Pad;

    /// <summary>Visible jobs in priority order (those whose building exists).</summary>
    private List<JobState> Visible()
    {
        var list = new List<JobState>();
        foreach (var js in _ws.Jobs) if (_ws.DerivedMax(js.Def) > 0) list.Add(js);
        return list;
    }

    // ── per-tile geometry (collapsed top row) ──
    private struct Geo { public Rectangle Box, Up, Down, Badge, Bar; }
    private Geo TileGeo(int boxX, int boxY, int innerW)
    {
        var g = new Geo();
        g.Box = new Rectangle(boxX, boxY, innerW, TileH);
        int badgeW = 62;
        g.Badge = new Rectangle(boxX + innerW - 10 - badgeW, boxY + 8, badgeW, 22);
        g.Down = new Rectangle(g.Badge.X - 6 - 18, boxY + 8, 18, 22);
        g.Up = new Rectangle(g.Down.X - 4 - 18, boxY + 8, 18, 22);
        g.Bar = new Rectangle(boxX + 24, boxY + TileH - 16, innerW - 24 - 110, 7);
        return g;
    }

    // ── detail geometry ──
    private struct OutGeo { public string Id; public Rectangle Minus, Plus, Up, Down; }
    private struct Detail
    {
        public Rectangle Box, CapMinus, CapPlus;
        public List<OutGeo> Outs;
        public int Height;
    }
    private Detail DetailGeo(JobState js, int boxX, int boxY, int innerW)
    {
        var d = new Detail { Outs = new List<OutGeo>() };
        bool choice = js.Def.OutputChoice;
        d.Height = 36 + (choice ? 22 + js.Def.Outputs.Count * 26 : 0) + 10;
        d.Box = new Rectangle(boxX, boxY, innerW, d.Height);
        d.CapMinus = new Rectangle(boxX + 110, boxY + 8, 22, 22);
        d.CapPlus = new Rectangle(boxX + 166, boxY + 8, 22, 22);
        if (choice)
        {
            int oy = boxY + 36 + 22;
            foreach (var oid in OrderedOutputs(js))
            {
                d.Outs.Add(new OutGeo
                {
                    Id = oid,
                    Minus = new Rectangle(boxX + innerW - 168, oy, 20, 20),
                    Plus = new Rectangle(boxX + innerW - 116, oy, 20, 20),
                    Up = new Rectangle(boxX + innerW - 80, oy, 18, 20),
                    Down = new Rectangle(boxX + innerW - 58, oy, 18, 20),
                });
                oy += 26;
            }
        }
        return d;
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

        // Close button.
        if (input.LeftPressed && new Rectangle(_x + _w - 26, _y + 7, 18, 18).Contains(mx, my)) { Close(); return; }
        if (!input.LeftPressed) return;

        int innerX = _x + Pad, innerW = _w - 2 * Pad;
        int y = ListTop;
        for (int i = 0; i < vis.Count; i++)
        {
            var js = vis[i];
            var g = TileGeo(innerX, y, innerW);
            bool expanded = js.Def.Id == _expanded;

            if (g.Up.Contains(mx, my)) { _ws.MoveJobBefore(js, i > 0 ? vis[i - 1] : js); return; }
            if (g.Down.Contains(mx, my)) { _ws.MoveJobBefore(js, i + 2 < vis.Count ? vis[i + 2] : null); return; }
            if (g.Badge.Contains(mx, my)) { _expanded = expanded ? "" : js.Def.Id; return; }

            y += TileH;
            if (expanded)
            {
                var d = DetailGeo(js, innerX, y, innerW);
                int eff = _ws.EffectiveCap(js), dmax = _ws.DerivedMax(js.Def);
                if (d.CapMinus.Contains(mx, my)) { _ws.SetCap(js, System.Math.Max(0, eff - 1)); return; }
                if (d.CapPlus.Contains(mx, my)) { _ws.SetCap(js, System.Math.Min(dmax, eff + 1)); return; }
                foreach (var o in d.Outs)
                {
                    var cur = js.OutputTargets.TryGetValue(o.Id, out var ot) ? ot : new OutputTarget();
                    if (o.Minus.Contains(mx, my)) { ot.TargetStock = System.Math.Max(0, cur.TargetStock - 1); js.OutputTargets[o.Id] = ot; return; }
                    if (o.Plus.Contains(mx, my)) { ot.TargetStock = System.Math.Min(99, cur.TargetStock + 1); js.OutputTargets[o.Id] = ot; return; }
                    if (o.Up.Contains(mx, my)) { SwapOutputPriority(js, o.Id, -1); return; }
                    if (o.Down.Contains(mx, my)) { SwapOutputPriority(js, o.Id, +1); return; }
                }
                y += d.Height;
            }

            // Start drag on the tile body (top row, not over a control).
            if (g.Box.Contains(mx, my) && my < g.Box.Y + 32
                && !g.Up.Contains(mx, my) && !g.Down.Contains(mx, my) && !g.Badge.Contains(mx, my))
            {
                _dragging = true; _dragJobId = js.Def.Id; _dragGrabDy = my - g.Box.Y; _dragMouseY = my;
                _expanded = ""; // collapse while dragging
                return;
            }
            y += Gap;
        }
    }

    private void UpdateDrag(InputState input, int my, List<JobState> vis)
    {
        if (_debugDragHold) return;
        _dragMouseY = my;
        if (input.LeftDown) return;
        // Drop: compute insertion among the non-dragged visible jobs.
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

        // measure panel height
        int contentH;
        if (_dragging) contentH = (vis.Count) * SlotH; // all collapsed + one floating gap accounted by count
        else
        {
            int h = 0;
            foreach (var js in vis)
            {
                h += SlotH;
                if (js.Def.Id == _expanded) h += DetailGeo(js, innerX, 0, innerW).Height;
            }
            contentH = h;
        }
        _h = TitleH + Pad + System.Math.Max(SlotH, contentH) + Pad - Gap;

        var panel = new Rectangle(_x, _y, _w, _h);
        _batch.Draw(_pixel, panel, new Color(24, 22, 28, 240));
        Border(panel, new Color(150, 140, 162, 220), 2);
        Text("Jobs", _x + Pad, _y + 7, FsTitle, new Color(230, 224, 234));
        Text("top = highest priority", _x + _w - 180, _y + 12, FsSmall, new Color(150, 144, 158));
        DrawButton(new Rectangle(_x + _w - 26, _y + 7, 18, 18), "x", new Color(86, 54, 54, 235));

        if (vis.Count == 0)
        {
            Text("No job buildings yet — build a pile or table.", innerX, ListTop, FsName, new Color(155, 150, 162));
            return;
        }

        if (_dragging) { DrawDragging(vis, innerX, innerW); return; }

        int y = ListTop;
        foreach (var js in vis)
        {
            int vi = js.Priority; // displayed number derived below from visible order
            DrawTile(js, innerX, y, innerW, IndexInVisible(vis, js) + 1, false);
            y += TileH;
            if (js.Def.Id == _expanded) { var d = DetailGeo(js, innerX, y, innerW); DrawDetail(js, d, innerX, innerW); y += d.Height; }
            y += Gap;
        }
    }

    private void DrawDragging(List<JobState> vis, int innerX, int innerW)
    {
        var others = new List<JobState>();
        JobState? dragged = null;
        foreach (var js in vis) { if (js.Def.Id == _dragJobId) dragged = js; else others.Add(js); }
        int insertIdx = InsertIndex(_dragMouseY, others.Count);

        // gap slot
        int gapY = ListTop + insertIdx * SlotH;
        var gapRect = new Rectangle(innerX, gapY, innerW, TileH);
        _batch.Draw(_pixel, gapRect, new Color(60, 70, 96, 90));
        Border(gapRect, new Color(120, 140, 190, 160), 1);

        // other tiles, skipping the gap slot
        int slot = 0;
        for (int k = 0; k < others.Count; k++)
        {
            if (slot == insertIdx) slot++;
            int ty = ListTop + slot * SlotH;
            DrawTile(others[k], innerX, ty, innerW, slot + 1, false);
            slot++;
        }

        // floating dragged tile (on top, lifted)
        if (dragged != null)
        {
            int fy = System.Math.Clamp(_dragMouseY - _dragGrabDy, ListTop, _y + _h - TileH - Pad);
            // soft shadow
            _batch.Draw(_pixel, new Rectangle(innerX + 4, fy + 5, innerW, TileH), new Color(0, 0, 0, 70));
            DrawTile(dragged, innerX, fy, innerW, insertIdx + 1, true);
        }
    }

    private int IndexInVisible(List<JobState> vis, JobState js)
    { for (int i = 0; i < vis.Count; i++) if (vis[i] == js) return i; return 0; }

    private void DrawTile(JobState js, int x, int y, int innerW, int number, bool lifted)
    {
        var def = js.Def;
        var g = TileGeo(x, y, innerW);
        bool full = _ws.IsStorageFull(def);
        int assigned = js.AssignedWorkers.Count;
        int eff = _ws.EffectiveCap(js);

        _batch.Draw(_pixel, g.Box, lifted ? new Color(58, 56, 72, 245)
            : (def.Id == _expanded ? new Color(46, 42, 56, 240) : new Color(36, 34, 42, 235)));
        Border(g.Box, lifted ? new Color(170, 180, 220, 240) : new Color(92, 86, 104, 205), lifted ? 2 : 1);

        // grip
        Text(":::", g.Box.X + 5, g.Box.Y + 9, FsName, new Color(120, 114, 132));
        Text($"{number}.", g.Box.X + 22, g.Box.Y + 8, FsName, new Color(150, 146, 160));
        Text(def.DisplayName, g.Box.X + 44, g.Box.Y + 8, FsName, new Color(228, 224, 234));

        // storage / population bar (bottom row)
        var (cur, max) = _ws.JobStorage(def);
        _batch.Draw(_pixel, g.Bar, new Color(15, 14, 18, 235));
        if (max > 0)
        {
            float f = System.Math.Clamp(cur / (float)max, 0f, 1f);
            var fillCol = full ? new Color(200, 84, 72) : (def.SpawnsUnit ? new Color(150, 112, 200) : new Color(92, 172, 122));
            if (f > 0f) _batch.Draw(_pixel, new Rectangle(g.Bar.X, g.Bar.Y, (int)(g.Bar.Width * f), g.Bar.Height), fillCol);
        }
        string cap = max > 0 ? max.ToString() : "-";
        Text($"{cur}/{cap}{(full ? "  full" : "")}", g.Bar.Right + 8, g.Bar.Y - 4, FsSmall,
            full ? new Color(222, 124, 112) : new Color(160, 156, 170));

        DrawButton(g.Up, "^", new Color(52, 50, 62, 235));
        DrawButton(g.Down, "v", new Color(52, 50, 62, 235));
        DrawButton(g.Badge, $"{assigned} / {eff}", assigned > 0 ? new Color(58, 82, 64, 240) : new Color(50, 48, 60, 235));
    }

    private void DrawDetail(JobState js, Detail d, int innerX, int innerW)
    {
        var def = js.Def;
        _batch.Draw(_pixel, d.Box, new Color(30, 28, 36, 240));
        Border(d.Box, new Color(82, 78, 96, 190), 1);

        Text("Workers", d.Box.X + 10, d.Box.Y + 11, FsSmall, new Color(182, 178, 190));
        DrawButton(d.CapMinus, "-", new Color(64, 52, 52, 235));
        Text($"{_ws.EffectiveCap(js)}", d.CapMinus.Right + 16, d.Box.Y + 9, FsName, new Color(228, 228, 232));
        DrawButton(d.CapPlus, "+", new Color(52, 64, 52, 235));
        Text($"max {_ws.DerivedMax(def)}", d.CapPlus.Right + 12, d.Box.Y + 11, FsSmall, new Color(150, 146, 160));

        if (def.OutputChoice)
        {
            Text("Keep in stock:", d.Box.X + 10, d.Box.Y + 38, FsSmall, new Color(172, 168, 182));
            int host = _ws.FindHostBuilding(def, default);
            foreach (var o in d.Outs)
            {
                var outDef = def.Outputs.Find(z => z.Id == o.Id);
                string name = string.IsNullOrEmpty(outDef.DisplayName) ? o.Id : outDef.DisplayName;
                int have = host >= 0 ? _ws.StoredOf(host, o.Id) : 0;
                int tgt = js.OutputTargets.TryGetValue(o.Id, out var ot) ? ot.TargetStock : 0;
                int rowY = o.Minus.Y;
                Text(name, d.Box.X + 14, rowY + 1, FsSmall, new Color(220, 218, 226));
                Text($"have {have}", d.Box.X + 150, rowY + 2, FsSmall, new Color(150, 146, 160));
                DrawButton(o.Minus, "-", new Color(64, 52, 52, 235));
                Text($"{tgt}", o.Minus.Right + 6, rowY + 1, FsSmall, new Color(228, 228, 232));
                DrawButton(o.Plus, "+", new Color(52, 64, 52, 235));
                DrawButton(o.Up, "^", new Color(52, 50, 62, 235));
                DrawButton(o.Down, "v", new Color(52, 50, 62, 235));
            }
        }
    }

    // IModalLayer
    public bool ContainsMouse(int mx, int my) => _visible && new Rectangle(_x, _y, _w, _h).Contains(mx, my);
    public void OnCancel() => Close();
    public bool LightDismiss => false;
    public bool IsBlocking => false;

    private void Border(Rectangle r, Color c, int t) => Render.DrawUtils.DrawRectBorder(_batch, _pixel, r, c, t);
    private void Text(string s, int x, int y, int size, Color c) => _r.DrawText(s, x, y, size, c);

    private void DrawButton(Rectangle r, string label, Color fill)
    {
        _batch.Draw(_pixel, r, fill);
        Border(r, new Color(198, 198, 206, 195), 1);
        var sz = _r.MeasureText(label, FsBtn);
        _r.DrawText(label, (int)(r.X + (r.Width - sz.X) / 2f), (int)(r.Y + (r.Height - sz.Y) / 2f), FsBtn, new Color(232, 232, 236));
    }
}
