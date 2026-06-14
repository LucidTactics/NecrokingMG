using Microsoft.Xna.Framework;
using Necroking.Core;
using Necroking.GameSystems;

namespace Necroking.Scenario.Scenarios;

/// <summary>
/// [UI test] Renders the HP/Mana bars at partial fill (HP 62%, Mana 78%) so the
/// parchment + gold-trim design and the fill level are visible. Screenshot:
/// ui_statusbar.png.
/// </summary>
public class UIStatusBarsScenario : UIScenarioBase
{
    public override string Name => "UIStatusBars";
    public override bool WantsWidgetRenderer => true; // the bar uses harmonized chrome

    private float _t;
    private int _phase;
    private bool _complete;

    public override void OnInit(Simulation sim)
    {
        DebugLog.Clear(ScenarioLog);
        DebugLog.Log(ScenarioLog, "=== UI Status Bars ===");
        ZoomOnLocation(10f, 10f, 32f);
        BackgroundColor = new Color(40, 46, 56);

        int n = sim.SpawnUnitByID("necromancer", new Vec2(10f, 10f));
        if (n >= 0)
        {
            sim.SetNecromancerIndex(n); // so the HUD renders the HP bar too
            var st = sim.Units[n].Stats;
            sim.UnitsMut[n].Stats.HP = (int)(st.MaxHP * 0.62f);
            DebugLog.Log(ScenarioLog, $"necromancer #{n} HP {sim.Units[n].Stats.HP}/{st.MaxHP}");
        }
        sim.NecroState.Mana = sim.NecroState.MaxMana * 0.78f;
    }

    public override void OnTick(Simulation sim, float dt)
    {
        if (_complete) return;
        _t += dt;
        if (_phase == 0 && _t > 0.5f) { DeferredScreenshot = "ui_statusbar"; _phase = 1; }
        else if (_phase == 1 && DeferredScreenshot == null) _complete = true;
    }

    public override bool IsComplete => _complete;
    public override int OnComplete(Simulation sim) => 0;
}
