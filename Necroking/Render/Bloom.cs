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

    // Cached scatter blend state — BlendState is a native graphics object, so
    // it's rebuilt only when the quantized scatter value changes, not per frame.
    private BlendState? _scatterBlend;
    private byte _scatterBlendByte;

    // Gaussian blur kernel for the final wide blur, precomputed in CreateTargets
    // (it only changes with RT size).
    private const int BlurSampleCount = 15;
    private const float BlurScale = 1.5f;
    private readonly Vector2[] _blurOffsetsH = new Vector2[BlurSampleCount];
    private readonly Vector2[] _blurOffsetsV = new Vector2[BlurSampleCount];
    private readonly float[] _blurWeights = new float[BlurSampleCount];

    public bool IsInitialized => _initialized;
    public RenderTarget2D? SceneRT => _sceneRT;
    public bool IsHDR => _hdrScene;

    /// <summary>Set to true to show only the bloom extract result (diagnostic).</summary>
    public bool DebugShowExtract;

    public void Init(GraphicsDevice device, ContentManager content, int screenW, int screenH)
    {
        _screenW = screenW;
        _screenH = screenH;

        CreateTargets(device);

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

    /// <summary>Recreate the scene + mip render targets at a new size, keeping the
    /// already-loaded shaders. Called on window resize so the bloom buffers match
    /// the new back-buffer dimensions. No-op if size is unchanged or invalid.</summary>
    public void Resize(GraphicsDevice device, int screenW, int screenH)
    {
        if (!_initialized || screenW <= 0 || screenH <= 0) return;
        if (screenW == _screenW && screenH == _screenH) return;
        _sceneRT?.Dispose(); _sceneRT = null;
        for (int i = 0; i < _mipCount; i++) { _mips[i]?.Dispose(); _mips[i] = null; }
        _tempBlurRT?.Dispose(); _tempBlurRT = null;
        _mipCount = 0;
        _screenW = screenW;
        _screenH = screenH;
        CreateTargets(device);
    }

    /// <summary>(Re)create the scene capture target and the bloom mip chain at the
    /// current _screenW/_screenH. Shaders are loaded separately in Init.</summary>
    private void CreateTargets(GraphicsDevice device)
    {
        int screenW = _screenW, screenH = _screenH;

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

        // Precompute the Gaussian blur kernel for the final H/V passes — it only
        // depends on the RT sizes created above, so rebuild it here (and on Resize
        // via CreateTargets) instead of reallocating twice per frame in EndScene.
        if (_mipCount > 0 && _mips[0] != null && _tempBlurRT != null)
        {
            BuildBlurKernel(BlurScale / _mips[0]!.Width, 0f, _blurOffsetsH, _blurWeights);
            BuildBlurKernel(0f, BlurScale / _tempBlurRT.Height, _blurOffsetsV, _blurWeights);
        }

        Console.Error.WriteLine($"[Bloom] Scene RT: {_sceneRT!.Format} ({_sceneRT.Width}x{_sceneRT.Height})");
        for (int i = 0; i < _mipCount; i++)
            Console.Error.WriteLine($"[Bloom]   mip[{i}]: {_mips[i]!.Format} ({_mips[i]!.Width}x{_mips[i]!.Height})");
        Console.Error.WriteLine($"[Bloom] Mip chain: {_mipCount} levels, HDR={_hdrScene}");
    }

    public void BeginScene(GraphicsDevice device)
    {
        if (!_initialized || _sceneRT == null) return;
        device.SetRenderTarget(_sceneRT);
        device.Clear(Color.Black);
    }

    public void EndScene(GraphicsDevice device, SpriteBatch batch, BloomSettings settings,
        RenderTarget2D? outputTarget = null)
    {
        if (!_initialized || _sceneRT == null || _mipCount < 1 ||
            _extractEffect == null || _blurEffect == null || _combineEffect == null)
        {
            // No bloom — just blit scene
            device.SetRenderTarget(outputTarget);
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
            device.SetRenderTarget(outputTarget);
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

        if (DebugShowExtract)
        {
            // Diagnostic: show just what the extract pass produced (bright areas
            // only), skipping the downsample/upsample/blur chain entirely.
            device.SetRenderTarget(outputTarget);
            batch.Begin(SpriteSortMode.Immediate, BlendState.Opaque, SamplerState.LinearClamp);
            batch.Draw(_mips[0], new Rectangle(0, 0, _screenW, _screenH), Color.White);
            batch.End();
            return;
        }

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
        if (_scatterBlend == null || _scatterBlendByte != sb)
        {
            _scatterBlend?.Dispose();
            _scatterBlend = new BlendState
            {
                ColorSourceBlend = Blend.BlendFactor,
                ColorDestinationBlend = Blend.One,
                AlphaSourceBlend = Blend.BlendFactor,
                AlphaDestinationBlend = Blend.One,
                BlendFactor = new Color(sb, sb, sb, sb)
            };
            _scatterBlendByte = sb;
        }
        var scatterBlend = _scatterBlend;

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

        // Extra Gaussian blur on the final bloom to spread it beyond the mip chain
        // limit. Kernel arrays are precomputed in CreateTargets; only the uploads
        // happen per frame (offsets alternate H/V on the same effect parameter).
        if (_tempBlurRT != null && _blurEffect != null)
        {
            _blurEffect.Parameters["SampleWeights"]?.SetValue(_blurWeights);
            _blurEffect.Parameters["SampleOffsets"]?.SetValue(_blurOffsetsH);
            device.SetRenderTarget(_tempBlurRT);
            batch.Begin(SpriteSortMode.Immediate, BlendState.Opaque, SamplerState.LinearClamp, null, null, _blurEffect);
            batch.Draw(_mips[0], new Rectangle(0, 0, _tempBlurRT.Width, _tempBlurRT.Height), Color.White);
            batch.End();

            _blurEffect.Parameters["SampleOffsets"]?.SetValue(_blurOffsetsV);
            device.SetRenderTarget(_mips[0]);
            batch.Begin(SpriteSortMode.Immediate, BlendState.Opaque, SamplerState.LinearClamp, null, null, _blurEffect);
            batch.Draw(_tempBlurRT, new Rectangle(0, 0, _mips[0]!.Width, _mips[0]!.Height), Color.White);
            batch.End();
        }

        // --- Step 4: Composite — scene + bloom * intensity → output target ---
        device.SetRenderTarget(outputTarget);

        _combineEffect.Parameters["BloomIntensity"]?.SetValue(settings.Intensity);

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

        // BloomSampler (s1) declares no sampler state in the .fx, so it samples
        // with whatever is left in device slot 1 (default LinearWrap, which
        // bleeds bloom across opposite screen edges). Set it explicitly.
        device.SamplerStates[1] = SamplerState.LinearClamp;

        batch.Begin(SpriteSortMode.Immediate, BlendState.Opaque, SamplerState.LinearClamp, null, null, _combineEffect);
        batch.Draw(_sceneRT, new Rectangle(0, 0, _screenW, _screenH), Color.White);
        batch.End();
    }

    /// <summary>Fill the given offset/weight arrays with a normalized 1D Gaussian
    /// kernel along (dx, dy). Weights are identical for H and V, so both calls
    /// write the same values into the shared weights array.</summary>
    private static void BuildBlurKernel(float dx, float dy, Vector2[] offsets, float[] weights)
    {
        float totalWeight = 0;
        float sigma = (BlurSampleCount - 1) / 4f;
        for (int i = 0; i < BlurSampleCount; i++)
        {
            float offset = i - (BlurSampleCount - 1) / 2f;
            offsets[i] = new Vector2(offset * dx, offset * dy);
            weights[i] = MathF.Exp(-offset * offset / (2 * sigma * sigma));
            totalWeight += weights[i];
        }
        for (int i = 0; i < BlurSampleCount; i++) weights[i] /= totalWeight;
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
