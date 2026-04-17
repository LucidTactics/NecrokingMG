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
    public Vector2 WorldToScreen(Vec2 worldPos, float worldHeight, Camera25D cam)
    {
        float sx = (worldPos.X - cam.Position.X) * cam.Zoom + _screenW * 0.5f;
        float sy = (worldPos.Y - cam.Position.Y) * cam.Zoom * cam.YRatio + _screenH * 0.5f - worldHeight * cam.Zoom * cam.YRatio;
        return new Vector2(sx, sy);
    }

    // Pixel-space height: literal screen pixels, zoom-independent.
    // Use for screen-space effects (rain streaks, lightning arcs) anchored to a world point.
    public Vector2 WorldToScreenPx(Vec2 worldPos, float pixelHeight, Camera25D cam)
    {
        float sx = (worldPos.X - cam.Position.X) * cam.Zoom + _screenW * 0.5f;
        float sy = (worldPos.Y - cam.Position.Y) * cam.Zoom * cam.YRatio + _screenH * 0.5f - pixelHeight;
        return new Vector2(sx, sy);
    }

    public Vec2 ScreenToWorld(Vector2 screenPos, Camera25D cam)
    {
        float wx = (screenPos.X - _screenW * 0.5f) / cam.Zoom + cam.Position.X;
        float wy = (screenPos.Y - _screenH * 0.5f) / (cam.Zoom * cam.YRatio) + cam.Position.Y;
        return new Vec2(wx, wy);
    }

    /// <summary>
    /// Draw a sprite frame from an atlas at a world position.
    /// </summary>
    public void DrawSprite(SpriteBatch batch, SpriteAtlas atlas, SpriteFrame frame,
                           Vec2 worldPos, float height, Camera25D cam,
                           float scale = 1f, bool flipX = false, Color? tint = null)
    {
        if (atlas.Texture == null) return;

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

        batch.Draw(atlas.Texture, destRect, sourceRect, tint ?? Color.White,
                   0f, Vector2.Zero, effects, 0f);
    }

    /// <summary>
    /// Draw a flipbook frame at a world position.
    /// </summary>
    public void DrawFlipbookFrame(SpriteBatch batch, Flipbook flipbook, int frameIndex,
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
