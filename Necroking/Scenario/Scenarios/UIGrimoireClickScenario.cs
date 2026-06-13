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

        // Path filter: Shock = the first path tab (backing PathTab_Shock_Backing)
        var shockPt = _grim.DebugChildCenter("PathTab_Shock_Backing");
        _grim.HandleClickAt(shockPt.X, shockPt.Y);
        Check($"Shock filter -> 4 (got {_grim.DebugShownCount})", _grim.DebugShownCount == 4);

        // Path All resets
        var pAll = _grim.DebugChildCenter("PathTab_All_Backing");
        _grim.HandleClickAt(pAll.X, pAll.Y);
        Check($"Path-All resets -> 22 (got {_grim.DebugShownCount})", _grim.DebugShownCount == 22);

        // School filter: Conjuration (backing SchoolTab_Conjuration_Backing)
        var conj = _grim.DebugChildCenter("SchoolTab_Conjuration_Backing");
        _grim.HandleClickAt(conj.X, conj.Y);
        int conjCount = _grim.DebugShownCount;
        DebugLog.Log(ScenarioLog, $"Conjuration count = {conjCount}");
        Check("Conjuration filter < All", conjCount > 0 && conjCount < 22);

        // School All resets (backing SchoolTab_All_Backing)
        var sAll = _grim.DebugChildCenter("SchoolTab_All_Backing");
        _grim.HandleClickAt(sAll.X, sAll.Y);
        Check($"School-All resets -> 22 (got {_grim.DebugShownCount})", _grim.DebugShownCount == 22);

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
