using System;
using System.Collections.Generic;
using System.Linq;
using Necroking.Core;
using Necroking.Data;
using Necroking.Data.Registries;

namespace Necroking.UI;

/// <summary>
/// Binder for GrimoireDyn: populates the 2-wide tile grid from the spell
/// registry, optionally filtered by school (tab) and/or magic path (icon
/// strip). Each GrimoireSpellTile is a union of the four GodMenu3 tile variants; the
/// spell's tileTemplate (summon / evocation / buff / debuff) decides which
/// optional parts show. Returns the shown spells (index = tile index) so the
/// overlay can map a tile click back to a spell.
/// </summary>
public static class GrimoirePanel
{
    public const string WidgetId = "GrimoireDyn";
    public const int MaxTiles = 22;

    private const string IcoFatigue = "assets/UI/Imported/exhausted.2.24.png";

    /// <summary>The full filtered spell list (no tile cap), in display order.
    /// school == null and path == None mean "no filter on that axis". Pulled out
    /// of <see cref="Populate"/> so the overlay can size its scroll window off the
    /// real count without binding tiles.</summary>
    public static List<SpellDef> Filter(GameData gameData,
        string? school = null, MagicPath path = MagicPath.None, Func<SpellDef, bool>? canShow = null)
        => gameData.Spells.All()
            .Where(s => !s.Hidden && !string.IsNullOrEmpty(s.DisplayName))
            .Where(s => school == null || s.School == school)
            .Where(s => path == MagicPath.None || MagicPathHelpers.FromJsonId(s.PrimaryPath) == path)
            .Where(s => canShow == null || canShow(s))
            .ToList();

    /// <summary>Write all overrides for the grimoire instance and return the
    /// spells shown (parallel to tile indices). school == null and path == None
    /// mean "no filter on that axis". <paramref name="firstTile"/> scrolls the
    /// 22-tile viewport: tiles bind to the window [firstTile, firstTile+MaxTiles)
    /// of the filtered list.</summary>
    public static List<SpellDef> Populate(RuntimeWidgetRenderer r, GameData gameData,
        string instanceId, string? school = null, MagicPath path = MagicPath.None,
        Func<SpellDef, bool>? canShow = null, int firstTile = 0)
        => BindWindow(r, instanceId, Filter(gameData, school, path, canShow), firstTile);

    /// <summary>Bind the 22 physical tiles to the window of <paramref name="spells"/>
    /// starting at <paramref name="firstTile"/>, hiding any past the list end, and
    /// return the spells actually shown (index = visible tile index, so a tile click
    /// maps straight to the returned list). The viewport into a longer list is how the
    /// grimoire scrolls without per-tile widget repositioning.</summary>
    public static List<SpellDef> BindWindow(RuntimeWidgetRenderer r, string instanceId,
        List<SpellDef> spells, int firstTile)
    {
        r.ClearOverridesRecursive(instanceId);
        var def = r.GetWidgetDef(WidgetId);
        if (def == null) return new List<SpellDef>();
        if (firstTile < 0) firstTile = 0;

        var visible = new List<SpellDef>(MaxTiles);
        for (int i = 0; i < MaxTiles; i++)
        {
            int childIdx = def.Children.FindIndex(c => c.Name == $"tile{i}");
            if (childIdx < 0) continue;
            int sIdx = firstTile + i;
            if (sIdx >= spells.Count)
            {
                r.SetHidden(instanceId, $"tile{i}", true);
                continue;
            }
            BindTile(r, $"{instanceId}.{childIdx}", spells[sIdx]);
            visible.Add(spells[sIdx]);
        }
        return visible;
    }

    private static void BindTile(RuntimeWidgetRenderer r, string inst, SpellDef s)
    {
        r.SetText(inst, "title", s.DisplayName);
        // Path icon + level — hidden for PATHLESS skills (PrimaryPath None), e.g.
        // the non-magical Skill-tab abilities, so they don't show a bogus path req.
        var primPath = MagicPathHelpers.FromJsonId(s.PrimaryPath);
        bool pathless = primPath == MagicPath.None;
        r.SetHidden(inst, "path_v", pathless);
        r.SetHidden(inst, "path_i", pathless);
        if (!pathless)
        {
            r.SetText(inst, "path_v", Math.Max(1, s.PrimaryLevel).ToString());
            r.SetImage(inst, "path_i", MagicPathHelpers.IconPath(primPath, 24));
        }
        // Cost = the spell's casting cost (mana — the fatigue-analog), shown
        // with the fatigue icon. The green gem icon is reserved for future
        // gem-cost spells (none authored yet); without the override the
        // summon-tile template default (NatureCrystal gem) leaks through.
        r.SetText(inst, "cost_v", s.ManaCost.ToString("0.#"));
        r.SetImage(inst, "cost_i", IcoFatigue);
        if (!string.IsNullOrEmpty(s.Icon))
            r.SetImage(inst, "icon", s.Icon);
        else
           r.SetImage(inst, "icon", GamePaths.PlaceholderSpellIcon);

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
