using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Content;
using Microsoft.Xna.Framework.Graphics;
using Necroking.Core;
using Necroking.Data;
using Necroking.Data.Registries;
using Necroking.Movement;

namespace Necroking.Render;

/// <summary>
/// GPU-based fog of war using three render targets:
///   - Visibility RT: current-frame vision, cleared each Update, circles drawn opaque
///   - Explored RT:   persistent ever-seen map, max-blended from visibility each Update
///   - Combined RT:   R = explored, G = visible, packed each Update via channel-masked
///                    blends. The composite shader samples only this single RT (slot 0),
///                    sidestepping SpriteBatch's unreliable multi-sampler binding.
///
/// Modes: Off, Explored (permanent reveal), FogOfWar (classic three-state).
///
/// NOTE: If remote-vision spells or watchtower buildings are added later,
/// add them as additional vision sources in Update.
/// </summary>
public class FogOfWarSystem
{
    // RT resolution — scales with world size so tiny maps don't get pixelated circle
    // edges. Clamped to [1024, 2048] to bound GPU memory (~16 MB per RT at 2048²).
    private const int MinRTSize = 1024;
    private const int MaxRTSize = 2048;
    private const int TexelsPerWorldUnit = 16;
    private int _rtSize = MinRTSize;

    // Update throttle for the *hard* visibility circles: vision barely changes
    // frame-to-frame, so refresh the raw circle-draw every N frames. The temporal
    // smoothing still runs every frame so the fade stays buttery.
    private const int UpdateInterval = 2;
    private int _frameCounter;

    // Temporal smoothing: time (seconds) for the displayed visibility to converge
    // on the true visibility. 0.5s matches the user-facing fade target.
    private const float FadeTime = 0.5f;

    private int _worldW, _worldH;
    private GraphicsDevice? _device;

    // Render targets. Pipeline:
    //   _visibilityRT         — raw per-tick vision (hard circles drawn by units).
    //   _smoothedVisibilityRT — temporal lerp toward _visibilityRT each frame,
    //                           giving a 0.5s fade-in/out so new reveals glide in
    //                           instead of popping.
    //   _exploredRT           — cumulative max of _smoothedVisibilityRT (once a
    //                           pixel fades in, it stays "explored").
    //   _combinedRT           — R=explored, G=smoothed visibility. Single sampler
    //                           for the composite shader, sidesteps SpriteBatch's
    //                           unreliable multi-sampler binding.
    private RenderTarget2D? _visibilityRT;
    private RenderTarget2D? _smoothedVisibilityRT;
    private RenderTarget2D? _exploredRT;
    private RenderTarget2D? _combinedRT;

    // Hard-edged circle texture for drawing crisp oval vision. Sized large enough
    // that the 1-pixel perimeter is sub-pixel at any reasonable upscale to the RT.
    private Texture2D? _circleTex;
    private const int CircleTexSize = 256;

    // Composite shader
    private Microsoft.Xna.Framework.Graphics.Effect? _compositeEffect;

    // CPU-side visibility grid for gameplay queries (unit/projectile/damage-number
    // culling). One bool per world tile; rebuilt in Update alongside the GPU RTs.
    // O(1) lookup via IsVisible(pos). Mirrors what the GPU visibility RT shows.
    private bool[] _visibleTiles = Array.Empty<bool>();
    private FogOfWarMode _lastMode = FogOfWarMode.Off;

    // Max blend: only brighten (used when accumulating visibility into explored).
    private static readonly BlendState MaxBlend = new()
    {
        ColorBlendFunction = BlendFunction.Max,
        ColorSourceBlend = Blend.One,
        ColorDestinationBlend = Blend.One,
        AlphaBlendFunction = BlendFunction.Max,
        AlphaSourceBlend = Blend.One,
        AlphaDestinationBlend = Blend.One,
    };

    // Write only the R channel (used when packing explored into the combined RT).
    private static readonly BlendState OpaqueRedOnly = new()
    {
        ColorBlendFunction = BlendFunction.Add,
        ColorSourceBlend = Blend.One,
        ColorDestinationBlend = Blend.Zero,
        AlphaBlendFunction = BlendFunction.Add,
        AlphaSourceBlend = Blend.One,
        AlphaDestinationBlend = Blend.Zero,
        ColorWriteChannels = ColorWriteChannels.Red,
    };

    // Write only the G channel (used when packing visibility into the combined RT).
    private static readonly BlendState OpaqueGreenOnly = new()
    {
        ColorBlendFunction = BlendFunction.Add,
        ColorSourceBlend = Blend.One,
        ColorDestinationBlend = Blend.Zero,
        AlphaBlendFunction = BlendFunction.Add,
        AlphaSourceBlend = Blend.One,
        AlphaDestinationBlend = Blend.Zero,
        ColorWriteChannels = ColorWriteChannels.Green,
    };

    // Smoothing shader. Outputs premultiplied (src.rgb * Rate, Rate) so that
    // BlendState.AlphaBlend produces newDst = lerp(oldDst, src, Rate). Portable
    // across DesktopGL and WindowsDX (Blend.BlendFactor is not reliable in the
    // OpenGL backend, which is why this project took the shader route).
    private Microsoft.Xna.Framework.Graphics.Effect? _smoothEffect;

    public void Init(int worldW, int worldH, GraphicsDevice device, ContentManager content)
    {
        _worldW = worldW;
        _worldH = worldH;
        _device = device;

        _visibilityRT?.Dispose();
        _smoothedVisibilityRT?.Dispose();
        _exploredRT?.Dispose();
        _combinedRT?.Dispose();

        int desired = Math.Max(worldW, worldH) * TexelsPerWorldUnit;
        _rtSize = Math.Clamp(desired, MinRTSize, MaxRTSize);

        _visibilityRT = new RenderTarget2D(device, _rtSize, _rtSize, false, SurfaceFormat.Color, DepthFormat.None,
            0, RenderTargetUsage.PreserveContents);
        _smoothedVisibilityRT = new RenderTarget2D(device, _rtSize, _rtSize, false, SurfaceFormat.Color, DepthFormat.None,
            0, RenderTargetUsage.PreserveContents);
        _exploredRT = new RenderTarget2D(device, _rtSize, _rtSize, false, SurfaceFormat.Color, DepthFormat.None,
            0, RenderTargetUsage.PreserveContents);
        _combinedRT = new RenderTarget2D(device, _rtSize, _rtSize, false, SurfaceFormat.Color, DepthFormat.None,
            0, RenderTargetUsage.PreserveContents);

        _visibleTiles = new bool[worldW * worldH];

        // Clear all smoothable RTs to black (all unexplored). Don't need to clear
        // them every frame — they carry state across frames via PreserveContents.
        device.SetRenderTarget(_smoothedVisibilityRT);
        device.Clear(Color.Black);
        device.SetRenderTarget(_exploredRT);
        device.Clear(Color.Black);
        device.SetRenderTarget(_combinedRT);
        device.Clear(Color.Black);
        device.SetRenderTarget(null);

        _circleTex?.Dispose();
        _circleTex = CreateCircleTexture(device, CircleTexSize);

        try { _compositeEffect = content.Load<Microsoft.Xna.Framework.Graphics.Effect>("FogComposite"); }
        catch (Exception ex) { DebugLog.Log("error", $"Failed to load FogComposite shader: {ex.Message}"); }

        try { _smoothEffect = content.Load<Microsoft.Xna.Framework.Graphics.Effect>("FogSmooth"); }
        catch (Exception ex) { DebugLog.Log("error", $"Failed to load FogSmooth shader: {ex.Message}"); }

        _frameCounter = 0;

        DebugLog.Log("startup", $"FogOfWar GPU init: world {worldW}x{worldH}, RT {_rtSize}x{_rtSize}");
    }

    /// <summary>
    /// Update fog. Runs two kinds of work each frame:
    ///   - (throttled) redraw the raw visibility circles into _visibilityRT
    ///   - (every frame) temporally lerp _smoothedVisibilityRT toward the raw
    ///     visibility, accumulate into explored, and pack into combined
    /// The temporal pass uses GraphicsDevice.BlendFactor to control the lerp
    /// rate without a custom shader.
    /// </summary>
    public void Update(SpriteBatch spriteBatch, UnitArrays units, FogOfWarSettings settings, float dt)
    {
        if (_device == null || _visibilityRT == null || _smoothedVisibilityRT == null
            || _exploredRT == null || _combinedRT == null || _circleTex == null) return;
        var mode = (FogOfWarMode)settings.Mode;
        _lastMode = mode;
        if (mode == FogOfWarMode.Off) return;

        // --- Part A: raw visibility circles (throttled) ---
        _frameCounter++;
        bool redrawCircles = (_frameCounter % UpdateInterval) == 0;
        if (redrawCircles)
        {
            float scaleX = (float)_rtSize / _worldW;
            float scaleY = (float)_rtSize / _worldH;

            Array.Clear(_visibleTiles, 0, _visibleTiles.Length);

            _device.SetRenderTarget(_visibilityRT);
            _device.Clear(Color.Black);

            spriteBatch.Begin(SpriteSortMode.Deferred, MaxBlend, SamplerState.LinearClamp);
            for (int i = 0; i < units.Count; i++)
            {
                var unit = units[i];
                if (!unit.Alive || unit.Faction != Faction.Undead) continue;

                float sightRange = unit.DetectionRange;
                if (sightRange <= 0f) sightRange = settings.DefaultSightRange;

                float cx = unit.Position.X * scaleX;
                float cy = unit.Position.Y * scaleY;
                float radiusX = sightRange * scaleX;
                float radiusY = sightRange * scaleY;

                spriteBatch.Draw(_circleTex,
                    new Rectangle((int)(cx - radiusX), (int)(cy - radiusY),
                                  (int)(radiusX * 2f), (int)(radiusY * 2f)),
                    Color.White);

                // CPU visibility grid for gameplay queries (stays instant — we
                // don't want the culling logic to lag behind the renderer).
                float sightSq = sightRange * sightRange;
                int minTx = Math.Max(0, (int)(unit.Position.X - sightRange));
                int maxTx = Math.Min(_worldW - 1, (int)(unit.Position.X + sightRange));
                int minTy = Math.Max(0, (int)(unit.Position.Y - sightRange));
                int maxTy = Math.Min(_worldH - 1, (int)(unit.Position.Y + sightRange));
                for (int ty = minTy; ty <= maxTy; ty++)
                {
                    float dy = (ty + 0.5f) - unit.Position.Y;
                    float dySq = dy * dy;
                    int row = ty * _worldW;
                    for (int tx = minTx; tx <= maxTx; tx++)
                    {
                        float dx = (tx + 0.5f) - unit.Position.X;
                        if (dx * dx + dySq <= sightSq) _visibleTiles[row + tx] = true;
                    }
                }
            }
            spriteBatch.End();
        }

        // --- Part B: temporal smoothing (every frame, independent of throttle) ---
        // Lerp smoothedVisibility toward visibilityRT at rate = dt / FadeTime.
        // Exponential approach gives a natural ~FadeTime-second fade-in/out.
        float rate = Math.Clamp(dt / FadeTime, 0f, 1f);

        _device.SetRenderTarget(_smoothedVisibilityRT);
        if (_smoothEffect != null)
        {
            _smoothEffect.Parameters["Rate"]?.SetValue(rate);
            spriteBatch.Begin(SpriteSortMode.Immediate, BlendState.AlphaBlend, SamplerState.LinearClamp,
                null, null, _smoothEffect);
            spriteBatch.Draw(_visibilityRT, new Rectangle(0, 0, _rtSize, _rtSize), Color.White);
            spriteBatch.End();
        }
        else
        {
            // Fallback without shader: copy hard visibility directly (no smoothing).
            spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.Opaque, SamplerState.LinearClamp);
            spriteBatch.Draw(_visibilityRT, new Rectangle(0, 0, _rtSize, _rtSize), Color.White);
            spriteBatch.End();
        }

        // Explored = cumulative max of smoothed visibility. Pixels rise smoothly
        // when first revealed; once at 1 they stay there (max doesn't decrease),
        // so when units leave they transition visible → fogged but not → unexplored.
        _device.SetRenderTarget(_exploredRT);
        spriteBatch.Begin(SpriteSortMode.Deferred, MaxBlend, SamplerState.LinearClamp);
        spriteBatch.Draw(_smoothedVisibilityRT, new Rectangle(0, 0, _rtSize, _rtSize), Color.White);
        spriteBatch.End();

        // --- Part C: pack explored (R) + smoothed visibility (G) into combinedRT ---
        _device.SetRenderTarget(_combinedRT);
        _device.Clear(Color.Black);

        spriteBatch.Begin(SpriteSortMode.Deferred, OpaqueRedOnly, SamplerState.LinearClamp);
        spriteBatch.Draw(_exploredRT, new Rectangle(0, 0, _rtSize, _rtSize), Color.White);
        spriteBatch.End();

        spriteBatch.Begin(SpriteSortMode.Deferred, OpaqueGreenOnly, SamplerState.LinearClamp);
        spriteBatch.Draw(_smoothedVisibilityRT, new Rectangle(0, 0, _rtSize, _rtSize), Color.White);
        spriteBatch.End();

        _device.SetRenderTarget(null);
    }

    /// <summary>
    /// Returns true if the given world position should be rendered in full:
    ///   - Mode.Off:      always yes (no fog gameplay)
    ///   - Mode.Explored: always yes (permanent reveal — explored tiles stay lit,
    ///                    and the user wants units visible in them too)
    ///   - Mode.FogOfWar: yes only if the tile is currently inside some friendly
    ///                    undead's detection range
    /// Used to cull enemy unit sprites, their shadows, buffs, projectiles, and
    /// damage numbers when the necromancer can't see them.
    /// </summary>
    public bool IsVisible(Vec2 pos)
    {
        if (_lastMode != FogOfWarMode.FogOfWar) return true;
        int x = (int)pos.X;
        int y = (int)pos.Y;
        if (x < 0 || x >= _worldW || y < 0 || y >= _worldH) return false;
        return _visibleTiles[y * _worldW + x];
    }

    /// <summary>
    /// Draw the fog overlay on screen using the composite shader.
    /// Call during the main draw pass (after world, before HUD).
    /// </summary>
    public void Draw(SpriteBatch spriteBatch, Camera25D camera, Renderer renderer, int screenW, int screenH,
        FogOfWarSettings settings)
    {
        if (_device == null || _visibilityRT == null || _exploredRT == null || _combinedRT == null) return;
        var mode = (FogOfWarMode)settings.Mode;
        if (mode == FogOfWarMode.Off) return;

        // Compute screen rectangle covering the world
        var topLeft = renderer.WorldToScreen(Vec2.Zero, 0f, camera);
        var bottomRight = renderer.WorldToScreen(new Vec2(_worldW, _worldH), 0f, camera);
        var destRect = new Rectangle(
            (int)topLeft.X, (int)topLeft.Y,
            (int)(bottomRight.X - topLeft.X), (int)(bottomRight.Y - topLeft.Y));

        if (_compositeEffect != null)
        {
            float unexploredA = settings.UnexploredAlpha;
            float foggedA = mode == FogOfWarMode.Explored ? 0f : settings.FoggedAlpha;

            _compositeEffect.Parameters["UnexploredAlpha"]?.SetValue(unexploredA);
            _compositeEffect.Parameters["FoggedAlpha"]?.SetValue(foggedA);

            spriteBatch.Begin(SpriteSortMode.Immediate, BlendState.AlphaBlend, SamplerState.LinearClamp,
                null, null, _compositeEffect);
            spriteBatch.Draw(_combinedRT, destRect, Color.White);
            spriteBatch.End();
        }
        else
        {
            byte alpha = (byte)(settings.UnexploredAlpha * 255);
            spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.LinearClamp);
            spriteBatch.Draw(_combinedRT, destRect, new Color((byte)0, (byte)0, (byte)0, alpha));
            spriteBatch.End();
        }
    }

    /// <summary>
    /// Soft-edged white circle texture. A feather zone near the perimeter gives
    /// gradient alpha values, which the composite shader's widened smoothstep
    /// then stretches into a diffused transition between visible/fogged/unexplored.
    /// The interior is solid (so the center of vision is fully revealed).
    /// </summary>
    private static Texture2D CreateCircleTexture(GraphicsDevice device, int size)
    {
        var tex = new Texture2D(device, size, size);
        var pixels = new Color[size * size];
        float center = size * 0.5f;
        // Last 15% of the radius fades from solid to zero. Small enough to keep
        // the oval clearly circular, wide enough that the shader's smoothstep
        // produces a visibly diffuse edge rather than a sharp cutoff.
        float featherStart = center * 0.85f;
        float featherRange = center - featherStart;

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float dx = x + 0.5f - center;
                float dy = y + 0.5f - center;
                float dist = MathF.Sqrt(dx * dx + dy * dy);

                float a01;
                if (dist <= featherStart)
                    a01 = 1f;
                else if (dist >= center)
                    a01 = 0f;
                else
                    a01 = 1f - (dist - featherStart) / featherRange;

                byte a = (byte)(a01 * 255f);
                pixels[y * size + x] = new Color(a, a, a, a);
            }
        }

        tex.SetData(pixels);
        return tex;
    }

    public void Dispose()
    {
        _visibilityRT?.Dispose();
        _smoothedVisibilityRT?.Dispose();
        _exploredRT?.Dispose();
        _combinedRT?.Dispose();
        _circleTex?.Dispose();
        _visibilityRT = null;
        _smoothedVisibilityRT = null;
        _exploredRT = null;
        _combinedRT = null;
        _circleTex = null;
    }
}
