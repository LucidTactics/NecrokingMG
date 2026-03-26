using System;

namespace Necroking.Core;

public enum CombatTargetKind : byte { None, Unit, Building, Wall }

public struct CombatTarget : IEquatable<CombatTarget>
{
    public CombatTargetKind Kind;
    public uint Value;

    public static CombatTarget None => new() { Kind = CombatTargetKind.None };
    public static CombatTarget Unit(uint uid) => new() { Kind = CombatTargetKind.Unit, Value = uid };
    public static CombatTarget Building(int idx) => new() { Kind = CombatTargetKind.Building, Value = (uint)idx };
    public static CombatTarget Wall(int tileIdx) => new() { Kind = CombatTargetKind.Wall, Value = (uint)tileIdx };

    public bool IsNone => Kind == CombatTargetKind.None;
    public bool IsUnit => Kind == CombatTargetKind.Unit;
    public bool IsBuilding => Kind == CombatTargetKind.Building;
    public bool IsWall => Kind == CombatTargetKind.Wall;

    public uint UnitID => Value;
    public int BuildingIdx => (int)Value;
    public int WallTileIdx => (int)Value;

    public bool Equals(CombatTarget other) => Kind == other.Kind && Value == other.Value;
    public override bool Equals(object? obj) => obj is CombatTarget ct && Equals(ct);
    public override int GetHashCode() => HashCode.Combine(Kind, Value);
    public static bool operator ==(CombatTarget a, CombatTarget b) => a.Equals(b);
    public static bool operator !=(CombatTarget a, CombatTarget b) => !a.Equals(b);
}
