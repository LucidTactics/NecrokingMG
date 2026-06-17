using Necroking.Core;
using Necroking.Data;
using Necroking.Game;
using Necroking.GameSystems;

namespace Necroking.Scenario.Scenarios;

/// <summary>
/// Verifies the corpses_eaten milestone gates the Wight transformation: become_wight
/// carries an "event: corpses_eaten" cost that's unaffordable until enough corpses
/// have been eaten, even with the potion cost satisfied.
/// (The eat-a-corpse tally itself lives in the Corpse Eating metamorph action UI.)
/// </summary>
public class CorpsesEatenScenario : ScenarioBase
{
    public override string Name => "corpses_eaten";

    private bool _complete;
    private int _fail; // 0 = pass

    public override void OnInit(Simulation sim)
    {
        DebugLog.Clear(ScenarioLog);
        DebugLog.Log(ScenarioLog, "=== Corpses Eaten / Wight requirement ===");
        var gd = sim.GameData;
        if (gd == null) { Fail(1, "no GameData"); return; }

        var book = new SkillBookState();
        var inv = new Inventory(20, gd.Items);
        inv.AddItem("potion_death_evolution", 5); // satisfy the item cost up front

        var wight = book.FindSkill("become_wight");
        if (wight == null) { Fail(2, "become_wight not found"); return; }

        // The event cost must be present in the data.
        SkillCost? ce = null;
        foreach (var c in wight.Costs)
            if (c.Type == "event" && c.Id == "corpses_eaten") ce = c;
        if (ce == null) { Fail(3, "become_wight has no corpses_eaten event cost"); return; }
        int need = ce.Value.Amount;
        DebugLog.Log(ScenarioLog, $"become_wight requires {need} corpses_eaten (+ potions)");

        // Before eating any corpses: blocked, despite having the potions.
        bool ceBefore = book.CanAffordCost(ce.Value, inv);
        bool wholeBefore = book.CanAfford(wight, inv);
        DebugLog.Log(ScenarioLog, $"before: corpses_eaten={book.Events.Get("corpses_eaten")} costMet={ceBefore} canAfford={wholeBefore}");
        if (ceBefore || wholeBefore) { Fail(4, "wight affordable before eating corpses"); return; }

        // Eat just under the requirement: still blocked.
        book.Events.Tally("corpses_eaten", need - 1);
        if (book.CanAffordCost(ce.Value, inv)) { Fail(5, $"cost met at {need - 1}/{need}"); return; }
        DebugLog.Log(ScenarioLog, $"at {need - 1}/{need}: still blocked (correct)");

        // Reach the requirement: now affordable.
        book.Events.Tally("corpses_eaten", 1);
        bool ceAfter = book.CanAffordCost(ce.Value, inv);
        bool wholeAfter = book.CanAfford(wight, inv);
        DebugLog.Log(ScenarioLog, $"after: corpses_eaten={book.Events.Get("corpses_eaten")} costMet={ceAfter} canAfford={wholeAfter}");
        if (!ceAfter) { Fail(6, "corpses_eaten cost still unmet at requirement"); return; }
        if (!wholeAfter) { Fail(7, "become_wight not affordable with potions + corpses"); return; }

        // Milestone is not consumed by checking: still met after.
        if (!book.CanAffordCost(ce.Value, inv)) { Fail(8, "milestone consumed unexpectedly"); return; }

        DebugLog.Log(ScenarioLog, "All checks passed.");
    }

    private void Fail(int code, string why)
    {
        _fail = code;
        DebugLog.Log(ScenarioLog, $"FAIL ({code}): {why}");
    }

    public override void OnTick(Simulation sim, float dt) => _complete = true;
    public override bool IsComplete => _complete;

    public override int OnComplete(Simulation sim)
    {
        DebugLog.Log(ScenarioLog, $"Result: {(_fail == 0 ? "PASS" : $"FAIL ({_fail})")}");
        return _fail;
    }
}
