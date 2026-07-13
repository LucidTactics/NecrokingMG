using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using Necroking.Render;

namespace Necroking.UI;

/// <summary>The full-screen load-game menu (MenuState.LoadMenu), reachable from
/// both the main menu and the pause menu — Game1._backMenuState decides where
/// Back/ESC returns. Scrollable save rows reuse the SaveGameWindow row/card
/// metrics and GameRenderer's save-preview widgets. Layout, drawing and input
/// live together — BuildLayout is the single source of truth Draw and Update
/// both consume. The save list and scroll offset stay on Game1
/// (_loadMenuSaves / _loadMenuScrollPx — dev commands poke them); the
/// scrollbar drag state lives here. Drawn from GameRenderer.Draw's LoadMenu
/// early-out; Update is called from the matching block in Game1.Update.</summary>
public sealed class LoadMenuScreen
{
    private readonly Game1 _g;
    internal LoadMenuScreen(Game1 g) { _g = g; }

    private bool _scrollDragging;     // dragging the scrollbar thumb
    private float _scrollGrabOffset;  // Y within the thumb where the drag started

    // Fully-resolved layout for one frame: one rect per save row (positioned for
    // the current scroll offset) plus the back button and the scrollbar geometry.
    internal struct View
    {
        public Rectangle[] RowRects; // aligned with _loadMenuSaves, scroll applied
        public Rectangle BackRect;
        public int RowStride;        // row height + gap, for wheel scrolling
        // Scrollbar column (pixel units, feeds the shared Necroking.UI.VScrollbar).
        public int ScrollX;
        public int ScrollY;
        public int ScrollViewH;
        public float ScrollContentH;

        /// <summary>Clamp a pixel scroll offset to the valid range for this layout.</summary>
        public float ClampScroll(float scrollPx) => Math.Clamp(scrollPx, 0f, Math.Max(0f, ScrollContentH - ScrollViewH));
    }

    private View BuildLayout(int screenW, int screenH, int saveCount, float scrollPx)
    {
        int btnW = SaveGameWindow.PanelW, btnH = SaveGameWindow.RowH, btnGap = 10;
        int stride = btnH + btnGap;
        int titleY = screenH / 20 + 20;
        int listY = titleY + 70;
        // Leave room for the back button + margin at the bottom.
        int maxRows = Math.Max(1, (screenH - 80 - listY) / stride);
        int visRows = Math.Min(saveCount, maxRows);
        int viewH = visRows * stride;
        int scroll = (int)Math.Round(scrollPx);

        var view = new View
        {
            RowRects = new Rectangle[saveCount],
            RowStride = stride,
            ScrollY = listY,
            ScrollViewH = viewH,
            ScrollContentH = saveCount * stride,
        };
        int x = (screenW - btnW) / 2;
        view.ScrollX = x + btnW + 8;
        for (int i = 0; i < saveCount; i++)
            view.RowRects[i] = new Rectangle(x, listY + i * stride - scroll, btnW, btnH);
        // Back button fixed just below the visible window so it never scrolls away.
        view.BackRect = new Rectangle((screenW - 200) / 2, listY + viewH + 14, 200, 45);
        return view;
    }

    /// <summary>Enter the load menu (from the main or pause menu): refresh the
    /// save list, reset the scroll, and remember where Back/ESC returns.</summary>
    public void Open()
    {
        _g._loadMenuSaves = Game1.ListSaveGames();
        _g._loadMenuScrollPx = 0f;
        _g._backMenuState = _g._menuState;
        _g._menuState = MenuState.LoadMenu;
    }

    /// <summary>Per-frame input (Game1.Update's LoadMenu block): ESC back,
    /// scrollbar drag, wheel scroll, row/back clicks — against the same layout
    /// Draw renders.</summary>
    public void Update()
    {
        var input = _g._input;

        // Escape to go back
        if (input.WasKeyPressed(Keys.Escape))
        {
            _g._menuState = _g._backMenuState;
            return;
        }

        int screenW = _g.GraphicsDevice.Viewport.Width;
        int screenH = _g.GraphicsDevice.Viewport.Height;
        int mx = (int)input.MousePos.X, my = (int)input.MousePos.Y;
        var view = BuildLayout(screenW, screenH, _g._loadMenuSaves.Count, _g._loadMenuScrollPx);

        // Draggable scrollbar — same behaviour as the scenario menu (shared
        // Necroking.UI.VScrollbar). The scroll offset is stored in pixels, so
        // the thumb math and drag conversion are both pixel-native.
        if (_scrollDragging && !input.LeftDown)
            _scrollDragging = false;

        bool hasBar = !VScrollbar.Fits(view.ScrollViewH, view.ScrollContentH);
        if (hasBar)
        {
            var thumb = VScrollbar.ThumbRect(view.ScrollX, view.ScrollY, view.ScrollViewH, view.ScrollContentH, _g._loadMenuScrollPx);
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
                _g._loadMenuScrollPx = view.ClampScroll(newPx);
            }
        }

        // Wheel scroll (half a row per notch), clamped to the layout height.
        if (!_scrollDragging && input.ScrollDelta != 0)
        {
            _g._loadMenuScrollPx += (input.ScrollDelta > 0 ? -1 : 1) * view.RowStride * 0.5f;
            _g._loadMenuScrollPx = view.ClampScroll(_g._loadMenuScrollPx);
        }

        // Row / back-button clicks (skipped while dragging the scrollbar).
        if (input.LeftPressed && !_scrollDragging)
        {
            // Only clicks inside the scrolled list window count — a partially
            // clipped bottom row must not be clickable in the gap below it.
            bool inListWindow = my >= view.ScrollY && my < view.ScrollY + view.ScrollViewH;
            if (inListWindow)
            {
                for (int i = 0; i < view.RowRects.Length; i++)
                {
                    if (!view.RowRects[i].Contains(mx, my)) continue;
                    // On success LoadSaveGame has switched _menuState to None; on a
                    // validation failure (logged) we simply stay on this menu.
                    _g.LoadSaveGame(_g._loadMenuSaves[i].Name);
                    break;
                }
            }

            if (_g._menuState == MenuState.LoadMenu && view.BackRect.Contains(mx, my))
                _g._menuState = _g._backMenuState;
        }
    }

    public void Draw(int screenW, int screenH)
    {
        MenuDraw.Backdrop(screenW, screenH);

        int titleY = screenH / 20 + 20;
        if (_g._largeFont != null)
        {
            string title = "LOAD GAME";
            var titleSize = _g._largeFont.MeasureString(title);
            MenuDraw.Text(_g._largeFont, title, new Vector2(screenW / 2f - titleSize.X / 2f + 3, titleY + 3), new Color(0, 0, 0, 180));
            MenuDraw.Text(_g._largeFont, title, new Vector2(screenW / 2f - titleSize.X / 2f, titleY), new Color(220, 180, 100));
        }

        var saves = _g._loadMenuSaves;
        var view = BuildLayout(screenW, screenH, saves.Count, _g._loadMenuScrollPx);

        if (saves.Count == 0 && _g._font != null)
        {
            string none = "(no saves found)";
            var size = _g._font.MeasureString(none);
            MenuDraw.Text(_g._font, none, new Vector2((int)(screenW / 2f - size.X / 2f), titleY + 80), new Color(140, 140, 160));
        }

        if (saves.Count > 0)
        {
            // Clip the rows to their scroll window so partially-scrolled rows are
            // cut at the edges (smooth sub-row scroll) rather than spilling into
            // the title / back-button gaps — same mechanism as the scenario grid.
            var device = _g.Scope.GraphicsDevice;
            var prevScissor = device.ScissorRectangle;
            device.ScissorRectangle = new Rectangle(0, view.ScrollY, screenW, view.ScrollViewH);
            _g.Scope.PushMaterial(Materials.HudScissor);

            for (int i = 0; i < view.RowRects.Length; i++)
            {
                var r = view.RowRects[i];
                if (r.Bottom <= view.ScrollY || r.Y >= view.ScrollY + view.ScrollViewH) continue;
                var s = saves[i];
                MenuDraw.Button("", r.X, r.Y, r.Width, r.Height);
                _g._gameRenderer.DrawSavePreviewCard(new Rectangle(r.X + 4, r.Y + 4, SaveGameWindow.CardW, SaveGameWindow.RowH - 8), s.FormId, s.SpellBar, s.Inventory);

                _g._gameRenderer.DrawSaveGameText(new(r.X + SaveGameWindow.CardW + 2, r.Y, r.Width - (SaveGameWindow.CardW + 2), r.Height), s);
            }

            _g.Scope.PopMaterial();
            device.ScissorRectangle = prevScissor;
        }

        MenuDraw.Button("< Back", view.BackRect.X, view.BackRect.Y, view.BackRect.Width, view.BackRect.Height);

        // Scrollbar — canonical look, draw only; input lives in Update above.
        MenuDraw.Scrollbar(view.ScrollX, view.ScrollY, view.ScrollViewH, view.ScrollContentH,
            _g._loadMenuScrollPx, _scrollDragging);
    }
}
