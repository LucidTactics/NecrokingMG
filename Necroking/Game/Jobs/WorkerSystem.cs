using System;
using System.Collections.Generic;
using Necroking.Core;
using Necroking.Data;
using Necroking.GameSystems;
using Necroking.World;

namespace Necroking.Game.Jobs;

/// <summary>
/// The job-system "brain". Owns the runtime job list, the grave→worker
/// assignment, and the coarse-tick dispatcher that auto-assigns the shared
/// worker pool to active jobs top-down by priority (capped per job). Also
/// exposes the building/source queries that <see cref="Necroking.AI.WorkerHandler"/>
/// uses to execute a job — policy lives here, the per-unit FSM lives in the
/// handler.
///
/// P0/P1 scope: data spine + the Collect archetype (Forage Mushrooms) end to
/// end. Process jobs are loaded into the board but not yet executed.
/// </summary>
public class WorkerSystem
{
    private Simulation _sim = null!;
    private EnvironmentSystem _env = null!;
    private GameData _gameData = null!;
    private readonly JobRegistry _jobs = new();

    private readonly List<JobState> _jobStates = new();
    public IReadOnlyList<JobState> Jobs => _jobStates;

    // Coarse dispatch cadence (seconds). Worker assignment doesn't need per-frame.
    private const float DispatchInterval = 0.5f;
    private float _dispatchTimer;

    public void Bind(Simulation sim, EnvironmentSystem env, GameData gameData)
    {
        _sim = sim; _env = env; _gameData = gameData;
    }

    /// <summary>(Re)load jobs.json and rebuild the runtime job list. Call on
    /// startup and on new-game / map-load.</summary>
    public void Reset()
    {
        _jobs.Load();
        _jobStates.Clear();
        int pri = 0;
        foreach (var def in _jobs.Defs)
        {
            var st = new JobState(def) { Priority = pri++, WorkerCap = 99 };
            // Seed maintain-stock targets for multi-output process jobs.
            int opri = 0;
            foreach (var o in def.Outputs)
                st.OutputTargets[o.Id] = new OutputTarget { Priority = opri++, TargetStock = 5 };
            _jobStates.Add(st);
        }
        _dispatchTimer = 0f;
    }

    public JobState? GetJobState(string id)
    {
        foreach (var js in _jobStates)
            if (string.Equals(js.Def.Id, id, StringComparison.OrdinalIgnoreCase)) return js;
        return null;
    }

    // ─────────────────────────────────────────────────────────────
    //  Grave assignment
    // ─────────────────────────────────────────────────────────────

    /// <summary>Is a humanoid undead eligible to be assigned as a worker?</summary>
    public bool IsEligibleWorker(int unitIdx)
    {
        if (unitIdx < 0 || unitIdx >= _sim.Units.Count) return false;
        var u = _sim.Units[unitIdx];
        if (!u.Alive) return false;
        if (u.Faction != Faction.Undead) return false;
        if (u.Archetype == Necroking.AI.ArchetypeRegistry.Worker) return false; // already a worker
        if (u.Archetype == Necroking.AI.ArchetypeRegistry.PlayerControlled) return false; // the necromancer
        return true;
    }

    public bool IsGraveOccupied(int graveObjIdx)
    {
        for (int i = 0; i < _sim.Units.Count; i++)
        {
            var u = _sim.Units[i];
            if (u.Alive && u.Archetype == Necroking.AI.ArchetypeRegistry.Worker
                && u.WorkerHomeObjIdx == graveObjIdx) return true;
        }
        return false;
    }

    /// <summary>Convert the unit into a worker housed in the given grave. Stores
    /// the previous archetype so Unassign can restore it. Returns false if the
    /// unit isn't eligible or the grave is taken.</summary>
    public bool AssignWorker(uint unitId, int graveObjIdx)
    {
        if (!_sim.Units.TryGetIndex(unitId, out int idx)) return false;
        if (!IsEligibleWorker(idx)) return false;
        if (IsGraveOccupied(graveObjIdx)) return false;

        var u = _sim.Units[idx];
        u.WorkerPrevArchetype = u.Archetype;
        u.Archetype = Necroking.AI.ArchetypeRegistry.Worker;
        u.WorkerHomeObjIdx = graveObjIdx;
        u.WorkerJobId = "";
        u.WorkerPhase = 0;
        u.WorkerTargetObjIdx = -1;
        u.WorkerCarryType = "";
        u.WorkerCarryAmount = 0;
        u.Routine = 0;
        u.Subroutine = 0;
        return true;
    }

    /// <summary>Restore a worker to its prior archetype and clear worker state.
    /// Any carried resource is dropped (lost) for now.</summary>
    public bool UnassignWorker(uint unitId)
    {
        if (!_sim.Units.TryGetIndex(unitId, out int idx)) return false;
        var u = _sim.Units[idx];
        if (u.Archetype != Necroking.AI.ArchetypeRegistry.Worker) return false;
        u.Archetype = u.WorkerPrevArchetype;
        u.WorkerHomeObjIdx = -1;
        u.WorkerJobId = "";
        u.WorkerPhase = 0;
        u.WorkerTargetObjIdx = -1;
        u.WorkerCarryType = "";
        u.WorkerCarryAmount = 0;
        u.Routine = 0;
        u.Subroutine = 0;
        u.PreferredVel = Vec2.Zero;
        return true;
    }

    public bool UnassignGrave(int graveObjIdx)
    {
        for (int i = 0; i < _sim.Units.Count; i++)
        {
            var u = _sim.Units[i];
            if (u.Alive && u.Archetype == Necroking.AI.ArchetypeRegistry.Worker
                && u.WorkerHomeObjIdx == graveObjIdx)
                return UnassignWorker(u.Id);
        }
        return false;
    }

    // ─────────────────────────────────────────────────────────────
    //  Building queries
    // ─────────────────────────────────────────────────────────────

    /// <summary>Count alive placed instances of an env def id.</summary>
    public int CountBuildings(string defId)
    {
        int defIdx = _env.FindDef(defId);
        if (defIdx < 0) return 0;
        int n = 0;
        for (int i = 0; i < _env.ObjectCount; i++)
            if (_env.GetObject(i).DefIndex == defIdx && _env.GetObjectRuntime(i).Alive) n++;
        return n;
    }

    /// <summary>Building-derived worker cap for a job (instances × slots/building).</summary>
    public int DerivedMax(JobDef def) => CountBuildings(def.BuildingDefId) * def.WorkerSlotsPerBuilding;

    /// <summary>Nearest alive building that stockpiles <paramref name="resource"/>
    /// and still has space, or -1. Matched by the building def's StoredResource so
    /// a carrying worker can deposit even if its job changed mid-haul.</summary>
    public int FindDepositBuilding(string resource, Vec2 from)
    {
        if (string.IsNullOrEmpty(resource)) return -1;
        int best = -1; float bestSq = float.MaxValue;
        for (int i = 0; i < _env.ObjectCount; i++)
        {
            var rt = _env.GetObjectRuntime(i);
            if (!rt.Alive) continue;
            var bdef = _env.GetDef(_env.GetObject(i).DefIndex);
            if (!string.Equals(bdef.StoredResource, resource, StringComparison.OrdinalIgnoreCase)) continue;
            if (bdef.StorageCap > 0 && rt.StoredAmount >= bdef.StorageCap) continue;
            var obj = _env.GetObject(i);
            float sq = (new Vec2(obj.X, obj.Y) - from).LengthSq();
            if (sq < bestSq) { bestSq = sq; best = i; }
        }
        return best;
    }

    /// <summary>Nearest collectable source for a Collect job, or -1. Matches the
    /// job's SourceForagableType ("" = any foragable).</summary>
    public int FindNearestSource(JobDef def, Vec2 from)
    {
        int best = -1; float bestSq = float.MaxValue;
        for (int i = 0; i < _env.ObjectCount; i++)
        {
            if (!_env.IsObjectVisible(i)) continue;
            var d = _env.GetDef(_env.GetObject(i).DefIndex);
            if (!d.IsForagable) continue;
            if (!string.IsNullOrEmpty(def.SourceForagableType)
                && !string.Equals(d.ForagableType, def.SourceForagableType, StringComparison.OrdinalIgnoreCase))
                continue;
            var obj = _env.GetObject(i);
            float sq = (new Vec2(obj.X, obj.Y) - from).LengthSq();
            if (sq < bestSq) { bestSq = sq; best = i; }
        }
        return best;
    }

    /// <summary>Add to a building's stockpile (clamped to its cap). Returns the
    /// amount actually accepted.</summary>
    public int Deposit(int buildingObjIdx, int amount)
    {
        if (buildingObjIdx < 0 || buildingObjIdx >= _env.ObjectCount) return 0;
        var rt = _env.GetObjectRuntime(buildingObjIdx);
        var def = _env.GetDef(_env.GetObject(buildingObjIdx).DefIndex);
        int cap = def.StorageCap > 0 ? def.StorageCap : int.MaxValue;
        int room = Math.Max(0, cap - rt.StoredAmount);
        int accepted = Math.Min(room, amount);
        rt.StoredAmount += accepted;
        _env.SetObjectRuntime(buildingObjIdx, rt);
        return accepted;
    }

    /// <summary>Is the job active (has a building and somewhere to put output)?</summary>
    public bool IsJobActive(JobState js)
    {
        var def = js.Def;
        if (DerivedMax(def) <= 0) return false;
        // P1: only Collect jobs execute. Process jobs are visible but idle.
        if (def.Archetype != JobArchetype.Collect) return false;
        // Need at least one storage building with space and at least one source.
        if (FindDepositBuilding(def.StoreResource, NecroPos()) < 0) return false;
        return true;
    }

    private Vec2 NecroPos() =>
        _sim.NecromancerIndex >= 0 ? _sim.Units[_sim.NecromancerIndex].Position : Vec2.Zero;

    // ─────────────────────────────────────────────────────────────
    //  Dispatcher
    // ─────────────────────────────────────────────────────────────

    public void Update(float dt)
    {
        _dispatchTimer -= dt;
        if (_dispatchTimer > 0f) return;
        _dispatchTimer = DispatchInterval;
        Dispatch();
    }

    private readonly List<uint> _pool = new();

    private void Dispatch()
    {
        // 1. Reset assignments.
        foreach (var js in _jobStates) js.AssignedWorkers.Clear();

        // 2. Gather the idle/available worker pool.
        _pool.Clear();
        for (int i = 0; i < _sim.Units.Count; i++)
        {
            var u = _sim.Units[i];
            if (u.Alive && u.Archetype == Necroking.AI.ArchetypeRegistry.Worker)
                _pool.Add(u.Id);
        }
        if (_pool.Count == 0) return;

        // 3. Active jobs, highest priority (lowest number) first.
        var active = new List<JobState>();
        foreach (var js in _jobStates)
            if (IsJobActive(js)) active.Add(js);
        active.Sort((a, b) => a.Priority.CompareTo(b.Priority));

        // 4. Greedy fill top-down.
        int next = 0;
        foreach (var js in active)
        {
            int demand = Math.Min(js.WorkerCap, DerivedMax(js.Def));
            for (int k = 0; k < demand && next < _pool.Count; k++)
            {
                uint id = _pool[next++];
                js.AssignedWorkers.Add(id);
                SetWorkerJob(id, js.Def.Id);
            }
        }
        // 5. Leftovers go idle.
        for (; next < _pool.Count; next++)
            SetWorkerJob(_pool[next], "");
    }

    private void SetWorkerJob(uint unitId, string jobId)
    {
        if (!_sim.Units.TryGetIndex(unitId, out int idx)) return;
        var u = _sim.Units[idx];
        if (u.WorkerJobId != jobId)
        {
            u.WorkerJobId = jobId;
            // Reset the FSM so a re-tasked worker drops back to "decide".
            u.WorkerPhase = 0;
            u.WorkerTargetObjIdx = -1;
        }
    }
}
