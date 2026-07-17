using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Necroking.Core;
using Necroking.Game.Jobs;

namespace Necroking.UI;

/// <summary>
/// Click an Empty Grave → assign an unassigned humanoid undead into it (making it
/// a worker) or unassign the housed one. Primitive-drawn modal layer following the
/// TableCraftMenuUI pattern.
/// </summary>
public class GraveRosterUI : IModalLayer
{
    private Render.SpriteScope Scope => Game1.Instance.Scope;  // straight-alpha draw surface
    private Texture2D _pixel = null!;
    private RuntimeWidgetRenderer _r = null!;
    private WorkerSystem _ws = null!;
    private const int FsTitle = 18, FsBody = 14, FsSmall = 12, FsBtn = 13;

    private bool _visible;
    private int _graveObjIdx = -1;
    private int _x, _y, _w, _h;
    private int _scroll;

    private const int RowH = 26;
    private const int Pad = 12;
    private const int TitleH = 26;
    private const int MaxVisibleRows = 8;

    public bool IsVisible => _visible;

    public void Init(Texture2D pixel, RuntimeWidgetRenderer renderer, WorkerSystem ws)
    {
        _pixel = pixel; _r = renderer; _ws = ws;
    }

    public void OpenForGrave(int graveObjIdx, int screenW, int screenH)
    {
        _graveObjIdx = graveObjIdx;
        _visible = true;
        _scroll = 0;
        _w = 300;
        _h = TitleH + Pad + RowH + 10 + 18 + MaxVisibleRows * RowH + Pad;
        _x = screenW / 2 - _w / 2;
        _y = screenH / 2 - _h / 2;
    }

    public void Close()
    {
        _visible = false;
        _graveObjIdx = -1;
    }

    private Rectangle CloseRect => new(_x + _w - 22, _y + 4, 18, 18);
    private Rectangle HousedActionRect => new(_x + _w - Pad - 78, _y + TitleH + Pad, 78, RowH - 4);
    private int ListTop => _y + TitleH + Pad + RowH + 10 + 18;
    private Rectangle RowActionRect(int screenY) => new(_x + _w - Pad - 64, screenY + 2, 64, RowH - 6);

    public void Update(InputState input)
    {
        if (!_visible || _graveObjIdx < 0) return;
        int mx = (int)input.MousePos.X, my = (int)input.MousePos.Y;

        if (input.LeftPressed && CloseRect.Contains(mx, my)) { Close(); return; }

        // Housed worker action (unassign).
        var housed = _ws.HousedWorker(_graveObjIdx);
        if (housed != null && input.LeftPressed && HousedActionRect.Contains(mx, my))
        {
            _ws.UnassignWorker(housed.Value.Id);
            return;
        }

        // Scroll.
        var candidates = _ws.UnassignedWorkers();
        int maxScroll = System.Math.Max(0, candidates.Count - MaxVisibleRows);
        if (input.ScrollDelta != 0 && ContainsMouse(mx, my))
            _scroll = System.Math.Clamp(_scroll + (input.ScrollDelta > 0 ? -1 : 1), 0, maxScroll);

        // Assign clicks.
        if (input.LeftPressed)
        {
            for (int r = 0; r < MaxVisibleRows; r++)
            {
                int idx = _scroll + r;
                if (idx >= candidates.Count) break;
                int rowY = ListTop + r * RowH;
                if (RowActionRect(rowY).Contains(mx, my))
                {
                    _ws.AssignWorker(candidates[idx].Id, _graveObjIdx);
                    return;
                }
            }
        }
    }

    public void Draw()
    {
        if (!_visible || _graveObjIdx < 0) return;
        var panel = new Rectangle(_x, _y, _w, _h);
        Scope.Draw(_pixel, panel, new Color(22, 20, 26, 235));
        Border(panel, new Color(150, 140, 160, 220), 2);

        Text("Empty Grave", _x + Pad, _y + 6, FsTitle, new Color(228, 222, 232));
        DrawButton(CloseRect, "x", new Color(80, 50, 50, 230));

        // Housed worker row.
        var housed = _ws.HousedWorker(_graveObjIdx);
        int hy = _y + TitleH + Pad;
        Text("Housed:", _x + Pad, hy + 5, FsBody, new Color(172, 167, 182));
        if (housed != null)
        {
            Text(housed.Value.Name, _x + Pad + 66, hy + 5, FsBody, new Color(222, 222, 227));
            DrawButton(HousedActionRect, "Unassign", new Color(96, 58, 58, 235));
        }
        else Text("(empty)", _x + Pad + 66, hy + 5, FsBody, new Color(142, 137, 147));

        // Divider + label.
        int dy = hy + RowH + 6;
        Scope.Draw(_pixel, new Rectangle(_x + Pad, dy, _w - 2 * Pad, 1), new Color(90, 85, 95, 200));
        Text("Assign humanoid undead:", _x + Pad, dy + 7, FsSmall, new Color(172, 167, 182));

        // Candidate list.
        var candidates = _ws.UnassignedWorkers();
        for (int r = 0; r < MaxVisibleRows; r++)
        {
            int idx = _scroll + r;
            if (idx >= candidates.Count) break;
            int rowY = ListTop + r * RowH;
            var rowRect = new Rectangle(_x + Pad, rowY, _w - 2 * Pad, RowH - 4);
            Scope.Draw(_pixel, rowRect, new Color(38, 36, 44, 220));
            Border(rowRect, new Color(70, 66, 80, 160), 1);
            Text(candidates[idx].Name, _x + Pad + 8, rowY + 5, FsBody, new Color(218, 218, 223));
            DrawButton(RowActionRect(rowY), "Assign", new Color(58, 84, 64, 235));
        }
        if (candidates.Count == 0)
            Text("(none available)", _x + Pad + 8, ListTop + 5, FsBody, new Color(142, 137, 147));
    }

    // IModalLayer
    public bool ContainsMouse(int mx, int my) => _visible && new Rectangle(_x, _y, _w, _h).Contains(mx, my);
    public Rectangle? HitBounds(int screenW, int screenH) => _visible ? new Rectangle(_x, _y, _w, _h) : null;
    public void OnCancel() => Close();
    public bool LightDismiss => true;
    public bool IsBlocking => false;

    // helpers
    private void Border(Rectangle r, Color c, int t) => Render.DrawUtils.DrawRectBorder(Scope, _pixel, r, c, t);

    private void DrawButton(Rectangle r, string label, Color fill)
    {
        Scope.Draw(_pixel, r, fill);
        Border(r, new Color(206, 206, 214, 200), 1);
        var sz = _r.MeasureText(label, FsBtn);
        _r.DrawText(label, (int)(r.X + (r.Width - sz.X) / 2f), (int)(r.Y + (r.Height - sz.Y) / 2f), FsBtn, new Color(232, 232, 236));
    }

    private void Text(string s, int x, int y, int size, Color c) => _r.DrawText(s, x, y, size, c);
}
