using Necroking.Core;
using Necroking.Data;
using Necroking.Data.Registries;
using Necroking.Game;
using Necroking.Game.SkillEffects;
using Necroking.GameSystems;
using Necroking.Lib;
using Necroking.Movement;

namespace Necroking.Scenario.Scenarios;

/// <summary>
/// Verifies that buffs can grant magic paths to a caster, and that the
/// arcane_apprentice skill (effect=grant_path, arg=shock:1) raises the
/// necromancer's Shock path so new spells become castable.
///
/// Part A — mechanism: a raw "+2 Shock" path buff on a skeleton lifts its
///   EffectivePathLevel(Shock) by 2 over its native UnitDef level.
/// Part B — skill flow: a Necromancer learns arcane_apprentice (via the real
///   SkillBook learn path), and its EffectivePathLevel(Shock) goes native+1.
/// </summary>
public class PathBuffScenario : ScenarioBase
{
    public override string Name => "path_buff";

    private bool _complete;
    private int _fail; // 0 = pass

    public override void OnInit(Simulation sim)
    {
        DebugLog.Clear(ScenarioLog);
        DebugLog.Log(ScenarioLog, "=== Path Buff / grant_path Scenario ===");
        var gd = sim.GameData;
        if (gd == null) { Fail(1, "no GameData"); return; }

        // ---- Part A: raw path buff on a skeleton ----
        int skel = sim.UnitsMut.AddUnit(new Vec2(20f, 20f), UnitType.Skeleton);
        var skelDef = gd.Units.Get(sim.Units[skel].UnitDefID);
        int nativeA = BuffSystem.EffectivePathLevel(sim.UnitsMut, skel, skelDef, MagicPath.Shock);
        DebugLog.Log(ScenarioLog, $"A: skeleton native Shock = {nativeA}");

        var buff = new BuffDef
        {
            Id = "test_path_shock", Duration = 0f, Intrinsic = true, MaxStacks = 1,
            Effects = { new BuffEffect { Type = "Add", Stat = BuffSystem.PathStat(MagicPath.Shock), Value = 2 } },
        };
        BuffSystem.ApplyBuff(sim.UnitsMut, skel, buff, gd);
        int afterA = BuffSystem.EffectivePathLevel(sim.UnitsMut, skel, skelDef, MagicPath.Shock);
        DebugLog.Log(ScenarioLog, $"A: after +2 Shock buff = {afterA} (expected {nativeA + 2})");
        if (afterA != nativeA + 2) { Fail(2, $"path buff didn't apply: {afterA} != {nativeA + 2}"); return; }

        // Sanity: an unrelated path is untouched.
        int fireA = BuffSystem.EffectivePathLevel(sim.UnitsMut, skel, skelDef, MagicPath.Fire);
        DebugLog.Log(ScenarioLog, $"A: Fire path still = {fireA} (buff is Shock-only)");

        // ---- Part B: arcane_apprentice grants Shock to the necromancer ----
        int necro = sim.UnitsMut.AddUnit(new Vec2(40f, 20f), UnitType.Necromancer);
        sim.SetNecromancerIndex(necro);
        var necroDef = gd.Units.Get(sim.Units[necro].UnitDefID);
        int nativeB = BuffSystem.EffectivePathLevel(sim.UnitsMut, necro, necroDef, MagicPath.Shock);
        DebugLog.Log(ScenarioLog, $"B: necromancer native Shock = {nativeB}");

        var book = new SkillBookState();
        sim.SetSkillBook(book);
        var ctx = new SkillEffectContext
        {
            Inventory = Inventory!, GameData = gd, BookState = book, Sim = sim,
        };

        var def = book.FindSkill("arcane_apprentice");
        if (def == null) { Fail(3, "arcane_apprentice not in SkillBookDefs"); return; }
        DebugLog.Log(ScenarioLog, $"B: arcane_apprentice effect='{def.Effect}' arg='{def.EffectArg}' startLearned={def.StartLearned}");
        if (def.StartLearned) { Fail(4, "arcane_apprentice should NOT be startLearned"); return; }

        bool learned = book.LearnFree("arcane_apprentice", ctx);
        int afterB = BuffSystem.EffectivePathLevel(sim.UnitsMut, necro, necroDef, MagicPath.Shock);
        DebugLog.Log(ScenarioLog, $"B: learned={learned}, after = {afterB} (expected {nativeB + 1})");
        if (!learned) { Fail(5, "arcane_apprentice failed to learn"); return; }
        if (afterB != nativeB + 1) { Fail(6, $"arcane_apprentice didn't raise Shock: {afterB} != {nativeB + 1}"); return; }

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
