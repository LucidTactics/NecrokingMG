using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Necroking.Core;
using Necroking.Data;
using Necroking.GameSystems;
using Necroking.Movement;
using Necroking.World;

namespace Necroking.Render;

public struct DebugFlags
{
    public bool ShowHorde;
    public bool ShowQuadtree;
    public bool ShowPathGlobal;
    public bool ShowPathUnit;
    public bool ShowVelocities;
    public bool ShowGrid;
}

public enum CollisionDebugMode
{
    Off = 0,
    CostField,
    UnitORCA,
    Velocity,
    OccupiedTiles,
    All,
    Count
}

public class DebugDraw
{
    public uint SelectedUnitID = GameConstants.InvalidUnit;

    private Texture2D? _pixel;

    public void Draw(SpriteBatch batch, GraphicsDevice device, Simulation sim, Camera25D cam,
                     Renderer renderer, DebugFlags flags, bool showUnitRadius = false)
    {
        EnsurePixel(device);

        var units = sim.Units;

        if (showUnitRadius)
        {
            for (int i = 0; i < units.Count; i++)
            {
                if (!units.Alive[i]) continue;
                DrawCircle(batch, renderer, cam, units.Position[i], units.Radius[i],
                    units.Faction[i] == Faction.Undead ? Color.Green : Color.Red);
            }
        }

        if (flags.ShowVelocities)
        {
            for (int i = 0; i < units.Count; i++)
            {
                if (!units.Alive[i]) continue;
                var start = renderer.WorldToScreen(units.Position[i], 0f, cam);
                var vel = units.Velocity[i];
                if (vel.LengthSq() > 0.01f)
                {
                    var end = renderer.WorldToScreen(units.Position[i] + vel * 0.5f, 0f, cam);
                    DrawLine(batch, start, end, Color.Yellow);
                }
            }
        }
    }

    /// <summary>
    /// Draw collision debug overlays based on the current debug mode.
    /// Call after game world drawing but before HUD.
    /// </summary>
    public void DrawCollisionDebug(SpriteBatch batch, GraphicsDevice device, Simulation sim,
                                    Camera25D cam, Renderer renderer,
                                    CollisionDebugMode mode, EnvironmentSystem? envSystem = null)
    {
        if (mode == CollisionDebugMode.Off) return;
        EnsurePixel(device);

        if (mode == CollisionDebugMode.CostField || mode == CollisionDebugMode.All)
            DrawCostFieldOverlay(batch, sim, cam, renderer);

        if (mode == CollisionDebugMode.UnitORCA || mode == CollisionDebugMode.All)
            DrawUnitORCARadii(batch, sim, cam, renderer);

        if (mode == CollisionDebugMode.Velocity || mode == CollisionDebugMode.All)
            DrawVelocityVectors(batch, sim, cam, renderer);

        if (mode == CollisionDebugMode.OccupiedTiles || mode == CollisionDebugMode.All)
            DrawOccupiedTiles(batch, sim, cam, renderer, envSystem);
    }

    /// <summary>
    /// Get a display string for the current collision debug mode.
    /// </summary>
    public static string GetModeLabel(CollisionDebugMode mode) => mode switch
    {
        CollisionDebugMode.Off => "Off",
        CollisionDebugMode.CostField => "Cost Field",
        CollisionDebugMode.UnitORCA => "Unit ORCA Radii",
        CollisionDebugMode.Velocity => "Velocity Vectors",
        CollisionDebugMode.OccupiedTiles => "Occupied Tiles",
        CollisionDebugMode.All => "All",
        _ => "Unknown"
    };

    // ========== Cost Field Overlay ==========

    private void DrawCostFieldOverlay(SpriteBatch batch, Simulation sim, Camera25D cam, Renderer renderer)
    {
        if (_pixel == null) return;
        var grid = sim.Grid;
        int gridW = grid.Width;
        int gridH = grid.Height;

        // Compute visible tile range to avoid drawing off-screen tiles
        float viewLeft = cam.Position.X - renderer.ScreenW / (2f * cam.Zoom) - 1;
        float viewRight = cam.Position.X + renderer.ScreenW / (2f * cam.Zoom) + 1;
        float viewTop = cam.Position.Y - renderer.ScreenH / (cam.Zoom * cam.YRatio) - 1;
        float viewBottom = cam.Position.Y + renderer.ScreenH / (cam.Zoom * cam.YRatio) + 1;

        int minX = Math.Max(0, (int)viewLeft);
        int maxX = Math.Min(gridW - 1, (int)viewRight);
        int minY = Math.Max(0, (int)viewTop);
        int maxY = Math.Min(gridH - 1, (int)viewBottom);

        for (int y = minY; y <= maxY; y++)
        {
            for (int x = minX; x <= maxX; x++)
            {
                byte cost = grid.GetCost(x, y);

                // Open (cost=1): skip (no overlay)
                if (cost <= 1) continue;

                Color tint;
                if (cost >= 255)
                {
                    // Impassable: red tint
                    tint = new Color(255, 40, 40, 80);
                }
                else if (cost >= 8)
                {
                    // Water-like (high cost): blue tint
                    tint = new Color(40, 80, 255, 60);
                }
                else
                {
                    // Rough terrain: yellow tint
                    tint = new Color(255, 220, 40, 45);
                }

                var screenPos = renderer.WorldToScreen(new Vec2(x, y), 0f, cam);
                float tileW = cam.Zoom * GameConstants.TileSize;
                float tileH = cam.Zoom * cam.YRatio * GameConstants.TileSize;

                var rect = new Rectangle(
                    (int)screenPos.X,
                    (int)screenPos.Y,
                    Math.Max(1, (int)tileW),
                    Math.Max(1, (int)tileH));

                batch.Draw(_pixel, rect, tint);
            }
        }
    }

    // ========== Unit ORCA Radii ==========

    private void DrawUnitORCARadii(SpriteBatch batch, Simulation sim, Camera25D cam, Renderer renderer)
    {
        var units = sim.Units;
        int necroIdx = sim.NecromancerIndex;

        for (int i = 0; i < units.Count; i++)
        {
            if (!units.Alive[i]) continue;

            bool isNecromancer = (i == necroIdx);
            bool isUndead = units.Faction[i] == Faction.Undead;

            Color circleColor;
            if (isNecromancer)
                circleColor = new Color(100, 255, 100, 255); // bright green for necromancer
            else if (isUndead)
                circleColor = new Color(50, 200, 50, 200); // green for undead
            else
                circleColor = new Color(220, 50, 50, 200); // red for human

            int segments = isNecromancer ? 32 : 24;
            float radius = units.Radius[i];

            // Draw the circle
            DrawCircle(batch, renderer, cam, units.Position[i], radius, circleColor, segments);

            // Necromancer gets a second, slightly larger circle for emphasis
            if (isNecromancer)
            {
                DrawCircle(batch, renderer, cam, units.Position[i], radius * 1.15f,
                    new Color(180, 255, 180, 120), segments);
            }
        }
    }

    // ========== Velocity Vectors ==========

    private void DrawVelocityVectors(SpriteBatch batch, Simulation sim, Camera25D cam, Renderer renderer)
    {
        var units = sim.Units;

        for (int i = 0; i < units.Count; i++)
        {
            if (!units.Alive[i]) continue;

            var pos = units.Position[i];

            // Current velocity (green arrow)
            var vel = units.Velocity[i];
            if (vel.LengthSq() > 0.001f)
            {
                var start = renderer.WorldToScreen(pos, 0f, cam);
                var end = renderer.WorldToScreen(pos + vel * 0.5f, 0f, cam);
                DrawArrow(batch, start, end, new Color(50, 255, 50, 220));
            }

            // Preferred velocity (blue arrow)
            var prefVel = units.PreferredVel[i];
            if (prefVel.LengthSq() > 0.001f)
            {
                var start = renderer.WorldToScreen(pos, 0f, cam);
                var end = renderer.WorldToScreen(pos + prefVel * 0.5f, 0f, cam);
                DrawArrow(batch, start, end, new Color(80, 120, 255, 220));
            }
        }
    }

    // ========== Occupied Tiles (Environment Objects) ==========

    private void DrawOccupiedTiles(SpriteBatch batch, Simulation sim, Camera25D cam, Renderer renderer,
                                    EnvironmentSystem? envSystem)
    {
        if (_pixel == null || envSystem == null) return;
        var grid = sim.Grid;

        for (int oi = 0; oi < envSystem.ObjectCount; oi++)
        {
            var obj = envSystem.GetObject(oi);
            if (obj.DefIndex >= envSystem.DefCount) continue;
            var def = envSystem.GetDef(obj.DefIndex);
            if (def.CollisionRadius <= 0f) continue;

            float cx = obj.X + def.CollisionOffsetX;
            float cy = obj.Y + def.CollisionOffsetY;
            float radius = def.CollisionRadius * obj.Scale;

            // Draw the collision circle in magenta
            DrawCircle(batch, renderer, cam, new Vec2(cx, cy), radius, new Color(255, 80, 255, 180));

            // Highlight tiles that the object marks as impassable
            int minTX = Math.Max(0, (int)MathF.Floor(cx - radius));
            int maxTX = Math.Min(grid.Width - 1, (int)MathF.Ceiling(cx + radius));
            int minTY = Math.Max(0, (int)MathF.Floor(cy - radius));
            int maxTY = Math.Min(grid.Height - 1, (int)MathF.Ceiling(cy + radius));

            float r2 = radius * radius;
            for (int ty = minTY; ty <= maxTY; ty++)
            {
                for (int tx = minTX; tx <= maxTX; tx++)
                {
                    float dx = tx + 0.5f - cx;
                    float dy = ty + 0.5f - cy;
                    if (dx * dx + dy * dy <= r2)
                    {
                        var screenPos = renderer.WorldToScreen(new Vec2(tx, ty), 0f, cam);
                        float tileW = cam.Zoom * GameConstants.TileSize;
                        float tileH = cam.Zoom * cam.YRatio * GameConstants.TileSize;

                        var rect = new Rectangle(
                            (int)screenPos.X,
                            (int)screenPos.Y,
                            Math.Max(1, (int)tileW),
                            Math.Max(1, (int)tileH));

                        batch.Draw(_pixel, rect, new Color(255, 120, 255, 50));
                    }
                }
            }
        }
    }

    // ========== Drawing Primitives ==========

    private void EnsurePixel(GraphicsDevice device)
    {
        if (_pixel == null)
        {
            _pixel = new Texture2D(device, 1, 1);
            _pixel.SetData(new[] { Color.White });
        }
    }

    private void DrawCircle(SpriteBatch batch, Renderer renderer, Camera25D cam,
                            Vec2 worldCenter, float worldRadius, Color color, int segments = 24)
    {
        if (_pixel == null) return;
        for (int i = 0; i < segments; i++)
        {
            float a1 = i * MathF.PI * 2f / segments;
            float a2 = (i + 1) * MathF.PI * 2f / segments;
            var p1 = worldCenter + new Vec2(MathF.Cos(a1), MathF.Sin(a1)) * worldRadius;
            var p2 = worldCenter + new Vec2(MathF.Cos(a2), MathF.Sin(a2)) * worldRadius;
            var s1 = renderer.WorldToScreen(p1, 0f, cam);
            var s2 = renderer.WorldToScreen(p2, 0f, cam);
            DrawLine(batch, s1, s2, color);
        }
    }

    private void DrawArrow(SpriteBatch batch, Vector2 start, Vector2 end, Color color)
    {
        if (_pixel == null) return;
        DrawLine(batch, start, end, color);

        // Draw arrowhead
        var diff = end - start;
        float length = diff.Length();
        if (length < 4f) return;

        float headSize = Math.Min(6f, length * 0.3f);
        float angle = MathF.Atan2(diff.Y, diff.X);
        float spread = 0.45f; // radians (~25 degrees)

        var left = end - new Vector2(MathF.Cos(angle - spread), MathF.Sin(angle - spread)) * headSize;
        var right = end - new Vector2(MathF.Cos(angle + spread), MathF.Sin(angle + spread)) * headSize;

        DrawLine(batch, end, left, color);
        DrawLine(batch, end, right, color);
    }

    private void DrawLine(SpriteBatch batch, Vector2 start, Vector2 end, Color color)
    {
        if (_pixel == null) return;
        var diff = end - start;
        float length = diff.Length();
        if (length < 0.5f) return;
        float angle = MathF.Atan2(diff.Y, diff.X);
        batch.Draw(_pixel, start, null, color, angle, Vector2.Zero,
            new Vector2(length, 1f), SpriteEffects.None, 0f);
    }
}
