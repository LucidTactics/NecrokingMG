using Microsoft.Xna.Framework;
using Necroking.Core;
using Necroking.GameSystems;

namespace Necroking.Scenario.Scenarios;

/// <summary>
/// [UI test] First-pass renders of the GodMenu3 imports: GrimoireWindow +
/// the three spell tooltips, at fixed crops for capture comparison.
/// Run with --resolution 1700x1250. Screenshot: ui_grimoire.png.
/// </summary>
public class UIGrimoireScenario : ScenarioBase
{
    public override string Name => "UIGrimoire";
    public override bool WantsWidgetRenderer => true;

    private float _elapsed;
    private int _phase;
    private bool _complete;

    public override void OnInit(Simulation sim)
    {
        DebugLog.Clear(ScenarioLog);
        DebugLog.Log(ScenarioLog, "=== UI Grimoire Scenario ===");
        ZoomOnLocation(10f, 10f, 32f);
        BackgroundColor = new Color(58, 62, 72);
        BloomOverride = new Data.Registries.BloomSettings { Enabled = false };

        bool bound = false;
        CustomUIDraw = (batch, screenW, screenH) =>
        {
            if (WidgetRenderer == null) return;
            if (!bound)
            {
                bound = true;
                // All view (left) + filter verification (logged counts)
                var all = UI.GrimoirePanel.Populate(WidgetRenderer, sim.GameData, "grimoire");
                var evo = UI.GrimoirePanel.Populate(WidgetRenderer, sim.GameData, "grim_evo", "Evocation");
                var shock = UI.GrimoirePanel.Populate(WidgetRenderer, sim.GameData, "grim_shock",
                    null, Necroking.Data.Registries.MagicPath.Shock);
                var constr = UI.GrimoirePanel.Populate(WidgetRenderer, sim.GameData, "grim_shock", "Construction");
                DebugLog.Log(ScenarioLog, $"All={all.Count} Evocation={evo.Count} Shock={shock.Count} Construction={constr.Count}");
                DebugLog.Log(ScenarioLog, "Construction spells: " + string.Join(", ", constr.ConvertAll(s => s.DisplayName)));
                // Left: default All view, "All" tabs lit. Right: Evocation school
                // + Shock path selected, so the screenshot shows both school-tab
                // and path-tab highlighting (active bright, rest dimmed).
                UI.GrimoirePanel.Populate(WidgetRenderer, sim.GameData, "grimoire");
                UI.GrimoireOverlay.ApplyTabHighlights(WidgetRenderer, "grimoire",
                    null, Necroking.Data.Registries.MagicPath.None);
                UI.GrimoirePanel.Populate(WidgetRenderer, sim.GameData, "grim_shock", "Construction");
                UI.GrimoireOverlay.ApplyTabHighlights(WidgetRenderer, "grim_shock",
                    "Construction", Necroking.Data.Registries.MagicPath.None);
            }
            WidgetRenderer.DrawWidget(UI.GrimoirePanel.WidgetId, 10, 10, "grimoire");
            WidgetRenderer.DrawWidget(UI.GrimoirePanel.WidgetId, 730, 10, "grim_shock");
        };
    }

    public override void OnTick(Simulation sim, float dt)
    {
        _elapsed += dt;
        if (_phase == 0 && _elapsed > 0.5f)
        {
            DeferredScreenshot = "ui_grimoire";
            _phase = 1;
        }
        else if (_phase == 1 && DeferredScreenshot == null && LaunchArgs.Headless)
        {
            _complete = true;
        }
    }

    public override bool IsComplete => _complete;
    public override int OnComplete(Simulation sim) => 0;
}
