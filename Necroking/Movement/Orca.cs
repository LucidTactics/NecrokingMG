using System;
using System.Collections.Generic;
using Necroking.Core;

namespace Necroking.Movement;

public struct ORCALine
{
    public Vec2 Point;
    public Vec2 Direction;
}

public struct ORCANeighbor
{
    public Vec2 Position;
    public Vec2 Velocity;
    public float Radius;
    public uint Id;
    public int Priority;
    /// <summary>
    /// Static = immovable obstacle (e.g. a tree). The unit takes 100% of the
    /// avoidance responsibility since the neighbor cannot move. Velocity is
    /// assumed to be Vec2.Zero for statics (caller's responsibility).
    /// </summary>
    public bool IsStatic;
}

public struct ORCAParams
{
    public float TimeHorizon;
    public float MaxSpeed;
    public float Radius;
    public int MaxNeighbors;
    public int Priority;

    public static ORCAParams Default => new()
    {
        TimeHorizon = 3f, MaxSpeed = 3f, Radius = 0.3f, MaxNeighbors = 10, Priority = 0
    };
}

public static class Orca
{
    private const float Epsilon = 1e-5f;

    // Reused across every ComputeORCAVelocity call. Sim is single-threaded; if
    // we ever parallelize unit movement these should go [ThreadStatic].
    // Previously `new List<ORCALine>(neighbors.Count)` allocated a list per
    // unit per tick (~u=600 calls ⇒ 600 small-list allocs/tick of GC pressure).
    private static readonly List<ORCALine> _orcaLinesScratch = new();
    private static readonly List<ORCALine> _projLinesScratch = new();

    private static float Det(Vec2 a, Vec2 b) => a.X * b.Y - a.Y * b.X;

    public static Vec2 ComputeORCAVelocity(
        Vec2 position, Vec2 currentVelocity, Vec2 preferredVelocity,
        List<ORCANeighbor> neighbors, ORCAParams param, float dt)
    {
        var orcaLines = _orcaLinesScratch;
        orcaLines.Clear();
        float invTimeHorizon = 1f / param.TimeHorizon;

        foreach (var neighbor in neighbors)
        {
            Vec2 relPos = neighbor.Position - position;
            Vec2 relVel = currentVelocity - neighbor.Velocity;
            float distSq = relPos.LengthSq();
            float combinedRadius = param.Radius + neighbor.Radius;
            float combinedRadiusSq = combinedRadius * combinedRadius;

            // Responsibility sharing based on priority. Static neighbors (trees,
            // rocks) can't move, so the unit takes 100% of the avoidance — this is
            // the canonical ORCA handling for circular static obstacles.
            float responsibility;
            if (neighbor.IsStatic)
                responsibility = 1.0f;
            else if (param.Priority < neighbor.Priority)
                responsibility = 0.9f;
            else if (param.Priority > neighbor.Priority)
                responsibility = 0.1f;
            else
                responsibility = 0.5f;

            ORCALine line;

            if (distSq > combinedRadiusSq)
            {
                // No collision yet
                Vec2 w = relVel - relPos * invTimeHorizon;
                float wLenSq = w.LengthSq();
                float dotProd = w.Dot(relPos);

                if (dotProd < 0f && dotProd * dotProd > combinedRadiusSq * wLenSq)
                {
                    float wLen = MathF.Sqrt(wLenSq);
                    Vec2 unitW = w * (1f / wLen);
                    line.Direction = new Vec2(unitW.Y, -unitW.X);
                    Vec2 u = unitW * (combinedRadius * invTimeHorizon - wLen);
                    line.Point = currentVelocity + u * responsibility;
                }
                else
                {
                    float dist = MathF.Sqrt(distSq);
                    float leg = MathF.Sqrt(MathF.Max(0f, distSq - combinedRadiusSq));

                    if (Det(relPos, w) > 0f)
                    {
                        line.Direction = new Vec2(
                            relPos.X * leg - relPos.Y * combinedRadius,
                            relPos.X * combinedRadius + relPos.Y * leg
                        ) * (1f / distSq);
                    }
                    else
                    {
                        line.Direction = new Vec2(
                            relPos.X * leg + relPos.Y * combinedRadius,
                            -relPos.X * combinedRadius + relPos.Y * leg
                        ) * (-1f / distSq);
                    }

                    float dotProd2 = relVel.Dot(line.Direction);
                    Vec2 u = line.Direction * dotProd2 - relVel;
                    line.Point = currentVelocity + u * responsibility;
                }
            }
            else
            {
                // Already overlapping — push apart
                float invDT = 1f / MathF.Max(dt, Epsilon);
                Vec2 w = relVel - relPos * invDT;
                float wLen = w.Length();

                if (wLen > Epsilon)
                {
                    Vec2 unitW = w * (1f / wLen);
                    line.Direction = new Vec2(unitW.Y, -unitW.X);
                    Vec2 u = unitW * (combinedRadius * invDT - wLen);
                    line.Point = currentVelocity + u * responsibility;
                }
                else
                {
                    Vec2 pushDir = relPos.LengthSq() > Epsilon
                        ? relPos.Normalized()
                        : new Vec2(1f, 0f);
                    line.Direction = new Vec2(pushDir.Y, -pushDir.X);
                    Vec2 u = pushDir * (combinedRadius * invDT);
                    line.Point = currentVelocity + u * responsibility;
                }
            }

            orcaLines.Add(line);
        }

        var vel = LinearProgram2D(orcaLines, param.MaxSpeed, preferredVelocity, false);
        // Guard against NaN/infinity from degenerate cases
        if (float.IsNaN(vel.X) || float.IsNaN(vel.Y) || float.IsInfinity(vel.X) || float.IsInfinity(vel.Y))
            return Vec2.Zero;
        return vel;
    }

    private static bool LinearProgram1D(
        List<ORCALine> lines, int lineIdx, float maxSpeed,
        Vec2 optVel, bool directionOpt, ref Vec2 result)
    {
        var line = lines[lineIdx];
        float dotProduct = line.Point.Dot(line.Direction);
        float discriminant = dotProduct * dotProduct + maxSpeed * maxSpeed - line.Point.Dot(line.Point);

        if (discriminant < 0f) return false;

        float sqrtDisc = MathF.Sqrt(discriminant);
        float tLeft = -dotProduct - sqrtDisc;
        float tRight = -dotProduct + sqrtDisc;

        for (int j = 0; j < lineIdx; j++)
        {
            float denom = Det(line.Direction, lines[j].Direction);
            float numer = Det(lines[j].Direction, line.Point - lines[j].Point);

            if (MathF.Abs(denom) <= Epsilon)
            {
                if (numer < 0f) return false;
                continue;
            }

            float t = numer / denom;
            if (denom > 0f)
                tRight = MathF.Min(tRight, t);
            else
                tLeft = MathF.Max(tLeft, t);

            if (tLeft > tRight) return false;
        }

        if (directionOpt)
            result = line.Point + (optVel.Dot(line.Direction) > 0f ? tRight : tLeft) * line.Direction;
        else
        {
            float t = line.Direction.Dot(optVel - line.Point);
            t = MathUtil.Clamp(t, tLeft, tRight);
            result = line.Point + t * line.Direction;
        }

        return true;
    }

    private static Vec2 LinearProgram2D(
        List<ORCALine> lines, float maxSpeed, Vec2 optVel, bool directionOpt)
    {
        Vec2 result;

        if (directionOpt)
            result = optVel * maxSpeed;
        else if (optVel.LengthSq() > maxSpeed * maxSpeed)
            result = optVel.Normalized() * maxSpeed;
        else
            result = optVel;

        for (int i = 0; i < lines.Count; i++)
        {
            if (Det(lines[i].Direction, lines[i].Point - result) > 0f)
            {
                Vec2 tempResult = result;
                if (!LinearProgram1D(lines, i, maxSpeed, optVel, directionOpt, ref result))
                {
                    result = tempResult;
                    LinearProgram3Fallback(lines, 0, i, maxSpeed, ref result);
                    return result;
                }
            }
        }

        return result;
    }

    private static void LinearProgram3Fallback(
        List<ORCALine> lines, int numObstLines, int beginLine,
        float maxSpeed, ref Vec2 result)
    {
        float distance = 0f;

        for (int i = beginLine; i < lines.Count; i++)
        {
            float d = Det(lines[i].Direction, lines[i].Point - result);
            if (d > distance)
            {
                var projLines = _projLinesScratch;
                projLines.Clear();

                for (int j = 0; j < i; j++)
                {
                    ORCALine newLine;
                    float crossVal = Det(lines[i].Direction, lines[j].Direction);

                    if (MathF.Abs(crossVal) <= Epsilon)
                    {
                        if (lines[i].Direction.Dot(lines[j].Direction) > 0f)
                            continue;
                        newLine.Point = (lines[i].Point + lines[j].Point) * 0.5f;
                    }
                    else
                    {
                        float t = Det(lines[j].Direction, lines[i].Point - lines[j].Point) / crossVal;
                        newLine.Point = lines[i].Point + t * lines[i].Direction;
                    }

                    newLine.Direction = (lines[j].Direction - lines[i].Direction).Normalized();
                    projLines.Add(newLine);
                }

                Vec2 optDir = new(-lines[i].Direction.Y, lines[i].Direction.X);
                if (optDir.LengthSq() > Epsilon)
                    result = LinearProgram2D(projLines, maxSpeed, optDir, true);

                distance = Det(lines[i].Direction, lines[i].Point - result);
                if (distance < 0f) distance = 0f;
            }
        }
    }
}
