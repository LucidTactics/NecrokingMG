using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Content;
using Microsoft.Xna.Framework.Graphics;

namespace Necroking.Render;

/// <summary>
/// Shader-based UI rendering primitives. Replaces the stacked-SpriteBatch
/// approach in <see cref="UIGfx"/> with real HLSL/GLSL pixel shaders.
///
/// Each method ends and re-begins the active SpriteBatch so the caller does
/// not have to manage effect state. This is intentionally simple, not fast:
/// use these for panels and modals, not per-frame HUD where count matters.
///
/// Each shader is tested in isolation by a dedicated scenario before being
/// applied in real UI (see todos/css_rendering.md for the policy). When you
/// add a method here, add an isolated test scenario before using it.
/// </summary>
public class UIShaders
{
    // Effects (aliased to disambiguate from our Necroking.Render.Effect class)
    public Microsoft.Xna.Framework.Graphics.Effect? Gradient;
    public Microsoft.Xna.Framework.Graphics.Effect? RectShadow;
    public Microsoft.Xna.Framework.Graphics.Effect? CircleEffect;

    // Shared pixel texture for stretching quads.
    private readonly Texture2D _pixel;
    public Texture2D GetPixel() => _pixel;

    // Cached begin-params so we can resume the default batch state.
    private readonly BlendState _defaultBlend;
    private readonly SamplerState _defaultSampler;

    public UIShaders(GraphicsDevice device, Texture2D pixel,
        BlendState defaultBlend, SamplerState defaultSampler)
    {
        _pixel = pixel;
        _defaultBlend = defaultBlend;
        _defaultSampler = defaultSampler;
    }

    public void Load(ContentManager content)
    {
        try { Gradient = content.Load<Microsoft.Xna.Framework.Graphics.Effect>("UIGradient"); }
        catch (Exception ex) { Core.DebugLog.Log("startup", $"UIGradient load failed: {ex.Message}"); }
        try { RectShadow = content.Load<Microsoft.Xna.Framework.Graphics.Effect>("UIRectShadow"); }
        catch (Exception ex) { Core.DebugLog.Log("startup", $"UIRectShadow load failed: {ex.Message}"); }
        try { CircleEffect = content.Load<Microsoft.Xna.Framework.Graphics.Effect>("UICircleEffect"); }
        catch (Exception ex) { Core.DebugLog.Log("startup", $"UICircleEffect load failed: {ex.Message}"); }
    }

    /// <summary>
    /// Modes for <see cref="DrawGradientRect"/>. Matches the shader's Mode uniform.
    /// </summary>
    public enum GradientMode
    {
        VerticalLinear   = 0,
        HorizontalLinear = 1,
        Vertical3Stop    = 2,
        Radial           = 3,
    }

    /// <summary>
    /// Draws a vertical/horizontal linear gradient in `rect`. Ends and re-begins
    /// the batch around the effect pass.
    /// </summary>
    public void DrawVerticalGradient(SpriteBatch batch, Rectangle rect, Color top, Color bottom)
        => DrawGradientInternal(batch, rect, GradientMode.VerticalLinear,
            top, bottom, Color.Transparent, 0.5f, new Vector2(0.5f, 0.5f), 1f);

    public void DrawHorizontalGradient(SpriteBatch batch, Rectangle rect, Color left, Color right)
        => DrawGradientInternal(batch, rect, GradientMode.HorizontalLinear,
            left, right, Color.Transparent, 0.5f, new Vector2(0.5f, 0.5f), 1f);

    public void DrawVertical3StopGradient(SpriteBatch batch, Rectangle rect,
        Color top, Color mid, Color bottom, float midStop = 0.5f)
        => DrawGradientInternal(batch, rect, GradientMode.Vertical3Stop,
            top, mid, bottom, midStop, new Vector2(0.5f, 0.5f), 1f);

    /// <summary>
    /// Radial gradient. Center and radius are in UV space (0..1 across the rect).
    /// </summary>
    public void DrawRadialGradient(SpriteBatch batch, Rectangle rect,
        Color inner, Color outer, Vector2 centerUV, float radiusUV)
        => DrawGradientInternal(batch, rect, GradientMode.Radial,
            inner, outer, Color.Transparent, 0.5f, centerUV, radiusUV);

    /// <summary>
    /// Draws a soft drop shadow around an inner rect. `outerRect` should
    /// include `softness` padding on all sides of the content area so the
    /// shadow has room to fade. `fillColor` fills the inner rect (pass
    /// Color.Transparent to draw shadow only).
    /// </summary>
    public void DrawDropShadow(SpriteBatch batch, Rectangle outerRect,
        Rectangle innerRect, Color fillColor, Color shadowColor, float softness)
    {
        if (RectShadow == null) { batch.Draw(_pixel, innerRect, fillColor); return; }

        batch.End();

        RectShadow.Parameters["Mode"]?.SetValue(0f);
        RectShadow.Parameters["RectSize"]?.SetValue(new Vector2(outerRect.Width, outerRect.Height));
        RectShadow.Parameters["InnerOffset"]?.SetValue(new Vector2(
            innerRect.X - outerRect.X, innerRect.Y - outerRect.Y));
        RectShadow.Parameters["InnerSize"]?.SetValue(new Vector2(innerRect.Width, innerRect.Height));
        RectShadow.Parameters["Softness"]?.SetValue(softness);
        RectShadow.Parameters["FillColor"]?.SetValue(fillColor.ToVector4());
        RectShadow.Parameters["ShadowColor"]?.SetValue(shadowColor.ToVector4());

        batch.Begin(SpriteSortMode.Immediate, _defaultBlend, _defaultSampler,
            null, null, RectShadow);
        batch.Draw(_pixel, outerRect, Color.White);
        batch.End();

        batch.Begin(SpriteSortMode.Deferred, _defaultBlend, _defaultSampler, null, null);
    }

    /// <summary>
    /// Draws a soft inset shadow inside `rect`. `softness` controls how far
    /// inward the shadow fades. Inner area (beyond softness from any edge)
    /// is transparent -- the shadow is meant to overlay existing content.
    /// </summary>
    public void DrawInsetShadow(SpriteBatch batch, Rectangle rect,
        Color shadowColor, float softness)
    {
        if (RectShadow == null) return;

        batch.End();

        RectShadow.Parameters["Mode"]?.SetValue(1f);
        RectShadow.Parameters["RectSize"]?.SetValue(new Vector2(rect.Width, rect.Height));
        RectShadow.Parameters["InnerOffset"]?.SetValue(Vector2.Zero);
        RectShadow.Parameters["InnerSize"]?.SetValue(new Vector2(rect.Width, rect.Height));
        RectShadow.Parameters["Softness"]?.SetValue(softness);
        RectShadow.Parameters["FillColor"]?.SetValue(Color.Transparent.ToVector4());
        RectShadow.Parameters["ShadowColor"]?.SetValue(shadowColor.ToVector4());

        batch.Begin(SpriteSortMode.Immediate, _defaultBlend, _defaultSampler,
            null, null, RectShadow);
        batch.Draw(_pixel, rect, Color.White);
        batch.End();

        batch.Begin(SpriteSortMode.Deferred, _defaultBlend, _defaultSampler, null, null);
    }

    /// <summary>
    /// Draws an anti-aliased filled circle with an optional outer glow ring.
    /// `center` and `radius` are in screen pixels. `glowRadius` is the outer
    /// extent of the glow (set `>= radius + softness` to show glow; set equal
    /// to `radius` to disable glow). `fillTop`/`fillBottom` produce a vertical
    /// gradient inside the circle; pass the same color for solid fill.
    /// </summary>
    public void DrawCircle(SpriteBatch batch, Vector2 center, float radius,
        float glowRadius, Color fillTop, Color fillBottom, Color glowColor,
        float edgeAA = 1.5f)
    {
        if (CircleEffect == null)
        {
            // Fallback: coarse filled square
            batch.Draw(_pixel, new Rectangle(
                (int)(center.X - radius), (int)(center.Y - radius),
                (int)(radius * 2), (int)(radius * 2)), fillTop);
            return;
        }

        float outer = MathF.Max(glowRadius, radius);
        // Drawn quad bounds: covers circle + glow + a little AA slack.
        int pad = (int)MathF.Ceiling(outer) + 2;
        var quad = new Rectangle(
            (int)(center.X - pad), (int)(center.Y - pad),
            pad * 2, pad * 2);
        var localCenter = new Vector2(center.X - quad.X, center.Y - quad.Y);

        batch.End();

        CircleEffect.Parameters["RectSize"]?.SetValue(new Vector2(quad.Width, quad.Height));
        CircleEffect.Parameters["Center"]?.SetValue(localCenter);
        CircleEffect.Parameters["Radius"]?.SetValue(radius);
        CircleEffect.Parameters["GlowRadius"]?.SetValue(outer);
        CircleEffect.Parameters["EdgeAA"]?.SetValue(edgeAA);
        CircleEffect.Parameters["FillTopColor"]?.SetValue(fillTop.ToVector4());
        CircleEffect.Parameters["FillBottomColor"]?.SetValue(fillBottom.ToVector4());
        CircleEffect.Parameters["GlowColor"]?.SetValue(glowColor.ToVector4());

        batch.Begin(SpriteSortMode.Immediate, _defaultBlend, _defaultSampler,
            null, null, CircleEffect);
        batch.Draw(_pixel, quad, Color.White);
        batch.End();

        batch.Begin(SpriteSortMode.Deferred, _defaultBlend, _defaultSampler, null, null);
    }

    private void DrawGradientInternal(SpriteBatch batch, Rectangle rect,
        GradientMode mode, Color a, Color b, Color c, float midStop,
        Vector2 center, float radius)
    {
        if (Gradient == null)
        {
            // Fallback: flat fill with color A so callers don't crash if the
            // shader failed to load.
            batch.Draw(_pixel, rect, a);
            return;
        }

        batch.End();

        Gradient.Parameters["Mode"]?.SetValue((float)mode);
        Gradient.Parameters["ColorA"]?.SetValue(a.ToVector4());
        Gradient.Parameters["ColorB"]?.SetValue(b.ToVector4());
        Gradient.Parameters["ColorC"]?.SetValue(c.ToVector4());
        Gradient.Parameters["MidStop"]?.SetValue(midStop);
        Gradient.Parameters["Center"]?.SetValue(center);
        Gradient.Parameters["Radius"]?.SetValue(radius);

        batch.Begin(SpriteSortMode.Immediate, _defaultBlend, _defaultSampler,
            null, null, Gradient);
        batch.Draw(_pixel, rect, Color.White);
        batch.End();

        // Resume the caller's default batch state
        batch.Begin(SpriteSortMode.Deferred, _defaultBlend, _defaultSampler, null, null);
    }
}
