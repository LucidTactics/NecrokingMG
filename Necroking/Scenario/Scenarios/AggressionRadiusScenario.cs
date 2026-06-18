using System;
using Necroking.AI;
using Necroking.Core;
using Necroking.Data;
using Necroking.GameSystems;
using Necroking.Movement;

namespace Necroking.Scenario.Scenarios;

/// <summary>
/// Verifies the aggression-level scaling of the horde engagement + leash radii
/// (Shift+Q/Shift+E control, HordeSystem.AggressionLevel 0..4):
///   center (2) = configured offsets (current behavior),
///   max (4)    = engagement and leash both DOUBLED,
///   min (0)    = engagement == formation edge (EffectiveRadius), leash == formation + LeashOffset,
///   levels 1/3 = linear interpolation.
/// Pure-getter test: sets AggressionLevel and reads AggroRadius/LeashRadius.
/// </summary>
public class AggressionRadiusScenario : ScenarioBase
{
    public override string Name => "aggression_radius";

    private bool _complete, _fail;
    private string _failReason = "";

    public override void OnInit(Simulation sim)
    {
        DebugLog.Clear(ScenarioLog);
        DebugLog.Log(ScenarioLog, "=== Aggression Radius Scaling ===");

        var units = sim.UnitsMut;
        int nIdx = sim.SpawnUnitByID("necromancer", new Vec2(50f, 50f));
        units[nIdx].Archetype = 0;
        units[nIdx].AI = AIBehavior.IdleAtPoint;
        sim.SetNecromancerIndex(nIdx);

        // Enroll a dozen minions so EffectiveRadius (the "formation distance") is
        // non-zero and the eff-dependent terms get exercised.
        for (int i = 0; i < 12; i++)
        {
            int m = units.AddUnit(new Vec2(50f + i, 50f), UnitType.Skeleton);
            units[m].Archetype = ArchetypeRegistry.HordeMinion;
            units[m].Faction = Faction.Undead;
            sim.Horde.AddUnit(units[m].Id);
        }
    }

    public override void OnTick(Simulation sim, float dt)
    {
        if (_complete) return;

        float eff = sim.Horde.EffectiveRadius;
        float engOff = sim.Horde.Settings.EngagementOffset;
        float leashOff = sim.Horde.Settings.LeashOffset;
        float mid = eff + engOff;            // center aggro
        float midLeash = eff + engOff + leashOff; // center leash
        DebugLog.Log(ScenarioLog,
            $"eff={eff:F2} engOff={engOff:F1} leashOff={leashOff:F1} mid={mid:F2} midLeash={midLeash:F2}");

        static float Lerp(float a, float b, float t) => a + (b - a) * t;

        for (int level = 0; level <= HordeSystem.AggressionMax; level++)
        {
            sim.Horde.AggressionLevel = level;
            float t = level / (float)HordeSystem.AggressionMax;
            float expAggro = t <= 0.5f ? Lerp(eff, mid, t / 0.5f)
                                       : Lerp(mid, 2f * mid, (t - 0.5f) / 0.5f);
            float expLeash = t <= 0.5f ? Lerp(eff + leashOff, midLeash, t / 0.5f)
                                       : Lerp(midLeash, 2f * midLeash, (t - 0.5f) / 0.5f);
            float gotAggro = sim.Horde.AggroRadius;
            float gotLeash = sim.Horde.LeashRadius;
            bool ok = MathF.Abs(gotAggro - expAggro) < 0.05f && MathF.Abs(gotLeash - expLeash) < 0.05f;
            DebugLog.Log(ScenarioLog,
                $"L{level}: aggro got={gotAggro:F2} exp={expAggro:F2} | leash got={gotLeash:F2} exp={expLeash:F2} -> {(ok ? "OK" : "MISMATCH")}");
            if (!ok) { _fail = true; _failReason = $"level {level}: aggro {gotAggro:F2}!={expAggro:F2} or leash {gotLeash:F2}!={expLeash:F2}"; }
        }

        // Spec endpoints spelled out explicitly.
        sim.Horde.AggressionLevel = HordeSystem.AggressionMax;
        bool maxOk = MathF.Abs(sim.Horde.AggroRadius - 2f * mid) < 0.05f
                  && MathF.Abs(sim.Horde.LeashRadius - 2f * midLeash) < 0.05f;
        sim.Horde.AggressionLevel = 0;
        bool minOk = MathF.Abs(sim.Horde.AggroRadius - eff) < 0.05f
                  && MathF.Abs(sim.Horde.LeashRadius - (eff + leashOff)) < 0.05f;
        sim.Horde.AggressionLevel = HordeSystem.AggressionCenter;
        bool midOk = MathF.Abs(sim.Horde.AggroRadius - mid) < 0.05f
                  && MathF.Abs(sim.Horde.LeashRadius - midLeash) < 0.05f;
        DebugLog.Log(ScenarioLog, $"endpoints: max(doubled)={maxOk} min(formation)={minOk} center(current)={midOk}");
        if (!maxOk || !minOk || !midOk) { _fail = true; _failReason += " | endpoint spec check failed"; }

        _complete = true;
    }

    public override bool IsComplete => _complete;

    public override int OnComplete(Simulation sim)
    {
        if (_fail) { DebugLog.Log(ScenarioLog, "FAIL: " + _failReason); return 1; }
        DebugLog.Log(ScenarioLog, "PASS: aggression scaling matches spec at all 5 levels");
        return 0;
    }
}
