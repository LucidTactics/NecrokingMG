using System;
using System.Collections.Generic;
using System.Text;
using Necroking.Core;
using Necroking.Data;
using Necroking.GameSystems;
using Necroking.World;

namespace Necroking.Game.Jobs;

/// <summary>
/// The job-system "brain". Owns the runtime job list, the grave→worker
/// assignment, per-building stockpiles, and the coarse-tick dispatcher that
/// auto-assigns the shared worker pool to active jobs top-down by priority
/// (capped per job). Also exposes the queries that
/// <see cref="Necroking.AI.WorkerHandler"/> uses to execute a job — policy lives
/// here, the per-unit FSM lives in the handler.
///
/// Resource model (placeholder economy): every storage building holds a small
/// multi-type stockpile keyed by resource id, capped by the def's StorageCap
/// (total items). The "Essence" output routes to the global PlayerResources
/// pool instead. Corpses are carried physically during the Collect step then
/// abstracted to a "Corpse" count in the pile; Process jobs withdraw/deposit
/// abstract counts and (Reanimate) spawn a unit.
/// </summary>
public class WorkerSystem
{
    private Simulation _sim = null!;
    private EnvironmentSystem _env = null!;
    private GameData _gameData = null!;
    private readonly JobRegistry _jobs = new();

    private readonly List<JobState> _jobStates = new();
    public IReadOnlyList<JobState> Jobs => _jobStates;

    // objectId → (resource → count). Keyed by stable ObjectID string.
    private readonly Dictionary<string, Dictionary<string, int>> _stock = new();

    // Coarse dispatch cadence (seconds). Worker assignment doesn't need per-frame.
    private const float DispatchInterval = 0.5f;
    private float _dispatchTimer;

    /// <summary>Soft cap on total undead the Reanimate job will create. Placeholder.</summary>
    public int MaxUndead = 150;

    public byte WorkerArchetype => Necroking.AI.ArchetypeRegistry.Worker;

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
        _stock.Clear();
        int pri = 0;
        foreach (var def in _jobs.Defs)
        {
            var st = new JobState(def) { Priority = pri++, WorkerCap = 99 };
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
    //  UI support
    // ─────────────────────────────────────────────────────────────

    public struct WorkerCandidate { public uint Id; public string Name; }

    /// <summary>Eligible (alive, undead, non-worker, non-necromancer) humanoid
    /// undead not yet housed in a grave — the Grave Roster's assignable list.</summary>
    public List<WorkerCandidate> UnassignedWorkers()
    {
        var list = new List<WorkerCandidate>();
        for (int i = 0; i < _sim.Units.Count; i++)
        {
            if (!IsEligibleWorker(i)) continue;
            var u = _sim.Units[i];
            var def = _gameData.Units.Get(u.UnitDefID);
            if (def != null && def.UndeadCategory == Data.Registries.UndeadCategory.Monster) continue; // humanoids only
            string name = def != null && !string.IsNullOrEmpty(def.DisplayName) ? def.DisplayName
                        : (string.IsNullOrEmpty(u.UnitDefID) ? u.Type.ToString() : u.UnitDefID);
            list.Add(new WorkerCandidate { Id = u.Id, Name = $"{name} #{u.Id}" });
        }
        return list;
    }

    public WorkerCandidate? HousedWorker(int graveObjIdx)
    {
        for (int i = 0; i < _sim.Units.Count; i++)
        {
            var u = _sim.Units[i];
            if (u.Alive && u.Archetype == WorkerArchetype && u.WorkerHomeObjIdx == graveObjIdx)
            {
                var def = _gameData.Units.Get(u.UnitDefID);
                string name = def != null && !string.IsNullOrEmpty(def.DisplayName) ? def.DisplayName
                            : (string.IsNullOrEmpty(u.UnitDefID) ? u.Type.ToString() : u.UnitDefID);
                return new WorkerCandidate { Id = u.Id, Name = $"{name} #{u.Id}" };
            }
        }
        return null;
    }

    public bool IsWorkerHomeDef(int objIdx)
        => objIdx >= 0 && objIdx < _env.ObjectCount && _env.GetDef(_env.GetObject(objIdx).DefIndex).IsWorkerHome;

    /// <summary>Player-effective cap = min(requested cap, building-derived max).</summary>
    public int EffectiveCap(JobState js) => Math.Min(js.WorkerCap, DerivedMax(js.Def));

    /// <summary>Storage shown on a job tile: (current, max). Spawn-unit jobs show
    /// the undead population vs the soft cap; others sum the host buildings.</summary>
    public (int cur, int max) JobStorage(JobDef def)
    {
        if (def.SpawnsUnit) return (CountUndead(), MaxUndead);
        int defIdx = _env.FindDef(def.BuildingDefId);
        if (defIdx < 0) return (0, 0);
        int cur = 0, max = 0;
        for (int i = 0; i < _env.ObjectCount; i++)
        {
            if (_env.GetObject(i).DefIndex != defIdx || !Built(i)) continue;
            cur += TotalStored(i);
            var d = _env.GetDef(defIdx);
            max += d.StorageCap > 0 ? d.StorageCap : 0;
        }
        return (cur, max);
    }

    /// <summary>True when the job's storage is full (tile shows paused/red).</summary>
    public bool IsStorageFull(JobDef def)
    {
        var (cur, max) = JobStorage(def);
        return max > 0 && cur >= max;
    }

    /// <summary>Reorder the job list (drag/▲▼) and renumber priorities.</summary>
    public void MoveJob(int fromIndex, int toIndex)
    {
        if (fromIndex < 0 || fromIndex >= _jobStates.Count) return;
        toIndex = Math.Clamp(toIndex, 0, _jobStates.Count - 1);
        if (toIndex == fromIndex) return;
        var js = _jobStates[fromIndex];
        _jobStates.RemoveAt(fromIndex);
        _jobStates.Insert(toIndex, js);
        for (int i = 0; i < _jobStates.Count; i++) _jobStates[i].Priority = i;
    }

    public void SetCap(JobState js, int cap) => js.WorkerCap = Math.Max(0, cap);

    // ─────────────────────────────────────────────────────────────
    //  Grave assignment
    // ─────────────────────────────────────────────────────────────

    public bool IsEligibleWorker(int unitIdx)
    {
        if (unitIdx < 0 || unitIdx >= _sim.Units.Count) return false;
        var u = _sim.Units[unitIdx];
        if (!u.Alive) return false;
        if (u.Faction != Faction.Undead) return false;
        if (u.Archetype == WorkerArchetype) return false;
        if (u.Archetype == Necroking.AI.ArchetypeRegistry.PlayerControlled) return false;
        return true;
    }

    public bool IsGraveOccupied(int graveObjIdx)
    {
        for (int i = 0; i < _sim.Units.Count; i++)
        {
            var u = _sim.Units[i];
            if (u.Alive && u.Archetype == WorkerArchetype && u.WorkerHomeObjIdx == graveObjIdx)
                return true;
        }
        return false;
    }

    public bool AssignWorker(uint unitId, int graveObjIdx)
    {
        if (!_sim.Units.TryGetIndex(unitId, out int idx)) return false;
        if (!IsEligibleWorker(idx)) return false;
        if (IsGraveOccupied(graveObjIdx)) return false;

        var u = _sim.Units[idx];
        u.WorkerPrevArchetype = u.Archetype;
        u.Archetype = WorkerArchetype;
        u.WorkerHomeObjIdx = graveObjIdx;
        u.WorkerJobId = "";
        u.WorkerPhase = 0;
        u.WorkerTargetObjIdx = -1;
        u.WorkerCarryType = "";
        u.WorkerCarryAmount = 0;
        u.Routine = 0; u.Subroutine = 0;
        return true;
    }

    public bool UnassignWorker(uint unitId)
    {
        if (!_sim.Units.TryGetIndex(unitId, out int idx)) return false;
        var u = _sim.Units[idx];
        if (u.Archetype != WorkerArchetype) return false;
        u.Archetype = u.WorkerPrevArchetype;
        u.WorkerHomeObjIdx = -1;
        u.WorkerJobId = "";
        u.WorkerPhase = 0;
        u.WorkerTargetObjIdx = -1;
        u.WorkerCarryType = "";
        u.WorkerCarryAmount = 0;
        u.Routine = 0; u.Subroutine = 0;
        u.PreferredVel = Vec2.Zero;
        return true;
    }

    public bool UnassignGrave(int graveObjIdx)
    {
        for (int i = 0; i < _sim.Units.Count; i++)
        {
            var u = _sim.Units[i];
            if (u.Alive && u.Archetype == WorkerArchetype && u.WorkerHomeObjIdx == graveObjIdx)
                return UnassignWorker(u.Id);
        }
        return false;
    }

    // ─────────────────────────────────────────────────────────────
    //  Stockpiles
    // ─────────────────────────────────────────────────────────────

    private string ObjId(int objIdx) => _env.GetObject(objIdx).ObjectID;

    public int StoredOf(int objIdx, string resource)
    {
        if (objIdx < 0 || objIdx >= _env.ObjectCount) return 0;
        return _stock.TryGetValue(ObjId(objIdx), out var d) && d.TryGetValue(resource, out var n) ? n : 0;
    }

    public int TotalStored(int objIdx)
    {
        if (objIdx < 0 || objIdx >= _env.ObjectCount) return 0;
        int t = 0;
        if (_stock.TryGetValue(ObjId(objIdx), out var d))
            foreach (var v in d.Values) t += v;
        return t;
    }

    private void SyncStoredAmount(int objIdx)
    {
        var rt = _env.GetObjectRuntime(objIdx);
        rt.StoredAmount = TotalStored(objIdx);
        _env.SetObjectRuntime(objIdx, rt);
    }

    private int BuildingCap(int objIdx)
    {
        var def = _env.GetDef(_env.GetObject(objIdx).DefIndex);
        return def.StorageCap > 0 ? def.StorageCap : int.MaxValue;
    }

    /// <summary>Add to a building's stockpile (clamped to its total cap). Returns
    /// the amount actually accepted.</summary>
    public int Deposit(int objIdx, string resource, int amount)
    {
        if (objIdx < 0 || objIdx >= _env.ObjectCount || amount <= 0) return 0;
        int room = Math.Max(0, BuildingCap(objIdx) - TotalStored(objIdx));
        int acc = Math.Min(room, amount);
        if (acc > 0)
        {
            string key = ObjId(objIdx);
            if (!_stock.TryGetValue(key, out var d)) { d = new(); _stock[key] = d; }
            d[resource] = (d.TryGetValue(resource, out var n) ? n : 0) + acc;
            SyncStoredAmount(objIdx);
        }
        return acc;
    }

    /// <summary>Remove from a building's stockpile. Returns amount taken.</summary>
    public int Withdraw(int objIdx, string resource, int amount)
    {
        if (objIdx < 0 || objIdx >= _env.ObjectCount || amount <= 0) return 0;
        if (!_stock.TryGetValue(ObjId(objIdx), out var d) || !d.TryGetValue(resource, out var have)) return 0;
        int take = Math.Min(have, amount);
        if (take > 0) { d[resource] = have - take; SyncStoredAmount(objIdx); }
        return take;
    }

    // ─────────────────────────────────────────────────────────────
    //  Building queries
    // ─────────────────────────────────────────────────────────────

    /// <summary>A building only hosts a job / stockpile once fully constructed
    /// (placed blueprints from the build menu start at BuildProgress 0).</summary>
    private bool Built(int objIdx)
    {
        var rt = _env.GetObjectRuntime(objIdx);
        return rt.Alive && rt.BuildProgress >= 1f;
    }

    public int CountBuildings(string defId)
    {
        int defIdx = _env.FindDef(defId);
        if (defIdx < 0) return 0;
        int n = 0;
        for (int i = 0; i < _env.ObjectCount; i++)
            if (_env.GetObject(i).DefIndex == defIdx && Built(i)) n++;
        return n;
    }

    public int DerivedMax(JobDef def) => CountBuildings(def.BuildingDefId) * def.WorkerSlotsPerBuilding;

    /// <summary>Nearest alive building that stockpiles <paramref name="resource"/>
    /// and still has space, or -1.</summary>
    public int FindDepositBuilding(string resource, Vec2 from)
    {
        if (string.IsNullOrEmpty(resource)) return -1;
        int best = -1; float bestSq = float.MaxValue;
        for (int i = 0; i < _env.ObjectCount; i++)
        {
            if (!Built(i)) continue;
            var bdef = _env.GetDef(_env.GetObject(i).DefIndex);
            if (!string.Equals(bdef.StoredResource, resource, StringComparison.OrdinalIgnoreCase)) continue;
            if (TotalStored(i) >= BuildingCap(i)) continue;
            var obj = _env.GetObject(i);
            float sq = (new Vec2(obj.X, obj.Y) - from).LengthSq();
            if (sq < bestSq) { bestSq = sq; best = i; }
        }
        return best;
    }

    /// <summary>Nearest alive building holding at least <paramref name="minAmount"/>
    /// of <paramref name="resource"/>, or -1.</summary>
    public int FindWithdrawBuilding(string resource, Vec2 from, int minAmount)
    {
        if (string.IsNullOrEmpty(resource)) return -1;
        int best = -1; float bestSq = float.MaxValue;
        for (int i = 0; i < _env.ObjectCount; i++)
        {
            if (!Built(i)) continue;
            if (StoredOf(i, resource) < minAmount) continue;
            var obj = _env.GetObject(i);
            float sq = (new Vec2(obj.X, obj.Y) - from).LengthSq();
            if (sq < bestSq) { bestSq = sq; best = i; }
        }
        return best;
    }

    /// <summary>Nearest alive host building (BuildingDefId) for a job with output
    /// room, or -1.</summary>
    public int FindHostBuilding(JobDef def, Vec2 from)
    {
        int defIdx = _env.FindDef(def.BuildingDefId);
        if (defIdx < 0) return -1;
        int best = -1; float bestSq = float.MaxValue;
        for (int i = 0; i < _env.ObjectCount; i++)
        {
            if (_env.GetObject(i).DefIndex != defIdx) continue;
            if (!Built(i)) continue;
            // Output room: spawn-unit jobs need no storage; others need building space.
            if (!def.SpawnsUnit && HostOutputResource(def) != JobResources.Essence
                && TotalStored(i) >= BuildingCap(i)) continue;
            var obj = _env.GetObject(i);
            float sq = (new Vec2(obj.X, obj.Y) - from).LengthSq();
            if (sq < bestSq) { bestSq = sq; best = i; }
        }
        return best;
    }

    /// <summary>Nearest collectable source for a Collect job, or -1.</summary>
    public int FindNearestSource(JobDef def, Vec2 from)
    {
        switch (def.CollectKind)
        {
            case "corpse": return FindNearestCorpseObj(from);
            case "berry":  return FindNearestBerryBush(from);
            default:       return FindNearestForagable(def, from);
        }
    }

    private int FindNearestForagable(JobDef def, Vec2 from)
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

    private int FindNearestBerryBush(Vec2 from)
    {
        int best = -1; float bestSq = float.MaxValue;
        for (int i = 0; i < _env.ObjectCount; i++)
        {
            if (!_env.GetObjectRuntime(i).Alive) continue;
            var d = _env.GetDef(_env.GetObject(i).DefIndex);
            if (!d.IsBerryBush) continue;
            if (_env.GetObjectRuntime(i).BerryState != BerryState.Berries) continue;
            var obj = _env.GetObject(i);
            float sq = (new Vec2(obj.X, obj.Y) - from).LengthSq();
            if (sq < bestSq) { bestSq = sq; best = i; }
        }
        return best;
    }

    // For corpse jobs, FindNearestSource returns a CorpseID (not an env index);
    // the handler stores it in WorkerTargetObjIdx and resolves position via CorpsePos.
    private int FindNearestCorpseObj(Vec2 from)
    {
        int best = -1; float bestSq = float.MaxValue;
        var cs = _sim.Corpses;
        for (int i = 0; i < cs.Count; i++)
        {
            var c = cs[i];
            if (c.Dissolving || c.ConsumedBySummon) continue;
            if (c.ReanimInstanceId != 0) continue;
            if (c.DraggedByUnitID != GameConstants.InvalidUnit) continue;
            float sq = (c.Position - from).LengthSq();
            if (sq < bestSq) { bestSq = sq; best = c.CorpseID; }
        }
        return best;
    }

    public Vec2? CorpsePos(int corpseId)
    {
        var c = _sim.FindCorpseByID(corpseId);
        return c == null ? (Vec2?)null : c.Position;
    }

    /// <summary>Begin physically carrying a corpse (no pickup-anim gating — the
    /// corpse follows the unit each sim tick while CarryingCorpseID is set).</summary>
    public bool StartCarryCorpse(int unitIdx, int corpseId)
    {
        var c = _sim.FindCorpseByID(corpseId);
        if (c == null || c.DraggedByUnitID != GameConstants.InvalidUnit) return false;
        c.LerpStartPos = c.Position;
        c.DraggedByUnitID = _sim.Units[unitIdx].Id;
        _sim.Units[unitIdx].CarryingCorpseID = corpseId;
        return true;
    }

    /// <summary>Consume the carried corpse (remove from the world) — used when it's
    /// deposited into a pile as an abstract "Corpse" count.</summary>
    public void ConsumeCarriedCorpse(int unitIdx)
    {
        int cid = _sim.Units[unitIdx].CarryingCorpseID;
        if (cid >= 0)
        {
            int ci = _sim.FindCorpseIndexByID(cid);
            if (ci >= 0) _sim.CorpsesMut.RemoveAt(ci);
        }
        _sim.Units[unitIdx].CarryingCorpseID = -1;
        _sim.Units[unitIdx].CorpseInteractPhase = 0;
    }

    // ─────────────────────────────────────────────────────────────
    //  Process output emission
    // ─────────────────────────────────────────────────────────────

    private static string HostOutputResource(JobDef def)
        => def.Outputs.Count > 0 ? def.Outputs[0].Id : "";

    /// <summary>Pick the output (for multi-output jobs) that is furthest below its
    /// maintain-stock target, highest-priority first. Returns -1 if all targets met.</summary>
    public int PickOutputToProduce(JobState js, int hostObjIdx)
    {
        var def = js.Def;
        if (def.Outputs.Count == 0) return -1;
        if (def.Outputs.Count == 1) return 0;

        int bestIdx = -1; int bestPri = int.MaxValue; int bestDeficit = 0;
        for (int k = 0; k < def.Outputs.Count; k++)
        {
            var o = def.Outputs[k];
            var tgt = js.OutputTargets.TryGetValue(o.Id, out var t) ? t : new OutputTarget { Priority = k, TargetStock = 5 };
            int have = StoredOf(hostObjIdx, o.Id);
            int deficit = tgt.TargetStock - have;
            if (deficit <= 0) continue;
            // Highest priority (lowest number) wins; tie-break on larger deficit.
            if (tgt.Priority < bestPri || (tgt.Priority == bestPri && deficit > bestDeficit))
            { bestPri = tgt.Priority; bestDeficit = deficit; bestIdx = k; }
        }
        return bestIdx;
    }

    /// <summary>Emit a process job's output at its host building. Routes Essence to
    /// the global pool, spawns a unit for Reanimate, else deposits to the host
    /// stockpile (choosing the maintain-stock output for multi-output jobs).</summary>
    public void EmitProcessOutput(JobState js, int hostObjIdx)
    {
        var def = js.Def;
        if (def.SpawnsUnit)
        {
            var obj = _env.GetObject(hostObjIdx);
            float oy = _env.GetDef(obj.DefIndex).SpawnOffsetY;
            SpawnReanimated(def.SpawnUnitDefId, new Vec2(obj.X, obj.Y + (oy > 0 ? oy : 1.5f)));
            return;
        }

        if (def.OutputChoice)
        {
            // Alternatives (potions): emit exactly the maintain-stock pick.
            int pick = PickOutputToProduce(js, hostObjIdx);
            if (pick < 0) return;
            var o = def.Outputs[pick];
            if (o.Id == JobResources.Essence) _sim.PlayerResources.AddEssence(o.Amount);
            else Deposit(hostObjIdx, o.Id, o.Amount);
            return;
        }

        // Co-products (breakdown): emit every output. Essence → global pool.
        foreach (var o in def.Outputs)
        {
            if (o.Id == JobResources.Essence) _sim.PlayerResources.AddEssence(o.Amount);
            else Deposit(hostObjIdx, o.Id, o.Amount);
        }
    }

    private void SpawnReanimated(string unitDefId, Vec2 pos)
    {
        SpawnWorkerUnit?.Invoke(unitDefId, pos);
    }

    /// <summary>Game1 wires this to its SpawnUnit so the brain can create units
    /// without referencing Game1.</summary>
    public Action<string, Vec2>? SpawnWorkerUnit;

    /// <summary>Dev/diagnostic dump of every building stockpile + global totals.</summary>
    public string StockReport()
    {
        var sb = new StringBuilder();
        for (int i = 0; i < _env.ObjectCount; i++)
        {
            if (!_env.GetObjectRuntime(i).Alive) continue;
            var def = _env.GetDef(_env.GetObject(i).DefIndex);
            if (!def.IsBuilding) continue;
            bool hasStock = _stock.TryGetValue(_env.GetObject(i).ObjectID, out var d) && d.Count > 0;
            if (string.IsNullOrEmpty(def.StoredResource) && !hasStock) continue;
            var parts = new List<string>();
            if (hasStock) foreach (var kv in d) if (kv.Value > 0) parts.Add($"{kv.Key}={kv.Value}");
            string cap = def.StorageCap > 0 ? def.StorageCap.ToString() : "∞";
            sb.Append($"  {def.Id} obj{i}: {(parts.Count > 0 ? string.Join(",", parts) : "empty")} ({TotalStored(i)}/{cap})\n");
        }
        sb.Append($"  Essence={_sim.PlayerResources.Essence}  Undead={CountUndead()}\n");
        return sb.ToString();
    }

    public int CountUndead()
    {
        int n = 0;
        for (int i = 0; i < _sim.Units.Count; i++)
            if (_sim.Units[i].Alive && _sim.Units[i].Faction == Faction.Undead) n++;
        return n;
    }

    // ─────────────────────────────────────────────────────────────
    //  Job activeness
    // ─────────────────────────────────────────────────────────────

    public bool IsJobActive(JobState js)
    {
        var def = js.Def;
        if (DerivedMax(def) <= 0) return false;
        Vec2 anchor = NecroPos();

        if (def.Archetype == JobArchetype.Collect)
        {
            if (FindDepositBuilding(def.StoreResource, anchor) < 0) return false; // storage full / none
            return FindNearestSource(def, anchor) >= 0;                            // something to gather
        }

        // Process
        if (FindHostBuilding(def, anchor) < 0) return false;                       // no host with room
        foreach (var inp in def.Inputs)
            if (FindWithdrawBuilding(inp.Resource, anchor, inp.Amount) < 0) return false; // input unavailable
        if (def.SpawnsUnit && CountUndead() >= MaxUndead) return false;            // unit cap
        if (def.OutputChoice)
        {
            // Alternatives: active only while some output is below its target.
            int host = FindHostBuilding(def, anchor);
            if (host >= 0 && PickOutputToProduce(js, host) < 0) return false;
        }
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
        foreach (var js in _jobStates) js.AssignedWorkers.Clear();

        _pool.Clear();
        for (int i = 0; i < _sim.Units.Count; i++)
        {
            var u = _sim.Units[i];
            if (u.Alive && u.Archetype == WorkerArchetype) _pool.Add(u.Id);
        }
        if (_pool.Count == 0) return;

        var active = new List<JobState>();
        foreach (var js in _jobStates)
            if (IsJobActive(js)) active.Add(js);
        active.Sort((a, b) => a.Priority.CompareTo(b.Priority));

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
        for (; next < _pool.Count; next++)
            SetWorkerJob(_pool[next], "");
    }

    private void SetWorkerJob(uint unitId, string jobId)
    {
        if (!_sim.Units.TryGetIndex(unitId, out int idx)) return;
        var u = _sim.Units[idx];
        if (u.WorkerJobId != jobId && string.IsNullOrEmpty(u.WorkerCarryType))
        {
            // Only re-task a worker that isn't mid-haul (avoid dropping carried goods).
            u.WorkerJobId = jobId;
            u.WorkerPhase = 0;
            u.WorkerTargetObjIdx = -1;
        }
        else if (u.WorkerJobId != jobId && !string.IsNullOrEmpty(u.WorkerCarryType))
        {
            // Carrying: keep current job until it delivers, then re-dispatch picks it up.
        }
    }
}
