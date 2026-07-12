using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Necroking.Data;
using Necroking.Editor;

namespace Necroking.UI;

/// <summary>
/// Pause-menu → Save Game submenu. Shows the existing saves (click one to take
/// its name and overwrite it), a name field for typing a new name, and a
/// confirm button that reads "Overwrite Save" when the name already exists,
/// otherwise "New Save". Pure UI — the actual file work is injected from
/// Game1.Saves.cs via <see cref="SetCallbacks"/>. Follows the SettingsWindow /
/// MultiplayerWindow pattern: driven by the shared EditorBase, closed via
/// <see cref="WantsClose"/>.
/// </summary>
public class SaveGameWindow
{
    private readonly EditorBase _ui;

    /// <summary>Set to true when the user clicks Cancel or a save succeeds
    /// (ESC goes through the modal layer).</summary>
    public bool WantsClose { get; set; }

    private Func<List<SaveGameInfo>> _listSaves = () => new List<SaveGameInfo>();
    private Func<string, string> _uniqueName = b => b;
    private Func<string, bool> _fileExists = _ => false;
    private Func<string, bool> _writeSave = _ => false;
    private Func<string, bool> _deleteSave = _ => false;
    private Func<string, string> _sanitize = s => s;

    private List<SaveGameInfo> _saves = new();
    private string _name = "";
    private string _error = "";
    // What the confirm button would write — shown next to the name field.
    private string _currentFormId = "";
    private List<string> _currentSpells = new();

    public const int PanelW = 720;
    public const int RowH = 112;
    public const int CardW = 176;

    public SaveGameWindow(EditorBase ui)
    {
        _ui = ui;
    }

    public void SetCallbacks(Func<List<SaveGameInfo>> listSaves, Func<string, string> uniqueName,
        Func<string, bool> fileExists, Func<string, bool> writeSave, Func<string, bool> deleteSave,
        Func<string, string> sanitize)
    {
        _listSaves = listSaves;
        _uniqueName = uniqueName;
        _fileExists = fileExists;
        _writeSave = writeSave;
        _deleteSave = deleteSave;
        _sanitize = sanitize;
    }

    /// <summary>Called when the pause-menu Save Game button opens this window:
    /// refresh the save list, prefill a free default name, and snapshot the
    /// current game's preview data (form + first spellbar spells).</summary>
    public void OnOpen(string currentFormId, List<string> currentSpells)
    {
        _saves = _listSaves();
        _name = _uniqueName("Quicksave");
        _error = "";
        _currentFormId = currentFormId;
        _currentSpells = currentSpells;
        _ui.SetScrollOffset("save_list", 0);
        WantsClose = false;
        data = Game1.Instance.GetSaveDataJson();
    }

    SaveGameData data;

    public void Draw(int screenW, int screenH)
    {
        _ui.DrawRect(new Rectangle(0, 0, screenW, screenH), new Color(0, 0, 0, 180));

        var PanelH = screenH - 50;
        int panelX = (screenW - PanelW) / 2;
        int panelY = (screenH - PanelH) / 2;
        _ui.DrawRect(new Rectangle(panelX, panelY, PanelW, PanelH), EditorBase.PanelBg);
        _ui.DrawBorder(new Rectangle(panelX, panelY, PanelW, PanelH), EditorBase.PanelBorder);
        _ui.DrawRect(new Rectangle(panelX, panelY, PanelW, 3), EditorBase.AccentColor);

        string title = "SAVE GAME";
        var titleSize = _ui.MeasureText(title);
        _ui.DrawText(title, new Vector2(panelX + PanelW / 2f - (int)(titleSize.X / 2f), panelY + 8), EditorBase.TextBright);

        int x = panelX + 20;
        int w = PanelW - 40;
        int y = panelY + 36;

        // ── What you're saving: current-game preview card + name field ───
        Game1.Instance._gameRenderer.DrawSavePreviewCard(new Rectangle(x, y, CardW, RowH), _currentFormId,
            data.SpellBar, data.Player.Inventory);
        int fieldX = x + CardW + 12;

        // ── Existing saves (click to take that name → overwrite) ─────────
        // Rows are drawn BELOW first so a row click can override the name
        // field's return value this frame: the same click deactivates the
        // focused field (committing its old buffer), and taking that committed
        // buffer would stomp the row's name.
        int listY = y + RowH + 14;
        // Delete row sits at the very bottom; confirm/cancel row above it.
        int deleteBtnY = panelY + PanelH - 42;
        int btnY = deleteBtnY - 38;
        // Scrollable list window: from below the header down to above the
        // error line / confirm buttons. BeginScrollPanel owns wheel + clip +
        // the draggable bar; rows are narrowed to keep clear of the bar column.
        var listRect = new Rectangle(x, listY + 22, w, btnY - 28 - (listY + 22));
        bool pickedRow = false;
        var sp = _ui.BeginScrollPanel("save_list", listRect, topPad: 0);
        int rowsY = sp.ContentY;
        if (_saves.Count == 0)
        {
            _ui.DrawText("(no saves yet)", new Vector2(x, rowsY), EditorBase.TextDim);
            rowsY += 24;
        }
        else
        {
            // Newest-first from ListSaveGames.
            int rowW = w - 12;
            for (int i = 0; i < _saves.Count; i++)
            {
                var s = _saves[i];
                if (_ui.DrawButton("", x, rowsY, rowW, RowH, bgOverride: _name == s.Name ? new(100, 100, 85, 240) : null))
                {
                    _name = s.Name;
                    pickedRow = true;
                }

                var cardRect = new Rectangle(x + 4, rowsY + 4, CardW, RowH - 8);
                Game1.Instance._gameRenderer.DrawSavePreviewCard(cardRect, s.FormId, s.SpellBar, s.Inventory);
                Game1.Instance._gameRenderer.DrawSaveGameText(
                    new(x + SaveGameWindow.CardW + 2, rowsY, rowW - (SaveGameWindow.CardW + 2), RowH), s);
                rowsY += RowH + 6;
            }
        }
        sp.End(rowsY);

        // Name field next to the current-game card (drawn after the rows so a
        // row click this frame wins — see comment above).
        string edited = _ui.DrawTextField("save_name", "Save name", _name, fieldX, y + (RowH - 20) / 2, w - CardW - 12);
        if (!pickedRow) _name = edited;
        _ui.DrawText("EXISTING SAVES", new Vector2(x, listY), EditorBase.AccentColor);
        y = listRect.Bottom + 6;

        if (_error != "")
        {
            _ui.DrawText(_error, new Vector2(x, y), EditorBase.DangerColor);
            y += 20;
        }

        // ── Confirm / Cancel ─────────────────────────────────────────────
        string clean = _sanitize(_name);
        bool overwrite = clean != "" && _fileExists(clean);
        string confirmLabel = overwrite ? "Overwrite Save" : "New Save";
        Color confirmBg = overwrite ? new Color(120, 50, 50, 240) : EditorBase.ButtonBg;
        if (_ui.DrawButton(confirmLabel, panelX + PanelW / 2 - 170, btnY, 200, 30, confirmBg))
        {
            if (clean == "") clean = _uniqueName("Quicksave");
            if (_writeSave(clean))
            {
                WantsClose = true;
            }
            else
            {
                _error = "Save failed - see log";
                _saves = _listSaves();
            }
        }
        if (_ui.DrawButton("Cancel", panelX + PanelW / 2 + 50, btnY, 120, 30))
            WantsClose = true;

        // ── Delete selected save (own row at the bottom) ─────────────────
        bool canDelete = overwrite; // a save with this name exists
        if (_ui.DrawButton(canDelete ? $"Delete \"{clean}\"" : "Delete Save",
                panelX + PanelW / 2 - 110, deleteBtnY, 220, 30,
                canDelete ? new Color(120, 50, 50, 240) : EditorBase.ButtonBg) && canDelete)
        {
            if (_deleteSave(clean))
            {
                _saves = _listSaves();
                _name = _uniqueName("Quicksave");
                _error = "";
            }
            else
            {
                _error = "Delete failed - see log";
            }
        }
    }
}
