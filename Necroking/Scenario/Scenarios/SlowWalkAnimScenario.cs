using Necroking.AI;
using Necroking.Core;
using Necroking.Data;
using Necroking.GameSystems;
using Necroking.Movement;
using Necroking.Render;

namespace Necroking.Scenario.Scenarios;

/// <summary>
/// Regression test for the "slow movers slide in Idle" bug: a unit moving at
/// grazing speed (~0.18 wu/s — below the old 0.25 idle gate) must resolve its
/// locomotion animation to Walk, not Idle. Deer graze/feed at ~0.1–0.18 wu/s;
/// before the fix the PreferredVel gate (0.25) and IdleWalkEnter (0.25) both
/// forced Idle, so they slid around without animating.
///
/// Setup: a lone unit given the DeerHerd archetype (so it runs the real deer AI:
/// IdleRoaming → casual graze-wander via SubroutineSteps.MoveToward) at deer
/// CombatSpeed (1.2 → roam speed ≈ 0.18 wu/s).
///
/// We assert on movement INTENT (PreferredVel), not just actual velocity: when
/// the unit clearly intends to move slowly (PreferredVel in the graze band) AND
/// is translating, its RoutineAnim.State must be Walk. The legitimate
/// "stopped, coasting on residual momentum" case (PreferredVel ≈ 0) is excluded,
/// since that one *should* read Idle.
/// </summary>
public class SlowWalkAnimScenario : ScenarioBase
{
    public override string Name => "slow_walk_anim";

    private float _elapsed;
    private bool _complete;
    private const float TestDuration = 14f;
    // CombatSpeed chosen so the resolved IdleRoam speed lands squarely in the slow
    // band: IdleRoaming sets effort Walk×0.5, so roam speed = 0.36 × 0.5 = 0.18 wu/s.
    // (Under the old double-penalty bug this collapsed to 0.18 × 0.3 = 0.054 — below
    // the walk threshold — so the deer slid in Idle.)
    private const float DeerCombatSpeed = 0.36f;
    private const float ExpectedRoamSpeed = 0.18f; // = DeerCombatSpeed × 0.5 effort cap

    private uint _unitId;
    private float _logTimer;

    // Observations (sampled every tick)
    private int _grazeSamples;        // ticks where intent+motion are clearly in the slow band
    private int _walkSamples;         // of those, how many correctly showed Walk
    private bool _sawIdleWhileGrazing; // intent+motion clearly slow-moving but state==Idle → bug
    private float _maxPrefObserved;
    private float _maxVelObserved;
    private float _resolvedMaxSpeed;   // the unit's MaxSpeed field while grazing (effort-resolved cap)

    public override void OnInit(Simulation sim)
    {
        DebugLog.Clear(ScenarioLog);
        DebugLog.Log(ScenarioLog, "=== SlowWalkAnim Scenario ===");
        DebugLog.Log(ScenarioLog, $"A deer-archetype unit grazing at ~{ExpectedRoamSpeed:F2} wu/s must animate Walk (not Idle)");
        DebugLog.Log(ScenarioLog, "and must actually move at its resolved MaxSpeed (no walkSpeedFraction double-penalty).");

        var units = sim.UnitsMut;
        var start = new Vec2(20f, 20f);
        int idx = units.AddUnit(start, UnitType.Skeleton);
        units[idx].Archetype = ArchetypeRegistry.DeerHerd; // run the real deer AI
        units[idx].Faction = Faction.Animal;
        units[idx].Stats.CombatSpeed = DeerCombatSpeed;
        _unitId = units[idx].Id;

        DebugLog.Log(ScenarioLog, $"Spawned deer-archetype unit id={_unitId} at ({start.X:F1},{start.Y:F1}), CombatSpeed={DeerCombatSpeed:F2}");
        ZoomOnLocation(20f, 20f, 32f);
    }

    public override void OnTick(Simulation sim, float dt)
    {
        _elapsed += dt;
        var units = sim.Units;
        int idx = FindByID(units, _unitId);
        if (idx < 0 || !units[idx].Alive) { _complete = true; return; }

        float vel = units[idx].Velocity.Length();
        float pref = units[idx].PreferredVel.Length();
        AnimState state = units[idx].RoutineAnim.State;
        if (pref > _maxPrefObserved) _maxPrefObserved = pref;
        if (vel > _maxVelObserved) _maxVelObserved = vel;

        // "Clearly grazing": the unit intends to move at graze speed AND is actually
        // translating. This excludes the stop-with-residual-momentum case (pref≈0).
        bool clearlyGrazing = pref >= 0.10f && pref <= 0.24f && vel >= 0.08f;
        if (clearlyGrazing)
        {
            _grazeSamples++;
            if (state == AnimState.Walk) _walkSamples++;
            if (state == AnimState.Idle) _sawIdleWhileGrazing = true;
            _resolvedMaxSpeed = units[idx].MaxSpeed; // effort-resolved cap while grazing
        }

        _logTimer -= dt;
        if (_logTimer <= 0f)
        {
            _logTimer = 0.5f;
            string routine = units[idx].Routine.ToString();
            DebugLog.Log(ScenarioLog,
                $"t={_elapsed:F1}s: vel={vel:F3} pref={pref:F3} state={state} routine={routine}" +
                (clearlyGrazing ? "  <-- grazing (want Walk)" : ""));
        }

        if (_elapsed >= TestDuration) _complete = true;
    }

    public override bool IsComplete => _complete;

    public override int OnComplete(Simulation sim)
    {
        DebugLog.Log(ScenarioLog, "=== SlowWalkAnim Validation ===");
        DebugLog.Log(ScenarioLog, $"Max PreferredVel observed: {_maxPrefObserved:F3} wu/s (expected ~{ExpectedRoamSpeed:F2})");
        DebugLog.Log(ScenarioLog, $"Max Velocity observed: {_maxVelObserved:F3} wu/s");
        DebugLog.Log(ScenarioLog, $"Resolved MaxSpeed (effort cap) while grazing: {_resolvedMaxSpeed:F3} wu/s");
        DebugLog.Log(ScenarioLog, $"Grazing samples (clear intent + motion, slow band): {_grazeSamples}");
        DebugLog.Log(ScenarioLog, $"  of those, showed Walk: {_walkSamples}");
        DebugLog.Log(ScenarioLog, $"  saw Idle while grazing (the bug): {_sawIdleWhileGrazing}");

        // Sanity: the unit must actually have grazed in the slow band, else the test proves nothing.
        bool actuallyGrazed = _grazeSamples >= 10;
        DebugLog.Log(ScenarioLog, $"Sanity — got enough grazing samples (>=10): {actuallyGrazed} -> {(actuallyGrazed ? "PASS" : "FAIL")}");

        // The fix: while clearly grazing, the unit animates Walk (allow a few transition
        // ticks of slack) and is never pinned to Idle mid-graze.
        bool walksWhileGrazing = _grazeSamples > 0 && _walkSamples >= _grazeSamples * 0.9f;
        DebugLog.Log(ScenarioLog, $"Walks while grazing (>=90% of samples): {walksWhileGrazing} -> {(walksWhileGrazing ? "PASS" : "FAIL")}");
        DebugLog.Log(ScenarioLog, $"Never idle mid-graze: {!_sawIdleWhileGrazing} -> {(!_sawIdleWhileGrazing ? "PASS" : "FAIL")}");

        // The deer-handler fix: the unit must actually MOVE at its resolved MaxSpeed —
        // not a fraction of it. With the old walkSpeedFraction double-penalty, velocity
        // peaked at ~0.3× the resolved cap. Require it to reach >=85% of MaxSpeed.
        bool movesAtResolvedSpeed = _resolvedMaxSpeed > 0f && _maxVelObserved >= _resolvedMaxSpeed * 0.85f;
        DebugLog.Log(ScenarioLog,
            $"Moves at resolved MaxSpeed (vel {_maxVelObserved:F3} >= 0.85 × {_resolvedMaxSpeed:F3}): " +
            $"{movesAtResolvedSpeed} -> {(movesAtResolvedSpeed ? "PASS" : "FAIL")}");

        bool pass = actuallyGrazed && walksWhileGrazing && !_sawIdleWhileGrazing && movesAtResolvedSpeed;
        DebugLog.Log(ScenarioLog, $"Overall: {(pass ? "PASS" : "FAIL")}");
        return pass ? 0 : 1;
    }

    private static int FindByID(UnitArrays units, uint id)
    {
        for (int i = 0; i < units.Count; i++)
            if (units[i].Id == id) return i;
        return -1;
    }
}
