using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Necroking.Core;
using Necroking.Data;
using Necroking.GameSystems;
using Necroking.Lib;

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
    internal UI.LoadGameWindow _loadGameWindow = null!;

    // Main-menu save list: refreshed on entering the main menu, read by its
    // Continue button. (The load window keeps its own list, refreshed in Open.)
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
                Version = data.Version,
                Name = Path.GetFileNameWithoutExtension(file),
                MapName = data.MapName,
                SavedAt = savedAt,
                FilePath = file,
                FormId = data.Player.FormId,
                SpellBar = data.SpellBar,
                Inventory = data.Player.Inventory,
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

    internal SaveGameData GetSaveDataJson()
    {
        int idx = _sim.NecromancerIndex;
        if (idx < 0)
        {
            DebugLog.Log("saves", "WriteSaveGame failed: no live necromancer");
            return null;
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
                // grant_path buffs (buff_path_*) are code-built, not in the buff
                // registry — the load-side buff restore can't rebuild them, so
                // they're excluded here and replayed from the learned skill set
                // instead (SkillBookState.ApplySave).
                Buffs = unit.ActiveBuffs
                    .Where(b => !b.BuffDefID.StartsWith("buff_path_"))
                    .Select(b => new SavedBuff
                    {
                        Id = b.BuffDefID,
                        Remaining = b.RemainingDuration,
                        Permanent = b.Permanent,
                        Stacks = b.StackCount,
                    }).ToList(),
                Inventory = SnapshotInventory(),
            },
            SpellBar = _spellBarState.Slots.Select(s => s.SpellID ?? "").ToList(),
            SkillBook = _skillBookState.ExportSave(),
        };
        return data;

    }

    /// <summary>Delete saves/{name}.json. Returns false (logged) when the file
    /// doesn't exist or deletion throws.</summary>
    internal bool DeleteSaveGame(string name)
    {
        if (!SaveFileExists(name))
        {
            DebugLog.Log("saves", $"DeleteSaveGame: no save named '{name}'");
            return false;
        }
        try
        {
            File.Delete(SaveFilePath(name));
            DebugLog.Log("saves", $"Deleted save '{name}'");
            return true;
        }
        catch (Exception e)
        {
            DebugLog.Log("saves", $"DeleteSaveGame failed for '{name}': {e.Message}");
            return false;
        }
    }

    /// <summary>In-memory "save game": end any spirit walk (so the BODY is what
    /// gets snapshotted, not the spirit NecromancerIndex points at), then capture
    /// the session into a SaveGameData. Null (logged) when there is no live
    /// player. The file-free half of WriteSaveGame — pair with ApplySaveData for
    /// a save/load round-trip that never touches disk.</summary>
    internal SaveGameData CaptureSaveData()
    {
        GameSystems.SpiritWalkSystem.End(this);
        return GetSaveDataJson();
    }

    /// <summary>Snapshot the current session into saves/{name}.json. Returns
    /// false (logged) when there is no live player to save, or while a scenario
    /// is running — StartScenario never sets _currentMapName, so a scenario save
    /// would record the previous map's name and load into the wrong world.
    /// (CaptureSaveData stays unguarded: in-memory round-trips are how the
    /// map-reload scenarios test save/load.)</summary>
    internal bool WriteSaveGame(string name)
    {
        if (_activeScenario != null)
        {
            DebugLog.Log("saves", $"WriteSaveGame '{name}' blocked: scenario active, world is not a saveable map");
            return false;
        }
        var data = CaptureSaveData();
        if (data == null) return false;
        Directory.CreateDirectory(GamePaths.Resolve(GamePaths.SavesDir));
        bool ok = JsonFile.Save(SaveFilePath(name), data, JsonDefaults.Indented);
        if (ok) DebugLog.Log("saves", $"Saved game '{name}' (map {data.MapName})");
        return ok;
    }

    /// <summary>Non-empty inventory slots, each tagged with its index so exact
    /// layout round-trips. Returns an empty list when there is no inventory.</summary>
    private List<SavedInventorySlot> SnapshotInventory()
    {
        var result = new List<SavedInventorySlot>();
        if (_inventory == null) return result;
        for (int i = 0; i < _inventory.SlotCount; i++)
        {
            var slot = _inventory.GetSlot(i);
            if (slot.IsEmpty) continue;
            result.Add(new SavedInventorySlot { Slot = i, ItemId = slot.ItemId, Quantity = slot.Quantity });
        }
        return result;
    }

    /// <summary>In-memory "load game": rebuild the world from a SaveGameData —
    /// start its map through the normal StartGame flow, then apply the saved
    /// player state on top. The file-free half of LoadSaveGame. Returns false
    /// (logged, world untouched) when the save's map doesn't exist. With
    /// <paramref name="mapMemory"/> the map itself also comes from memory
    /// (captured editor state) instead of assets/maps/.</summary>
    internal bool ApplySaveData(SaveGameData save, Data.MapData.MapJsonBundle? mapMemory = null)
    {
        // "empty_test" is synthesized in code; every other map needs its JSON —
        // unless the map is supplied in memory, which needs no file at all.
        if (mapMemory == null
            && save.MapName != "empty_test"
            && !File.Exists(GamePaths.Resolve($"{GamePaths.MapsDir}/{save.MapName}.json")))
        {
            DebugLog.Log("saves", $"ApplySaveData failed: map '{save.MapName}' not found");
            return false;
        }
        StartGame(save.MapName, mapMemory);
        ApplySaveToWorld(save);
        return true;
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
        if (!ApplySaveData(save)) return false;
        DebugLog.Log("saves", $"Loaded game '{name}' (map {save.MapName})");
        return true;
    }

    /// <summary>Minimal snapshot of the player-facing UI/session context — the
    /// things a world rebuild stomps but the player perceives as "where I am":
    /// camera, time settings, menu state, the editor's Save target. Deliberately
    /// bottom-up and small: no panel internals (those hold references into the
    /// world being torn down — better to save and load too little than too much).
    /// Captured with <see cref="SaveCurrentUIState"/>, restored with
    /// <see cref="ApplyUIState"/>. Also handy for tests.</summary>
    internal sealed class UIStateSnapshot
    {
        public Vec2 CameraPosition;
        public float CameraZoom;
        public MenuState MenuState;
        public Core.GameClock.PauseSource PauseSources;
        public float TimeScale;
        public string EditorMapFilename = "";
    }

    internal UIStateSnapshot SaveCurrentUIState() => new()
    {
        CameraPosition = _camera.Position,
        CameraZoom = _camera.Zoom,
        MenuState = _menuState,
        PauseSources = _clock.PauseSources,
        TimeScale = _clock.TimeScale,
        EditorMapFilename = _mapEditor.MapFilename,
    };

    internal void ApplyUIState(UIStateSnapshot ui)
    {
        _camera.Position = ui.CameraPosition;
        _camera.Zoom = ui.CameraZoom;
        _menuState = ui.MenuState;
        _clock.ClearAllPauses();
        if (ui.PauseSources != Core.GameClock.PauseSource.None)
            _clock.Pause(ui.PauseSources); // Pause() ORs flags, so one call restores the whole set
        _clock.SetTimeScale(ui.TimeScale);
        _mapEditor.SetMapFilename(ui.EditorMapFilename);
    }

    /// <summary>The map editor's "Reload Map" button: a save/load round-trip that
    /// never touches disk — CaptureSaveData + CaptureMapToMemory + ApplySaveData —
    /// so the world resets exactly like saving the map, saving the game, and
    /// loading both back (units/objects/villages/triggers rebuilt, player carried
    /// across and teleported like a load), with the map taken from the LIVE editor
    /// state: unsaved editor changes survive, and nothing is written to
    /// assets/maps/. Unlike a real load it preserves the editing context: camera
    /// position/zoom, the current menu state (the editor stays open — StartGame's
    /// None and ApplySaveToWorld's camera snap are undone within the same frame,
    /// so neither is ever visible), and the editor's typed Save target. Editor UI
    /// state survives because nothing here resets it (deliberately no
    /// _editorUi.ResetAllState, unlike the load-window path).</summary>
    internal void ReloadMapInEditor()
    {
        var save = CaptureSaveData();
        if (save == null)
        {
            _mapEditor.OnMapReloaded(false, _currentMapName);
            return;
        }
        // Capture the live map BEFORE StartGame's ResetWorldState wipes it.
        Data.MapData.MapJsonBundle mapMemory;
        try
        {
            mapMemory = _mapEditor.CaptureMapToMemory();
        }
        catch (Exception e)
        {
            DebugLog.Log("saves", $"Reload map: live-map capture failed: {e.Message}");
            _mapEditor.OnMapReloaded(false, _currentMapName);
            return;
        }
        var ui = SaveCurrentUIState();
        if (!ApplySaveData(save, mapMemory))
        {
            _mapEditor.OnMapReloaded(false, save.MapName);
            return;
        }
        ApplyUIState(ui);
        // Come back paused regardless of the restored pause state: StartGame's
        // OnWorldStart cleared every holder and the editor-entry default-pause
        // transition doesn't re-fire (we never observably left MapEditor). Same
        // User source as editor entry / the time controls, so the pause button
        // reflects it and unpausing works normally.
        _clock.Pause(GameClock.PauseSource.User);
        DebugLog.Log("saves", $"Editor-reloaded map '{save.MapName}' in place (from live editor state)");
        _mapEditor.OnMapReloaded(true, save.MapName);
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

        // Skill book: StartGame reset it, so refill learned/points/tallies/
        // unlocks verbatim, then ApplySave replays the learned grant_path
        // effects (their buffs are code-built — excluded from the snapshot
        // above). Everything else round-trips as data: cap/passive buffs came
        // back through the buff restore, unlocks gate systems that read the
        // book directly.
        _skillBookState.ApplySave(save.SkillBook, new Game.SkillEffects.SkillEffectContext
        {
            Inventory = _inventory,
            GameData = _gameData,
            Bar = _spellBarState,
            BookState = _skillBookState,
            Sim = _sim,
        });

        // Metamorph accruals (Corpse Eating +MaxHP, Soul Consumption +MaxMana)
        // mutate stats directly at consume time rather than living on a buff,
        // so layer them back onto the freshly rebuilt stat block / NecroState.
        if (_skillBookState.CorpseEatingBonus > 0)
        {
            var u = _sim.UnitsMut[idx];
            var stats = u.Stats;
            stats.MaxHP += _skillBookState.CorpseEatingBonus;
            stats.HP += _skillBookState.CorpseEatingBonus;
            u.Stats = stats;
        }
        if (_skillBookState.SoulConsumptionBonus > 0)
        {
            _sim.NecroState.MaxMana += _skillBookState.SoulConsumptionBonus;
            _sim.NecroState.Mana = Math.Min(_sim.NecroState.Mana + _skillBookState.SoulConsumptionBonus,
                _sim.NecroState.MaxMana);
        }

        // Inventory: StartGame recreated + cleared _inventory, so restore the
        // saved slots verbatim (SetSlot preserves exact positions). Validate ids
        // against the live registry so a removed item doesn't break the load.
        if (_inventory != null)
        {
            foreach (var s in save.Player.Inventory)
            {
                if (_gameData.Items.Get(s.ItemId) == null)
                {
                    DebugLog.Log("saves", $"Saved item '{s.ItemId}' not in item registry — slot {s.Slot} skipped");
                    continue;
                }
                _inventory.SetSlot(s.Slot, s.ItemId, s.Quantity);
            }
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
