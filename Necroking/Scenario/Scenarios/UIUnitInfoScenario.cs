using Microsoft.Xna.Framework;
using Necroking.Core;
using Necroking.Data;
using Necroking.GameSystems;

namespace Necroking.Scenario.Scenarios;

/// <summary>
/// [UI test] Renders the UnitInfoPanel (imported Unit Tooltip2 sheet wired to
/// live unit data) for a soldier (full loadout: weapons/armor/shield rows) and
/// then a skeleton (sparse loadout: unused rows must hide cleanly).
/// Screenshots: ui_unitinfo_soldier.png, ui_unitinfo_skeleton.png.
/// </summary>
public class UIUnitInfoScenario : ScenarioBase
{
    public override string Name => "UIUnitInfo";
    public override bool WantsWidgetRenderer => true;

    private float _elapsed;
    private int _phase;
    private bool _complete;
    private int _soldierIdx = -1, _skeletonIdx = -1;
    private UI.UnitInfoPanel? _panel;

    public override void OnInit(Simulation sim)
    {
        DebugLog.Clear(ScenarioLog);
        DebugLog.Log(ScenarioLog, "=== UI Unit Info Panel Scenario ===");
        ZoomOnLocation(10f, 10f, 32f);
        BackgroundColor = new Color(58, 62, 72);
        BloomOverride = new Data.Registries.BloomSettings { Enabled = false };

        // SpawnUnitByID (not raw AddUnit) so stats/weapons/armor come from the def
        _soldierIdx = sim.SpawnUnitByID("soldier", new Vec2(8f, 10f));
        _skeletonIdx = sim.SpawnUnitByID("skeleton", new Vec2(60f, 60f)); // far apart so they don't fight
        var s = sim.Units[_soldierIdx].Stats;
        DebugLog.Log(ScenarioLog,
            $"soldier: melee={s.MeleeWeapons.Count} ranged={s.RangedWeapons.Count} " +
            $"shieldProt={s.ShieldProtection} bodyProt={s.Armor.BodyProtection} headProt={s.Armor.HeadProtection}");

        CustomUIDraw = (batch, screenW, screenH) =>
        {
            if (WidgetRenderer == null) return;
            if (_panel == null)
            {
                _panel = new UI.UnitInfoPanel();
                _panel.Init(WidgetRenderer, sim.GameData);
                _panel.DrawUnitIconCallback = DrawUnitSprite == null ? null
                    : (defId, rect) => DrawUnitSprite(defId, rect);
                _panel.ShowForUnit(_soldierIdx);
                DebugLog.Log(ScenarioLog, "panel created, showing soldier");
            }
            _panel.Draw(screenW, screenH, sim);
        };
    }

    public override void OnTick(Simulation sim, float dt)
    {
        _elapsed += dt;
        if (_phase == 0 && _elapsed > 0.5f && _panel != null)
        {
            DeferredScreenshot = "ui_unitinfo_soldier";
            _phase = 1;
        }
        else if (_phase == 1 && DeferredScreenshot == null)
        {
            DebugLog.Log(ScenarioLog, "soldier shot taken, switching to skeleton");
            _panel!.ShowForUnit(_skeletonIdx);
            DeferredScreenshot = "ui_unitinfo_skeleton";
            _phase = 2;
        }
        else if (_phase == 2 && DeferredScreenshot == null)
        {
            if (LaunchArgs.Headless)
            {
                _panel?.Hide();
                DebugLog.Log(ScenarioLog, "both screenshots taken, complete");
                _complete = true;
            }
        }
    }

    public override bool IsComplete => _complete;

    public override int OnComplete(Simulation sim)
    {
        DebugLog.Log(ScenarioLog, "Result: PASS");
        return 0;
    }
}
