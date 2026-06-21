using Necroking.Core;
using Necroking.Game;
using Necroking.GameSystems;

namespace Necroking.Scenario.Scenarios;

/// <summary>
/// Validates the Skillpoint Potion item: that its consumable fields deserialize
/// from items.json (skillPointPool / skillPointAmount) and that granting those
/// points through SkillBookState works. This is the data/logic half of the
/// click-to-consume feature (the inventory click wiring is UI and tested by hand).
/// </summary>
public class ItemConsumeScenario : ScenarioBase
{
    public override string Name => "item_consume";

    private bool _complete;
    private bool _pass;

    public override void OnInit(Simulation sim)
    {
        DebugLog.Clear(ScenarioLog);
        DebugLog.Log(ScenarioLog, $"=== Scenario: {Name} ===");

        var gd = sim.GameData;
        if (gd == null) { DebugLog.Log(ScenarioLog, "ERROR: no GameData"); _complete = true; return; }

        var def = gd.Items.Get("potion_skillpoint");
        if (def == null)
        {
            DebugLog.Log(ScenarioLog, "FAIL: item 'potion_skillpoint' not found in items.json");
            _complete = true;
            return;
        }

        DebugLog.Log(ScenarioLog,
            $"Loaded def: name='{def.DisplayName}', pool='{def.SkillPointPool}', amount={def.SkillPointAmount}, icon='{def.Icon}'");

        bool fieldsOk = def.DisplayName == "Skillpoint Potion"
            && def.SkillPointPool == "potions"
            && def.SkillPointAmount == 10;

        // Exercise the grant path the consume handler uses.
        var book = new SkillBookState();
        int before = book.GetSkillPoints(def.SkillPointPool);
        book.AddSkillPoints(def.SkillPointPool, def.SkillPointAmount);
        int after = book.GetSkillPoints(def.SkillPointPool);
        DebugLog.Log(ScenarioLog, $"Skill points '{def.SkillPointPool}': {before} -> {after}");

        bool grantOk = after - before == def.SkillPointAmount && after == 10;

        _pass = fieldsOk && grantOk;
        DebugLog.Log(ScenarioLog, $"fieldsOk={fieldsOk}, grantOk={grantOk}");
        DebugLog.Log(ScenarioLog, _pass ? "RESULT: PASS" : "RESULT: FAIL");
        _complete = true;
    }

    public override void OnTick(Simulation sim, float dt) { }
    public override bool IsComplete => _complete;
    public override int OnComplete(Simulation sim) => _pass ? 0 : 1;
}
