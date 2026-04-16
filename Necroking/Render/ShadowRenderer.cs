using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Necroking.Core;
using Necroking.Data;
using Necroking.Data.Registries;
using Necroking.GameSystems;
using Necroking.World;

namespace Necroking.Render;

public class ShadowRenderer
{
    private BasicEffect? _shadowEffect;
    private readonly VertexPositionColorTexture[] _shadowVerts = new VertexPositionColorTexture[4];
    private static readonly short[] ShadowIndices = { 0, 1, 2, 0, 2, 3 };

    internal void Init(GraphicsDevice device)
    {
        _shadowEffect = new BasicEffect(device)
        {
            TextureEnabled = true,
            VertexColorEnabled = true,
            LightingEnabled = false,
        };
    }

    internal void Draw(
        GraphicsDevice device,
        SpriteBatch spriteBatch,
        Texture2D glowTex,
        Camera25D camera,
        Renderer renderer,
        Simulation sim,
        GameData gameData,
        Dictionary<uint, Game1.UnitAnimData> unitAnims,
        SpriteAtlas[] atlases,
        EnvironmentSystem envSystem)
    {
        var shadow = gameData.Settings.Shadow;
        if (!shadow.Enabled) return;

        bool useShader = (UnitShadowMode)shadow.UnitShadowMode == UnitShadowMode.Shader;

        if (useShader)
            DrawShaderShadows(device, spriteBatch, glowTex, camera, renderer, sim, gameData, unitAnims, atlases, envSystem, shadow);
        else
            DrawEllipseShadows(spriteBatch, glowTex, camera, renderer, sim, gameData, envSystem, shadow);
    }

    private void DrawEllipseShadows(
        SpriteBatch spriteBatch,
        Texture2D glowTex,
        Camera25D camera,
        Renderer renderer,
        Simulation sim,
        GameData gameData,
        EnvironmentSystem envSystem,
        ShadowSettings shadow)
    {
        // C++ style: two concentric soft ellipses per unit at ground position
        byte outerAlpha = (byte)Math.Clamp(shadow.Opacity * 255f * 0.6f, 0, 255);
        byte innerAlpha = (byte)Math.Clamp(shadow.Opacity * 255f * 0.35f, 0, 255);
        var outerColor = new Color((byte)0, (byte)0, (byte)0, outerAlpha);
        var innerColor = new Color((byte)0, (byte)0, (byte)0, innerAlpha);

        for (int i = 0; i < sim.Units.Count; i++)
        {
            if (!sim.Units[i].Alive) continue;
            float unitRadius = sim.Units[i].Radius;
            var worldPos = sim.Units[i].Position;
            var sp = renderer.WorldToScreen(worldPos, 0f, camera);

            float r = unitRadius * camera.Zoom * 0.8f;
            float ry = r * camera.YRatio;

            // Outer soft ellipse
            spriteBatch.Draw(glowTex,
                new Rectangle((int)(sp.X - r), (int)(sp.Y - ry * 0.5f), (int)(r * 2), (int)ry),
                outerColor);
            // Inner darker ellipse (60% size)
            float ir = r * 0.6f;
            float iry = ry * 0.6f;
            spriteBatch.Draw(glowTex,
                new Rectangle((int)(sp.X - ir), (int)(sp.Y - iry * 0.5f), (int)(ir * 2), (int)iry),
                innerColor);
        }

        // Corpse shadows (fade with dissolve)
        foreach (var corpse in sim.Corpses)
        {
            var unitDef = gameData.Units.Get(corpse.UnitDefID);
            if (unitDef == null) continue;
            float radius = unitDef.Radius > 0 ? unitDef.Radius : 0.495f;
            var sp = renderer.WorldToScreen(corpse.Position, 0f, camera);
            float r = radius * camera.Zoom * 0.8f;
            float ry = r * camera.YRatio;

            float dissolveAlpha = corpse.Dissolving ? MathF.Max(0f, 1f - corpse.DissolveTimer / 2f) : 1f;
            byte coa = (byte)(outerAlpha * dissolveAlpha);
            byte cia = (byte)(innerAlpha * dissolveAlpha);

            spriteBatch.Draw(glowTex,
                new Rectangle((int)(sp.X - r), (int)(sp.Y - ry * 0.5f), (int)(r * 2), (int)ry),
                new Color((byte)0, (byte)0, (byte)0, coa));
            float ir2 = r * 0.6f;
            float iry2 = ry * 0.6f;
            spriteBatch.Draw(glowTex,
                new Rectangle((int)(sp.X - ir2), (int)(sp.Y - iry2 * 0.5f), (int)(ir2 * 2), (int)iry2),
                new Color((byte)0, (byte)0, (byte)0, cia));
        }

        // Environment object shadows (ellipse at ground pivot, skip collected)
        for (int i = 0; i < envSystem.ObjectCount; i++)
        {
            if (!envSystem.IsObjectVisible(i)) continue;
            var obj = envSystem.Objects[i];
            var def = envSystem.Defs[obj.DefIndex];
            var tex = envSystem.GetDefTexture(obj.DefIndex);
            if (tex == null) continue;

            // Use per-frame dimensions for animated spritesheets
            float texW = tex.Width;
            float texH = tex.Height;
            if (def.IsAnimated && def.AnimTotalFrames > 1)
            {
                texW = tex.Width / (float)Math.Max(def.AnimFramesX, 1);
                texH = tex.Height / (float)Math.Max(def.AnimFramesY, 1);
            }

            float worldH = def.SpriteWorldHeight * obj.Scale * def.Scale;
            float scale = worldH * camera.Zoom / texH;
            float objW = texW * scale;
            float r = objW * 0.4f;
            float ry = r * camera.YRatio;

            var sp = renderer.WorldToScreen(new Vec2(obj.X, obj.Y), 0f, camera);

            spriteBatch.Draw(glowTex,
                new Rectangle((int)(sp.X - r), (int)(sp.Y - ry * 0.5f), (int)(r * 2), (int)ry),
                outerColor);
            float ir3 = r * 0.6f;
            float iry3 = ry * 0.6f;
            spriteBatch.Draw(glowTex,
                new Rectangle((int)(sp.X - ir3), (int)(sp.Y - iry3 * 0.5f), (int)(ir3 * 2), (int)iry3),
                innerColor);
        }
    }

    private void DrawShaderShadows(
        GraphicsDevice device,
        SpriteBatch spriteBatch,
        Texture2D glowTex,
        Camera25D camera,
        Renderer renderer,
        Simulation sim,
        GameData gameData,
        Dictionary<uint, Game1.UnitAnimData> unitAnims,
        SpriteAtlas[] atlases,
        EnvironmentSystem envSystem,
        ShadowSettings shadow)
    {
        // Projected shadows as skewed parallelogram quads (matching C++ implementation).
        // Bottom edge sits at feet, top edge shifted by sun direction vector.
        float sunRad = shadow.SunAngle * MathF.PI / 180f;
        float sdxDir = MathF.Cos(sunRad);
        float sdyDir = MathF.Sin(sunRad);
        byte shAlpha = (byte)Math.Clamp(shadow.Opacity * 255f, 0, 255);
        var shadowColor = new Color((byte)0, (byte)0, (byte)0, shAlpha);

        // End SpriteBatch, draw quads with BasicEffect, then resume SpriteBatch
        spriteBatch.End();

        // Set up BasicEffect for shadow quads
        _shadowEffect!.Projection = Matrix.CreateOrthographicOffCenter(
            0, device.Viewport.Width, device.Viewport.Height, 0, 0, 1);
        _shadowEffect.View = Matrix.Identity;
        _shadowEffect.World = Matrix.Identity;
        _shadowEffect.Alpha = 1f;

        device.BlendState = BlendState.AlphaBlend;
        device.SamplerStates[0] = SamplerState.LinearClamp;
        device.RasterizerState = RasterizerState.CullNone;

        // Unit shadows
        for (int i = 0; i < sim.Units.Count; i++)
        {
            if (!sim.Units[i].Alive) continue;
            uint uid = sim.Units[i].Id;
            if (!unitAnims.TryGetValue(uid, out var animData)) continue;

            var unitDef = gameData.Units.Get(sim.Units[i].UnitDefID);
            if (unitDef == null) continue;

            var atlas = atlases[(int)animData.AtlasID];
            if (!atlas.IsLoaded || atlas.Texture == null) continue;

            var fr = animData.Ctrl.GetCurrentFrame(sim.Units[i].FacingAngle);
            if (fr.Frame == null) continue;

            float worldH = (unitDef.SpriteWorldHeight > 0 ? unitDef.SpriteWorldHeight : 1.8f) * sim.Units[i].SpriteScale;
            float pixelH = worldH * camera.Zoom;
            float scale = pixelH / animData.RefFrameHeight;

            var srcRect = fr.Frame.Value.Rect;
            float destW = srcRect.Width * scale;
            float destH = srcRect.Height * scale;
            float shadowH = destH * shadow.Squash;

            float anchorX = fr.FlipX ? (1f - fr.Frame.Value.PivotX) : fr.Frame.Value.PivotX;
            float leftOff = destW * anchorX;
            float rightOff = destW * (1f - anchorX);

            float swLen = worldH * shadow.LengthScale * camera.Zoom;
            float sdx = sdxDir * swLen;
            float sdy = sdyDir * swLen * camera.YRatio;

            var feetSp = renderer.WorldToScreen(sim.Units[i].Position, 0f, camera);

            // UV coordinates from atlas
            float texW = atlas.Texture.Width;
            float texH = atlas.Texture.Height;
            float u0 = srcRect.X / texW;
            float v0 = srcRect.Y / texH;
            float u1 = (srcRect.X + srcRect.Width) / texW;
            float v1 = (srcRect.Y + srcRect.Height) / texH;
            if (fr.FlipX) { (u0, u1) = (u1, u0); }

            DrawShadowQuad(device, atlas.Texture, feetSp.X, feetSp.Y,
                leftOff, rightOff, shadowH, sdx, sdy,
                u0, v0, u1, v1, shadowColor);
        }

        // Environment object shadows (skip collected foragables)
        for (int i = 0; i < envSystem.ObjectCount; i++)
        {
            if (!envSystem.IsObjectVisible(i)) continue;
            var obj = envSystem.Objects[i];
            var def = envSystem.Defs[obj.DefIndex];
            if (def.ShadowType == 2) continue; // None — skip entirely

            var tex = envSystem.GetDefTexture(obj.DefIndex);
            if (tex == null) continue;

            // Use per-frame dimensions for animated spritesheets
            float fTexW = tex.Width;
            float fTexH = tex.Height;
            float u0 = 0f, v0 = 0f, u1 = 1f, v1 = 1f;
            if (def.IsAnimated && def.AnimTotalFrames > 1)
            {
                // Shadow uses first frame only (no animation on shadows)
                var frameRect = def.GetAnimFrameRect(tex.Width, tex.Height, 0);
                fTexW = frameRect.Width;
                fTexH = frameRect.Height;
                u0 = frameRect.X / (float)tex.Width;
                v0 = frameRect.Y / (float)tex.Height;
                u1 = (frameRect.X + frameRect.Width) / (float)tex.Width;
                v1 = (frameRect.Y + frameRect.Height) / (float)tex.Height;
            }

            float worldH = def.SpriteWorldHeight * obj.Scale * def.Scale;
            float pixelH = worldH * camera.Zoom;
            float scale = pixelH / fTexH;
            float destW = fTexW * scale;
            float destH = pixelH;

            var feetSp = renderer.WorldToScreen(new Vec2(obj.X, obj.Y), 0f, camera);

            if (def.ShadowType == 1)
            {
                // Diffuse ellipse — simple oval at feet using the glow texture
                float ellipseW = destW * 0.7f;
                float ellipseH = ellipseW * 0.4f * camera.YRatio;
                DrawShadowQuad(device, glowTex ?? tex, feetSp.X, feetSp.Y,
                    ellipseW * 0.5f, ellipseW * 0.5f, ellipseH, 0f, 0f,
                    0f, 0f, 1f, 1f, shadowColor);
            }
            else
            {
                // Sprite projection (default)
                float shadowH = destH * shadow.Squash;
                float leftOff = destW * def.PivotX;
                float rightOff = destW * (1f - def.PivotX);

                float swLen = worldH * shadow.LengthScale * camera.Zoom;
                float sdx = sdxDir * swLen;
                float sdy = sdyDir * swLen * camera.YRatio;

                DrawShadowQuad(device, tex, feetSp.X, feetSp.Y,
                    leftOff, rightOff, shadowH, sdx, sdy,
                    u0, v0, u1, v1, shadowColor);
            }
        }

        // Resume SpriteBatch
        spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.LinearClamp);
    }

    /// <summary>
    /// Draw a shadow as a textured parallelogram: bottom edge at feet, top edge skewed by sun offset.
    /// Matches the C++ rlBegin(RL_QUADS) shadow rendering.
    /// </summary>
    private void DrawShadowQuad(GraphicsDevice device, Texture2D texture, float feetX, float feetY,
        float leftOff, float rightOff, float shadowH, float sdx, float sdy,
        float u0, float v0, float u1, float v1, Color color)
    {
        // Bottom-left/right at feet level
        float blX = feetX - leftOff;
        float blY = feetY;
        float brX = feetX + rightOff;
        float brY = feetY;

        // Top-left/right shifted by sun direction
        float tlX = feetX - leftOff + sdx;
        float tlY = feetY - shadowH + sdy;
        float trX = feetX + rightOff + sdx;
        float trY = feetY - shadowH + sdy;

        _shadowVerts[0] = new VertexPositionColorTexture(new Vector3(tlX, tlY, 0), color, new Vector2(u0, v0));
        _shadowVerts[1] = new VertexPositionColorTexture(new Vector3(blX, blY, 0), color, new Vector2(u0, v1));
        _shadowVerts[2] = new VertexPositionColorTexture(new Vector3(brX, brY, 0), color, new Vector2(u1, v1));
        _shadowVerts[3] = new VertexPositionColorTexture(new Vector3(trX, trY, 0), color, new Vector2(u1, v0));

        _shadowEffect!.Texture = texture;
        foreach (var pass in _shadowEffect.CurrentTechnique.Passes)
        {
            pass.Apply();
            device.DrawUserIndexedPrimitives(
                PrimitiveType.TriangleList, _shadowVerts, 0, 4, ShadowIndices, 0, 2);
        }
    }
}
