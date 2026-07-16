using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Content;
using Microsoft.Xna.Framework.Graphics;
using Necroking.Data.Registries;
using XnaEffect = Microsoft.Xna.Framework.Graphics.Effect;

namespace Necroking.Render;

/// <summary>
/// HDR bloom using a mip-chain downsample/upsample approach (Jimenez SIGGRAPH 2014 style).
/// 1. Prefilter: 13-tap Karis-weighted downsample + soft-knee threshold into mips[0] (half-res)
/// 2. Downsample: 13-tap filtered halving through the mip chain
/// 3. Upsample: blend each mip back up with additive scatter
/// 4. Composite: scene + bloom * intensity, then optional shoulder tonemap
///    (rolls >1.0 values off to white instead of hard-clipping)
/// </summary>
public class BloomRenderer
{
    private const int MaxMips = 8;

    private RenderTarget2D? _sceneRT;
    private readonly RenderTarget2D?[] _mips = new RenderTarget2D?[MaxMips];
    // Half-res scratch for the zoom-magnified halo (see EndScene): the fractional
    // deep-mip mix is accumulated here with plain weighted-additive draws instead
    // of in-place InverseBlendFactor lerps (which field-tested broken: a tiny
    // blend factor gutted the destination instead of preserving it).
    private RenderTarget2D? _haloScratch;
    private int _mipCount;
    private int _screenW, _screenH;
    private bool _initialized;
    private bool _hdrScene;
    private XnaEffect? _extractEffect;
    private XnaEffect? _combineEffect;
    private XnaEffect? _downsampleEffect;
    private XnaEffect? _bicubicUpsampleEffect;

    // Cached weighted-additive blend states, one slot per distinct per-frame
    // weight (see GetWeightBlend).
    private readonly BlendState?[] _scatterBlends = new BlendState?[5];
    private readonly byte[] _scatterBlendBytes = new byte[5];

    /// <summary>Weighted-additive blend: dst + src * w. The ONE blend shape this
    /// pipeline uses for mixing (proven by the scatter chain — InverseBlendFactor
    /// lerps corrupted their destination at small factors and are banned here).
    /// Slots keep distinct per-frame weights cached (BlendState is a native
    /// object; rebuild only when the quantized weight changes). Slots: 0/1 =
    /// upsample scatter (regular/deepest), 2/3 = halo scratch mix, 4 = halo add.</summary>
    private BlendState GetWeightBlend(float w, int slot)
    {
        byte sb = (byte)(Math.Clamp(w, 0f, 1f) * 255f);
        if (_scatterBlends[slot] == null || _scatterBlendBytes[slot] != sb)
        {
            _scatterBlends[slot]?.Dispose();
            _scatterBlends[slot] = new BlendState
            {
                ColorSourceBlend = Blend.BlendFactor,
                ColorDestinationBlend = Blend.One,
                AlphaSourceBlend = Blend.BlendFactor,
                AlphaDestinationBlend = Blend.One,
                BlendFactor = new Color(sb, sb, sb, sb)
            };
            _scatterBlendBytes[slot] = sb;
        }
        return _scatterBlends[slot]!;
    }

    public bool IsInitialized => _initialized;
    public RenderTarget2D? SceneRT => _sceneRT;
    public bool IsHDR => _hdrScene;

    // Last-frame zoom-bloom internals for the on-screen debug widget (temporary
    // while tuning the zoom-threshold artifacts): which code path produced the
    // frame the user is looking at.
    public float DebugBias, DebugFSpread, DebugFrac, DebugComp;
    public int DebugIters, DebugSrcMip;

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
            _downsampleEffect = content.Load<XnaEffect>("BloomDownsample");
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
        _haloScratch?.Dispose(); _haloScratch = null;
        for (int i = 0; i < _mipCount; i++) { _mips[i]?.Dispose(); _mips[i] = null; }
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

        if (_mipCount > 0)
        {
            try
            {
                _haloScratch = new RenderTarget2D(device, _mips[0]!.Width, _mips[0]!.Height,
                    false, bloomFmt, DepthFormat.None);
            }
            catch
            {
                _haloScratch = new RenderTarget2D(device, _mips[0]!.Width, _mips[0]!.Height,
                    false, SurfaceFormat.Color, DepthFormat.None);
            }
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

    /// <param name="zoomSpreadBias">Zoom-relative bloom spread in mip levels:
    /// log2(cameraZoom / authoringZoom). Realism model: a light source lights a
    /// fixed WORLD distance of nearby air, so the halo should occupy zoom-scaled
    /// screen pixels at the SAME intensity — never a fixed pixel size (reads huge
    /// zoomed out) and never energy-dispersed (reads absent zoomed in).
    /// Below the authoring zoom (bias &lt; 0): the effective iteration count
    /// shrinks, trimming the halo's outer levels (the finest mip is the floor —
    /// can't blur less than half-res). Above it (bias &gt; 0): the bloom composites
    /// from a DEEPER mip stretched up — the identical tuned profile magnified 2x
    /// per level at full intensity — with a fractional lerp between adjacent mips
    /// so wheel-zoom is smooth. 0 (the default, and the editor preview) = tuned look.</param>
    public void EndScene(GraphicsDevice device, SpriteBatch batch, BloomSettings settings,
        RenderTarget2D? outputTarget = null, float zoomSpreadBias = 0f)
    {
        if (!_initialized || _sceneRT == null || _mipCount < 1 ||
            _extractEffect == null || _downsampleEffect == null || _combineEffect == null)
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

        // Zoomed OUT (negative bias): shrink the chain depth; the fractional part
        // becomes the deepest level's blend weight (smooth radius change).
        float fSpread = Math.Clamp(settings.Iterations + MathF.Min(0f, zoomSpreadBias), 1f, _mipCount);
        int iters = (int)MathF.Ceiling(fSpread);
        float deepestFrac = 1f - (iters - fSpread); // 1 when fSpread is integral
        DebugBias = zoomSpreadBias; DebugFSpread = fSpread; DebugIters = iters;
        DebugSrcMip = 0; DebugFrac = 0f; DebugComp = 1f;

        // --- Step 1: Prefilter — 13-tap Karis downsample + threshold → mips[0] ---
        _extractEffect.Parameters["BloomThreshold"]?.SetValue(settings.Threshold);
        _extractEffect.Parameters["SoftKnee"]?.SetValue(settings.SoftKnee);
        _extractEffect.Parameters["TexelSize"]?.SetValue(
            new Vector2(1f / _sceneRT.Width, 1f / _sceneRT.Height));
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

        // --- Step 2: Downsample chain — 13-tap filtered halving (no shimmer) ---
        for (int i = 1; i < iters; i++)
        {
            _downsampleEffect.Parameters["TexelSize"]?.SetValue(
                new Vector2(1f / _mips[i - 1]!.Width, 1f / _mips[i - 1]!.Height));
            device.SetRenderTarget(_mips[i]);
            device.Clear(Color.Black);
            batch.Begin(SpriteSortMode.Immediate, BlendState.Opaque, SamplerState.LinearClamp, null, null, _downsampleEffect);
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
        // The DEEPEST level additionally carries deepestFrac (the fractional part
        // of the zoom-biased chain depth) so spread grows smoothly while zooming
        // instead of popping at integer mip counts.
        for (int i = iters - 2; i >= 0; i--)
        {
            bool deepest = i == iters - 2;
            var scatterBlend = GetWeightBlend(deepest ? scatter * deepestFrac : scatter, deepest ? 1 : 0);
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

        // Zoomed IN (positive bias): composite from a deeper mip — bilinear
        // stretch magnifies the whole tuned halo profile 2x per level at full
        // intensity (the realism model: same world illumination radius, more
        // pixels). Fractional bias lerps the next-deeper mip into the source mip
        // (it's rebuilt next frame, so blending in place is safe) for smooth
        // wheel-zoom. srcMip stays within the levels the chain actually filled.
        // Zoomed IN (positive bias): grow the halo by ADDING a deeper (2x-wider
        // per level) mip on top of the untouched mip0 — never attenuate or drop
        // the tuned bloom. The sharp half-res core is what makes a thin beam's
        // bloom read hot; any scheme that fades mip0 out produced hard thin/thick
        // thresholds at the octave zooms (64/128), field-verified with the debug
        // widget. Construction uses ONLY the proven weighted-additive blend
        // (src*factor + dst, same as the scatter chain — the in-place
        // InverseBlendFactor lerp corrupted its destination at small factors):
        //   scratch = (1-frac)*stretch(mip[deep]) + frac*stretch(mip[deep+1])
        //   mip0   += lambda * stretch(scratch),  lambda ramping to a cap over
        // the first octave. Halo radius grows smoothly; the core never dims, so
        // no intensity compensation is needed.
        int srcMip = 0;
        float intensityComp = 1f;
        if (zoomSpreadBias > 0f && _haloScratch != null)
        {
            float b = MathF.Min(zoomSpreadBias, iters - 1);
            int deep = Math.Max(1, (int)b);
            float frac = b - (int)b;
            if (deep + 1 > iters - 1) frac = 0f; // no deeper mip in the chain to mix

            device.SetRenderTarget(_haloScratch);
            device.Clear(Color.Black);
            var scratchRect = new Rectangle(0, 0, _haloScratch.Width, _haloScratch.Height);
            batch.Begin(SpriteSortMode.Immediate, GetWeightBlend(1f - frac, 2), SamplerState.LinearClamp);
            batch.Draw(_mips[deep], scratchRect, Color.White);
            batch.End();
            if (frac > 0.001f)
            {
                batch.Begin(SpriteSortMode.Immediate, GetWeightBlend(frac, 3), SamplerState.LinearClamp);
                batch.Draw(_mips[deep + 1], scratchRect, Color.White);
                batch.End();
            }

            const float HaloShare = 0.6f; // added halo weight cap
            float lambda = HaloShare * MathF.Min(1f, zoomSpreadBias);
            device.SetRenderTarget(_mips[0]);
            batch.Begin(SpriteSortMode.Immediate, GetWeightBlend(lambda, 4), SamplerState.LinearClamp);
            batch.Draw(_haloScratch, new Rectangle(0, 0, _mips[0]!.Width, _mips[0]!.Height), Color.White);
            batch.End();

            DebugSrcMip = deep; DebugFrac = lambda; DebugComp = intensityComp;
        }

        // --- Step 4: Composite — scene + bloom * intensity → output target ---
        // (The old extra Gaussian pass here was a band-aid for spread; the 13-tap
        // downsample chain provides the softness, and scatter/iterations tune it.)
        device.SetRenderTarget(outputTarget);

        _combineEffect.Parameters["BloomIntensity"]?.SetValue(settings.Intensity * intensityComp);
        _combineEffect.Parameters["TonemapEnabled"]?.SetValue(settings.Tonemap ? 1f : 0f);
        _combineEffect.Parameters["TonemapShoulder"]?.SetValue(settings.TonemapShoulder);
        _combineEffect.Parameters["TonemapWhitePoint"]?.SetValue(settings.TonemapWhitePoint);
        _combineEffect.Parameters["TonemapDesaturate"]?.SetValue(settings.TonemapDesaturate);

        // Bind the bloom texture to sampler slot 1 via the effect parameter.
        // srcMip > 0 stretches automatically: the combine shader samples
        // normalized UVs, so a smaller RT magnifies under LinearClamp.
        var bloomParam = _combineEffect.Parameters["BloomSampler"];
        if (bloomParam != null)
        {
            bloomParam.SetValue(_mips[srcMip]);
        }
        else
        {
            Console.Error.WriteLine("[Bloom] WARNING: BloomSampler parameter not found, using fallback Textures[1]");
            device.Textures[1] = _mips[srcMip];
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

    public void Unload()
    {
        _sceneRT?.Dispose(); _sceneRT = null;
        _haloScratch?.Dispose(); _haloScratch = null;
        for (int i = 0; i < _mipCount; i++)
        {
            _mips[i]?.Dispose();
            _mips[i] = null;
        }
        _mipCount = 0;
        _initialized = false;
    }
}
