namespace Necroking.AI;

/// <summary>
/// Interface for AI archetype handlers. One singleton instance per archetype type.
/// All per-unit state lives in UnitArrays — handlers are stateless.
///
/// Architecture:
///   Archetype (IArchetypeHandler) — singleton per type, drives behavior
///     → Routine (byte index) — high-level behavior mode (Idle, Fighting, Fleeing, etc.)
///       → Subroutine (byte index) — step within routine (MoveToTarget, Attack, Wait, etc.)
///
/// The handler reads Routine/Subroutine/Timer from UnitArrays, executes the current step,
/// and advances or switches routines based on events and conditions.
///
/// No switch statements in the hot path — subroutine steps are dispatched via delegate arrays.
/// </summary>
public interface IArchetypeHandler
{
    /// <summary>Per-frame update for one unit. Called from Simulation after awareness pass.</summary>
    void Update(ref AIContext ctx);

    /// <summary>Called when a unit of this archetype is first spawned. Initialize routine state.</summary>
    void OnSpawn(ref AIContext ctx);

    /// <summary>
    /// Fired by <see cref="AIContext.TransitionTo"/> / <see cref="AIControl"/> just BEFORE
    /// the unit leaves <paramref name="oldRoutine"/>. Put the routine's cleanup here — clear
    /// the fields that routine owns (combat locks, target indices, work timers) so no exit
    /// path can leak them. Written once per routine instead of once per transition site.
    ///
    /// Contract: touch unit fields only (ctx.Units[ctx.UnitIndex].*). Never change
    /// Routine/Subroutine from inside a hook (no nested transitions), and never rely on the
    /// context's world services (Horde, EnvSystem, …) — external interrupters call hooks
    /// with a minimal context where those are null.
    /// </summary>
    void OnRoutineExit(ref AIContext ctx, byte oldRoutine, byte newRoutine) { }

    /// <summary>
    /// Fired just AFTER the unit enters <paramref name="newRoutine"/> (Routine/Subroutine/
    /// SubroutineTimer already stamped). Put entry invariants here — state that must hold
    /// whenever the routine is entered, whatever path led in (e.g. "Returning means
    /// disengaged: Target cleared, InCombat false"). Same contract as OnRoutineExit.
    /// </summary>
    void OnRoutineEnter(ref AIContext ctx, byte oldRoutine, byte newRoutine) { }

    /// <summary>Human-readable name for the given routine index.</summary>
    string GetRoutineName(byte routine);

    /// <summary>Human-readable name for the given subroutine index within a routine.</summary>
    string GetSubroutineName(byte routine, byte subroutine);
}

/// <summary>
/// Registry of archetype handlers. Indexed by ArchetypeId (byte).
/// Archetypes are registered at startup; the array is indexed during the hot loop.
/// </summary>
public static class ArchetypeRegistry
{
    private static readonly IArchetypeHandler?[] _handlers = new IArchetypeHandler?[32];
    private static readonly string[] _names = new string[32];

    public static void Register(byte id, string name, IArchetypeHandler handler)
    {
        _handlers[id] = handler;
        _names[id] = name;
    }

    public static IArchetypeHandler? Get(byte id) =>
        id < _handlers.Length ? _handlers[id] : null;

    public static string GetName(byte id) =>
        id < _names.Length ? _names[id] ?? "Unknown" : "Unknown";

    /// <summary>
    /// Resolve a unit-def archetype name (e.g. "HordeMinion") to its byte id.
    /// Single source of truth for the name→id mapping — used by every spawn
    /// path (Game1.SpawnUnit, Simulation.SpawnZombieMinion) so a def's archetype
    /// is applied consistently no matter which path creates the unit. Returns
    /// <see cref="None"/> for null/empty/unknown names.
    /// </summary>
    public static byte FromName(string? name) => name switch
    {
        "PlayerControlled" => PlayerControlled,
        "HordeMinion" => HordeMinion,
        "WolfPack" => WolfPack,
        "RatPack" => RatPack,
        "DeerHerd" => DeerHerd,
        "PatrolSoldier" => PatrolSoldier,
        "GuardStationary" => GuardStationary,
        "ArmyUnit" => ArmyUnit,
        "CasterUnit" => CasterUnit,
        "ArcherUnit" => ArcherUnit,
        "Civilian" => Civilian,
        "Worker" => Worker,
        "CorpsePuppet" => CorpsePuppet,
        "Watchdog" => Watchdog,
        "SoloPredator" => SoloPredator,
        "AmbushPredator" => AmbushPredator,
        _ => None,
    };

    // Well-known archetype IDs (constants, not enum, to allow future extension)
    public const byte None = 0;
    public const byte PlayerControlled = 1;
    public const byte HordeMinion = 2;
    public const byte WolfPack = 3;
    public const byte DeerHerd = 4;
    public const byte PatrolSoldier = 5;
    public const byte GuardStationary = 6;
    public const byte ArmyUnit = 7;
    public const byte CasterUnit = 8;
    public const byte ArcherUnit = 9;
    public const byte Civilian = 10;
    public const byte RatPack = 11;
    public const byte Worker = 12;
    public const byte CorpsePuppet = 13;
    public const byte Watchdog = 14;
    /// <summary>Un-packed hunting animal, hit-and-run cycle (dire wolves, boars).</summary>
    public const byte SoloPredator = 15;
    /// <summary>SoloPredator that waits for the target to face away before committing (bears).</summary>
    public const byte AmbushPredator = 16;
}
