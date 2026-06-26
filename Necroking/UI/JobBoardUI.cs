using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Necroking.Core;
using Necroking.Game.Jobs;

namespace Necroking.UI;

/// <summary>
/// The job board: priority-ordered tiles (hidden until their building exists),
/// each showing workers/cap + a storage fill bar. Drag a tile (or use ▲▼) to
/// reorder priority; click the badge to expand a detail panel with the worker-cap
/// stepper and, for multi-output jobs, the maintain-stock target steppers.
/// Primitive-drawn modal layer (TableCraftMenuUI pattern). Toggle with the 'B'... 'J' key.
/// </summary>
public class JobBoardUI : IModalLayer
{
    private SpriteBatch _batch = null!;
    private Texture2D _pixel = null!;
    private SpriteFont? _font;
    private WorkerSystem _ws = null!;

    private bool _visible;
    private int _x, _y, _w, _h;
    private string _expanded = "";

    // drag state
    private bool _dragging;
    private int _dragVisibleIdx = -1;
    private int _dragMouseY;

    private const int Pad = 12;
    private const int TitleH = 28;
    private const int TileH = 50;
    private const int Gap = 6;
    private const int BtnW = 22;
    private const int BarH = 6;

    public bool IsVisible => _visible;

    public void Init(SpriteBatch batch, Texture2D pixel, SpriteFont? font, WorkerSystem ws)
    { _batch = batch; _pixel = pixel; _font = font; _ws = ws; }

    public void Toggle(int screenW, int screenH)
    {
        if (_visible) { Close(); return; }
        _visible = true;
        _w = 380;
        _x = 40;
        _y = 70;
        _h = screenH - 140;
        Game1.Popups.Push(this);
    }

    public void Close() { _visible = false; _dragging = false; Game1.Popups.Pop(this); }

    /// <summary>Expand a job's detail panel (also used by dev tooling for screenshots).</summary>
    public void Expand(string jobId) => _expanded = jobId;

    private struct OutRow { public string Id; public Rectangle Row, Minus, Plus, Up, Down; }
    private struct Tile
    {
        public JobState Js; public int VisibleIdx;
        public Rectangle Box, Drag, Badge, Up, Down, Bar;
        public bool Expanded; public Rectangle Detail, CapMinus, CapPlus;
        public List<OutRow> Outs;
    }

    private List<Tile> BuildLayout()
    {
        var tiles = new List<Tile>();
        int y = _y + TitleH + Pad;
        int vi = 0;
        foreach (var js in _ws.Jobs)
        {
            if (_ws.DerivedMax(js.Def) <= 0) continue; // hidden: no building
            var t = new Tile { Js = js, VisibleIdx = vi, Outs = new List<OutRow>() };
            int innerX = _x + Pad, innerW = _w - 2 * Pad;
            t.Box = new Rectangle(innerX, y, innerW, TileH);
            t.Drag = new Rectangle(innerX, y, 16, TileH);
            t.Badge = new Rectangle(innerX + innerW - 86, y + 6, 86, 22);
            t.Up = new Rectangle(innerX + innerW - 86 - 2 - BtnW, y + 6, BtnW, 16);
            t.Down = new Rectangle(innerX + innerW - 86 - 2 - BtnW, y + 24, BtnW, 16);
            t.Bar = new Rectangle(innerX + 22, y + TileH - 14, innerW - 120, BarH);
            t.Expanded = js.Def.Id == _expanded;
            y += TileH;

            if (t.Expanded)
            {
                int dh = 30 + (js.Def.OutputChoice ? 20 + js.Def.Outputs.Count * 24 : 0) + 8;
                t.Detail = new Rectangle(innerX, y, innerW, dh);
                t.CapMinus = new Rectangle(innerX + 120, y + 5, 22, 20);
                t.CapPlus = new Rectangle(innerX + 178, y + 5, 22, 20);
                if (js.Def.OutputChoice)
                {
                    int oy = y + 30 + 18;
                    var ordered = OrderedOutputs(js);
                    foreach (var oid in ordered)
                    {
                        var row = new Rectangle(innerX + 6, oy, innerW - 12, 22);
                        tiles_AddOut(ref t, oid,
                            row,
                            new Rectangle(innerX + innerW - 150, oy + 1, 20, 18),
                            new Rectangle(innerX + innerW - 98, oy + 1, 20, 18),
                            new Rectangle(innerX + innerW - 64, oy + 1, 18, 18),
                            new Rectangle(innerX + innerW - 42, oy + 1, 18, 18));
                        oy += 24;
                    }
                }
                y += dh;
            }
            y += Gap;
            tiles.Add(t);
            vi++;
        }
        _h = (y - _y) + Pad;
        return tiles;
    }

    private static void tiles_AddOut(ref Tile t, string id, Rectangle row, Rectangle minus, Rectangle plus, Rectangle up, Rectangle down)
        => t.Outs.Add(new OutRow { Id = id, Row = row, Minus = minus, Plus = plus, Up = up, Down = down });

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

    public void Update(InputState input)
    {
        if (!_visible) return;
        int mx = (int)input.MousePos.X, my = (int)input.MousePos.Y;
        var tiles = BuildLayout();

        // Close button.
        if (input.LeftPressed && new Rectangle(_x + _w - 24, _y + 5, 18, 18).Contains(mx, my)) { Close(); return; }

        // Drag handling.
        if (_dragging)
        {
            if (!input.LeftDown)
            {
                // Drop: find target visible index by Y.
                int target = tiles.Count - 1;
                for (int i = 0; i < tiles.Count; i++)
                    if (my < tiles[i].Box.Y + tiles[i].Box.Height / 2) { target = i; break; }
                ReorderVisible(tiles, _dragVisibleIdx, target);
                _dragging = false; _dragVisibleIdx = -1;
            }
            return;
        }

        if (!input.LeftPressed)
        {
            // scroll not needed (panel fits); start drag on press handled below
            return;
        }

        foreach (var t in tiles)
        {
            // Reorder buttons.
            if (t.Up.Contains(mx, my)) { ReorderVisible(tiles, t.VisibleIdx, t.VisibleIdx - 1); return; }
            if (t.Down.Contains(mx, my)) { ReorderVisible(tiles, t.VisibleIdx, t.VisibleIdx + 1); return; }
            // Expand toggle.
            if (t.Badge.Contains(mx, my)) { _expanded = t.Expanded ? "" : t.Js.Def.Id; return; }
            // Cap steppers (expanded).
            if (t.Expanded)
            {
                int eff = _ws.EffectiveCap(t.Js), dmax = _ws.DerivedMax(t.Js.Def);
                if (t.CapMinus.Contains(mx, my)) { _ws.SetCap(t.Js, System.Math.Max(0, eff - 1)); return; }
                if (t.CapPlus.Contains(mx, my)) { _ws.SetCap(t.Js, System.Math.Min(dmax, eff + 1)); return; }
                foreach (var o in t.Outs)
                {
                    var cur = t.Js.OutputTargets.TryGetValue(o.Id, out var ot) ? ot : new OutputTarget();
                    if (o.Minus.Contains(mx, my)) { ot.TargetStock = System.Math.Max(0, cur.TargetStock - 1); t.Js.OutputTargets[o.Id] = ot; return; }
                    if (o.Plus.Contains(mx, my)) { ot.TargetStock = System.Math.Min(99, cur.TargetStock + 1); t.Js.OutputTargets[o.Id] = ot; return; }
                    if (o.Up.Contains(mx, my)) { SwapOutputPriority(t.Js, o.Id, -1); return; }
                    if (o.Down.Contains(mx, my)) { SwapOutputPriority(t.Js, o.Id, +1); return; }
                }
            }
            // Begin drag on the tile body / handle.
            if (t.Drag.Contains(mx, my) || (t.Box.Contains(mx, my) && my < t.Box.Y + 28))
            { _dragging = true; _dragVisibleIdx = t.VisibleIdx; _dragMouseY = my; return; }
        }
    }

    private void ReorderVisible(List<Tile> tiles, int fromVisible, int toVisible)
    {
        if (fromVisible < 0 || fromVisible >= tiles.Count) return;
        toVisible = System.Math.Clamp(toVisible, 0, tiles.Count - 1);
        if (toVisible == fromVisible) return;
        // Map visible indices to absolute job-list indices.
        int fromAbs = AbsIndex(tiles[fromVisible].Js);
        int toAbs = AbsIndex(tiles[toVisible].Js);
        _ws.MoveJob(fromAbs, toAbs);
    }

    private int AbsIndex(JobState js)
    {
        for (int i = 0; i < _ws.Jobs.Count; i++) if (_ws.Jobs[i] == js) return i;
        return 0;
    }

    private void SwapOutputPriority(JobState js, string id, int dir)
    {
        var ordered = OrderedOutputs(js);
        int p = ordered.IndexOf(id);
        int q = p + dir;
        if (q < 0 || q >= ordered.Count) return;
        string other = ordered[q];
        var a = js.OutputTargets.TryGetValue(id, out var ta) ? ta : new OutputTarget();
        var b = js.OutputTargets.TryGetValue(other, out var tb) ? tb : new OutputTarget();
        (a.Priority, b.Priority) = (b.Priority, a.Priority);
        js.OutputTargets[id] = a; js.OutputTargets[other] = b;
    }

    public void Draw()
    {
        if (!_visible) return;
        var tiles = BuildLayout();
        var panel = new Rectangle(_x, _y, _w, _h);
        _batch.Draw(_pixel, panel, new Color(20, 18, 24, 235));
        Border(panel, new Color(150, 140, 160, 220), 2);
        DrawText("Jobs", _x + Pad, _y + 7, new Color(228, 222, 232), 1.05f);
        DrawText("top = highest priority", _x + _w - 168, _y + 11, new Color(140, 135, 148), 0.7f);
        DrawButton(new Rectangle(_x + _w - 24, _y + 5, 18, 18), "x", new Color(80, 50, 50, 230));

        if (tiles.Count == 0)
            DrawText("No job buildings yet. Build a pile or table.", _x + Pad, _y + TitleH + Pad, new Color(150, 145, 155), 0.8f);

        foreach (var t in tiles)
        {
            var js = t.Js; var def = js.Def;
            bool full = _ws.IsStorageFull(def);
            int assigned = js.AssignedWorkers.Count;
            int eff = _ws.EffectiveCap(js);

            _batch.Draw(_pixel, t.Box, t.Expanded ? new Color(44, 40, 52, 235) : new Color(34, 32, 40, 230));
            Border(t.Box, new Color(90, 85, 100, 200), 1);
            // drag handle dots
            DrawText(":::", t.Box.X + 3, t.Box.Y + TileH / 2 - 7, new Color(120, 115, 130), 0.7f);
            DrawText($"{t.VisibleIdx + 1}. {def.DisplayName}", t.Box.X + 22, t.Box.Y + 6, new Color(225, 222, 230), 0.9f);

            // storage / population bar
            var (cur, max) = _ws.JobStorage(def);
            _batch.Draw(_pixel, t.Bar, new Color(15, 14, 18, 230));
            if (max > 0)
            {
                float f = System.Math.Clamp(cur / (float)max, 0f, 1f);
                var fillCol = full ? new Color(200, 80, 70) : (def.SpawnsUnit ? new Color(150, 110, 200) : new Color(90, 170, 120));
                _batch.Draw(_pixel, new Rectangle(t.Bar.X, t.Bar.Y, (int)(t.Bar.Width * f), t.Bar.Height), fillCol);
            }
            DrawText($"{cur}/{(max > 0 ? max.ToString() : "-")}{(full ? "  FULL" : "")}",
                t.Bar.X + t.Bar.Width + 6, t.Bar.Y - 4, full ? new Color(220, 120, 110) : new Color(160, 156, 168), 0.7f);

            // reorder buttons
            DrawButton(t.Up, "^", new Color(50, 48, 60, 230));
            DrawButton(t.Down, "v", new Color(50, 48, 60, 230));
            // badge: assigned / cap
            var badgeCol = assigned > 0 ? new Color(56, 78, 60, 235) : new Color(48, 46, 56, 230);
            DrawButton(t.Badge, $"{assigned} / {eff}", badgeCol);

            if (t.Expanded) DrawDetail(t);
        }

        // dragged ghost
        if (_dragging && _dragVisibleIdx >= 0 && _dragVisibleIdx < tiles.Count)
        {
            var ghost = new Rectangle(_x + Pad, _dragMouseY - 12, _w - 2 * Pad, 24);
            _batch.Draw(_pixel, ghost, new Color(80, 110, 160, 120));
        }
    }

    private void DrawDetail(Tile t)
    {
        var js = t.Js; var def = js.Def;
        _batch.Draw(_pixel, t.Detail, new Color(28, 26, 34, 235));
        Border(t.Detail, new Color(80, 76, 92, 180), 1);
        int dmax = _ws.DerivedMax(def);
        DrawText("Workers", t.Detail.X + 8, t.Detail.Y + 8, new Color(180, 176, 188), 0.8f);
        DrawButton(t.CapMinus, "-", new Color(60, 50, 50, 230));
        DrawText($"{_ws.EffectiveCap(js)}", t.CapMinus.Right + 14, t.Detail.Y + 7, new Color(225, 225, 230), 0.95f);
        DrawButton(t.CapPlus, "+", new Color(50, 60, 50, 230));
        DrawText($"max {dmax}", t.CapPlus.Right + 10, t.Detail.Y + 9, new Color(150, 146, 158), 0.72f);

        if (!string.IsNullOrEmpty(def.RequiredCapability))
            DrawText($"requires: {def.RequiredCapability}", t.Detail.X + 8, t.Detail.Y + 30, new Color(170, 150, 120), 0.72f);

        if (def.OutputChoice)
        {
            DrawText("Keep in stock:", t.Detail.X + 8, t.Detail.Y + 32, new Color(170, 166, 178), 0.75f);
            int host = _ws.FindHostBuilding(def, default);
            foreach (var o in t.Outs)
            {
                var outDef = def.Outputs.Find(x => x.Id == o.Id);
                string name = string.IsNullOrEmpty(outDef.DisplayName) ? o.Id : outDef.DisplayName;
                int have = host >= 0 ? _ws.StoredOf(host, o.Id) : 0;
                int tgt = js.OutputTargets.TryGetValue(o.Id, out var ot) ? ot.TargetStock : 0;
                DrawText(name, o.Row.X + 4, o.Row.Y + 3, new Color(218, 216, 224), 0.78f);
                DrawText($"have {have}", o.Row.X + 150, o.Row.Y + 4, new Color(150, 146, 158), 0.7f);
                DrawButton(o.Minus, "-", new Color(60, 50, 50, 230));
                DrawText($"{tgt}", o.Minus.Right + 6, o.Row.Y + 3, new Color(225, 225, 230), 0.8f);
                DrawButton(o.Plus, "+", new Color(50, 60, 50, 230));
                DrawButton(o.Up, "^", new Color(50, 48, 60, 230));
                DrawButton(o.Down, "v", new Color(50, 48, 60, 230));
            }
        }
    }

    // IModalLayer
    public bool ContainsMouse(int mx, int my) => _visible && new Rectangle(_x, _y, _w, _h).Contains(mx, my);
    public void OnCancel() => Close();
    public bool LightDismiss => false;
    public bool IsBlocking => false;

    private void Border(Rectangle r, Color c, int t) => Render.DrawUtils.DrawRectBorder(_batch, _pixel, r, c, t);

    private void DrawButton(Rectangle r, string label, Color fill)
    {
        _batch.Draw(_pixel, r, fill);
        Border(r, new Color(200, 200, 200, 190), 1);
        if (_font == null) return;
        var size = _font.MeasureString(label);
        float s = System.Math.Min(0.8f, (r.Width - 4) / System.Math.Max(1f, size.X));
        var pos = new Vector2((int)(r.X + (r.Width - size.X * s) / 2f), (int)(r.Y + (r.Height - size.Y * s) / 2f));
        _batch.DrawString(_font, label, pos, new Color(230, 230, 230), 0f, Vector2.Zero, s, SpriteEffects.None, 0f);
    }

    private void DrawText(string text, int x, int y, Color c, float scale)
    {
        if (_font == null) return;
        _batch.DrawString(_font, text, new Vector2((int)x, (int)y), c, 0f, Vector2.Zero, scale, SpriteEffects.None, 0f);
    }
}
