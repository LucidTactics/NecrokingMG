using Necroking.Core;
using Necroking.GameSystems;

namespace Necroking.Scenario.Scenarios;

/// <summary>
/// [UI test] Renders the HUD spell bar so the grimoire-framed slots (parchment
/// backing + spell icon + SpiderFrame) can be visually checked. Slot contents
/// come from data/spellbar.json. Screenshot: ui_spellbar.png.
/// </summary>
public class UISpellBarScenario : UIScenarioBase
{
    public override string Name => "UISpellBar";
    public override bool WantsWidgetRenderer => true; // HUD reuses grimoire frame elements
    public override string[] DebugPrimarySpells => new[] { "summon_skeleton", "fireball", "raise_zombie", "lightning_zap" };
    public override string[] DebugSecondarySpells => new[] { "god_ray", "nether_darts", "sky_lightning", "life_drain", "potion_frenzy", "potion_paralysis" };

    private float _t;
    private int _phase;
    private bool _complete;

    public override void OnInit(Simulation sim)
    {
        DebugLog.Clear(ScenarioLog);
        DebugLog.Log(ScenarioLog, "=== UI Spell Bar Scenario ===");
        ZoomOnLocation(10f, 10f, 32f);
        // A necromancer gives the HUD real mana so spells don't all read as
        // low-mana (red tint) in the capture.
        sim.SpawnUnitByID("necromancer", new Vec2(10f, 10f));
    }

    public override void OnTick(Simulation sim, float dt)
    {
        if (_complete) return;
        _t += dt;
        if (_phase == 0 && _t > 0.6f) { DeferredScreenshot = "ui_spellbar"; _phase = 1; }
        else if (_phase == 1 && DeferredScreenshot == null) _complete = true;
    }

    public override bool IsComplete => _complete;
    public override int OnComplete(Simulation sim) => 0;
}
