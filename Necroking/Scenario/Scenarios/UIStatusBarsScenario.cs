using Microsoft.Xna.Framework;
using Necroking.Core;
using Necroking.GameSystems;
using Necroking.Render;

namespace Necroking.Scenario.Scenarios;

/// <summary>
/// [UI test] Renders each HP/Mana bar skin (0 = original, 1..10 reuse grimoire /
/// unit-sheet / tooltip chrome) at partial fill so the fill is visible.
/// Screenshots ui_statusbar_00.png .. ui_statusbar_10.png for design review.
/// </summary>
public class UIStatusBarsScenario : UIScenarioBase
{
    public override string Name => "UIStatusBars";
    public override bool WantsWidgetRenderer => true; // skins use harmonized chrome

    private int _skin;
    private int _phase;
    private float _t;
    private bool _complete;

    public override void OnInit(Simulation sim)
    {
        DebugLog.Clear(ScenarioLog);
        DebugLog.Log(ScenarioLog, "=== UI Status Bars (skin sweep) ===");
        ZoomOnLocation(10f, 10f, 32f);
        BackgroundColor = new Color(40, 46, 56);

        // A necromancer gives the HUD real HP + mana. Knock both to partial so the
        // fill portion is clearly visible in every skin.
        int n = sim.SpawnUnitByID("necromancer", new Vec2(10f, 10f));
        if (n >= 0)
        {
            sim.SetNecromancerIndex(n); // so the HUD renders the HP bar too
            var st = sim.Units[n].Stats;
            sim.UnitsMut[n].Stats.HP = (int)(st.MaxHP * 0.62f);
            DebugLog.Log(ScenarioLog, $"necromancer #{n} HP {sim.Units[n].Stats.HP}/{st.MaxHP}");
        }
        sim.NecroState.Mana = sim.NecroState.MaxMana * 0.78f;
        DebugLog.Log(ScenarioLog, $"mana {sim.NecroState.Mana:F0}/{sim.NecroState.MaxMana:F0}");
    }

    public override void OnTick(Simulation sim, float dt)
    {
        if (_complete) return;
        _t += dt;
        if (_phase == 0 && _t > 0.4f)
        {
            HUDRenderer.DebugSkinOverride = _skin;
            DeferredScreenshot = $"ui_statusbar_{_skin:D2}";
            DebugLog.Log(ScenarioLog, $"shot skin {_skin}");
            _phase = 1;
        }
        else if (_phase == 1 && DeferredScreenshot == null)
        {
            _skin++;
            if (_skin >= HUDRenderer.StatusBarSkinCount)
            {
                HUDRenderer.DebugSkinOverride = -1;
                _complete = true;
            }
            else { _phase = 0; _t = 0f; }
        }
    }

    public override bool IsComplete => _complete;
    public override int OnComplete(Simulation sim)
    {
        HUDRenderer.DebugSkinOverride = -1;
        DebugLog.Log(ScenarioLog, "status-bar skin sweep complete");
        return 0;
    }
}
