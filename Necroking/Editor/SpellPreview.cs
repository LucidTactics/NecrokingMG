using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Necroking.Core;
using Necroking.Data.Registries;
using Necroking.GameSystems;
using Necroking.Render;

namespace Necroking.Editor;

/// <summary>
/// Self-contained spell preview renderer for the spell editor.
/// Renders into a RenderTarget2D showing a real-time preview of the selected spell.
/// </summary>
public class SpellPreview
{
    private int _previewWidth = 400;
    private int _previewHeight = 250;
    private const float ReplayDelay = 1.5f;
    private const float MaxProjectileAge = 10.0f;
    // Physics constants are the game's single source of truth — reference the shared
    // ProjectileManager values so a retune can't leave the editor preview lying.
    private const float ProjGravity = ProjectileManager.Gravity;
    private const float DefaultSpeed = ProjectileManager.MagicSpeed;
    private const float PI = MathF.PI;

    // Scene layout (world units)
    private const float CasterX = -3.0f;
    private const float TargetX = 3.0f;
    // Camera: maps world to preview pixels
    private const float CameraZoom = 35.0f;
    private const float CameraYRatio = 0.5f;

    private GraphicsDevice _gd = null!;
    private SpriteBatch _sb = null!;
    private Render.SpriteScope Scope => _sb;  // straight-alpha draw surface (implicit conversion)
    private RenderTarget2D? _rt;
    private Texture2D _pixel = null!;
    private Texture2D _glowTex = null!;
    private Microsoft.Xna.Framework.Graphics.Effect? _hdrSpriteEffect;
    private Dictionary<string, Flipbook>? _flipbooks;
    private BloomRenderer? _bloom;
    private Microsoft.Xna.Framework.Content.ContentManager? _content;
    private bool _initialized;
    // Bolts/tendrils and god rays use the SAME renderers as the game
    // (LightningRenderer.Add*StripsStatic + GodRayRenderer), collected during the
    // additive pass and flushed as triangle passes before bloom — so the preview
    // cannot drift from the in-game look.
    private readonly Render.HdrStripBatch _strips = new();
    private GodRayRenderer? _godRays;

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
        public HdrColor ProjectileColor;
        public float Scale;
        public string FlipbookID;
        public float GravityScale;
    }

    // Strike state
    private struct PreviewStrike
    {
        public Vector2 TargetPos;
        public float TelegraphTimer;
        public float TelegraphDuration;
        public bool TelegraphVisible;
        public float EffectTimer;
        public float EffectDuration;
        public bool Alive;
        public float AoeRadius;
        public LightningStyle Style;
        public bool IsGodRay;
        public GodRayParams? GodRay;
    }

    // Zap state (unit-targeted strike)
    private struct PreviewZap
    {
        public Vector2 StartPos;
        public Vector2 EndPos;
        public float Timer;
        public float Duration;
        public bool Alive;
        public LightningStyle Style;
    }

    // Beam state
    private struct PreviewBeam
    {
        public Vector2 StartPos;
        public Vector2 EndPos;
        public float Elapsed;
        public float MaxDuration;
        public bool Alive;
        public LightningStyle Style;
    }

    // Drain state
    private struct PreviewDrain
    {
        public Vector2 SourcePos;
        public Vector2 DestPos;
        public float Elapsed;
        public float MaxDuration;
        public bool Alive;
        public DrainVisualParams Visuals;
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

    public int Width => _previewWidth;
    public int Height => _previewHeight;

    /// <summary>Resize the preview render target if dimensions changed.</summary>
    public void Resize(int w, int h)
    {
        if (w == _previewWidth && h == _previewHeight) return;
        if (w < 100 || h < 60) return;
        _previewWidth = w;
        _previewHeight = h;
        if (_gd != null && _initialized)
        {
            _rt?.Dispose();
            SurfaceFormat rtFormat = SurfaceFormat.Color;
            try { using var test = new RenderTarget2D(_gd, 4, 4, false, SurfaceFormat.HalfVector4, DepthFormat.None); rtFormat = SurfaceFormat.HalfVector4; }
            catch { }
            _rt = new RenderTarget2D(_gd, w, h, false, rtFormat, DepthFormat.None, 0, RenderTargetUsage.PreserveContents);
            // Reinit bloom at new dimensions if we have content manager access
            // (bloom creates its own internal RTs sized to the scene)
            _bloom?.Unload();
            if (_content != null)
            {
                _bloom = new BloomRenderer();
                _bloom.Init(_gd, _content, w, h);
            }
            else
                _bloom = null;
        }
    }
    public bool IsInitialized => _initialized;

    public void Init(GraphicsDevice gd, Texture2D pixel,
        Microsoft.Xna.Framework.Graphics.Effect? hdrSpriteEffect = null,
        Dictionary<string, Flipbook>? flipbooks = null,
        Microsoft.Xna.Framework.Content.ContentManager? content = null)
    {
        _gd = gd;
        _pixel = pixel;
        _hdrSpriteEffect = hdrSpriteEffect;
        _flipbooks = flipbooks;
        _sb = new SpriteBatch(gd);

        // Initialize mini-bloom for preview (uses same shaders as main bloom)
        _content = content;
        if (content != null)
        {
            _bloom = new BloomRenderer();
            _bloom.Init(gd, content, _previewWidth, _previewHeight);
        }

        // Try HDR format for proper bloom preview, fallback to LDR
        SurfaceFormat rtFormat = SurfaceFormat.Color;
        try
        {
            using var test = new RenderTarget2D(gd, 4, 4, false, SurfaceFormat.HalfVector4, DepthFormat.None);
            rtFormat = SurfaceFormat.HalfVector4;
            test.Dispose();
        }
        catch { }
        _rt = new RenderTarget2D(gd, _previewWidth, _previewHeight, false,
            rtFormat, DepthFormat.None, 0, RenderTargetUsage.PreserveContents);

        // Shared radial glow texture (cached in TextureUtil — matches Game1's _glowTex).
        _glowTex = TextureUtil.GetRadialGlow(gd);

        // HdrIntensity.fx drives the ribbon/god-ray triangle passes. ContentManager
        // caches loads, so this is the same Effect instance the game uses (params
        // are set per draw). Null content → HdrStripBatch/GodRayRenderer fall back
        // to BasicEffect (LDR, no bloom pickup) rather than failing.
        Microsoft.Xna.Framework.Graphics.Effect? hdrIntensity = null;
        if (content != null)
        {
            try { hdrIntensity = content.Load<Microsoft.Xna.Framework.Graphics.Effect>("HdrIntensity"); }
            catch { }
        }
        _strips.Init(gd, hdrIntensity);
        _godRays = new GodRayRenderer();
        _godRays.Init(gd, hdrIntensity);

        _initialized = true;
    }

    public void Unload()
    {
        _rt?.Dispose();
        _rt = null;
        // _glowTex is the shared TextureUtil.GetRadialGlow cache — do NOT dispose it here.
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
                Color effColor;
                if (spell.CastFlipbook != null)
                    effColor = spell.CastFlipbook.Color.ToColor();
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
                Color summonColor;
                if (spell.SummonFlipbook != null)
                    summonColor = spell.SummonFlipbook.Color.ToColor();
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
                var style = spell.BuildStrikeStyle();
                bool isGodRay = string.Equals(spell.StrikeVisualType, "GodRay", StringComparison.OrdinalIgnoreCase);

                if (spell.StrikeTargetUnit)
                {
                    _zaps.Add(new PreviewZap
                    {
                        StartPos = new Vector2(CasterX, 0),
                        EndPos = new Vector2(TargetX, 0),
                        Timer = 0f,
                        Duration = Math.Max(0.1f, spell.ZapDuration),
                        Alive = true,
                        Style = style,
                    });
                }
                else
                {
                    _strikes.Add(new PreviewStrike
                    {
                        TargetPos = Vector2.Zero,
                        TelegraphTimer = spell.TelegraphDuration,
                        TelegraphDuration = spell.TelegraphDuration,
                        TelegraphVisible = spell.TelegraphVisible,
                        EffectTimer = 0f,
                        EffectDuration = Math.Max(0.1f, spell.StrikeDuration),
                        Alive = true,
                        AoeRadius = spell.AoeRadius,
                        Style = style,
                        IsGodRay = isGodRay,
                        GodRay = isGodRay ? spell.BuildGodRayParams() : null,
                    });
                }
                break;
            }

            case "Beam":
            {
                var style = spell.BuildBeamStyle();
                _beams.Add(new PreviewBeam
                {
                    StartPos = new Vector2(CasterX, 0),
                    EndPos = new Vector2(TargetX, 0),
                    Elapsed = 0f,
                    MaxDuration = spell.BeamMaxDuration > 0f
                        ? Math.Min(spell.BeamMaxDuration, 3f) : 2f,
                    Alive = true,
                    Style = style,
                });
                break;
            }

            case "Drain":
            {
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
                    Visuals = spell.BuildDrainVisuals(),
                });
                break;
            }

            case "Cloud":
            {
                // Show expanding cloud as a pulsing circle at the target position
                Color cloudColor = spell.CloudColor.ToColor();
                _effects.Add(new PreviewEffect
                {
                    Position = new Vector2(TargetX, 0),
                    Timer = 0f,
                    Duration = Math.Min(spell.CloudDuration > 0 ? spell.CloudDuration : 3f, 4f),
                    Alive = true,
                    EffectColor = cloudColor,
                    Scale = Math.Max(0.5f, spell.CloudRadius * 0.3f),
                    IsExpanding = true,
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
        bool useBloom = BloomEnabled && _bloom != null;

        // When bloom is enabled, render into bloom's scene RT; otherwise directly to preview RT
        if (useBloom)
            _bloom!.BeginScene(_gd);
        else
        {
            _gd.SetRenderTarget(_rt);
            _gd.Clear(new Color(18, 18, 28));
        }

        // Alpha blend pass: ground, markers, projectiles, effects
        Render.Materials.Hud.Begin(_sb);
        DrawGround();
        DrawMarkers();
        DrawProjectiles();
        DrawEffects();
        DrawHitEffects();
        _sb.End();

        // Additive HDR blend pass: lightning, beams, drains, projectile flipbooks
        if (_hdrSpriteEffect != null)
        {
            _hdrSpriteEffect.Parameters["AlphaMode"]?.SetValue(0f);
            _sb.Begin(SpriteSortMode.Deferred, BlendState.Additive, SamplerState.LinearClamp,
                effect: _hdrSpriteEffect);
            Render.Materials.NoteAdHocBatch(); // HDR-encoded colors — no tint conversion
        }
        else
        {
            _sb.Begin(SpriteSortMode.Deferred, BlendState.Additive, SamplerState.LinearClamp);
            Render.Materials.NoteAdHocBatch(); // additive pass — colors pass through raw
        }

        _strips.Clear();
        _godRays?.PendingGodRays.Clear();
        DrawStrikes();
        DrawBeams();
        DrawDrains();
        DrawZaps();
        DrawProjectileGlows();
        DrawHitGlows();
        _sb.End();

        // Ribbon bolts/tendrils + god rays: the same post-batch triangle passes as
        // the game's LightningTris pass, flushed onto the scene RT before bloom.
        _strips.DrawAll();
        _godRays?.DrawAll();

        // Bloom composites scene + bloom → preview RT; without bloom, already on preview RT
        if (useBloom)
        {
            var bloomSettings = new Data.Registries.BloomSettings
            {
                Enabled = true, Threshold = 0.4f, SoftKnee = 0.5f,
                Intensity = 2.0f, Scatter = 0.7f, Iterations = 4,
                BicubicUpsampling = true,
            };
            _bloom!.EndScene(_gd, _sb, bloomSettings, _rt);
        }

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
        float sx = _previewWidth * 0.5f + wx * CameraZoom;
        float sy = _previewHeight * 0.5f + wy * CameraZoom * CameraYRatio;
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
        int groundY = (int)(_previewHeight * 0.5f + 0.8f * CameraZoom * CameraYRatio);
        var groundColor = new Color(40, 45, 55);
        Scope.Draw(_pixel, new Rectangle(0, groundY, _previewWidth, 1), groundColor);

        // Ground gradient below
        for (int i = 0; i < 30; i++)
        {
            float a = 1f - i / 30f;
            var c = new Color(35, 38, 48) * (a * 0.3f);
            Scope.Draw(_pixel, new Rectangle(0, groundY + i, _previewWidth, 1), c);
        }

        // Grid dots
        var gridColor = new Color(50, 55, 65, 60);
        for (float wx = -5f; wx <= 5f; wx += 1f)
        {
            var sp = WorldToScreen(wx, 0);
            Scope.Draw(_pixel, new Rectangle((int)sp.X, groundY, 1, 1), gridColor);
        }
    }

    private void DrawMarkers()
    {
        int groundY = (int)(_previewHeight * 0.5f + 0.8f * CameraZoom * CameraYRatio);

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
            Scope.Draw(_pixel, new Rectangle((int)center.X - w, (int)center.Y - i, w * 2 + 1, 1), color);
            if (i > 0)
                Scope.Draw(_pixel, new Rectangle((int)center.X - w, (int)center.Y + i, w * 2 + 1, 1), color);
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

            // Shadow on ground (always drawn in alpha pass)
            var shadowPos = WorldToScreen(p.Position.X, p.Position.Y);
            int groundY = (int)(_previewHeight * 0.5f + 0.8f * CameraZoom * CameraYRatio);
            Scope.Draw(_pixel, new Rectangle((int)shadowPos.X - 2, groundY, 4, 1),
                new Color(0, 0, 0, 60));

            // If flipbook available, skip alpha-pass dot — flipbook draws in HDR additive pass
            if (!string.IsNullOrEmpty(p.FlipbookID) && _flipbooks != null &&
                _flipbooks.TryGetValue(p.FlipbookID, out var fb) && fb.IsLoaded)
                continue;

            // Fallback: core dot + trail (no flipbook)
            var screen = WorldToScreenWithHeight(p.Position.X, p.Position.Y, p.Height);
            float glowSize = 6f * CameraZoom / 32f * p.Scale;
            var projColor = p.ProjectileColor.ToColor();
            Scope.Draw(_pixel, screen, null, projColor,
                0f, new Vector2(0.5f, 0.5f), glowSize * 0.5f, SpriteEffects.None, 0f);

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
                    Scope.Draw(_pixel, ts, null, new Color(projColor.R, projColor.G, projColor.B, alpha),
                        0f, new Vector2(0.5f, 0.5f), trailLen / t, SpriteEffects.None, 0f);
                }
            }
        }
    }

    /// <summary>
    /// Draws projectile flipbooks + glows in the HDR additive pass.
    /// Matches Game1.DrawProjectilesHdr rendering.
    /// </summary>
    private void DrawProjectileGlows()
    {
        foreach (var p in _projectiles)
        {
            if (!p.Alive) continue;
            var screen = WorldToScreenWithHeight(p.Position.X, p.Position.Y, p.Height);

            // Try flipbook rendering (matches Game1.DrawProjectilesHdr)
            if (!string.IsNullOrEmpty(p.FlipbookID) && _flipbooks != null &&
                _flipbooks.TryGetValue(p.FlipbookID, out var fb) && fb.IsLoaded)
            {
                float worldSize = p.Scale * 1.5f;
                float pixelSize = worldSize * CameraZoom;
                int frameIdx = fb.GetFrameAtTime(p.Age);
                var srcRect = fb.GetFrameRect(frameIdx);
                float scale = pixelSize / srcRect.Width;
                var origin = new Vector2(srcRect.Width / 2f, srcRect.Height / 2f);

                // Trail: 2 previous frames behind with lower alpha
                Vec2 velDir = p.Velocity.LengthSquared() > 0.01f
                    ? new Vec2(p.Velocity.X, p.Velocity.Y).Normalized()
                    : new Vec2(1f, 0f);
                for (int trail = 2; trail >= 0; trail--)
                {
                    float trailOffset = trail * 0.4f * CameraZoom;
                    float trailAlpha = trail == 0 ? 1f : trail == 1 ? 0.5f : 0.25f;
                    float trailScale = trail == 0 ? 1f : trail == 1 ? 0.8f : 0.6f;

                    int trailFrame = fb.GetFrameAtTime(p.Age - trail * 0.05f);
                    var trailSrc = fb.GetFrameRect(trailFrame);
                    var trailPos = new Vector2(
                        screen.X - velDir.X * trailOffset,
                        screen.Y - velDir.Y * trailOffset * CameraYRatio);

                    var color = HdrColor.ToHdrVertex(p.ProjectileColor.ToColor(), trailAlpha, p.ProjectileColor.Intensity);
                    Scope.Draw(fb.Texture, trailPos, trailSrc, color,
                        p.Age * 2f, origin, scale * trailScale, SpriteEffects.None, 0f);
                }
            }
            else
            {
                // Fallback: radial glow dot
                float glowSize = 8f * CameraZoom / 32f * p.Scale;
                var glowColor = HdrColor.ToHdrVertex(p.ProjectileColor.ToColor(), 160f / 255f, p.ProjectileColor.Intensity);
                Scope.Draw(_glowTex, screen, null, glowColor,
                    p.Age * 2f, new Vector2(32f, 32f), glowSize / 32f, SpriteEffects.None, 0f);
            }
        }
    }

    private void DrawStrikes()
    {
        foreach (var s in _strikes)
        {
            if (!s.Alive) continue;
            var screen = WorldToScreen(s.TargetPos);
            int groundY = (int)(_previewHeight * 0.5f + 0.8f * CameraZoom * CameraYRatio);

            if (s.TelegraphTimer > 0)
            {
                if (s.TelegraphVisible)
                {
                    float pulse = 0.5f + 0.5f * MathF.Sin(_elapsed * 20f);
                    float radius = s.AoeRadius * CameraZoom * pulse;
                    if (radius < 4f) radius = 4f;
                    var gc = s.Style.GlowColor;
                    var telegraphColor = HdrColor.ToHdrVertex(gc.ToColor(), pulse * 0.4f, gc.Intensity * 0.5f);
                    Scope.Draw(_glowTex, new Vector2(screen.X, groundY), null, telegraphColor,
                        0f, new Vector2(32f, 32f), new Vector2(radius * 2 / 32f, radius * CameraYRatio / 32f),
                        SpriteEffects.None, 0f);
                }
            }
            else
            {
                float fade = 1f - s.EffectTimer / Math.Max(0.01f, s.EffectDuration);
                var groundPos = new Vector2(screen.X, groundY);

                if (s.IsGodRay && s.GodRay != null && _godRays != null)
                {
                    var skyPos = new Vector2(screen.X - _previewWidth * 0.1f, groundY - _previewHeight * 0.6f);
                    _godRays.PendingGodRays.Add((skyPos, groundPos,
                        s.Style, s.GodRay, _elapsed, s.EffectTimer, s.EffectDuration));
                }
                else
                {
                    var skyPos = new Vector2(screen.X - 20f, 5f);
                    LightningRenderer.AddBoltStripsStatic(_strips, skyPos, groundPos,
                        s.Style, fade, _elapsed);

                    // Impact glow
                    float radius = Math.Max(8f, s.AoeRadius * CameraZoom);
                    var splashColor = HdrColor.ToHdrVertex(s.Style.CoreColor.ToColor(), fade, s.Style.CoreColor.Intensity);
                    Scope.Draw(_glowTex, groundPos, null, splashColor,
                        0f, new Vector2(32f, 32f), new Vector2(radius / 32f, radius * CameraYRatio * 0.5f / 32f),
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
            LightningRenderer.AddBoltStripsStatic(_strips, startScreen, endScreen,
                z.Style, fade, _elapsed);
        }
    }

    private void DrawBeams()
    {
        foreach (var b in _beams)
        {
            if (!b.Alive) continue;
            var startScreen = WorldToScreenWithHeight(b.StartPos.X, b.StartPos.Y, 1.5f);
            var endScreen = WorldToScreenWithHeight(b.EndPos.X, b.EndPos.Y, 1.0f);
            float pulse = 1f + 0.3f * MathF.Sin(_elapsed * 8f);
            LightningRenderer.AddBoltStripsStatic(_strips, startScreen, endScreen,
                b.Style, 1f, _elapsed, widthScale: pulse);
        }
    }

    private void DrawDrains()
    {
        foreach (var d in _drains)
        {
            if (!d.Alive) continue;
            var srcScreen = WorldToScreenWithHeight(d.SourcePos.X, d.SourcePos.Y, 1.0f);
            var dstScreen = WorldToScreenWithHeight(d.DestPos.X, d.DestPos.Y, 1.5f);
            LightningRenderer.AddDrainTendrilStripsStatic(_strips, srcScreen, dstScreen, d.Visuals, d.Elapsed);
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
                DrawCircleOutline(screen, (int)radius, Core.ColorUtils.Fade(e.EffectColor, fade), 2);

                // Inner glow
                float innerRadius = t * 15f * e.Scale;
                DrawCircleOutline(screen, (int)innerRadius, e.EffectColor * (fade * 0.5f), 1);

                // Sparkles
                for (int s = 0; s < 4; s++)
                {
                    float angle = s * PI * 0.5f + _elapsed * 3f;
                    float r = radius * 0.7f;
                    var sparkle = screen + new Vector2(MathF.Cos(angle) * r, MathF.Sin(angle) * r * 0.5f);
                    Scope.Draw(_pixel, new Rectangle((int)sparkle.X - 1, (int)sparkle.Y - 1, 3, 3),
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
            int groundY = (int)(_previewHeight * 0.5f + 0.8f * CameraZoom * CameraYRatio);
            screen.Y = groundY;

            // Expanding ring
            float radius = t * 15f * h.Scale;
            DrawCircleOutline(screen, (int)radius, Core.ColorUtils.Fade(h.EffectColor, fade), 2);

            // Core flash dot
            float flashSize = (1f - t * t) * 4f * h.Scale;
            Scope.Draw(_pixel, screen, null,
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
            int groundY = (int)(_previewHeight * 0.5f + 0.8f * CameraZoom * CameraYRatio);
            screen.Y = groundY;

            // Radial glow burst
            float glowScale = (0.3f + t * 0.7f) * h.Scale * 0.6f;
            byte alpha = (byte)(200 * fade);
            Scope.Draw(_glowTex, screen, null,
                new Color(h.EffectColor.R, h.EffectColor.G, h.EffectColor.B, alpha),
                0f, new Vector2(32f, 32f), glowScale, SpriteEffects.None, 0f);
        }
    }

    // ========================================
    // Drawing primitives — ported from Game1.cs
    // ========================================

    // Lightning bolt, god ray, and tendril drawing now use shared static methods
    // from LightningRenderer and GodRayRenderer — no duplication needed.

    private void DrawThickLine(Vector2 a, Vector2 b, float thickness, Color color)
    {
        var diff = b - a;
        float length = diff.Length();
        if (length < 0.5f) return;
        float angle = MathF.Atan2(diff.Y, diff.X);
        Scope.Draw(_pixel, a, null, color,
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
    /// <summary>Randomize the swirl params (freq/amplitude/phase) on a projectile —
    /// shared by the Swirly and HomingSwirly trajectories.</summary>
    private static void ApplySwirl(ref PreviewProjectile p)
    {
       p.SwirlFreq = 1f + (float)RandUnit() * 0.5f;
       p.SwirlAmplitude = 1.0f + (float)RandUnit() * 0.25f;
       p.SwirlPhase = (float)RandUnit() * 2f * MathF.PI;
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

        // Color and flipbook from projectile definition
        if (spell.ProjectileFlipbook != null)
        {
            p.ProjectileColor = spell.ProjectileFlipbook.Color;
            p.FlipbookID = spell.ProjectileFlipbook.FlipbookID ?? "";
        }
        else
        {
            p.ProjectileColor = new HdrColor(255, 160, 80, 255, 1.5f);
            p.FlipbookID = "";
        }

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

        p.GravityScale = spell.GravityScale;

        switch (spell.TrajectoryMods) {
           case "Swirly": {
              ApplySwirl(ref p);
           }
              break;
           case "Swirly3d": {
              ApplySwirl(ref p);
           }
              break;
        }

        switch (spell.Trajectory)
        {
            // Trajectory theta comes from the shared ProjectileManager solver; the
            // velocity split stays local because the preview uses XNA Vector2 while the
            // shared BallisticVelocity works in Vec2.
            case "Lob":
            {
                float theta = ProjectileManager.SolveLobTheta(dist, speed);
                p.Velocity = dir * speed * MathF.Cos(theta);
                p.VelocityZ = speed * MathF.Sin(theta);
                break;
            }
            case "HighLob":
            {
                float theta = ProjectileManager.SolveLobTheta(dist, speed, preferLob: true);
                p.Velocity = dir * speed * MathF.Cos(theta);
                p.VelocityZ = speed * MathF.Sin(theta);
                break;
            }
            case "DirectFire":
            {
                float theta = ProjectileManager.DirectFireTheta;
                p.Velocity = dir * speed * MathF.Cos(theta);
                p.VelocityZ = speed * MathF.Sin(theta);
                break;
            }
            case "Homing":
            {
                float theta = ProjectileManager.DirectFireTheta;
                p.Velocity = dir * speed * MathF.Cos(theta);
                p.VelocityZ = speed * MathF.Sin(theta);
                p.TargetPos = new Vector2(TargetX, 0);
                p.HomingStrength = 5f;
                break;
            }
            case "Swirly":
            {
                float theta = ProjectileManager.DirectFireTheta;
                p.Velocity = dir * speed * MathF.Cos(theta);
                p.VelocityZ = speed * MathF.Sin(theta);
                
                ApplySwirl(ref p);
                break;
            }
            case "HomingSwirly":
            {
                float theta = ProjectileManager.DirectFireTheta;
                p.Velocity = dir * speed * MathF.Cos(theta);
                p.VelocityZ = speed * MathF.Sin(theta);
                p.TargetPos = new Vector2(TargetX, 0);
                p.HomingStrength = 5f;
                ApplySwirl(ref p);
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
            p.VelocityZ -= ProjGravity * dt * p.GravityScale;
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
                    ? _cachedSpell.HitEffectFlipbook.Color.ToColor()
                    : p.ProjectileColor.ToColor();
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
                        EffectColor = s.Style.CoreColor.ToColor(),
                        Scale = Math.Max(0.5f, s.AoeRadius * 0.1f + 0.5f),
                    });
                }
            }

            _strikes[i] = s;
        }
        for (int i = _strikes.Count - 1; i >= 0; i--)
            if (!_strikes[i].Alive) _strikes.RemoveAt(i);
    }

    /// <summary>Age a list of timed preview elements by <paramref name="dt"/>: drop any
    /// already-dead entry, advance the rest via <paramref name="step"/> (which bumps the
    /// element's timer and clears Alive when it reaches its duration), and drop those that
    /// just expired. Replaces the five near-identical UpdateZaps/Beams/Drains/Effects/
    /// HitEffects loops, whose separate second removal pass was redundant. Works for
    /// struct elements (write-back via <c>list[i] = e</c>).</summary>
    private static void AgeAndExpire<T>(List<T> list, float dt, Func<T, float, T> step, Func<T, bool> alive)
    {
        for (int i = list.Count - 1; i >= 0; i--)
        {
            var e = list[i];
            if (!alive(e)) { list.RemoveAt(i); continue; }
            e = step(e, dt);
            list[i] = e;
            if (!alive(e)) list.RemoveAt(i);
        }
    }

    private void UpdateZaps(float dt) => AgeAndExpire(_zaps, dt,
        (z, d) => { z.Timer += d; if (z.Timer >= z.Duration) z.Alive = false; return z; },
        z => z.Alive);

    private void UpdateBeams(float dt) => AgeAndExpire(_beams, dt,
        (b, d) => { b.Elapsed += d; if (b.Elapsed >= b.MaxDuration) b.Alive = false; return b; },
        b => b.Alive);

    private void UpdateDrains(float dt) => AgeAndExpire(_drains, dt,
        (dr, d) => { dr.Elapsed += d; if (dr.Elapsed >= dr.MaxDuration) dr.Alive = false; return dr; },
        dr => dr.Alive);

    private void UpdateEffects(float dt) => AgeAndExpire(_effects, dt,
        (e, d) => { e.Timer += d; if (e.Timer >= e.Duration) e.Alive = false; return e; },
        e => e.Alive);

    private void UpdateHitEffects(float dt) => AgeAndExpire(_hitEffects, dt,
        (h, d) => { h.Timer += d; if (h.Timer >= h.Duration) h.Alive = false; return h; },
        h => h.Alive);

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
