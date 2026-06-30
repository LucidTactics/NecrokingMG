using System.Collections.Generic;

namespace Necroking.Game.Jobs;

/// <summary>
/// How a job's worker physically executes. Drives which branch of
/// <see cref="Necroking.AI.WorkerHandler"/> runs.
///   Collect — walk to a source in the world (foragable / corpse), gather it,
///             carry it to a storage building, deposit. (P1)
///   Process — fetch inputs from a source stockpile, carry to a processing
///             building, channel a craft, emit output. (P3 — defined now, not
///             yet executed.)
/// </summary>
public enum JobArchetype { Collect, Process }

/// <summary>
/// Static, data-driven template for one job category (loaded from
/// data/jobs.json). One JobDef per row on the job board. A job is tied to a
/// building type that gates its worker cap; the runtime, player-tunable side
/// (priority / cap / assigned workers) lives in <see cref="JobState"/>.
/// </summary>
public class JobDef
{
    public string Id = "";
    public string DisplayName = "";
    public string Icon = "";
    public JobArchetype Archetype = JobArchetype.Collect;

    /// <summary>Env def id of the building that hosts this job. The job is hidden
    /// from the board until at least one instance exists, and the worker cap is
    /// clamped to (instances × <see cref="WorkerSlotsPerBuilding"/>).</summary>
    public string BuildingDefId = "";
    public int WorkerSlotsPerBuilding = 1;

    /// <summary>Optional unit-type/tag filter — a worker must satisfy it to take
    /// this job. Empty = any assigned (humanoid) worker. Hook only in v1; not
    /// enforced yet.</summary>
    public string RequiredCapability = "";

    // ── Collect archetype ──
    /// <summary>What the worker gathers from the world:
    ///   "foragable" — a foragable env object (mushroom); instant pickup.
    ///   "corpse"    — a loose Corpse; physically carried to the pile.
    ///   "berry"     — a Berries-state bush; channel-poison it at the source.</summary>
    public string CollectKind = "foragable";
    /// <summary>ForagableType filter for CollectKind=="foragable" ("" = any).
    /// Matched against EnvironmentObjectDef.ForagableType.</summary>
    public string SourceForagableType = "";
    /// <summary>Resource id deposited into the host building's stockpile on a
    /// successful collect. Must match the building def's StoredResource.</summary>
    public string StoreResource = "";

    // ── Process archetype (P3 — present so jobs.json is complete; unused in P1) ──
    public List<JobResourceAmount> Inputs = new();
    public List<JobOutput> Outputs = new();
    /// <summary>How multiple outputs are interpreted:
    ///   false (co-products) — every output is emitted each craft (Breakdown:
    ///          Essence + Reagent).
    ///   true  (alternatives) — exactly one output is produced per craft, chosen
    ///          by the maintain-stock targets in JobState (Potions: Frenzy vs Poison).</summary>
    public bool OutputChoice = false;
    public float ProcessTime = 5f;
    /// <summary>True when the output is a spawned unit (Reanimate) rather than a
    /// stockpiled good — capped by the unit limit, not building storage.</summary>
    public bool SpawnsUnit = false;
    /// <summary>UnitDef id spawned per craft when SpawnsUnit (Reanimate).</summary>
    public string SpawnUnitDefId = "skeleton";
}

/// <summary>Output resource id that routes to the global PlayerResources.Essence
/// pool instead of a building stockpile. Placeholder economy hook.</summary>
public static class JobResources
{
    public const string Essence = "Essence";
    public const string Corpse = "Corpse"; // abstract corpse count stored in a Corpse Pile (storeResource in jobs.json)
}

public struct JobResourceAmount
{
    public string Resource;
    public int Amount;
}

/// <summary>A single output of a multi-output process job (Potion X vs Y). The
/// player-set target stock + per-output priority live in <see cref="JobState"/>.</summary>
public struct JobOutput
{
    public string Id;          // resource/item id produced
    public string DisplayName;
    public int Amount;         // produced per craft
}
