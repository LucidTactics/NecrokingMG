using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Necroking.Core;
using Necroking.Data.Registries;
using Necroking.Lib;

namespace Necroking.Render;

/// <summary>
/// The view a spell-VFX draw call runs against — built once per frame by the
/// game (real camera, fog gate, its ScatterGlow) and by the spell editor's
/// preview (fixed camera into an RT, no fog, its own ScatterGlow instance).
/// Cache the WorldToScreen delegate on the owner; don't rebuild closures per
/// frame.
/// </summary>
public sealed class VfxView
{
    public SpriteScope Scope = null!;
    public GraphicsDevice Device = null!;
    public Dictionary<string, Flipbook> Flipbooks = null!;
    public Texture2D GlowTex = null!;
    public float Zoom;
    /// <summary>(worldPos, worldHeight) -> screen px.</summary>
    public Func<Vec2, float, Vector2> WorldToScreen = null!;
    /// <summary>Fog gate; null = everything visible (preview).</summary>
    public Func<Vec2, bool>? Visible;
    /// <summary>Scatter registration target; null = none.</summary>
    public ScatterGlowSystem? Scatter;
}

/// <summary>
/// Spell-effect drawing shared VERBATIM by the game renderer and the spell
/// editor's preview — the one place the "how does an effect look" math lives
/// (docs/locate-behavior/render.md; extracted from GameRenderer.World so the
/// preview can't drift).
/// </summary>
public static class SpellVfxDraw
{
    /// <summary>Draw all of an EffectManager's effects matching the given
    /// blend mode (0 = alpha, 1 = additive). Registers scatter halos while
    /// drawing (each effect matches exactly one blendMode, so scatter
    /// registers once per frame despite two filtered calls).</summary>
    public static void DrawEffects(VfxView v, IReadOnlyList<Effect> effects, int blendMode)
    {
        foreach (var eff in effects)
        {
            if (!eff.Alive || eff.BlendMode != blendMode) continue;
            // Fog of war: enemy/trap strikes and clouds land in fogged
            // territory too — their hit effects must not flash through.
            if (v.Visible != null && !v.Visible(eff.Position)) continue;
            // Max guard: a data Duration of 0 gives Lifetime 0, and this draw
            // can run before the first Update culls it — 0/0 NaN otherwise.
            float t = eff.Age / MathF.Max(eff.Lifetime, 0.001f);
            float alpha = eff.AlphaCurve.Evaluate(t);

            // ScatterGlow: impact explosions light the air, breathing with the
            // effect's alpha envelope.
            if (eff.ScatterRadius > 0f)
                v.Scatter?.AddPoint(eff.Position, eff.ScatterRadius, eff.ScatterRgb,
                    eff.ScatterStrength * alpha);
            // ScaleCurve is WORLD units — the world→px multiply happens once below.
            // (A stray extra *Zoom/32 here made every impact flipbook scale ∝ Zoom²:
            // right at 32, 2x too big at 64, 4x too small at 8. Round-2 sweep find.)
            float scale = eff.ScaleCurve.Evaluate(t);

            var sp = v.WorldToScreen(eff.Position, 0f);

            // Try flipbook
            if (!string.IsNullOrEmpty(eff.FlipbookKey) && v.Flipbooks.TryGetValue(eff.FlipbookKey, out var fb) && fb.IsLoaded)
            {
                // Loop cycles frames (FPS override honored); one-shots map the
                // clip once onto the lifetime so it always completes exactly.
                float fps = eff.FpsOverride > 0f ? eff.FpsOverride : fb.FPS;
                int frameIdx = eff.LoopFrames
                    ? (fb.TotalFrames > 0 ? (int)(eff.Age * fps) % fb.TotalFrames : 0)
                    : fb.GetFrameAtNormalizedTime(eff.Age / MathF.Max(eff.Lifetime, 0.001f));
                var srcRect = fb.GetFrameRect(frameIdx);
                var origin = new Vector2(srcRect.Width * eff.AnchorX, srcRect.Height * eff.AnchorY);
                // Scale relative to world size
                float worldSize = scale * 2f; // scale curve gives world units
                float pixelSize = worldSize * v.Zoom;
                float fbScale = pixelSize / srcRect.Width;
                Color color = blendMode == 0
                    ? HdrColor.ToHdrVertexAlpha(eff.Tint, alpha, eff.HdrIntensity)
                    : HdrColor.ToHdrVertex(eff.Tint, alpha, eff.HdrIntensity);
                // HDR (EXR) sheets need an override material (temperature ramp
                // or plain linear-texture variant)
                var hdrMat = Materials.SelectHdrFlipbookMaterial(v.Device, fb,
                    additive: blendMode == 1, eff.TempRamp);
                if (hdrMat != null) v.Scope.PushMaterial(hdrMat);
                v.Scope.Draw(fb.Texture, sp, srcRect, color, 0f, origin, fbScale, SpriteEffects.None, 0f);
                if (hdrMat != null) v.Scope.PopMaterial();
            }
            else
            {
                // Fallback glow (radial gradient circle)
                float glowAlpha = alpha * (200f / 255f);
                Color color = blendMode == 0
                    ? HdrColor.ToHdrVertexAlpha(eff.Tint, glowAlpha, eff.HdrIntensity)
                    : HdrColor.ToHdrVertex(eff.Tint, glowAlpha, eff.HdrIntensity);
                float glowSize = scale * v.Zoom * 0.5f / 32f;
                v.Scope.Draw(v.GlowTex, sp, null, color,
                    0f, new Vector2(32f, 32f), glowSize, SpriteEffects.None, 0f);
            }
        }
    }

    /// <summary>Draw one FlipbookRef at a world position with elapsed-time
    /// playback — the channel-beam hit effect (stateless per-frame draw:
    /// position tracking, looping, and kill-on-end fall out for free). Loop
    /// cycles the clip; off = play once and hold the last frame. Skips when
    /// the ref's blend mode doesn't match the active pass.</summary>
    public static void DrawFlipbookRefLoop(VfxView v, FlipbookRef fbRef, Vec2 worldPos,
        float worldHeight, float elapsed, int blendMode)
    {
        if (string.IsNullOrEmpty(fbRef.FlipbookID)) return;
        if ((fbRef.BlendMode == "Additive" ? 1 : 0) != blendMode) return;
        if (!v.Flipbooks.TryGetValue(fbRef.FlipbookID, out var fb) || !fb.IsLoaded) return;
        if (v.Visible != null && !v.Visible(worldPos)) return;

        // ScatterGlow halo while the loop plays (lit air around the channel hit
        // point). Registered here — after the blend-pass and fog gates — so it
        // fires exactly once per frame (a ref matches only one blend pass).
        if (fbRef.ScatterRadius > 0f)
            v.Scatter?.AddPoint(worldPos, fbRef.ScatterRadius,
                new Color(fbRef.Color.R, fbRef.Color.G, fbRef.Color.B), fbRef.ScatterStrength);

        float fps = fbRef.FPS > 0f ? fbRef.FPS : fb.FPS;
        int frameIdx = fbRef.Loop
            ? (fb.TotalFrames > 0 && fps > 0f ? (int)(elapsed * fps) % fb.TotalFrames : 0)
            : fb.GetFrameAtNormalizedTime(fps > 0f && fb.TotalFrames > 0
                ? elapsed * fps / fb.TotalFrames : 1f);
        var srcRect = fb.GetFrameRect(frameIdx);

        var sp = v.WorldToScreen(worldPos, worldHeight);
        var origin = new Vector2(srcRect.Width * 0.5f, srcRect.Height * 0.5f);
        // Same world-size convention as DrawEffects (scale × 2 world units)
        float fbScale = fbRef.Scale * 2f * v.Zoom / srcRect.Width;
        Color color = blendMode == 0
            ? HdrColor.ToHdrVertexAlpha(fbRef.Color.ToColor(), 1f, fbRef.Color.Intensity)
            : HdrColor.ToHdrVertex(fbRef.Color.ToColor(), 1f, fbRef.Color.Intensity);

        var hdrMat = Materials.SelectHdrFlipbookMaterial(v.Device, fb,
            additive: blendMode == 1, fbRef.TemperatureRamp);
        if (hdrMat != null) v.Scope.PushMaterial(hdrMat);
        v.Scope.Draw(fb.Texture, sp, srcRect, color, 0f, origin, fbScale, SpriteEffects.None, 0f);
        if (hdrMat != null) v.Scope.PopMaterial();
    }
}
