using System;
using Necroking.Core;
using Microsoft.Xna.Framework;
using Necroking.Lib;

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

    // World-unit height: scales with zoom, same scale as Y position (foreshortened).
    // Use this for anything physical: unit jumps, projectile altitude, arc heights, corpse Z.
    public Vector2 WorldToScreen(Vec2 worldPos, float worldHeight, int screenW, int screenH)
    {
        float sx = (worldPos.X - Position.X) * Zoom + screenW * 0.5f;
        float sy = (worldPos.Y - Position.Y) * Zoom * YRatio + screenH * 0.5f - worldHeight * Zoom * YRatio;
        return new Vector2(sx, sy);
    }

    // Pixel-space height: literal screen pixels, no zoom applied by the projection.
    // Use this to anchor pixel-authored effects (rain streaks, lightning/drain shapes,
    // damage-number lift) to a world point. The effect itself should still couple its
    // pixel dimensions to zoom — softly via SoftZoomScale for screen-space-authored
    // effects, or linearly (Zoom / 32) for effects meant to track world size.
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

    // THE canonical softened zoom coupling for pixel-authored effects (rain streaks,
    // damage numbers): sqrt keeps them legible at the extremes — full linear coupling
    // would shrink them 8x at MinZoom / grow them 4x at MaxZoom; sqrt gives ~0.35x–2x
    // around a reference zoom where the effect was tuned. Don't hand-roll this curve
    // per system; world-tracking effects use plain linear (Zoom / refZoom) instead.
    public float SoftZoomScale(float refZoom) => MathF.Sqrt(Zoom / refZoom);
}
