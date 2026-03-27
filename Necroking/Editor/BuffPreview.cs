using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Necroking.Data.Registries;

namespace Necroking.Editor;

/// <summary>
/// Self-contained buff visual preview renderer for the buff manager popup.
/// Renders into a RenderTarget2D showing a unit silhouette with buff visual effects
/// (orbital orbs, ground aura, pulsing outline, unit tint) that update live.
/// </summary>
public class BuffPreview
{
    private const int PreviewWidth = 350;
    private const int PreviewHeight = 200;
    private const float PI = MathF.PI;
    private const float CameraZoom = 45.0f;
    private const float CameraYRatio = 0.5f;

    private GraphicsDevice _gd = null!;
    private SpriteBatch _sb = null!;
    private RenderTarget2D? _rt;
    private Texture2D _pixel = null!;
    private bool _initialized;

    // Timing
    private float _elapsed;
    private bool _dirty;

    // Cached buff
    private BuffDef? _cachedBuff;
    private int _previewStackCount = 1;

    // Orbital state
    private struct OrbState
    {
        public float PlanetAngle;
        public float MoonAngle;
    }
    private readonly List<OrbState> _orbs = new();

    // Lightning arc state (random jitter)
    private float _lightningTimer;
    private readonly List<Vector2[]> _lightningArcs = new();

    // Animation state
    private int _previewAnimIdx; // 0=Idle, 1=Walk, 2=Attack
    public static readonly string[] AnimOptions = { "Idle", "Walk", "Attack" };

    // Unit silhouette dimensions (pixels in preview space)
    private const float UnitWidth = 30;
    private const float UnitHeight = 56;

    public int Width => PreviewWidth;
    public int Height => PreviewHeight;
    public bool IsInitialized => _initialized;
    public bool BloomEnabled { get; set; } = true;
    public int PreviewAnimIndex { get => _previewAnimIdx; set => _previewAnimIdx = value; }
    public int PreviewStackCount
    {
        get => _previewStackCount;
        set
        {
            int clamped = Math.Clamp(value, 1, 10);
            if (clamped != _previewStackCount)
            {
                _previewStackCount = clamped;
                _dirty = true;
            }
        }
    }

    public void Init(GraphicsDevice gd, Texture2D pixel)
    {
        _gd = gd;
        _pixel = pixel;
        _sb = new SpriteBatch(gd);
        _rt = new RenderTarget2D(gd, PreviewWidth, PreviewHeight, false,
            SurfaceFormat.Color, DepthFormat.None, 0, RenderTargetUsage.PreserveContents);
        _initialized = true;
    }

    public void Unload()
    {
        _rt?.Dispose();
        _rt = null;
        _sb?.Dispose();
        _initialized = false;
    }

    public void SetBuff(BuffDef buff)
    {
        _cachedBuff = buff;
        _dirty = true;
    }

    public void MarkDirty()
    {
        _dirty = true;
    }

    public void Update(float dt)
    {
        if (!_initialized || _cachedBuff == null) return;

        var bd = _cachedBuff;

        // Sync orbital orbs
        if (_dirty || ShouldResyncOrbs(bd))
        {
            SyncOrbs(bd);
            _dirty = false;
        }

        // Update orbital angles
        if (bd.HasOrbital && bd.Orbital != null)
        {
            for (int i = 0; i < _orbs.Count; i++)
            {
                var orb = _orbs[i];
                orb.PlanetAngle += bd.Orbital.SunOrbitSpeed * dt;
                orb.MoonAngle += bd.Orbital.MoonOrbitSpeed * dt;
                _orbs[i] = orb;
            }
        }

        // Update lightning arcs
        if (bd.HasLightningAura && bd.LightningAura != null)
        {
            float jitterHz = bd.LightningAura.JitterHz;
            if (jitterHz > 0)
            {
                _lightningTimer += dt;
                float jitterInterval = 1.0f / jitterHz;
                if (_lightningTimer >= jitterInterval)
                {
                    _lightningTimer -= jitterInterval;
                    RegenerateLightningArcs(bd.LightningAura);
                }
            }
        }

        _elapsed += dt;
    }

    public void RenderToTarget()
    {
        if (!_initialized || _rt == null || _cachedBuff == null) return;

        var prevTargets = _gd.GetRenderTargets();
        _gd.SetRenderTarget(_rt);
        _gd.Clear(new Color(20, 20, 30));

        _sb.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp);

        DrawGround();
        DrawGroundAura();
        DrawImageBehind();
        DrawBehindEffect();
        DrawOrbitalsBehind();
        DrawLightningAura();
        DrawPulsingOutline();
        DrawUnit();
        DrawOrbitalsInFront();
        DrawFrontEffect();

        _sb.End();

        // Bloom-like additive pass for glow effects
        if (BloomEnabled)
        {
            _sb.Begin(SpriteSortMode.Deferred, BlendState.Additive, SamplerState.PointClamp);
            DrawGroundAura();
            DrawOrbitalsBehind();
            DrawOrbitalsInFront();
            DrawLightningAura();
            DrawPulsingOutline();
            DrawImageBehind();
            DrawBehindEffect();
            DrawFrontEffect();
            _sb.End();
        }

        // Restore previous render target
        if (prevTargets.Length > 0 && prevTargets[0].RenderTarget != null)
            _gd.SetRenderTarget((RenderTarget2D)prevTargets[0].RenderTarget);
        else
            _gd.SetRenderTarget(null);
    }

    public Texture2D? GetTexture() => _rt;

    // ========================================
    // Coordinate helpers
    // ========================================
    private Vector2 UnitScreenPos()
    {
        // Center of the preview, slightly above center for ground plane
        return new Vector2(PreviewWidth * 0.5f, PreviewHeight * 0.52f);
    }

    private Vector2 UnitFeetPos()
    {
        var center = UnitScreenPos();
        return new Vector2(center.X, center.Y + UnitHeight * 0.35f);
    }

    // ========================================
    // Ground
    // ========================================
    private void DrawGround()
    {
        var feet = UnitFeetPos();
        int groundY = (int)feet.Y;

        // Ground line
        _sb.Draw(_pixel, new Rectangle(0, groundY, PreviewWidth, 1), new Color(40, 45, 55));

        // Ground gradient
        for (int i = 0; i < 20; i++)
        {
            float a = 1f - i / 20f;
            var c = new Color(35, 38, 48) * (a * 0.3f);
            _sb.Draw(_pixel, new Rectangle(0, groundY + i, PreviewWidth, 1), c);
        }

        // Grid dots
        var gridColor = new Color(50, 55, 65, 60);
        for (int dx = -6; dx <= 6; dx++)
        {
            int gx = (int)(feet.X + dx * CameraZoom);
            if (gx >= 0 && gx < PreviewWidth)
                _sb.Draw(_pixel, new Rectangle(gx, groundY, 1, 1), gridColor);
        }
    }

    // ========================================
    // Unit silhouette
    // ========================================
    private void DrawUnit()
    {
        if (_cachedBuff == null) return;
        var center = UnitScreenPos();

        // Apply unit tint
        Color bodyColor = new Color(60, 70, 90);
        Color headColor = new Color(80, 90, 110);

        var tint = _cachedBuff.UnitTint;
        if (tint != null && tint.A > 0)
        {
            float tintStrength = tint.A / 255.0f;
            var tintColor = new Color(tint.R, tint.G, tint.B);
            bodyColor = Color.Lerp(bodyColor, tintColor, tintStrength);
            headColor = Color.Lerp(headColor, tintColor, tintStrength);
        }

        // Simple animation offset
        float animBob = 0;
        float animLean = 0;
        if (_previewAnimIdx == 1) // Walk
        {
            animBob = MathF.Sin(_elapsed * 6f) * 2f;
            animLean = MathF.Sin(_elapsed * 3f) * 1f;
        }
        else if (_previewAnimIdx == 2) // Attack
        {
            float t = (_elapsed % 1.5f) / 1.5f;
            if (t < 0.3f)
                animLean = t / 0.3f * 8f;
            else if (t < 0.5f)
                animLean = 8f - (t - 0.3f) / 0.2f * 8f;
        }

        float bx = center.X - UnitWidth * 0.5f + animLean;
        float by = center.Y - UnitHeight * 0.4f + animBob;

        // Body (torso)
        int bodyW = (int)(UnitWidth * 0.65f);
        int bodyH = (int)(UnitHeight * 0.5f);
        int bodyX = (int)(bx + (UnitWidth - bodyW) * 0.5f);
        int bodyY = (int)(by + UnitHeight * 0.2f);
        _sb.Draw(_pixel, new Rectangle(bodyX, bodyY, bodyW, bodyH), bodyColor);

        // Head (circle approximation)
        int headSize = (int)(UnitWidth * 0.45f);
        int headX = (int)(bx + (UnitWidth - headSize) * 0.5f);
        int headY = (int)(by);
        DrawFilledCircle(new Vector2(headX + headSize * 0.5f, headY + headSize * 0.5f), headSize * 0.5f, headColor);

        // Legs
        int legW = (int)(UnitWidth * 0.22f);
        int legH = (int)(UnitHeight * 0.3f);
        int legY = bodyY + bodyH;
        float legSpread = _previewAnimIdx == 1 ? MathF.Sin(_elapsed * 6f) * 3f : 0;
        _sb.Draw(_pixel, new Rectangle((int)(bodyX + 1 - legSpread), legY, legW, legH), bodyColor);
        _sb.Draw(_pixel, new Rectangle((int)(bodyX + bodyW - legW - 1 + legSpread), legY, legW, legH), bodyColor);

        // Arms
        int armW = (int)(UnitWidth * 0.15f);
        int armH = (int)(UnitHeight * 0.35f);
        float armSwing = _previewAnimIdx == 2 ?
            ((_elapsed % 1.5f) < 0.5f ? MathF.Sin((_elapsed % 1.5f) / 0.5f * PI) * 6f : 0) : 0;
        _sb.Draw(_pixel, new Rectangle((int)(bodyX - armW - 1), bodyY + 2 - (int)armSwing, armW, armH), bodyColor);
        _sb.Draw(_pixel, new Rectangle(bodyX + bodyW + 1, bodyY + 2 + (int)(armSwing * 0.5f), armW, armH), bodyColor);
    }

    // ========================================
    // Ground Aura
    // ========================================
    private void DrawGroundAura()
    {
        if (_cachedBuff == null || !_cachedBuff.HasGroundAura || _cachedBuff.GroundAura == null) return;
        var ga = _cachedBuff.GroundAura;

        var feet = UnitFeetPos();
        var color = ga.Color.ToScaledColor();

        float pulse = 1.0f;
        if (ga.PulseSpeed > 0 && ga.PulseAmount > 0)
            pulse = 1.0f + MathF.Sin(_elapsed * ga.PulseSpeed * 2 * PI) * ga.PulseAmount;

        float radius = ga.Scale * CameraZoom * 0.5f * pulse;
        float yRadius = radius * CameraYRatio;

        // Draw filled ellipse for ground aura
        DrawFilledEllipse(feet, radius, yRadius, color * 0.4f);
        DrawEllipseOutline(feet, radius, yRadius, color * 0.8f, 2);
    }

    // ========================================
    // Orbital Orbs
    // ========================================
    private void DrawOrbitalsBehind()
    {
        DrawOrbitals(behind: true);
    }

    private void DrawOrbitalsInFront()
    {
        DrawOrbitals(behind: false);
    }

    private void DrawOrbitals(bool behind)
    {
        if (_cachedBuff == null || !_cachedBuff.HasOrbital || _cachedBuff.Orbital == null) return;
        var orb = _cachedBuff.Orbital;
        var unitPos = UnitScreenPos();

        for (int i = 0; i < _orbs.Count; i++)
        {
            var os = _orbs[i];

            // Planet position (sun orbit)
            float sunX = MathF.Cos(os.PlanetAngle) * orb.SunOrbitRadius * CameraZoom;
            float sunY = MathF.Sin(os.PlanetAngle) * orb.SunOrbitRadius * CameraZoom * CameraYRatio;

            // Determine if orb is behind unit (Y-based depth)
            bool isBehind = MathF.Sin(os.PlanetAngle) > 0;
            if (isBehind != behind) continue;

            // Moon offset (secondary orbit around planet position)
            float moonX = MathF.Cos(os.MoonAngle) * orb.MoonOrbitRadius * CameraZoom;
            float moonY = MathF.Sin(os.MoonAngle) * orb.MoonOrbitRadius * CameraZoom * CameraYRatio;

            float orbX = unitPos.X + sunX + moonX;
            float orbY = unitPos.Y + sunY + moonY;

            var color = orb.OrbColor.ToScaledColor();
            float radius = Math.Max(3, orb.OrbScale * 6);

            // Glow
            DrawFilledCircle(new Vector2(orbX, orbY), radius * 1.8f, color * 0.25f);
            // Core
            DrawFilledCircle(new Vector2(orbX, orbY), radius, color);
            // Bright center
            DrawFilledCircle(new Vector2(orbX, orbY), radius * 0.4f, Color.Lerp(color, Color.White, 0.6f));
        }
    }

    // ========================================
    // Pulsing Outline
    // ========================================
    private void DrawPulsingOutline()
    {
        if (_cachedBuff == null || !_cachedBuff.HasPulsingOutline || _cachedBuff.PulsingOutline == null) return;
        var po = _cachedBuff.PulsingOutline;

        var center = UnitScreenPos();
        var color = po.Color.ToScaledColor();
        var pulseColor = po.PulseColor.ToScaledColor();

        float pulse = MathF.Sin(_elapsed * po.PulseSpeed * 2 * PI) * 0.5f + 0.5f;
        var blendedColor = Color.Lerp(color, pulseColor, pulse);

        float width = po.OutlineWidth + pulse * po.PulseWidth;

        // Draw outline around unit shape
        float hw = UnitWidth * 0.5f + width;
        float hh = UnitHeight * 0.5f + width;
        float bx = center.X - hw;
        float by = center.Y - hh * 0.65f;
        float w = hw * 2;
        float h = hh * 1.65f;

        int thick = Math.Max(1, (int)width);
        // Top
        _sb.Draw(_pixel, new Rectangle((int)bx, (int)by, (int)w, thick), blendedColor * 0.7f);
        // Bottom
        _sb.Draw(_pixel, new Rectangle((int)bx, (int)(by + h - thick), (int)w, thick), blendedColor * 0.7f);
        // Left
        _sb.Draw(_pixel, new Rectangle((int)bx, (int)by, thick, (int)h), blendedColor * 0.7f);
        // Right
        _sb.Draw(_pixel, new Rectangle((int)(bx + w - thick), (int)by, thick, (int)h), blendedColor * 0.7f);
    }

    // ========================================
    // Image Behind (scaled silhouette glow)
    // ========================================
    private void DrawImageBehind()
    {
        if (_cachedBuff == null || !_cachedBuff.HasImageBehind || _cachedBuff.ImageBehind == null) return;
        var ib = _cachedBuff.ImageBehind;

        var center = UnitScreenPos();
        var color = ib.Color.ToScaledColor();

        float pulse = 1.0f;
        if (ib.PulseSpeed > 0 && ib.PulseAmount > 0)
            pulse = 1.0f + MathF.Sin(_elapsed * ib.PulseSpeed * 2 * PI) * ib.PulseAmount;

        float scale = ib.Scale * pulse;
        float hw = UnitWidth * 0.5f * scale;
        float hh = UnitHeight * 0.5f * scale;

        // Draw a glowing rectangle behind the unit
        DrawFilledCircle(center, Math.Max(hw, hh), color * 0.15f);
        _sb.Draw(_pixel,
            new Rectangle((int)(center.X - hw), (int)(center.Y - hh * 0.65f), (int)(hw * 2), (int)(hh * 1.65f)),
            color * 0.2f);
    }

    // ========================================
    // Behind Effect (upright flipbook indicator)
    // ========================================
    private void DrawBehindEffect()
    {
        if (_cachedBuff == null || !_cachedBuff.HasBehindEffect || _cachedBuff.BehindEffect == null) return;
        DrawUprightEffect(_cachedBuff.BehindEffect, -0.15f);
    }

    // ========================================
    // Front Effect (upright flipbook indicator)
    // ========================================
    private void DrawFrontEffect()
    {
        if (_cachedBuff == null || !_cachedBuff.HasFrontEffect || _cachedBuff.FrontEffect == null) return;
        DrawUprightEffect(_cachedBuff.FrontEffect, 0.15f);
    }

    private void DrawUprightEffect(UprightEffectVisual ue, float yOffsetScale)
    {
        var center = UnitScreenPos();
        var color = ue.Color.ToScaledColor();

        float effectScale = ue.Scale;
        float yOff = ue.YOffset * CameraZoom * CameraYRatio + yOffsetScale * CameraZoom;

        // Draw a pulsing diamond/star shape as effect indicator
        float radius = effectScale * CameraZoom * 0.3f;
        float pulse = MathF.Sin(_elapsed * 3f) * 0.15f + 1.0f;
        radius *= pulse;

        var pos = new Vector2(center.X, center.Y + yOff);
        DrawFilledCircle(pos, radius, color * 0.3f);
        DrawCircleOutline(pos, radius, color * 0.6f, 1);
    }

    // ========================================
    // Lightning Aura
    // ========================================
    private void DrawLightningAura()
    {
        if (_cachedBuff == null || !_cachedBuff.HasLightningAura || _cachedBuff.LightningAura == null) return;
        var la = _cachedBuff.LightningAura;

        var center = UnitScreenPos();
        var coreColor = la.CoreColor.ToScaledColor();
        var glowColor = la.GlowColor.ToScaledColor();

        // Flicker
        float flicker = 1.0f;
        if (la.FlickerHz > 0)
            flicker = (MathF.Sin(_elapsed * la.FlickerHz * 2 * PI) > 0) ? 1.0f : 0.3f;

        float arcRadius = la.ArcRadius * CameraZoom;

        // Draw lightning arcs
        for (int a = 0; a < _lightningArcs.Count; a++)
        {
            var points = _lightningArcs[a];
            if (points.Length < 2) continue;

            for (int i = 0; i < points.Length - 1; i++)
            {
                var p1 = center + points[i] * arcRadius;
                var p2 = center + points[i + 1] * arcRadius;

                // Glow
                DrawThickLine(p1, p2, la.GlowWidth, glowColor * (0.5f * flicker));
                // Core
                DrawThickLine(p1, p2, la.CoreWidth, coreColor * flicker);
            }
        }
    }

    // ========================================
    // Orbital sync
    // ========================================
    private bool ShouldResyncOrbs(BuffDef bd)
    {
        if (!bd.HasOrbital) return _orbs.Count > 0;
        if (bd.Orbital == null) return _orbs.Count > 0;

        int expected = bd.Orbital.OrbCountMatchesStacks
            ? _previewStackCount
            : bd.Orbital.OrbCount;
        expected = Math.Max(0, expected);
        return _orbs.Count != expected;
    }

    private void SyncOrbs(BuffDef bd)
    {
        if (!bd.HasOrbital || bd.Orbital == null)
        {
            _orbs.Clear();
            return;
        }

        int count = bd.Orbital.OrbCountMatchesStacks
            ? _previewStackCount
            : bd.Orbital.OrbCount;
        count = Math.Max(0, count);

        _orbs.Clear();
        for (int i = 0; i < count; i++)
        {
            _orbs.Add(new OrbState
            {
                PlanetAngle = (2.0f * PI * i) / Math.Max(count, 1),
                MoonAngle = (2.0f * PI * i) / Math.Max(count, 1) * 0.7f
            });
        }

        // Also regenerate lightning arcs if needed
        if (bd.HasLightningAura && bd.LightningAura != null)
            RegenerateLightningArcs(bd.LightningAura);
    }

    private static readonly Random _rand = new();

    private void RegenerateLightningArcs(LightningAuraVisual la)
    {
        _lightningArcs.Clear();
        for (int a = 0; a < la.ArcCount; a++)
        {
            // Random arc: start from unit, zigzag outward
            float startAngle = (float)(_rand.NextDouble() * 2 * PI);
            int segments = 4 + _rand.Next(4);
            var points = new Vector2[segments];
            float curAngle = startAngle;
            for (int i = 0; i < segments; i++)
            {
                float t = i / (float)(segments - 1);
                float r = t;
                curAngle += (float)(_rand.NextDouble() - 0.5) * 1.2f;
                points[i] = new Vector2(MathF.Cos(curAngle) * r, MathF.Sin(curAngle) * r * CameraYRatio);
            }
            _lightningArcs.Add(points);
        }
    }

    // ========================================
    // Drawing primitives
    // ========================================
    private void DrawFilledCircle(Vector2 center, float radius, Color color)
    {
        if (radius < 1) return;
        int r = (int)radius;
        for (int dy = -r; dy <= r; dy++)
        {
            float hw = MathF.Sqrt(r * r - dy * dy);
            int x = (int)(center.X - hw);
            int w = (int)(hw * 2);
            if (w > 0)
                _sb.Draw(_pixel, new Rectangle(x, (int)center.Y + dy, w, 1), color);
        }
    }

    private void DrawFilledEllipse(Vector2 center, float rx, float ry, Color color)
    {
        if (rx < 1 || ry < 1) return;
        int iRy = (int)ry;
        for (int dy = -iRy; dy <= iRy; dy++)
        {
            float t = dy / ry;
            float hw = rx * MathF.Sqrt(1 - t * t);
            int x = (int)(center.X - hw);
            int w = (int)(hw * 2);
            if (w > 0)
                _sb.Draw(_pixel, new Rectangle(x, (int)center.Y + dy, w, 1), color);
        }
    }

    private void DrawEllipseOutline(Vector2 center, float rx, float ry, Color color, int thickness)
    {
        int segments = Math.Max(16, (int)(rx + ry));
        float angleStep = 2f * PI / segments;
        var prev = center + new Vector2(rx, 0);
        for (int i = 1; i <= segments; i++)
        {
            float angle = i * angleStep;
            var cur = center + new Vector2(MathF.Cos(angle) * rx, MathF.Sin(angle) * ry);
            DrawThickLine(prev, cur, thickness, color);
            prev = cur;
        }
    }

    private void DrawCircleOutline(Vector2 center, float radius, Color color, int thickness)
    {
        if (radius < 1) return;
        int segments = Math.Max(12, (int)(radius * 2));
        float angleStep = 2f * PI / segments;
        var prev = center + new Vector2(radius, 0);
        for (int i = 1; i <= segments; i++)
        {
            float angle = i * angleStep;
            var cur = center + new Vector2(MathF.Cos(angle) * radius, MathF.Sin(angle) * radius);
            DrawThickLine(prev, cur, thickness, color);
            prev = cur;
        }
    }

    private void DrawThickLine(Vector2 a, Vector2 b, float thickness, Color color)
    {
        var diff = b - a;
        float length = diff.Length();
        if (length < 0.5f) return;
        float angle = MathF.Atan2(diff.Y, diff.X);
        _sb.Draw(_pixel,
            new Rectangle((int)a.X, (int)a.Y, (int)length, Math.Max(1, (int)thickness)),
            null, color, angle, new Vector2(0, 0.5f), SpriteEffects.None, 0f);
    }
}
