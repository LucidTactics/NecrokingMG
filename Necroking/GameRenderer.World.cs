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
using Necroking.Lib;
using Necroking.UI;

namespace Necroking;

// Game1 partial: Terrain, effects and projectile rendering.
partial class GameRenderer
{
    private void DrawSoulOrbs()
    {
        var orbs = _g._sim.SoulOrbs;
        for (int i = 0; i < orbs.Count; i++)
        {
            var orb = orbs[i];
            var sp = _g._renderer.WorldToScreen(orb.Position, 0.5f, _g._camera);
            float orbZoom = _g._camera.Zoom / 32f; // radii authored at zoom 32

            // Outer purple glow
            float outerR = 6f * orbZoom;
            _g.Scope.Draw(_g._pixel, new Vector2(sp.X - outerR, sp.Y - outerR), null,
                new Color(120, 40, 180, 80), 0f, Vector2.Zero,
                new Vector2(outerR * 2, outerR * 2), SpriteEffects.None, 0f);

            // Inner white bright
            float innerR = 2f * orbZoom;
            _g.Scope.Draw(_g._pixel, new Vector2(sp.X - innerR, sp.Y - innerR), null,
                new Color(255, 255, 255, 200), 0f, Vector2.Zero,
                new Vector2(innerR * 2, innerR * 2), SpriteEffects.None, 0f);
        }
    }

    /// <summary>Ground pass body — runs OUTSIDE any open batch (its own pass in
    /// the pipeline); both paths open and close their own batch.</summary>
    private void DrawGround()
    {
        int worldW = _g._groundSystem.WorldW > 0 ? _g._groundSystem.WorldW : Game1.WorldSize;
        int worldH = _g._groundSystem.WorldH > 0 ? _g._groundSystem.WorldH : Game1.WorldSize;

        // Try shader-based ground rendering
        if (_g._groundEffect != null && _g._groundVertexMapTex != null && _g._groundSystem.TypeCount > 0)
        {
            DrawGroundShader(worldW, worldH);
            return;
        }

        // Fallback: per-tile texture rendering
        Render.EffectBatch.BeginScenePass(_g._spriteBatch);
        float viewLeft = _g._camera.Position.X - _g._renderer.ScreenW / (2f * _g._camera.Zoom) - 1;
        float viewRight = _g._camera.Position.X + _g._renderer.ScreenW / (2f * _g._camera.Zoom) + 1;
        float viewTop = _g._camera.Position.Y - _g._renderer.ScreenH / (_g._camera.Zoom * _g._camera.YRatio) - 1;
        float viewBottom = _g._camera.Position.Y + _g._renderer.ScreenH / (_g._camera.Zoom * _g._camera.YRatio) + 1;

        int minX = Math.Max(0, (int)viewLeft);
        int maxX = Math.Min(worldW - 1, (int)viewRight);
        int minY = Math.Max(0, (int)viewTop);
        int maxY = Math.Min(worldH - 1, (int)viewBottom);

        for (int y = minY; y <= maxY; y++)
        {
            for (int x = minX; x <= maxX; x++)
            {
                var sp = _g._renderer.WorldToScreen(new Vec2(x, y), 0f, _g._camera);
                float tileW = _g._camera.Zoom;
                float tileH = _g._camera.Zoom * _g._camera.YRatio;

                byte groundType = _g._groundSystem.GetVertex(x, y);
                var tex = _g._groundSystem.GetTexture(groundType);

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
                    _g.Scope.Draw(tex, destPos, srcRect, Color.White, 0f, Vector2.Zero, destScale, SpriteEffects.None, 0f);
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
                    _g.Scope.Draw(_g._pixel, sp, null, color, 0f, Vector2.Zero, new Vector2(tileW + 0.5f, tileH + 0.5f), SpriteEffects.None, 0f);
                }
            }
        }

        _g._spriteBatch.End();
    }

    // Matches the shader's array length (16 in GroundShader.fx).
    private const int MaxGroundTypes = 16;
    // Reused every frame (refilled, never re-allocated) — the ground-shader
    // uniform uploads previously allocated both arrays each frame.
    private readonly Vector4[] _groundTintArr = new Vector4[MaxGroundTypes];
    private readonly float[] _groundWaterArr = new float[MaxGroundTypes];

    private void DrawGroundShader(int worldW, int worldH)
    {
        _g._groundDrawStopwatch.Restart();

        // Set shader parameters (independent of SpriteBatch state — they upload
        // at Apply time inside the Begin below)
        _g._groundEffect!.Parameters["AmbientColor"]?.SetValue(new Vector3(_g._ambientColor.R / 255f, _g._ambientColor.G / 255f, _g._ambientColor.B / 255f));
        _g._groundEffect.Parameters["CameraPos"]?.SetValue(new Vector2(_g._camera.Position.X, _g._camera.Position.Y));
        _g._groundEffect.Parameters["Zoom"]?.SetValue(_g._camera.Zoom);
        _g._groundEffect.Parameters["YRatio"]?.SetValue(_g._camera.YRatio);
        _g._groundEffect.Parameters["ScreenSize"]?.SetValue(new Vector2(_g._renderer.ScreenW, _g._renderer.ScreenH));
        _g._groundEffect.Parameters["WorldSize"]?.SetValue(new Vector2(_g._groundSystem.VertexW, _g._groundSystem.VertexH));
        _g._groundEffect.Parameters["TypeWarpStrength"]?.SetValue(_g._groundSystem.TypeWarpStrength);
        _g._groundEffect.Parameters["UvWarpAmp"]?.SetValue(_g._groundSystem.UvWarpAmp);
        _g._groundEffect.Parameters["UvWarpFreq"]?.SetValue(_g._groundSystem.UvWarpFreq);
        _g._groundEffect.Parameters["Time"]?.SetValue(_g._gameTime);

        // Per-ground-type uniform arrays, indexed by the type id in the bottom
        // 5 bits of the tilemap byte (the top 3 bits hold the texture-slot id,
        // 0..7, which drives the shader cascade). The shader clamps decoded
        // type ids to 0..15, so a 17th+ ground type renders with type-15's
        // tint/water entry rather than reading past the arrays.
        const int MaxTextureSlots = 8;
        var tintArr = _groundTintArr;
        var waterArr = _groundWaterArr;
        for (int i = 0; i < MaxGroundTypes; i++) { tintArr[i] = Vector4.One; waterArr[i] = 0f; }
        int typeCap = Math.Min(_g._groundSystem.TypeCount, MaxGroundTypes);
        for (int i = 0; i < typeCap; i++)
        {
            var def = _g._groundSystem.GetTypeDef(i);
            tintArr[i] = def.TintColor.ToVector4();
            waterArr[i] = (def.MovementTerrain == Necroking.World.TerrainType.ShallowWater
                        || def.MovementTerrain == Necroking.World.TerrainType.DeepWater) ? 1f : 0f;
        }
        _g._groundEffect.Parameters["TintColors"]?.SetValue(tintArr);
        _g._groundEffect.Parameters["IsWaterType"]?.SetValue(waterArr);

        // Bind unique ground textures via Effect.Parameters (named texture params, not register slots).
        // Shader cascade supports MaxTextureSlots unique texture slots; types past those reuse slot 0 fallback.
        string[] texParamNames = {
            "GroundTexture0", "GroundTexture1", "GroundTexture2", "GroundTexture3",
            "GroundTexture4", "GroundTexture5", "GroundTexture6", "GroundTexture7",
        };
        int slotCap = Math.Min(_g._groundSystem.UniqueTextureCount, Math.Min(texParamNames.Length, MaxTextureSlots));
        for (int i = 0; i < slotCap; i++)
        {
            var tex = _g._groundSystem.GetUniqueTexture(i);
            if (tex != null)
                _g._groundEffect.Parameters[texParamNames[i]]?.SetValue(tex);
        }

        // Self-contained batch (the ground pass runs outside any open batch):
        // one fullscreen quad through the ground shader, Opaque + PointClamp.
        _g._spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.Opaque, SamplerState.PointClamp,
            null, null, _g._groundEffect);
        Materials.NoteAdHocBatch(); // opaque ground-shader quad, draws White only

        // SpriteBatch.Draw binds _g._groundVertexMapTex to slot 0 (= TilemapSampler)
        _g.Scope.Draw(_g._groundVertexMapTex!, new Rectangle(0, 0, _g._renderer.ScreenW, _g._renderer.ScreenH), Color.White);

        _g._spriteBatch.End();

        _g._groundDrawStopwatch.Stop();
        double groundDrawMs = _g._groundDrawStopwatch.Elapsed.TotalMilliseconds;
        const double GroundEmaAlpha = 0.1;
        _g._groundMsAvg = _g._groundMsAvg * (1.0 - GroundEmaAlpha) + groundDrawMs * GroundEmaAlpha;

        if (_g._activeScenario is Scenario.Scenarios.PerfWaterScenario perf)
            perf.LastGroundMs = groundDrawMs;
    }

    private void DrawRoads()
    {
        var roads = _g._roadSystem.Roads;
        var junctions = _g._roadSystem.Junctions;
        if (roads.Count == 0 && junctions.Count == 0) return;

        var roadColor = MultiplyColor(new Color(100, 90, 80), _g._ambientColor);

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
                    var screenA = _g._renderer.WorldToScreen(prev, 0f, _g._camera);
                    var screenB = _g._renderer.WorldToScreen(cur, 0f, _g._camera);

                    float dx = screenB.X - screenA.X;
                    float dy = screenB.Y - screenA.Y;
                    float segLen = MathF.Sqrt(dx * dx + dy * dy);
                    if (segLen < 0.1f) { prev = cur; prevW = curW; continue; }

                    float angle = MathF.Atan2(dy, dx);
                    float avgWidth = (prevW + curW) * 0.5f * _g._camera.Zoom;

                    _g.Scope.Draw(_g._pixel, screenA, null, roadColor,
                        angle, new Vector2(0, 0.5f), new Vector2(segLen + 1f, avgWidth), SpriteEffects.None, 0f);

                    prev = cur;
                    prevW = curW;
                }
            }
        }

        // Draw junctions as filled circle approximations
        foreach (var junc in junctions)
        {
            var sp = _g._renderer.WorldToScreen(junc.Position, 0f, _g._camera);
            float radius = junc.Radius * _g._camera.Zoom;
            int r = Math.Max(2, (int)radius);

            // Draw circle as a series of horizontal lines
            for (int dy = -r; dy <= r; dy++)
            {
                float halfW = MathF.Sqrt(r * r - dy * dy);
                int x0 = (int)(sp.X - halfW);
                int w = (int)(halfW * 2f);
                if (w < 1) w = 1;
                _g.Scope.Draw(_g._pixel, new Rectangle(x0, (int)sp.Y + dy, w, 1), roadColor);
            }
        }
    }

    private void DrawWalls()
    {
        if (_g._wallSystem.Width == 0 || _g._wallSystem.Height == 0 || _g._wallSystem.DefCount == 0) return;

        // View culling bounds (same approach as DrawGround)
        float viewLeft = _g._camera.Position.X - _g._renderer.ScreenW / (2f * _g._camera.Zoom) - 1;
        float viewRight = _g._camera.Position.X + _g._renderer.ScreenW / (2f * _g._camera.Zoom) + 1;
        float viewTop = _g._camera.Position.Y - _g._renderer.ScreenH / (_g._camera.Zoom * _g._camera.YRatio) - 1;
        float viewBottom = _g._camera.Position.Y + _g._renderer.ScreenH / (_g._camera.Zoom * _g._camera.YRatio) + 1;

        int minX = Math.Max(0, (int)viewLeft);
        int maxX = Math.Min(_g._wallSystem.Width - 1, (int)viewRight);
        int minY = Math.Max(0, (int)viewTop);
        int maxY = Math.Min(_g._wallSystem.Height - 1, (int)viewBottom);

        float tileW = _g._camera.Zoom;
        float tileH = _g._camera.Zoom * _g._camera.YRatio;

        for (int y = minY; y <= maxY; y++)
        {
            for (int x = minX; x <= maxX; x++)
            {
                if (!_g._wallSystem.IsAlive(x, y)) continue;

                byte wallType = _g._wallSystem.GetWallType(x, y);
                if (wallType == 0 || wallType > _g._wallSystem.DefCount) continue;

                var def = _g._wallSystem.Defs[wallType - 1];
                var sp = _g._renderer.WorldToScreen(new Vec2(x, y), 0f, _g._camera);

                // Draw colored rectangle as placeholder (using def's Color)
                // Make wall tiles slightly taller to give a wall appearance
                float wallH = tileH * 1.5f;
                var wallColor = MultiplyColor(def.Color, _g._ambientColor);
                _g.Scope.Draw(_g._pixel, new Vector2(sp.X, sp.Y - wallH + tileH), null,
                    wallColor, 0f, Vector2.Zero,
                    new Vector2(tileW + 0.5f, wallH), SpriteEffects.None, 0f);

                // Draw a darker top edge for depth effect
                var darkColor = new Color(
                    (byte)(wallColor.R * 0.6f),
                    (byte)(wallColor.G * 0.6f),
                    (byte)(wallColor.B * 0.6f),
                    wallColor.A);
                _g.Scope.Draw(_g._pixel, new Vector2(sp.X, sp.Y - wallH + tileH), null,
                    darkColor, 0f, Vector2.Zero,
                    new Vector2(tileW + 0.5f, 2f * _g._camera.Zoom / 32f), SpriteEffects.None, 0f);
            }
        }
    }

    /// <summary>Draw ground-layer objects (traps) — above dirt, below grass/units.
    /// Runs as the Traps layer item inside the world queue; the scope carries the
    /// resume material for any Push/Pop draw (e.g. a dissolving trap).</summary>
    private void DrawGroundLayerObjects(SpriteScope scope)
    {
        for (int i = 0; i < _g._envSystem.ObjectCount; i++)
        {
            if (!_g._envSystem.IsObjectVisible(i)) continue;
            var obj = _g._envSystem.Objects[i];
            var def = _g._envSystem.Defs[obj.DefIndex];
            if (def.Category != "Traps") continue;
            DrawSingleEnvObject(scope, i);
        }
    }

    /// <summary>True when this projectile renders in the additive HDR pass
    /// (DrawProjectilesHdr) instead of the plain alpha pass: explosives always, and
    /// any RegularHit projectile carrying a loaded projectile flipbook — spells fired
    /// as RegularHit are magic darts and use their SpellDef's ProjectileFlipbook visuals,
    /// not the hardcoded arrow shaft. A RegularHit projectile whose flipbook is missing/
    /// unloaded falls back to the classic arrow rendering rather than a fireball glow.</summary>
    private bool RendersInHdrPass(Projectile proj)
        => proj.Type == ProjectileType.Explosive
        || (proj.Type == ProjectileType.RegularHit
            && !string.IsNullOrEmpty(proj.FlipbookID)
            && _g._flipbooks.TryGetValue(proj.FlipbookID, out var fb) && fb.IsLoaded);

    private void DrawProjectiles()
    {
        foreach (var proj in _g._sim.Projectiles.Projectiles)
        {
            if (!proj.Alive) continue;
            // Explosives and flipbook-carrying magic darts are drawn in the additive
            // HDR pass (DrawProjectilesHdr)
            if (RendersInHdrPass(proj)) continue;
            // Fog of war: hide projectile if its current tile isn't visible.
            if (!_g._fogOfWar.IsVisible(proj.Position)) continue;

            var sp = _g._renderer.WorldToScreen(proj.Position, proj.Height, _g._camera);

            if (proj.Type == ProjectileType.RegularHit)
            {
                // Oriented arrow shaft — include the height (Z) component so the shaft
                // tilts with the arc (WorldToScreen projects height up-screen at YRatio).
                Vec2 arrowVel = proj.VisualVelocity;
                float angle = MathF.Atan2(
                    (arrowVel.Y - proj.VisualVelocityZ) * _g._camera.YRatio, arrowVel.X);
                float projZoom = _g._camera.Zoom / 32f;
                float len = 12f * projZoom;
                _g.Scope.Draw(_g._pixel, sp, null, new Color(200, 180, 120),
                    angle, new Vector2(0, 0.5f), new Vector2(len, 1.5f * projZoom), SpriteEffects.None, 0f);
                // Arrowhead (origin offset is in source texels — scaling the draw
                // scale carries it, so only the scale term needs the zoom factor)
                _g.Scope.Draw(_g._pixel, sp, null, new Color(160, 140, 100),
                    angle, new Vector2(-2f, 1.5f), new Vector2(4f, 3f) * projZoom, SpriteEffects.None, 0f);
            }
            else if (proj.Type == ProjectileType.Potion)
            {
                // Potion bottle tumbling through the air
                var tex = _g.GetItemTextureByPath(proj.IconTexturePath);
                if (tex != null)
                {
                    float worldSize = proj.ParticleScale * 1.2f;
                    float pixelSize = worldSize * _g._camera.Zoom;
                    float scale = pixelSize / MathF.Max(tex.Width, tex.Height);
                    var origin = new Vector2(tex.Width / 2f, tex.Height / 2f);
                    float tumble = proj.Age * 6f; // fast spin
                    _g.Scope.Draw(tex, sp, null, Color.White,
                        tumble, origin, scale, SpriteEffects.None, 0f);
                }
                else
                {
                    // Fallback colored dot
                    float glowSize = 5f * _g._camera.Zoom / 32f;
                    _g.Scope.Draw(_g._pixel, sp, null, new Color(100, 200, 100, 200),
                        0f, new Vector2(0.5f, 0.5f), glowSize, SpriteEffects.None, 0f);
                }
            }
            else
            {
                // Unknown projectile type — never throw from the draw loop (that would take
                // down all rendering); log and skip drawing this one.
                Necroking.Core.DebugLog.Log("render", $"DrawProjectiles: unhandled ProjectileType {proj.Type} — skipped");
            }
        }
    }

    /// <summary>Resolve a tether endpoint to a world position + rope-attach height. Returns
    /// false if the unit/corpse no longer exists (that tether is skipped this frame; the
    /// sim-side UpdateTethers removes it).</summary>
    private bool TryTetherEndWorld(Game1.TetherEnd e, out Vec2 pos, out float height)
    {
        pos = default; height = 0f;
        if (e.Kind == Game1.TetherEndKind.Unit)
        {
            int ui = Necroking.Movement.UnitUtil.ResolveUnitIndex(_g._sim.Units, e.UnitId);
            if (ui < 0) return false;
            pos = _g._sim.Units[ui].Position; height = 1.2f; // near the hand/torso
            return true;
        }
        int ci = _g._sim.FindCorpseIndexByID(e.CorpseId);
        if (ci < 0) return false;
        pos = _g._sim.Corpses[ci].Position; height = 0.25f;  // low on the body
        return true;
    }

    /// <summary>Draw every tether as a sagging bezier between its two endpoints. Slack
    /// (short) when close so it hangs; pulls straight + taut as it stretches to full length
    /// and the corpse starts dragging.</summary>
    private void DrawRope()
    {
        var ropeBright = new Color(214, 184, 122); // bright hemp
        var ropeDim = new Color(176, 148, 94);     // every-other segment, ~0.8× brightness
        foreach (var t in _g._tethers)
        {
            if (!TryTetherEndWorld(t.A, out var wa, out var ha)) continue;
            if (!TryTetherEndWorld(t.B, out var wb, out var hb)) continue;

            var a = _g._renderer.WorldToScreen(wa, ha, _g._camera);
            var b = _g._renderer.WorldToScreen(wb, hb, _g._camera);

            float dist = (wb - wa).Length();
            // Slack fraction: 1 when the ends coincide, 0 when the rope is fully taut.
            float slack = Necroking.Core.MathUtil.Clamp(
                (Game1.RopeMaxLength - dist) / Game1.RopeMaxLength, 0f, 1f);
            // Downward sag (screen +Y), scaled by on-screen rope length so a long slack rope
            // droops more than a short one.
            float ropeLenPx = (b - a).Length();
            float sag = slack * ropeLenPx * 0.25f;
            var mid = (a + b) * 0.5f + new Vector2(0f, sag);

            // Sample the quadratic bezier a→mid→b; alternate segments render slightly darker
            // for a braided/twisted-fibre read.
            const int segments = 14;
            var prev = a;
            for (int s = 1; s <= segments; s++)
            {
                float tt = s / (float)segments;
                float u = 1f - tt;
                var p = u * u * a + 2f * u * tt * mid + tt * tt * b;
                DrawUtils.DrawLine(_g._spriteBatch, _g._pixel, prev, p,
                    (s % 2 == 0) ? ropeBright : ropeDim,
                    MathF.Max(1f, 2f * _g._camera.Zoom / 32f));
                prev = p;
            }
        }
    }

    /// <summary>Draw fireball and flipbook-arrow projectiles with HDR intensity
    /// (called in additive HdrSprite pass). Pass membership is decided by
    /// <see cref="RendersInHdrPass"/> — keep the two draw methods' guards in sync
    /// through it, or a projectile draws twice or not at all.</summary>
    private void DrawProjectilesHdr()
    {
        foreach (var proj in _g._sim.Projectiles.Projectiles)
        {
            if (!proj.Alive || !RendersInHdrPass(proj)) continue;
            if (!_g._fogOfWar.IsVisible(proj.Position)) continue;
            var sp = _g._renderer.WorldToScreen(proj.Position, proj.Height, _g._camera);

            string fbId = proj.FlipbookID;
            if (!string.IsNullOrEmpty(fbId) && _g._flipbooks.TryGetValue(fbId, out var fb) && fb.IsLoaded)
            {
                int frameIdx = fb.GetFrameAtTime(proj.Age);
                var srcRect = fb.GetFrameRect(frameIdx);
                float worldSize = proj.ParticleScale * 1.5f;
                float pixelSize = worldSize * _g._camera.Zoom;
                float scale = pixelSize / srcRect.Width;
                var origin = new Vector2(srcRect.Width / 2f, srcRect.Height / 2f);

                // Face the current 3D travel direction. On-screen velocity is world XY
                // (VisualVelocity — includes the swirl wobble) plus the height
                // contribution: WorldToScreen subtracts Height * Zoom * YRatio, so
                // rising (VelocityZ > 0) moves the sprite up-screen at the same
                // YRatio foreshortening as world Y.
                Vec2 visVel = proj.VisualVelocity;
                var screenVel = new Vector2(
                    visVel.X,
                    (visVel.Y - proj.VisualVelocityZ) * _g._camera.YRatio);
                // fire_loop art has its flame tail at the top of the frame (nose = down,
                // screen angle +π/2); rotate that nose onto the travel direction.
                float faceAngle = MathF.Atan2(screenVel.Y, screenVel.X) - MathF.PI / 2f;
                if (screenVel.LengthSquared() > 1e-6f) screenVel.Normalize();

                // Trail: draw 2 previous frames behind with lower alpha, then main sprite
                for (int trail = 2; trail >= 0; trail--)
                {
                    float trailOffset = trail * 0.4f * _g._camera.Zoom;
                    float trailAlpha = (trail == 0) ? 1.0f : (trail == 1) ? 0.5f : 0.25f;
                    float trailScale = (trail == 0) ? 1.0f : (trail == 1) ? 0.8f : 0.6f;

                    int trailFrame = fb.GetFrameAtTime(proj.Age - trail * 0.05f);
                    Rectangle trailSrc = fb.GetFrameRect(trailFrame);

                    Vector2 trailPos = sp - screenVel * trailOffset;

                    var color = HdrColor.ToHdrVertex(proj.ParticleColor.ToColor(), trailAlpha, proj.ParticleColor.Intensity);
                    _g.Scope.Draw(fb.Texture, trailPos, trailSrc, color,
                        faceAngle, origin, scale * trailScale, SpriteEffects.None, 0f);
                }
            }
            else
            {
                // Fallback glow dot
                float glowSize = 6f * _g._camera.Zoom / 32f;
                var color = HdrColor.ToHdrVertex(new Color(255, 120, 40), 200f / 255f, 1f);
                _g.Scope.Draw(_g._pixel, sp, null, color,
                    0f, new Vector2(0.5f, 0.5f), glowSize, SpriteEffects.None, 0f);

                // Trail segments
                float trailLen = 4f * _g._camera.Zoom / 32f;
                for (int t = 1; t <= 3; t++)
                {
                    var trailPos = _g._renderer.WorldToScreen(
                        proj.Position - proj.Velocity.Normalized() * (t * 0.3f),
                        proj.Height - proj.VelocityZ * t * 0.02f, _g._camera);
                    float trailAlpha = (120f / t) / 255f;
                    var tColor = HdrColor.ToHdrVertex(new Color(255, 100, 30), trailAlpha, 1f);
                    _g.Scope.Draw(_g._pixel, trailPos, null, tColor,
                        0f, new Vector2(0.5f, 0.5f), trailLen / t, SpriteEffects.None, 0f);
                }
            }
        }
    }

    /// <summary>Draw effects matching the given blend mode (0=alpha, 1=additive).</summary>
    private void DrawEffectsFiltered(int blendMode)
    {
        foreach (var eff in _g._effectManager.Effects)
        {
            if (!eff.Alive || eff.BlendMode != blendMode) continue;
            float t = eff.Age / eff.Lifetime;
            float alpha = eff.AlphaCurve.Evaluate(t);
            // ScaleCurve is WORLD units — the world→px multiply happens once below.
            // (A stray extra *Zoom/32 here made every impact flipbook scale ∝ Zoom²:
            // right at 32, 2x too big at 64, 4x too small at 8. Round-2 sweep find.)
            float scale = eff.ScaleCurve.Evaluate(t);

            var sp = _g._renderer.WorldToScreen(eff.Position, 0f, _g._camera);

            // Try flipbook
            if (!string.IsNullOrEmpty(eff.FlipbookKey) && _g._flipbooks.TryGetValue(eff.FlipbookKey, out var fb) && fb.IsLoaded)
            {
                int frameIdx = fb.GetFrameAtTime(eff.Age);
                var srcRect = fb.GetFrameRect(frameIdx);
                var origin = new Vector2(srcRect.Width * eff.AnchorX, srcRect.Height * eff.AnchorY);
                // Scale relative to world size
                float worldSize = scale * 2f; // scale curve gives world units
                float pixelSize = worldSize * _g._camera.Zoom;
                float fbScale = pixelSize / srcRect.Width;
                Color color = blendMode == 0
                    ? HdrColor.ToHdrVertexAlpha(eff.Tint, alpha, eff.HdrIntensity)
                    : HdrColor.ToHdrVertex(eff.Tint, alpha, eff.HdrIntensity);
                _g.Scope.Draw(fb.Texture, sp, srcRect, color, 0f, origin, fbScale, SpriteEffects.None, 0f);
            }
            else
            {
                // Fallback glow (radial gradient circle)
                float glowAlpha = alpha * (200f / 255f);
                Color color = blendMode == 0
                    ? HdrColor.ToHdrVertexAlpha(eff.Tint, glowAlpha, eff.HdrIntensity)
                    : HdrColor.ToHdrVertex(eff.Tint, glowAlpha, eff.HdrIntensity);
                float glowSize = scale * _g._camera.Zoom * 0.5f / 32f;
                _g.Scope.Draw(_g._glowTex, sp, null, color,
                    0f, new Vector2(32f, 32f), glowSize, SpriteEffects.None, 0f);
            }
        }
    }

    /// <summary>Spawn new effects from projectile impacts (called once per frame, blend-mode independent).</summary>
    private void SpawnImpactEffects()
    {
        foreach (var impact in _g._sim.Projectiles.Impacts)
        {
            string fbId = impact.HitEffectFlipbookID;
            if (!string.IsNullOrEmpty(fbId))
            {
                _g._effectManager.SpawnSpellImpact(impact.Position, impact.HitEffectScale,
                    impact.HitEffectColor.ToColor(), fbId, hdrIntensity: impact.HitEffectColor.Intensity,
                    blendMode: impact.HitEffectBlendMode, alignment: impact.HitEffectAlignment);
            }
            else if (impact.AoeRadius > 0)
            {
                _g._effectManager.SpawnExplosion(impact.Position, impact.AoeRadius);
            }
        }
    }

    private void DrawDamageNumbers()
    {
        if (_g._font == null) return;
        var dnSettings = _g._gameData.Settings.General;
        if (!dnSettings.DamageNumbersEnabled) return;
        var dnColor = dnSettings.DamageNumberColor;
        // Text size and float-up rise both couple softly to zoom (same curve as rain):
        // a bit bigger/farther zoomed in, never unreadable at MinZoom. The rise is
        // converted to pixels here (dn.Height is authored in world units at zoom 32)
        // so text size and motion stay in one space instead of mixing conventions.
        // POLICY FLAG (user-approved deviation, 2026-07): this sqrt curve is a sanctioned
        // exception to the strict-realism linear rule — see vfx-zoom-audit.md policy flags.
        float softScale = _g._camera.SoftZoomScale(32f);
        float dnScale = dnSettings.DamageNumberSize / 16f * softScale; // normalize against default 16

        foreach (var dn in _g._damageNumbers)
        {
            float fade = 1f - dn.Timer / dnSettings.DamageNumberFadeTime;
            if (fade <= 0f) continue;
            // Fog of war: hide damage numbers whose position is in fog. This covers
            // the "from non-undead" case — numbers pinned to hidden enemies don't
            // render, while numbers appearing on your own (visible) units do.
            if (!_g._fogOfWar.IsVisible(dn.WorldPos)) continue;
            float risePx = dn.Height * 32f * _g._camera.YRatio * softScale;
            var sp = _g._renderer.WorldToScreenPx(dn.WorldPos, risePx, _g._camera);
            byte alpha = (byte)(255 * fade);

            // Pickup text or damage number. Alerts (e.g. "Horde Full") render
            // raw — no "+" prefix — since they're not a numeric gain.
            string text;
            if (dn.PickupText != null)
                text = dn.IsAlert ? dn.PickupText : $"+{dn.PickupText}";
            else
                text = dn.Damage.ToString();
            var size = _g._font.MeasureString(text) * dnScale;
            var pos = new Vector2(sp.X - size.X / 2f, sp.Y - size.Y / 2f);

            // Shadow pass
            var shadowColor = new Color((byte)0, (byte)0, (byte)0, alpha);
            _g.Scope.DrawString(_g._font, text, new Vector2(pos.X + 1f, pos.Y + 1f), shadowColor,
                0f, Vector2.Zero, dnScale, SpriteEffects.None, 0f);

            // Text pass — alert=red, pickup=gold, poison=green, fatigue=blue, else DamageNumberColor
            Color color;
            if (dn.IsAlert)
                color = new Color(255, 80, 80, (int)alpha);
            else if (dn.PickupText != null)
                color = new Color(255, 220, 100, (int)alpha);
            else if (dn.IsPoison)
                color = new Color(40, 200, 40, (int)alpha);
            else if (dn.IsFatigue)
                color = new Color(80, 140, 255, (int)alpha);
            else
                color = new Color(dnColor.R, dnColor.G, dnColor.B, alpha);
            _g.Scope.DrawString(_g._font, text, pos, color,
                0f, Vector2.Zero, dnScale, SpriteEffects.None, 0f);
        }
    }
}
