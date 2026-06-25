using System;

namespace Necroking.Core;

public struct Vec2
{
    public float X;
    public float Y;

    public Vec2(float x, float y) { X = x; Y = y; }

    public static Vec2 Zero => new(0f, 0f);

    public static Vec2 operator +(Vec2 a, Vec2 b) => new(a.X + b.X, a.Y + b.Y);
    public static Vec2 operator -(Vec2 a, Vec2 b) => new(a.X - b.X, a.Y - b.Y);
    public static Vec2 operator *(Vec2 v, float s) => new(v.X * s, v.Y * s);
    public static Vec2 operator *(float s, Vec2 v) => new(s * v.X, s * v.Y);
    public static Vec2 operator -(Vec2 v) => new(-v.X, -v.Y);

    public float Dot(Vec2 o) => X * o.X + Y * o.Y;
    public float Cross(Vec2 o) => X * o.Y - Y * o.X;
    public float LengthSq() => X * X + Y * Y;
    public float Length() => MathF.Sqrt(LengthSq());

    public Vec2 Normalized()
    {
        float len = Length();
        return len > 1e-6f ? new Vec2(X / len, Y / len) : Zero;
    }

    public Vec2 PerpCW() => new(Y, -X);
    public Vec2 PerpCCW() => new(-Y, X);

    public static Vec2 Lerp(Vec2 a, Vec2 b, float t) =>
        new(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);

    /// <summary>Squared distance between two points (no sqrt). Use in nearest-tracking loops.</summary>
    public static float DistSq(Vec2 a, Vec2 b) => (a - b).LengthSq();
    /// <summary>Distance between two points.</summary>
    public static float Dist(Vec2 a, Vec2 b) => MathF.Sqrt(DistSq(a, b));
    /// <summary>True if a and b are within `range` of each other — centralizes the
    /// error-prone "remember to square the range" idiom (DistSq &lt;= range*range).</summary>
    public static bool WithinRange(Vec2 a, Vec2 b, float range) => DistSq(a, b) <= range * range;

    public override string ToString() => $"({X:F2}, {Y:F2})";
}

public struct GridCoord : IEquatable<GridCoord>
{
    public int X;
    public int Y;

    public GridCoord(int x, int y) { X = x; Y = y; }

    public bool Equals(GridCoord other) => X == other.X && Y == other.Y;
    public override bool Equals(object? obj) => obj is GridCoord gc && Equals(gc);
    public override int GetHashCode() => HashCode.Combine(X, Y);
    public static bool operator ==(GridCoord a, GridCoord b) => a.Equals(b);
    public static bool operator !=(GridCoord a, GridCoord b) => !a.Equals(b);
}

public static class GameConstants
{
    public const float TileSize = 1.0f;
    public const int MaxUnits = 8192;
    public const float InfCost = 1e18f;
    public const uint InvalidUnit = uint.MaxValue;

    // Corpse carry mode. false = pick up and carry the raw corpse sprite (current
    // prototype). true = the legacy "body bag" flow (bag the corpse, then carry the
    // bag). The body-bag code is fully preserved behind this flag — flip to re-enable.
    public const bool UseBodyBag = false;
}

public static class MathUtil
{
    public static float Lerp(float a, float b, float t) => a + (b - a) * t;
    public static float Clamp(float v, float lo, float hi) => v < lo ? lo : (v > hi ? hi : v);
}
