using System.Linq;
using Microsoft.Xna.Framework;

namespace Necroking.UI;

/// <summary>The escape/pause menu (MenuState.PauseMenu): Resume, save/load, the
/// editor launchers, Settings, Multiplayer, Main Menu, Quit. Layout, drawing
/// and click handling live together — BuildLayout is the single source of truth
/// Draw and HandleClick both consume. Drawn from MenuHostLayer (Menu band, which
/// also owns the blocking blanket + ESC walk-back); HandleClick is called from
/// the matching block in Game1.Update.</summary>
public sealed class PauseMenuScreen
{
    private readonly Game1 _g;
    internal PauseMenuScreen(Game1 g) { _g = g; }

    internal struct View
    {
        public Rectangle Box;
        public MenuButton[] Buttons;
        public int ControlsY; // top of the controls-reference text block
    }

    private static readonly string[] Controls = {
        "WASD - Move     Space - Jump",
        "Q/E/1-8 - Cast spells",
        "Shift - Run    G - Ghost mode",
        "+/- - Speed   Scroll - Zoom"
    };

    // The ONE definition of the pause-menu layout — Draw and HandleClick both consume it.
    private View BuildLayout(int screenW, int screenH)
    {
        // (id, label, group gap before this button)
        (MenuButtonId id, string label, bool gapBefore)[] items = {
            (MenuButtonId.Resume, "Resume", false),
            (MenuButtonId.SaveGame, "Save Game", true),
            (MenuButtonId.LoadGame, "Load Game", false),
            (MenuButtonId.UnitEditor, "Unit Editor (F9)", true),
            (MenuButtonId.SpellEditor, "Spell Editor (F10)", false),
            (MenuButtonId.MapEditor, "Map Editor (F11)", false),
            (MenuButtonId.UIEditor, "UI Editor (F12)", false),
            (MenuButtonId.ItemEditor, "Item Editor", false),
            (MenuButtonId.Settings, "Settings", false),
            (MenuButtonId.Multiplayer, "Multiplayer", false),
            (MenuButtonId.ToMainMenu, "Main Menu", true),
            (MenuButtonId.Quit, "Quit", false),
        };

        int boxW = 350;
        int btnW = 280, btnH = 40, btnGap = 10;
        int gapSpace = 25;
        int extraGaps = items.Count(i => i.gapBefore);
        int boxH = 60 + items.Length * (btnH + btnGap) + 10 + Controls.Length * 16 + 20 + gapSpace * extraGaps;
        int boxX = (screenW - boxW) / 2;
        int boxY = (screenH - boxH) / 2;

        var view = new View
        {
            Box = new Rectangle(boxX, boxY, boxW, boxH),
            Buttons = new MenuButton[items.Length],
        };
        int x = boxX + (boxW - btnW) / 2;
        int y = boxY + 60;
        for (int i = 0; i < items.Length; i++)
        {
            if (items[i].gapBefore) y += gapSpace;
            view.Buttons[i] = new MenuButton
                { Id = items[i].id, Label = items[i].label, Rect = new Rectangle(x, y, btnW, btnH), Interactable = true };
            y += btnH + btnGap;
        }
        view.ControlsY = y + 10;
        return view;
    }

    /// <summary>Left-click dispatch — called from Game1.Update's PauseMenu block,
    /// hit-testing the same layout Draw renders. Returns true when the click
    /// switched to the load menu, telling the caller to skip the rest of this
    /// Update frame (the new state must not fall through to gameplay input).</summary>
    public bool HandleClick()
    {
        int sw = _g.GraphicsDevice.Viewport.Width;
        int sh = _g.GraphicsDevice.Viewport.Height;
        int mx = (int)_g._input.MousePos.X, my = (int)_g._input.MousePos.Y;
        var view = BuildLayout(sw, sh);
        foreach (var b in view.Buttons)
        {
            if (!b.Interactable || !b.Rect.Contains(mx, my)) continue;
            switch (b.Id)
            {
                case MenuButtonId.Resume:
                    _g._menuState = MenuState.None;
                    _g._clock.ClearAllPauses();
                    break;
                case MenuButtonId.SaveGame:
                    _g.OpenSaveMenu();
                    break;
                case MenuButtonId.LoadGame:
                    _g._loadMenu.Open();
                    return true;
                case MenuButtonId.UnitEditor:
                    _g._menuState = MenuState.UnitEditor;
                    _g._clock.ClearAllPauses();
                    break;
                case MenuButtonId.SpellEditor:
                    _g._menuState = MenuState.SpellEditor;
                    _g._clock.ClearAllPauses();
                    break;
                case MenuButtonId.MapEditor:
                    _g._menuState = MenuState.MapEditor;
                    _g._clock.ClearAllPauses();
                    _g._mapEditor.SuppressClicksUntilRelease();
                    break;
                case MenuButtonId.UIEditor:
                    _g.EnsureUIEditorInitialized();
                    _g._menuState = MenuState.UIEditor;
                    _g._clock.ClearAllPauses();
                    break;
                case MenuButtonId.ItemEditor:
                    _g._menuState = MenuState.ItemEditor;
                    _g._clock.ClearAllPauses();
                    break;
                case MenuButtonId.Settings:
                    _g._menuState = MenuState.Settings;
                    break;
                case MenuButtonId.Multiplayer:
                    _g._menuState = MenuState.Multiplayer;
                    break;
                case MenuButtonId.ToMainMenu:
                    _g._menuState = MenuState.MainMenu;
                    _g._clock.ClearAllPauses();
                    _g._gameWorldLoaded = false;
                    break;
                case MenuButtonId.Quit:
                    _g.Exit();
                    break;
            }
        }
        return false;
    }

    public void Draw(int screenW, int screenH)
    {
        if (_g._gameData.Settings.General.PauseDimBackground)
            _g.Scope.Draw(_g._pixel, new Rectangle(0, 0, screenW, screenH), new Color(0, 0, 0, 150));

        var view = BuildLayout(screenW, screenH);
        MenuDraw.Panel(view.Box, new Color(30, 30, 50, 235), new Color(100, 100, 180), 3);

        if (_g._largeFont != null)
        {
            string title = "PAUSED";
            var titleSize = _g._largeFont.MeasureString(title);
            MenuDraw.Text(_g._largeFont, title, new Vector2(view.Box.X + view.Box.Width / 2f - titleSize.X / 2f, view.Box.Y + 15), Color.White);
        }

        foreach (var b in view.Buttons)
            MenuDraw.Button(b.Label, b.Rect.X, b.Rect.Y, b.Rect.Width, b.Rect.Height, b.Interactable);

        // Controls reference
        if (_g._smallFont != null)
        {
            for (int i = 0; i < Controls.Length; i++)
                MenuDraw.Text(_g._smallFont, Controls[i], new Vector2(view.Box.X + 20, view.ControlsY + i * 16), new Color(140, 140, 160));
        }
    }
}
