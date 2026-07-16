using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Necroking.UI;

// Button identities for the pause/main menu layouts (PauseMenuScreen /
// MainMenuScreen BuildLayout). The click handlers switch on these instead of
// re-deriving button positions.
internal enum MenuButtonId
{
    // Pause menu
    Resume, SaveGame, LoadGame, UnitEditor, SpellEditor, MapEditor,
    UIEditor, ItemEditor, Settings, Multiplayer, ToMainMenu, Quit,
    // Main menu (LoadGame/Quit shared with the pause menu)
    Continue, Play, PlayTestMap, Scenarios,
}

// One resolved menu button: identity, label and rect. Each screen's Draw and
// click handler both consume the same array, so they can't drift apart.
internal struct MenuButton
{
    public MenuButtonId Id;
    public string Label;
    public Rectangle Rect;
    public bool Interactable;
}

/// <summary>Shared drawing vocabulary of the full-screen menu family
/// (MainMenuScreen, PauseMenuScreen, ScenarioListScreen — plus
/// GameRenderer.DrawSaveGameText, which reuses the button-text style): the menu
/// button, backdrop, panel and scrollbar. One canonical implementation — the
/// screens differ only in layout, not look.</summary>
internal static class MenuDraw
{
    private static Game1 G => Game1.Instance;

    public static void Text(SpriteFont? font, string text, Vector2 pos, Color color, float scale = 1f)
    {
        if (font != null)
            G.Scope.DrawString(font, text, pos, color, 0f, Vector2.Zero, scale, SpriteEffects.None, 0f);
    }

    public static Vector2 TextAt(string text, int x, int y, int w, int h, Vector2 pivot, SpriteFont font, Color color)
    {
        var textSize = font.MeasureString(text);
        Text(font, text,
            new Vector2((int)(x + (w - textSize.X) * pivot.X), (int)(y + (h - textSize.Y) * pivot.Y)),
            color);
        return textSize;
    }

    public static void ButtonText(string text, int x, int y, int w, int h, Vector2? pivot = null, Color? color = null)
    {
        if (G._font == null) return;
        var p = pivot ?? new Vector2(0.5f, 0.5f);
        // Shrink overly long labels (e.g. scenario names) to fit inside the cell.
        var textSize = G._font.MeasureString(text);
        float scale = textSize.X > w - 12 ? (w - 12) / textSize.X : 1f;
        Text(G._font, text,
            new Vector2((int)(x + (w - textSize.X * scale) * p.X), (int)(y + (h - textSize.Y * scale) * p.Y)),
            color ?? new Color(255, 245, 220), scale);
    }

    // Draws a menu button at an absolute position (grid-friendly; no advancing cursor).
    public static void Button(string text, int x, int y, int w, int h, bool interactable = true)
    {
        int mx = (int)G._input.MousePos.X, my = (int)G._input.MousePos.Y;
        bool hover = mx >= x && mx < x + w && my >= y && my < y + h;
        Color bg = hover ? new Color(90, 60, 120, 240) : new Color(60, 40, 80, 220);
        G.Scope.Draw(G._pixel, new Rectangle(x, y, w, h), interactable ? bg : new Color(108, 88, 128));
        G.Scope.Draw(G._pixel, new Rectangle(x, y, w, 2),
            interactable ? new Color(220, 180, 100, hover ? 255 : 120) : new Color(180, 140, 100, 120));
        G.Scope.Draw(G._pixel, new Rectangle(x, y + h - 2, w, 2),
            interactable ? new Color(220, 180, 100, hover ? 255 : 60) : new Color(180, 140, 100, 60));
        ButtonText(text, x, y, w, h, color: interactable ? null : new Color(192, 192, 192));
    }

    /// <summary>Filled panel with an accent bar at the top (or bottom) — the
    /// pause-menu box style.</summary>
    public static void Panel(Rectangle r, Color fill, Color accent, int accentH = 2, bool bottomAccent = false)
    {
        G.Scope.Draw(G._pixel, r, fill);
        if (bottomAccent)
            G.Scope.Draw(G._pixel, new Rectangle(r.X, r.Bottom - accentH, r.Width, accentH), accent);
        else
            G.Scope.Draw(G._pixel, new Rectangle(r.X, r.Y, r.Width, accentH), accent);
    }

    /// <summary>Menu background: cover-scale bg image (or fallback fill) + dark
    /// overlay for contrast. Every full-screen menu's first draw call.</summary>
    public static void Backdrop(int screenW, int screenH)
    {
        // Background image (scaled to fill, centered)
        if (G._mainMenuBg != null)
        {
            float bgScale = System.MathF.Max((float)screenW / G._mainMenuBg.Width,
                                             (float)screenH / G._mainMenuBg.Height);
            float bgW = G._mainMenuBg.Width * bgScale;
            float bgH = G._mainMenuBg.Height * bgScale;
            G.Scope.Draw(G._mainMenuBg,
                new Rectangle((int)((screenW - bgW) * 0.5f), (int)((screenH - bgH) * 0.5f),
                              (int)bgW, (int)bgH),
                Color.White);
        }
        else
        {
            G.Scope.Draw(G._pixel, new Rectangle(0, 0, screenW, screenH), new Color(20, 15, 30));
        }
        // Dark overlay for contrast
        G.Scope.Draw(G._pixel, new Rectangle(0, 0, screenW, screenH), new Color(0, 0, 0, 120));
    }

    // Draws a menu-family scrollbar using the shared VScrollbar geometry. The
    // thumb goes "hot" while hovered or being dragged (drag state lives on the
    // owning screen).
    public static void Scrollbar(int x, int y, int viewH, float contentH, float scrollPx, bool dragging)
    {
        if (VScrollbar.Fits(viewH, contentH)) return;

        var track = VScrollbar.TrackRect(x, y, viewH);
        var thumb = VScrollbar.ThumbRect(x, y, viewH, contentH, scrollPx);

        int mx = (int)G._input.MousePos.X, my = (int)G._input.MousePos.Y;
        bool overThumb = thumb.Contains(mx, my);
        bool hot = dragging || overThumb;

        G.Scope.Draw(G._pixel, track, VScrollbar.TrackColor);
        G.Scope.Draw(G._pixel, thumb, hot ? VScrollbar.ThumbHotColor : VScrollbar.ThumbColor);
    }
}
