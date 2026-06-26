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
    private SpriteBatch _batch = null!;
    private Texture2D _pixel = null!;
    private SpriteFont? _font;
    private WorkerSystem _ws = null!;

    private bool _visible;
    private int _graveObjIdx = -1;
    private int _x, _y, _w, _h;
    private int _scroll;

    private const int RowH = 26;
    private const int Pad = 12;
    private const int TitleH = 26;
    private const int MaxVisibleRows = 8;

    public bool IsVisible => _visible;

    public void Init(SpriteBatch batch, Texture2D pixel, SpriteFont? font, WorkerSystem ws)
    {
        _batch = batch; _pixel = pixel; _font = font; _ws = ws;
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
        Game1.Popups.Push(this);
    }

    public void Close()
    {
        _visible = false;
        _graveObjIdx = -1;
        Game1.Popups.Pop(this);
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
        _batch.Draw(_pixel, panel, new Color(22, 20, 26, 235));
        Border(panel, new Color(150, 140, 160, 220), 2);

        DrawText("Empty Grave", _x + Pad, _y + 6, new Color(225, 220, 230), 1f);
        DrawButton(CloseRect, "x", new Color(80, 50, 50, 230));

        // Housed worker row.
        var housed = _ws.HousedWorker(_graveObjIdx);
        int hy = _y + TitleH + Pad;
        DrawText("Housed:", _x + Pad, hy + 4, new Color(170, 165, 180), 0.85f);
        if (housed != null)
        {
            DrawText(housed.Value.Name, _x + Pad + 64, hy + 4, new Color(220, 220, 225), 0.85f);
            DrawButton(HousedActionRect, "Unassign", new Color(90, 55, 55, 230));
        }
        else DrawText("(empty)", _x + Pad + 64, hy + 4, new Color(140, 135, 145), 0.85f);

        // Divider + label.
        int dy = hy + RowH + 6;
        _batch.Draw(_pixel, new Rectangle(_x + Pad, dy, _w - 2 * Pad, 1), new Color(90, 85, 95, 200));
        DrawText("Assign humanoid undead:", _x + Pad, dy + 6, new Color(170, 165, 180), 0.8f);

        // Candidate list.
        var candidates = _ws.UnassignedWorkers();
        for (int r = 0; r < MaxVisibleRows; r++)
        {
            int idx = _scroll + r;
            if (idx >= candidates.Count) break;
            int rowY = ListTop + r * RowH;
            var rowRect = new Rectangle(_x + Pad, rowY, _w - 2 * Pad, RowH - 4);
            _batch.Draw(_pixel, rowRect, new Color(38, 36, 44, 220));
            DrawText(candidates[idx].Name, _x + Pad + 6, rowY + 4, new Color(215, 215, 220), 0.8f);
            DrawButton(RowActionRect(rowY), "Assign", new Color(55, 80, 60, 230));
        }
        if (candidates.Count == 0)
            DrawText("(none available)", _x + Pad + 6, ListTop + 4, new Color(140, 135, 145), 0.8f);
    }

    // IModalLayer
    public bool ContainsMouse(int mx, int my) => _visible && new Rectangle(_x, _y, _w, _h).Contains(mx, my);
    public void OnCancel() => Close();
    public bool LightDismiss => true;
    public bool IsBlocking => false;

    // helpers
    private void Border(Rectangle r, Color c, int t) => Render.DrawUtils.DrawRectBorder(_batch, _pixel, r, c, t);

    private void DrawButton(Rectangle r, string label, Color fill)
    {
        _batch.Draw(_pixel, r, fill);
        Border(r, new Color(210, 210, 210, 200), 1);
        if (_font == null) return;
        var size = _font.MeasureString(label);
        float s = System.Math.Min(0.8f, (r.Width - 6) / System.Math.Max(1f, size.X));
        var pos = new Vector2((int)(r.X + (r.Width - size.X * s) / 2f), (int)(r.Y + (r.Height - size.Y * s) / 2f));
        _batch.DrawString(_font, label, pos, new Color(230, 230, 230), 0f, Vector2.Zero, s, SpriteEffects.None, 0f);
    }

    private void DrawText(string text, int x, int y, Color c, float scale)
    {
        if (_font == null) return;
        _batch.DrawString(_font, text, new Vector2((int)x, (int)y), c, 0f, Vector2.Zero, scale, SpriteEffects.None, 0f);
    }
}
