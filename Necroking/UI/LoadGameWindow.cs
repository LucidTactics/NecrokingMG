using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Necroking.Data;
using Necroking.Editor;

namespace Necroking.UI;

/// <summary>
/// Load Game window (MenuState.LoadMenu), reachable from both the main menu and
/// the pause menu. Follows the SettingsWindow / SaveGameWindow pattern: hosted
/// by MenuHostLayer (drawn over whichever menu opened it — Game1._backMenuState
/// decides where Back/ESC returns), driven by the shared EditorBase, closed via
/// <see cref="WantsClose"/>. Rows reuse SaveGameWindow's metrics and
/// GameRenderer's save-preview widgets. A row click only REQUESTS the load
/// (<see cref="PendingLoad"/>): immediate-mode clicks land during the Hud
/// render pass, and rebuilding the world mid-draw is not safe — Game1.Update
/// performs the actual LoadSaveGame on the next frame.
/// </summary>
public sealed class LoadGameWindow
{
    private readonly EditorBase _ui;

    /// <summary>Set to true when the user clicks Back (ESC goes through MenuHostLayer).</summary>
    public bool WantsClose { get; set; }

    /// <summary>Save name whose row was clicked — Game1.Update picks it up and
    /// runs LoadSaveGame outside the draw pass.</summary>
    public string? PendingLoad { get; set; }

    private List<SaveGameInfo> _saves = new();

    public LoadGameWindow(EditorBase ui) { _ui = ui; }

    /// <summary>Open from the main or pause menu: refresh the save list, reset
    /// the scroll, and switch state. MenuHostLayer hosts it from there;
    /// _backMenuState (stamped every main/pause-menu frame) already holds the
    /// menu we came from.</summary>
    public void Open()
    {
        _saves = Game1.ListSaveGames();
        _ui.SetScrollOffset("load_list", 0);
        WantsClose = false;
        PendingLoad = null;
        Game1.Instance._menuState = MenuState.LoadMenu;
    }

    public void Draw(int screenW, int screenH)
    {
        int panelW = SaveGameWindow.PanelW;
        int panelH = screenH - 50;
        int panelX = (screenW - panelW) / 2;
        int panelY = (screenH - panelH) / 2;
        _ui.DrawRect(new Rectangle(panelX, panelY, panelW, panelH), EditorBase.PanelBg);
        _ui.DrawBorder(new Rectangle(panelX, panelY, panelW, panelH), EditorBase.PanelBorder);
        _ui.DrawRect(new Rectangle(panelX, panelY, panelW, 3), EditorBase.AccentColor);

        string title = "LOAD GAME";
        var titleSize = _ui.MeasureText(title);
        _ui.DrawText(title, new Vector2(panelX + panelW / 2f - (int)(titleSize.X / 2f), panelY + 8), EditorBase.TextBright);

        int x = panelX + 20;
        int w = panelW - 40;

        // Back button pinned at the bottom; the scrollable list fills the rest.
        int backBtnY = panelY + panelH - 42;
        int listY = panelY + 36;
        var listRect = new Rectangle(x, listY, w, backBtnY - 10 - listY);
        var sp = _ui.BeginScrollPanel("load_list", listRect, topPad: 0);
        int rowsY = sp.ContentY;
        if (_saves.Count == 0)
        {
            _ui.DrawText("(no saves found)", new Vector2(x, rowsY), EditorBase.TextDim);
            rowsY += 24;
        }
        else
        {
            int rowW = w - 12; // keep clear of the scrollbar column
            for (int i = 0; i < _saves.Count; i++)
            {
                var s = _saves[i];
                if (_ui.DrawButton("", x, rowsY, rowW, SaveGameWindow.RowH))
                    PendingLoad = s.Name;
                var cardRect = new Rectangle(x + 4, rowsY + 4, SaveGameWindow.CardW, SaveGameWindow.RowH - 8);
                Game1.Instance._gameRenderer.DrawSavePreviewCard(cardRect, s.FormId, s.SpellBar, s.Inventory);
                Game1.Instance._gameRenderer.DrawSaveGameText(
                    new(x + SaveGameWindow.CardW + 2, rowsY, rowW - (SaveGameWindow.CardW + 2), SaveGameWindow.RowH), s);
                rowsY += SaveGameWindow.RowH + 6;
            }
        }
        sp.End(rowsY);

        if (_ui.DrawButton("Back", panelX + panelW / 2 - 60, backBtnY, 120, 30))
            WantsClose = true;
    }
}
