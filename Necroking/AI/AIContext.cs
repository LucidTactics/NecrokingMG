using Necroking.Core;
using Necroking.Data;
using Necroking.Movement;

namespace Necroking.AI;

/// <summary>
/// Passed to archetype handlers each frame. Wraps all the state an AI needs
/// to make decisions without coupling to Simulation internals.
/// Zero allocations — this is a ref struct passed by reference.
/// </summary>
public ref struct AIContext
{
    public int UnitIndex;
    public UnitArrays Units;
    public float Dt;
    public int FrameNumber;

    // World queries (set by Simulation before AI tick)
    public GameData? GameData;
    public World.Pathfinder? Pathfinder;
    public Spatial.Quadtree? Quadtree;
    public GameSystems.HordeSystem? Horde;
    public GameSystems.TriggerSystem? TriggerSystem;
    public World.EnvironmentSystem? EnvSystem;

    // Game clock
    public float GameTime;          // total elapsed seconds
    public float DayTime;           // 0..1 fraction of current day cycle
    public bool IsNight;            // true during night period

    // Convenience accessors
    public readonly Vec2 MyPos => Units.Position[UnitIndex];
    public readonly float MySpeed => Units.MaxSpeed[UnitIndex];
    public readonly Faction MyFaction => Units.Faction[UnitIndex];
    public readonly uint MyId => Units.Id[UnitIndex];
    public readonly byte Routine { get => Units.Routine[UnitIndex]; set => Units.Routine[UnitIndex] = value; }
    public readonly byte Subroutine { get => Units.Subroutine[UnitIndex]; set => Units.Subroutine[UnitIndex] = value; }
    public readonly float SubroutineTimer { get => Units.SubroutineTimer[UnitIndex]; set => Units.SubroutineTimer[UnitIndex] = value; }
    public readonly byte AlertState { get => Units.AlertState[UnitIndex]; set => Units.AlertState[UnitIndex] = value; }
    public readonly float AlertTimer { get => Units.AlertTimer[UnitIndex]; set => Units.AlertTimer[UnitIndex] = value; }
    public readonly uint AlertTarget { get => Units.AlertTarget[UnitIndex]; set => Units.AlertTarget[UnitIndex] = value; }
}

/// <summary>Alert states shared by all archetypes.</summary>
public enum UnitAlertState : byte
{
    Unaware = 0,
    Alert = 1,
    Aggressive = 2,
}
