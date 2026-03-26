using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Content;
using Microsoft.Xna.Framework.Graphics;
using Necroking.Data.Registries;
using XnaEffect = Microsoft.Xna.Framework.Graphics.Effect;

namespace Necroking.Render;

public class BloomRenderer
{
    private RenderTarget2D? _sceneRT;
    private RenderTarget2D? _bloomRT1;
    private RenderTarget2D? _bloomRT2;
    private int _screenW, _screenH;
    private bool _initialized;

    private XnaEffect? _extractEffect;
    private XnaEffect? _combineEffect;
    private XnaEffect? _blurEffect;

    public bool IsInitialized => _initialized;
    public RenderTarget2D? SceneRT => _sceneRT;

    public void Init(GraphicsDevice device, ContentManager content, int screenW, int screenH)
    {
        _screenW = screenW;
        _screenH = screenH;

        _sceneRT = new RenderTarget2D(device, screenW, screenH, false,
            SurfaceFormat.Color, DepthFormat.Depth24Stencil8);

        int bloomW = screenW / 2;
        int bloomH = screenH / 2;
        _bloomRT1 = new RenderTarget2D(device, bloomW, bloomH, false, SurfaceFormat.Color, DepthFormat.None);
        _bloomRT2 = new RenderTarget2D(device, bloomW, bloomH, false, SurfaceFormat.Color, DepthFormat.None);

        try
        {
            _extractEffect = content.Load<XnaEffect>("BloomExtract");
            _combineEffect = content.Load<XnaEffect>("BloomCombine");
            _blurEffect = content.Load<XnaEffect>("GaussianBlur");
            _initialized = true;
        }
        catch
        {
            // Shaders failed to compile — bloom disabled
            _initialized = false;
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
        if (!_initialized || _sceneRT == null || _bloomRT1 == null || _bloomRT2 == null ||
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

        // Step 1: Extract bright pixels
        _extractEffect.Parameters["BloomThreshold"]?.SetValue(settings.Threshold);
        device.SetRenderTarget(_bloomRT1);
        batch.Begin(SpriteSortMode.Immediate, BlendState.Opaque, null, null, null, _extractEffect);
        batch.Draw(_sceneRT, new Rectangle(0, 0, _bloomRT1.Width, _bloomRT1.Height), Color.White);
        batch.End();

        // Step 2: Horizontal blur
        SetBlurParameters(_blurEffect, 1f / _bloomRT1.Width, 0);
        device.SetRenderTarget(_bloomRT2);
        batch.Begin(SpriteSortMode.Immediate, BlendState.Opaque, null, null, null, _blurEffect);
        batch.Draw(_bloomRT1, new Rectangle(0, 0, _bloomRT2.Width, _bloomRT2.Height), Color.White);
        batch.End();

        // Step 3: Vertical blur
        SetBlurParameters(_blurEffect, 0, 1f / _bloomRT2.Height);
        device.SetRenderTarget(_bloomRT1);
        batch.Begin(SpriteSortMode.Immediate, BlendState.Opaque, null, null, null, _blurEffect);
        batch.Draw(_bloomRT2, new Rectangle(0, 0, _bloomRT1.Width, _bloomRT1.Height), Color.White);
        batch.End();

        // Step 4: Combine
        device.SetRenderTarget(null);
        device.Textures[1] = _bloomRT1;
        _combineEffect.Parameters["BloomIntensity"]?.SetValue(settings.Intensity);
        _combineEffect.Parameters["BaseIntensity"]?.SetValue(1f);
        _combineEffect.Parameters["BloomSaturation"]?.SetValue(1.25f);
        _combineEffect.Parameters["BaseSaturation"]?.SetValue(1f);

        batch.Begin(SpriteSortMode.Immediate, BlendState.Opaque, null, null, null, _combineEffect);
        batch.Draw(_sceneRT, new Rectangle(0, 0, _screenW, _screenH), Color.White);
        batch.End();
    }

    private static void SetBlurParameters(XnaEffect effect, float dx, float dy)
    {
        const int sampleCount = 15;
        var offsets = new Vector2[sampleCount];
        var weights = new float[sampleCount];

        // Gaussian weights
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
        _bloomRT1?.Dispose(); _bloomRT1 = null;
        _bloomRT2?.Dispose(); _bloomRT2 = null;
        _initialized = false;
    }
}
