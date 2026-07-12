using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Necroking.Core;
using Necroking.Data;
using Necroking.GameSystems;

namespace Necroking;

// Game1 partial: save games. Writes/reads saves/{name}.json (SaveGameData) and
// applies a loaded save on top of the normal StartGame(mapName) flow: reuse the
// map-spawned necromancer (never spawn a second player), transform to the saved
// form, teleport, re-apply buffs through BuffSystem (so granted weapons/+MaxHP/
// incap side effects land), and overwrite the spellbar slots in memory.
public partial class Game1
{
    /// <summary>Map name passed to the current StartGame — what a save records.</summary>
    internal string _currentMapName = "default";
    internal UI.SaveGameWindow _saveGameWindow = null!;

    // Load-menu (main-menu family) state: list refreshed when the menu opens.
    // Internal — GameRenderer.Hud.cs reads it to draw the rows.
    internal List<SaveGameInfo> _loadMenuSaves = new();

    /// <summary>The single pathing choke point for save files. A future move to a
    /// roaming location only needs to change this + GamePaths.SavesDir.</summary>
    internal static string SaveFilePath(string name)
        => GamePaths.Resolve($"{GamePaths.SavesDir}/{name}.json");

    internal static bool SaveFileExists(string name)
        => File.Exists(SaveFilePath(name));

    /// <summary>Strip filename-invalid characters and trim. Returns "" when
    /// nothing usable remains (caller substitutes a unique default name).</summary>
    internal static string SanitizeSaveName(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var invalid = Path.GetInvalidFileNameChars();
        string clean = new string(raw.Where(c => !invalid.Contains(c)).ToArray()).Trim();
        return clean;
    }

    /// <summary>"Quicksave", then "Quicksave2", "Quicksave3", … — first name
    /// without an existing file (case-insensitive via the filesystem).</summary>
    internal static string UniqueSaveName(string baseName = "Quicksave")
    {
        if (!SaveFileExists(baseName)) return baseName;
        for (int n = 2; ; n++)
            if (!SaveFileExists(baseName + n)) return baseName + n;
    }

    /// <summary>Enumerate saves/*.json, newest first. Unparseable files are
    /// skipped (logged) so one corrupt save doesn't hide the rest.</summary>
    internal static List<SaveGameInfo> ListSaveGames()
    {
        var result = new List<SaveGameInfo>();
        string dir = GamePaths.Resolve(GamePaths.SavesDir);
        if (!Directory.Exists(dir)) return result;
        foreach (string file in Directory.EnumerateFiles(dir, "*.json"))
        {
            if (!JsonFile.Load<SaveGameData>(file, JsonDefaults.Indented, out var data) || data == null)
            {
                DebugLog.Log("saves", $"Skipping unreadable save file: {file}");
                continue;
            }
            if (!DateTime.TryParse(data.SavedAtUtc, null,
                    System.Globalization.DateTimeStyles.AdjustToUniversal, out var savedAt))
                savedAt = File.GetLastWriteTimeUtc(file);
            result.Add(new SaveGameInfo
            {
                Name = Path.GetFileNameWithoutExtension(file),
                MapName = data.MapName,
                SavedAt = savedAt,
                FilePath = file,
                FormId = data.Player.FormId,
                SpellBar = data.SpellBar,
            });
        }
        result.Sort((a, b) => b.SavedAt.CompareTo(a.SavedAt));
        return result;
    }

    /// <summary>Open the save dialog with the current game's preview data
    /// (form + spellbar) — the one entry point for showing the SaveMenu.</summary>
    internal void OpenSaveMenu()
    {
        int idx = _sim.NecromancerIndex;
        _saveGameWindow.OnOpen(
            idx >= 0 ? _sim.Units[idx].UnitDefID : "",
            _spellBarState.Slots.Select(s => s.SpellID ?? "").ToList());
        _menuState = MenuState.SaveMenu;
    }

    /// <summary>Snapshot the current session into saves/{name}.json. Returns
    /// false (logged) when there is no live player to save.</summary>
    internal bool WriteSaveGame(string name)
    {
        int idx = _sim.NecromancerIndex;
        if (idx < 0)
        {
            DebugLog.Log("saves", "WriteSaveGame failed: no live necromancer");
            return false;
        }
        var unit = _sim.Units[idx];
        var data = new SaveGameData
        {
            MapName = _currentMapName,
            SavedAtUtc = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"),
            Player = new SavedPlayer
            {
                X = unit.Position.X,
                Y = unit.Position.Y,
                Facing = unit.FacingAngle,
                FormId = unit.UnitDefID,
                Buffs = unit.ActiveBuffs.Select(b => new SavedBuff
                {
                    Id = b.BuffDefID,
                    Remaining = b.RemainingDuration,
                    Permanent = b.Permanent,
                    Stacks = b.StackCount,
                }).ToList(),
            },
            SpellBar = _spellBarState.Slots.Select(s => s.SpellID ?? "").ToList(),
        };
        Directory.CreateDirectory(GamePaths.Resolve(GamePaths.SavesDir));
        bool ok = JsonFile.Save(SaveFilePath(name), data, JsonDefaults.Indented);
        if (ok) DebugLog.Log("saves", $"Saved game '{name}' (map {data.MapName})");
        return ok;
    }

    /// <summary>Load saves/{name}.json: start its map through the normal flow,
    /// then apply the saved player state on top. On any validation failure the
    /// menu state is left untouched (caller stays where it is).</summary>
    internal bool LoadSaveGame(string name)
    {
        if (!JsonFile.Load<SaveGameData>(SaveFilePath(name), JsonDefaults.Indented, out var save) || save == null)
        {
            DebugLog.Log("saves", $"LoadSaveGame failed: cannot read '{name}'");
            return false;
        }
        // "empty_test" is synthesized in code; every other map needs its JSON.
        if (save.MapName != "empty_test"
            && !File.Exists(GamePaths.Resolve($"{GamePaths.MapsDir}/{save.MapName}.json")))
        {
            DebugLog.Log("saves", $"LoadSaveGame failed: map '{save.MapName}' not found");
            return false;
        }
        StartGame(save.MapName);
        ApplySaveToWorld(save);
        DebugLog.Log("saves", $"Loaded game '{name}' (map {save.MapName})");
        return true;
    }

    private void ApplySaveToWorld(SaveGameData save)
    {
        // Reuse the map-spawned (or fallback) necromancer — never add a second.
        int idx = _sim.NecromancerIndex;
        if (idx < 0)
        {
            DebugLog.Log("saves", "ApplySaveToWorld: no necromancer after StartGame, save not applied");
            return;
        }

        // Form first: TransformUnit rebuilds stats/sprite/size, so buffs applied
        // afterwards land on the final form's stat block.
        var unit = _sim.Units[idx];
        string formId = save.Player.FormId;
        if (!string.IsNullOrEmpty(formId) && formId != unit.UnitDefID)
        {
            if (_gameData.Units.Get(formId) != null)
                _sim.TransformUnit(idx, formId);
            else
                DebugLog.Log("saves", $"Saved form '{formId}' not in unit registry — keeping '{unit.UnitDefID}'");
        }

        // Position/facing + the same camera/horde snap StartGame does.
        var pos = new Vec2(save.Player.X, save.Player.Y);
        unit.Position = pos;
        unit.FacingAngle = save.Player.Facing;
        _camera.Position = pos;
        _sim.Horde.CircleCenter = pos;

        // Buffs: clear whatever the map spawn came with, then re-apply the saved
        // set through BuffSystem so per-stack side effects (granted weapons,
        // +MaxHP, incap state) are recreated. Permanent buffs use the ≤0-duration
        // sentinel; timed buffs get their saved remaining time.
        foreach (string buffId in unit.ActiveBuffs.Select(b => b.BuffDefID).Distinct().ToList())
            BuffSystem.RemoveBuff(_sim.UnitsMut, idx, buffId);
        foreach (var b in save.Player.Buffs)
        {
            var def = _gameData.Buffs.Get(b.Id);
            if (def == null)
            {
                DebugLog.Log("saves", $"Saved buff '{b.Id}' not in buff registry — skipped");
                continue;
            }
            float duration = b.Permanent ? 0f : b.Remaining;
            for (int s = 0; s < Math.Max(1, b.Stacks); s++)
                BuffSystem.ApplyBuffWithDuration(_sim.UnitsMut, idx, def, duration, _gameData);
        }

        // Spellbar: overwrite the slots StartGame loaded from user settings.
        // Deliberately no SaveSpellBars() here — loading a save must not clobber
        // the per-machine default loadout in user settings/spellbar.json.
        for (int i = 0; i < _spellBarState.Slots.Length; i++)
        {
            string spellId = i < save.SpellBar.Count ? save.SpellBar[i] ?? "" : "";
            if (spellId != "" && _gameData.Spells.Get(spellId) == null)
            {
                DebugLog.Log("saves", $"Saved spell '{spellId}' not in spell registry — slot {i} cleared");
                spellId = "";
            }
            _spellBarState.Slots[i] = new SpellBarSlot { SpellID = spellId };
        }
    }
}
