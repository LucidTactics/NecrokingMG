using System;
using System.Collections.Generic;
using Necroking.Core;
using Necroking.Data;
using Necroking.Movement;
using Necroking.World;

namespace Necroking.GameSystems;

public class TriggerSystem
{
    private List<TriggerRegion> _regions = new();
    private List<PatrolRoute> _patrolRoutes = new();
    private List<TriggerDef> _triggers = new();
    private List<TriggerInstance> _instances = new();
    private List<TriggerRuntimeState> _runtimeState = new();
    private EnvironmentSystem? _envSystem;
    private readonly List<PendingSpawn> _pendingSpawns = new();

    // Lookup maps
    private readonly Dictionary<string, int> _regionIndexByID = new();
    private readonly Dictionary<string, int> _triggerDefIndexByID = new();
    private readonly Dictionary<string, int> _instanceIndexByID = new();

    private struct PendingSpawn
    {
        public string UnitDefID;
        public Faction Faction;
        public Vec2 Position;
        public int Remaining;
        public float Timer;
        public float Interval;
    }

    // --- Setup ---

    public void SetRegions(List<TriggerRegion> regions) { _regions = regions; RebuildLookups(); }
    public void SetTriggers(List<TriggerDef> triggers) { _triggers = triggers; RebuildLookups(); }
    public void SetInstances(List<TriggerInstance> instances)
    {
        _instances = instances;
        _runtimeState.Clear();
        foreach (var inst in _instances)
            _runtimeState.Add(new TriggerRuntimeState { Active = inst.ActiveByDefault });
        RebuildLookups();
    }
    public void SetPatrolRoutes(List<PatrolRoute> routes) { _patrolRoutes = routes; }
    public void SetEnvironmentSystem(EnvironmentSystem? env) { _envSystem = env; }

    public IReadOnlyList<TriggerRegion> Regions => _regions;
    public IReadOnlyList<TriggerDef> Triggers => _triggers;
    public IReadOnlyList<TriggerInstance> Instances => _instances;
    public IReadOnlyList<PatrolRoute> PatrolRoutes => _patrolRoutes;

    // Mutable access for editors
    public List<TriggerRegion> RegionsMut => _regions;
    public List<TriggerDef> TriggersMut => _triggers;
    public List<TriggerInstance> InstancesMut => _instances;
    public List<PatrolRoute> PatrolRoutesMut => _patrolRoutes;

    public int AddRegion(TriggerRegion region) { _regions.Add(region); RebuildLookups(); return _regions.Count - 1; }
    public void RemoveRegion(int index) { if (index >= 0 && index < _regions.Count) { _regions.RemoveAt(index); RebuildLookups(); } }
    public int AddTrigger(TriggerDef trigger) { _triggers.Add(trigger); RebuildLookups(); return _triggers.Count - 1; }
    public void RemoveTrigger(int index) { if (index >= 0 && index < _triggers.Count) { _triggers.RemoveAt(index); RebuildLookups(); } }
    public int AddInstance(TriggerInstance instance)
    {
        _instances.Add(instance);
        _runtimeState.Add(new TriggerRuntimeState { Active = instance.ActiveByDefault });
        RebuildLookups();
        return _instances.Count - 1;
    }
    public void RemoveInstance(int index)
    {
        if (index >= 0 && index < _instances.Count)
        {
            _instances.RemoveAt(index);
            if (index < _runtimeState.Count) _runtimeState.RemoveAt(index);
            RebuildLookups();
        }
    }

    // --- Tick ---

    public void Tick(float dt, Simulation sim)
    {
        // Update pending spawns
        for (int i = _pendingSpawns.Count - 1; i >= 0; i--)
        {
            var ps = _pendingSpawns[i];
            ps.Timer -= dt;
            if (ps.Timer <= 0f && ps.Remaining > 0)
            {
                sim.SpawnUnitByID(ps.UnitDefID, ps.Position);
                ps.Remaining--;
                ps.Timer = ps.Interval;
            }
            if (ps.Remaining <= 0)
                _pendingSpawns.RemoveAt(i);
            else
                _pendingSpawns[i] = ps;
        }

        // Evaluate each trigger instance
        var evalCtx = new TriggerEvalContext
        {
            GameTime = sim.GameTime,
            RuntimeStates = _runtimeState.ToArray()
        };

        for (int i = 0; i < _instances.Count; i++)
        {
            if (i >= _runtimeState.Count) break;
            var rs = _runtimeState[i];
            if (!rs.Active) continue;

            // Find parent trigger def
            var inst = _instances[i];
            if (!_triggerDefIndexByID.TryGetValue(inst.ParentTriggerID, out int defIdx)) continue;
            if (defIdx < 0 || defIdx >= _triggers.Count) continue;
            var def = _triggers[defIdx];

            // Check fire limit
            if (def.OneShot && rs.FireCount > 0) continue;
            if (def.MaxFireCount > 0 && rs.FireCount >= def.MaxFireCount) continue;

            // Tick cooldown
            if (rs.CooldownTimer > 0f)
            {
                rs.CooldownTimer -= dt;
                _runtimeState[i] = rs;
                continue;
            }

            // Evaluate condition
            bool condResult = def.Condition?.Evaluate(evalCtx, i) ?? true;
            rs.LastConditionResult = condResult;

            if (condResult)
            {
                // Fire effects
                foreach (var effect in def.Effects)
                    ExecuteEffect(effect, i, sim);

                rs.FireCount++;
            }

            _runtimeState[i] = rs;
        }
    }

    // --- Event callbacks ---

    public void OnUnitKilled(int unitIdx, UnitArrays units)
    {
        for (int i = 0; i < _runtimeState.Count; i++)
        {
            if (!_runtimeState[i].Active) continue;
            var rs = _runtimeState[i];
            rs.KillCounter++;
            _runtimeState[i] = rs;
        }
    }

    public void ResetRuntime()
    {
        _runtimeState.Clear();
        foreach (var inst in _instances)
            _runtimeState.Add(new TriggerRuntimeState { Active = inst.ActiveByDefault });
        _pendingSpawns.Clear();
    }

    // --- Effect execution ---

    private void ExecuteEffect(TriggerEffect effect, int instanceIdx, Simulation sim)
    {
        switch (effect)
        {
            case EffActivateTrigger activate:
                if (_instanceIndexByID.TryGetValue(activate.TriggerID, out int activateIdx))
                    if (activateIdx < _runtimeState.Count)
                    { var rs = _runtimeState[activateIdx]; rs.Active = true; _runtimeState[activateIdx] = rs; }
                break;

            case EffDeactivateTrigger deactivate:
                if (_instanceIndexByID.TryGetValue(deactivate.TriggerID, out int deactivateIdx))
                    if (deactivateIdx < _runtimeState.Count)
                    { var rs = _runtimeState[deactivateIdx]; rs.Active = false; _runtimeState[deactivateIdx] = rs; }
                break;

            case EffSpawnUnits spawn:
                Vec2 spawnPos = spawn.Position;
                // Resolve spawn location from region if specified
                if (!string.IsNullOrEmpty(spawn.RegionID) && _regionIndexByID.TryGetValue(spawn.RegionID, out int regionIdx))
                {
                    if (regionIdx < _regions.Count)
                    {
                        var region = _regions[regionIdx];
                        spawnPos = new Vec2(region.X, region.Y);
                    }
                }

                if (spawn.SpawnInterval > 0f && spawn.Count > 1)
                {
                    _pendingSpawns.Add(new PendingSpawn
                    {
                        UnitDefID = spawn.UnitDefID,
                        Faction = spawn.Faction,
                        Position = spawnPos,
                        Remaining = spawn.Count,
                        Timer = 0f,
                        Interval = spawn.SpawnInterval
                    });
                }
                else
                {
                    for (int j = 0; j < spawn.Count; j++)
                    {
                        float angle = j * MathF.PI * 2f / spawn.Count;
                        var offset = new Vec2(MathF.Cos(angle), MathF.Sin(angle)) * spawn.SpawnDistance;
                        sim.SpawnUnitByID(spawn.UnitDefID, spawnPos + offset);
                    }
                }
                break;

            case EffKillUnits kill:
                // Kill units in specified region
                if (!string.IsNullOrEmpty(kill.RegionID) && _regionIndexByID.TryGetValue(kill.RegionID, out int killRegionIdx))
                {
                    if (killRegionIdx < _regions.Count)
                    {
                        var region = _regions[killRegionIdx];
                        int killed = 0;
                        for (int j = 0; j < sim.Units.Count; j++)
                        {
                            if (!sim.Units[j].Alive) continue;
                            if (kill.MaxKills > 0 && killed >= kill.MaxKills) break;
                            if (region.ContainsPoint(sim.Units[j].Position))
                            {
                                sim.UnitsMut[j].Alive = false;
                                killed++;
                            }
                        }
                    }
                }
                break;
        }
    }

    // --- Lookup helpers ---

    private void RebuildLookups()
    {
        _regionIndexByID.Clear();
        for (int i = 0; i < _regions.Count; i++)
            _regionIndexByID[_regions[i].Id] = i;

        _triggerDefIndexByID.Clear();
        for (int i = 0; i < _triggers.Count; i++)
            _triggerDefIndexByID[_triggers[i].Id] = i;

        _instanceIndexByID.Clear();
        for (int i = 0; i < _instances.Count; i++)
            _instanceIndexByID[_instances[i].InstanceID] = i;
    }
}
