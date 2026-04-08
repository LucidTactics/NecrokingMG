using System;
using Microsoft.Xna.Framework;
using Necroking.Core;
using Necroking.Data;
using Necroking.Data.Registries;
using Necroking.GameSystems;

namespace Necroking.Scenario.Scenarios;

/// <summary>
/// Test: deer should flee when taking poison cloud damage.
/// Spawns a FemaleDeer (DeerHerd archetype) and a poison cloud on top of it.
/// </summary>
public class PoisonFleeScenario : ScenarioBase
{
    public override string Name => "poison_flee";
    public override bool IsComplete => _complete;

    private bool _complete;
    private float _elapsed;
    private bool _spawned;
    private bool _fled;
    private Vec2 _deerStartPos;
    private uint _deerID;

    public override void OnInit(Simulation sim)
    {
        DebugLog.Log(ScenarioLog, "=== Poison Flee Test ===");
        DebugLog.Log(ScenarioLog, "Spawns FemaleDeer + poison cloud on top, verifies deer flees");

        if (sim.GameData != null)
            sim.GameData.Settings.Weather.Enabled = false;
        BloomOverride = new BloomSettings { Enabled = false };
        BackgroundColor = new Color(40, 30, 50);

        ZoomOnLocation(15, 15, 30);
    }

    public override void OnTick(Simulation sim, float dt)
    {
        _elapsed += dt;

        if (!_spawned && _elapsed >= 0.1f)
        {
            _spawned = true;

            // Spawn a real deer (Dynamic unit with DeerHerd archetype)
            _deerStartPos = new Vec2(15, 15);
            int deerIdx = sim.SpawnUnitByID("FemaleDeer", _deerStartPos);
            if (deerIdx >= 0)
            {
                sim.UnitsMut[deerIdx].Stats.HP = 50;
                sim.UnitsMut[deerIdx].Stats.MaxHP = 50;
                _deerID = sim.Units[deerIdx].Id;
                DebugLog.Log(ScenarioLog, $"[{_elapsed:F2}s] Spawned FemaleDeer at {_deerStartPos}, " +
                    $"id={_deerID}, HP={sim.Units[deerIdx].Stats.HP}, " +
                    $"AI={sim.Units[deerIdx].AI}, faction={sim.Units[deerIdx].Faction}");
            }
            else
            {
                DebugLog.Log(ScenarioLog, "ERROR: Failed to spawn deer");
                _complete = true;
                return;
            }

            // Spawn poison cloud directly on the deer
            var spell = sim.GameData?.Spells.Get("poison_burst");
            if (spell != null)
            {
                sim.PoisonClouds.SpawnCloud(_deerStartPos, spell, Faction.Undead);
                DebugLog.Log(ScenarioLog, $"[{_elapsed:F2}s] Spawned poison cloud at {_deerStartPos}");
                DebugLog.Log(ScenarioLog, $"  cloudTickDamage={spell.CloudTickDamage}, spellDamage={spell.Damage}");

                // Also apply initial poison stacks (mirrors spell cast AoE path)
                if (spell.Damage > 0)
                {
                    int di = -1;
                    for (int i = 0; i < sim.Units.Count; i++)
                        if (sim.Units[i].Id == _deerID) { di = i; break; }
                    if (di >= 0)
                    {
                        sim.UnitsMut[di].PoisonStacks += spell.Damage;
                        sim.UnitsMut[di].HitReacting = true;
                        if (sim.Units[di].PoisonTickTimer <= 0f)
                            sim.UnitsMut[di].PoisonTickTimer = 3f;
                        DebugLog.Log(ScenarioLog, $"  Applied {spell.Damage} poison stacks + HitReacting to deer");
                    }
                }
            }
            else
            {
                DebugLog.Log(ScenarioLog, "ERROR: poison_burst spell not found");
                _complete = true;
                return;
            }
        }

        if (!_spawned) return;

        // Find the deer
        int deerIdx2 = -1;
        for (int i = 0; i < sim.Units.Count; i++)
            if (sim.Units[i].Id == _deerID && sim.Units[i].Alive) { deerIdx2 = i; break; }

        if (deerIdx2 >= 0)
        {
            var deer = sim.Units[deerIdx2];
            float dist = (deer.Position - _deerStartPos).Length();

            // Log every 0.25s
            if ((int)(_elapsed * 4) > (int)((_elapsed - dt) * 4))
            {
                DebugLog.Log(ScenarioLog, $"[{_elapsed:F2}s] Deer: pos=({deer.Position.X:F1},{deer.Position.Y:F1}), " +
                    $"dist={dist:F1}, HP={deer.Stats.HP}, poison={deer.PoisonStacks}, " +
                    $"hitReact={deer.HitReacting}, routine={deer.Routine}, AI={deer.AI}");
            }

            if (dist > 1f)
            {
                DebugLog.Log(ScenarioLog, $"[{_elapsed:F2}s] SUCCESS: Deer fled {dist:F1} units from start");
                _fled = true;
                _complete = true;
                return;
            }
        }
        else
        {
            DebugLog.Log(ScenarioLog, $"[{_elapsed:F2}s] Deer died or removed");
            _complete = true;
            return;
        }

        if (_elapsed > 10f)
        {
            DebugLog.Log(ScenarioLog, $"[{_elapsed:F2}s] FAIL: Deer did not flee within 10 seconds");
            _complete = true;
        }
    }

    public override int OnComplete(Simulation sim)
    {
        DebugLog.Log(ScenarioLog, $"=== Poison Flee Test Complete (fled={_fled}) ===");
        return _fled ? 0 : 1;
    }
}
