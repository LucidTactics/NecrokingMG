using System;
using System.Collections.Generic;
using System.Linq;
using Necroking.Data;
using Necroking.Data.Registries;

namespace Necroking.UI;

/// <summary>
/// Phase-1 binder for GrimoireDyn: populates the 2-wide tile grid from the
/// spell registry. Each GM_Tile is a union of the four GodMenu3 tile variants;
/// the spell's tileTemplate (summon / evocation / buff / debuff) decides which
/// optional parts show. Interaction (tabs, scrolling, clicks) is Phase 2 —
/// this only renders the data.
/// </summary>
public static class GrimoirePanel
{
    public const string WidgetId = "GrimoireDyn";
    private const int MaxTiles = 22;

    private const string IcoDeathPath = "assets/UI/Imported/Death24.png";

    /// <summary>Write all overrides for the grimoire instance. school = null
    /// shows every visible spell (the "All" tab); otherwise filters.</summary>
    public static void Populate(RuntimeWidgetRenderer r, GameData gameData, string instanceId,
        string? school = null)
    {
        r.ClearOverridesRecursive(instanceId);
        var def = r.GetWidgetDef(WidgetId);
        if (def == null) return;

        var spells = gameData.Spells.All()
            .Where(s => !s.Hidden && !string.IsNullOrEmpty(s.DisplayName))
            .Where(s => school == null || s.School == school)
            .ToList();

        int shown = Math.Min(spells.Count, MaxTiles);
        if (spells.Count > MaxTiles)
            Core.DebugLog.Log("grimoire", $"Populate: {spells.Count} spells, only {MaxTiles} tiles (scroll is Phase 2)");

        for (int i = 0; i < MaxTiles; i++)
        {
            int childIdx = def.Children.FindIndex(c => c.Name == $"tile{i}");
            if (childIdx < 0) continue;
            if (i >= shown)
            {
                r.SetHidden(instanceId, $"tile{i}", true);
                continue;
            }
            BindTile(r, $"{instanceId}.{childIdx}", spells[i]);
        }
    }

    private static void BindTile(RuntimeWidgetRenderer r, string inst, SpellDef s)
    {
        r.SetText(inst, "title", s.DisplayName);
        r.SetText(inst, "path_v", Math.Max(1, s.PrimaryLevel).ToString());
        r.SetImage(inst, "path_i", IcoDeathPath); // all current spells are death path
        r.SetText(inst, "cost_v", s.ManaCost.ToString("0.#"));
        if (!string.IsNullOrEmpty(s.Icon))
            r.SetImage(inst, "icon", s.Icon);

        // Second path/cost slots: only when authored (none of ours yet)
        bool path2 = !string.IsNullOrEmpty(s.SecondaryPath) && s.SecondaryLevel > 0;
        r.SetHidden(inst, "path2_v", !path2);
        r.SetHidden(inst, "path2_i", !path2);
        r.SetHidden(inst, "cost2_v", true);
        r.SetHidden(inst, "cost2_i", true);

        string tpl = string.IsNullOrEmpty(s.TileTemplate) ? "evocation" : s.TileTemplate;
        bool dmg = tpl == "evocation";
        bool buff = tpl == "buff" || tpl == "debuff";
        bool target = tpl == "summon";

        r.SetHidden(inst, "dmg_v", !dmg);
        r.SetHidden(inst, "dmg_m1", true);            // MRN: no MR-negation flag in our data yet
        r.SetHidden(inst, "dmg_m2", !(dmg && s.ArmorNegating));
        if (dmg) r.SetText(inst, "dmg_v", s.Damage.ToString());

        r.SetHidden(inst, "buff_p", !buff);
        r.SetHidden(inst, "buff_i", !buff);
        if (buff)
        {
            r.SetText(inst, "buff_p", tpl == "buff" ? "Buff:" : "Debuff:");
            if (!string.IsNullOrEmpty(s.Icon))
                r.SetImage(inst, "buff_i", s.Icon);
        }

        // Summon target portrait: needs the unit-sprite draw callback — Phase 2.
        r.SetHidden(inst, "target", !target || true);
    }
}
