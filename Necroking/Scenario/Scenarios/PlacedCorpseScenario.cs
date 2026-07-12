using Necroking.Core;
using Necroking.Data;
using Necroking.GameSystems;
using Necroking.Lib;
using Necroking.World;

namespace Necroking.Scenario.Scenarios;

/// <summary>
/// Verifies Simulation.SpawnCorpseFromUnit — the path the map editor's
/// "Place as corpse" toggle uses. Spawns a row of living soldiers and a row of
/// living skeletons, then converts every unit to a corpse via the helper and
/// screenshots the result. A pass means: the units were removed from the unit
/// array, the corpse count matches, and each corpse kept its UnitDefID (so it
/// renders a real death sprite rather than vanishing).
/// </summary>
public class PlacedCorpseScenario : ScenarioBase
{
    public override string Name => "placed_corpse";

    private const int PerRow = 5;
    private float _elapsed;
    private int _phase;
    private bool _complete;

    private int _spawnedUnits;
    private int _corpsesWithDef;

    public override void OnInit(Simulation sim)
    {
        DebugLog.Clear(ScenarioLog);
        DebugLog.Log(ScenarioLog, $"=== Scenario: {Name} ===");

        // Left half: LIVING reference units (kept alive). Right half: the same unit
        // types converted to corpses via SpawnCorpseFromUnit. The screenshot lets us
        // confirm the right side renders death poses, not standing units.
        float cy = 32f;
        // Living reference column pair (x=24 soldier row, x=24 skeleton row).
        int ls = sim.UnitsMut.AddUnit(new Vec2(24f, cy - 3f), UnitType.Dynamic);
        sim.UnitsMut[ls].UnitDefID = "soldier";
        int lk = sim.UnitsMut.AddUnit(new Vec2(24f, cy + 3f), UnitType.Dynamic);
        sim.UnitsMut[lk].UnitDefID = "skeleton";
        int livingKeep = sim.Units.Count; // these stay alive

        // Units destined to become corpses, to the right.
        for (int i = 0; i < PerRow; i++)
        {
            float x = 30f + i * 3f;
            int s = sim.UnitsMut.AddUnit(new Vec2(x, cy - 3f), UnitType.Dynamic);
            sim.UnitsMut[s].UnitDefID = "soldier";
            int k = sim.UnitsMut.AddUnit(new Vec2(x, cy + 3f), UnitType.Dynamic);
            sim.UnitsMut[k].UnitDefID = "skeleton";
        }
        _spawnedUnits = sim.Units.Count - livingKeep;
        DebugLog.Log(ScenarioLog, $"Spawned {sim.Units.Count} units; {_spawnedUnits} to convert, {livingKeep} kept living");

        // Convert only the right-side units to corpses (indices >= livingKeep).
        for (int i = sim.Units.Count - 1; i >= livingKeep; i--)
        {
            var corpse = sim.SpawnCorpseFromUnit(i);
            if (corpse != null && !string.IsNullOrEmpty(corpse.UnitDefID))
                _corpsesWithDef++;
        }
        DebugLog.Log(ScenarioLog,
            $"After conversion: units={sim.Units.Count}, corpses={sim.Corpses.Count}, corpsesWithDef={_corpsesWithDef}");

        ZoomOnLocation(31f, 32f, 22f);
    }

    public override void OnTick(Simulation sim, float dt)
    {
        _elapsed += dt;
        // Screenshot almost immediately: a PreSettled corpse must already be on the
        // final death frame (collapsed) on the very first rendered frame — it must
        // NOT be mid-death-animation. If corpses look standing/falling here, the
        // "start dead" path is broken.
        if (_phase == 0 && _elapsed > 0.05f)
        {
            DeferredScreenshot = "placed_corpse";
            _phase = 1;
        }
        else if (_phase == 1 && _elapsed > 0.5f)
        {
            _complete = true;
        }
    }

    public override bool IsComplete => _complete;

    public override int OnComplete(Simulation sim)
    {
        int expectedCorpses = _spawnedUnits;
        bool livingKept = sim.Units.Count == 2; // the two reference units stay alive
        bool corpseCountOk = sim.Corpses.Count == expectedCorpses;
        bool defsKept = _corpsesWithDef == expectedCorpses;

        DebugLog.Log(ScenarioLog, "=== Summary ===");
        DebugLog.Log(ScenarioLog, $"living kept:     {livingKept} (units={sim.Units.Count}, expected 2)");
        DebugLog.Log(ScenarioLog, $"corpse count:    {corpseCountOk} (corpses={sim.Corpses.Count}, expected {expectedCorpses})");
        DebugLog.Log(ScenarioLog, $"UnitDefID kept:  {defsKept} ({_corpsesWithDef}/{expectedCorpses})");

        bool pass = livingKept && corpseCountOk && defsKept;
        DebugLog.Log(ScenarioLog, pass ? "RESULT: PASS" : "RESULT: FAIL");
        return pass ? 0 : 1;
    }
}
