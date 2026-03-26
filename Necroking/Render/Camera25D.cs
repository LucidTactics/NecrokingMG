using System;
using Necroking.Core;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;

namespace Necroking.Render;

public class Camera25D
{
    public Vec2 Position = Vec2.Zero;
    public float Zoom = 32.0f;         // pixels per world unit
    public float HeightScale = 16.0f;  // pixels per height unit
    public float YRatio = 0.5f;        // Y foreshortening (0.5 = isometric)

    public float PanSpeed = 20.0f;
    public float ZoomSpeed = 0.1f;
    public float MinZoom = 4.0f;
    public float MaxZoom = 128.0f;

    public void HandleInput(float dt, int screenW, int screenH)
    {
        var kb = Keyboard.GetState();
        float speed = PanSpeed / Zoom * 32.0f;

        if (kb.IsKeyDown(Keys.W) || kb.IsKeyDown(Keys.Up)) Position.Y -= speed * dt;
        if (kb.IsKeyDown(Keys.S) || kb.IsKeyDown(Keys.Down)) Position.Y += speed * dt;
        if (kb.IsKeyDown(Keys.A) || kb.IsKeyDown(Keys.Left)) Position.X -= speed * dt;
        if (kb.IsKeyDown(Keys.D) || kb.IsKeyDown(Keys.Right)) Position.X += speed * dt;

        var mouse = Mouse.GetState();
        int wheel = mouse.ScrollWheelValue;
        // MonoGame scroll is cumulative, need delta tracking
    }

    public Vector2 WorldToScreen(Vec2 worldPos, float height, int screenW, int screenH)
    {
        float sx = (worldPos.X - Position.X) * Zoom + screenW * 0.5f;
        float sy = (worldPos.Y - Position.Y) * Zoom * YRatio + screenH * 0.5f - height * HeightScale;
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
