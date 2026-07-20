using System;
using System.IO;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Necroking.Editor;

/// <summary>
/// Shared preview drawing for flipbook/plain textures — used by the texture
/// file browser's right panel and the Flipbook Manager popup. A grid with
/// more than one frame animates; anything else draws the whole image.
/// </summary>
public static class FlipbookPreviewPanel
{
    /// <summary>Frame hold time for animated previews.</summary>
    public const int FrameMs = 130;

    /// <summary>Draw into <paramref name="area"/>: checkerboarded image (one
    /// animated frame when cols*rows &gt; 1) at the top, then dimension /
    /// grid / filename labels underneath. The animation clock is wall time
    /// (<paramref name="animStartMs"/> from Environment.TickCount64) — the
    /// editors often run with the game clock paused.</summary>
    public static void Draw(EditorBase ui, Rectangle area, Texture2D? tex,
        int cols, int rows, long animStartMs, string? fileName = null)
    {
        if (tex == null)
        {
            ui.DrawText("No preview", new Vector2(area.X + 8, area.Y + 8), EditorBase.TextDim);
            return;
        }

        bool isFlipbook = cols >= 1 && rows >= 1 && cols * rows > 1;
        int srcW = isFlipbook ? tex.Width / cols : tex.Width;
        int srcH = isFlipbook ? tex.Height / rows : tex.Height;
        if (srcW < 1 || srcH < 1) { srcW = tex.Width; srcH = tex.Height; isFlipbook = false; }
        Rectangle srcRect;
        if (isFlipbook)
        {
            int total = cols * rows;
            int frame = (int)((Environment.TickCount64 - animStartMs) / FrameMs) % total;
            srcRect = new Rectangle(frame % cols * srcW, frame / cols * srcH, srcW, srcH);
        }
        else
        {
            srcRect = new Rectangle(0, 0, srcW, srcH);
        }

        // Scale to fit, leaving room for the label lines under the image
        int labelH = isFlipbook ? 60 : 44;
        float scale = Math.Min((float)(area.Width - 16) / srcW, (float)(area.Height - labelH - 16) / srcH);
        scale = Math.Min(scale, 1f);
        int drawW = Math.Max(1, (int)(srcW * scale));
        int drawH = Math.Max(1, (int)(srcH * scale));
        int drawX = area.X + (area.Width - drawW) / 2;
        int drawY = area.Y + 8;

        // Checkerboard background for transparency
        for (int cy = drawY; cy < drawY + drawH; cy += 8)
            for (int cx = drawX; cx < drawX + drawW; cx += 8)
            {
                bool dark = ((cx - drawX) / 8 + (cy - drawY) / 8) % 2 == 0;
                ui.DrawRect(new Rectangle(cx, cy,
                    Math.Min(8, drawX + drawW - cx), Math.Min(8, drawY + drawH - cy)),
                    dark ? new Color(35, 35, 35) : new Color(55, 55, 55));
            }

        ui.Scope.Draw(tex, new Rectangle(drawX, drawY, drawW, drawH), srcRect, Color.White);

        int infoY = drawY + drawH + 4;
        ui.DrawText($"{tex.Width}x{tex.Height}", new Vector2(area.X + 8, infoY), EditorBase.TextDim);
        if (isFlipbook)
        {
            ui.DrawText($"Flipbook {cols}x{rows} ({cols * rows} frames)",
                new Vector2(area.X + 8, infoY + 16), EditorBase.AccentColor);
            infoY += 16;
        }
        if (!string.IsNullOrEmpty(fileName))
            ui.DrawText(fileName, new Vector2(area.X + 8, infoY + 16), EditorBase.TextBright);
    }
}
