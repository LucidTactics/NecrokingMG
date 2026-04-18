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
    All,
    Chunks,
    CostField,
    UnitORCA,
    Velocity,
    OccupiedTiles,
    Count
}

public class DebugDraw
{
    public uint SelectedUnitID = GameConstants.InvalidUnit;

    private Texture2D? _pixel;
    private SpriteFont? _font;

    public void SetFont(SpriteFont? font) => _font = font;

    public void Draw(SpriteBatch batch, GraphicsDevice device, Simulation sim, Camera25D cam,
                     Renderer renderer, DebugFlags flags, bool showUnitRadius = false)
    {
        EnsurePixel(device);

        var units = sim.Units;

        if (showUnitRadius)
        {
            for (int i = 0; i < units.Count; i++)
            {
                if (!units[i].Alive) continue;
                DrawCircle(batch, renderer, cam, units[i].Position, units[i].Radius,
                    units[i].Faction == Faction.Undead ? Color.Green :
                    units[i].Faction == Faction.Animal ? Color.Yellow : Color.Red);
            }
        }

        if (flags.ShowVelocities)
        {
            for (int i = 0; i < units.Count; i++)
            {
                if (!units[i].Alive) continue;
                var start = renderer.WorldToScreen(units[i].Position, 0f, cam);
                var vel = units[i].Velocity;
                if (vel.LengthSq() > 0.01f)
                {
                    var end = renderer.WorldToScreen(units[i].Position + vel * 0.5f, 0f, cam);
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
                                    CollisionDebugMode mode, EnvironmentSystem? envSystem = null,
                                    Pathfinder? pathfinder = null)
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

        if (mode == CollisionDebugMode.Chunks || mode == CollisionDebugMode.All)
            DrawChunkOverlay(batch, sim, cam, renderer, pathfinder);
    }

    /// <summary>
    /// Get a display string for the current collision debug mode.
    /// </summary>
    public static string GetModeLabel(CollisionDebugMode mode) => mode switch
    {
        CollisionDebugMode.Off => "Off",
        CollisionDebugMode.All => "All",
        CollisionDebugMode.Chunks => "Chunks",
        CollisionDebugMode.CostField => "Cost Field",
        CollisionDebugMode.UnitORCA => "Unit ORCA Radii",
        CollisionDebugMode.Velocity => "Velocity Vectors",
        CollisionDebugMode.OccupiedTiles => "Occupied Tiles",
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
            if (!units[i].Alive) continue;

            bool isNecromancer = (i == necroIdx);
            bool isUndead = units[i].Faction == Faction.Undead;

            Color circleColor;
            if (isNecromancer)
                circleColor = new Color(100, 255, 100, 255); // bright green for necromancer
            else if (isUndead)
                circleColor = new Color(50, 200, 50, 200); // green for undead
            else
                circleColor = new Color(220, 50, 50, 200); // red for human

            int segments = isNecromancer ? 32 : 24;
            float radius = units[i].Radius;

            // Draw the circle
            DrawCircle(batch, renderer, cam, units[i].Position, radius, circleColor, segments);

            // Necromancer gets a second, slightly larger circle for emphasis
            if (isNecromancer)
            {
                DrawCircle(batch, renderer, cam, units[i].Position, radius * 1.15f,
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
            if (!units[i].Alive) continue;

            var pos = units[i].Position;

            // Current velocity (green arrow)
            var vel = units[i].Velocity;
            if (vel.LengthSq() > 0.001f)
            {
                var start = renderer.WorldToScreen(pos, 0f, cam);
                var end = renderer.WorldToScreen(pos + vel * 0.5f, 0f, cam);
                DrawArrow(batch, start, end, new Color(50, 255, 50, 220));
            }

            // Preferred velocity (blue arrow)
            var prefVel = units[i].PreferredVel;
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
        if (_pixel == null) return;
        var grid = sim.Grid;

        // --- Env object collision circles (match the runtime math used by
        //     BakeCollisions + EnvSpatialIndex + ORCA) ---
        if (envSystem != null)
        {
            for (int oi = 0; oi < envSystem.ObjectCount; oi++)
            {
                var obj = envSystem.GetObject(oi);
                if (obj.DefIndex >= envSystem.DefCount) continue;
                var def = envSystem.GetDef(obj.DefIndex);
                if (def.CollisionRadius <= 0f) continue;

                // Skip destroyed / collected — they aren't in the runtime index.
                if (oi < envSystem.Objects.Count)
                {
                    var rt = envSystem.GetObjectRuntime(oi);
                    if (!rt.Alive || rt.Collected) continue;
                }

                // Same math as BakeCollisions / EnvSpatialIndex: offset and radius
                // both scale by def.Scale * obj.Scale.
                float es = def.Scale * obj.Scale;
                float cx = obj.X + def.CollisionOffsetX * es;
                float cy = obj.Y + def.CollisionOffsetY * es;
                float radius = def.CollisionRadius * es;

                // Magenta outline = what ORCA actually avoids.
                DrawCircle(batch, renderer, cam, new Vec2(cx, cy), radius, new Color(255, 80, 255, 220));

                // Dim tile fill = the footprint the unscaled radius covers.
                // Useful only as context; pathfinding uses the per-tier inflated
                // field, and movement uses circle tests (not tiles) now.
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

                            batch.Draw(_pixel, rect, new Color(255, 120, 255, 40));
                        }
                    }
                }
            }
        }

        // --- Unit collision circles (the .Radius value ORCA uses) ---
        // Necromancer stands out in lime, other undead in cyan, non-undead in
        // orange so it's easy to see who's colliding with what.
        var units = sim.Units;
        int necroIdx = sim.NecromancerIndex;
        for (int i = 0; i < units.Count; i++)
        {
            if (!units[i].Alive) continue;
            float r = units[i].Radius;
            if (r <= 0f) continue;
            Color c = (i == necroIdx) ? new Color(150, 255, 80, 240)
                    : (units[i].Faction == Faction.Undead) ? new Color(80, 220, 255, 200)
                    : new Color(255, 170, 60, 200);
            DrawCircle(batch, renderer, cam, units[i].Position, r, c);
        }
    }

    // ========== Drawing Primitives ==========

    public void EnsurePixel(GraphicsDevice device)
    {
        if (_pixel == null)
        {
            _pixel = new Texture2D(device, 1, 1);
            _pixel.SetData(new[] { Color.White });
        }
    }

    public void DrawCircle(SpriteBatch batch, Renderer renderer, Camera25D cam,
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

    public void DrawArrow(SpriteBatch batch, Vector2 start, Vector2 end, Color color)
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

    public void DrawLine(SpriteBatch batch, Vector2 start, Vector2 end, Color color)
    {
        if (_pixel == null) return;
        var diff = end - start;
        float length = diff.Length();
        if (length < 0.5f) return;
        float angle = MathF.Atan2(diff.Y, diff.X);
        batch.Draw(_pixel, start, null, color, angle, Vector2.Zero,
            new Vector2(length, 1f), SpriteEffects.None, 0f);
    }

    // ========== Chunk Overlay ==========

    private void DrawChunkOverlay(SpriteBatch batch, Simulation sim, Camera25D cam, Renderer renderer,
        Pathfinder? pathfinder)
    {
        if (pathfinder == null) return;
        int scx = pathfinder.SectorCountX;
        int scy = pathfinder.SectorCountY;
        int ss = Pathfinder.SectorSize;

        // Draw sector grid lines
        for (int sy = 0; sy <= scy; sy++)
        {
            var left = renderer.WorldToScreen(new Vec2(0, sy * ss), 0f, cam);
            var right = renderer.WorldToScreen(new Vec2(scx * ss, sy * ss), 0f, cam);
            DrawLine(batch, left, right, new Color(100, 100, 255, 60));
        }
        for (int sx = 0; sx <= scx; sx++)
        {
            var top = renderer.WorldToScreen(new Vec2(sx * ss, 0), 0f, cam);
            var bot = renderer.WorldToScreen(new Vec2(sx * ss, scy * ss), 0f, cam);
            DrawLine(batch, top, bot, new Color(100, 100, 255, 60));
        }

        // Draw sector labels
        if (_font != null && cam.Zoom >= 8f)
        {
            for (int sy = 0; sy < scy; sy++)
            {
                for (int sx = 0; sx < scx; sx++)
                {
                    var center = renderer.WorldToScreen(new Vec2((sx + 0.5f) * ss, (sy + 0.5f) * ss), 0f, cam);
                    if (center.X > -50 && center.X < renderer.ScreenW + 50 &&
                        center.Y > -50 && center.Y < renderer.ScreenH + 50)
                    {
                        string label = $"S({sx},{sy})";
                        batch.DrawString(_font, label, center - new Vector2(20, 6), new Color(120, 120, 255, 80));
                    }
                }
            }
        }

        // Draw imaginary chunks for each unit that has one (different color: orange/yellow)
        foreach (int unitIdx in pathfinder.GetActiveImaginaryChunkUnits())
        {
            if (unitIdx < 0 || unitIdx >= sim.Units.Count || !sim.Units[unitIdx].Alive) continue;

            var info = pathfinder.GetImaginaryChunkInfo(unitIdx);
            if (info == null) continue;
            var (baseX, baseY, w, h, active) = info.Value;

            // Draw imaginary chunk boundary as a thick orange rectangle
            var tl = renderer.WorldToScreen(new Vec2(baseX, baseY), 0f, cam);
            var tr = renderer.WorldToScreen(new Vec2(baseX + w, baseY), 0f, cam);
            var bl = renderer.WorldToScreen(new Vec2(baseX, baseY + h), 0f, cam);
            var br = renderer.WorldToScreen(new Vec2(baseX + w, baseY + h), 0f, cam);

            var chunkColor = new Color(255, 180, 40, 140); // orange for imaginary chunk

            // Top
            DrawLine(batch, tl, tr, chunkColor);
            DrawLine(batch, tl + new Vector2(0, 1), tr + new Vector2(0, 1), chunkColor);
            // Bottom
            DrawLine(batch, bl, br, chunkColor);
            DrawLine(batch, bl + new Vector2(0, -1), br + new Vector2(0, -1), chunkColor);
            // Left
            DrawLine(batch, tl, bl, chunkColor);
            DrawLine(batch, tl + new Vector2(1, 0), bl + new Vector2(1, 0), chunkColor);
            // Right
            DrawLine(batch, tr, br, chunkColor);
            DrawLine(batch, tr + new Vector2(-1, 0), br + new Vector2(-1, 0), chunkColor);

            // Label
            if (_font != null)
            {
                var labelPos = renderer.WorldToScreen(new Vec2(baseX + 1, baseY + 1), 0f, cam);
                batch.DrawString(_font, $"Imag #{unitIdx}", labelPos, new Color(255, 200, 50, 220));
            }
        }

        // Highlight which sector each unit is in (draw each sector at most once)
        var drawnSectors = new HashSet<int>();
        var units = sim.Units;
        for (int i = 0; i < units.Count; i++)
        {
            if (!units[i].Alive) continue;
            var pos = units[i].Position;
            int sx = (int)(pos.X / ss);
            int sy = (int)(pos.Y / ss);
            if (sx < 0 || sx >= scx || sy < 0 || sy >= scy) continue;

            int sectorKey = sy * scx + sx;
            if (!drawnSectors.Add(sectorKey)) continue; // already drawn

            bool isUndead = units[i].Faction == 0;
            var sectorTint = isUndead ? new Color(40, 120, 40, 25) : new Color(120, 40, 40, 25);
            var sp = renderer.WorldToScreen(new Vec2(sx * ss, sy * ss), 0f, cam);
            float tileW = ss * cam.Zoom;
            float tileH = ss * cam.Zoom * cam.YRatio;
            batch.Draw(_pixel, new Rectangle((int)sp.X, (int)sp.Y, (int)tileW, (int)tileH), sectorTint);
        }
    }

}
