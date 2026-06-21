using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Audio;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using Necroking.Data;
using Necroking.Data.Registries;
using Necroking.Core;
using Necroking.Render;
using Necroking.Movement;
using Necroking.Game;
using Necroking.GameSystems;
using Necroking.World;
using Necroking.Scenario;
using Necroking.Editor;
using Necroking.UI;

namespace Necroking;

// Game1 partial: Terrain, effects and projectile rendering.
public partial class Game1
{
    private void DrawSoulOrbs()
    {
        var orbs = _sim.SoulOrbs;
        for (int i = 0; i < orbs.Count; i++)
        {
            var orb = orbs[i];
            var sp = _renderer.WorldToScreen(orb.Position, 0.5f, _camera);

            // Outer purple glow
            float outerR = 6f;
            _spriteBatch.Draw(_pixel, new Vector2(sp.X - outerR, sp.Y - outerR), null,
                Color.FromNonPremultiplied(120, 40, 180, 80), 0f, Vector2.Zero,
                new Vector2(outerR * 2, outerR * 2), SpriteEffects.None, 0f);

            // Inner white bright
            float innerR = 2f;
            _spriteBatch.Draw(_pixel, new Vector2(sp.X - innerR, sp.Y - innerR), null,
                Color.FromNonPremultiplied(255, 255, 255, 200), 0f, Vector2.Zero,
                new Vector2(innerR * 2, innerR * 2), SpriteEffects.None, 0f);
        }
    }

    private void DrawGround()
    {
        int worldW = _groundSystem.WorldW > 0 ? _groundSystem.WorldW : WorldSize;
        int worldH = _groundSystem.WorldH > 0 ? _groundSystem.WorldH : WorldSize;

        // Try shader-based ground rendering
        if (_groundEffect != null && _groundVertexMapTex != null && _groundSystem.TypeCount > 0)
        {
            DrawGroundShader(worldW, worldH);
            return;
        }

        // Fallback: per-tile texture rendering
        float viewLeft = _camera.Position.X - _renderer.ScreenW / (2f * _camera.Zoom) - 1;
        float viewRight = _camera.Position.X + _renderer.ScreenW / (2f * _camera.Zoom) + 1;
        float viewTop = _camera.Position.Y - _renderer.ScreenH / (_camera.Zoom * _camera.YRatio) - 1;
        float viewBottom = _camera.Position.Y + _renderer.ScreenH / (_camera.Zoom * _camera.YRatio) + 1;

        int minX = Math.Max(0, (int)viewLeft);
        int maxX = Math.Min(worldW - 1, (int)viewRight);
        int minY = Math.Max(0, (int)viewTop);
        int maxY = Math.Min(worldH - 1, (int)viewBottom);

        for (int y = minY; y <= maxY; y++)
        {
            for (int x = minX; x <= maxX; x++)
            {
                var sp = _renderer.WorldToScreen(new Vec2(x, y), 0f, _camera);
                float tileW = _camera.Zoom;
                float tileH = _camera.Zoom * _camera.YRatio;

                byte groundType = _groundSystem.GetVertex(x, y);
                var tex = _groundSystem.GetTexture(groundType);

                if (tex != null)
                {
                    // Tile the ground texture — use float dest rect via Vector2 position + scale
                    float texScale = 8f;
                    int srcX = (int)((x % texScale) / texScale * tex.Width);
                    int srcY = (int)((y % texScale) / texScale * tex.Height);
                    int srcW = (int)(tex.Width / texScale);
                    int srcH = (int)(tex.Height / texScale);
                    if (srcW < 1) srcW = 1;
                    if (srcH < 1) srcH = 1;
                    var srcRect = new Rectangle(srcX, srcY, srcW, srcH);
                    // Use float-based destination to avoid jitter
                    var destPos = new Vector2(sp.X, sp.Y);
                    var destScale = new Vector2((tileW + 0.5f) / srcW, (tileH + 0.5f) / srcH);
                    _spriteBatch.Draw(tex, destPos, srcRect, Color.White, 0f, Vector2.Zero, destScale, SpriteEffects.None, 0f);
                }
                else
                {
                    var color = groundType switch
                    {
                        0 => new Color(55, 95, 45),
                        1 => new Color(90, 75, 50),
                        2 => new Color(70, 65, 55),
                        _ => new Color(55, 95, 45)
                    };
                    _spriteBatch.Draw(_pixel, sp, null, color, 0f, Vector2.Zero, new Vector2(tileW + 0.5f, tileH + 0.5f), SpriteEffects.None, 0f);
                }
            }
        }
    }

    private void DrawGroundShader(int worldW, int worldH)
    {
        _groundDrawStopwatch.Restart();
        // End the current SpriteBatch and start a new one with the ground shader
        _spriteBatch.End();

        // Set shader parameters
        _groundEffect!.Parameters["AmbientColor"]?.SetValue(new Vector3(_ambientColor.R / 255f, _ambientColor.G / 255f, _ambientColor.B / 255f));
        _groundEffect.Parameters["CameraPos"]?.SetValue(new Vector2(_camera.Position.X, _camera.Position.Y));
        _groundEffect.Parameters["Zoom"]?.SetValue(_camera.Zoom);
        _groundEffect.Parameters["YRatio"]?.SetValue(_camera.YRatio);
        _groundEffect.Parameters["ScreenSize"]?.SetValue(new Vector2(_renderer.ScreenW, _renderer.ScreenH));
        _groundEffect.Parameters["WorldSize"]?.SetValue(new Vector2(_groundSystem.VertexW, _groundSystem.VertexH));
        _groundEffect.Parameters["TypeWarpStrength"]?.SetValue(_groundSystem.TypeWarpStrength);
        _groundEffect.Parameters["UvWarpAmp"]?.SetValue(_groundSystem.UvWarpAmp);
        _groundEffect.Parameters["UvWarpFreq"]?.SetValue(_groundSystem.UvWarpFreq);
        _groundEffect.Parameters["Time"]?.SetValue(_gameTime);

        // Per-type uniforms: tint (defaults white) and water-animation flag.
        // Shader treats array slots 0..7; unused slots are harmless defaults.
        // Per-ground-type uniform arrays. Indexed by ground-type id (0..31)
        // matching the bottom 5 bits of the tilemap byte; the top 3 bits hold
        // the texture-slot id (0..7) which drives the shader cascade. The
        // tint/iswater arrays must match the shader's array length (32 in
        // GroundShader.fx).
        const int MaxGroundTypes = 16;
        const int MaxTextureSlots = 8;
        var tintArr = new Vector4[MaxGroundTypes];
        var waterArr = new float[MaxGroundTypes];
        for (int i = 0; i < MaxGroundTypes; i++) { tintArr[i] = Vector4.One; waterArr[i] = 0f; }
        int typeCap = Math.Min(_groundSystem.TypeCount, MaxGroundTypes);
        for (int i = 0; i < typeCap; i++)
        {
            var def = _groundSystem.GetTypeDef(i);
            tintArr[i] = def.TintColor.ToVector4();
            waterArr[i] = (def.MovementTerrain == Necroking.World.TerrainType.ShallowWater
                        || def.MovementTerrain == Necroking.World.TerrainType.DeepWater) ? 1f : 0f;
        }
        _groundEffect.Parameters["TintColors"]?.SetValue(tintArr);
        _groundEffect.Parameters["IsWaterType"]?.SetValue(waterArr);

        // Bind unique ground textures via Effect.Parameters (named texture params, not register slots).
        // Shader cascade supports MaxTextureSlots unique texture slots; types past those reuse slot 0 fallback.
        string[] texParamNames = {
            "GroundTexture0", "GroundTexture1", "GroundTexture2", "GroundTexture3",
            "GroundTexture4", "GroundTexture5", "GroundTexture6", "GroundTexture7",
        };
        int slotCap = Math.Min(_groundSystem.UniqueTextureCount, Math.Min(texParamNames.Length, MaxTextureSlots));
        for (int i = 0; i < slotCap; i++)
        {
            var tex = _groundSystem.GetUniqueTexture(i);
            if (tex != null)
                _groundEffect.Parameters[texParamNames[i]]?.SetValue(tex);
        }

        _spriteBatch.Begin(SpriteSortMode.Immediate, BlendState.Opaque, SamplerState.PointClamp,
            null, null, _groundEffect);

        // SpriteBatch.Draw binds _groundVertexMapTex to slot 0 (= TilemapSampler)
        _spriteBatch.Draw(_groundVertexMapTex!, new Rectangle(0, 0, _renderer.ScreenW, _renderer.ScreenH), Color.White);
        _spriteBatch.End();

        // Resume normal SpriteBatch (premultiplied alpha)
        _spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.LinearClamp);

        _groundDrawStopwatch.Stop();
        double groundDrawMs = _groundDrawStopwatch.Elapsed.TotalMilliseconds;
        const double GroundEmaAlpha = 0.1;
        _groundMsAvg = _groundMsAvg * (1.0 - GroundEmaAlpha) + groundDrawMs * GroundEmaAlpha;

        if (_activeScenario is Scenario.Scenarios.PerfWaterScenario perf)
            perf.LastGroundMs = groundDrawMs;
    }

    private void DrawRoads()
    {
        var roads = _roadSystem.Roads;
        var junctions = _roadSystem.Junctions;
        if (roads.Count == 0 && junctions.Count == 0) return;

        var roadColor = MultiplyColor(new Color(100, 90, 80), _ambientColor);

        // Draw road segments using Catmull-Rom interpolation
        foreach (var road in roads)
        {
            var pts = road.Points;
            if (pts.Count < 2) continue;

            for (int seg = 0; seg < pts.Count - 1; seg++)
            {
                // Get the 4 control points for Catmull-Rom (clamping at edges)
                var p0 = pts[Math.Max(0, seg - 1)].Position;
                var p1 = pts[seg].Position;
                var p2 = pts[seg + 1].Position;
                var p3 = pts[Math.Min(pts.Count - 1, seg + 2)].Position;

                float w1 = pts[seg].Width;
                float w2 = pts[seg + 1].Width;

                const int subdivisions = 10;
                Vec2 prev = p1;
                float prevW = w1;

                for (int s = 1; s <= subdivisions; s++)
                {
                    float t = s / (float)subdivisions;
                    var cur = RoadSystem.CatmullRom(p0, p1, p2, p3, t);
                    float curW = w1 + (w2 - w1) * t;

                    // Convert to screen space
                    var screenA = _renderer.WorldToScreen(prev, 0f, _camera);
                    var screenB = _renderer.WorldToScreen(cur, 0f, _camera);

                    float dx = screenB.X - screenA.X;
                    float dy = screenB.Y - screenA.Y;
                    float segLen = MathF.Sqrt(dx * dx + dy * dy);
                    if (segLen < 0.1f) { prev = cur; prevW = curW; continue; }

                    float angle = MathF.Atan2(dy, dx);
                    float avgWidth = (prevW + curW) * 0.5f * _camera.Zoom;

                    _spriteBatch.Draw(_pixel, screenA, null, roadColor,
                        angle, new Vector2(0, 0.5f), new Vector2(segLen + 1f, avgWidth), SpriteEffects.None, 0f);

                    prev = cur;
                    prevW = curW;
                }
            }
        }

        // Draw junctions as filled circle approximations
        foreach (var junc in junctions)
        {
            var sp = _renderer.WorldToScreen(junc.Position, 0f, _camera);
            float radius = junc.Radius * _camera.Zoom;
            int r = Math.Max(2, (int)radius);

            // Draw circle as a series of horizontal lines
            for (int dy = -r; dy <= r; dy++)
            {
                float halfW = MathF.Sqrt(r * r - dy * dy);
                int x0 = (int)(sp.X - halfW);
                int w = (int)(halfW * 2f);
                if (w < 1) w = 1;
                _spriteBatch.Draw(_pixel, new Rectangle(x0, (int)sp.Y + dy, w, 1), roadColor);
            }
        }
    }

    private void DrawWalls()
    {
        if (_wallSystem.Width == 0 || _wallSystem.Height == 0 || _wallSystem.DefCount == 0) return;

        // View culling bounds (same approach as DrawGround)
        float viewLeft = _camera.Position.X - _renderer.ScreenW / (2f * _camera.Zoom) - 1;
        float viewRight = _camera.Position.X + _renderer.ScreenW / (2f * _camera.Zoom) + 1;
        float viewTop = _camera.Position.Y - _renderer.ScreenH / (_camera.Zoom * _camera.YRatio) - 1;
        float viewBottom = _camera.Position.Y + _renderer.ScreenH / (_camera.Zoom * _camera.YRatio) + 1;

        int minX = Math.Max(0, (int)viewLeft);
        int maxX = Math.Min(_wallSystem.Width - 1, (int)viewRight);
        int minY = Math.Max(0, (int)viewTop);
        int maxY = Math.Min(_wallSystem.Height - 1, (int)viewBottom);

        float tileW = _camera.Zoom;
        float tileH = _camera.Zoom * _camera.YRatio;

        for (int y = minY; y <= maxY; y++)
        {
            for (int x = minX; x <= maxX; x++)
            {
                if (!_wallSystem.IsAlive(x, y)) continue;

                byte wallType = _wallSystem.GetWallType(x, y);
                if (wallType == 0 || wallType > _wallSystem.DefCount) continue;

                var def = _wallSystem.Defs[wallType - 1];
                var sp = _renderer.WorldToScreen(new Vec2(x, y), 0f, _camera);

                // Draw colored rectangle as placeholder (using def's Color)
                // Make wall tiles slightly taller to give a wall appearance
                float wallH = tileH * 1.5f;
                var wallColor = MultiplyColor(def.Color, _ambientColor);
                _spriteBatch.Draw(_pixel, new Vector2(sp.X, sp.Y - wallH + tileH), null,
                    wallColor, 0f, Vector2.Zero,
                    new Vector2(tileW + 0.5f, wallH), SpriteEffects.None, 0f);

                // Draw a darker top edge for depth effect
                var darkColor = new Color(
                    (byte)(wallColor.R * 0.6f),
                    (byte)(wallColor.G * 0.6f),
                    (byte)(wallColor.B * 0.6f),
                    wallColor.A);
                _spriteBatch.Draw(_pixel, new Vector2(sp.X, sp.Y - wallH + tileH), null,
                    darkColor, 0f, Vector2.Zero,
                    new Vector2(tileW + 0.5f, 2f), SpriteEffects.None, 0f);
            }
        }
    }

    /// <summary>Draw ground-layer objects (traps) — above dirt, below grass/units.</summary>
    private void DrawGroundLayerObjects()
    {
        for (int i = 0; i < _envSystem.ObjectCount; i++)
        {
            if (!_envSystem.IsObjectVisible(i)) continue;
            var obj = _envSystem.Objects[i];
            var def = _envSystem.Defs[obj.DefIndex];
            if (def.Category != "Traps") continue;
            DrawSingleEnvObject(i);
        }
    }

    private void DrawProjectiles()
    {
        foreach (var proj in _sim.Projectiles.Projectiles)
        {
            if (!proj.Alive) continue;
            // Fireballs are drawn in the additive HDR pass (DrawProjectilesHdr)
            if (proj.Type == ProjectileType.Fireball) continue;
            // Fog of war: hide projectile if its current tile isn't visible.
            if (!_fogOfWar.IsVisible(proj.Position)) continue;

            var sp = _renderer.WorldToScreen(proj.Position, proj.Height, _camera);

            if (proj.Type == ProjectileType.Arrow)
            {
                // Oriented arrow shaft
                float angle = MathF.Atan2(proj.Velocity.Y * _camera.YRatio, proj.Velocity.X);
                float len = 12f * _camera.Zoom / 32f;
                _spriteBatch.Draw(_pixel, sp, null, new Color(200, 180, 120),
                    angle, new Vector2(0, 0.5f), new Vector2(len, 1.5f), SpriteEffects.None, 0f);
                // Arrowhead
                _spriteBatch.Draw(_pixel, sp, null, new Color(160, 140, 100),
                    angle, new Vector2(-2f, 1.5f), new Vector2(4f, 3f), SpriteEffects.None, 0f);
            }
            else if (proj.Type == ProjectileType.Potion && !string.IsNullOrEmpty(proj.IconTexturePath))
            {
                // Potion bottle tumbling through the air
                var tex = GetItemTextureByPath(proj.IconTexturePath);
                if (tex != null)
                {
                    float worldSize = proj.ParticleScale * 1.2f;
                    float pixelSize = worldSize * _camera.Zoom;
                    float scale = pixelSize / MathF.Max(tex.Width, tex.Height);
                    var origin = new Vector2(tex.Width / 2f, tex.Height / 2f);
                    float tumble = proj.Age * 6f; // fast spin
                    _spriteBatch.Draw(tex, sp, null, Color.White,
                        tumble, origin, scale, SpriteEffects.None, 0f);
                }
                else
                {
                    // Fallback colored dot
                    float glowSize = 5f * _camera.Zoom / 32f;
                    _spriteBatch.Draw(_pixel, sp, null, new Color(100, 200, 100, 200),
                        0f, new Vector2(0.5f, 0.5f), glowSize, SpriteEffects.None, 0f);
                }
            }
            else
            {
                // Clean out later.
                throw new Exception($"Missing ProjectileType: {proj.Type}");
            }
        }
    }

    /// <summary>Draw fireball projectiles with HDR intensity (called in additive HdrSprite pass).</summary>
    private void DrawProjectilesHdr()
    {
        foreach (var proj in _sim.Projectiles.Projectiles)
        {
            if (!proj.Alive || proj.Type != ProjectileType.Fireball) continue;
            if (!_fogOfWar.IsVisible(proj.Position)) continue;
            var sp = _renderer.WorldToScreen(proj.Position, proj.Height, _camera);

            string fbId = proj.FlipbookID;
            if (!string.IsNullOrEmpty(fbId) && _flipbooks.TryGetValue(fbId, out var fb) && fb.IsLoaded)
            {
                int frameIdx = fb.GetFrameAtTime(proj.Age);
                var srcRect = fb.GetFrameRect(frameIdx);
                float worldSize = proj.ParticleScale * 1.5f;
                float pixelSize = worldSize * _camera.Zoom;
                float scale = pixelSize / srcRect.Width;
                var origin = new Vector2(srcRect.Width / 2f, srcRect.Height / 2f);

                // Trail: draw 2 previous frames behind with lower alpha, then main sprite
                Vec2 velDir = proj.Velocity.Normalized();
                for (int trail = 2; trail >= 0; trail--)
                {
                    float trailOffset = trail * 0.4f * _camera.Zoom;
                    float trailAlpha = (trail == 0) ? 1.0f : (trail == 1) ? 0.5f : 0.25f;
                    float trailScale = (trail == 0) ? 1.0f : (trail == 1) ? 0.8f : 0.6f;

                    int trailFrame = fb.GetFrameAtTime(proj.Age - trail * 0.05f);
                    Rectangle trailSrc = fb.GetFrameRect(trailFrame);

                    Vector2 trailPos = new Vector2(
                        sp.X - velDir.X * trailOffset,
                        sp.Y - velDir.Y * trailOffset * _camera.YRatio
                    );

                    var color = HdrColor.ToHdrVertex(proj.ParticleColor.ToColor(), trailAlpha, proj.ParticleColor.Intensity);
                    _spriteBatch.Draw(fb.Texture, trailPos, trailSrc, color,
                        proj.Age * 2f, origin, scale * trailScale, SpriteEffects.None, 0f);
                }
            }
            else
            {
                // Fallback glow dot
                float glowSize = 6f * _camera.Zoom / 32f;
                var color = HdrColor.ToHdrVertex(new Color(255, 120, 40), 200f / 255f, 1f);
                _spriteBatch.Draw(_pixel, sp, null, color,
                    0f, new Vector2(0.5f, 0.5f), glowSize, SpriteEffects.None, 0f);

                // Trail segments
                float trailLen = 4f * _camera.Zoom / 32f;
                for (int t = 1; t <= 3; t++)
                {
                    var trailPos = _renderer.WorldToScreen(
                        proj.Position - proj.Velocity.Normalized() * (t * 0.3f),
                        proj.Height - proj.VelocityZ * t * 0.02f, _camera);
                    float trailAlpha = (120f / t) / 255f;
                    var tColor = HdrColor.ToHdrVertex(new Color(255, 100, 30), trailAlpha, 1f);
                    _spriteBatch.Draw(_pixel, trailPos, null, tColor,
                        0f, new Vector2(0.5f, 0.5f), trailLen / t, SpriteEffects.None, 0f);
                }
            }
        }
    }

    /// <summary>Draw effects matching the given blend mode (0=alpha, 1=additive).</summary>
    private void DrawEffectsFiltered(int blendMode)
    {
        foreach (var eff in _effectManager.Effects)
        {
            if (!eff.Alive || eff.BlendMode != blendMode) continue;
            float t = eff.Age / eff.Lifetime;
            float alpha = eff.AlphaCurve.Evaluate(t);
            float scale = eff.ScaleCurve.Evaluate(t) * _camera.Zoom / 32f;

            var sp = _renderer.WorldToScreen(eff.Position, 0f, _camera);

            // Try flipbook
            if (!string.IsNullOrEmpty(eff.FlipbookKey) && _flipbooks.TryGetValue(eff.FlipbookKey, out var fb) && fb.IsLoaded)
            {
                int frameIdx = fb.GetFrameAtTime(eff.Age);
                var srcRect = fb.GetFrameRect(frameIdx);
                var origin = new Vector2(srcRect.Width * eff.AnchorX, srcRect.Height * eff.AnchorY);
                // Scale relative to world size
                float worldSize = scale * 2f; // scale curve gives world units
                float pixelSize = worldSize * _camera.Zoom;
                float fbScale = pixelSize / srcRect.Width;
                Color color = blendMode == 0
                    ? HdrColor.ToHdrVertexAlpha(eff.Tint, alpha, eff.HdrIntensity)
                    : HdrColor.ToHdrVertex(eff.Tint, alpha, eff.HdrIntensity);
                _spriteBatch.Draw(fb.Texture, sp, srcRect, color, 0f, origin, fbScale, SpriteEffects.None, 0f);
            }
            else
            {
                // Fallback glow (radial gradient circle)
                float glowAlpha = alpha * (200f / 255f);
                Color color = blendMode == 0
                    ? HdrColor.ToHdrVertexAlpha(eff.Tint, glowAlpha, eff.HdrIntensity)
                    : HdrColor.ToHdrVertex(eff.Tint, glowAlpha, eff.HdrIntensity);
                float glowSize = scale * _camera.Zoom * 0.5f / 32f;
                _spriteBatch.Draw(_glowTex, sp, null, color,
                    0f, new Vector2(32f, 32f), glowSize, SpriteEffects.None, 0f);
            }
        }
    }

    /// <summary>Spawn new effects from projectile impacts (called once per frame, blend-mode independent).</summary>
    private void SpawnImpactEffects()
    {
        foreach (var impact in _sim.Projectiles.Impacts)
        {
            string fbId = impact.HitEffectFlipbookID;
            if (!string.IsNullOrEmpty(fbId))
            {
                _effectManager.SpawnSpellImpact(impact.Position, impact.HitEffectScale,
                    impact.HitEffectColor.ToColor(), fbId, hdrIntensity: impact.HitEffectColor.Intensity,
                    blendMode: impact.HitEffectBlendMode, alignment: impact.HitEffectAlignment);
            }
            else if (impact.AoeRadius > 0)
            {
                _effectManager.SpawnExplosion(impact.Position, impact.AoeRadius);
            }
        }
    }

    private void DrawDamageNumbers()
    {
        if (_font == null) return;
        var dnSettings = _gameData.Settings.General;
        if (!dnSettings.DamageNumbersEnabled) return;
        var dnColor = dnSettings.DamageNumberColor;
        float dnScale = dnSettings.DamageNumberSize / 16f; // normalize against default 16

        foreach (var dn in _damageNumbers)
        {
            float fade = 1f - dn.Timer / dnSettings.DamageNumberFadeTime;
            if (fade <= 0f) continue;
            // Fog of war: hide damage numbers whose position is in fog. This covers
            // the "from non-undead" case — numbers pinned to hidden enemies don't
            // render, while numbers appearing on your own (visible) units do.
            if (!_fogOfWar.IsVisible(dn.WorldPos)) continue;
            var sp = _renderer.WorldToScreen(dn.WorldPos, dn.Height, _camera);
            byte alpha = (byte)(255 * fade);

            // Pickup text or damage number. Alerts (e.g. "Horde Full") render
            // raw — no "+" prefix — since they're not a numeric gain.
            string text;
            if (dn.PickupText != null)
                text = dn.IsAlert ? dn.PickupText : $"+{dn.PickupText}";
            else
                text = dn.Damage.ToString();
            var size = _font.MeasureString(text) * dnScale;
            var pos = new Vector2(sp.X - size.X / 2f, sp.Y - size.Y / 2f);

            // Shadow pass
            var shadowColor = new Color((byte)0, (byte)0, (byte)0, alpha);
            _spriteBatch.DrawString(_font, text, new Vector2(pos.X + 1f, pos.Y + 1f), shadowColor,
                0f, Vector2.Zero, dnScale, SpriteEffects.None, 0f);

            // Text pass — alert=red, pickup=gold, poison=green, fatigue=blue, else DamageNumberColor
            Color color;
            if (dn.IsAlert)
                color = Color.FromNonPremultiplied(255, 80, 80, alpha);
            else if (dn.PickupText != null)
                color = Color.FromNonPremultiplied(255, 220, 100, alpha);
            else if (dn.IsPoison)
                color = Color.FromNonPremultiplied(40, 200, 40, alpha);
            else if (dn.IsFatigue)
                color = Color.FromNonPremultiplied(80, 140, 255, alpha);
            else
                color = Color.FromNonPremultiplied(dnColor.R, dnColor.G, dnColor.B, alpha);
            _spriteBatch.DrawString(_font, text, pos, color,
                0f, Vector2.Zero, dnScale, SpriteEffects.None, 0f);
        }
    }
}
