using Microsoft.Xna.Framework;
using Necroking.Core;
using Necroking.Data.Registries;
using Necroking.GameSystems;

namespace Necroking.Scenario.Scenarios;

/// <summary>
/// [UI test] Drives the GrimoireOverlay's click handling directly (no OS
/// cursor) to verify Phase-2 interaction: path-tab filter, school-tab filter,
/// the "All" resets, and tile-click assignment firing the pick callback.
/// Headless pass/fail via asserts logged to scenario.log.
/// </summary>
public class UIGrimoireClickScenario : ScenarioBase
{
    public override string Name => "UIGrimoireClick";
    public override bool WantsWidgetRenderer => true;

    private UI.GrimoireOverlay? _grim;
    private string _picked = "";
    private int _frames;
    private bool _complete;
    private int _fail;

    public override void OnInit(Simulation sim)
    {
        DebugLog.Clear(ScenarioLog);
        DebugLog.Log(ScenarioLog, "=== UI Grimoire Click Scenario ===");
        BackgroundColor = new Color(58, 62, 72);
        BloomOverride = new Data.Registries.BloomSettings { Enabled = false };

        CustomUIDraw = (batch, screenW, screenH) =>
        {
            if (WidgetRenderer == null) return;
            if (_grim == null)
            {
                _grim = new UI.GrimoireOverlay();
                _grim.Init(WidgetRenderer, sim.GameData);
                _grim.OpenForAssign(id => _picked = id);
            }
            _grim.Draw(screenW, screenH);
        };
    }

    private void Check(string label, bool ok)
    {
        DebugLog.Log(ScenarioLog, $"{(ok ? "PASS" : "FAIL")}: {label}");
        if (!ok) _fail++;
    }

    public override void OnTick(Simulation sim, float dt)
    {
        _frames++;
        if (_grim == null || _frames < 3) return; // let Draw set the layout first

        int baseline = _grim.DebugShownCount;
        Check($"All shows 22 (got {baseline})", baseline == 22);

        // Path filter: Shock = path tab #1 (tab0 is "All") in the PathTabBar.
        var shockPt = _grim.DebugTabCenter("PathTabBar", 1);
        _grim.HandleClickAt(shockPt.X, shockPt.Y);
        Check($"Shock filter -> 4 (got {_grim.DebugShownCount})", _grim.DebugShownCount == 4);

        // Path All (tab0) resets
        var pAll = _grim.DebugTabCenter("PathTabBar", 0);
        _grim.HandleClickAt(pAll.X, pAll.Y);
        Check($"Path-All resets -> 22 (got {_grim.DebugShownCount})", _grim.DebugShownCount == 22);

        // School filter: Conjuration = school tab #1 (tab0 is "All").
        var conj = _grim.DebugTabCenter("SchoolTabBar", 1);
        _grim.HandleClickAt(conj.X, conj.Y);
        int conjCount = _grim.DebugShownCount;
        DebugLog.Log(ScenarioLog, $"Conjuration count = {conjCount}");
        Check("Conjuration filter < All", conjCount > 0 && conjCount < 22);

        // School All (tab0) resets
        var sAll = _grim.DebugTabCenter("SchoolTabBar", 0);
        _grim.HandleClickAt(sAll.X, sAll.Y);
        Check($"School-All resets -> 22 (got {_grim.DebugShownCount})", _grim.DebugShownCount == 22);

        // New Skill tab (#4): filters to school "Skill" -> the Command skill.
        var skill = _grim.DebugTabCenter("SchoolTabBar", 4);
        _grim.HandleClickAt(skill.X, skill.Y);
        string skill0 = _grim.DebugShownId(0);
        DebugLog.Log(ScenarioLog, $"Skill tab: count={_grim.DebugShownCount} first='{skill0}'");
        Check($"Skill tab shows order_attack (count={_grim.DebugShownCount}, id='{skill0}')",
            _grim.DebugShownCount >= 1 && skill0 == "order_attack");

        // Construction tab (#5) still works after the Skill insertion / tab renumber.
        var constr = _grim.DebugTabCenter("SchoolTabBar", 5);
        _grim.HandleClickAt(constr.X, constr.Y);
        int constrCount = _grim.DebugShownCount;
        DebugLog.Log(ScenarioLog, $"Construction tab count={constrCount}");
        Check($"Construction filter > 0 and < All (got {constrCount})", constrCount > 0 && constrCount < 22);

        // Reset to All for the tile-click assign check below.
        var sAll2 = _grim.DebugTabCenter("SchoolTabBar", 0);
        _grim.HandleClickAt(sAll2.X, sAll2.Y);

        // Tile click assigns: tile0 -> pick callback fires with shown[0]
        string expect = _grim.DebugShownId(0);
        var t0 = _grim.DebugChildCenter("tile0");
        _grim.HandleClickAt(t0.X, t0.Y);
        Check($"tile0 click assigned '{_picked}' (expected '{expect}')", _picked == expect && _picked != "");
        Check("overlay hid after assign", !_grim.IsVisible);

        _complete = true;
    }

    public override bool IsComplete => _complete;
    public override int OnComplete(Simulation sim)
    {
        DebugLog.Log(ScenarioLog, _fail == 0 ? "ALL CHECKS PASSED" : $"{_fail} CHECK(S) FAILED");
        return _fail;
    }
}
