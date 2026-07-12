using System.Collections.Generic;
using Necroking.Core;
using Necroking.Data;
using Necroking.Lib;
using Necroking.Movement;

namespace Necroking.GameSystems;

/// <summary>How a village is currently behaving toward the undead threat.</summary>
public enum VillagePosture : byte
{
    Calm = 0,       // no known threat — daily life
    Defending = 1,  // has militia guard → muster a hunting party and attack the threat
    Fleeing = 2,    // no guard, daytime → civilians flee to a safer neighbouring village
    Cowering = 3,   // no guard, night → too dark to run, civilians stay home (easy prey)
}

/// <summary>
/// A village: a cluster of structures and people that act together. Membership is by
/// <see cref="Movement.Unit.VillageId"/>; roads to neighbours are precomputed in
/// <see cref="Neighbors"/>. All the reactive state (alert level, known threat, chosen
/// flee destination) is recomputed each tick by <see cref="VillageSystem"/>.
/// </summary>
public class Village
{
    public int Id;
    public string Name = "";
    public Vec2 Center;
    public float Radius = 25f;
    public List<int> Neighbors = new();   // village ids reachable by road

    // ── runtime state (owned by VillageSystem.Update) ──
    public VillagePosture Posture;
    public float AlertLevel;               // 0..1, decays over time; 1 = fresh warning
    public bool ThreatKnown;
    public uint ThreatUnitId = GameConstants.InvalidUnit;
    public Vec2 ThreatPos;
    public Vec2 FleeTarget;
    public bool FleeTargetSet;
    public int DefenderCount;              // living militia/hunters that belong here
    public int PeasantCount;
}

/// <summary>
/// Runtime coordinator for villages. Each tick it recomputes per-village membership
/// counts, decays the alert level, resolves the current known threat position, and
/// derives a <see cref="VillagePosture"/> that the village AI handlers
/// (<see cref="AI.VillagerHandler"/>, <see cref="AI.WatchdogHandler"/> and the shared
/// <see cref="AI.CombatUnitHandler"/> via rallied militia) read to decide what to do.
///
/// Warnings are pushed in by watchdogs (and engaged militia) through
/// <see cref="RaiseAlert"/>; the system does not scan for threats itself.
/// </summary>
public class VillageSystem
{
    private readonly List<Village> _villages = new();

    public IReadOnlyList<Village> Villages => _villages;
    public int Count => _villages.Count;
    public Village? Get(int id) => (id >= 0 && id < _villages.Count) ? _villages[id] : null;
    public void Clear() => _villages.Clear();

    public int Add(Village v)
    {
        v.Id = _villages.Count;
        _villages.Add(v);
        return v.Id;
    }

    /// <summary>Look up a village index by its authoring id (from the villages JSON).
    /// Used at load time to resolve neighbour/patrol links. Linear scan — only runs
    /// during map load.</summary>
    public int FindByName(string name)
    {
        for (int i = 0; i < _villages.Count; i++)
            if (_villages[i].Name == name) return i;
        return -1;
    }

    // Tunables
    private const float AlertDecayPerSec = 0.05f; // ~20s memory once the threat is gone
    private const float PostureThreshold = 0.12f; // below this the village is Calm

    /// <summary>Sound the alarm for a village: a watchdog barked, or a guard engaged.
    /// Refreshes the alert to full and records the current threat unit + position.</summary>
    public void RaiseAlert(int villageId, uint threatUnitId, Vec2 threatPos)
    {
        var v = Get(villageId);
        if (v == null) return;
        v.AlertLevel = 1f;
        v.ThreatKnown = true;
        v.ThreatUnitId = threatUnitId;
        v.ThreatPos = threatPos;
    }

    public void Update(UnitArrays units, float dt, bool isNight)
    {
        if (_villages.Count == 0) return;

        for (int vi = 0; vi < _villages.Count; vi++)
        {
            _villages[vi].DefenderCount = 0;
            _villages[vi].PeasantCount = 0;
        }

        // One pass over units: tally membership and let engaged guards refresh the alarm
        // (so a militiaman who spots the undead himself also mobilises the town).
        for (int i = 0; i < units.Count; i++)
        {
            if (!units[i].Alive) continue;
            short vid = units[i].VillageId;
            if (vid < 0 || vid >= _villages.Count) continue;
            var v = _villages[vid];
            byte arch = units[i].Archetype;
            bool isMilitia = arch == AI.ArchetypeRegistry.ArmyUnit || arch == AI.ArchetypeRegistry.PatrolSoldier;
            if (isMilitia || arch == AI.ArchetypeRegistry.ArcherUnit)
                v.DefenderCount++;
            else if (arch == AI.ArchetypeRegistry.Civilian)
                v.PeasantCount++;

            if (isMilitia && units[i].AlertState >= (byte)AI.UnitAlertState.Alert
                && units[i].AlertTarget != GameConstants.InvalidUnit)
            {
                int ti = UnitUtil.ResolveUnitIndex(units, units[i].AlertTarget);
                if (ti >= 0 && units[ti].Alive && units[ti].Faction == Faction.Undead)
                {
                    v.AlertLevel = 1f;
                    v.ThreatKnown = true;
                    v.ThreatUnitId = units[i].AlertTarget;
                    v.ThreatPos = units[ti].Position;
                }
            }
        }

        for (int vi = 0; vi < _villages.Count; vi++)
        {
            var v = _villages[vi];

            // Keep the threat position fresh; forget the threat if it died/despawned.
            if (v.ThreatKnown && v.ThreatUnitId != GameConstants.InvalidUnit)
            {
                int ti = UnitUtil.ResolveUnitIndex(units, v.ThreatUnitId);
                if (ti >= 0 && units[ti].Alive && units[ti].Faction == Faction.Undead)
                    v.ThreatPos = units[ti].Position;
                else
                    v.ThreatUnitId = GameConstants.InvalidUnit;
            }

            v.AlertLevel = MathUtil.Clamp(v.AlertLevel - AlertDecayPerSec * dt, 0f, 1f);

            if (v.AlertLevel < PostureThreshold)
            {
                v.Posture = VillagePosture.Calm;
                v.ThreatKnown = false;
                v.FleeTargetSet = false;
                continue;
            }

            if (v.DefenderCount > 0)
            {
                v.Posture = VillagePosture.Defending;
                v.FleeTargetSet = false;
                RallyMilitia(units, v);
            }
            else if (!isNight)
            {
                if (v.Posture != VillagePosture.Fleeing || !v.FleeTargetSet)
                    ComputeFleeTarget(v);
                v.Posture = VillagePosture.Fleeing;
            }
            else
            {
                v.Posture = VillagePosture.Cowering;
                v.FleeTargetSet = false;
            }
        }
    }

    /// <summary>Send every idle village militiaman after the known threat, turning the
    /// garrison into a hunting party. Once a unit is chasing (Combat routine) it self-
    /// sustains via its combat target, so this only nudges the not-yet-engaged ones.</summary>
    private void RallyMilitia(UnitArrays units, Village v)
    {
        if (v.ThreatUnitId == GameConstants.InvalidUnit) return;
        for (int i = 0; i < units.Count; i++)
        {
            if (!units[i].Alive || units[i].VillageId != v.Id) continue;
            byte arch = units[i].Archetype;
            if (arch != AI.ArchetypeRegistry.ArmyUnit && arch != AI.ArchetypeRegistry.PatrolSoldier) continue;
            // Idle (0) or Alert (1) in CombatUnitHandler — not already chasing/returning.
            if (units[i].AlertState == (byte)AI.UnitAlertState.Unaware && units[i].Routine <= 1)
            {
                units[i].AlertState = (byte)AI.UnitAlertState.Aggressive;
                units[i].AlertTarget = v.ThreatUnitId;
            }
        }
    }

    /// <summary>Choose where fleeing civilians run: the neighbouring village that lies
    /// most directly away from the threat. With no neighbours, just run away from it.</summary>
    private void ComputeFleeTarget(Village v)
    {
        Vec2 awayDir = v.Center - v.ThreatPos;
        if (awayDir.LengthSq() < 0.01f) awayDir = new Vec2(1, 0);
        else awayDir = awayDir.Normalized();

        int best = -1;
        float bestScore = -2f;
        for (int k = 0; k < v.Neighbors.Count; k++)
        {
            var nb = Get(v.Neighbors[k]);
            if (nb == null) continue;
            Vec2 d = nb.Center - v.Center;
            if (d.LengthSq() < 0.01f) continue;
            d = d.Normalized();
            float score = d.X * awayDir.X + d.Y * awayDir.Y;
            if (score > bestScore) { bestScore = score; best = v.Neighbors[k]; }
        }

        v.FleeTarget = best >= 0 ? Get(best)!.Center : v.Center + awayDir * 60f;
        v.FleeTargetSet = true;
    }
}
