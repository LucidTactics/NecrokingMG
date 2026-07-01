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
partial class GameRenderer
{
    // Draw the reanimating body morphing death-pose -> standup-start via the SDF morph shader
    // (the amoeba gain/shed-pixels morph): interpolates the two poses' distance fields, fills with
    // the crossfaded body color + green energy in the bulge gaps, and traces a pulsing green outline
    // on the morphed edge. Returns false if the shader / morph data is unavailable (caller falls
    // back to an alpha crossfade). Draws in its own batch (like DrawSpriteOutline), then restores.
    private bool DrawReanimMorph(SpriteAtlas atlasDeath, int deathAtlasId, SpriteFrame death, bool deathFlip,
        SpriteAtlas atlasStandup, int standupAtlasId, SpriteFrame standup, bool standupFlip,
        Vector2 sp, float scale, Color tint, float morphT,
        HdrColor outline, float outlineWidth, float pulseWidth, float pulseSpeed)
    {
        if (_g._morphSdfEffect == null) return false;
        var md = _g._reanimMorph.GetOrBuild(_g.GraphicsDevice, atlasDeath, deathAtlasId, death, deathFlip,
                                            atlasStandup, standupAtlasId, standup, standupFlip);
        if (!md.Valid || md.ColorA == null || md.ColorB == null || md.Sdf == null) return false;

        float pulse = 0.5f + 0.5f * MathF.Sin(_g._gameTime * pulseSpeed * 2f * MathF.PI);
        var greenHue = new Vector3(outline.R / 255f, outline.G / 255f, outline.B / 255f);
        float outlineStrength = (outline.A / 255f) * (0.3f + 0.3f * pulse); // fade-in (alpha) + pulse

        var fx = _g._morphSdfEffect;
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
        else { _g.GraphicsDevice.Textures[1] = md.ColorB; _g.GraphicsDevice.SamplerStates[1] = SamplerState.LinearClamp; }
        var pS = fx.Parameters["SdfMap"];
        if (pS != null) pS.SetValue(md.Sdf);
        else { _g.GraphicsDevice.Textures[2] = md.Sdf; _g.GraphicsDevice.SamplerStates[2] = SamplerState.LinearClamp; }

        _g._spriteBatch.End();
        _g._spriteBatch.Begin(SpriteSortMode.Immediate, BlendState.AlphaBlend, SamplerState.LinearClamp, null, null, fx);
        _g._spriteBatch.Draw(md.ColorA, sp, null, tint, 0f, new Vector2(md.PivotX, md.PivotY), scale, SpriteEffects.None, 0f);
        _g._spriteBatch.End();
        _g._spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.LinearClamp);
        return true;
    }

    // Cache of anim controllers for reanimation TARGET types (the zombie a corpse rises into), so the
    // morph can read the target's standup-start frame + atlas without rebuilding each frame. Keyed by
    // unit def id; null is cached for unknown/sprite-less defs. Static-pose lookups only (never ticked).
    private readonly Dictionary<string, Game1.UnitAnimData?> _reanimTargetAnims = new();

    private Game1.UnitAnimData? GetReanimTargetAnim(string defId)
    {
        if (string.IsNullOrEmpty(defId)) return null;
        if (_reanimTargetAnims.TryGetValue(defId, out var cached)) return cached;
        Game1.UnitAnimData? result = null;
        var def = _g._gameData.Units.Get(defId);
        if (def?.Sprite != null)
        {
            int atlasId = AtlasDefs.ResolveAtlasName(def.Sprite.AtlasName);
            if (atlasId >= 0 && atlasId < _g._atlases.Length && _g._atlases[atlasId].IsLoaded)
            {
                var spriteData = _g._atlases[atlasId].GetUnit(def.Sprite.SpriteName);
                if (spriteData != null)
                {
                    var ctrl = new AnimController();
                    ctrl.Init(spriteData);
                    if (_g._animMeta.Count > 0)
                        ctrl.SetAnimMeta(_g._animMeta, def.Sprite.SpriteName);
                    float refH = 128f;
                    var idle = spriteData.GetAnim("Idle");
                    if (idle != null) { var kfs = PickIdleFrames(idle); if (kfs != null && kfs.Count > 0) refH = kfs[0].Frame.Rect.Height; }
                    result = new Game1.UnitAnimData { Ctrl = ctrl, AtlasID = atlasId, RefFrameHeight = refH, CachedDefID = defId };
                }
            }
        }
        _reanimTargetAnims[defId] = result;
        return result;
    }

    private void DrawCorpses()
    {
        // Hover-highlight target (captured below as its sprite is drawn).
        Corpse? hoveredCorpse = (_g._gameData.Settings.Tooltips.ShowHoverHighlight
            && _g._hoveredCorpseIdx >= 0 && _g._hoveredCorpseIdx < _g._sim.Corpses.Count)
            ? _g._sim.Corpses[_g._hoveredCorpseIdx] : null;

        foreach (var corpse in _g._sim.Corpses)
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
                DrawCorpseCarriedFrame(corpse, _g._renderer.WorldToScreen(corpse.Position, corpse.Z, _g._camera));
                continue;
            }

            var unitDef = _g._gameData.Units.Get(corpse.UnitDefID);
            if (unitDef?.Sprite == null) continue;
            var atlasId = AtlasDefs.ResolveAtlasName(unitDef.Sprite.AtlasName);
            var atlas = _g._atlases[atlasId];
            if (!atlas.IsLoaded) continue;

            // Get or create corpse anim controller
            if (!_g._corpseAnims.TryGetValue(corpse.CorpseID, out var cad))
            {
                var spriteData = atlas.GetUnit(unitDef.Sprite.SpriteName);
                if (spriteData == null) continue;
                var ctrl = new AnimController();
                ctrl.Init(spriteData);
                if (_g._animMeta.Count > 0)
                    ctrl.SetAnimMeta(_g._animMeta, unitDef.Sprite.SpriteName);
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

                cad = new Game1.UnitAnimData { Ctrl = ctrl, AtlasID = atlasId, RefFrameHeight = refH, CachedDefID = corpse.UnitDefID };
                _g._corpseAnims[corpse.CorpseID] = cad;
            }

            // When corpse lands from knockback arc, snap to final death frame
            if (!corpse.InPhysics && cad.Ctrl.CurrentState == AnimState.Fall)
                cad.Ctrl.ForceStateAtEnd(AnimState.Death);

            bool reanimating = corpse.ReanimInstanceId > 0;
            if (!cad.Ctrl.IsAnimFinished && !_g._paused)
                cad.Ctrl.Update(MathF.Min(_g._rawDt, 1f / 20f) * _g._timeScale);

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
                float pixelH = worldH * _g._camera.Zoom;
                float scale = pixelH / cad.RefFrameHeight;

                var sp = _g._renderer.WorldToScreen(corpse.Position, corpse.Z, _g._camera);
                Color corpseTint = MultiplyColor(new Color(alpha, alpha, alpha, alpha), _g._ambientColor);

                // While reanimating, MORPH the body from its death pose to the Standup START pose
                // over the build-up — a true SDF "amoeba" morph (silhouette gains/sheds pixels), so
                // it visibly gathers before rising and hands off seamlessly to the risen unit, with
                // a pulsing green outline tracing the morphed edge. Falls back to an alpha crossfade.
                if (reanimating &&
                    _g._reanimFx.TryGetCorpseOutline(corpse.ReanimInstanceId, out var co1, out var co2,
                        out var cow, out var cpw, out var cps, out float morphT))
                {
                    // Target the RISEN ZOMBIE's standup-start pose + sprite/atlas (not the corpse's own
                    // type), so the morph ends on — and hands off seamlessly to — whatever actually rises,
                    // including cross-type raises. Falls back to the corpse's own standup if the target
                    // zombie type/sprite is unavailable.
                    SpriteAtlas upAtlas = atlas; int upAtlasId = atlasId;
                    AnimController upCtrl = cad.Ctrl; float upScale = scale;
                    var ztarget = GetReanimTargetAnim(corpse.ReanimZombieDefId);
                    if (ztarget != null && ztarget.Value.AtlasID >= 0 && ztarget.Value.AtlasID < _g._atlases.Length
                        && _g._atlases[ztarget.Value.AtlasID].IsLoaded)
                    {
                        var zt = ztarget.Value;
                        var zdef = _g._gameData.Units.Get(corpse.ReanimZombieDefId);
                        upAtlas = _g._atlases[zt.AtlasID]; upAtlasId = zt.AtlasID; upCtrl = zt.Ctrl;
                        float zWorldH = (zdef != null && zdef.SpriteWorldHeight > 0 ? zdef.SpriteWorldHeight : 1.8f)
                                        * (zdef?.SpriteScale ?? 1f);
                        upScale = (zWorldH * _g._camera.Zoom) / zt.RefFrameHeight;
                    }
                    var frUp = upCtrl.GetFrameForStateStart(AnimState.Standup, corpse.FacingAngle);
                    // Lerp the body's display scale corpse->zombie so it ends at the zombie's real size
                    // (no size pop at the hand-off); a no-op when the two share a scale.
                    float morphScale = scale + (upScale - scale) * morphT;
                    // The SDF morph is opt-in (Performance.ReanimMorph, off by default) — its build is
                    // heavy; when off, fall through to the cheap alpha crossfade below.
                    bool morphed = _g._gameData.Settings.Performance.ReanimMorph
                        && frUp.Frame != null
                        && DrawReanimMorph(atlas, atlasId, fr.Frame.Value, fr.FlipX,
                                           upAtlas, upAtlasId, frUp.Frame.Value, frUp.FlipX,
                                           sp, morphScale, corpseTint, morphT, co1, cow, cpw, cps);
                    if (!morphed)
                    {
                        DrawSpriteFrame(atlas, fr.Frame.Value, sp, scale, fr.FlipX, corpseTint * (1f - morphT));
                        if (frUp.Frame != null)
                            DrawSpriteFrame(upAtlas, frUp.Frame.Value, sp, upScale, frUp.FlipX, corpseTint * morphT);
                    }
                }
                else
                {
                    DrawSpriteFrame(atlas, fr.Frame.Value, sp, scale, fr.FlipX, corpseTint);
                }

                if (corpse == hoveredCorpse)
                    _g._hoverBoxCorpse = SpriteFrameAABB(sp, fr.Frame.Value, scale, fr.FlipX);
            }

            // Draw bagging progress bar
            if (corpse.BaggedByUnitID != GameConstants.InvalidUnit && corpse.BaggingProgress > 0f)
            {
                var sp = _g._renderer.WorldToScreen(corpse.Position, 0f, _g._camera);
                DrawBaggingProgressBar(sp, corpse.BaggingProgress);
            }
        }
    }

    private FrameResult GetBodyBagFrame(float facingAngle)
    {
        var corpsesAtlasId = AtlasDefs.ResolveAtlasName("Corpses");
        int atlasIdx = corpsesAtlasId;
        if (atlasIdx >= _g._atlases.Length || !_g._atlases[atlasIdx].IsLoaded) return default;
        var corpsesAtlas = _g._atlases[atlasIdx];
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
    internal static List<Render.Keyframe>? PickIdleFrames(Render.AnimationData idle)
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
        if (atlasIdx >= _g._atlases.Length || !_g._atlases[atlasIdx].IsLoaded) return 128f;
        var bodyBagSprite = _g._atlases[atlasIdx].GetUnit("BodyBag");
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
        var corpsesAtlas = _g._atlases[corpsesAtlasId];

        // Bag size is the SAME everywhere it appears (carry / ground / table) —
        // CarryBagScale is the canonical world-height. Doesn't multiply by
        // corpse.SpriteScale: the bag visual shouldn't grow/shrink with the
        // source unit's stature (a bear corpse and a soldier corpse get visually
        // identical bags). The unbagged dead-body sprite still uses SpriteScale
        // — only the bagged form is uniform.
        float refH = GetBodyBagRefHeight();
        float scale = (CarryBagScale * _g._camera.Zoom) / refH;

        var sp = _g._renderer.WorldToScreen(corpse.Position, 0f, _g._camera);
        DrawSpriteFrame(corpsesAtlas, fr.Frame.Value, sp, scale, fr.FlipX, _g._ambientColor);
    }

    private void DrawBaggedCorpseAt(Vector2 screenPos, float facingAngle, float rotation = 0f)
    {
        var fr = GetBodyBagFrame(facingAngle);
        if (fr.Frame == null) return;

        var corpsesAtlasId = AtlasDefs.ResolveAtlasName("Corpses");
        int atlasIdx = corpsesAtlasId;
        if (atlasIdx >= _g._atlases.Length || !_g._atlases[atlasIdx].IsLoaded) return;
        var corpsesAtlas = _g._atlases[atlasIdx];

        float refH = GetBodyBagRefHeight();
        float scale = (CarryBagScale * _g._camera.Zoom) / refH; // matches carry / ground bag size
        if (rotation == 0f)
        {
            DrawSpriteFrame(corpsesAtlas, fr.Frame.Value, screenPos, scale, fr.FlipX, _g._ambientColor);
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
            _g._spriteBatch.Draw(tex, screenPos, frame.Rect, _g._ambientColor, rotation, origin, scale, effects, 0f);
        }
    }

    private void DrawBaggingProgressBar(Vector2 screenPos, float progress)
    {
        float barW = 26f;
        float barH = 3f;
        float barX = screenPos.X - barW / 2f;
        float barY = screenPos.Y - 18f;

        _g._spriteBatch.Draw(_g._pixel, new Rectangle((int)barX - 1, (int)barY - 1, (int)barW + 2, (int)barH + 2), new Color(0, 0, 0, 180));
        _g._spriteBatch.Draw(_g._pixel, new Rectangle((int)barX, (int)barY, (int)(barW * progress), (int)barH), new Color(220, 180, 40));
    }

    private void DrawBuildProgressBar(Vector2 screenPos, float progress, float worldRadius = 0f)
    {
        float barW, barY;
        if (worldRadius > 0f)
        {
            float screenRadiusX = worldRadius * _g._camera.Zoom;
            float screenRadiusY = screenRadiusX * _g._camera.YRatio;
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

        _g._spriteBatch.Draw(_g._pixel, new Rectangle((int)barX - 1, (int)barY - 1, (int)barW + 2, (int)barH + 2), new Color(0, 0, 0, 180));
        _g._spriteBatch.Draw(_g._pixel, new Rectangle((int)barX, (int)barY, (int)(barW * progress), (int)barH), new Color(80, 180, 220));
    }

    private void DrawCarriedBodyBag(int unitIdx, Vector2 unitScreenPos, float unitScale, float facingAngle)
    {
        var fr = GetBodyBagFrame(facingAngle);
        if (fr.Frame == null) return;

        var corpsesAtlasId = AtlasDefs.ResolveAtlasName("Corpses");
        int atlasIdx = corpsesAtlasId;
        if (atlasIdx >= _g._atlases.Length || !_g._atlases[atlasIdx].IsLoaded) return;
        var corpsesAtlas = _g._atlases[atlasIdx];

        float refH = GetBodyBagRefHeight();
        float bagScale = (CarryBagScale * _g._camera.Zoom) / refH;

        // Flip-aware offset: X offset flips with the sprite
        bool flipX = fr.FlipX;
        float ofsX = flipX ? -CarryOffsetX : CarryOffsetX;

        // Position at weapon hilt point if available
        var unitDef = _g._gameData.Units.Get(_g._sim.Units[unitIdx].UnitDefID);
        if (unitDef != null && _g._unitAnims.TryGetValue(_g._sim.Units[unitIdx].Id, out var animData))
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
                var hiltScreen = _g._renderer.WorldToScreenPx(attach.HiltWorld, attach.HiltHeight * _g._camera.Zoom, _g._camera);
                hiltScreen.X += ofsX;
                hiltScreen.Y += CarryOffsetY; // small fine-tune; can be negative to nudge bag up
                DrawSpriteFrame(corpsesAtlas, fr.Frame.Value, hiltScreen, bagScale, fr.FlipX, _g._ambientColor);
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
        float spritePixelH = spriteWorldH * _g._sim.Units[unitIdx].SpriteScale * _g._camera.Zoom;
        float bagY = unitScreenPos.Y - spritePixelH * 0.30f + CarryOffsetY;

        DrawSpriteFrame(corpsesAtlas, fr.Frame.Value, new Vector2(bagX, bagY), bagScale, fr.FlipX, _g._ambientColor);
    }

    /// <summary>Resolve the final death-pose frame for a corpse's unit def at a
    /// facing angle. refH is the unit's Idle height so corpse scale matches the
    /// living unit (and the on-ground corpse). Returns false if unavailable.</summary>
    private bool TryGetCorpseDeathFrame(string unitDefID, float facingAngle,
        out SpriteAtlas atlas, out SpriteFrame frame, out bool flipX, out float refH)
    {
        atlas = default!; frame = default; flipX = false; refH = 128f;
        var unitDef = _g._gameData.Units.Get(unitDefID);
        if (unitDef?.Sprite == null) return false;
        var atlasId = AtlasDefs.ResolveAtlasName(unitDef.Sprite.AtlasName);
        int ai = atlasId;
        if (ai < 0 || ai >= _g._atlases.Length || !_g._atlases[ai].IsLoaded) return false;
        atlas = _g._atlases[ai];
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
        var unitDef = _g._gameData.Units.Get(unitDefID);
        float worldH = (unitDef != null && unitDef.SpriteWorldHeight > 0 ? unitDef.SpriteWorldHeight : 1.8f) * spriteScale;
        float scale = (worldH * _g._camera.Zoom) / refH;
        float pivotX = flipX ? (1f - frame.PivotX) : frame.PivotX;
        float pivotY = 1f - frame.PivotY; // spritemeta pivots are bottom-left origin
        var origin = new Vector2(pivotX * frame.Rect.Width, pivotY * frame.Rect.Height);
        var effects = flipX ? SpriteEffects.FlipHorizontally : SpriteEffects.None;
        _g._spriteBatch.Draw(tex, screenPos, frame.Rect, _g._ambientColor, rotation, origin, scale, effects, 0f);
    }

    /// <summary>Opaque-pixel centroid of a frame, in frame-local top-left pixels.
    /// Used to balance a carried corpse on the carrier's hands. Cached two ways:
    /// in-memory by (texture, rect), and on disk in <c>cache/frame_centroids.json</c>
    /// keyed by (atlas name, page, rect) — so the ~85ms GetData read-back on the
    /// huge unit atlases is paid at most once per frame, ever, across all runs.</summary>
    private Vector2 GetFrameCentroid(Microsoft.Xna.Framework.Graphics.Texture2D tex, SpriteFrame frame)
    {
        var key = (tex, frame.Rect);
        if (_g._frameCentroidCache.TryGetValue(key, out var cached)) return cached;

        if (_g._persistedCentroids == null) LoadPersistedCentroids();
        string? pkey = CentroidKeyFor(tex, frame);
        if (pkey != null && _g._persistedCentroids!.TryGetValue(pkey, out var persisted))
        {
            _g._frameCentroidCache[key] = persisted;
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
        _g._frameCentroidCache[key] = result;
        if (pkey != null)
        {
            _g._persistedCentroids![pkey] = result;
            _g._centroidsDirty = true;
            // Persist immediately on a genuinely-new frame (rare) so it survives a
            // crash. Suppressed during the bulk bake, which saves once at the end.
            if (!_g._bulkCentroidBake) SavePersistedCentroids();
        }
        return result;
    }

    private static string CentroidCachePath => Core.GamePaths.Resolve(Core.GamePaths.FrameCentroidsJson);

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
        if (_g._texToAtlasIdx == null)
        {
            _g._texToAtlasIdx = new();
            for (int i = 0; i < _g._atlases.Length; i++)
            {
                var a = _g._atlases[i];
                if (a == null || !a.IsLoaded) continue;
                foreach (var t in a.Textures)
                    if (t != null) _g._texToAtlasIdx[t] = i;
            }
        }
        return _g._texToAtlasIdx.TryGetValue(tex, out int idx) ? idx : -1;
    }

    private void LoadPersistedCentroids()
    {
        _g._persistedCentroids = new();
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
                    _g._persistedCentroids[p.Name] = new Vector2(cx, cy);
            }
        }
        catch { /* corrupt/missing cache → recompute lazily */ }
    }

    private void SavePersistedCentroids()
    {
        if (_g._persistedCentroids == null || !_g._centroidsDirty) return;
        try
        {
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            var map = new Dictionary<string, string>(_g._persistedCentroids.Count);
            foreach (var kv in _g._persistedCentroids)
                map[kv.Key] = kv.Value.X.ToString(ci) + "," + kv.Value.Y.ToString(ci);
            var json = System.Text.Json.JsonSerializer.Serialize(map,
                Necroking.Core.JsonDefaults.Indented);
            Directory.CreateDirectory(Core.GamePaths.Resolve(Core.GamePaths.CacheDir));
            File.WriteAllText(CentroidCachePath, json);
            _g._centroidsDirty = false;
        }
        catch { /* read-only data dir → cache stays in-memory only */ }
    }

    /// <summary>Compute + persist every unit's final death-frame centroid so the disk
    /// cache is complete and no carry ever stalls on a GetData read-back. Run once
    /// offline via <c>--bake-centroids</c>; the resulting file ships with the build.</summary>
    internal void BakeAllCorpseCentroids()
    {
        if (_g._gameData?.Units == null) return;
        if (_g._persistedCentroids == null) LoadPersistedCentroids();
        _g._bulkCentroidBake = true;
        int total = 0;
        foreach (var def in _g._gameData.Units.All())
        {
            if (def?.Sprite == null) continue;
            int ai = Core.AtlasDefs.ResolveAtlasName(def.Sprite.AtlasName);
            if (ai < 0 || ai >= _g._atlases.Length || !_g._atlases[ai].IsLoaded) continue;
            var death = _g._atlases[ai].GetUnit(def.Sprite.SpriteName)?.GetAnim("Death");
            if (death == null) continue;
            foreach (var (_, kfs) in death.AngleFrames)
            {
                if (kfs == null || kfs.Count == 0) continue;
                var frame = kfs[kfs.Count - 1].Frame;
                var tex = _g._atlases[ai].GetTextureForFrame(frame);
                if (tex == null) continue;
                GetFrameCentroid(tex, frame); // computes + marks dirty
                total++;
            }
        }
        _g._bulkCentroidBake = false;
        SavePersistedCentroids();
        DebugLog.Log("startup", $"[bake] corpse centroids: {total} frames -> {CentroidCachePath}");
    }

    private readonly struct MorphPrewarmJob
    {
        public readonly SpriteAtlas DeathAtlas; public readonly int DeathAtlasId;
        public readonly SpriteFrame Death; public readonly bool DeathFlip;
        public readonly SpriteAtlas StandupAtlas; public readonly int StandupAtlasId;
        public readonly SpriteFrame Standup; public readonly bool StandupFlip;
        public MorphPrewarmJob(SpriteAtlas da, int dai, SpriteFrame d, bool df,
                               SpriteAtlas sa, int sai, SpriteFrame s, bool sf)
        { DeathAtlas = da; DeathAtlasId = dai; Death = d; DeathFlip = df;
          StandupAtlas = sa; StandupAtlasId = sai; Standup = s; StandupFlip = sf; }
    }

    private readonly Queue<MorphPrewarmJob> _morphPrewarmQueue = new();

    /// <summary>Collect (but don't yet build) every reanimation pose-morph the game can
    /// hit — death pose → standup-start SDF morph, one per unit type/facing. The actual
    /// build (<see cref="ReanimMorph.GetOrBuild"/>) is heavy — two GetData GPU read-backs
    /// + two distance transforms + three texture uploads — and was paid lazily on the
    /// morph's first draw frame, hitching the summon. We can't do them all at world-load
    /// (that froze startup), and the morph textures are live GPU resources (can't be
    /// serialized like the centroid cache), so instead we enqueue cheap descriptors here
    /// (frame lookups only) and drain them one heavy build per frame in
    /// <see cref="TickReanimMorphPrewarm"/>. Covers every def with both a Death and a
    /// Standup anim (any such corpse can be raised).</summary>
    internal void QueueReanimMorphPrewarm()
    {
        _morphPrewarmQueue.Clear();
        if (_g._morphSdfEffect == null || _g._gameData?.Units == null) return;
        var seen = new HashSet<string>();
        foreach (var def in _g._gameData.Units.All())
        {
            if (def?.Sprite == null) continue;
            int cai = Core.AtlasDefs.ResolveAtlasName(def.Sprite.AtlasName);
            if (cai < 0 || cai >= _g._atlases.Length || !_g._atlases[cai].IsLoaded) continue;
            var cAtlas = _g._atlases[cai];
            var cSprite = cAtlas.GetUnit(def.Sprite.SpriteName);
            if (cSprite?.GetAnim("Death") == null) continue;   // must have a death pose to morph FROM

            // The morph now targets the RISEN ZOMBIE's standup, so prewarm corpse-death -> zombie-standup
            // (across atlases). Group-based zombie types resolve randomly at runtime; we prewarm one
            // representative and let any other member build lazily on first raise.
            string zid = Necroking.Game.TableCraftingSystem.ResolveZombieUnitID(_g._gameData, def.Id);
            var ztarget = GetReanimTargetAnim(zid);
            if (ztarget == null) continue;                     // no resolvable zombie -> can't reanimate
            var zt = ztarget.Value;
            if (zt.AtlasID < 0 || zt.AtlasID >= _g._atlases.Length || !_g._atlases[zt.AtlasID].IsLoaded) continue;
            var zAtlas = _g._atlases[zt.AtlasID];

            var ctrl = new AnimController();
            ctrl.Init(cSprite);
            if (_g._animMeta.Count > 0) ctrl.SetAnimMeta(_g._animMeta, def.Sprite.SpriteName);
            ctrl.ForceStateAtEnd(AnimState.Death);

            // Sample a 30° facing ring — covers every authored Death/Standup angle+flip
            // pair the runtime resolver can pick. Duplicate descriptors are harmless:
            // GetOrBuild dedupes by key (a repeat is a ~free cache hit at drain time).
            for (int deg = 0; deg < 360; deg += 30)
            {
                var fr = ctrl.GetCurrentFrame(deg);                                // corpse death
                var frUp = zt.Ctrl.GetFrameForStateStart(AnimState.Standup, deg);   // zombie standup
                if (fr.Frame == null || frUp.Frame == null) continue;
                var d = fr.Frame.Value; var s = frUp.Frame.Value;
                // Key must match ReanimMorph's cache key (incl. the two atlas ids).
                string key = $"{cai}#{d.TextureIndex}:{d.Rect.X},{d.Rect.Y},{d.Rect.Width},{d.Rect.Height},{fr.FlipX}|"
                           + $"{zt.AtlasID}#{s.TextureIndex}:{s.Rect.X},{s.Rect.Y},{s.Rect.Width},{s.Rect.Height},{frUp.FlipX}";
                if (!seen.Add(key)) continue;
                _morphPrewarmQueue.Enqueue(new MorphPrewarmJob(cAtlas, cai, d, fr.FlipX, zAtlas, zt.AtlasID, s, frUp.FlipX));
            }
        }
        DebugLog.Log("startup", $"[prewarm] reanim morph jobs queued: {_morphPrewarmQueue.Count}");
    }

    /// <summary>Drain the reanim-morph prewarm queue with a per-frame time budget so the
    /// builds spread over a few seconds of gameplay instead of freezing one frame. The
    /// budget is checked before each job, so at most one heavy build runs per frame (a
    /// single build ≈ the cost of one lazy raise); duplicate cache-hit jobs are ~free and
    /// many drain at once. Cheap no-op once empty. Call once per frame from Update.</summary>
    internal void TickReanimMorphPrewarm()
    {
        if (_morphPrewarmQueue.Count == 0) return;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        // ~4ms/frame budget — a few builds per frame, invisible against a 16ms frame,
        // so the (now deduped) queue warms in a couple seconds. Checked before each job,
        // so at least one always runs even if a single build overruns the budget.
        while (_morphPrewarmQueue.Count > 0 && sw.Elapsed.TotalMilliseconds < 4.0)
        {
            var j = _morphPrewarmQueue.Dequeue();
            _g._reanimMorph.GetOrBuild(_g.GraphicsDevice, j.DeathAtlas, j.DeathAtlasId, j.Death, j.DeathFlip,
                                       j.StandupAtlas, j.StandupAtlasId, j.Standup, j.StandupFlip);
        }
        if (_morphPrewarmQueue.Count == 0)
            DebugLog.Log("startup", $"[prewarm] reanim morphs done: {_g._reanimMorph.Count} cached");
    }

    /// <summary>Draw the carried corpse balanced on the carrier's hands. Resolves
    /// orientation through the CARRIER's own controller (lockstep with the
    /// necromancer's facing), records that exact angle+flip on the corpse so it
    /// survives the drop unchanged, then renders the centroid-pegged frame.</summary>
    private void DrawCarriedCorpse(int unitIdx, Vector2 unitScreenPos)
    {
        var cc = _g._sim.FindCorpseByID(_g._sim.Units[unitIdx].CarryingCorpseID);
        if (cc == null) return;
        if (!_g._unitAnims.TryGetValue(_g._sim.Units[unitIdx].Id, out var carrierAnim)) return;

        var corpseDef = _g._gameData.Units.Get(cc.UnitDefID);
        if (corpseDef?.Sprite == null) return;
        var atlasId = AtlasDefs.ResolveAtlasName(corpseDef.Sprite.AtlasName);
        int ai = atlasId;
        if (ai < 0 || ai >= _g._atlases.Length || !_g._atlases[ai].IsLoaded) return;
        var death = _g._atlases[ai].GetUnit(corpseDef.Sprite.SpriteName)?.GetAnim("Death");
        if (death == null) return;

        // Resolve through the CARRIER's controller (same scheme + hysteresis as the
        // necromancer's body) so they snap together. Fall back to the corpse's own
        // resolution only if its art lacks that angle (e.g. a different-scheme
        // animal). Freeze the result on the corpse so the dropped pose matches.
        float carryFacing = _g._sim.Units[unitIdx].FacingAngle;
        int angle = carrierAnim.Ctrl.ResolveAngle(carryFacing, out bool flipX);
        if (death.GetAngle(angle) is not { Count: > 0 })
            angle = AnimController.ResolveAngleFor(death, carryFacing, out flipX);
        cc.CarryDisplayAngle = angle;
        cc.CarryDisplayFlip = flipX;

        // Hand/carry anchor — weapon hilt (the carry pose holds both hands out front).
        Vector2 pos = unitScreenPos;
        var carrierDef = _g._gameData.Units.Get(_g._sim.Units[unitIdx].UnitDefID);
        if (carrierDef != null)
        {
            var attach = ComputeWeaponAttach(unitIdx, carrierDef, carrierAnim);
            if (attach.Valid)
                pos = _g._renderer.WorldToScreenPx(attach.HiltWorld, attach.HiltHeight * _g._camera.Zoom, _g._camera);
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
        var corpseDef = _g._gameData.Units.Get(cc.UnitDefID);
        if (corpseDef?.Sprite == null) return;
        var atlasId = AtlasDefs.ResolveAtlasName(corpseDef.Sprite.AtlasName);
        int ai = atlasId;
        if (ai < 0 || ai >= _g._atlases.Length || !_g._atlases[ai].IsLoaded) return;
        var atlas = _g._atlases[ai];
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
        float scale = (worldH * _g._camera.Zoom) / refH;

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
        Color tint = MultiplyColor(new Color(alpha, alpha, alpha, alpha), _g._ambientColor);

        bool flipX = cc.CarryDisplayFlip;
        var centroid = GetFrameCentroid(tex, frame);
        float originX = flipX ? (frame.Rect.Width - centroid.X) : centroid.X;
        var origin = new Vector2(originX, centroid.Y);
        var effects = flipX ? SpriteEffects.FlipHorizontally : SpriteEffects.None;
        _g._spriteBatch.Draw(tex, screenPos, frame.Rect, tint, 0f, origin, scale, effects, 0f);

        // Hover-highlight: this draw uses a centroid origin (not the pivot), so
        // build the box from that rather than SpriteFrameAABB.
        if (_g._gameData.Settings.Tooltips.ShowHoverHighlight
            && _g._hoveredCorpseIdx >= 0 && _g._hoveredCorpseIdx < _g._sim.Corpses.Count
            && ReferenceEquals(_g._sim.Corpses[_g._hoveredCorpseIdx], cc))
        {
            float bw = frame.Rect.Width * scale, bh = frame.Rect.Height * scale;
            _g._hoverBoxCorpse = new Rectangle(
                (int)(screenPos.X - origin.X * scale), (int)(screenPos.Y - origin.Y * scale),
                (int)bw, (int)bh);
        }
    }

    /// <summary>Carried visual anchored on the carrier — bag or raw corpse per
    /// GameConstants.UseBodyBag.</summary>
    private void DrawCarriedVisual(int unitIdx, Vector2 unitScreenPos, float unitScale)
    {
        if (GameConstants.UseBodyBag)
            DrawCarriedBodyBag(unitIdx, unitScreenPos, unitScale, _g._sim.Units[unitIdx].FacingAngle);
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
        var cc = _g._sim.FindCorpseByID(_g._sim.Units[unitIdx].CarryingCorpseID);
        if (cc != null)
            DrawCorpseSpriteAt(cc.UnitDefID, screenPos, facingAngle, cc.SpriteScale, rotation);
    }
}
