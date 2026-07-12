using System;
using System.Collections.Generic;
using Necroking.Core;
using Necroking.Lib;

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
    public int Priority;
    // (MaxNeighbors and the unused Default preset were deleted 2026-07-04 —
    //  neighbor caps live at the gather site in Simulation: TopK dynamic + 6
    //  static; the solver itself takes whatever list it's given.)
}

public static class Orca
{
    private const float Epsilon = 1e-5f;

    // Reused across every ComputeORCAVelocity call. Sim is single-threaded; if
    // we ever parallelize unit movement these should go [ThreadStatic].
    // Previously `new List<ORCALine>(neighbors.Count)` allocated a list per
    // unit per tick (~u=600 calls ⇒ 600 small-list allocs/tick of GC pressure).
    // _projLinesScratch is safe to share since the 2026-07-04 RVO2-canonical
    // restructure: LP2D no longer calls the LP3 fallback (it returns a fail
    // index and the CALLER invokes LP3, whose inner LP2D failure rolls back
    // instead of recursing), so re-entry is structurally impossible.
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

        // Static (obstacle) lines FIRST — RVO2's numObstLines convention. The
        // infeasibility fallback keeps [0, numStaticLines) hard and relaxes
        // only agent lines, so crowd pressure can never squeeze a velocity
        // INTO a tree/rock (the old fallback relaxed every constraint).
        int numStaticLines = 0;
        for (int n = 0; n < neighbors.Count; n++)
        {
            if (!neighbors[n].IsStatic) continue;
            orcaLines.Add(MakeLine(neighbors[n], position, currentVelocity, param, invTimeHorizon, dt));
            numStaticLines++;
        }
        for (int n = 0; n < neighbors.Count; n++)
        {
            if (neighbors[n].IsStatic) continue;
            orcaLines.Add(MakeLine(neighbors[n], position, currentVelocity, param, invTimeHorizon, dt));
        }

        Vec2 vel = default;
        int lineFail = LinearProgram2D(orcaLines, param.MaxSpeed, preferredVelocity, false, ref vel);
        if (lineFail < orcaLines.Count)
            LinearProgram3(orcaLines, numStaticLines, lineFail, param.MaxSpeed, ref vel);

        // Guard against NaN/infinity from degenerate cases
        if (float.IsNaN(vel.X) || float.IsNaN(vel.Y) || float.IsInfinity(vel.X) || float.IsInfinity(vel.Y))
            return Vec2.Zero;
        return vel;
    }

    /// <summary>Construct one neighbor's ORCA half-plane constraint — canonical
    /// RVO2 construction (cutoff-circle / leg projection / colliding push).</summary>
    private static ORCALine MakeLine(ORCANeighbor neighbor, Vec2 position, Vec2 currentVelocity,
        ORCAParams param, float invTimeHorizon, float dt)
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
                    // Degenerate w — mirror the healthy branch above: with
                    // relVel≈0, w = -relPos·invDT, so the push must point AWAY
                    // from the neighbor (the old +relPos sign drove the unit
                    // deeper into the overlap).
                    Vec2 pushDir;
                    if (relPos.LengthSq() > Epsilon)
                    {
                        pushDir = relPos.Normalized() * -1f;
                    }
                    else
                    {
                        // Exactly coincident: derive the arbitrary direction from
                        // the neighbor's id so the two sides pick different escape
                        // vectors — a shared constant translated the pair together
                        // forever without separating.
                        float ang = (neighbor.Id * 2654435761u & 0xFFFFu) * (MathF.PI * 2f / 65536f);
                        pushDir = new Vec2(MathF.Cos(ang), MathF.Sin(ang));
                    }
                    line.Direction = new Vec2(pushDir.Y, -pushDir.X);
                    Vec2 u = pushDir * (combinedRadius * invDT);
                    line.Point = currentVelocity + u * responsibility;
                }
            }

            return line;
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

    /// <summary>2D linear program: velocity closest to optVel subject to the
    /// half-plane constraints and the maxSpeed disc. Canonical RVO2 form:
    /// returns lines.Count on success, or the index of the failing line — the
    /// CALLER decides whether to run the LP3 relaxation (the old auto-recursion
    /// into the fallback both forced a per-iteration allocation and "relaxed"
    /// projected artifact lines, which is meaningless w.r.t. the original
    /// problem). result holds the best feasible point found so far either way.</summary>
    private static int LinearProgram2D(
        List<ORCALine> lines, float maxSpeed, Vec2 optVel, bool directionOpt, ref Vec2 result)
    {
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
                    return i;
                }
            }
        }

        return lines.Count;
    }

    /// <summary>
    /// Infeasible-case relaxation (canonical RVO2 linearProgram3): starting at
    /// the first failed line, minimize the maximum violation of the AGENT
    /// lines while keeping the first numStaticLines obstacle lines hard —
    /// dense crowd pressure at a tree line yields "wait / slide along the
    /// tree", never "press into the tree". The inner LP2D failure restores
    /// the previous result instead of recursing (re-entry is structurally
    /// impossible), which is why projLines can be shared scratch now.
    /// </summary>
    private static void LinearProgram3(
        List<ORCALine> lines, int numStaticLines, int beginLine, float maxSpeed, ref Vec2 result)
    {
        float distance = 0f;

        for (int i = beginLine; i < lines.Count; i++)
        {
            if (Det(lines[i].Direction, lines[i].Point - result) > distance)
            {
                var projLines = _projLinesScratch;
                projLines.Clear();
                // Obstacle lines stay hard.
                for (int j = 0; j < numStaticLines; j++)
                    projLines.Add(lines[j]);

                for (int j = numStaticLines; j < i; j++)
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

                Vec2 tempResult = result;
                if (LinearProgram2D(projLines, maxSpeed,
                        new Vec2(-lines[i].Direction.Y, lines[i].Direction.X), true, ref result) < projLines.Count)
                {
                    // "This should in principle not happen" (RVO2): the result
                    // is already on projLines' boundary; a failure here is
                    // numerical. Keep the previous best rather than recursing.
                    result = tempResult;
                }

                distance = Det(lines[i].Direction, lines[i].Point - result);
            }
        }
    }
}
