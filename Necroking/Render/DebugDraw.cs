using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Necroking.Core;
using Necroking.GameSystems;
using Necroking.Movement;

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

public class DebugDraw
{
    public uint SelectedUnitID = GameConstants.InvalidUnit;

    private Texture2D? _pixel;

    public void Draw(SpriteBatch batch, GraphicsDevice device, Simulation sim, Camera25D cam,
                     Renderer renderer, DebugFlags flags, bool showUnitRadius = false)
    {
        if (_pixel == null)
        {
            _pixel = new Texture2D(device, 1, 1);
            _pixel.SetData(new[] { Color.White });
        }

        var units = sim.Units;

        if (showUnitRadius)
        {
            for (int i = 0; i < units.Count; i++)
            {
                if (!units.Alive[i]) continue;
                DrawCircle(batch, renderer, cam, units.Position[i], units.Radius[i],
                    units.Faction[i] == Data.Faction.Undead ? Color.Green : Color.Red);
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

    private void DrawCircle(SpriteBatch batch, Renderer renderer, Camera25D cam,
                            Vec2 worldCenter, float worldRadius, Color color)
    {
        if (_pixel == null) return;
        int segments = 24;
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
