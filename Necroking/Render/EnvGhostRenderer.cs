using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Necroking.World;

namespace Necroking.Render;

/// <summary>
/// Shared cursor-ghost preview for placing an environment object: the def's
/// sprite drawn semi-transparent at the would-be placement point, tinted
/// <c>validTint</c> when placement is allowed and red when it isn't, plus the
/// def's PlacementRadius circle. This owns the draw mechanics (animated-frame
/// slicing, pivot/scale math, tinting); callers own the data — which def,
/// which validity check, which valid-spot color. Used by BuildingMenuUI
/// (green) and the map editor Objects tab (grey).
/// </summary>
public static class EnvGhostRenderer
{
    /// <summary>~0.3 — the proven ghost transparency (straight alpha).</summary>
    public const byte GhostAlpha = 76;

    /// <summary>Player build menu valid-spot tint (green).</summary>
    public static readonly Color BuildValidTint = new(50, 200, 50, (int)GhostAlpha);
    /// <summary>Editor valid-spot tint ("greyed out").</summary>
    public static readonly Color EditorValidTint = new(140, 140, 140, (int)GhostAlpha);
    /// <summary>Can't-place tint (red), shared by every caller.</summary>
    public static readonly Color InvalidTint = new(200, 50, 50, (int)GhostAlpha);

    /// <summary>Draw the ghost with the def's pivot anchored at
    /// <paramref name="screenPos"/> — exactly where a real placement would land.
    /// <paramref name="canPlace"/> should be the same validity check the caller's
    /// click path uses so the color always matches the click outcome. Animated
    /// sheets preview frame 0; placeholder textures are never frame-sliced.
    /// Pass <paramref name="pixel"/> to also draw the PlacementRadius circle.</summary>
    public static void Draw(SpriteScope batch, EnvironmentSystem env, int defIdx,
        Vector2 screenPos, float cameraZoom, bool canPlace, Color validTint,
        Texture2D? pixel = null)
    {
        var tex = env.GetDefTexture(defIdx);
        if (tex == null) return;
        var def = env.GetDef(defIdx);

        Rectangle? srcRect = null;
        float frameW = tex.Width, frameH = tex.Height;
        if (def.IsAnimated && def.AnimTotalFrames > 1 && !env.IsUsingPlaceholder(defIdx))
        {
            var r = def.GetAnimFrameRect(tex.Width, tex.Height, 0);
            srcRect = r;
            frameW = r.Width;
            frameH = r.Height;
        }

        float worldH = def.SpriteWorldHeight * def.Scale;
        float scale = worldH * cameraZoom / frameH;
        var origin = new Vector2(def.PivotX * frameW, def.PivotY * frameH);
        Color tint = canPlace ? validTint : InvalidTint;
        batch.Draw(tex, screenPos, srcRect, tint, 0f, origin, scale, SpriteEffects.None, 0f);

        if (pixel != null && def.PlacementRadius > 0)
        {
            DrawUtils.DrawCircleOutline(batch, pixel, screenPos,
                def.PlacementRadius * cameraZoom,
                new Color(tint.R, tint.G, tint.B, (byte)40), 16);
        }
    }
}
