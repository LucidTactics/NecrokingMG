using System;
using Necroking.Core;
using Necroking.Data;
using Necroking.GameSystems;
using Necroking.World;

namespace Necroking.Scenario.Scenarios;

/// <summary>
/// Undead base produces skeletons from corpses, then raids peasant village.
/// Port of C++ undead_raid scenario.
/// </summary>
public class UndeadRaidScenario : ScenarioBase
{
    public override string Name => "undead_raid";

    private const int HutCount = 5;
    private const int PeasantsPerHut = 2;
    private const int InitialCorpses = 5;
    private const int SkeletonsToRaid = 5;
    private const float CX = 32f;
    private const float CY = 32f;
    private const float BaseX = CX - 18f;
    private const float BaseY = CY;

    private static readonly float[][] HutPositions = {
        new[] { CX - 5f,  CY - 13f },
        new[] { CX - 5f,  CY },
        new[] { CX - 5f,  CY + 13f },
        new[] { CX + 15f, CY - 4f },
        new[] { CX + 15f, CY + 4f },
    };

    private bool _complete;
    private float _elapsed;
    private int _phase;
    private readonly int[] _hutObjIdx = new int[HutCount];
    private readonly bool[] _hutDestroyed = new bool[HutCount];
    private bool _raidStarted;
    private int _lastUndeadCount = -1;
    private int _lastHumanCount = -1;

    public override void OnInit(Simulation sim)
    {
        DebugLog.Clear(ScenarioLog);
        DebugLog.Log(ScenarioLog, $"=== Scenario: {Name} ===");

        var env = sim.EnvironmentSystem!;
        var units = sim.UnitsMut;

        // Obelisk building def
        var obelisk = new EnvironmentObjectDef
        {
            Id = "undead_obelisk", Name = "Dark Obelisk", Category = "Scenario",
            IsBuilding = true, BuildingMaxHP = 200, BuildingDefaultOwner = 0,
            Input1 = new ProcessSlot { Kind = "corpse" },
            Output = new ProcessSlot { Kind = "unit", ResourceID = "skeleton" },
            ProcessTime = 1.5f, MaxInputQueue = 10, AutoSpawn = true,
            SpawnOffsetY = 1.5f, SpriteWorldHeight = 2.5f, CollisionRadius = 0.5f
        };
        int obeliskDefIdx = env.AddDef(obelisk);

        // Peasant hut def
        var hut = new EnvironmentObjectDef
        {
            Id = "peasant_hut", Name = "Peasant Hut", Category = "Scenario",
            IsBuilding = true, BuildingMaxHP = 30, BuildingDefaultOwner = 1,
            SpriteWorldHeight = 3f, CollisionRadius = 1f
        };
        int hutDefIdx = env.AddDef(hut);

        // Place obelisk
        env.AddObject((ushort)obeliskDefIdx, BaseX, BaseY);
        DebugLog.Log(ScenarioLog, $"Undead obelisk placed at ({BaseX:F0}, {BaseY:F0})");

        // Workers
        for (int w = 0; w < 2; w++)
        {
            float wx = BaseX + 3f + w * 2f;
            float wy = BaseY + 2f - w * 2f;
            int workerIdx = units.AddUnit(new Vec2(wx, wy), UnitType.Skeleton);
            if (workerIdx >= 0)
                units.AI[workerIdx] = AIBehavior.CorpseWorker;
        }

        // Corpses around base
        for (int c = 0; c < InitialCorpses; c++)
        {
            float angle = c * (360f / InitialCorpses);
            float rad = angle * MathF.PI / 180f;
            float dist = 8f + (c % 2) * 2f;
            sim.CorpsesMut.Add(new Corpse
            {
                Position = new Vec2(BaseX + dist * MathF.Cos(rad), BaseY + dist * MathF.Sin(rad)),
                UnitType = UnitType.Militia, CorpseID = 9000 + c, FacingAngle = 90f, SpriteScale = 1f
            });
        }
        DebugLog.Log(ScenarioLog, $"Placed {InitialCorpses} corpses around undead base");

        // Huts with peasants
        for (int h = 0; h < HutCount; h++)
        {
            float hx = HutPositions[h][0], hy = HutPositions[h][1];
            _hutObjIdx[h] = env.AddObject((ushort)hutDefIdx, hx, hy);

            for (int p = 0; p < PeasantsPerHut; p++)
            {
                float px = hx + (p == 0 ? -3f : 3f);
                float py = hy + (p == 0 ? 2f : -2f);
                int idx = units.AddUnit(new Vec2(px, py), UnitType.Militia);
                if (idx >= 0)
                {
                    units.AI[idx] = AIBehavior.IdleAtPoint;
                    units.MoveTarget[idx] = new Vec2(hx, hy);
                }
            }
        }
        DebugLog.Log(ScenarioLog, $"Placed {HutCount} huts with {HutCount * PeasantsPerHut} peasants");
        ZoomOnLocation(CX, CY, 13f);
    }

    public override void OnTick(Simulation sim, float dt)
    {
        if (_complete) return;
        if (DeferredScreenshot != null) return;
        _elapsed += dt;

        var units = sim.Units;
        var env = sim.EnvironmentSystem!;

        int undeadCount = 0, humanCount = 0, skeletonCombat = 0;
        for (int i = 0; i < units.Count; i++)
        {
            if (!units.Alive[i] || units.Type[i] == UnitType.Necromancer) continue;
            if (units.Faction[i] == Faction.Undead)
            {
                undeadCount++;
                if (units.AI[i] != AIBehavior.CorpseWorker) skeletonCombat++;
            }
            else humanCount++;
        }

        int hutsAlive = 0;
        for (int h = 0; h < HutCount; h++)
        {
            if (_hutObjIdx[h] >= 0 && env.GetObjectRuntime(_hutObjIdx[h]).Alive)
                hutsAlive++;
        }

        if (undeadCount != _lastUndeadCount || humanCount != _lastHumanCount)
        {
            DebugLog.Log(ScenarioLog, $"[{_elapsed:F1}s] Undead={undeadCount} (combat={skeletonCombat}) Human={humanCount} Huts={hutsAlive}/{HutCount}");
            _lastUndeadCount = undeadCount;
            _lastHumanCount = humanCount;
        }

        // Check for hut destruction → spawn reinforcements
        for (int h = 0; h < HutCount; h++)
        {
            if (_hutDestroyed[h] || _hutObjIdx[h] < 0) continue;
            if (!env.GetObjectRuntime(_hutObjIdx[h]).Alive)
            {
                _hutDestroyed[h] = true;
                float hx = HutPositions[h][0], hy = HutPositions[h][1];
                DebugLog.Log(ScenarioLog, $"[{_elapsed:F1}s] Hut {h} destroyed! Spawning reinforcement peasant");
                int idx = sim.UnitsMut.AddUnit(new Vec2(hx, hy), UnitType.Militia);
                if (idx >= 0) sim.UnitsMut.AI[idx] = AIBehavior.AttackClosest;
            }
        }

        // Start raid when enough idle skeletons gather near base
        int idleNearBase = 0;
        for (int i = 0; i < units.Count; i++)
        {
            if (!units.Alive[i] || units.Faction[i] != Faction.Undead) continue;
            if (units.AI[i] == AIBehavior.CorpseWorker || units.AI[i] == AIBehavior.Raid) continue;
            float dist2 = (units.Position[i] - new Vec2(BaseX, BaseY)).LengthSq();
            if (dist2 < 15f * 15f) idleNearBase++;
        }

        if (idleNearBase >= SkeletonsToRaid && !_raidStarted)
        {
            _raidStarted = true;
            DebugLog.Log(ScenarioLog, $"[{_elapsed:F1}s] === RAID WAVE === {idleNearBase} idle skeletons switching to Raid AI");
            var mu = sim.UnitsMut;
            for (int i = 0; i < mu.Count; i++)
            {
                if (!mu.Alive[i] || mu.Faction[i] != Faction.Undead) continue;
                if (mu.AI[i] == AIBehavior.CorpseWorker || mu.AI[i] == AIBehavior.Raid) continue;
                float dist2 = (mu.Position[i] - new Vec2(BaseX, BaseY)).LengthSq();
                if (dist2 < 15f * 15f) mu.AI[i] = AIBehavior.Raid;
            }
        }

        // Screenshots
        if (_phase == 0)
        {
            DeferredScreenshot = "undead_raid_setup";
            _phase = 1;
        }
        else if (_phase == 1 && _raidStarted)
        {
            DeferredScreenshot = "undead_raid_march";
            _phase = 2;
        }
        else if (_phase == 2 && hutsAlive < HutCount)
        {
            DeferredScreenshot = "undead_raid_battle";
            _phase = 3;
        }

        // End conditions
        if (_elapsed > 5f)
        {
            if (humanCount == 0 && hutsAlive == 0)
            {
                DeferredScreenshot = "undead_raid_victory";
                _complete = true;
            }
            else if (undeadCount == 0)
            {
                DeferredScreenshot = "undead_raid_defeat";
                _complete = true;
            }
        }

        // Force engagement at 60s
        if (_elapsed >= 60f && !_complete)
        {
            var mu = sim.UnitsMut;
            int forced = 0;
            for (int i = 0; i < mu.Count; i++)
            {
                if (!mu.Alive[i] || mu.Type[i] == UnitType.Necromancer) continue;
                var ai = mu.AI[i];
                if (ai == AIBehavior.IdleAtPoint || ai == AIBehavior.CorpseWorker || ai == AIBehavior.DefendPoint)
                {
                    mu.AI[i] = AIBehavior.AttackClosest;
                    forced++;
                }
            }
            if (forced > 0) DebugLog.Log(ScenarioLog, $"[{_elapsed:F1}s] Forced {forced} units to AttackClosest (timeout)");
        }

        // Hard timeout
        if (_elapsed >= 90f && !_complete)
        {
            DeferredScreenshot = "undead_raid_timeout";
            _complete = true;
        }
    }

    public override bool IsComplete => _complete && DeferredScreenshot == null;

    public override int OnComplete(Simulation sim)
    {
        DebugLog.Log(ScenarioLog, $"=== Scenario Complete: {Name} ===");
        DebugLog.Log(ScenarioLog, $"Elapsed: {_elapsed:F1}s, Raid started: {_raidStarted}");
        return 0; // Always pass — tests setup and battle flow
    }
}
