using Necroking.Core;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;

namespace Necroking.Render;

public class Camera25D
{
    public Vec2 Position = Vec2.Zero;
    public float Zoom = 32.0f;         // pixels per world unit (X axis)
    public float YRatio = 0.5f;        // Y foreshortening (0.5 = isometric)

    public float PanSpeed = 20.0f;
    public float ZoomSpeed = 0.1f;
    public float MinZoom = 4.0f;
    public float MaxZoom = 128.0f;

    public void HandleInput(InputState input, float dt)
    {
        float speed = PanSpeed / Zoom * 32.0f;

        if (input.IsKeyDown(Keys.W) || input.IsKeyDown(Keys.Up)) Position.Y -= speed * dt;
        if (input.IsKeyDown(Keys.S) || input.IsKeyDown(Keys.Down)) Position.Y += speed * dt;
        if (input.IsKeyDown(Keys.A) || input.IsKeyDown(Keys.Left)) Position.X -= speed * dt;
        if (input.IsKeyDown(Keys.D) || input.IsKeyDown(Keys.Right)) Position.X += speed * dt;

        if (!input.IsScrollConsumed && input.ScrollDelta != 0)
            ZoomBy(input.ScrollDelta / 120f);
    }

    // World-unit height: scales with zoom, same scale as Y position (foreshortened).
    // Use this for anything physical: unit jumps, projectile altitude, arc heights, corpse Z.
    public Vector2 WorldToScreen(Vec2 worldPos, float worldHeight, int screenW, int screenH)
    {
        float sx = (worldPos.X - Position.X) * Zoom + screenW * 0.5f;
        float sy = (worldPos.Y - Position.Y) * Zoom * YRatio + screenH * 0.5f - worldHeight * Zoom * YRatio;
        return new Vector2(sx, sy);
    }

    // Pixel-space height: literal screen pixels, zoom-independent.
    // Use this for screen-space effects that should look the same at every zoom:
    // rain streaks, lightning arc shapes, screen-space overlays anchored to world points.
    public Vector2 WorldToScreenPx(Vec2 worldPos, float pixelHeight, int screenW, int screenH)
    {
        float sx = (worldPos.X - Position.X) * Zoom + screenW * 0.5f;
        float sy = (worldPos.Y - Position.Y) * Zoom * YRatio + screenH * 0.5f - pixelHeight;
        return new Vector2(sx, sy);
    }

    public Vec2 ScreenToWorld(Vector2 screenPos, int screenW, int screenH)
    {
        float wx = (screenPos.X - screenW * 0.5f) / Zoom + Position.X;
        float wy = (screenPos.Y - screenH * 0.5f) / (Zoom * YRatio) + Position.Y;
        return new Vec2(wx, wy);
    }

    public void ZoomBy(float delta)
    {
        Zoom *= (1.0f + delta * ZoomSpeed);
        Zoom = MathUtil.Clamp(Zoom, MinZoom, MaxZoom);
    }
}
