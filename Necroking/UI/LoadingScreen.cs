using System;
using Microsoft.Xna.Framework;

namespace Necroking.UI;

/// <summary>The boot loading view (MenuState.Loading), shown before the main menu
/// while Game1.Loading.cs works through the startup step queue. Deliberately
/// low-resource: it needs only the SpriteBatch, _pixel and the SpriteFonts the
/// slimmed LoadContent provides — a backdrop, the current step's label centered
/// on screen, and a thin progress bar. The title matches MainMenuScreen's
/// placement so the handoff to the menu doesn't jump.</summary>
public sealed class LoadingScreen
{
    private readonly Game1 _g;
    internal LoadingScreen(Game1 g) { _g = g; }

    public void Draw(int screenW, int screenH)
    {
        MenuDraw.Backdrop(screenW, screenH);

        // Title (same style/position as MainMenuScreen).
        if (_g._largeFont != null)
        {
            string title = "NECROKING";
            var titleSize = _g._largeFont.MeasureString(title);
            int titleY = screenH / 5;
            int titleX = (int)(screenW / 2f - titleSize.X / 2f);
            MenuDraw.Text(_g._largeFont, title, new Vector2(titleX + 3, titleY + 3), new Color(0, 0, 0, 180));
            MenuDraw.Text(_g._largeFont, title, new Vector2(titleX, titleY), new Color(220, 180, 100));
        }

        // Current step, centered. The dot count animates on wall-clock time (the
        // game clock doesn't tick during loading) so a long step still looks alive;
        // centering measures the bare label so the text doesn't shift as dots cycle.
        string label = _g.LoadingStatus;
        var size = _g._font?.MeasureString(label) ?? Vector2.Zero;
        int x = (int)(screenW / 2f - size.X / 2f);
        int y = (int)(screenH / 2f - size.Y / 2f);
        string text = label + new string('.', 1 + Environment.TickCount / 350 % 3);
        MenuDraw.Text(_g._font, text, new Vector2(x + 2, y + 2), new Color(0, 0, 0, 160));
        MenuDraw.Text(_g._font, text, new Vector2(x, y), new Color(230, 220, 200));

        // Thin progress bar (steps completed / total) under the label.
        int barW = Math.Min(420, screenW - 80), barH = 6;
        int barX = screenW / 2 - barW / 2, barY = y + (int)size.Y + 24;
        _g.Scope.Draw(_g._pixel, new Rectangle(barX, barY, barW, barH), new Color(60, 40, 80, 220));
        int fillW = (int)(barW * _g.LoadingProgress);
        if (fillW > 0)
            _g.Scope.Draw(_g._pixel, new Rectangle(barX, barY, fillW, barH), new Color(220, 180, 100));
    }
}
