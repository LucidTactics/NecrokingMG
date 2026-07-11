using Necroking.AI;
using Necroking.Core;
using Necroking.Data;
using Necroking.GameSystems;
using Necroking.Movement;
using Necroking.Render;

namespace Necroking.Scenario.Scenarios;

/// <summary>
/// Reproduces "zombie deer chases a deer and plays attack animations while moving".
///
/// A HordeMinion zombie deer engages a target; the instant its swing animation is
/// up, the target is yanked out of melee range — forcing an Engaged→Chasing
/// transition mid-swing. That transition used to clear PostAttackTimer (releasing
/// the movement lock) while the one-shot attack OVERRIDE anim was still playing, so
/// the unit slid forward (chasing) with the swing on screen.
///
/// Driven deterministically: a dummy target is lured into melee, then teleported
/// out whenever the zombie deer's OverrideAnim is an attack state. The horde leash
/// is widened so the chase never cuts off.
///
/// Invariant: the unit's OverrideAnim must never be an attack state (Attack1/2/3)
/// while it is moving at a chase speed.
/// </summary>
public class ChaseAttackAnimScenario : ScenarioBase
{
    public override string Name => "chase_attack_anim";

    private uint _necroId, _zDeerId, _dummyId;
    private float _elapsed;
    private bool _complete;
    private bool _fail;
    private string _failReason = "";

    // Clearly "moving", not a swing-end twitch. Must sit below the zombie deer's
    // actual chase speed: combatSpeed 0.96 (80% of the living deer per the zombie
    // speed rule) chases at ~1.7 u/s.
    private const float ChaseVelThreshold = 1.4f;
    private int _attackWhileMovingFrames;
    private float _worstVelDuringAttack;
    private int _attackFrames;     // frames the attack override was up at all
    private int _chaseFrames;      // frames moving fast at all

    public override void OnInit(Simulation sim)
    {
        DebugLog.Clear(ScenarioLog);
        DebugLog.Log(ScenarioLog, "=== Chase Attack-Anim Test (deterministic) ===");

        // NOTE: do NOT mutate sim.Horde.Settings here — those are the shared,
        // persisted per-machine settings; changing them leaks to user settings.json.
        // The chase is kept from hitting the leash by pinning the necromancer (the
        // horde-center anchor) right behind the zombie deer each tick instead.
        var units = sim.UnitsMut;

        int nIdx = sim.SpawnUnitByID("necromancer", new Vec2(10f, 10f));
        units[nIdx].Archetype = 0;
        units[nIdx].AI = AIBehavior.IdleAtPoint;
        _necroId = units[nIdx].Id;
        sim.SetNecromancerIndex(nIdx);

        int zIdx = sim.SpawnZombieMinion("ZombieFemaleDeer", new Vec2(12f, 10f));
        if (zIdx < 0) { _fail = true; _failReason = "spawn zombie deer failed"; _complete = true; return; }
        units[zIdx].Stats.MaxHP = 100000; units[zIdx].Stats.HP = 100000;
        units[zIdx].FacingAngle = 0f;
        _zDeerId = units[zIdx].Id;

        // Dummy target: enemy the zombie deer self-aggros. We fully drive its position.
        int dIdx = sim.SpawnUnitByID("soldier", new Vec2(13.3f, 10f));
        if (dIdx < 0) { _fail = true; _failReason = "spawn dummy failed"; _complete = true; return; }
        units[dIdx].Faction = Faction.Human;
        units[dIdx].Archetype = 0;  // legacy idle dummy — def archetype is auto-wired now
        units[dIdx].AI = AIBehavior.IdleAtPoint;
        units[dIdx].Stats.MaxHP = 100000; units[dIdx].Stats.HP = 100000;
        _dummyId = units[dIdx].Id;

        ZoomOnLocation(14f, 10f, 28f);
    }

    public override void OnTick(Simulation sim, float dt)
    {
        if (_complete) return;
        _elapsed += dt;

        int nIdx = FindById(sim.Units, _necroId);
        int zIdx = FindById(sim.Units, _zDeerId);
        int dIdx = FindById(sim.Units, _dummyId);
        if (nIdx < 0 || zIdx < 0 || dIdx < 0) { _complete = true; return; }

        // Necro follows just behind the zombie deer so the horde center tracks it and
        // the chase never crosses the leash (no need to touch the leash setting).
        Vec2 zp = sim.Units[zIdx].Position;
        sim.UnitsMut[nIdx].Position = zp - new Vec2(2f, 0f);
        sim.UnitsMut[nIdx].Velocity = Vec2.Zero;
        sim.UnitsMut[nIdx].PreferredVel = Vec2.Zero;

        // Keep both combatants alive.
        sim.UnitsMut[zIdx].Stats.HP = 100000;
        sim.UnitsMut[dIdx].Stats.HP = 100000;

        var z = sim.Units[zIdx];
        var ov = z.OverrideAnim;
        bool attackAnim = ov.IsActive &&
            (ov.State == AnimState.Attack1 || ov.State == AnimState.Attack2 || ov.State == AnimState.Attack3);
        float vel = z.Velocity.Length();

        // Drive the dummy on a timer: a ~0.45s "in melee" window (engage + swing),
        // then a ~0.65s "fled" window (the deer must chase). The fled window is long
        // enough that the swing finishes and a real chase happens — that chase is when
        // the old bug showed the attack anim sliding along.
        Vec2 zPos = z.Position;
        sim.UnitsMut[dIdx].Velocity = Vec2.Zero;
        sim.UnitsMut[dIdx].PreferredVel = Vec2.Zero;
        bool fledWindow = (_elapsed % 1.1f) >= 0.45f;
        sim.UnitsMut[dIdx].Position = fledWindow
            ? zPos + new Vec2(6.0f, 0f)   // out of melee → chase
            : zPos + new Vec2(1.3f, 0f);  // in melee → engage/attack

        // Invariant tracking.
        if (attackAnim) _attackFrames++;
        if (vel > ChaseVelThreshold) _chaseFrames++;
        if (attackAnim && vel > ChaseVelThreshold)
        {
            _attackWhileMovingFrames++;
            if (vel > _worstVelDuringAttack) _worstVelDuringAttack = vel;
        }

        if (_elapsed % 1f < dt)
        {
            DebugLog.Log(ScenarioLog,
                $"t={_elapsed:F1}s routine={z.Routine} vel={vel:F1} ovState={ov.State} " +
                $"postAtk={z.PostAttackTimer:F2} pend={(!z.PendingAttack.IsNone)} " +
                $"atkFrames={_attackFrames} chaseFrames={_chaseFrames} badFrames={_attackWhileMovingFrames}");
        }

        if (_elapsed > 12f)
        {
            if (_attackFrames < 5) { _fail = true; _failReason = $"zombie deer barely attacked (atkFrames={_attackFrames}) — invariant untested"; }
            else if (_chaseFrames < 5) { _fail = true; _failReason = $"zombie deer barely chased (chaseFrames={_chaseFrames}) — invariant untested"; }
            else if (_attackWhileMovingFrames > 2)
            {
                _fail = true;
                _failReason = $"attack anim played while moving on {_attackWhileMovingFrames} frames (worst vel={_worstVelDuringAttack:F1})";
            }
            DebugLog.Log(ScenarioLog,
                $"END atkFrames={_attackFrames} chaseFrames={_chaseFrames} " +
                $"attackWhileMovingFrames={_attackWhileMovingFrames} worstVel={_worstVelDuringAttack:F1}");
            _complete = true;
        }
    }

    public override bool IsComplete => _complete;

    public override int OnComplete(Simulation sim)
    {
        if (_fail) { DebugLog.Log(ScenarioLog, "FAIL: " + _failReason); return 1; }
        DebugLog.Log(ScenarioLog, "PASS: no attack animation while moving during the chase");
        return 0;
    }

    private static int FindById(UnitArrays units, uint id)
    {
        for (int i = 0; i < units.Count; i++)
            if (units[i].Id == id) return i;
        return -1;
    }
}
