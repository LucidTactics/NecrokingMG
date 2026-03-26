using System;
using Necroking.Core;
using Necroking.Data;
using Necroking.GameSystems;
using Necroking.Movement;

namespace Necroking.Scenario.Scenarios;

public class SpellTestScenario : ScenarioBase
{
    public override string Name => "spell_test";
    private float _elapsed;
    private bool _complete;
    private int _projectilesFired;
    private int _enemiesHit;
    private int _initialEnemyCount;
    private int _castIndex;
    private float _castTimer;
    private const float CastInterval = 0.8f;
    private const float MaxDuration = 15f;
    private readonly Vec2[] _enemyPositions = new Vec2[5];

    public override void OnInit(Simulation sim)
    {
        DebugLog.Clear(ScenarioLog);
        DebugLog.Log(ScenarioLog, "=== Spell Test Scenario ===");
        DebugLog.Log(ScenarioLog, "Testing fireball projectile spawning and enemy damage");

        var units = sim.UnitsMut;

        // Spawn a necromancer at center
        int necroIdx = units.AddUnit(new Vec2(10f, 10f), UnitType.Necromancer);
        units.AI[necroIdx] = AIBehavior.PlayerControlled;
        sim.SetNecromancerIndex(necroIdx);
        DebugLog.Log(ScenarioLog, $"Spawned necromancer at (10, 10), idx={necroIdx}, id={units.Id[necroIdx]}");

        // Spawn 5 enemies spread out in a semicircle
        for (int i = 0; i < 5; i++)
        {
            float angle = -60f + i * 30f; // -60 to +60 degrees
            float rad = angle * MathF.PI / 180f;
            float dist = 6f;
            var pos = new Vec2(10f + MathF.Cos(rad) * dist, 10f + MathF.Sin(rad) * dist);
            _enemyPositions[i] = pos;
            int enemyIdx = units.AddUnit(pos, UnitType.Soldier);
            units.AI[enemyIdx] = AIBehavior.IdleAtPoint; // Don't fight back, stay still
            DebugLog.Log(ScenarioLog, $"Spawned enemy {i} at ({pos.X:F1}, {pos.Y:F1}), idx={enemyIdx}");
        }

        _initialEnemyCount = 5;
        _castTimer = 0.5f; // small delay before first cast
        DebugLog.Log(ScenarioLog, $"Total units: {units.Count} (1 necro + 5 enemies)");
        ZoomOnLocation(10f, 10f, 32f);
    }

    public override void OnTick(Simulation sim, float dt)
    {
        _elapsed += dt;

        // Cast fireballs at enemies one by one
        if (_castIndex < 5)
        {
            _castTimer -= dt;
            if (_castTimer <= 0f)
            {
                // Find necromancer
                int necroIdx = sim.NecromancerIndex;
                if (necroIdx >= 0 && necroIdx < sim.Units.Count && sim.Units.Alive[necroIdx])
                {
                    var necroPos = sim.Units.Position[necroIdx];
                    var targetPos = _enemyPositions[_castIndex];
                    uint necroUid = sim.Units.Id[necroIdx];

                    sim.Projectiles.SpawnFireball(necroPos, targetPos, Faction.Undead, necroUid,
                        25, 1.5f, "Fireball");
                    _projectilesFired++;
                    DebugLog.Log(ScenarioLog, $"Cast fireball #{_projectilesFired} at enemy {_castIndex} pos=({targetPos.X:F1}, {targetPos.Y:F1})");
                }
                else
                {
                    DebugLog.Log(ScenarioLog, $"WARNING: necromancer not found (idx={necroIdx})");
                }

                _castIndex++;
                _castTimer = CastInterval;
            }
        }

        // Count remaining alive enemies
        int aliveEnemies = 0;
        int totalDamaged = 0;
        for (int i = 0; i < sim.Units.Count; i++)
        {
            if (sim.Units.Faction[i] == Faction.Human)
            {
                if (sim.Units.Alive[i]) aliveEnemies++;
                // Check if they took damage (HP < max)
                if (sim.Units.Stats[i].HP < sim.Units.Stats[i].MaxHP || !sim.Units.Alive[i])
                    totalDamaged++;
            }
        }

        // Check projectile count
        int activeProjectiles = 0;
        for (int i = 0; i < sim.Projectiles.Projectiles.Count; i++)
            if (sim.Projectiles.Projectiles[i].Alive) activeProjectiles++;

        // Complete when all fireballs have been cast and resolved (or timeout)
        if ((_castIndex >= 5 && activeProjectiles == 0 && _elapsed > 8f) || _elapsed > MaxDuration)
        {
            _enemiesHit = totalDamaged;
            DebugLog.Log(ScenarioLog, $"Scenario ending at t={_elapsed:F1}s");
            DebugLog.Log(ScenarioLog, $"  Projectiles fired: {_projectilesFired}");
            DebugLog.Log(ScenarioLog, $"  Enemies hit/damaged: {_enemiesHit}/{_initialEnemyCount}");
            DebugLog.Log(ScenarioLog, $"  Enemies still alive: {aliveEnemies}");
            DebugLog.Log(ScenarioLog, $"  Active projectiles remaining: {activeProjectiles}");
            _complete = true;
        }
    }

    public override bool IsComplete => _complete;

    public override int OnComplete(Simulation sim)
    {
        DebugLog.Log(ScenarioLog, "=== Spell Test Validation ===");

        bool projectilesSpawned = _projectilesFired == 5;
        bool enemiesWereDamaged = _enemiesHit > 0;

        DebugLog.Log(ScenarioLog, $"Projectiles spawned: {_projectilesFired}/5 → {(projectilesSpawned ? "PASS" : "FAIL")}");
        DebugLog.Log(ScenarioLog, $"Enemies took damage: {_enemiesHit}/{_initialEnemyCount} → {(enemiesWereDamaged ? "PASS" : "FAIL")}");

        bool pass = projectilesSpawned && enemiesWereDamaged;
        DebugLog.Log(ScenarioLog, $"Overall: {(pass ? "PASS" : "FAIL")}");
        return pass ? 0 : 1;
    }
}
