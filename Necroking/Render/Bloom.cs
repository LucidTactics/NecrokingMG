using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Content;
using Microsoft.Xna.Framework.Graphics;
using Necroking.Data.Registries;
using XnaEffect = Microsoft.Xna.Framework.Graphics.Effect;

namespace Necroking.Render;

/// <summary>
/// HDR bloom using a mip-chain downsample/upsample approach (matching C++ implementation).
/// 1. Prefilter: extract pixels above threshold into mips[0] (half-res)
/// 2. Downsample: progressively halve resolution through the mip chain
/// 3. Upsample: blend each mip back up with additive scatter
/// 4. Composite: blend the final bloom (mips[0]) with the scene using intensity
/// </summary>
public class BloomRenderer
{
    private const int MaxMips = 8;

    private RenderTarget2D? _sceneRT;
    private readonly RenderTarget2D?[] _mips = new RenderTarget2D?[MaxMips];
    private RenderTarget2D? _tempBlurRT; // for blur passes at each mip level
    private int _mipCount;
    private int _screenW, _screenH;
    private bool _initialized;
    private bool _hdrScene;
    private XnaEffect? _extractEffect;
    private XnaEffect? _combineEffect;
    private XnaEffect? _blurEffect;
    private XnaEffect? _bicubicUpsampleEffect;

    public bool IsInitialized => _initialized;
    public RenderTarget2D? SceneRT => _sceneRT;
    public bool IsHDR => _hdrScene;

    /// <summary>Set to true to show only the bloom extract result (diagnostic).</summary>
    public bool DebugShowExtract;

    public void Init(GraphicsDevice device, ContentManager content, int screenW, int screenH)
    {
        _screenW = screenW;
        _screenH = screenH;

        // Scene RT: HDR format so additive effects can exceed 1.0
        _hdrScene = false;
        try
        {
            _sceneRT = new RenderTarget2D(device, screenW, screenH, false,
                SurfaceFormat.HalfVector4, DepthFormat.Depth24Stencil8);
            _hdrScene = true;
            Console.Error.WriteLine("[Bloom] Scene RT: HalfVector4 (HDR)");
        }
        catch
        {
            _sceneRT = new RenderTarget2D(device, screenW, screenH, false,
                SurfaceFormat.Color, DepthFormat.Depth24Stencil8);
            Console.Error.WriteLine("[Bloom] Scene RT: Color (LDR fallback)");
        }

        // Mip chain: each level is half the previous
        SurfaceFormat bloomFmt = _hdrScene ? SurfaceFormat.HalfVector4 : SurfaceFormat.Color;
        _mipCount = 0;
        int mw = screenW / 2;
        int mh = screenH / 2;
        for (int i = 0; i < MaxMips && mw >= 2 && mh >= 2; i++)
        {
            try
            {
                _mips[i] = new RenderTarget2D(device, mw, mh, false, bloomFmt, DepthFormat.None);
            }
            catch
            {
                _mips[i] = new RenderTarget2D(device, mw, mh, false, SurfaceFormat.Color, DepthFormat.None);
            }
            _mipCount++;
            mw /= 2;
            mh /= 2;
        }

        // Temp blur RT at first mip level size (for blur passes)
        if (_mipCount > 0 && _mips[0] != null)
        {
            try
            {
                _tempBlurRT = new RenderTarget2D(device, _mips[0]!.Width, _mips[0]!.Height, false, bloomFmt, DepthFormat.None);
            }
            catch
            {
                _tempBlurRT = new RenderTarget2D(device, _mips[0]!.Width, _mips[0]!.Height, false, SurfaceFormat.Color, DepthFormat.None);
            }
        }

        Console.Error.WriteLine($"[Bloom] Scene RT: {_sceneRT!.Format} ({_sceneRT.Width}x{_sceneRT.Height})");
        for (int i = 0; i < _mipCount; i++)
            Console.Error.WriteLine($"[Bloom]   mip[{i}]: {_mips[i]!.Format} ({_mips[i]!.Width}x{_mips[i]!.Height})");
        Console.Error.WriteLine($"[Bloom] Mip chain: {_mipCount} levels, HDR={_hdrScene}");

        try
        {
            _extractEffect = content.Load<XnaEffect>("BloomExtract");
            _combineEffect = content.Load<XnaEffect>("BloomCombine");
            _blurEffect = content.Load<XnaEffect>("GaussianBlur");
            try
            {
                _bicubicUpsampleEffect = content.Load<XnaEffect>("BloomUpsampleBicubic");
                Console.Error.WriteLine("[Bloom] Bicubic upsample shader loaded");
            }
            catch (Exception bex)
            {
                _bicubicUpsampleEffect = null;
                Console.Error.WriteLine($"[Bloom] Bicubic upsample not loaded: {bex.Message}");
            }
            _initialized = true;
            Console.Error.WriteLine("[Bloom] Shaders loaded successfully");
        }
        catch (Exception ex)
        {
            _initialized = false;
            Console.Error.WriteLine($"[Bloom] Shader load failed: {ex.Message}");
        }
    }

    public void BeginScene(GraphicsDevice device)
    {
        if (!_initialized || _sceneRT == null) return;
        device.SetRenderTarget(_sceneRT);
        device.Clear(Color.Black);
    }

    public void EndScene(GraphicsDevice device, SpriteBatch batch, BloomSettings settings)
    {
        if (!_initialized || _sceneRT == null || _mipCount < 1 ||
            _extractEffect == null || _blurEffect == null || _combineEffect == null)
        {
            // No bloom — just blit scene
            device.SetRenderTarget(null);
            if (_sceneRT != null)
            {
                batch.Begin(SpriteSortMode.Immediate, BlendState.Opaque);
                batch.Draw(_sceneRT, new Rectangle(0, 0, _screenW, _screenH), Color.White);
                batch.End();
            }
            return;
        }

        if (!settings.Enabled)
        {
            device.SetRenderTarget(null);
            batch.Begin(SpriteSortMode.Immediate, BlendState.Opaque);
            batch.Draw(_sceneRT, new Rectangle(0, 0, _screenW, _screenH), Color.White);
            batch.End();
            return;
        }

        int iters = Math.Clamp(settings.Iterations, 1, _mipCount);

        // --- Step 1: Prefilter — extract bright pixels from scene → mips[0] ---
        _extractEffect.Parameters["BloomThreshold"]?.SetValue(settings.Threshold);
        _extractEffect.Parameters["SoftKnee"]?.SetValue(settings.SoftKnee);
        device.SetRenderTarget(_mips[0]);
        device.Clear(Color.Black);
        batch.Begin(SpriteSortMode.Immediate, BlendState.Opaque, SamplerState.LinearClamp, null, null, _extractEffect);
        batch.Draw(_sceneRT, new Rectangle(0, 0, _mips[0]!.Width, _mips[0]!.Height), Color.White);
        batch.End();

        // --- Step 2: Downsample chain — progressively halve resolution ---
        for (int i = 1; i < iters; i++)
        {
            device.SetRenderTarget(_mips[i]);
            device.Clear(Color.Black);
            batch.Begin(SpriteSortMode.Immediate, BlendState.Opaque, SamplerState.LinearClamp);
            batch.Draw(_mips[i - 1], new Rectangle(0, 0, _mips[i]!.Width, _mips[i]!.Height), Color.White);
            batch.End();
        }

        // --- Step 3: Upsample chain — blend each mip back up with scatter ---
        // Matches C++: optionally uses bicubic upsampling shader
        float scatter = Math.Clamp(settings.Scatter, 0f, 1f);
        bool useBicubic = settings.BicubicUpsampling && _bicubicUpsampleEffect != null;

        // Weighted additive: mip[i] = mip[i] + upsample(mip[i+1]) * scatter
        // BlendFactor controls how much of the wider blur gets added at each level.
        // Destination stays at full strength (One) so sharp detail is preserved.
        byte sb = (byte)(scatter * 255f);
        var scatterBlend = new BlendState
        {
            ColorSourceBlend = Blend.BlendFactor,
            ColorDestinationBlend = Blend.One,
            AlphaSourceBlend = Blend.BlendFactor,
            AlphaDestinationBlend = Blend.One,
            BlendFactor = new Color(sb, sb, sb, sb)
        };

        for (int i = iters - 2; i >= 0; i--)
        {
            device.SetRenderTarget(_mips[i]);
            if (useBicubic)
            {
                _bicubicUpsampleEffect!.Parameters["TexelSize"]?.SetValue(
                    new Vector2(1f / _mips[i + 1]!.Width, 1f / _mips[i + 1]!.Height));
                batch.Begin(SpriteSortMode.Immediate, scatterBlend, SamplerState.LinearClamp,
                    null, null, _bicubicUpsampleEffect);
            }
            else
            {
                batch.Begin(SpriteSortMode.Immediate, scatterBlend, SamplerState.LinearClamp);
            }
            batch.Draw(_mips[i + 1], new Rectangle(0, 0, _mips[i]!.Width, _mips[i]!.Height), Color.White);
            batch.End();
        }

        scatterBlend.Dispose();

        // Extra Gaussian blur on the final bloom to spread it beyond the mip chain limit
        if (_tempBlurRT != null && _blurEffect != null)
        {
            float blurScale = 1.5f;

            SetBlurParameters(_blurEffect, blurScale / _mips[0]!.Width, 0);
            device.SetRenderTarget(_tempBlurRT);
            batch.Begin(SpriteSortMode.Immediate, BlendState.Opaque, SamplerState.LinearClamp, null, null, _blurEffect);
            batch.Draw(_mips[0], new Rectangle(0, 0, _tempBlurRT.Width, _tempBlurRT.Height), Color.White);
            batch.End();

            SetBlurParameters(_blurEffect, 0, blurScale / _tempBlurRT.Height);
            device.SetRenderTarget(_mips[0]);
            batch.Begin(SpriteSortMode.Immediate, BlendState.Opaque, SamplerState.LinearClamp, null, null, _blurEffect);
            batch.Draw(_tempBlurRT, new Rectangle(0, 0, _mips[0]!.Width, _mips[0]!.Height), Color.White);
            batch.End();
        }

        // --- Step 4: Composite — scene + bloom * intensity → backbuffer ---
        device.SetRenderTarget(null);

        if (DebugShowExtract)
        {
            // Diagnostic: show just what the extract pass produced (should be bright areas only)
            batch.Begin(SpriteSortMode.Immediate, BlendState.Opaque, SamplerState.LinearClamp);
            batch.Draw(_mips[0], new Rectangle(0, 0, _screenW, _screenH), Color.White);
            batch.End();
            return;
        }

        _combineEffect.Parameters["BloomIntensity"]?.SetValue(settings.Intensity);
        _combineEffect.Parameters["BaseIntensity"]?.SetValue(1f);
        _combineEffect.Parameters["BloomSaturation"]?.SetValue(1.25f);
        _combineEffect.Parameters["BaseSaturation"]?.SetValue(1f);

        // Bind the bloom texture to sampler slot 1 via the effect parameter
        var bloomParam = _combineEffect.Parameters["BloomSampler"];
        if (bloomParam != null)
        {
            bloomParam.SetValue(_mips[0]);
        }
        else
        {
            Console.Error.WriteLine("[Bloom] WARNING: BloomSampler parameter not found, using fallback Textures[1]");
            device.Textures[1] = _mips[0];
            device.SamplerStates[1] = SamplerState.LinearClamp;
        }

        batch.Begin(SpriteSortMode.Immediate, BlendState.Opaque, SamplerState.LinearClamp, null, null, _combineEffect);
        batch.Draw(_sceneRT, new Rectangle(0, 0, _screenW, _screenH), Color.White);
        batch.End();
    }

    private static void SetBlurParameters(XnaEffect effect, float dx, float dy)
    {
        const int sampleCount = 15;
        var offsets = new Vector2[sampleCount];
        var weights = new float[sampleCount];

        float totalWeight = 0;
        float sigma = (sampleCount - 1) / 4f;
        for (int i = 0; i < sampleCount; i++)
        {
            float offset = i - (sampleCount - 1) / 2f;
            offsets[i] = new Vector2(offset * dx, offset * dy);
            weights[i] = MathF.Exp(-offset * offset / (2 * sigma * sigma));
            totalWeight += weights[i];
        }
        for (int i = 0; i < sampleCount; i++) weights[i] /= totalWeight;

        effect.Parameters["SampleOffsets"]?.SetValue(offsets);
        effect.Parameters["SampleWeights"]?.SetValue(weights);
    }

    public void Unload()
    {
        _sceneRT?.Dispose(); _sceneRT = null;
        for (int i = 0; i < _mipCount; i++)
        {
            _mips[i]?.Dispose();
            _mips[i] = null;
        }
        _tempBlurRT?.Dispose(); _tempBlurRT = null;
        _mipCount = 0;
        _initialized = false;
    }
}
