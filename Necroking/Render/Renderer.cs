using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Necroking.Core;

namespace Necroking.Render;

public class Renderer
{
    private int _screenW, _screenH;

    public int ScreenW => _screenW;
    public int ScreenH => _screenH;

    public void Init(int screenWidth, int screenHeight)
    {
        _screenW = screenWidth;
        _screenH = screenHeight;
    }

    public void SetScreenSize(int w, int h)
    {
        _screenW = w;
        _screenH = h;
    }

    public void HandleCameraInput(Camera25D cam, InputState input, float dt)
    {
        float speed = cam.PanSpeed / cam.Zoom * 32f;

        if (input.IsKeyDown(Microsoft.Xna.Framework.Input.Keys.W) || input.IsKeyDown(Microsoft.Xna.Framework.Input.Keys.Up))
            cam.Position.Y -= speed * dt;
        if (input.IsKeyDown(Microsoft.Xna.Framework.Input.Keys.S) || input.IsKeyDown(Microsoft.Xna.Framework.Input.Keys.Down))
            cam.Position.Y += speed * dt;
        if (input.IsKeyDown(Microsoft.Xna.Framework.Input.Keys.A) || input.IsKeyDown(Microsoft.Xna.Framework.Input.Keys.Left))
            cam.Position.X -= speed * dt;
        if (input.IsKeyDown(Microsoft.Xna.Framework.Input.Keys.D) || input.IsKeyDown(Microsoft.Xna.Framework.Input.Keys.Right))
            cam.Position.X += speed * dt;

        if (!input.IsScrollConsumed && input.ScrollDelta != 0)
            cam.ZoomBy(input.ScrollDelta / 120f);
    }

    // World-unit height: scales with zoom. Use for jumps, projectile altitude, arc heights, Z.
    // Delegates to Camera25D (single home for the 2.5D projection) with our screen size.
    public Vector2 WorldToScreen(Vec2 worldPos, float worldHeight, Camera25D cam)
        => cam.WorldToScreen(worldPos, worldHeight, _screenW, _screenH);

    // Pixel-space height: literal screen pixels, zoom-independent.
    // Use for screen-space effects (rain streaks, lightning arcs) anchored to a world point.
    public Vector2 WorldToScreenPx(Vec2 worldPos, float pixelHeight, Camera25D cam)
        => cam.WorldToScreenPx(worldPos, pixelHeight, _screenW, _screenH);

    public Vec2 ScreenToWorld(Vector2 screenPos, Camera25D cam)
        => cam.ScreenToWorld(screenPos, _screenW, _screenH);

    /// <summary>
    /// Draw a sprite frame from an atlas at a world position.
    /// </summary>
    public void DrawSprite(SpriteScope batch, SpriteAtlas atlas, SpriteFrame frame,
                           Vec2 worldPos, float height, Camera25D cam,
                           float scale = 1f, bool flipX = false, Color? tint = null)
    {
        var tex = atlas.GetTextureForFrame(frame);
        if (tex == null) return;

        var screenPos = WorldToScreen(worldPos, height, cam);
        float pixelScale = scale * cam.Zoom / 32f; // normalized so zoom=32 → 1:1

        float drawW = frame.Rect.Width * pixelScale;
        float drawH = frame.Rect.Height * pixelScale;

        // Pivot: (0.5, 1.0) = bottom-center. The world position maps to this point on the sprite.
        // When flipped, mirror the X pivot around 0.5
        float pivotX = flipX ? (1f - frame.PivotX) : frame.PivotX;
        float pivotOffsetX = pivotX * drawW;
        float pivotOffsetY = frame.PivotY * drawH;

        var destRect = new Rectangle(
            (int)(screenPos.X - pivotOffsetX),
            (int)(screenPos.Y - pivotOffsetY),
            (int)drawW, (int)drawH);

        var sourceRect = frame.Rect;

        // Handle horizontal flip
        SpriteEffects effects = flipX ? SpriteEffects.FlipHorizontally : SpriteEffects.None;

        batch.Draw(tex, destRect, sourceRect, tint ?? Color.White,
                   0f, Vector2.Zero, effects, 0f);
    }

    /// <summary>
    /// Draw a flipbook frame at a world position.
    /// </summary>
    public void DrawFlipbookFrame(SpriteScope batch, Flipbook flipbook, int frameIndex,
                                   Vec2 worldPos, float height, Camera25D cam,
                                   float scale = 1f, Color? tint = null)
    {
        if (flipbook.Texture == null) return;

        var sourceRect = flipbook.GetFrameRect(frameIndex);
        var screenPos = WorldToScreen(worldPos, height, cam);
        float pixelScale = scale * cam.Zoom / 32f;

        float drawW = sourceRect.Width * pixelScale;
        float drawH = sourceRect.Height * pixelScale;

        var destRect = new Rectangle(
            (int)(screenPos.X - drawW * 0.5f),
            (int)(screenPos.Y - drawH * 0.5f),
            (int)drawW, (int)drawH);

        batch.Draw(flipbook.Texture, destRect, sourceRect, tint ?? Color.White);
    }
}
