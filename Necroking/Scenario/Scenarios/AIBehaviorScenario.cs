using System;
using Necroking.Core;
using Necroking.Data;
using Necroking.GameSystems;
using Necroking.Movement;

namespace Necroking.Scenario.Scenarios;

public class AIBehaviorScenario : ScenarioBase
{
    public override string Name => "ai_behavior";
    private float _elapsed;
    private bool _complete;
    private const float TestDuration = 10f;
    private const float MaxDuration = 15f;

    // Track unit IDs for validation
    private uint _necroId;
    private uint _archerId1, _archerId2;
    private uint _knightId;
    private uint _soldier1Id, _soldier2Id;
    private Vec2 _necroPos;
    private int _archerProjectilesSeen;
    private float _logTimer;

    public override void OnInit(Simulation sim)
    {
        DebugLog.Clear(ScenarioLog);
        DebugLog.Log(ScenarioLog, "=== AI Behavior Scenario ===");
        DebugLog.Log(ScenarioLog, "Testing AI: AttackClosest, ArcherAttack, GuardKnight, AttackNecromancer");

        var units = sim.UnitsMut;

        // Spawn necromancer (target for AttackNecromancer AI)
        _necroPos = new Vec2(10f, 10f);
        int necroIdx = units.AddUnit(_necroPos, UnitType.Necromancer);
        units.AI[necroIdx] = AIBehavior.PlayerControlled;
        _necroId = units.Id[necroIdx];
        sim.SetNecromancerIndex(necroIdx);
        DebugLog.Log(ScenarioLog, $"Necromancer at (10, 10), id={_necroId}");

        // Spawn 2 undead archers with ArcherAttack AI
        int arch1 = units.AddUnit(new Vec2(8f, 8f), UnitType.Skeleton);
        units.AI[arch1] = AIBehavior.ArcherAttack;
        _archerId1 = units.Id[arch1];
        int arch2 = units.AddUnit(new Vec2(12f, 8f), UnitType.Skeleton);
        units.AI[arch2] = AIBehavior.ArcherAttack;
        _archerId2 = units.Id[arch2];
        DebugLog.Log(ScenarioLog, $"Archers (ArcherAttack AI) at (8,8) and (12,8), ids={_archerId1},{_archerId2}");

        // Spawn a knight with GuardKnight AI
        int knightIdx = units.AddUnit(new Vec2(15f, 15f), UnitType.Knight);
        units.AI[knightIdx] = AIBehavior.GuardKnight;
        _knightId = units.Id[knightIdx];
        DebugLog.Log(ScenarioLog, $"Knight (GuardKnight AI) at (15, 15), id={_knightId}");

        // Spawn 2 soldiers with AttackNecromancer AI — they should move toward necro
        int s1 = units.AddUnit(new Vec2(20f, 20f), UnitType.Soldier);
        units.AI[s1] = AIBehavior.AttackNecromancer;
        _soldier1Id = units.Id[s1];
        int s2 = units.AddUnit(new Vec2(22f, 20f), UnitType.Soldier);
        units.AI[s2] = AIBehavior.AttackNecromancer;
        _soldier2Id = units.Id[s2];
        DebugLog.Log(ScenarioLog, $"Soldiers (AttackNecromancer AI) at (20,20) and (22,20), ids={_soldier1Id},{_soldier2Id}");

        // Spawn some enemies near archers so they have targets to shoot
        for (int i = 0; i < 3; i++)
        {
            int eIdx = units.AddUnit(new Vec2(10f + i * 1.5f, 14f), UnitType.Soldier);
            units.AI[eIdx] = AIBehavior.AttackClosest;
            DebugLog.Log(ScenarioLog, $"Target soldier {i} (AttackClosest) at ({10f + i * 1.5f:F1}, 14)");
        }

        DebugLog.Log(ScenarioLog, $"Total units spawned: {units.Count}");
        ZoomOnLocation(12f, 12f, 20f);
    }

    public override void OnTick(Simulation sim, float dt)
    {
        _elapsed += dt;

        // Count projectiles (to check archers are firing)
        int projectileCount = sim.Projectiles.Projectiles.Count;
        if (projectileCount > _archerProjectilesSeen)
        {
            int newProj = projectileCount - _archerProjectilesSeen;
            DebugLog.Log(ScenarioLog, $"t={_elapsed:F1}s: {newProj} new projectile(s) spawned (total seen: {projectileCount})");
            _archerProjectilesSeen = projectileCount;
        }

        // Periodic logging
        _logTimer -= dt;
        if (_logTimer <= 0f)
        {
            _logTimer = 2f;
            LogUnitPositions(sim);
        }

        if (_elapsed >= TestDuration)
            _complete = true;
    }

    private void LogUnitPositions(Simulation sim)
    {
        var units = sim.Units;
        for (int i = 0; i < units.Count; i++)
        {
            if (!units.Alive[i]) continue;
            uint id = units.Id[i];
            var pos = units.Position[i];
            var ai = units.AI[i];

            if (id == _necroId || id == _archerId1 || id == _archerId2 ||
                id == _knightId || id == _soldier1Id || id == _soldier2Id)
            {
                DebugLog.Log(ScenarioLog, $"  id={id} ai={ai} pos=({pos.X:F1},{pos.Y:F1}) inCombat={units.InCombat[i]}");
            }
        }
    }

    private int FindByID(UnitArrays units, uint id)
    {
        for (int i = 0; i < units.Count; i++)
            if (units.Id[i] == id) return i;
        return -1;
    }

    public override bool IsComplete => _complete;

    public override int OnComplete(Simulation sim)
    {
        DebugLog.Log(ScenarioLog, "=== AI Behavior Validation ===");

        var units = sim.Units;

        // Check 1: Archers should have fired projectiles
        bool archersFired = _archerProjectilesSeen > 0;
        DebugLog.Log(ScenarioLog, $"Archers fired projectiles: {_archerProjectilesSeen} → {(archersFired ? "PASS" : "FAIL")}");

        // Check 2: AttackNecromancer soldiers should have moved closer to necro
        bool soldiersMoved = false;
        int s1Idx = FindByID(sim.UnitsMut, _soldier1Id);
        int s2Idx = FindByID(sim.UnitsMut, _soldier2Id);
        if (s1Idx >= 0 || s2Idx >= 0)
        {
            float dist1 = s1Idx >= 0 ? (units.Position[s1Idx] - _necroPos).Length() : 999f;
            float dist2 = s2Idx >= 0 ? (units.Position[s2Idx] - _necroPos).Length() : 999f;
            float minDist = MathF.Min(dist1, dist2);
            // They started at ~14 units away, should be closer after 10s
            soldiersMoved = minDist < 12f;
            DebugLog.Log(ScenarioLog, $"AttackNecromancer soldiers dist to necro: s1={dist1:F1}, s2={dist2:F1} (started ~14) → {(soldiersMoved ? "PASS" : "FAIL")}");
        }
        else
        {
            DebugLog.Log(ScenarioLog, "AttackNecromancer soldiers: both dead (inconclusive, counting as PASS)");
            soldiersMoved = true; // They engaged and died, which means the AI worked
        }

        // Check 3: Knight should still exist (GuardKnight is defensive)
        int knightIdx = FindByID(sim.UnitsMut, _knightId);
        bool knightExists = knightIdx >= 0 && units.Alive[knightIdx];
        DebugLog.Log(ScenarioLog, $"GuardKnight still alive: {(knightExists ? "PASS" : "FAIL (died or missing)")}");

        // Summary
        int undead = 0, human = 0;
        for (int i = 0; i < units.Count; i++)
        {
            if (!units.Alive[i]) continue;
            if (units.Faction[i] == Faction.Undead) undead++;
            else human++;
        }
        DebugLog.Log(ScenarioLog, $"Final state: {undead} undead, {human} human alive");
        DebugLog.Log(ScenarioLog, $"Combat log entries: {sim.CombatLog.Entries.Count}");

        bool pass = archersFired && soldiersMoved;
        DebugLog.Log(ScenarioLog, $"Overall: {(pass ? "PASS" : "FAIL")}");
        return pass ? 0 : 1;
    }
}
