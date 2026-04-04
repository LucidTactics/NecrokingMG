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
    public readonly Vec2 MyPos => Units[UnitIndex].Position;
    public readonly float MySpeed => Units[UnitIndex].MaxSpeed;
    public readonly Faction MyFaction => Units[UnitIndex].Faction;
    public readonly uint MyId => Units[UnitIndex].Id;
    public readonly byte Routine { get => Units[UnitIndex].Routine; set => Units[UnitIndex].Routine = value; }
    public readonly byte Subroutine { get => Units[UnitIndex].Subroutine; set => Units[UnitIndex].Subroutine = value; }
    public readonly float SubroutineTimer { get => Units[UnitIndex].SubroutineTimer; set => Units[UnitIndex].SubroutineTimer = value; }
    public readonly byte AlertState { get => Units[UnitIndex].AlertState; set => Units[UnitIndex].AlertState = value; }
    public readonly float AlertTimer { get => Units[UnitIndex].AlertTimer; set => Units[UnitIndex].AlertTimer = value; }
    public readonly uint AlertTarget { get => Units[UnitIndex].AlertTarget; set => Units[UnitIndex].AlertTarget = value; }
}

/// <summary>Alert states shared by all archetypes.</summary>
public enum UnitAlertState : byte
{
    Unaware = 0,
    Alert = 1,
    Aggressive = 2,
}
