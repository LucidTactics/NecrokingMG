using System;
using System.Collections.Generic;
using System.Text.Json;
using Necroking.Data.Registries;

namespace Necroking;

public partial class Game1
{
    /// <summary>
    /// <c>add_data</c> dev command — inject one or more registry entries (spells,
    /// units, items, buffs, …) into the LIVE game from JSON, exactly like a row in
    /// the matching <c>data/&lt;file&gt;.json</c>. Nothing is written to disk — the
    /// point is to try a new spell/unit/item without touching the JSON files. If the
    /// matching editor panel is open, the freshest entry is selected so it's visible
    /// immediately (the editors are immediate-mode and re-read the registry every
    /// frame, so the list itself updates with no extra work).
    ///
    /// JSON is passed via <c>opts.json</c> in one of three shapes:
    ///   • a single entry object       <c>{"id":"x", ...}</c>        (kind from arg[0])
    ///   • an array of entry objects   <c>[{...},{...}]</c>          (kind from arg[0])
    ///   • a whole data-file object    <c>{"spells":[{...}]}</c>     (kind auto-detected from the root key)
    ///
    /// arg[0] (optional when the data-file form is used) names the registry —
    /// spell|unit|item|buff|weapon|armor|shield|potion|flipbook (singular or plural).
    /// <c>opts.open=true</c> also switches the game to that entry's editor first.
    ///
    /// Caveat: spells/items/buffs/weapons/armor work fully at runtime (looked up by
    /// id when used). Units/flipbooks may still need atlas/sprite wiring that only
    /// happens at load time, so a freshly-added unit can render incompletely until a
    /// reload — fine for editing its data, not for spawning a polished unit.
    /// </summary>
    void DevAddData(Necroking.Dev.DevCommand c)
    {
        string raw = c.Opt("json") ?? "";
        if (string.IsNullOrWhiteSpace(raw))
        {
            c.Complete(Necroking.Dev.DevServer.Error(
                "add_data needs opts.json — the entry object, an array of entries, or a whole {\"<key>\":[...]} data-file object"));
            return;
        }

        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            root = doc.RootElement.Clone(); // clone so it outlives the document
        }
        catch (Exception ex)
        {
            c.Complete(Necroking.Dev.DevServer.Error($"bad json: {ex.Message}"));
            return;
        }

        string kind = c.Args.Length >= 1 ? c.Args[0] : "";
        JsonElement entries = root;

        // Data-file form: an object with no "id" of its own and exactly one
        // array-valued property → that property name is the registry and its array
        // holds the entries (e.g. {"spells":[...]}). Don't misread a single entry
        // (which carries an "id") as a data file.
        if (string.IsNullOrEmpty(kind) && root.ValueKind == JsonValueKind.Object
            && !root.TryGetProperty("id", out _))
        {
            string? arrayKey = null;
            int arrayProps = 0;
            foreach (var p in root.EnumerateObject())
                if (p.Value.ValueKind == JsonValueKind.Array) { arrayKey = p.Name; arrayProps++; }
            if (arrayProps == 1 && arrayKey != null)
            {
                kind = arrayKey;
                entries = root.GetProperty(arrayKey);
            }
        }

        if (string.IsNullOrEmpty(kind))
        {
            c.Complete(Necroking.Dev.DevServer.Error(
                "kind unknown — pass it as arg[0] (spell|unit|item|buff|weapon|armor|shield|potion|flipbook) "
                + "or send a {\"<key>\":[...]} data-file object"));
            return;
        }

        var added = new List<string>();
        var errors = new List<string>();
        string norm = kind.Trim().ToLowerInvariant();
        if (norm.EndsWith('s')) norm = norm[..^1]; // plural → singular (spells→spell, armors→armor)

        // Add every element of `entries` (an array, or a single object) into reg.
        void AddAll<TDef>(RegistryBase<TDef> reg) where TDef : class, IHasId, new()
        {
            if (entries.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in entries.EnumerateArray())
                {
                    var def = reg.AddFromJson(el, out string err);
                    if (def != null) added.Add(def.Id); else errors.Add(err);
                }
            }
            else if (entries.ValueKind == JsonValueKind.Object)
            {
                var def = reg.AddFromJson(entries, out string err);
                if (def != null) added.Add(def.Id); else errors.Add(err);
            }
            else
            {
                errors.Add($"json must be an object or array, got {entries.ValueKind}");
            }
        }

        // Map the registry to the editor that shows it (None = no dedicated editor).
        MenuState editorState = MenuState.None;
        switch (norm)
        {
            case "spell":    AddAll(_gameData.Spells);    editorState = MenuState.SpellEditor; break;
            case "unit":     AddAll(_gameData.Units);     editorState = MenuState.UnitEditor;  break;
            case "item":     AddAll(_gameData.Items);     editorState = MenuState.ItemEditor;  break;
            case "buff":     AddAll(_gameData.Buffs);     break;
            case "weapon":   AddAll(_gameData.Weapons);   break;
            case "armor":    AddAll(_gameData.Armors);    break;
            case "shield":   AddAll(_gameData.Shields);   break;
            case "potion":   AddAll(_gameData.Potions);   break;
            case "flipbook": AddAll(_gameData.Flipbooks); break;
            default:
                c.Complete(Necroking.Dev.DevServer.Error(
                    $"unknown data kind: {kind} (spell|unit|item|buff|weapon|armor|shield|potion|flipbook)"));
                return;
        }

        if (added.Count == 0)
        {
            c.Complete(Necroking.Dev.DevServer.Error($"added nothing — {string.Join("; ", errors)}"));
            return;
        }

        // Surface the freshest entry in its editor (optionally opening it first).
        string editorNote = "";
        if (editorState != MenuState.None)
        {
            if (c.OptBool("open")) SetUiPanel(editorState.ToString());
            if (_menuState == editorState)
            {
                string? sel = SelectEditorEntry(added[^1]);
                if (sel != null) editorNote = $", selected \"{sel}\" in {editorState}";
            }
        }

        string errNote = errors.Count > 0 ? $" ({errors.Count} failed: {string.Join("; ", errors)})" : "";
        c.Complete(Necroking.Dev.DevServer.Ok(
            $"added {added.Count} {norm}(s) [{string.Join(", ", added)}] to the live game "
            + $"(runtime only, not saved to disk){editorNote}{errNote}"));
    }
}
