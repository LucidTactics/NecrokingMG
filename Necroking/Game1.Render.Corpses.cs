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

// Game1 partial: Corpses, body-bags, carried visuals, centroid cache.
public partial class Game1
{
    // Draw the reanimating body morphing death-pose -> standup-start via the SDF morph shader
    // (the amoeba gain/shed-pixels morph): interpolates the two poses' distance fields, fills with
    // the crossfaded body color + green energy in the bulge gaps, and traces a pulsing green outline
    // on the morphed edge. Returns false if the shader / morph data is unavailable (caller falls
    // back to an alpha crossfade). Draws in its own batch (like DrawSpriteOutline), then restores.
    private bool DrawReanimMorph(SpriteAtlas atlas, SpriteFrame death, bool deathFlip,
        SpriteFrame standup, bool standupFlip, Vector2 sp, float scale, Color tint, float morphT,
        HdrColor outline, float outlineWidth, float pulseWidth, float pulseSpeed)
    {
        if (_morphSdfEffect == null) return false;
        var md = _reanimMorph.GetOrBuild(GraphicsDevice, atlas, death, deathFlip, standup, standupFlip);
        if (!md.Valid || md.ColorA == null || md.ColorB == null || md.Sdf == null) return false;

        float pulse = 0.5f + 0.5f * MathF.Sin(_gameTime * pulseSpeed * 2f * MathF.PI);
        var greenHue = new Vector3(outline.R / 255f, outline.G / 255f, outline.B / 255f);
        float outlineStrength = (outline.A / 255f) * (0.3f + 0.3f * pulse); // fade-in (alpha) + pulse

        var fx = _morphSdfEffect;
        fx.Parameters["MorphT"]?.SetValue(morphT);
        fx.Parameters["MaxDist"]?.SetValue(md.MaxDist);
        fx.Parameters["EdgeSoftness"]?.SetValue(1.5f);
        fx.Parameters["Bulge"]?.SetValue(4f);
        fx.Parameters["GreenFill"]?.SetValue(greenHue);
        fx.Parameters["OutlineColor"]?.SetValue(greenHue);
        fx.Parameters["OutlineWidth"]?.SetValue(1.2f);
        fx.Parameters["OutlinePulse"]?.SetValue(outlineStrength);

        var pB = fx.Parameters["ColorB"];
        if (pB != null) pB.SetValue(md.ColorB);
        else { GraphicsDevice.Textures[1] = md.ColorB; GraphicsDevice.SamplerStates[1] = SamplerState.LinearClamp; }
        var pS = fx.Parameters["SdfMap"];
        if (pS != null) pS.SetValue(md.Sdf);
        else { GraphicsDevice.Textures[2] = md.Sdf; GraphicsDevice.SamplerStates[2] = SamplerState.LinearClamp; }

        _spriteBatch.End();
        _spriteBatch.Begin(SpriteSortMode.Immediate, BlendState.AlphaBlend, SamplerState.LinearClamp, null, null, fx);
        _spriteBatch.Draw(md.ColorA, sp, null, tint, 0f, new Vector2(md.PivotX, md.PivotY), scale, SpriteEffects.None, 0f);
        _spriteBatch.End();
        _spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.LinearClamp);
        return true;
    }

    private void DrawCorpses()
    {
        // Hover-highlight target (captured below as its sprite is drawn).
        Corpse? hoveredCorpse = (_gameData.Settings.Tooltips.ShowHoverHighlight
            && _hoveredCorpseIdx >= 0 && _hoveredCorpseIdx < _sim.Corpses.Count)
            ? _sim.Corpses[_hoveredCorpseIdx] : null;

        foreach (var corpse in _sim.Corpses)
        {
            // Don't render corpses attached to a unit — drawn on unit in DrawSingleUnit
            // (covers carried phase 0, pickup phase 4, putdown phase 5). Applies to
            // both the bagged-bag flow and the raw-corpse carry flow.
            if (corpse.DraggedByUnitID != GameConstants.InvalidUnit)
                continue;

            // Bagged corpses render as BodyBag from Corpses atlas
            if (corpse.Bagged)
            {
                DrawBaggedCorpse(corpse);
                continue;
            }

            // A corpse that was carried + dropped keeps its exact carried pose
            // (frozen angle + centroid anchor), so the settled draw matches the
            // put-down draw with no jump. Skip while flying (physics) so a
            // knocked-back dropped corpse still tumbles via the normal path.
            if (corpse.CarryDisplayAngle >= 0 && !corpse.InPhysics)
            {
                DrawCorpseCarriedFrame(corpse, _renderer.WorldToScreen(corpse.Position, corpse.Z, _camera));
                continue;
            }

            var unitDef = _gameData.Units.Get(corpse.UnitDefID);
            if (unitDef?.Sprite == null) continue;
            var atlasId = AtlasDefs.ResolveAtlasName(unitDef.Sprite.AtlasName);
            var atlas = _atlases[atlasId];
            if (!atlas.IsLoaded) continue;

            // Get or create corpse anim controller
            if (!_corpseAnims.TryGetValue(corpse.CorpseID, out var cad))
            {
                var spriteData = atlas.GetUnit(unitDef.Sprite.SpriteName);
                if (spriteData == null) continue;
                var ctrl = new AnimController();
                ctrl.Init(spriteData);
                if (_animMeta.Count > 0)
                    ctrl.SetAnimMeta(_animMeta, unitDef.Sprite.SpriteName);
                if (unitDef.AnimTimings.Count > 0)
                {
                    var overrides = new Dictionary<string, AnimTimingOverride>();
                    foreach (var (anim, ov) in unitDef.AnimTimings)
                        overrides[anim] = new AnimTimingOverride
                        {
                            FrameDurationsMs = new List<int>(ov.FrameDurationsMs),
                            EffectTimeMs = ov.EffectTimeMs
                        };
                    ctrl.SetAnimTimings(overrides);
                }
                // Pre-settled corpses (editor-placed / present at map load) snap to
                // the END of the death anim so they appear already collapsed instead
                // of replaying the death fall on the first frame they're seen.
                // ForceStateAtEnd no-ops if already in Death, so call it from the
                // fresh controller's default (Idle) state rather than RequestState first.
                if (corpse.PreSettled)
                    ctrl.ForceStateAtEnd(AnimState.Death);
                else
                    ctrl.RequestState(corpse.InPhysics ? AnimState.Fall : AnimState.Death);

                float refH = 128f;
                var idle = spriteData.GetAnim("Idle");
                if (idle != null) { var kfs = PickIdleFrames(idle); if (kfs != null && kfs.Count > 0) refH = kfs[0].Frame.Rect.Height; }

                cad = new UnitAnimData { Ctrl = ctrl, AtlasID = atlasId, RefFrameHeight = refH, CachedDefID = corpse.UnitDefID };
                _corpseAnims[corpse.CorpseID] = cad;
            }

            // When corpse lands from knockback arc, snap to final death frame
            if (!corpse.InPhysics && cad.Ctrl.CurrentState == AnimState.Fall)
                cad.Ctrl.ForceStateAtEnd(AnimState.Death);

            bool reanimating = corpse.ReanimInstanceId > 0;
            if (!cad.Ctrl.IsAnimFinished && !_paused)
                cad.Ctrl.Update(MathF.Min(_rawDt, 1f / 20f) * _timeScale);

            int alphaInt = 255;
            if (corpse.Dissolving)
            {
                float t = corpse.DissolveTimer / 2f;
                float a = 255f * (1f - t);
                if ((int)(corpse.DissolveTimer * 8f) % 2 == 0) a *= 0.3f;
                alphaInt = (int)MathUtil.Clamp(a, 0f, 255f);
            }
            byte alpha = (byte)alphaInt;

            var fr = cad.Ctrl.GetCurrentFrame(corpse.FacingAngle);
            if (fr.Frame != null)
            {
                float worldH = (unitDef.SpriteWorldHeight > 0 ? unitDef.SpriteWorldHeight : 1.8f) * corpse.SpriteScale;
                float pixelH = worldH * _camera.Zoom;
                float scale = pixelH / cad.RefFrameHeight;

                var sp = _renderer.WorldToScreen(corpse.Position, corpse.Z, _camera);
                Color corpseTint = MultiplyColor(new Color(alpha, alpha, alpha, alpha), _ambientColor);

                // While reanimating, MORPH the body from its death pose to the Standup START pose
                // over the build-up — a true SDF "amoeba" morph (silhouette gains/sheds pixels), so
                // it visibly gathers before rising and hands off seamlessly to the risen unit, with
                // a pulsing green outline tracing the morphed edge. Falls back to an alpha crossfade.
                if (reanimating &&
                    _reanimFx.TryGetCorpseOutline(corpse.ReanimInstanceId, out var co1, out var co2,
                        out var cow, out var cpw, out var cps, out float morphT))
                {
                    var frUp = cad.Ctrl.GetFrameForStateStart(AnimState.Standup, corpse.FacingAngle);
                    bool morphed = frUp.Frame != null
                        && DrawReanimMorph(atlas, fr.Frame.Value, fr.FlipX, frUp.Frame.Value, frUp.FlipX,
                                           sp, scale, corpseTint, morphT, co1, cow, cpw, cps);
                    if (!morphed)
                    {
                        float wD = 1f - morphT, wU = morphT;
                        DrawSpriteFrame(atlas, fr.Frame.Value, sp, scale, fr.FlipX, corpseTint * wD);
                        if (frUp.Frame != null)
                            DrawSpriteFrame(atlas, frUp.Frame.Value, sp, scale, frUp.FlipX, corpseTint * wU);
                    }
                }
                else
                {
                    DrawSpriteFrame(atlas, fr.Frame.Value, sp, scale, fr.FlipX, corpseTint);
                }

                if (corpse == hoveredCorpse)
                    _hoverBoxCorpse = SpriteFrameAABB(sp, fr.Frame.Value, scale, fr.FlipX);
            }

            // Draw bagging progress bar
            if (corpse.BaggedByUnitID != GameConstants.InvalidUnit && corpse.BaggingProgress > 0f)
            {
                var sp = _renderer.WorldToScreen(corpse.Position, 0f, _camera);
                DrawBaggingProgressBar(sp, corpse.BaggingProgress);
            }
        }
    }

    private FrameResult GetBodyBagFrame(float facingAngle)
    {
        var corpsesAtlasId = AtlasDefs.ResolveAtlasName("Corpses");
        int atlasIdx = corpsesAtlasId;
        if (atlasIdx >= _atlases.Length || !_atlases[atlasIdx].IsLoaded) return default;
        var corpsesAtlas = _atlases[atlasIdx];
        var bodyBagSprite = corpsesAtlas.GetUnit("BodyBag");
        if (bodyBagSprite == null) return default;

        var iconAnim = bodyBagSprite.GetAnim("Icon");
        if (iconAnim == null) return default;

        // Use the static angle resolver so the body-bag picks the right scheme
        // (Old 30/60/300 vs New 0/45/90/270/315) from what's actually authored
        // in this anim. A throwaway AnimController without Init defaults to
        // Old, which silently misses on the now-new-scheme Corpses atlas.
        int spriteAngle = AnimController.ResolveAngleFor(iconAnim, facingAngle, out bool flipX);

        var kfs = iconAnim.GetAngle(spriteAngle);
        if (kfs == null || kfs.Count == 0) return default;

        return new FrameResult { Frame = kfs[0].Frame, FlipX = flipX };
    }

    /// <summary>Pick the keyframe list for the unit's Idle anim using the same
    /// angle-preference fallback that AnimController and DrawUnitIdleSprite use.
    /// Old atlases (VampireFaction, Navarre_Units) author angles 30/60/300,
    /// newer atlases (NecromancerEvolutions) use 0/45/90/270/315 — without this
    /// fallback, a hardcoded GetAngle(30) returns null on the new atlases and
    /// RefFrameHeight stays at its 128 default, scaling units to roughly half
    /// the correct on-screen size.</summary>
    private static List<Render.Keyframe>? PickIdleFrames(Render.AnimationData idle)
    {
        foreach (int pref in new[] { 30, 0, 45, 60, 315, 90, 270, 300 })
        {
            var kfs = idle.GetAngle(pref);
            if (kfs != null && kfs.Count > 0) return kfs;
        }
        // Last resort: any authored angle.
        foreach (var (_, frames) in idle.AngleFrames)
            if (frames.Count > 0) return frames;
        return null;
    }

    private float GetBodyBagRefHeight()
    {
        var corpsesAtlasId = AtlasDefs.ResolveAtlasName("Corpses");
        int atlasIdx = corpsesAtlasId;
        if (atlasIdx >= _atlases.Length || !_atlases[atlasIdx].IsLoaded) return 128f;
        var bodyBagSprite = _atlases[atlasIdx].GetUnit("BodyBag");
        if (bodyBagSprite == null) return 128f;
        var iconAnim = bodyBagSprite.GetAnim("Icon");
        if (iconAnim != null) { var kfs = PickIdleFrames(iconAnim); if (kfs != null && kfs.Count > 0) return kfs[0].Frame.Rect.Height; }
        return 128f;
    }

    private void DrawBaggedCorpse(Corpse corpse)
    {
        var fr = GetBodyBagFrame(corpse.FacingAngle);
        if (fr.Frame == null) return;

        var corpsesAtlasId = AtlasDefs.ResolveAtlasName("Corpses");
        var corpsesAtlas = _atlases[corpsesAtlasId];

        // Bag size is the SAME everywhere it appears (carry / ground / table) —
        // CarryBagScale is the canonical world-height. Doesn't multiply by
        // corpse.SpriteScale: the bag visual shouldn't grow/shrink with the
        // source unit's stature (a bear corpse and a soldier corpse get visually
        // identical bags). The unbagged dead-body sprite still uses SpriteScale
        // — only the bagged form is uniform.
        float refH = GetBodyBagRefHeight();
        float scale = (CarryBagScale * _camera.Zoom) / refH;

        var sp = _renderer.WorldToScreen(corpse.Position, 0f, _camera);
        DrawSpriteFrame(corpsesAtlas, fr.Frame.Value, sp, scale, fr.FlipX, _ambientColor);
    }

    private void DrawBaggedCorpseAt(Vector2 screenPos, float facingAngle, float rotation = 0f)
    {
        var fr = GetBodyBagFrame(facingAngle);
        if (fr.Frame == null) return;

        var corpsesAtlasId = AtlasDefs.ResolveAtlasName("Corpses");
        int atlasIdx = corpsesAtlasId;
        if (atlasIdx >= _atlases.Length || !_atlases[atlasIdx].IsLoaded) return;
        var corpsesAtlas = _atlases[atlasIdx];

        float refH = GetBodyBagRefHeight();
        float scale = (CarryBagScale * _camera.Zoom) / refH; // matches carry / ground bag size
        if (rotation == 0f)
        {
            DrawSpriteFrame(corpsesAtlas, fr.Frame.Value, screenPos, scale, fr.FlipX, _ambientColor);
        }
        else
        {
            // Inline rotation path — DrawSpriteFrame doesn't expose rotation yet
            // (only the table overlay needs it). Replicates DrawSpriteFrame's pivot
            // math + adds the rotation argument. Keep this branch tight; if a
            // second caller ever wants rotation, promote this into DrawSpriteFrame.
            var frame = fr.Frame.Value;
            var tex = corpsesAtlas.GetTextureForFrame(frame);
            if (tex == null) return;
            float pivotX = fr.FlipX ? (1f - frame.PivotX) : frame.PivotX;
            float pivotY = 1f - frame.PivotY;
            var origin = new Vector2(pivotX * frame.Rect.Width, pivotY * frame.Rect.Height);
            var effects = fr.FlipX ? SpriteEffects.FlipHorizontally : SpriteEffects.None;
            _spriteBatch.Draw(tex, screenPos, frame.Rect, _ambientColor, rotation, origin, scale, effects, 0f);
        }
    }

    private void DrawBaggingProgressBar(Vector2 screenPos, float progress)
    {
        float barW = 26f;
        float barH = 3f;
        float barX = screenPos.X - barW / 2f;
        float barY = screenPos.Y - 18f;

        _spriteBatch.Draw(_pixel, new Rectangle((int)barX - 1, (int)barY - 1, (int)barW + 2, (int)barH + 2), new Color(0, 0, 0, 180));
        _spriteBatch.Draw(_pixel, new Rectangle((int)barX, (int)barY, (int)(barW * progress), (int)barH), new Color(220, 180, 40));
    }

    private void DrawBuildProgressBar(Vector2 screenPos, float progress, float worldRadius = 0f)
    {
        float barW, barY;
        if (worldRadius > 0f)
        {
            float screenRadiusX = worldRadius * _camera.Zoom;
            float screenRadiusY = screenRadiusX * _camera.YRatio;
            barW = screenRadiusX * 2f;
            barY = screenPos.Y - screenRadiusY - 6f; // above the top of the circle
        }
        else
        {
            barW = 30f;
            barY = screenPos.Y - 22f;
        }
        float barH = 3f;
        float barX = screenPos.X - barW / 2f;

        _spriteBatch.Draw(_pixel, new Rectangle((int)barX - 1, (int)barY - 1, (int)barW + 2, (int)barH + 2), new Color(0, 0, 0, 180));
        _spriteBatch.Draw(_pixel, new Rectangle((int)barX, (int)barY, (int)(barW * progress), (int)barH), new Color(80, 180, 220));
    }

    private void DrawCarriedBodyBag(int unitIdx, Vector2 unitScreenPos, float unitScale, float facingAngle)
    {
        var fr = GetBodyBagFrame(facingAngle);
        if (fr.Frame == null) return;

        var corpsesAtlasId = AtlasDefs.ResolveAtlasName("Corpses");
        int atlasIdx = corpsesAtlasId;
        if (atlasIdx >= _atlases.Length || !_atlases[atlasIdx].IsLoaded) return;
        var corpsesAtlas = _atlases[atlasIdx];

        float refH = GetBodyBagRefHeight();
        float bagScale = (CarryBagScale * _camera.Zoom) / refH;

        // Flip-aware offset: X offset flips with the sprite
        bool flipX = fr.FlipX;
        float ofsX = flipX ? -CarryOffsetX : CarryOffsetX;

        // Position at weapon hilt point if available
        var unitDef = _gameData.Units.Get(_sim.Units[unitIdx].UnitDefID);
        if (unitDef != null && _unitAnims.TryGetValue(_sim.Units[unitIdx].Id, out var animData))
        {
            var attach = ComputeWeaponAttach(unitIdx, unitDef, animData);
            if (attach.Valid)
            {
                // The bag's spritemeta pivot (0.5, 0.15) sits at the visible bag's
                // natural anchor — the artist put the pivot ON the bag's visible
                // center, NOT at the frame's geometric bottom. So drawing at the
                // hilt screen position lands the visible bag's center directly on
                // the hilt; no anchor-to-center correction needed.
                //
                // HiltHeight is in world units, but the hilt's screen offset must
                // match the sprite's drawn scale (full Zoom — sprites aren't
                // yRatio'd, because the artist baked iso perspective into art).
                // Standard WorldToScreen subtracts `HiltHeight * Zoom * YRatio`,
                // yielding only half the correct offset. WorldToScreenPx takes
                // literal pixels and skips the yRatio fold — this restores the
                // pre-`421fdd3` behavior of `HiltHeight * Zoom / HeightScale`.
                var hiltScreen = _renderer.WorldToScreenPx(attach.HiltWorld, attach.HiltHeight * _camera.Zoom, _camera);
                hiltScreen.X += ofsX;
                hiltScreen.Y += CarryOffsetY; // small fine-tune; can be negative to nudge bag up
                DrawSpriteFrame(corpsesAtlas, fr.Frame.Value, hiltScreen, bagScale, fr.FlipX, _ambientColor);
                return;
            }
        }

        // Fallback: offset-based positioning (when weapon attach data is missing).
        // No centerCorrection because the bag's pivot already sits on the visible
        // bag's center. Estimate hilt at ~30% of unit height (mid-torso) to land
        // the bag near where the hand would be.
        float angleDeg = ((facingAngle % 360f) + 360f) % 360f;
        float offsetPx = 8f * unitScale;
        float hDir = (angleDeg > 90f && angleDeg < 270f) ? -1f : 1f;
        float bagX = unitScreenPos.X + offsetPx * hDir * 0.66f + ofsX;

        float spriteWorldH = (unitDef != null && unitDef.SpriteWorldHeight > 0) ? unitDef.SpriteWorldHeight : 1.8f;
        float spritePixelH = spriteWorldH * _sim.Units[unitIdx].SpriteScale * _camera.Zoom;
        float bagY = unitScreenPos.Y - spritePixelH * 0.30f + CarryOffsetY;

        DrawSpriteFrame(corpsesAtlas, fr.Frame.Value, new Vector2(bagX, bagY), bagScale, fr.FlipX, _ambientColor);
    }

    /// <summary>Resolve the final death-pose frame for a corpse's unit def at a
    /// facing angle. refH is the unit's Idle height so corpse scale matches the
    /// living unit (and the on-ground corpse). Returns false if unavailable.</summary>
    private bool TryGetCorpseDeathFrame(string unitDefID, float facingAngle,
        out SpriteAtlas atlas, out SpriteFrame frame, out bool flipX, out float refH)
    {
        atlas = default!; frame = default; flipX = false; refH = 128f;
        var unitDef = _gameData.Units.Get(unitDefID);
        if (unitDef?.Sprite == null) return false;
        var atlasId = AtlasDefs.ResolveAtlasName(unitDef.Sprite.AtlasName);
        int ai = atlasId;
        if (ai < 0 || ai >= _atlases.Length || !_atlases[ai].IsLoaded) return false;
        atlas = _atlases[ai];
        var spriteData = atlas.GetUnit(unitDef.Sprite.SpriteName);
        if (spriteData == null) return false;
        var death = spriteData.GetAnim("Death");
        if (death == null) return false;
        int spriteAngle = AnimController.ResolveAngleFor(death, facingAngle, out flipX);
        var kfs = death.GetAngle(spriteAngle);
        if (kfs == null || kfs.Count == 0) return false;
        frame = kfs[kfs.Count - 1].Frame; // final death pose (settled corpse)
        var idle = spriteData.GetAnim("Idle");
        if (idle != null) { var ik = PickIdleFrames(idle); if (ik != null && ik.Count > 0) refH = ik[0].Frame.Rect.Height; }
        return true;
    }

    /// <summary>Draw a corpse's death sprite at a screen position, optionally
    /// rotated (used for the table overlay + putdown lerp). Mirrors the body-bag
    /// draw path so it slots in wherever DrawBaggedCorpseAt was used.</summary>
    private void DrawCorpseSpriteAt(string unitDefID, Vector2 screenPos, float facingAngle, float spriteScale, float rotation = 0f)
    {
        if (!TryGetCorpseDeathFrame(unitDefID, facingAngle, out var atlas, out var frame, out bool flipX, out float refH))
            return;
        var tex = atlas.GetTextureForFrame(frame);
        if (tex == null) return;
        var unitDef = _gameData.Units.Get(unitDefID);
        float worldH = (unitDef != null && unitDef.SpriteWorldHeight > 0 ? unitDef.SpriteWorldHeight : 1.8f) * spriteScale;
        float scale = (worldH * _camera.Zoom) / refH;
        float pivotX = flipX ? (1f - frame.PivotX) : frame.PivotX;
        float pivotY = 1f - frame.PivotY; // spritemeta pivots are bottom-left origin
        var origin = new Vector2(pivotX * frame.Rect.Width, pivotY * frame.Rect.Height);
        var effects = flipX ? SpriteEffects.FlipHorizontally : SpriteEffects.None;
        _spriteBatch.Draw(tex, screenPos, frame.Rect, _ambientColor, rotation, origin, scale, effects, 0f);
    }

    /// <summary>Opaque-pixel centroid of a frame, in frame-local top-left pixels.
    /// Used to balance a carried corpse on the carrier's hands. Cached two ways:
    /// in-memory by (texture, rect), and on disk in <c>data/frame_centroids.json</c>
    /// keyed by (atlas name, page, rect) — so the ~85ms GetData read-back on the
    /// huge unit atlases is paid at most once per frame, ever, across all runs.</summary>
    private Vector2 GetFrameCentroid(Microsoft.Xna.Framework.Graphics.Texture2D tex, SpriteFrame frame)
    {
        var key = (tex, frame.Rect);
        if (_frameCentroidCache.TryGetValue(key, out var cached)) return cached;

        if (_persistedCentroids == null) LoadPersistedCentroids();
        string? pkey = CentroidKeyFor(tex, frame);
        if (pkey != null && _persistedCentroids!.TryGetValue(pkey, out var persisted))
        {
            _frameCentroidCache[key] = persisted;
            return persisted;
        }

        int w = frame.Rect.Width, h = frame.Rect.Height;
        Vector2 result = new Vector2(w * 0.5f, h * 0.5f);
        if (w > 0 && h > 0)
        {
            var data = new Color[w * h];
            tex.GetData(0, frame.Rect, data, 0, data.Length);
            double sx = 0, sy = 0; long n = 0;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    if (data[y * w + x].A > 16) { sx += x; sy += y; n++; }
            if (n > 0) result = new Vector2((float)(sx / n), (float)(sy / n));
        }
        _frameCentroidCache[key] = result;
        if (pkey != null)
        {
            _persistedCentroids![pkey] = result;
            _centroidsDirty = true;
            // Persist immediately on a genuinely-new frame (rare) so it survives a
            // crash. Suppressed during the bulk bake, which saves once at the end.
            if (!_bulkCentroidBake) SavePersistedCentroids();
        }
        return result;
    }

    private static string CentroidCachePath => Core.GamePaths.Resolve("data/frame_centroids.json");

    /// <summary>Stable disk key for a frame: atlas name + page index + rect. Independent
    /// of the runtime Texture2D identity so it survives across runs. Null if the
    /// texture isn't part of a loaded atlas.</summary>
    private string? CentroidKeyFor(Microsoft.Xna.Framework.Graphics.Texture2D tex, in SpriteFrame frame)
    {
        int ai = AtlasIdxForTexture(tex);
        if (ai < 0) return null;
        string name = ai < Core.AtlasDefs.Names.Length ? Core.AtlasDefs.Names[ai] : ai.ToString();
        var r = frame.Rect;
        return $"{name}#{frame.TextureIndex}#{r.X},{r.Y},{r.Width},{r.Height}";
    }

    private int AtlasIdxForTexture(Microsoft.Xna.Framework.Graphics.Texture2D tex)
    {
        if (_texToAtlasIdx == null)
        {
            _texToAtlasIdx = new();
            for (int i = 0; i < _atlases.Length; i++)
            {
                var a = _atlases[i];
                if (a == null || !a.IsLoaded) continue;
                foreach (var t in a.Textures)
                    if (t != null) _texToAtlasIdx[t] = i;
            }
        }
        return _texToAtlasIdx.TryGetValue(tex, out int idx) ? idx : -1;
    }

    private void LoadPersistedCentroids()
    {
        _persistedCentroids = new();
        try
        {
            string path = CentroidCachePath;
            if (!File.Exists(path)) return;
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                var s = p.Value.GetString();
                if (string.IsNullOrEmpty(s)) continue;
                int comma = s.IndexOf(',');
                if (comma < 0) continue;
                if (float.TryParse(s.AsSpan(0, comma), System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out float cx) &&
                    float.TryParse(s.AsSpan(comma + 1), System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out float cy))
                    _persistedCentroids[p.Name] = new Vector2(cx, cy);
            }
        }
        catch { /* corrupt/missing cache → recompute lazily */ }
    }

    private void SavePersistedCentroids()
    {
        if (_persistedCentroids == null || !_centroidsDirty) return;
        try
        {
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            var map = new Dictionary<string, string>(_persistedCentroids.Count);
            foreach (var kv in _persistedCentroids)
                map[kv.Key] = kv.Value.X.ToString(ci) + "," + kv.Value.Y.ToString(ci);
            var json = System.Text.Json.JsonSerializer.Serialize(map,
                Necroking.Core.JsonDefaults.Indented);
            File.WriteAllText(CentroidCachePath, json);
            _centroidsDirty = false;
        }
        catch { /* read-only data dir → cache stays in-memory only */ }
    }

    /// <summary>Compute + persist every unit's final death-frame centroid so the disk
    /// cache is complete and no carry ever stalls on a GetData read-back. Run once
    /// offline via <c>--bake-centroids</c>; the resulting file ships with the build.</summary>
    private void BakeAllCorpseCentroids()
    {
        if (_gameData?.Units == null) return;
        if (_persistedCentroids == null) LoadPersistedCentroids();
        _bulkCentroidBake = true;
        int total = 0;
        foreach (var def in _gameData.Units.All())
        {
            if (def?.Sprite == null) continue;
            int ai = Core.AtlasDefs.ResolveAtlasName(def.Sprite.AtlasName);
            if (ai < 0 || ai >= _atlases.Length || !_atlases[ai].IsLoaded) continue;
            var death = _atlases[ai].GetUnit(def.Sprite.SpriteName)?.GetAnim("Death");
            if (death == null) continue;
            foreach (var (_, kfs) in death.AngleFrames)
            {
                if (kfs == null || kfs.Count == 0) continue;
                var frame = kfs[kfs.Count - 1].Frame;
                var tex = _atlases[ai].GetTextureForFrame(frame);
                if (tex == null) continue;
                GetFrameCentroid(tex, frame); // computes + marks dirty
                total++;
            }
        }
        _bulkCentroidBake = false;
        SavePersistedCentroids();
        DebugLog.Log("startup", $"[bake] corpse centroids: {total} frames -> {CentroidCachePath}");
    }

    /// <summary>Draw the carried corpse balanced on the carrier's hands. Resolves
    /// orientation through the CARRIER's own controller (lockstep with the
    /// necromancer's facing), records that exact angle+flip on the corpse so it
    /// survives the drop unchanged, then renders the centroid-pegged frame.</summary>
    private void DrawCarriedCorpse(int unitIdx, Vector2 unitScreenPos)
    {
        var cc = _sim.FindCorpseByID(_sim.Units[unitIdx].CarryingCorpseID);
        if (cc == null) return;
        if (!_unitAnims.TryGetValue(_sim.Units[unitIdx].Id, out var carrierAnim)) return;

        var corpseDef = _gameData.Units.Get(cc.UnitDefID);
        if (corpseDef?.Sprite == null) return;
        var atlasId = AtlasDefs.ResolveAtlasName(corpseDef.Sprite.AtlasName);
        int ai = atlasId;
        if (ai < 0 || ai >= _atlases.Length || !_atlases[ai].IsLoaded) return;
        var death = _atlases[ai].GetUnit(corpseDef.Sprite.SpriteName)?.GetAnim("Death");
        if (death == null) return;

        // Resolve through the CARRIER's controller (same scheme + hysteresis as the
        // necromancer's body) so they snap together. Fall back to the corpse's own
        // resolution only if its art lacks that angle (e.g. a different-scheme
        // animal). Freeze the result on the corpse so the dropped pose matches.
        float carryFacing = _sim.Units[unitIdx].FacingAngle;
        int angle = carrierAnim.Ctrl.ResolveAngle(carryFacing, out bool flipX);
        if (death.GetAngle(angle) is not { Count: > 0 })
            angle = AnimController.ResolveAngleFor(death, carryFacing, out flipX);
        cc.CarryDisplayAngle = angle;
        cc.CarryDisplayFlip = flipX;

        // Hand/carry anchor — weapon hilt (the carry pose holds both hands out front).
        Vector2 pos = unitScreenPos;
        var carrierDef = _gameData.Units.Get(_sim.Units[unitIdx].UnitDefID);
        if (carrierDef != null)
        {
            var attach = ComputeWeaponAttach(unitIdx, carrierDef, carrierAnim);
            if (attach.Valid)
                pos = _renderer.WorldToScreenPx(attach.HiltWorld, attach.HiltHeight * _camera.Zoom, _camera);
        }
        pos.Y += CarriedCorpseHandOffsetY;

        DrawCorpseCarriedFrame(cc, pos);
    }

    /// <summary>Render a corpse using its frozen carry orientation
    /// (<see cref="Corpse.CarryDisplayAngle"/>/Flip), centroid-pegged to
    /// <paramref name="screenPos"/>. Shared by the carry, the ground put-down,
    /// and the settled-on-ground draw so all three are pixel-identical — no jump
    /// at hand-off and the placed corpse keeps its carried pose.</summary>
    private void DrawCorpseCarriedFrame(Corpse cc, Vector2 screenPos)
    {
        if (cc.CarryDisplayAngle < 0) return;
        var corpseDef = _gameData.Units.Get(cc.UnitDefID);
        if (corpseDef?.Sprite == null) return;
        var atlasId = AtlasDefs.ResolveAtlasName(corpseDef.Sprite.AtlasName);
        int ai = atlasId;
        if (ai < 0 || ai >= _atlases.Length || !_atlases[ai].IsLoaded) return;
        var atlas = _atlases[ai];
        var spriteData = atlas.GetUnit(corpseDef.Sprite.SpriteName);
        var death = spriteData?.GetAnim("Death");
        var kfs = death?.GetAngle(cc.CarryDisplayAngle);
        if (kfs == null || kfs.Count == 0) return;
        var frame = kfs[kfs.Count - 1].Frame;
        var tex = atlas.GetTextureForFrame(frame);
        if (tex == null) return;

        float refH = 128f;
        var idle = spriteData!.GetAnim("Idle");
        if (idle != null) { var ik = PickIdleFrames(idle); if (ik != null && ik.Count > 0) refH = ik[0].Frame.Rect.Height; }
        float worldH = (corpseDef.SpriteWorldHeight > 0 ? corpseDef.SpriteWorldHeight : 1.8f) * cc.SpriteScale;
        float scale = (worldH * _camera.Zoom) / refH;

        // Dissolve fade (mirrors DrawCorpses) so a placed corpse still fades when consumed.
        int alphaInt = 255;
        if (cc.Dissolving)
        {
            float t = cc.DissolveTimer / 2f;
            float a = 255f * (1f - t);
            if ((int)(cc.DissolveTimer * 8f) % 2 == 0) a *= 0.3f;
            alphaInt = (int)MathUtil.Clamp(a, 0f, 255f);
        }
        byte alpha = (byte)alphaInt;
        Color tint = MultiplyColor(new Color(alpha, alpha, alpha, alpha), _ambientColor);

        bool flipX = cc.CarryDisplayFlip;
        var centroid = GetFrameCentroid(tex, frame);
        float originX = flipX ? (frame.Rect.Width - centroid.X) : centroid.X;
        var origin = new Vector2(originX, centroid.Y);
        var effects = flipX ? SpriteEffects.FlipHorizontally : SpriteEffects.None;
        _spriteBatch.Draw(tex, screenPos, frame.Rect, tint, 0f, origin, scale, effects, 0f);

        // Hover-highlight: this draw uses a centroid origin (not the pivot), so
        // build the box from that rather than SpriteFrameAABB.
        if (_gameData.Settings.Tooltips.ShowHoverHighlight
            && _hoveredCorpseIdx >= 0 && _hoveredCorpseIdx < _sim.Corpses.Count
            && ReferenceEquals(_sim.Corpses[_hoveredCorpseIdx], cc))
        {
            float bw = frame.Rect.Width * scale, bh = frame.Rect.Height * scale;
            _hoverBoxCorpse = new Rectangle(
                (int)(screenPos.X - origin.X * scale), (int)(screenPos.Y - origin.Y * scale),
                (int)bw, (int)bh);
        }
    }

    /// <summary>Carried visual anchored on the carrier — bag or raw corpse per
    /// GameConstants.UseBodyBag.</summary>
    private void DrawCarriedVisual(int unitIdx, Vector2 unitScreenPos, float unitScale)
    {
        if (GameConstants.UseBodyBag)
            DrawCarriedBodyBag(unitIdx, unitScreenPos, unitScale, _sim.Units[unitIdx].FacingAngle);
        else
            DrawCarriedCorpse(unitIdx, unitScreenPos);
    }

    /// <summary>Carried visual at an explicit screen position (table-putdown lerp
    /// or ground drop) — bag or raw corpse per GameConstants.UseBodyBag.</summary>
    private void DrawCarriedVisualAt(int unitIdx, Vector2 screenPos, float facingAngle, float rotation = 0f)
    {
        if (GameConstants.UseBodyBag)
        {
            DrawBaggedCorpseAt(screenPos, facingAngle, rotation);
            return;
        }
        var cc = _sim.FindCorpseByID(_sim.Units[unitIdx].CarryingCorpseID);
        if (cc != null)
            DrawCorpseSpriteAt(cc.UnitDefID, screenPos, facingAngle, cc.SpriteScale, rotation);
    }
}
