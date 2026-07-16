using System;
using Microsoft.Xna.Framework;

namespace Necroking.UI;

/// <summary>The title screen (MenuState.MainMenu): Continue / Play / Load Game /
/// Scenarios / Settings / Quit. Layout, drawing and click handling live together —
/// BuildLayout is the single source of truth Draw and Update both consume.
/// Drawn from GameRenderer.Draw's MainMenu early-out (full-screen, bypasses the
/// UIRouter); Update is called from the matching block in Game1.Update.</summary>
public sealed class MainMenuScreen
{
    private readonly Game1 _g;
    internal MainMenuScreen(Game1 g) { _g = g; }

    // The ONE definition of the main-menu layout — Draw and Update both consume it.
    // The stack starts just above screenH/2 but shifts up (never into the title
    // block) when it would clip the bottom edge — keeps all buttons on-screen at 720p.
    private MenuButton[] BuildLayout(int screenW, int screenH)
    {
        bool hasSaves = _g._loadMenuSaves.Count > 0;
        (MenuButtonId id, string label, bool gapBefore, bool interactable)[] items = {
            (MenuButtonId.Continue, hasSaves ? $"Continue {_g._loadMenuSaves[0].Name}" : "Continue", false, hasSaves),
            (MenuButtonId.Play, "Play", true, true),
            (MenuButtonId.PlayTestMap, "Play Test Map", false, true),
            (MenuButtonId.LoadGame, "Load Game", true, true),
            (MenuButtonId.Scenarios, "Scenarios", false, true),
            (MenuButtonId.Settings, "Settings", false, true),
            (MenuButtonId.Quit, "Quit", true, true),
        };

        int btnW = 320, btnH = 55, btnGap = 10;
        int gapSpace = 18;
        int totalH = -btnGap;
        foreach (var it in items) totalH += (it.gapBefore ? gapSpace : 0) + btnH + btnGap;
        int x = screenW / 2 - btnW / 2;
        int y = Math.Min(screenH / 2 - 20 - btnH, screenH - 20 - totalH);
        y = Math.Max(y, screenH / 5 + 60);
        var buttons = new MenuButton[items.Length];
        for (int i = 0; i < items.Length; i++)
        {
            if (items[i].gapBefore) y += gapSpace;
            buttons[i] = new MenuButton
                { Id = items[i].id, Label = items[i].label, Rect = new Rectangle(x, y, btnW, btnH), Interactable = items[i].interactable };
            y += btnH + btnGap;
        }
        return buttons;
    }

    /// <summary>Left-click dispatch — called from Game1.Update's MainMenu block,
    /// hit-testing the same layout Draw renders.</summary>
    public void Update()
    {
        if (!_g._input.LeftPressed) return;
        int sw = _g.GraphicsDevice.Viewport.Width;
        int sh = _g.GraphicsDevice.Viewport.Height;
        int mx = (int)_g._input.MousePos.X, my = (int)_g._input.MousePos.Y;
        foreach (var b in BuildLayout(sw, sh))
        {
            if (!b.Interactable || !b.Rect.Contains(mx, my)) continue;
            switch (b.Id)
            {
                case MenuButtonId.Continue:
                    _g.LoadSaveGame(_g._loadMenuSaves[0].Name);
                    return;
                case MenuButtonId.Play:
                    _g.StartGame();
                    return;
                case MenuButtonId.PlayTestMap: // loads assets/maps/testmap.json
                    _g.StartGame("testmap");
                    return;
                case MenuButtonId.Scenarios:
                    _g._menuState = MenuState.ScenarioList;
                    _g._scenarioScrollPx = 0f;
                    return;
                case MenuButtonId.LoadGame:
                    _g._loadMenu.Open();
                    return;
                case MenuButtonId.Settings:
                    _g._menuState = MenuState.Settings;
                    return;
                case MenuButtonId.Quit:
                    _g.Exit();
                    return;
            }
        }
    }

    public void Draw(int screenW, int screenH)
    {
        MenuDraw.Backdrop(screenW, screenH);

        // Title
        if (_g._largeFont != null)
        {
            string title = "NECROKING";
            var titleSize = _g._largeFont.MeasureString(title);
            int titleY = screenH / 5;
            // Shadow
            MenuDraw.Text(_g._largeFont, title, new Vector2(screenW / 2f - titleSize.X / 2f + 3, titleY + 3), new Color(0, 0, 0, 180));
            MenuDraw.Text(_g._largeFont, title, new Vector2(screenW / 2f - titleSize.X / 2f, titleY), new Color(220, 180, 100));

            string subtitle = "Rise of the Undead";
            var subSize = _g._font?.MeasureString(subtitle) ?? Vector2.Zero;
            MenuDraw.Text(_g._font, subtitle, new Vector2(screenW / 2f - subSize.X / 2f, titleY + 30), new Color(180, 160, 120, 200));
        }

        foreach (var b in BuildLayout(screenW, screenH))
            MenuDraw.Button(b.Label, b.Rect.X, b.Rect.Y, b.Rect.Width, b.Rect.Height, b.Interactable);

        // Version info
        MenuDraw.Text(_g._smallFont, "MonoGame Port v0.1", new Vector2(10, screenH - 20), new Color(80, 80, 100));
    }
}
