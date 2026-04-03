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
}
