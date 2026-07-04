using Necroking.Core;
using Necroking.Game;
using Necroking.GameSystems;
using Necroking.Movement;

namespace Necroking.Scenario.Scenarios;

/// <summary>
/// Validates the Corpse Eater AI behavior end-to-end:
///   1. Unlock the corpse_eat behavior via SkillEffect.
///   2. Spawn a ZombieWolf next to two corpses — one fresh (age 0), one aged
///      past the 10s gate (we cheat the age by waiting the sim forward).
///   3. Assert the wolf does NOT eat the fresh corpse but DOES eat the aged
///      one, gaining buff_corpse_meal.
///
/// Coverage:
///   - Age gate (10s minimum).
///   - Per-unit stack cap (1 for Corpse Eater).
///   - Buff actually lands on the unit (verified via active-buff list).
///   - +MaxHP buff is a REAL HP gain (current HP and effective max both rise).
///
/// Out of scope (deferred):
///   - Active pathfinding to far corpses. Today's behaviour is passive: the
///     wolf eats whatever is within EatRadius. The corpse is placed in range
///     so passive consumption fires.
/// </summary>
public class CorpseEaterScenario : ScenarioBase
{
    public override string Name => "corpse_eater";
    public override int GridSize => 32;

    private float _t;
    private int _wolfIdx = -1;
    private int _freshCorpseID = -1;
    private int _agedCorpseID = -1;
    private bool _setupOk;
    private bool _result;
    private SkillBookState _bookState = new();
    private int _wolfHpBefore;
    private int _wolfBaseMaxHpBefore;

    public override void OnInit(Simulation sim)
    {
        DebugLog.Clear(ScenarioLog);
        DebugLog.Log(ScenarioLog, "=== corpse_eater scenario start ===");

        sim.SetSkillBook(_bookState);

        // Unlock corpse_eat (stack cap 1, matching Corpse Eater skill).
        var ctx = new Game.SkillEffects.SkillEffectContext
        {
            Inventory = new Inventory(20, sim.GameData!.Items),
            GameData = sim.GameData!,
            BookState = _bookState,
            Sim = sim,
        };
        Game.SkillEffects.SkillEffectRegistry.Apply("unlock_ai_behavior", ctx, "corpse_eat:1");

        _wolfIdx = sim.SpawnUnitByID("ZombieWolf", new Vec2(10, 10));
        if (_wolfIdx < 0) { DebugLog.Log(ScenarioLog, "FAIL: ZombieWolf spawn failed"); return; }
        _wolfHpBefore = sim.UnitsMut[_wolfIdx].Stats.HP;
        _wolfBaseMaxHpBefore = sim.UnitsMut[_wolfIdx].Stats.MaxHP;

        // Place two corpses near the wolf — one with Age = 0 (fresh) that
        // should NOT be eaten, and one we pre-age by mutating its Age field
        // past the gate so the wolf has a valid eligible target immediately.
        // The wolf sits at (10,10); EatRadius is 1.5, so within 1 unit each
        // direction is comfortably inside range.
        var corpses = sim.CorpsesMut;
        corpses.Add(new Corpse
        {
            Position = new Vec2(10.5f, 10f),
            UnitDefID = "ZombieFemaleDeer",
            CorpseID = 9000,
            Age = 0f,
            Bagged = false,
        });
        corpses.Add(new Corpse
        {
            Position = new Vec2(9.5f, 10f),
            UnitDefID = "ZombieFemaleDeer",
            CorpseID = 9001,
            Age = 11f, // already past the 10s gate
            Bagged = false,
        });
        _freshCorpseID = 9000;
        _agedCorpseID = 9001;
        _setupOk = true;
        DebugLog.Log(ScenarioLog, $"Spawned wolf at idx {_wolfIdx} with two corpses ({_freshCorpseID}=fresh, {_agedCorpseID}=aged)");
    }

    public override void OnTick(Simulation sim, float dt) { _t += dt; }

    public override bool IsComplete => _t > 4.0f; // EatDuration=2s + buffer

    public override int OnComplete(Simulation sim)
    {
        if (!_setupOk) return 1;

        // Aged corpse should be gone (consumed / dissolved) by now; fresh
        // corpse should still be there with low age. Wolf should have one
        // stack of buff_corpse_meal.
        int agedIdx = sim.FindCorpseIndexByID(_agedCorpseID);
        int freshIdx = sim.FindCorpseIndexByID(_freshCorpseID);
        bool agedEaten = agedIdx < 0 || sim.Corpses[agedIdx].ConsumedBySummon;
        bool freshSurvived = freshIdx >= 0 && !sim.Corpses[freshIdx].ConsumedBySummon;
        bool buffLanded = BuffSystem.HasBuff(sim.UnitsMut, _wolfIdx, "buff_corpse_meal");
        byte eaten = sim.UnitsMut[_wolfIdx].CorpsesEaten;

        // The +MaxHP buff must be a REAL HP gain: current HP rises by the buff's
        // MaxHP value, and EffectiveMaxHP rises by the same (base MaxHP unchanged).
        int expectGain = 0;
        var mealDef = sim.GameData.Buffs.Get("buff_corpse_meal");
        if (mealDef != null)
            foreach (var e in mealDef.Effects)
                if (e.Stat == "MaxHP") expectGain += (int)e.Value;
        int hpAfter = sim.UnitsMut[_wolfIdx].Stats.HP;
        int effMaxAfter = BuffSystem.EffectiveMaxHP(sim.UnitsMut, _wolfIdx);
        bool hpGranted = buffLanded
            && hpAfter == _wolfHpBefore + expectGain
            && effMaxAfter == _wolfBaseMaxHpBefore + expectGain;

        DebugLog.Log(ScenarioLog, $"Aged corpse consumed: {agedEaten} (expect True)");
        DebugLog.Log(ScenarioLog, $"Fresh corpse survived: {freshSurvived} (expect True — age < 10s)");
        DebugLog.Log(ScenarioLog, $"buff_corpse_meal on wolf: {buffLanded} (expect True)");
        DebugLog.Log(ScenarioLog, $"Wolf CorpsesEaten counter: {eaten} (expect 1)");
        DebugLog.Log(ScenarioLog, $"Wolf HP {_wolfHpBefore}->{hpAfter}, effMaxHP {_wolfBaseMaxHpBefore}->{effMaxAfter} (expect +{expectGain} each) -> realHpBuff={hpGranted}");

        _result = agedEaten && freshSurvived && buffLanded && eaten == 1 && hpGranted;
        DebugLog.Log(ScenarioLog, $"result: {(_result ? "PASS" : "FAIL")}");
        return _result ? 0 : 1;
    }
}
