using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using Necroking.Render;
using Necroking.Scenario;

namespace Necroking.UI;

/// <summary>The full-screen scenario picker (MenuState.ScenarioList): a
/// categorized, scrollable grid of every registered scenario. Layout, drawing
/// and input live together — BuildLayout is the single source of truth Draw and
/// Update both consume. The scroll offset stays on Game1 (_scenarioScrollPx —
/// dev commands poke it); the scrollbar drag state lives here. Drawn from
/// GameRenderer.Draw's ScenarioList early-out; Update is called from the
/// matching block in Game1.Update.</summary>
public sealed class ScenarioListScreen
{
    private readonly Game1 _g;
    internal ScenarioListScreen(Game1 g) { _g = g; }

    private bool _scrollDragging;     // dragging the scrollbar thumb
    private float _scrollGrabOffset;  // Y within the thumb where the drag started

    // A single laid-out element (category header or scenario button), already
    // positioned on screen for the current scroll offset.
    internal struct Entry
    {
        public bool IsHeader;
        public string Text;     // category title (header) or scenario name (button)
        public Rectangle Rect;  // on-screen bounds
        public bool Visible;    // within the current scroll window
    }

    // Fully-resolved layout for one frame: the positioned entries, the back
    // button, and the scrollbar geometry. The scrollbar values are in pixels so
    // they feed the shared Necroking.UI.VScrollbar helper directly.
    internal struct View
    {
        public List<Entry> Entries;
        public int TotalRows;
        public int RowsVisible;
        public int RowStride;
        public Rectangle BackRect;
        // Scrollbar column (pixel units): left edge X, top Y, visible height, and
        // total content height. ScrollContentH <= ScrollViewH means "fits, no bar".
        public int ScrollX;
        public int ScrollY;
        public int ScrollViewH;
        public float ScrollContentH;

        /// <summary>Clamp a pixel scroll offset to the valid range for this layout.</summary>
        public float ClampScroll(float scrollPx) => System.Math.Clamp(scrollPx, 0f, System.Math.Max(0f, ScrollContentH - ScrollViewH));
    }

    // Shared grid metrics so BuildLayout's rows and the scroll window agree.
    private static void GetGridLayout(int screenW, int screenH, out int cols, out int btnW, out int btnH, out int btnGap, out int gridX, out int menuY, out int rowsVisible)
    {
        cols = 5;
        btnGap = 12;
        btnH = 45;
        menuY = screenH / 20 + 80;
        int maxGridW = System.Math.Min(screenW - 80, 1400);
        btnW = (maxGridW - (cols - 1) * btnGap) / cols;
        int gridW = cols * btnW + (cols - 1) * btnGap;
        gridX = screenW / 2 - gridW / 2;
        // Leave room at the bottom for the back button + scroll hint.
        rowsVisible = System.Math.Max(1, (screenH - menuY - 110) / (btnH + btnGap));
    }

    // Builds the categorized layout for a given scroll offset (in pixels —
    // smooth, sub-row). Each category emits a header row followed by its
    // scenario buttons packed into `cols`-wide rows; `scrollPx` slides the whole
    // layout vertically. Entries partially inside the window are Visible (they're
    // scissor-clipped at draw time); ClampScroll keeps the last row reachable.
    private View BuildLayout(int screenW, int screenH, float scrollPx)
    {
        GetGridLayout(screenW, screenH, out int cols, out int btnW, out int btnH, out int btnGap,
            out int gridX, out int menuY, out int rowsVisible);
        int rowStride = btnH + btnGap;
        int gridW = cols * btnW + (cols - 1) * btnGap;
        int viewH = rowsVisible * rowStride;
        int scroll = (int)System.Math.Round(scrollPx);

        var entries = new List<Entry>();
        int layoutRow = 0;

        foreach (var cat in ScenarioRegistry.GetCategories())
        {
            int hy = menuY + layoutRow * rowStride - scroll;
            entries.Add(new Entry
            {
                IsHeader = true,
                Text = cat,
                Rect = new Rectangle(gridX, hy, gridW, btnH),
                Visible = hy + btnH > menuY && hy < menuY + viewH,
            });
            layoutRow++;

            var scen = ScenarioRegistry.GetNamesInCategory(cat);
            for (int i = 0; i < scen.Count; i++)
            {
                int col = i % cols;
                int by = menuY + (layoutRow + i / cols) * rowStride - scroll;
                int bx = gridX + col * (btnW + btnGap);
                entries.Add(new Entry
                {
                    IsHeader = false,
                    Text = scen[i],
                    Rect = new Rectangle(bx, by, btnW, btnH),
                    Visible = by + btnH > menuY && by < menuY + viewH,
                });
            }
            layoutRow += (scen.Count + cols - 1) / cols;
        }

        // Back button fixed just below the visible window so it never scrolls away.
        int backW = 320;
        int backY = menuY + viewH + 10;

        return new View
        {
            Entries = entries,
            TotalRows = layoutRow,
            RowsVisible = rowsVisible,
            RowStride = rowStride,
            BackRect = new Rectangle(screenW / 2 - backW / 2, backY, backW, btnH),
            ScrollX = gridX + gridW + 8,
            ScrollY = menuY,
            ScrollViewH = viewH,
            ScrollContentH = layoutRow * rowStride,
        };
    }

    /// <summary>Per-frame input (Game1.Update's ScenarioList block): ESC back,
    /// scrollbar drag, wheel scroll, grid/back clicks — against the same layout
    /// Draw renders.</summary>
    public void Update()
    {
        var input = _g._input;

        // Escape to go back
        if (input.WasKeyPressed(Keys.Escape))
        {
            _g._menuState = MenuState.MainMenu;
            return;
        }

        int screenW = _g.GraphicsDevice.Viewport.Width;
        int screenH = _g.GraphicsDevice.Viewport.Height;
        int mx = (int)input.MousePos.X, my = (int)input.MousePos.Y;
        var view = BuildLayout(screenW, screenH, _g._scenarioScrollPx);

        // Draggable scrollbar — same behaviour as the editor panels (shared
        // Necroking.UI.VScrollbar). The scroll offset is stored in pixels, so the
        // thumb math and drag conversion are both pixel-native (smooth, sub-row).
        if (_scrollDragging && !input.LeftDown)
            _scrollDragging = false;

        bool hasBar = !VScrollbar.Fits(view.ScrollViewH, view.ScrollContentH);
        if (hasBar)
        {
            var thumb = VScrollbar.ThumbRect(view.ScrollX, view.ScrollY, view.ScrollViewH, view.ScrollContentH, _g._scenarioScrollPx);
            var hit = VScrollbar.HitRect(view.ScrollX, view.ScrollY, view.ScrollViewH);

            // Grab the thumb, or click the track to jump (thumb centres on the cursor).
            if (input.LeftPressed && hit.Contains(mx, my))
            {
                _scrollDragging = true;
                _scrollGrabOffset = thumb.Contains(mx, my) ? my - thumb.Y : thumb.Height / 2f;
            }

            if (_scrollDragging)
            {
                float newPx = VScrollbar.ScrollFromDrag(my, _scrollGrabOffset,
                    view.ScrollY, view.ScrollViewH, view.ScrollContentH);
                _g._scenarioScrollPx = view.ClampScroll(newPx);
            }
        }

        // Wheel scroll (half a layout row per notch), clamped to the layout height.
        if (!_scrollDragging && input.ScrollDelta != 0)
        {
            _g._scenarioScrollPx += (input.ScrollDelta > 0 ? -1 : 1) * view.RowStride * 0.5f;
            _g._scenarioScrollPx = view.ClampScroll(_g._scenarioScrollPx);
        }

        // Grid / back-button clicks (skipped while dragging the scrollbar).
        if (input.LeftPressed && !_scrollDragging)
        {
            // Only clicks inside the scrolled grid window count — a partially
            // clipped bottom row must not be clickable in the gap below it.
            bool inGridWindow = my >= view.ScrollY && my < view.ScrollY + view.ScrollViewH;
            foreach (var e in view.Entries)
            {
                if (!e.Visible || e.IsHeader || !inGridWindow) continue;
                if (e.Rect.Contains(mx, my))
                {
                    _g.StartScenario(e.Text);
                    return;
                }
            }

            // Back button (fixed below the visible grid)
            if (view.BackRect.Contains(mx, my))
                _g._menuState = MenuState.MainMenu;
        }
    }

    public void Draw(int screenW, int screenH)
    {
        MenuDraw.Backdrop(screenW, screenH);

        int titleY = screenH / 20 + 20;
        // Title
        if (_g._largeFont != null)
        {
            string title = "SCENARIOS";
            var titleSize = _g._largeFont.MeasureString(title);
            MenuDraw.Text(_g._largeFont, title, new Vector2(screenW / 2f - titleSize.X / 2f + 3, titleY + 3), new Color(0, 0, 0, 180));
            MenuDraw.Text(_g._largeFont, title, new Vector2(screenW / 2f - titleSize.X / 2f, titleY), new Color(180, 220, 100));
        }

        if (_g._font != null)
        {
            string subtitle = "Select a scenario to run";
            var subSize = _g._font.MeasureString(subtitle);
            MenuDraw.Text(_g._font, subtitle, new Vector2(screenW / 2f - subSize.X / 2f, titleY + 35), new Color(140, 140, 160));
        }

        // Categorized scenario grid (shared layout with the click handler).
        var view = BuildLayout(screenW, screenH, _g._scenarioScrollPx);

        // Clip the grid to its scroll window so partially-scrolled rows are cut at
        // the edges (smooth sub-row scroll) rather than spilling into the title /
        // back-button gaps. Scissor rect is device state; the HudScissor material
        // enables the scissor test (same mechanism as the editor panels).
        var device = _g.Scope.GraphicsDevice;
        var prevScissor = device.ScissorRectangle;
        device.ScissorRectangle = new Rectangle(0, view.ScrollY, screenW, view.ScrollViewH);
        _g.Scope.PushMaterial(Materials.HudScissor);

        foreach (var e in view.Entries)
        {
            if (!e.Visible) continue;
            if (e.IsHeader)
                DrawCategoryHeader(e.Text, e.Rect);
            else
                MenuDraw.Button(e.Text, e.Rect.X, e.Rect.Y, e.Rect.Width, e.Rect.Height);
        }

        _g.Scope.PopMaterial();
        device.ScissorRectangle = prevScissor;

        // Back button (centered below the visible grid)
        MenuDraw.Button("< Back", view.BackRect.X, view.BackRect.Y, view.BackRect.Width, view.BackRect.Height);

        // Scrollbar — same canonical look/behaviour as the editor panels (shared
        // Necroking.UI.VScrollbar geometry). Draw only; input lives in Update above.
        MenuDraw.Scrollbar(view.ScrollX, view.ScrollY, view.ScrollViewH, view.ScrollContentH,
            _g._scenarioScrollPx, _scrollDragging);
    }

    // Draws a category label as a section divider above its scenario buttons.
    private void DrawCategoryHeader(string text, Rectangle rect)
    {
        if (_g._font == null) return;
        var color = new Color(180, 220, 130);
        // Vertically bottom-align the label within the row so it hugs its buttons.
        var size = _g._font.MeasureString(text);
        int ty = rect.Y + (int)(rect.Height - size.Y);
        MenuDraw.Text(_g._font, text, new Vector2(rect.X + 2, ty), color);
        // Underline spanning the grid width.
        int lineY = rect.Y + rect.Height - 2;
        _g.Scope.Draw(_g._pixel, new Rectangle(rect.X, lineY, rect.Width, 1), new Color(180, 220, 130, 90));
    }
}
