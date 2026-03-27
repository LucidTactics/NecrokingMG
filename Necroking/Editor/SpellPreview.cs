using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Necroking.Core;
using Necroking.Data.Registries;

namespace Necroking.Editor;

/// <summary>
/// Self-contained spell preview renderer for the spell editor.
/// Renders into a RenderTarget2D showing a real-time preview of the selected spell.
/// </summary>
public class SpellPreview
{
    private const int PreviewWidth = 400;
    private const int PreviewHeight = 250;
    private const float ReplayDelay = 1.5f;
    private const float MaxProjectileAge = 10.0f;
    private const float ProjGravity = 13.89f;
    private const float DefaultSpeed = 28.29f;
    private const float PI = MathF.PI;
    private const float Deg2Rad = PI / 180.0f;

    // Scene layout (world units)
    private const float CasterX = -3.0f;
    private const float TargetX = 3.0f;
    // Camera: maps world to preview pixels
    private const float CameraZoom = 35.0f;
    private const float CameraYRatio = 0.5f;

    private GraphicsDevice _gd = null!;
    private SpriteBatch _sb = null!;
    private RenderTarget2D? _rt;
    private Texture2D _pixel = null!;
    private Texture2D _glowTex = null!;
    private bool _initialized;

    // Timing
    private float _elapsed;
    private float _replayTimer;
    private bool _playing;
    private bool _waitingReplay;
    private bool _dirty;

    // Cached spell
    private SpellDef? _cachedSpell;
    private string _currentSpellId = "";

    // Public
    public bool BloomEnabled { get; set; } = true;

    // Projectile state
    private struct PreviewProjectile
    {
        public Vector2 Position;
        public float Height;
        public Vector2 Velocity;
        public float VelocityZ;
        public float Age;
        public bool Alive;
        public Vector2 TargetPos;
        public float HomingStrength;
        public float SwirlFreq;
        public float SwirlAmplitude;
        public float SwirlPhase;
        public Vector2 BaseDirection;
        public Color ProjectileColor;
        public float Scale;
    }

    // Strike state
    private struct PreviewStrike
    {
        public Vector2 TargetPos;
        public float TelegraphTimer;
        public float TelegraphDuration;
        public float EffectTimer;
        public float EffectDuration;
        public bool Alive;
        public float AoeRadius;
        public Color CoreColor;
        public Color GlowColor;
        public float CoreWidth;
        public float GlowWidth;
        public bool IsGodRay;
        // God ray params
        public float GodRayEdgeSoftness;
        public float GodRayNoiseSpeed;
        public float GodRayNoiseStrength;
        public float GodRayNoiseScale;
    }

    // Zap state (unit-targeted strike)
    private struct PreviewZap
    {
        public Vector2 StartPos;
        public Vector2 EndPos;
        public float Timer;
        public float Duration;
        public bool Alive;
        public Color CoreColor;
        public Color GlowColor;
        public float CoreWidth;
        public float GlowWidth;
    }

    // Beam state
    private struct PreviewBeam
    {
        public Vector2 StartPos;
        public Vector2 EndPos;
        public float Elapsed;
        public float MaxDuration;
        public bool Alive;
        public Color CoreColor;
        public Color GlowColor;
        public float CoreWidth;
        public float GlowWidth;
    }

    // Drain state
    private struct PreviewDrain
    {
        public Vector2 SourcePos;
        public Vector2 DestPos;
        public float Elapsed;
        public float MaxDuration;
        public bool Alive;
        public int TendrilCount;
        public float ArcHeight;
        public float SwayHz;
        public float SwayAmplitude;
        public Color CoreColor;
        public Color GlowColor;
        public float CoreWidth;
        public float GlowWidth;
        public float PulseHz;
        public float PulseStrength;
    }

    // Buff/Summon effect state
    private struct PreviewEffect
    {
        public Vector2 Position;
        public float Timer;
        public float Duration;
        public bool Alive;
        public Color EffectColor;
        public float Scale;
        public bool IsExpanding; // for ring/burst effects
    }

    // Hit effect state
    private struct PreviewHitEffect
    {
        public Vector2 Position;
        public float Timer;
        public float Duration;
        public bool Alive;
        public Color EffectColor;
        public float Scale;
    }

    private readonly List<PreviewProjectile> _projectiles = new();
    private readonly List<PreviewStrike> _strikes = new();
    private readonly List<PreviewZap> _zaps = new();
    private readonly List<PreviewBeam> _beams = new();
    private readonly List<PreviewDrain> _drains = new();
    private readonly List<PreviewEffect> _effects = new();
    private readonly List<PreviewHitEffect> _hitEffects = new();

    private int _remainingProjectiles;
    private float _projectileTimer;
    private float _projectileDelay;

    private static readonly Random _rand = new();

    public int Width => PreviewWidth;
    public int Height => PreviewHeight;
    public bool IsInitialized => _initialized;

    public void Init(GraphicsDevice gd, Texture2D pixel)
    {
        _gd = gd;
        _pixel = pixel;
        _sb = new SpriteBatch(gd);
        _rt = new RenderTarget2D(gd, PreviewWidth, PreviewHeight, false,
            SurfaceFormat.Color, DepthFormat.None, 0, RenderTargetUsage.PreserveContents);

        // Create radial glow texture (64x64 with smooth quadratic falloff) — matches Game1's _glowTex
        _glowTex = new Texture2D(gd, 64, 64);
        var glowData = new Color[64 * 64];
        for (int gy = 0; gy < 64; gy++)
            for (int gx = 0; gx < 64; gx++)
            {
                float dx = (gx - 31.5f) / 31.5f;
                float dy = (gy - 31.5f) / 31.5f;
                float dist = MathF.Sqrt(dx * dx + dy * dy);
                float alpha = MathF.Max(0, 1f - dist);
                alpha *= alpha; // quadratic falloff for soft glow
                glowData[gy * 64 + gx] = new Color((byte)255, (byte)255, (byte)255, (byte)(alpha * 255));
            }
        _glowTex.SetData(glowData);

        _initialized = true;
    }

    public void Unload()
    {
        _rt?.Dispose();
        _rt = null;
        _glowTex?.Dispose();
        _sb?.Dispose();
        _initialized = false;
    }

    public void MarkDirty()
    {
        _dirty = true;
    }

    public void UpdateSpell(SpellDef spell)
    {
        if (_cachedSpell != spell)
        {
            _cachedSpell = spell;
            _currentSpellId = spell.Id;
            _dirty = true;
        }
    }

    public void TriggerSpell(SpellDef spell)
    {
        Reset();
        _cachedSpell = spell;
        _currentSpellId = spell.Id;
        _playing = true;

        switch (spell.Category)
        {
            case "Projectile":
                _remainingProjectiles = Math.Max(1, spell.Quantity);
                _projectileDelay = spell.ProjectileDelay;
                _projectileTimer = 0f;
                SpawnProjectile(spell, 0);
                _remainingProjectiles--;
                break;

            case "Buff":
            case "Debuff":
            {
                // RS07: Use the spell's CastFlipbook color if available instead of hardcoded green/purple
                Color effColor;
                if (spell.CastFlipbook != null)
                    effColor = spell.CastFlipbook.Color.ToScaledColor();
                else
                    effColor = spell.Category == "Buff"
                        ? new Color(80, 255, 120, 200)
                        : new Color(200, 80, 255, 200);
                _effects.Add(new PreviewEffect
                {
                    Position = new Vector2(CasterX, 0),
                    Timer = 0f,
                    Duration = 1.2f,
                    Alive = true,
                    EffectColor = effColor,
                    Scale = 1.0f,
                    IsExpanding = true,
                });
                break;
            }

            case "Summon":
            {
                // RS08: Use the spell's SummonFlipbook color if available instead of hardcoded blue
                Color summonColor;
                if (spell.SummonFlipbook != null)
                    summonColor = spell.SummonFlipbook.Color.ToScaledColor();
                else
                    summonColor = new Color(80, 180, 255, 200);
                _effects.Add(new PreviewEffect
                {
                    Position = new Vector2(TargetX, 0),
                    Timer = 0f,
                    Duration = 1.5f,
                    Alive = true,
                    EffectColor = summonColor,
                    Scale = 1.2f,
                    IsExpanding = true,
                });
                break;
            }

            case "Strike":
            {
                Color coreCol = spell.StrikeCoreColor.ToScaledColor();
                Color glowCol = spell.StrikeGlowColor.ToScaledColor();
                bool isGodRay = string.Equals(spell.StrikeVisualType, "GodRay", StringComparison.OrdinalIgnoreCase);

                if (spell.StrikeTargetUnit)
                {
                    // Zap from caster to target
                    _zaps.Add(new PreviewZap
                    {
                        StartPos = new Vector2(CasterX, 0),
                        EndPos = new Vector2(TargetX, 0),
                        Timer = 0f,
                        Duration = Math.Max(0.1f, spell.ZapDuration),
                        Alive = true,
                        CoreColor = coreCol,
                        GlowColor = glowCol,
                        CoreWidth = Math.Max(1f, spell.StrikeCoreWidth),
                        GlowWidth = Math.Max(2f, spell.StrikeGlowWidth),
                    });
                }
                else
                {
                    // Sky strike with telegraph
                    _strikes.Add(new PreviewStrike
                    {
                        TargetPos = Vector2.Zero,
                        TelegraphTimer = spell.TelegraphDuration,
                        TelegraphDuration = spell.TelegraphDuration,
                        EffectTimer = 0f,
                        EffectDuration = Math.Max(0.1f, spell.StrikeDuration),
                        Alive = true,
                        AoeRadius = spell.AoeRadius,
                        CoreColor = coreCol,
                        GlowColor = glowCol,
                        CoreWidth = Math.Max(1f, spell.StrikeCoreWidth),
                        GlowWidth = Math.Max(2f, spell.StrikeGlowWidth),
                        IsGodRay = isGodRay,
                        GodRayEdgeSoftness = spell.GodRayEdgeSoftness,
                        GodRayNoiseSpeed = spell.GodRayNoiseSpeed,
                        GodRayNoiseStrength = spell.GodRayNoiseStrength,
                        GodRayNoiseScale = spell.GodRayNoiseScale,
                    });
                }
                break;
            }

            case "Beam":
            {
                Color coreCol = spell.BeamCoreColor.ToScaledColor();
                Color glowCol = spell.BeamGlowColor.ToScaledColor();
                _beams.Add(new PreviewBeam
                {
                    StartPos = new Vector2(CasterX, 0),
                    EndPos = new Vector2(TargetX, 0),
                    Elapsed = 0f,
                    MaxDuration = spell.BeamMaxDuration > 0f
                        ? Math.Min(spell.BeamMaxDuration, 3f) : 2f,
                    Alive = true,
                    CoreColor = coreCol,
                    GlowColor = glowCol,
                    CoreWidth = Math.Max(1f, spell.BeamCoreWidth),
                    GlowWidth = Math.Max(2f, spell.BeamGlowWidth),
                });
                break;
            }

            case "Drain":
            {
                Color coreCol = spell.DrainCoreColor.ToScaledColor();
                Color glowCol = spell.DrainGlowColor.ToScaledColor();

                Vector2 src, dst;
                if (spell.DrainReversed)
                {
                    src = new Vector2(CasterX, 0);
                    dst = new Vector2(TargetX, 0);
                }
                else
                {
                    src = new Vector2(TargetX, 0);
                    dst = new Vector2(CasterX, 0);
                }

                _drains.Add(new PreviewDrain
                {
                    SourcePos = src,
                    DestPos = dst,
                    Elapsed = 0f,
                    MaxDuration = spell.DrainMaxDuration > 0f
                        ? Math.Min(spell.DrainMaxDuration, 3f) : 2f,
                    Alive = true,
                    TendrilCount = Math.Max(1, spell.DrainTendrilCount),
                    ArcHeight = spell.DrainArcHeight,
                    SwayHz = spell.DrainSwayHz,
                    SwayAmplitude = spell.DrainSwayAmplitude,
                    CoreColor = coreCol,
                    GlowColor = glowCol,
                    CoreWidth = Math.Max(1f, spell.DrainCoreWidth),
                    GlowWidth = Math.Max(2f, spell.DrainGlowWidth),
                    PulseHz = spell.DrainPulseHz,
                    PulseStrength = spell.DrainPulseStrength,
                });
                break;
            }
        }
    }

    public void Update(float dt)
    {
        if (!_initialized) return;

        if (_dirty)
        {
            _dirty = false;
            if (_cachedSpell != null)
                TriggerSpell(_cachedSpell);
            return;
        }

        if (!_playing && !_waitingReplay) return;

        if (_waitingReplay)
        {
            _replayTimer += dt;
            if (_replayTimer >= ReplayDelay && _cachedSpell != null)
                TriggerSpell(_cachedSpell);
            return;
        }

        _elapsed += dt;

        // Multi-projectile timer
        if (_remainingProjectiles > 0 && _cachedSpell != null)
        {
            _projectileTimer += dt;
            if (_projectileTimer >= _projectileDelay)
            {
                _projectileTimer -= _projectileDelay;
                int idx = _cachedSpell.Quantity - _remainingProjectiles;
                SpawnProjectile(_cachedSpell, idx);
                _remainingProjectiles--;
            }
        }

        UpdateProjectiles(dt);
        UpdateStrikes(dt);
        UpdateZaps(dt);
        UpdateBeams(dt);
        UpdateDrains(dt);
        UpdateEffects(dt);
        UpdateHitEffects(dt);

        if (!IsSceneActive())
        {
            _playing = false;
            _waitingReplay = true;
            _replayTimer = 0f;
        }
    }

    /// <summary>
    /// Renders the preview into the RenderTarget. Must be called OUTSIDE of a SpriteBatch pass.
    /// The caller is responsible for ending and re-beginning its SpriteBatch.
    /// </summary>
    public void RenderToTarget()
    {
        if (!_initialized || _rt == null) return;

        var prevTargets = _gd.GetRenderTargets();
        _gd.SetRenderTarget(_rt);
        _gd.Clear(new Color(18, 18, 28));

        _sb.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp);

        DrawGround();
        DrawMarkers();
        DrawProjectiles();
        DrawEffects();
        DrawHitEffects();

        _sb.End();

        // Additive blend pass for lightning, beams, drains, and glow effects
        _sb.Begin(SpriteSortMode.Deferred, BlendState.Additive, SamplerState.LinearClamp);

        DrawStrikes();
        DrawBeams();
        DrawDrains();
        DrawZaps();
        DrawProjectileGlows();
        DrawHitGlows();

        // Bloom pass: second additive overlay for extra glow
        if (BloomEnabled)
        {
            DrawStrikes();
            DrawBeams();
            DrawZaps();
            DrawDrains();
        }

        _sb.End();

        // Restore previous render target
        if (prevTargets.Length > 0 && prevTargets[0].RenderTarget != null)
            _gd.SetRenderTarget((RenderTarget2D)prevTargets[0].RenderTarget);
        else
            _gd.SetRenderTarget(null);
    }

    /// <summary>
    /// Returns the render target texture for blitting to screen.
    /// </summary>
    public Texture2D? GetTexture() => _rt;

    // ========================================
    // World-to-screen coordinate transform
    // ========================================
    private Vector2 WorldToScreen(float wx, float wy)
    {
        float sx = PreviewWidth * 0.5f + wx * CameraZoom;
        float sy = PreviewHeight * 0.5f + wy * CameraZoom * CameraYRatio;
        return new Vector2(sx, sy);
    }

    private Vector2 WorldToScreen(Vector2 worldPos) => WorldToScreen(worldPos.X, worldPos.Y);

    private Vector2 WorldToScreenWithHeight(float wx, float wy, float height)
    {
        var screen = WorldToScreen(wx, wy);
        screen.Y -= height * CameraZoom * 0.5f;
        return screen;
    }

    // ========================================
    // Drawing helpers
    // ========================================
    private void DrawGround()
    {
        // Draw a ground line
        int groundY = (int)(PreviewHeight * 0.5f + 0.8f * CameraZoom * CameraYRatio);
        var groundColor = new Color(40, 45, 55);
        _sb.Draw(_pixel, new Rectangle(0, groundY, PreviewWidth, 1), groundColor);

        // Ground gradient below
        for (int i = 0; i < 30; i++)
        {
            float a = 1f - i / 30f;
            var c = new Color(35, 38, 48) * (a * 0.3f);
            _sb.Draw(_pixel, new Rectangle(0, groundY + i, PreviewWidth, 1), c);
        }

        // Grid dots
        var gridColor = new Color(50, 55, 65, 60);
        for (float wx = -5f; wx <= 5f; wx += 1f)
        {
            var sp = WorldToScreen(wx, 0);
            _sb.Draw(_pixel, new Rectangle((int)sp.X, groundY, 1, 1), gridColor);
        }
    }

    private void DrawMarkers()
    {
        int groundY = (int)(PreviewHeight * 0.5f + 0.8f * CameraZoom * CameraYRatio);

        // Caster marker (blue diamond)
        var casterScreen = WorldToScreen(CasterX, 0);
        DrawDiamond(new Vector2(casterScreen.X, groundY - 10), 6, new Color(80, 140, 255, 200));
        // Label
        DrawSmallLabel("Caster", (int)casterScreen.X - 14, groundY + 4, new Color(80, 140, 255, 150));

        // Target marker (red diamond)
        var targetScreen = WorldToScreen(TargetX, 0);
        DrawDiamond(new Vector2(targetScreen.X, groundY - 10), 6, new Color(255, 80, 80, 200));
        DrawSmallLabel("Target", (int)targetScreen.X - 14, groundY + 4, new Color(255, 80, 80, 150));
    }

    private void DrawDiamond(Vector2 center, int size, Color color)
    {
        for (int i = 0; i <= size; i++)
        {
            int w = size - i;
            _sb.Draw(_pixel, new Rectangle((int)center.X - w, (int)center.Y - i, w * 2 + 1, 1), color);
            if (i > 0)
                _sb.Draw(_pixel, new Rectangle((int)center.X - w, (int)center.Y + i, w * 2 + 1, 1), color);
        }
    }

    private void DrawSmallLabel(string text, int x, int y, Color color)
    {
        // Use pixel-level text (very simple block characters not available -- just draw nothing,
        // the diamond markers are sufficient visual cues)
        // We only have SpriteBatch + pixel, no font access. Leave the label out
        // since the markers with diamonds are clear.
    }

    private void DrawProjectiles()
    {
        foreach (var p in _projectiles)
        {
            if (!p.Alive) continue;
            var screen = WorldToScreenWithHeight(p.Position.X, p.Position.Y, p.Height);
            float glowSize = 6f * CameraZoom / 32f * p.Scale;

            // Core bright dot (matches Game1 fallback glow dot)
            _sb.Draw(_pixel, screen, null, p.ProjectileColor,
                0f, new Vector2(0.5f, 0.5f), glowSize * 0.5f, SpriteEffects.None, 0f);

            // Trail segments (matches Game1's trail rendering)
            var trailDir = p.Velocity;
            if (trailDir.LengthSquared() > 0.01f)
            {
                trailDir = Vector2.Normalize(trailDir);
                float trailLen = 4f * CameraZoom / 32f;
                for (int t = 1; t <= 3; t++)
                {
                    var tp = new Vector2(p.Position.X - trailDir.X * t * 0.3f,
                                         p.Position.Y - trailDir.Y * t * 0.3f);
                    var ts = WorldToScreenWithHeight(tp.X, tp.Y, Math.Max(0, p.Height - p.VelocityZ * t * 0.02f));
                    byte alpha = (byte)(120 / t);
                    var trailColor = new Color(p.ProjectileColor.R, p.ProjectileColor.G, p.ProjectileColor.B, alpha);
                    _sb.Draw(_pixel, ts, null, trailColor,
                        0f, new Vector2(0.5f, 0.5f), trailLen / t, SpriteEffects.None, 0f);
                }
            }

            // Shadow on ground
            var shadowPos = WorldToScreen(p.Position.X, p.Position.Y);
            int groundY = (int)(PreviewHeight * 0.5f + 0.8f * CameraZoom * CameraYRatio);
            _sb.Draw(_pixel, new Rectangle((int)shadowPos.X - 2, groundY, 4, 1),
                new Color(0, 0, 0, 60));
        }
    }

    /// <summary>
    /// Draws radial glows for projectiles in the additive pass.
    /// </summary>
    private void DrawProjectileGlows()
    {
        foreach (var p in _projectiles)
        {
            if (!p.Alive) continue;
            var screen = WorldToScreenWithHeight(p.Position.X, p.Position.Y, p.Height);
            float glowSize = 8f * CameraZoom / 32f * p.Scale;

            // Radial glow using glow texture (additive blend)
            _sb.Draw(_glowTex, screen, null,
                new Color(p.ProjectileColor.R, p.ProjectileColor.G, p.ProjectileColor.B, (byte)160),
                p.Age * 2f, new Vector2(32f, 32f), glowSize / 32f, SpriteEffects.None, 0f);
        }
    }

    private void DrawStrikes()
    {
        foreach (var s in _strikes)
        {
            if (!s.Alive) continue;
            var screen = WorldToScreen(s.TargetPos);
            int groundY = (int)(PreviewHeight * 0.5f + 0.8f * CameraZoom * CameraYRatio);

            if (s.TelegraphTimer > 0)
            {
                // Telegraph: pulsing circle on ground (matches Game1)
                float pulse = 0.5f + 0.5f * MathF.Sin((_elapsed) * 20f);
                float radius = s.AoeRadius * CameraZoom * pulse;
                if (radius < 4f) radius = 4f;
                byte alpha = (byte)(100 * pulse);
                _sb.Draw(_pixel, new Vector2(screen.X, groundY), null,
                    new Color(s.GlowColor.R, s.GlowColor.G, s.GlowColor.B, alpha),
                    0f, new Vector2(0.5f, 0.5f), new Vector2(radius * 2, radius * CameraYRatio),
                    SpriteEffects.None, 0f);
            }
            else
            {
                float fade = 1f - s.EffectTimer / Math.Max(0.01f, s.EffectDuration);
                var groundPos = new Vector2(screen.X, groundY);

                if (s.IsGodRay)
                {
                    // God ray: vertical column from sky to ground
                    var skyPos = new Vector2(screen.X - PreviewWidth * 0.1f, groundY - PreviewHeight * 0.6f);
                    DrawGodRay(skyPos, groundPos, s.CoreColor, s.GlowColor,
                        s.CoreWidth, s.GlowWidth,
                        s.GodRayEdgeSoftness, s.GodRayNoiseSpeed, s.GodRayNoiseStrength, s.GodRayNoiseScale,
                        _elapsed, s.EffectTimer, s.EffectDuration);
                }
                else
                {
                    // Lightning bolt from sky to ground (matches Game1.DrawLightningBolt)
                    var skyPos = new Vector2(screen.X - 20f, 5f);
                    DrawLightningBolt(skyPos, groundPos,
                        s.CoreColor, s.GlowColor, s.CoreWidth, s.GlowWidth, fade);

                    // Impact glow on ground using radial gradient
                    float radius = Math.Max(8f, s.AoeRadius * CameraZoom);
                    float glowScale = radius / 32f;
                    byte coreAlpha = (byte)(255 * fade);
                    _sb.Draw(_glowTex, groundPos, null,
                        new Color(s.CoreColor.R, s.CoreColor.G, s.CoreColor.B, coreAlpha),
                        0f, new Vector2(32f, 32f), new Vector2(glowScale, glowScale * CameraYRatio * 0.5f),
                        SpriteEffects.None, 0f);
                }
            }
        }
    }

    private void DrawZaps()
    {
        foreach (var z in _zaps)
        {
            if (!z.Alive) continue;
            float fade = 1f - z.Timer / Math.Max(0.01f, z.Duration);

            var startScreen = WorldToScreenWithHeight(z.StartPos.X, z.StartPos.Y, 1.5f);
            var endScreen = WorldToScreenWithHeight(z.EndPos.X, z.EndPos.Y, 1.0f);

            DrawLightningBolt(startScreen, endScreen,
                z.CoreColor, z.GlowColor, z.CoreWidth, z.GlowWidth, fade);
        }
    }

    private void DrawBeams()
    {
        foreach (var b in _beams)
        {
            if (!b.Alive) continue;

            var startScreen = WorldToScreenWithHeight(b.StartPos.X, b.StartPos.Y, 1.5f);
            var endScreen = WorldToScreenWithHeight(b.EndPos.X, b.EndPos.Y, 1.0f);

            // Pulsing width to show continuous activity
            float pulse = 1f + 0.3f * MathF.Sin(_elapsed * 8f);

            DrawLightningBolt(startScreen, endScreen,
                b.CoreColor, b.GlowColor, b.CoreWidth * pulse, b.GlowWidth * pulse, 1f);
        }
    }

    private void DrawDrains()
    {
        foreach (var d in _drains)
        {
            if (!d.Alive) continue;

            var srcScreen = WorldToScreenWithHeight(d.SourcePos.X, d.SourcePos.Y, 1.0f);
            var dstScreen = WorldToScreenWithHeight(d.DestPos.X, d.DestPos.Y, 1.5f);

            float pulse = 1f + d.PulseStrength * MathF.Sin(_elapsed * d.PulseHz * 2f * PI);

            // Draw multiple tendrils with sway (matches Game1's drain rendering)
            for (int i = 0; i < d.TendrilCount; i++)
            {
                float offset = (i - d.TendrilCount / 2f) * 8f;
                float sway = MathF.Sin(d.Elapsed * 3f + i * 2f) * 6f;
                var swayStart = new Vector2(srcScreen.X + offset, srcScreen.Y);
                var swayEnd = new Vector2(dstScreen.X + sway, dstScreen.Y);

                DrawTendril(swayStart, swayEnd, d.CoreColor, d.GlowColor,
                    d.CoreWidth * pulse, d.GlowWidth * pulse, d.Elapsed);
            }
        }
    }

    private void DrawEffects()
    {
        foreach (var e in _effects)
        {
            if (!e.Alive) continue;
            float t = e.Timer / Math.Max(0.01f, e.Duration);
            var screen = WorldToScreenWithHeight(e.Position.X, e.Position.Y, 1.0f);

            if (e.IsExpanding)
            {
                // Expanding ring
                float radius = t * 30f * e.Scale;
                float fade = 1f - t;
                DrawCircleOutline(screen, (int)radius, e.EffectColor * fade, 2);

                // Inner glow
                float innerRadius = t * 15f * e.Scale;
                DrawCircleOutline(screen, (int)innerRadius, e.EffectColor * (fade * 0.5f), 1);

                // Sparkles
                for (int s = 0; s < 4; s++)
                {
                    float angle = s * PI * 0.5f + _elapsed * 3f;
                    float r = radius * 0.7f;
                    var sparkle = screen + new Vector2(MathF.Cos(angle) * r, MathF.Sin(angle) * r * 0.5f);
                    _sb.Draw(_pixel, new Rectangle((int)sparkle.X - 1, (int)sparkle.Y - 1, 3, 3),
                        e.EffectColor * (fade * 0.8f));
                }
            }
        }
    }

    private void DrawHitEffects()
    {
        foreach (var h in _hitEffects)
        {
            if (!h.Alive) continue;
            float t = h.Timer / Math.Max(0.01f, h.Duration);
            float fade = 1f - t;
            var screen = WorldToScreen(h.Position);
            int groundY = (int)(PreviewHeight * 0.5f + 0.8f * CameraZoom * CameraYRatio);
            screen.Y = groundY;

            // Expanding ring
            float radius = t * 15f * h.Scale;
            DrawCircleOutline(screen, (int)radius, h.EffectColor * fade, 2);

            // Core flash dot
            float flashSize = (1f - t * t) * 4f * h.Scale;
            _sb.Draw(_pixel, screen, null,
                h.EffectColor * (fade * 0.7f),
                0f, new Vector2(0.5f, 0.5f), flashSize, SpriteEffects.None, 0f);
        }
    }

    /// <summary>
    /// Draws radial glow for hit effects in the additive pass.
    /// </summary>
    private void DrawHitGlows()
    {
        foreach (var h in _hitEffects)
        {
            if (!h.Alive) continue;
            float t = h.Timer / Math.Max(0.01f, h.Duration);
            float fade = 1f - t;
            var screen = WorldToScreen(h.Position);
            int groundY = (int)(PreviewHeight * 0.5f + 0.8f * CameraZoom * CameraYRatio);
            screen.Y = groundY;

            // Radial glow burst
            float glowScale = (0.3f + t * 0.7f) * h.Scale * 0.6f;
            byte alpha = (byte)(200 * fade);
            _sb.Draw(_glowTex, screen, null,
                new Color(h.EffectColor.R, h.EffectColor.G, h.EffectColor.B, alpha),
                0f, new Vector2(32f, 32f), glowScale, SpriteEffects.None, 0f);
        }
    }

    // ========================================
    // Drawing primitives — ported from Game1.cs
    // ========================================

    /// <summary>
    /// Procedural jagged lightning bolt — matches Game1.DrawLightningBolt exactly.
    /// Uses time-seeded LCG for jitter so the bolt reshapes each frame.
    /// </summary>
    private void DrawLightningBolt(Vector2 start, Vector2 end,
        Color coreColor, Color glowColor, float coreWidth, float glowWidth, float fade)
    {
        var dir = end - start;
        float length = dir.Length();
        if (length < 1f) return;
        var norm = dir / length;
        var perp = new Vector2(-norm.Y, norm.X);

        int segments = Math.Max(4, (int)(length / 15f));
        var points = new Vector2[segments + 1];
        points[0] = start;
        points[segments] = end;

        // Generate jagged midpoints using LCG (same as Game1)
        uint seed = (uint)(start.X * 1000 + end.Y * 7 + _elapsed * 60);
        for (int i = 1; i < segments; i++)
        {
            float t = i / (float)segments;
            var basePos = Vector2.Lerp(start, end, t);
            seed = seed * 1103515245 + 12345;
            float displacement = ((seed % 1000) / 500f - 1f) * coreWidth * 8f * (1f - MathF.Abs(t - 0.5f) * 2f);
            points[i] = basePos + perp * displacement;
        }

        // Draw segments: glow (wider, dimmer) then core (narrow, bright)
        byte coreAlpha = (byte)(fade * 255);

        for (int i = 0; i < segments; i++)
        {
            var segDir = points[i + 1] - points[i];
            float segLen = segDir.Length();
            if (segLen < 0.5f) continue;
            float angle = MathF.Atan2(segDir.Y, segDir.X);

            // Glow (wider, dimmer)
            _sb.Draw(_pixel, points[i], null,
                new Color(glowColor.R, glowColor.G, glowColor.B, (byte)(coreAlpha * 0.4f)),
                angle, new Vector2(0, 0.5f), new Vector2(segLen, glowWidth * fade),
                SpriteEffects.None, 0f);

            // Core (narrow, bright)
            _sb.Draw(_pixel, points[i], null,
                new Color(coreColor.R, coreColor.G, coreColor.B, coreAlpha),
                angle, new Vector2(0, 0.5f), new Vector2(segLen, coreWidth * fade),
                SpriteEffects.None, 0f);
        }
    }

    /// <summary>
    /// God ray column rendering — ported from Game1.DrawGodRay.
    /// Draws a vertical beam of light from sky to ground with noise distortion.
    /// </summary>
    private void DrawGodRay(Vector2 sky, Vector2 ground,
        Color coreColor, Color glowColor, float coreWidth, float glowWidth,
        float edgeSoftness, float noiseSpeed, float noiseStrength, float noiseScale,
        float elapsed, float effectTimer, float effectDuration)
    {
        float shimmer = MathF.Sin(elapsed * 8f) * 0.15f + 0.85f;
        float baseAlpha = shimmer;

        if (effectDuration > 0f)
        {
            float remaining = effectDuration - effectTimer;
            if (remaining < 0.15f) baseAlpha *= MathF.Max(0f, remaining / 0.15f);
        }
        if (baseAlpha <= 0.001f) return;

        float cw = coreWidth;
        float gw = glowWidth;

        var mid = new Color(
            (byte)((coreColor.R + glowColor.R) / 2),
            (byte)((coreColor.G + glowColor.G) / 2),
            (byte)((coreColor.B + glowColor.B) / 2),
            (byte)((coreColor.A + glowColor.A) / 2));

        // 4 layers from outer glow to inner core
        float[] layerT = { 1f, 0.66f, 0.33f, 0f };
        Color[] layerColors = { glowColor, mid, coreColor, coreColor };
        float[] layerAlphas = { 0.12f, 0.25f, 0.45f, 0.75f };

        float softness = MathF.Max(0f, MathF.Min(1f, edgeSoftness));
        const int EdgeSublayers = 3;
        const int Slices = 20;

        for (int li = 0; li < 4; li++)
        {
            float w = cw + (gw - cw) * layerT[li];
            // Scale widths for preview size (preview is smaller than game viewport)
            float widthTop = 3f * w;
            float widthBottom = 18f * w;
            Color lc = layerColors[li];
            float lAlpha = layerAlphas[li];

            for (int sub = EdgeSublayers; sub >= 0; sub--)
            {
                float expand = sub > 0 ? softness * sub / EdgeSublayers : 0f;
                float subAlphaMul = sub > 0 ? (1f / (sub + 1)) * 0.5f : 1f;
                float wMul = 1f + expand;
                float layerA = baseAlpha * lAlpha * subAlphaMul;
                if (layerA <= 0.001f) continue;

                byte ca = (byte)(lc.A * MathF.Min(1f, layerA));

                for (int s = 0; s < Slices; s++)
                {
                    float t0 = s / (float)Slices;
                    float t1 = (s + 1) / (float)Slices;

                    float y0 = sky.Y + (ground.Y - sky.Y) * t0;
                    float y1 = sky.Y + (ground.Y - sky.Y) * t1;
                    float cx0 = sky.X + (ground.X - sky.X) * t0;

                    float hw0 = (widthTop + (widthBottom - widthTop) * t0) * wMul;

                    // Noise modulation on innermost sub-layer
                    float n = 1f;
                    if (noiseStrength > 0.001f && sub == 0)
                    {
                        float raw = GodRayNoise(t0 * 10f, cx0 * 0.01f, elapsed, noiseScale, noiseSpeed);
                        n = 1f - noiseStrength * 0.6f + noiseStrength * 0.6f * raw;
                    }

                    byte sliceA = (byte)(ca * n);
                    Color sliceColor = new(lc.R, lc.G, lc.B, sliceA);

                    float sliceH = y1 - y0;
                    if (sliceH < 0.5f) continue;

                    _sb.Draw(_pixel, new Vector2(cx0 - hw0, y0), null, sliceColor,
                        0f, Vector2.Zero, new Vector2(hw0 * 2, sliceH), SpriteEffects.None, 0f);
                }
            }

            // Ground aura ellipse
            float auraW = widthBottom * 1.1f;
            float auraH = widthBottom * 0.35f;
            float auraAlpha = baseAlpha * lAlpha * 0.4f;
            byte ga = (byte)(lc.A * MathF.Min(1f, auraAlpha));
            Color auraColor = new(lc.R, lc.G, lc.B, ga);

            _sb.Draw(_pixel, new Vector2(ground.X - auraW, ground.Y - auraH * 0.5f), null,
                auraColor, 0f, Vector2.Zero, new Vector2(auraW * 2, auraH), SpriteEffects.None, 0f);
        }
    }

    private static float GodRayNoise(float y, float x, float t, float scale, float speed)
    {
        float s1 = MathF.Sin(y * scale + t * speed * 2.1f + x * 0.3f);
        float s2 = MathF.Sin(y * scale * 1.7f - t * speed * 1.4f + x * 0.5f);
        float s3 = MathF.Sin(y * scale * 0.6f + t * speed * 0.8f - x * 0.2f);
        return (s1 * s2 + s3) * 0.5f + 0.5f;
    }

    /// <summary>
    /// Tendril drawing — ported from Game1.DrawTendril.
    /// Arc + wave displacement with inner core and outer glow.
    /// </summary>
    private void DrawTendril(Vector2 start, Vector2 end, Color coreColor, Color glowColor,
        float coreWidth, float glowWidth, float time)
    {
        var dir = end - start;
        float length = dir.Length();
        if (length < 1f) return;
        var norm = dir / length;
        var perp = new Vector2(-norm.Y, norm.X);

        int segments = Math.Max(3, (int)(length / 20f));
        var points = new Vector2[segments + 1];
        points[0] = start;
        points[segments] = end;

        for (int i = 1; i < segments; i++)
        {
            float t = i / (float)segments;
            var basePos = Vector2.Lerp(start, end, t);
            float arc = MathF.Sin(t * PI) * 20f;
            float wave = MathF.Sin(time * 4f + t * 8f) * 5f;
            points[i] = basePos + perp * (arc + wave);
        }

        for (int i = 0; i < segments; i++)
        {
            var segDir = points[i + 1] - points[i];
            float segLen = segDir.Length();
            if (segLen < 0.5f) continue;
            float angle = MathF.Atan2(segDir.Y, segDir.X);

            // Glow
            _sb.Draw(_pixel, points[i], null,
                new Color(glowColor.R, glowColor.G, glowColor.B, (byte)120),
                angle, new Vector2(0, 0.5f), new Vector2(segLen, glowWidth), SpriteEffects.None, 0f);
            // Core
            _sb.Draw(_pixel, points[i], null,
                new Color(coreColor.R, coreColor.G, coreColor.B, (byte)200),
                angle, new Vector2(0, 0.5f), new Vector2(segLen, coreWidth), SpriteEffects.None, 0f);
        }
    }

    private void DrawThickLine(Vector2 a, Vector2 b, float thickness, Color color)
    {
        var diff = b - a;
        float length = diff.Length();
        if (length < 0.5f) return;
        float angle = MathF.Atan2(diff.Y, diff.X);
        _sb.Draw(_pixel, a, null, color,
            angle, new Vector2(0, 0.5f), new Vector2(length, Math.Max(1f, thickness)),
            SpriteEffects.None, 0f);
    }

    private void DrawCircleOutline(Vector2 center, int radius, Color color, int thickness)
    {
        if (radius < 1) return;
        int segments = Math.Max(12, radius * 2);
        float angleStep = 2f * PI / segments;
        var prev = center + new Vector2(radius, 0);
        for (int i = 1; i <= segments; i++)
        {
            float angle = i * angleStep;
            var cur = center + new Vector2(MathF.Cos(angle) * radius, MathF.Sin(angle) * radius * CameraYRatio);
            DrawThickLine(prev, cur, thickness, color);
            prev = cur;
        }
    }

    // ========================================
    // Spawning
    // ========================================
    private void SpawnProjectile(SpellDef spell, int projIndex)
    {
        var p = new PreviewProjectile
        {
            Position = new Vector2(CasterX, 0),
            Height = 1.5f,
            Age = 0f,
            Alive = true,
            Scale = spell.ProjectileFlipbook?.Scale ?? 1f,
            HomingStrength = 0f,
            SwirlFreq = 0f,
            SwirlAmplitude = 0f,
            SwirlPhase = 0f,
        };

        // Color from projectile flipbook or category default
        if (spell.ProjectileFlipbook != null)
            p.ProjectileColor = spell.ProjectileFlipbook.Color.ToScaledColor();
        else
            p.ProjectileColor = new Color(255, 160, 80, 255);

        var diff = new Vector2(TargetX - CasterX, 0);
        float dist = diff.Length();
        if (dist < 0.1f) dist = 0.1f;
        var dir = diff / dist;
        p.BaseDirection = dir;

        float speed = spell.ProjectileSpeed;
        if (speed <= 0f) speed = DefaultSpeed;

        // Multi-projectile scatter
        if (spell.Quantity > 1)
        {
            float scatter = RandBipolar() * 0.15f;
            var lateral = new Vector2(-dir.Y, dir.X);
            dir = Vector2.Normalize(dir + lateral * scatter);
            p.BaseDirection = dir;
        }

        switch (spell.Trajectory)
        {
            case "Lob":
            {
                float sinTwoTheta = dist * ProjGravity / (speed * speed);
                sinTwoTheta = Math.Min(sinTwoTheta, 1f);
                float theta = 0.5f * MathF.Asin(sinTwoTheta);
                p.Velocity = dir * speed * MathF.Cos(theta);
                p.VelocityZ = speed * MathF.Sin(theta);
                break;
            }
            case "DirectFire":
            {
                float theta = 5f * Deg2Rad;
                p.Velocity = dir * speed * MathF.Cos(theta);
                p.VelocityZ = speed * MathF.Sin(theta);
                break;
            }
            case "Homing":
            {
                float theta = 5f * Deg2Rad;
                p.Velocity = dir * speed * MathF.Cos(theta);
                p.VelocityZ = speed * MathF.Sin(theta) * 0.5f;
                p.TargetPos = new Vector2(TargetX, 0);
                p.HomingStrength = 5f;
                break;
            }
            case "Swirly":
            {
                float theta = 5f * Deg2Rad;
                p.Velocity = dir * speed * MathF.Cos(theta);
                p.VelocityZ = speed * MathF.Sin(theta);
                p.SwirlFreq = 3f + RandUnit() * 5f;
                p.SwirlAmplitude = 0.5f + RandUnit() * 1.5f;
                p.SwirlPhase = RandUnit() * 2f * PI;
                break;
            }
            case "HomingSwirly":
            {
                float theta = 5f * Deg2Rad;
                p.Velocity = dir * speed * MathF.Cos(theta);
                p.VelocityZ = speed * MathF.Sin(theta) * 0.5f;
                p.TargetPos = new Vector2(TargetX, 0);
                p.HomingStrength = 5f;
                p.SwirlFreq = 3f + RandUnit() * 5f;
                p.SwirlAmplitude = 0.5f + RandUnit() * 1.5f;
                p.SwirlPhase = RandUnit() * 2f * PI;
                break;
            }
        }

        _projectiles.Add(p);
    }

    // ========================================
    // Updates
    // ========================================
    private void UpdateProjectiles(float dt)
    {
        for (int i = _projectiles.Count - 1; i >= 0; i--)
        {
            var p = _projectiles[i];
            if (!p.Alive) { _projectiles.RemoveAt(i); continue; }

            p.Position += p.Velocity * dt;
            p.Height += p.VelocityZ * dt;
            p.VelocityZ -= ProjGravity * dt;
            p.Age += dt;

            // Homing
            if (p.HomingStrength > 0f)
            {
                var toTarget = p.TargetPos - p.Position;
                float tdist = toTarget.Length();
                if (tdist > 0.5f)
                {
                    var desired = toTarget / tdist;
                    float speed = p.Velocity.Length();
                    if (speed > 0.01f)
                    {
                        var currentDir = p.Velocity / speed;
                        float turnAmount = p.HomingStrength * dt;
                        var newDir = Vector2.Normalize(currentDir + desired * turnAmount);
                        p.Velocity = newDir * speed;
                        p.BaseDirection = newDir;
                    }
                }
            }

            // Swirl
            if (p.SwirlFreq > 0f)
            {
                var perp = new Vector2(-p.BaseDirection.Y, p.BaseDirection.X);
                float prevSwirl = MathF.Sin(p.SwirlFreq * (p.Age - dt) * 2f * PI + p.SwirlPhase) * p.SwirlAmplitude;
                float currSwirl = MathF.Sin(p.SwirlFreq * p.Age * 2f * PI + p.SwirlPhase) * p.SwirlAmplitude;
                p.Position += perp * (currSwirl - prevSwirl);
            }

            // Ground hit
            if (p.Height <= 0f && p.VelocityZ < 0f)
            {
                p.Height = 0f;
                p.Alive = false;

                // Spawn hit effect
                Color hitColor = _cachedSpell?.HitEffectFlipbook != null
                    ? _cachedSpell.HitEffectFlipbook.Color.ToScaledColor()
                    : p.ProjectileColor;
                float hitScale = _cachedSpell?.HitEffectFlipbook?.Scale ?? 1f;
                _hitEffects.Add(new PreviewHitEffect
                {
                    Position = p.Position,
                    Timer = 0f,
                    Duration = 0.5f,
                    Alive = true,
                    EffectColor = hitColor,
                    Scale = hitScale,
                });
            }

            if (p.Age > MaxProjectileAge)
                p.Alive = false;

            _projectiles[i] = p;
        }

        // Remove dead
        for (int i = _projectiles.Count - 1; i >= 0; i--)
            if (!_projectiles[i].Alive) _projectiles.RemoveAt(i);
    }

    private void UpdateStrikes(float dt)
    {
        for (int i = _strikes.Count - 1; i >= 0; i--)
        {
            var s = _strikes[i];
            if (!s.Alive) { _strikes.RemoveAt(i); continue; }

            if (s.TelegraphTimer > 0)
            {
                s.TelegraphTimer -= dt;
                if (s.TelegraphTimer <= 0)
                    s.TelegraphTimer = 0;
            }
            else
            {
                s.EffectTimer += dt;
                if (s.EffectTimer >= s.EffectDuration)
                {
                    s.Alive = false;
                    // Spawn hit effect
                    _hitEffects.Add(new PreviewHitEffect
                    {
                        Position = s.TargetPos,
                        Timer = 0f,
                        Duration = 0.4f,
                        Alive = true,
                        EffectColor = s.CoreColor,
                        Scale = Math.Max(0.5f, s.AoeRadius * 0.1f + 0.5f),
                    });
                }
            }

            _strikes[i] = s;
        }
        for (int i = _strikes.Count - 1; i >= 0; i--)
            if (!_strikes[i].Alive) _strikes.RemoveAt(i);
    }

    private void UpdateZaps(float dt)
    {
        for (int i = _zaps.Count - 1; i >= 0; i--)
        {
            var z = _zaps[i];
            if (!z.Alive) { _zaps.RemoveAt(i); continue; }
            z.Timer += dt;
            if (z.Timer >= z.Duration)
                z.Alive = false;
            _zaps[i] = z;
        }
        for (int i = _zaps.Count - 1; i >= 0; i--)
            if (!_zaps[i].Alive) _zaps.RemoveAt(i);
    }

    private void UpdateBeams(float dt)
    {
        for (int i = _beams.Count - 1; i >= 0; i--)
        {
            var b = _beams[i];
            if (!b.Alive) { _beams.RemoveAt(i); continue; }
            b.Elapsed += dt;
            if (b.Elapsed >= b.MaxDuration)
                b.Alive = false;
            _beams[i] = b;
        }
        for (int i = _beams.Count - 1; i >= 0; i--)
            if (!_beams[i].Alive) _beams.RemoveAt(i);
    }

    private void UpdateDrains(float dt)
    {
        for (int i = _drains.Count - 1; i >= 0; i--)
        {
            var d = _drains[i];
            if (!d.Alive) { _drains.RemoveAt(i); continue; }
            d.Elapsed += dt;
            if (d.Elapsed >= d.MaxDuration)
                d.Alive = false;
            _drains[i] = d;
        }
        for (int i = _drains.Count - 1; i >= 0; i--)
            if (!_drains[i].Alive) _drains.RemoveAt(i);
    }

    private void UpdateEffects(float dt)
    {
        for (int i = _effects.Count - 1; i >= 0; i--)
        {
            var e = _effects[i];
            if (!e.Alive) { _effects.RemoveAt(i); continue; }
            e.Timer += dt;
            if (e.Timer >= e.Duration)
                e.Alive = false;
            _effects[i] = e;
        }
        for (int i = _effects.Count - 1; i >= 0; i--)
            if (!_effects[i].Alive) _effects.RemoveAt(i);
    }

    private void UpdateHitEffects(float dt)
    {
        for (int i = _hitEffects.Count - 1; i >= 0; i--)
        {
            var h = _hitEffects[i];
            if (!h.Alive) { _hitEffects.RemoveAt(i); continue; }
            h.Timer += dt;
            if (h.Timer >= h.Duration)
                h.Alive = false;
            _hitEffects[i] = h;
        }
        for (int i = _hitEffects.Count - 1; i >= 0; i--)
            if (!_hitEffects[i].Alive) _hitEffects.RemoveAt(i);
    }

    // ========================================
    // Scene state
    // ========================================
    private void Reset()
    {
        _elapsed = 0f;
        _replayTimer = 0f;
        _playing = false;
        _waitingReplay = false;
        _projectiles.Clear();
        _strikes.Clear();
        _zaps.Clear();
        _beams.Clear();
        _drains.Clear();
        _effects.Clear();
        _hitEffects.Clear();
        _remainingProjectiles = 0;
        _projectileTimer = 0f;
    }

    private bool IsSceneActive()
    {
        if (_projectiles.Count > 0) return true;
        if (_strikes.Count > 0) return true;
        if (_zaps.Count > 0) return true;
        if (_beams.Count > 0) return true;
        if (_drains.Count > 0) return true;
        if (_effects.Count > 0) return true;
        if (_hitEffects.Count > 0) return true;
        if (_remainingProjectiles > 0) return true;
        return false;
    }

    private static float RandBipolar() => (_rand.Next(2001) - 1000) / 1000f;
    private static float RandUnit() => _rand.Next(1001) / 1000f;
}
