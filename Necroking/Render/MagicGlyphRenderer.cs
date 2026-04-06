using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Necroking.Core;
using Necroking.GameSystems;
using GfxEffect = Microsoft.Xna.Framework.Graphics.Effect;

namespace Necroking.Render;

public class MagicGlyphRenderer
{
    private GfxEffect? _effect;
    private SpriteBatch _spriteBatch = null!;
    private Texture2D _pixel = null!;
    private Texture2D _glowTex = null!;
    private Camera25D _camera = null!;
    private Renderer _renderer = null!;
    private Dictionary<string, Flipbook>? _flipbooks;
    private float _gameTime;

    public void LoadEffect(GfxEffect? effect)
    {
        _effect = effect;
    }

    public void SetContext(SpriteBatch spriteBatch, Texture2D pixel, Texture2D glowTex,
        Camera25D camera, Renderer renderer, Dictionary<string, Flipbook> flipbooks,
        float gameTime)
    {
        _spriteBatch = spriteBatch;
        _pixel = pixel;
        _glowTex = glowTex;
        _camera = camera;
        _renderer = renderer;
        _flipbooks = flipbooks;
        _gameTime = gameTime;
    }

    /// <summary>
    /// Draw glyph circles on the ground. Call during ground layer pass.
    /// </summary>
    public void DrawGround(MagicGlyphSystem glyphSystem)
    {
        if (glyphSystem.Glyphs.Count == 0) return;

        // End current batch to switch to shader mode
        _spriteBatch.End();

        foreach (var glyph in glyphSystem.Glyphs)
        {
            if (!glyph.Alive) continue;
            if (_effect != null)
                DrawGlyphShader(glyph);
        }

        // Resume normal alpha blend batch
        _spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp);
    }

    /// <summary>
    /// Draw energy columns for active glyphs. Call during additive blend pass.
    /// </summary>
    public void DrawEnergyColumns(MagicGlyphSystem glyphSystem)
    {
        foreach (var glyph in glyphSystem.Glyphs)
        {
            if (!glyph.Alive) continue;
            if (glyph.RibbonIntensity > 0.01f)
                DrawEnergyColumn(glyph);
        }
    }

    private void DrawGlyphShader(MagicGlyph glyph)
    {
        var center = _renderer.WorldToScreen(glyph.Position, 0f, _camera);

        float screenRadiusX = glyph.Radius * _camera.Zoom;
        float screenRadiusY = screenRadiusX * _camera.YRatio;

        float pad = 1.1f;
        int quadW = (int)(screenRadiusX * 2f * pad);
        int quadH = (int)(screenRadiusY * 2f * pad);
        int quadX = (int)(center.X - quadW / 2f);
        int quadY = (int)(center.Y - quadH / 2f);

        if (quadW < 2 || quadH < 2) return;

        var c1 = glyph.Color;
        var c2 = glyph.Color2;
        _effect!.Parameters["Time"]?.SetValue(_gameTime);
        _effect.Parameters["Activation"]?.SetValue(glyph.Activation);
        _effect.Parameters["Intensity"]?.SetValue(glyph.Intensity * c1.Intensity);
        _effect.Parameters["GlyphColor"]?.SetValue(new Vector3(c1.R / 255f, c1.G / 255f, c1.B / 255f));
        _effect.Parameters["GlyphColor2"]?.SetValue(new Vector3(c2.R / 255f, c2.G / 255f, c2.B / 255f));
        _effect.Parameters["Rotation"]?.SetValue(_gameTime * glyph.RotationSpeed);
        _effect.Parameters["PulseSpeed"]?.SetValue(glyph.PulseSpeed);

        _spriteBatch.Begin(SpriteSortMode.Immediate, BlendState.AlphaBlend,
            SamplerState.PointClamp, null, null, _effect);

        _spriteBatch.Draw(_pixel, new Rectangle(quadX, quadY, quadW, quadH), Color.White);

        _spriteBatch.End();
    }

    // Total number of ribbon spawn points: 20 pentagram + 18 circle = 38
    private const int PentagramRibbons = 20;
    private const int CircleRibbons = 18;
    private const int RibbonCount = PentagramRibbons + CircleRibbons;
    // How long each ribbon's rise cycle takes before it respawns
    private const float RibbonCycleTime = 1.2f;

    private void DrawEnergyColumn(MagicGlyph glyph)
    {
        var center = _renderer.WorldToScreen(glyph.Position, 0f, _camera);
        float ribbonPower = glyph.RibbonIntensity; // 1→0 decay
        float screenRadius = glyph.Radius * _camera.Zoom;

        // Max rise height ≈ necromancer height
        float maxRiseHeight = 2.0f * _camera.Zoom;

        var c1 = glyph.Color;
        var c2 = glyph.Color2;
        float rot = _gameTime * glyph.RotationSpeed;
        float slowRot = _gameTime * 0.2f;

        // At full power, draw 2x ribbons. As power decays, fewer are visible
        int activeRibbons = (int)(RibbonCount * 2 * ribbonPower);
        activeRibbons = Math.Clamp(activeRibbons, 0, RibbonCount * 2);

        for (int i = 0; i < activeRibbons; i++)
        {
            // Each ribbon has a unique phase offset so they stagger
            // For indices beyond base count, offset the seed so they don't overlap
            int baseIdx = i % RibbonCount;
            float extraOffset = (i >= RibbonCount) ? 17.3f : 0f;
            float seed = baseIdx * 3.7f + 0.5f + extraOffset;

            // Cycle: each ribbon rises independently on its own timer
            float cycleOffset = seed * 0.43f; // stagger start times
            float t = ((_gameTime + cycleOffset) % RibbonCycleTime) / RibbonCycleTime; // 0→1

            // Spawn position on the pentagram/circle geometry
            float spawnX, spawnY;
            if (baseIdx < PentagramRibbons)
            {
                // Pentagram lines: 5 lines, multiple ribbons per line
                int perLine = PentagramRibbons / 5;
                int lineIdx = baseIdx / perLine;
                float lineT = 0.15f + 0.7f * ((baseIdx % perLine) + 0.5f) / perLine;
                // Extra ribbons get slightly offset position along the line
                if (i >= RibbonCount) lineT = MathF.Min(0.85f, lineT + 0.1f);
                // Pentagram connects every-other vertex: 0→2, 2→4, 4→1, 1→3, 3→0
                int[] from = { 0, 2, 4, 1, 3 };
                int[] to   = { 2, 4, 1, 3, 0 };
                float a1 = rot + slowRot + from[lineIdx] * MathF.PI * 2f / 5f - MathF.PI / 2f;
                float a2 = rot + slowRot + to[lineIdx] * MathF.PI * 2f / 5f - MathF.PI / 2f;
                // Position along the line in normalized circle space
                float px = MathHelper.Lerp(MathF.Cos(a1), MathF.Cos(a2), lineT) * 0.70f;
                float py = MathHelper.Lerp(MathF.Sin(a1), MathF.Sin(a2), lineT) * 0.70f;
                // Convert to screen space (apply isometric squash)
                spawnX = px * screenRadius;
                spawnY = py * screenRadius * _camera.YRatio;
            }
            else
            {
                // Circle: distribute along outer and inner rings
                int circleIdx = baseIdx - PentagramRibbons;
                float ca = circleIdx * MathF.PI * 2f / CircleRibbons + slowRot * 0.5f + seed * 0.1f;
                float ringR = (circleIdx % 2 == 0) ? 0.90f : 0.70f;
                spawnX = MathF.Cos(ca) * ringR * screenRadius;
                spawnY = MathF.Sin(ca) * ringR * screenRadius * _camera.YRatio;
            }

            // Ribbon vertical extent
            float ribbonHeight = maxRiseHeight * (0.5f + 0.5f * MathF.Abs(SimplexNoise.Noise2D(seed, 0f)));

            // Bottom of ribbon rises, creating the "cut off" effect at the base
            // Top also rises, and fades out
            float bottomY = -t * ribbonHeight * 0.6f;        // base lifts off
            float topY = -(t * ribbonHeight + ribbonHeight * 0.4f); // top extends above

            // Ribbon only visible for part of its cycle
            // Fade in quickly at start, cut off and fade at end
            float fadeIn = smoothstep(0f, 0.15f, t);
            float fadeOut = smoothstep(1f, 0.7f, t);
            float ribbonAlpha = fadeIn * fadeOut * ribbonPower;
            if (ribbonAlpha < 0.01f) continue;

            // Slight horizontal wobble
            float wobble = MathF.Sin(_gameTime * 4f + seed * 2.3f) * screenRadius * 0.03f;

            // Ribbon width — wider at burst, narrows as power decays
            float widthMult = 0.5f + 1.5f * ribbonPower; // 2x at burst, 0.5x at end
            float ribbonW = screenRadius * 0.06f * widthMult * (0.7f + 0.3f * MathF.Abs(SimplexNoise.Noise2D(seed * 1.3f, _gameTime * 0.5f)));
            int pixW = Math.Max(2, (int)(ribbonW * 2f));

            // Screen position
            float sx = center.X + spawnX + wobble;
            float syBottom = center.Y + spawnY + bottomY;
            float syTop = center.Y + spawnY + topY;
            int pixH = Math.Max(2, (int)(syBottom - syTop));

            // Color: lerp primary → secondary as it rises
            float colorT = t;
            int cr = (int)MathHelper.Lerp(c1.R, c2.R, colorT);
            int cg = (int)MathHelper.Lerp(c1.G, c2.G, colorT);
            int cb = (int)MathHelper.Lerp(c1.B, c2.B, colorT);
            int ca2 = (int)(ribbonAlpha * 160f);
            ca2 = Math.Clamp(ca2, 0, 255);

            var color = new Color(
                Math.Clamp(cr, 0, 255),
                Math.Clamp(cg, 0, 255),
                Math.Clamp(cb, 0, 255),
                ca2);

            _spriteBatch.Draw(_glowTex,
                new Rectangle((int)(sx - pixW / 2), (int)syTop, pixW, pixH),
                color);
        }
    }

    private static float smoothstep(float edge0, float edge1, float x)
    {
        float t = Math.Clamp((x - edge0) / (edge1 - edge0), 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    // Legacy single-draw entry point
    public void Draw(MagicGlyphSystem glyphSystem) => DrawGround(glyphSystem);
}
