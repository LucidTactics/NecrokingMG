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

// Game1 partial: Unit, env-object and sprite rendering.
partial class GameRenderer
{
    /// <summary>
    /// Compute weapon hilt/tip world positions for buff weapon particle spawning.
    /// </summary>
    private WeaponAttachRuntime ComputeWeaponAttach(int unitIdx, UnitDef unitDef, Game1.UnitAnimData animData)
    {
        var result = new WeaponAttachRuntime();
        if (unitDef.Sprite == null || animData.RefFrameHeight <= 0f) return result;

        string animName = AnimController.StateToAnimName(animData.Ctrl.CurrentState);
        int spriteAngle = animData.Ctrl.ResolveAngle(_g._sim.Units[unitIdx].FacingAngle, out bool flipX);
        int frameIdx = animData.Ctrl.GetCurrentFrameIndex(_g._sim.Units[unitIdx].FacingAngle);

        AnimationMeta? meta = null;
        _g._animMeta.TryGetValue(AnimMetaLoader.MetaKey(unitDef.Sprite.SpriteName, animName), out meta);

        if (!WeaponPointResolver.TryResolve(unitDef, meta, animName, spriteAngle, frameIdx,
                animData.RefFrameHeight, out var wpf, out _)) return result;

        bool hiltSet = wpf.Hilt.X != 0f || wpf.Hilt.Y != 0f;
        bool tipSet = wpf.Tip.X != 0f || wpf.Tip.Y != 0f;
        if (!hiltSet && !tipSet) return result;

        float flipMul = flipX ? -1f : 1f;
        float worldH = (unitDef.SpriteWorldHeight > 0 ? unitDef.SpriteWorldHeight : 1.8f)
                       * _g._sim.Units[unitIdx].SpriteScale;
        float worldScale = worldH / animData.RefFrameHeight;
        float unitHeight = _g._sim.Units[unitIdx].Z;

        // Weapon attach points follow the sprite's cosmetic offset — so the weapon
        // lunges with the unit on attack. Gameplay never reads these.
        var unitRender = _g._sim.Units[unitIdx].RenderPos;
        result.HiltWorld = new Vec2(
            unitRender.X + wpf.Hilt.X * worldScale * flipMul,
            unitRender.Y);
        result.HiltHeight = unitHeight - wpf.Hilt.Y * worldScale;
        result.HiltBehind = wpf.Hilt.Behind;

        result.TipWorld = new Vec2(
            unitRender.X + wpf.Tip.X * worldScale * flipMul,
            unitRender.Y);
        result.TipHeight = unitHeight - wpf.Tip.Y * worldScale;
        result.TipBehind = wpf.Tip.Behind;

        result.Valid = true;
        return result;
    }

    // Cached submit callbacks for the world queue — one delegate instance per
    // item kind, created lazily; per-item payload rides in the item's CbA/CbB
    // ints so submission never allocates.
    private SpriteDrawCallback? _cbUnit, _cbEnvObject, _cbCloudPuff, _cbGrassTuft,
        _cbDeathFogPuff, _cbReanimDust;
    private SpriteDrawCallback? _cbRoads, _cbTraps, _cbGlyphs, _cbWalls, _cbShadows,
        _cbHoverMarkers, _cbCorpses, _cbProjectilesRope, _cbRain;

    /// <summary>Collect the whole world sprite pass: the fixed layer blocks
    /// (roads → corpses, projectiles → rain) as whole-layer callback slots, plus
    /// the depth-sorted YSort layer (units, env objects, and the particle types
    /// that interleave with them). Layer bands reproduce the old block order;
    /// within YSort the key (depth=worldY, seq=submission order) reproduces the
    /// DepthItem sort including its determinism tiebreaker.</summary>
    private void CollectWorldItems(RenderContext ctx)
    {
        _cbRoads ??= (SpriteScope _, int _, int _) => DrawRoads();
        _cbTraps ??= (SpriteScope s, int _, int _) => DrawGroundLayerObjects(s);
        _cbGlyphs ??= (SpriteScope s, int _, int _) =>
        {
            _g._glyphRenderer.DrawGround(s, _g._sim.MagicGlyphs);
            // Build progress bars for blueprint glyphs — shown from the moment
            // the glyph is placed (even at 0%) so players can see "trap placed,
            // awaiting build".
            foreach (var g in _g._sim.MagicGlyphs.Glyphs)
            {
                if (g.State == GameSystems.GlyphState.Blueprint && g.BuildProgress < 1f && g.Alive)
                {
                    var gsp = _g._renderer.WorldToScreen(g.Position, 0f, _g._camera);
                    DrawBuildProgressBar(gsp, g.BuildProgress, g.Radius);
                }
            }
        };
        _cbWalls ??= (SpriteScope _, int _, int _) => DrawWalls();
        // ShadowRenderer suspends the batch to draw raw quads; the scope owns
        // the resume state.
        _cbShadows ??= (SpriteScope s, int _, int _) =>
            _g._shadowRenderer.Draw(s, _g.GraphicsDevice, _g._spriteBatch, _g._glowTex, _g._camera, _g._renderer, _g._sim, _g._gameData, _g._unitAnims, _g._atlases, _g._envSystem, _g._fogOfWar, _g._groundSystem, _g._deathFog, _g._corpseAnims, _g._reanimFx);
        _cbHoverMarkers ??= (SpriteScope _, int _, int _) => DrawHoverGroundMarkers();
        _cbCorpses ??= (SpriteScope s, int _, int _) => DrawCorpses(s);
        _cbProjectilesRope ??= (SpriteScope _, int _, int _) =>
        {
            DrawProjectiles();
            DrawSoulOrbs();
            // Drag rope (necromancer → roped corpse)
            DrawRope();
        };
        _cbRain ??= (SpriteScope _, int _, int _) => _g._weatherRenderer.DrawRain(_ctx.ScreenW, _ctx.ScreenH);

        var q = _worldPass!;
        // Ordering safety: block layers use whole-layer callback slots today;
        // granular per-sprite submission inside these layers can come later
        // without changing the frame order (the layer bands pin it).
        q.SubmitCallback(WorldLayer.Roads, _cbRoads, 0, 0);
        q.SubmitCallback(WorldLayer.Traps, _cbTraps, 0, 0);
        // Glyph renderer context is primed at collect time; its draw runs when
        // the queue executes this frame.
        _g._glyphRenderer.SetContext(_g._spriteBatch, _g._pixel, _g._glowTex, _g._camera, _g._renderer, _g._flipbooks, _g._gameTime);
        q.SubmitCallback(WorldLayer.Glyphs, _cbGlyphs, 0, 0);
        q.SubmitCallback(WorldLayer.Walls, _cbWalls, 0, 0);
        q.SubmitCallback(WorldLayer.Shadows, _cbShadows, 0, 0);
        // Hover highlight — ground variants (Circle / Ground Box) BEHIND
        // corpses/units (RTS-style).
        q.SubmitCallback(WorldLayer.HoverMarkers, _cbHoverMarkers, 0, 0);
        q.SubmitCallback(WorldLayer.Corpses, _cbCorpses, 0, 0);
        q.SubmitCallback(WorldLayer.Projectiles, _cbProjectilesRope, 0, 0);
        // Rain (world-space, drawn over the sorted scene like the old block order)
        q.SubmitCallback(WorldLayer.Rain, _cbRain, 0, 0);

        CollectYSortItems(ctx);
    }

    /// <summary>Collect the depth-sorted YSort layer — units, env objects, and
    /// the particle types that interleave with them. This is the old
    /// DrawUnitsAndObjects minus sorting and dispatch.</summary>
    private void CollectYSortItems(RenderContext ctx)
    {
        _cbUnit ??= (SpriteScope s, int a, int _) => DrawSingleUnit(s, a);
        _cbEnvObject ??= (SpriteScope s, int a, int _) => DrawSingleEnvObject(s, a);
        _cbCloudPuff ??= (SpriteScope _, int a, int b) => _g._poisonCloudRenderer.DrawSinglePuff(a, b);
        // no_ground dev screenshots suppress grass too, for the clean
        // black-background look scenarios produce.
        _cbGrassTuft ??= (SpriteScope _, int a, int _) =>
        {
            if (!_g._devShotNoGround) _g._grassRenderer.DrawSingleTuft(_g._spriteBatch, a);
        };
        _cbDeathFogPuff ??= (SpriteScope _, int a, int _) => _g._deathFogRenderer.DrawSinglePuff(a);
        _cbReanimDust ??= (SpriteScope _, int a, int _) => _g._reanimFx.DrawSingleDust(a);

        var queue = _worldPass!;

        // View culling bounds
        float viewMargin = 20f;
        float viewLeft = _g._camera.Position.X - _g._renderer.ScreenW / (2f * _g._camera.Zoom) - viewMargin;
        float viewRight = _g._camera.Position.X + _g._renderer.ScreenW / (2f * _g._camera.Zoom) + viewMargin;
        float viewTop = _g._camera.Position.Y - _g._renderer.ScreenH / (_g._camera.Zoom * _g._camera.YRatio) - viewMargin;
        float viewBottom = _g._camera.Position.Y + _g._renderer.ScreenH / (_g._camera.Zoom * _g._camera.YRatio) + viewMargin;

        // Add units (view-culled, same bounds as env objects below). Use RenderPos
        // so a lunging unit re-sorts against its neighbors naturally — a forward-
        // lunging sprite that crosses another unit's Y draws in front during the
        // lunge. The 20-unit margin covers sprite overhang (sprites are a few world
        // units tall and shorter than the trees culled with the same bounds), so a
        // unit just off-screen whose head still pokes in isn't clipped early.
        // Without this, off-screen units each ran a full DrawSingleUnit every frame —
        // the dominant Draw cost on a populated map, especially with fog off.
        for (int i = 0; i < _g._sim.Units.Count; i++)
        {
            if (!_g._sim.Units[i].Alive) continue;
            var rp = _g._sim.Units[i].RenderPos;
            if (rp.X < viewLeft || rp.X > viewRight || rp.Y < viewTop || rp.Y > viewBottom)
                continue;
            queue.SubmitCallback(WorldLayer.YSort, rp.Y, _cbUnit, i, 0);
        }

        // Occlusion fade: precompute a screen box for every alive player-owned
        // (Undead) unit that's on screen — the necromancer AND the horde — so the
        // env loop below can fade tall objects that hide any of them. View-culled
        // (same bounds as the units/env), so off-screen minions cost nothing; the
        // list is reused each frame to avoid GC churn in this hot render path.
        // Possible precisely because submissions are inspectable data before drawing.
        _occlusionBoxes.Clear();
        for (int i = 0; i < _g._sim.Units.Count; i++)
        {
            var u = _g._sim.Units[i];
            if (!u.Alive || u.Faction != Faction.Undead) continue;
            var rp = u.RenderPos;
            if (rp.X < viewLeft || rp.X > viewRight || rp.Y < viewTop || rp.Y > viewBottom)
                continue;
            var udef = _g._gameData.Units.Get(u.UnitDefID);
            float uWorldH = (udef != null && udef.SpriteWorldHeight > 0 ? udef.SpriteWorldHeight : 1.8f) * u.SpriteScale;
            var usp = _g._renderer.WorldToScreen(rp, u.Z, _g._camera);
            float upxH = uWorldH * _g._camera.Zoom;
            _occlusionBoxes.Add((usp.X - upxH * 0.30f, usp.X + upxH * 0.30f, usp.Y - upxH, usp.Y, rp.Y));
        }

        // Add environment objects (with view culling, skip collected foragables, skip ground-layer objects)
        for (int i = 0; i < _g._envSystem.ObjectCount; i++)
        {
            if (!_g._envSystem.IsObjectVisible(i)) continue;
            var obj = _g._envSystem.Objects[i];
            var def = _g._envSystem.Defs[obj.DefIndex];
            if (def.Category == "Traps") continue; // drawn in ground layer pass
            if (obj.X < viewLeft || obj.X > viewRight || obj.Y < viewTop || obj.Y > viewBottom)
                continue;

            UpdateOcclusionFade(i, obj, def);

            // Note: defs whose sprites failed to load get a placeholder texture in EnvironmentSystem,
            // so GetDefTexture is non-null and the object still appears.
            queue.SubmitCallback(WorldLayer.YSort, obj.Y, _cbEnvObject, i, 0);
        }

        // Particle item kinds still assemble via the legacy DepthItem list —
        // their renderers' internal culling/context logic stays untouched; the
        // list is just a transfer vehicle into the queue now.
        _g._depthItems.Clear();
        var items = _g._depthItems;

        // Add poison cloud puffs
        _g._poisonCloudRenderer.SetContext(_g._spriteBatch, _g._glowTex, _g._camera, _g._renderer, _g._flipbooks, _g._gameTime);
        _g._poisonCloudRenderer.AddPuffsToDepthList(_g._sim.PoisonClouds, items);

        // Add grass tufts — Y-sorted with units so a tuft "in front" (higher Y)
        // correctly renders over a unit's feet, and one "behind" (lower Y) is
        // drawn first and hidden by the unit.
        _g._grassRenderer.AddTuftsToDepthList(
            _g._camera, _g._renderer.ScreenW, _g._renderer.ScreenH,
            _g._grassMap, _g._grassW, _g._grassH,
            _g._gameData.Settings.Grass, _g._ambientColor, items);

        // Add death-fog puffs — Y-sorted with units so puffs visually drift in
        // front of / behind characters depending on their relative ground Y.
        // Mirrors PoisonCloudRenderer's depth-list integration.
        if (_g._flipbooks != null && _g._flipbooks.TryGetValue("cloud03", out var deathFogFb))
        {
            // Ground fog: update banks/wisps and submit the back blankets
            // (behind YSort); the depth-tested wisps submit in CollectFxItems.
            _g._groundFog.SetContext(_g._spriteBatch, _g._camera, _g._renderer, deathFogFb, _g._glowTex, _g._gameTime);
            _g._groundFog.Update(_g._frameDt);
            _g._groundFog.CollectBack(queue, _g._ambientColor);

            _g._deathFogRenderer.SetContext(_g._spriteBatch, _g._camera, _g._renderer, deathFogFb, _g._gameTime);
            _g._deathFogRenderer.AddPuffsToDepthList(_g._deathFog, _g._renderer.ScreenW, _g._renderer.ScreenH, items);
            // Reanimation dust puffs — Y-sorted with units (reuses the cloud03 sheet).
            // SetContext here also primes the additive light/cloud pass (DrawReanimAdditive).
            _g._reanimFx.SetContext(_g._spriteBatch, _g._camera, _g._renderer, deathFogFb, _g._glowTex);
            // When depth-sorted fog is ON the dust is drawn interleaved with the clouds in the combined
            // sorted pass (DrawSortedParticles) instead — so keep it out of the unit Y-sort list here.
            if (!_g._gameData.Settings.Performance.DepthSortedFog)
                _g._reanimFx.AddDustToDepthList(items);
        }

        for (int i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var cb = item.Type switch
            {
                Game1.DepthItemType.CloudPuff => _cbCloudPuff,
                Game1.DepthItemType.GrassTuft => _cbGrassTuft,
                Game1.DepthItemType.DeathFogPuff => _cbDeathFogPuff,
                Game1.DepthItemType.ReanimDust => _cbReanimDust,
                _ => null,
            };
            if (cb != null)
                queue.SubmitCallback(WorldLayer.YSort, item.Y, cb, item.Index, item.SubIndex);
        }
    }

    // --- Occlusion fade: a tall env object between the camera and a player-owned
    // unit goes semi-transparent so that unit stays visible — the necromancer AND
    // any horde minion. Per-object fade state persists across frames for a smooth
    // lerp; entries at full opacity are dropped. Enabled by the retained model:
    // items are data before draws, so the collect loop can inspect "what draws in
    // front of the player's units."
    private readonly Dictionary<int, float> _occlusionFade = new();
    // One screen-space AABB per on-screen player unit (rebuilt each frame in the
    // collect loop). plY is that unit's ground Y, so an object only fades against
    // a unit it's actually in front of.
    private readonly List<(float left, float right, float top, float bottom, float plY)> _occlusionBoxes = new();
    private const float OccludedAlpha = 0.40f;      // fade floor while occluding
    private const float OcclusionMinWorldH = 2.5f;  // only tall things fade (trees, buildings)
    private const float OcclusionFadeRate = 8f;     // exp-lerp speed, 1/s

    private void UpdateOcclusionFade(int i, in PlacedObject obj, EnvironmentObjectDef def)
    {
        float target = 1f;
        float worldH = def.SpriteWorldHeight * obj.Scale * def.Scale;
        // Only tall objects fade, and only when at least one player unit is on screen.
        if (_occlusionBoxes.Count > 0 && worldH >= OcclusionMinWorldH)
        {
            // Approximate the object's screen box (aspect from its texture when
            // static; a 0.7 width ratio for animated sheets — close enough for
            // an overlap test that feeds a soft fade).
            var sp = _g._renderer.WorldToScreen(new Vec2(obj.X, obj.Y), 0f, _g._camera);
            float pxH = worldH * _g._camera.Zoom;
            var tex = _g._envSystem.GetDefTexture(obj.DefIndex);
            float halfW = (tex != null && !def.IsAnimated)
                ? pxH * (tex.Width / (float)tex.Height) * 0.5f
                : pxH * 0.35f;
            float objLeft = sp.X - halfW, objRight = sp.X + halfW;
            float objTop = sp.Y - pxH, objBottom = sp.Y;
            // Fade if the object is in front of AND overlapping ANY player unit.
            foreach (var b in _occlusionBoxes)
            {
                if (obj.Y > b.plY
                    && objLeft < b.right && objRight > b.left
                    && objTop < b.bottom && objBottom > b.top)
                {
                    target = OccludedAlpha;
                    break;
                }
            }
        }

        if (!_occlusionFade.TryGetValue(i, out float fade))
        {
            if (target >= 1f) return;   // fully opaque and staying there — no entry
            fade = 1f;
        }
        fade = MathHelper.Lerp(fade, target, 1f - MathF.Exp(-OcclusionFadeRate * _g._frameDt));
        if (fade > 0.995f) _occlusionFade.Remove(i);
        else _occlusionFade[i] = fade;
    }

    private void DrawSingleUnit(SpriteScope scope, int i)
    {
        // Fog of war: hide non-undead units (and their buffs, which draw inside
        // this method) when they're not currently in any undead's detection range.
        if (_g._sim.Units[i].Faction != Faction.Undead && !_g._fogOfWar.IsVisible(_g._sim.Units[i].Position))
            return;

        uint uid = _g._sim.Units[i].Id;
        if (!_g._unitAnims.TryGetValue(uid, out var animData)) return;

        var unitDef = _g._gameData.Units.Get(_g._sim.Units[i].UnitDefID);
        if (unitDef == null) return;

        var atlas = _g._atlases[animData.AtlasID];
        if (!atlas.IsLoaded) return;

        var fr = animData.Ctrl.GetCurrentFrame(_g._sim.Units[i].FacingAngle);
        if (fr.Frame == null) return;

        float worldH = (unitDef.SpriteWorldHeight > 0 ? unitDef.SpriteWorldHeight : 1.8f) * _g._sim.Units[i].SpriteScale;
        float pixelH = worldH * _g._camera.Zoom;
        float scale = pixelH / animData.RefFrameHeight;

        Color tint = _g._sim.Units[i].Faction == Faction.Undead
            ? new Color(190, 210, 190)
            : new Color(210, 195, 185);

        // Apply buff tinting
        foreach (var buff in _g._sim.Units[i].ActiveBuffs)
        {
            var buffDef = _g._gameData.Buffs.Get(buff.BuffDefID);
            if (buffDef?.UnitTint != null && buffDef.UnitTint.A > 0)
            {
                var bt = buffDef.UnitTint;
                float blend = bt.A / 255f;
                tint = new Color(
                    (byte)(tint.R * (1f - blend) + bt.R * blend),
                    (byte)(tint.G * (1f - blend) + bt.G * blend),
                    (byte)(tint.B * (1f - blend) + bt.B * blend));
            }
        }

        // Ghost mode: semi-transparent blue-shifted sprite (straight alpha; the
        // draw surface encodes it for the open material)
        if (_g._sim.Units[i].GhostMode)
            tint = new Color(
                Math.Min(255, (int)(tint.R * 0.7f + 80)),
                Math.Min(255, (int)(tint.G * 0.7f + 100)),
                Math.Min(255, (int)(tint.B * 0.7f + 120)), 100);

        // Apply weather ambient light
        tint = MultiplyColor(tint, _g._ambientColor);

        // Hit flash: brief lerp toward white on physical impact. Applied AFTER the
        // ambient multiply so it stays visible at night. This is the feedback that
        // survives reaction-anim suppression (fleeing/mid-attack/cooldown units).
        // Toggle + intensity live in the esc-menu Animation tab.
        var animSettings = _g._gameData.Settings.Animation;
        if (animSettings.HitFlashEnabled && _g._sim.Units[i].HitFlashTimer > 0f)
        {
            float flash = _g._sim.Units[i].HitFlashTimer / GameSystems.DamageSystem.HitFlashSeconds;
            tint = Color.Lerp(tint, Color.White, animSettings.HitFlashIntensity * flash);
        }

        float heightOffset = _g._sim.Units[i].Z;
        // Use RenderPos (Position + RenderOffset) so lunge and any future cosmetic
        // offsets propagate to every visual attached to this unit: sprite, weapon,
        // shield, status symbols, health bar, buff visuals, damage numbers, etc.
        var renderPos = _g._sim.Units[i].RenderPos;
        var sp = _g._renderer.WorldToScreen(renderPos, heightOffset, _g._camera);
        // For drawing above unit.
        var sp_upper = _g._renderer.WorldToScreen(renderPos, heightOffset + _g._sim.Units[i].CollisionHeight, _g._camera);

        // Hover-highlight: capture this unit's exact on-screen sprite box.
        if (i == _g._hoveredUnitIdx && _g._gameData.Settings.Tooltips.ShowHoverHighlight)
            _g._hoverBoxUnit = SpriteFrameAABB(sp, fr.Frame.Value, scale, fr.FlipX);
        // Dev-mark: persistent white box (via the 'mark' dev command).
        if (_g._devMarkedUnitIds.Count > 0 && _g._devMarkedUnitIds.Contains(_g._sim.Units[i].Id))
            _g._devMarkBoxes.Add(SpriteFrameAABB(sp, fr.Frame.Value, scale, fr.FlipX));

        // Compute weapon attachment for weapon particle buff visuals
        var weaponAttach = ComputeWeaponAttach(i, unitDef, animData);

        // Update weapon particle emitters (like C++, phase 0 only)
        {
            _g._wpDefsCache.Clear();
            foreach (var ab in _g._sim.Units[i].ActiveBuffs)
            {
                var bd = _g._gameData.Buffs.Get(ab.BuffDefID);
                if (bd != null && bd.HasWeaponParticle && bd.WeaponParticle != null)
                    _g._wpDefsCache.Add(bd);
            }
            if (_g._wpDefsCache.Count > 0 || _g._buffVisuals.HasEmitters(i))
                // WORLD domain (was rawDt*timeScale: unclamped and not even pause-gated,
                // so emitters kept spawning while paused/in editors).
                _g._buffVisuals.UpdateWeaponParticles(i, _g._clock.WorldDt, _g._gameTime, _g._wpDefsCache, weaponAttach, _g._gameData.Buffs);
        }

        // Buff visuals: phase 0 (behind sprite)
        _g._buffVisuals.DrawUnit(i, renderPos, 0, _g._gameTime,
            _g._spriteBatch, _g._camera, _g._renderer, _g._flipbooks, _g._gameData.Buffs, _g._sim.Units,
            atlas, fr.Frame.Value, scale, fr.FlipX,
            _g._sim.Units[i].EffectSpawnPos2D, _g._sim.Units[i].EffectSpawnHeight);

        // Pulsing outline: draw sprite 8 times at directional offsets behind the unit
        DrawUnitPulsingOutline(scope, i, atlas, fr.Frame.Value, sp, scale, fr.FlipX);

        // Reanimation rise outline — blinks undead-green and fades out over the effect.
        if (_g._reanimFx.TryGetOutline(_g._sim.Units[i].Id, out var ro1, out var ro2, out var rOw, out var rPw, out var rPs))
            DrawSpriteOutline(scope, atlas, fr.Frame.Value, sp, scale, fr.FlipX, ro1, ro2, rOw, rPw, rPs, 1);

        // Ghost mode: subtle blue pulsing outline
        if (_g._sim.Units[i].GhostMode)
            DrawGhostOutline(scope, atlas, fr.Frame.Value, sp, scale, fr.FlipX);

        // Carried body bag rendering (phase-aware: respects effect_ms action moment)
        byte cPhase = _g._sim.Units[i].CorpseInteractPhase;
        int putdownTableIdx = _g._sim.Units[i].PutDownTableIdx;
        bool tableBoundPutdown = cPhase == 5 && putdownTableIdx >= 0
            && _g._envSystem != null && putdownTableIdx < _g._envSystem.ObjectCount;
        bool hasCorpse = _g._sim.Units[i].CarryingCorpseID >= 0
            && (cPhase == 0 || cPhase == 4 || cPhase == 5);
        // facingAway = the unit's back is toward the camera, so the carried corpse
        // renders *behind* the sprite. Keyed off the RESOLVED sprite angle (not the
        // raw mouse angle) so the render order flips exactly when the sprite flips —
        // with the same buckets + hysteresis — instead of jittering on tiny mouse
        // moves. Back angles: new scheme N=270 / NE-NW=315, old scheme up=300.
        // Everything else (E/W=0, S=90, SE/SW=45, old 30/60) is front → on top.
        int sprAngle = animData.Ctrl.ResolveAngle(_g._sim.Units[i].FacingAngle, out _);
        bool facingAway = sprAngle == 270 || sprAngle == 315 || sprAngle == 300;
        bool drawBagAtHilt = false; // whether to draw on unit (vs at ground)

        if (hasCorpse && !tableBoundPutdown)
        {
            if (cPhase == 0)
                drawBagAtHilt = true; // fully carried
            else if (cPhase == 4) // Pickup: ground until action moment, then hilt
                drawBagAtHilt = animData.Ctrl.HasReachedActionMoment();
            else if (cPhase == 5) // PutDown: hilt until action moment, then ground
                drawBagAtHilt = !animData.Ctrl.HasReachedActionMoment();
        }

        // Pre-compute table-bound PutDown lerp target so we can draw it on the
        // correct side of the unit (back vs front) by Y-sort convention.
        Vector2? tableLerpScreen = null;
        float tableLerpRotation = 0f;
        if (tableBoundPutdown && hasCorpse)
        {
            // t = anim progress: 0 at PutDown start (bag at hand), 1 at completion
            // (bag on table). MathHelper.Lerp handles position and rotation.
            float t = animData.Ctrl.TimeFraction;

            // Source pose: hilt + carry offsets (mirrors DrawCarriedBodyBag).
            Vector2 sourcePos = sp; // fallback to unit screen pos if attach invalid
            var attach = ComputeWeaponAttach(i, unitDef, animData);
            if (attach.Valid)
                sourcePos = _g._renderer.WorldToScreenPx(attach.HiltWorld, attach.HiltHeight * _g._camera.Zoom, _g._camera);
            var bagFr = GetBodyBagFrame(_g._sim.Units[i].FacingAngle);
            float ofsX = bagFr.FlipX ? -CarryOffsetX : CarryOffsetX;
            sourcePos.X += ofsX;
            sourcePos.Y += CarryOffsetY;

            // Destination pose: table-overlay anchor (mirrors DrawSingleEnvObject's
            // table body-bag block). Same lift formula keeps position consistent
            // when the lerp finishes and the env overlay takes over.
            var tableObj = _g._envSystem.GetObject(putdownTableIdx);
            var tableDef = _g._envSystem.Defs[tableObj.DefIndex];
            float tableWorldH = tableDef.SpriteWorldHeight * tableObj.Scale * tableDef.Scale;
            float bagLift = tableWorldH * tableDef.PivotY * 1.22f;
            Vector2 destPos = _g._renderer.WorldToScreen(new Vec2(tableObj.X, tableObj.Y), bagLift, _g._camera);

            tableLerpScreen = Vector2.Lerp(sourcePos, destPos, t);
            tableLerpRotation = MathHelper.Lerp(0f, -MathF.PI / 12f, t);
        }

        if (hasCorpse && drawBagAtHilt && facingAway)
            DrawCarriedVisual(i, sp, scale);
        // Table-bound PutDown: draw the lerped corpse BEHIND the unit when facing away.
        if (tableBoundPutdown && tableLerpScreen.HasValue && facingAway)
            DrawCarriedVisualAt(i, tableLerpScreen.Value, _g._sim.Units[i].FacingAngle, tableLerpRotation);
        if (hasCorpse && !drawBagAtHilt && !tableBoundPutdown)
        {
            // Ground PutDown: draw at the corpse's drop point. In corpse mode use
            // the frozen carry frame (centroid-pegged) so it's identical to the
            // settled corpse that takes over at anim-finish — no hand-off jump.
            var cc = _g._sim.FindCorpseByID(_g._sim.Units[i].CarryingCorpseID);
            if (cc != null)
            {
                var groundSp = _g._renderer.WorldToScreen(cc.LerpStartPos, 0f, _g._camera);
                if (!GameConstants.UseBodyBag && cc.CarryDisplayAngle >= 0)
                    DrawCorpseCarriedFrame(cc, groundSp);
                else
                    DrawCarriedVisualAt(i, groundSp, cc.FacingAngle);
            }
        }

        // Wading visual: see Render/WadingState.cs for the math + constants.
        // All per-unit wading parameters (waterness, waterline V, top cut V,
        // diagonal slope, sprite angle) are computed in one place; the same
        // struct is used by the shadow renderer for consistency.
        // Sample at RenderPos (the visual position) — the shadow renderer also
        // uses RenderPos, so sprite and shadow agree on the waterline during
        // lunges near a shoreline.
        WadingState wading = WadingState.Compute(
            _g._sim.Units[i].RenderPos, _g._sim.Units[i].FacingAngle,
            fr.Frame.Value, unitDef, animData.Ctrl, _g._groundSystem, _g._camera.YRatio);
        if (wading.Active)
        {
            // World height that puts a particle drawn at the unit's foot
            // position level with the visual waterline cut on the sprite.
            // pivotFlippedV - waterlineV is the V distance from the cut up
            // to the pivot in sprite-V; multiplying by worldH gives the
            // equivalent world height, and dividing by YRatio undoes the
            // isometric squish that WorldToScreen applies to world Y.
            float pivotFlippedV = 1f - fr.Frame.Value.PivotY;
            float wakeLiftWorldH = (pivotFlippedV - wading.WaterlineV) * worldH / _g._camera.YRatio;

            // BACK pass — trail particles render behind the sprite so the
            // body covers anything drifting into its silhouette. Also runs
            // the per-frame update + edge-detect for entry splash spawning.
            float bodyLen = unitDef.BodyLengthWorld > 0f
                ? unitDef.BodyLengthWorld
                : (unitDef.IsQuadruped ? Render.WadingWakeSystem.QuadrupedDefaultBodyLength : 0f);
            // Use RenderPos (Position + sink offset) so wake particles
            // spawn at the body's *visual* footprint — when the unit sinks
            // into deep water, the trail and bow wave follow the sunken
            // body instead of floating above it at the sim Y.
            _g._wakeSystem.UpdateAndDrawBack(
                i, _g._frameDt,
                _g._sim.Units[i].RenderPos, _g._sim.Units[i].Velocity,
                _g._sim.Units[i].FacingAngle, bodyLen,
                wakeLiftWorldH, true,
                _g._spriteBatch, _g._pixel, _g._renderer, _g._camera);

            // Sprite with waterline fade. Top cut V = -1 sentinel disables the
            // top cut in the shader (used for 3/4 facings where the back-cut
            // line never read cleanly). Top slope always 0 — top cut only ever
            // applies on cardinal facings, which have no body-axis tilt.
            DrawWadingSpriteFrame(scope, atlas, fr.Frame.Value, sp, scale, fr.FlipX, tint,
                                  wading.WaterlineV, wading.TopWaterlineV,
                                  wading.Slope, 0f);

            // FRONT pass — bow wave + entry splash render in front of the
            // sprite. Needed because for N-facing motion the "ahead of
            // unit" position projects to the same screen Y range as the
            // visible body; drawing front-class particles after the sprite
            // keeps the front foam crescent visible.
            _g._wakeSystem.DrawFront(i, _g._spriteBatch, _g._renderer, _g._camera);
        }
        else
        {
            // Out of water but live particles may still be fading. The back
            // pass advances + dims the remaining tail and catches the
            // exit-splash edge; fast-exits if no state.
            float bodyLen = unitDef.BodyLengthWorld > 0f
                ? unitDef.BodyLengthWorld
                : (unitDef.IsQuadruped ? Render.WadingWakeSystem.QuadrupedDefaultBodyLength : 0f);
            _g._wakeSystem.UpdateAndDrawBack(
                i, _g._frameDt,
                _g._sim.Units[i].RenderPos, _g._sim.Units[i].Velocity,
                _g._sim.Units[i].FacingAngle, bodyLen,
                0f, false,
                _g._spriteBatch, _g._pixel, _g._renderer, _g._camera);

            DrawSpriteFrame(atlas, fr.Frame.Value, sp, scale, fr.FlipX, tint);

            // Any lingering front-class particles (a bow wave fading out
            // as the unit steps onto land) also need the after-sprite pass.
            _g._wakeSystem.DrawFront(i, _g._spriteBatch, _g._renderer, _g._camera);
        }

        // F2 water debug overlay — render after the sprite so it's not occluded.
        if (_g._waterDebug && _g._smallFont != null)
            DrawWaterDebugOverlay(i, fr.Frame.Value, sp, pixelH, wading);

        // Carried visual: draw IN FRONT if facing toward camera
        if (hasCorpse && drawBagAtHilt && !facingAway)
            DrawCarriedVisual(i, sp, scale);
        // Table-bound PutDown: draw the lerped corpse IN FRONT when facing toward camera.
        if (tableBoundPutdown && tableLerpScreen.HasValue && !facingAway)
            DrawCarriedVisualAt(i, tableLerpScreen.Value, _g._sim.Units[i].FacingAngle, tableLerpRotation);

        // Buff visuals: phase 1 (in front of sprite)
        _g._buffVisuals.DrawUnit(i, renderPos, 1, _g._gameTime,
            _g._spriteBatch, _g._camera, _g._renderer, _g._flipbooks, _g._gameData.Buffs, _g._sim.Units,
            atlas, fr.Frame.Value, scale, fr.FlipX,
            _g._sim.Units[i].EffectSpawnPos2D, _g._sim.Units[i].EffectSpawnHeight);

        DrawHPBar(i, sp);

        // --- Status symbol (? / !) above head during notice/react events ---
        if (_g._sim.Units[i].StatusSymbol != 0 && _g._largeFont != null)
        {
            const float SymScale = 0.7f;   // ~30% smaller than _g._largeFont default
            const byte SymAlpha = 128;      // ~0.5 alpha
            string sym = _g._sim.Units[i].StatusSymbol == (byte)UnitStatusSymbol.Notice ? "?" : "!";
            Color symColor = _g._sim.Units[i].StatusSymbol == (byte)UnitStatusSymbol.Notice
                ? new Color(255, 240, 80, (int)SymAlpha)   // yellow ?
                : new Color(255, 80, 60, (int)SymAlpha);   // red !
            Color outline = new Color(0, 0, 0, (int)SymAlpha);
            var textSize = _g._largeFont.MeasureString(sym);
            int symX = (int)(sp_upper.X - textSize.X * 0.5f);
            int symY = (int)(sp_upper.Y - textSize.Y - 0.25f * _g._camera.Zoom * _g._camera.YRatio);
            var symPos = new Vector2(symX, symY);

            // Black outline (8-way offset) for contrast and bolder look
            for (int ox = -2; ox <= 2; ox++)
                for (int oy = -2; oy <= 2; oy++)
                    if ((ox != 0 || oy != 0) && ox * ox + oy * oy <= 4)
                        _g.Scope.DrawString(_g._largeFont, sym,
                            symPos + new Vector2(ox, oy), outline,
                            0f, Vector2.Zero, SymScale, SpriteEffects.None, 0f);

            // Faux-bold: draw colored fill twice with 1px horizontal offset
            _g.Scope.DrawString(_g._largeFont, sym, symPos, symColor,
                0f, Vector2.Zero, SymScale, SpriteEffects.None, 0f);
            _g.Scope.DrawString(_g._largeFont, sym, symPos + new Vector2(1, 0), symColor,
                0f, Vector2.Zero, SymScale, SpriteEffects.None, 0f);
        }

        // --- Feature 1: Action label above head during a committed attack/spell ---
        // Read from the generic ActionLabel field. Every archetype commit point
        // (standard melee, sweep, pounce, trample BeginCharge, ranged, spell cast)
        // writes this field — the renderer doesn't need to know about each path.
        // Anchored at the unit's HEAD, centered — same placement convention as
        // the cast-fail floating text (SpawnCastFailText). pixelH is the sprite's
        // drawn height, so sp.Y - pixelH IS the head in screen space (the old
        // fixed -55px offset landed mid-sprite and shifted with zoom).
        if (_g._sim.Units[i].ActionLabelTimer > 0f
            && !string.IsNullOrEmpty(_g._sim.Units[i].ActionLabel)
            && _g._smallFont != null)
        {
            string label = _g._sim.Units[i].ActionLabel;
            var size = _g._smallFont.MeasureString(label);
            var labelPos = new Vector2((int)(sp.X - size.X * 0.5f),
                                       (int)(sp.Y - pixelH - size.Y * 0.5f));
            DrawText(_g._smallFont, label, labelPos, new Color(255, 220, 140, 220));
        }

    }

    private void DrawSingleEnvObject(SpriteScope scope, int i)
    {
        var obj = _g._envSystem.Objects[i];
        var def = _g._envSystem.Defs[obj.DefIndex];

        // Dissolve transition: between threshold-cross and full corruption, render
        // through the dissolve shader instead of the regular path. Shader needs
        // both textures bound; existing path can't carry a second sampler. Falls
        // through to the regular draw if either texture / the shader is missing.
        var rtCheck = _g._envSystem.GetObjectRuntime(i);
        if (rtCheck.CorruptionTime > 0f && !rtCheck.Corrupted && Materials.DissolveTree != null)
        {
            if (DrawDissolvingTree(scope, i, rtCheck)) return;
        }

        var tex = _g._envSystem.GetObjectTexture(i, out float alpha, out bool isOverride);
        if (tex == null) return;

        // Always compute scale from the main def texture so trap sprites render at same size.
        // For corrupted/override sprites we scale relative to the override texture itself
        // (it's a single frame, not a spritesheet, so refHeight should be its full height).
        var mainTex = _g._envSystem.GetDefTexture(obj.DefIndex);
        float refHeight = isOverride ? tex.Height : (mainTex != null ? mainTex.Height : tex.Height);

        // Animated spritesheet: use per-frame dimensions.
        // Skip slicing for the placeholder texture (single 32x32 swatch) and for
        // single-frame override textures (corrupted/trap sprites).
        bool usingPlaceholder = _g._envSystem.IsUsingPlaceholder(obj.DefIndex);
        Rectangle? sourceRect = null;
        float frameW = tex.Width;
        float frameH = tex.Height;
        if (def.IsAnimated && def.AnimTotalFrames > 1 && !usingPlaceholder && !isOverride)
        {
            int totalFrames = def.AnimTotalFrames;
            float animTime = _g._envSystem.GetObjectRuntime(i).AnimTime;
            int frame = Math.Clamp((int)animTime, 0, totalFrames - 1);
            sourceRect = def.GetAnimFrameRect(tex.Width, tex.Height, frame);
            frameW = sourceRect.Value.Width;
            frameH = sourceRect.Value.Height;
            refHeight = frameH; // scale relative to frame height, not full sheet
        }

        float worldH = def.SpriteWorldHeight * obj.Scale * def.Scale;
        float pixelH = worldH * _g._camera.Zoom;
        float scale = pixelH / refHeight;

        var screenPos = _g._renderer.WorldToScreen(new Vec2(obj.X, obj.Y), 0f, _g._camera);
        // Random per-instance horizontal flip (deterministic from seed). Mirror
        // the pivot's X so the sprite's base stays anchored at the same world point.
        bool flipX = _g._envSystem.ShouldFlipObject(i);
        var origin = new Vector2((flipX ? (1f - def.PivotX) : def.PivotX) * frameW, def.PivotY * frameH);

        float rotation = 0f;
        Color tint = alpha >= 1f ? Color.White : new Color(alpha, alpha, alpha, alpha);

        // Foragable proximity effects
        if (def.IsForagable && _g._sim.NecromancerIndex >= 0)
        {
            Vec2 objPos = new Vec2(obj.X, obj.Y);
            Vec2 necroPos = _g._sim.Units[_g._sim.NecromancerIndex].Position;
            float dist = (objPos - necroPos).Length();

            if (dist < ForagableWiggleRange)
            {
                // Wiggle: sinusoidal rotation, intensifies with proximity
                float proximity = 1f - (dist / ForagableWiggleRange); // 0 at edge, 1 at necro
                float wiggleAngle = MathF.Sin(_g._gameTime * 8f + obj.Seed * 10f) * 0.08f * proximity;
                rotation = wiggleAngle;

                // Scale pulse: subtle breathe effect
                float pulse = 1f + MathF.Sin(_g._gameTime * 4f + obj.Seed * 5f) * 0.03f * proximity;
                scale *= pulse;
            }

            // Mouse hover highlight: brighten + enlarge when cursor is over the object
            var mouseWorld = _g._renderer.ScreenToWorld(_g._input.MousePos, _g._camera);
            float mouseDist = (objPos - new Vec2(mouseWorld.X, mouseWorld.Y)).Length();
            if (mouseDist < 1.2f && dist < ForagableWiggleRange)
            {
                scale *= 1.1f;
                tint = new Color(1.3f, 1.3f, 1.3f, 1f); // brighten
            }
        }

        // Blueprint visual: semi-transparent with blue tint
        var rt = _g._envSystem.GetObjectRuntime(i);
        if (rt.BuildProgress < 1f)
        {
            float bpAlpha = 0.35f + 0.15f * rt.BuildProgress; // 0.35 → 0.5 as progress increases
            tint = new Color(0.5f * bpAlpha, 0.7f * bpAlpha, 1f * bpAlpha, bpAlpha);
        }

        // Apply weather ambient light
        tint = MultiplyColor(tint, _g._ambientColor);

        // Occlusion fade — semi-transparent while this object hides the player
        // (straight-alpha fade: scale A only; the draw surface premultiplies).
        if (_occlusionFade.TryGetValue(i, out float occFade))
            tint = ColorUtils.Fade(tint, occFade);

        _g.Scope.Draw(tex, screenPos, sourceRect, tint, rotation, origin, scale,
            flipX ? SpriteEffects.FlipHorizontally : SpriteEffects.None, 0f);

        // Hover-highlight: capture this object's exact on-screen sprite box. Env
        // origin already folds in the flip + raw pivot (Y not inverted, unlike unit
        // spritemeta), so build the box straight from origin.
        if (i == _g._hoveredObjectIdx && _g._gameData.Settings.Tooltips.ShowHoverHighlight)
        {
            float bw = frameW * scale, bh = frameH * scale;
            _g._hoverBoxObject = new Rectangle(
                (int)(screenPos.X - origin.X * scale), (int)(screenPos.Y - origin.Y * scale),
                (int)bw, (int)bh);
        }

        // Build progress bar for unbuilt objects — visible from the moment of placement
        // (empty bar at 0%) so players can see "placed, awaiting construction".
        if (rt.BuildProgress < 1f)
            DrawBuildProgressBar(screenPos, rt.BuildProgress);

        // Body bag overlay for craft-tables with a corpse loaded. Drawn immediately
        // after the table sprite (deferred SpriteBatch = call order = render order),
        // so the bag is always layered on top of the table within this object's
        // depth slot. Other Y-sorted objects still occlude correctly via the outer
        // depth-list pass.
        //
        // Lift parametrization: tableWorldH × pivotY locates the visual TOP of the
        // sprite in world-elevation (artists use pivotY=0.93 to anchor near the
        // base, so 0.93×height is the height above pivot to reach the sprite's
        // top edge). The 0.92 trim pulls the bag down a hair so it overlaps the
        // tabletop instead of floating above the rim. No magic constants — every
        // factor is sourced from def fields the artist already tuned.
        if (Game.TableSystem.IsTable(def))
        {
            var ts = _g._envSystem.GetTableState(i);
            if (ts.HasAnyCorpse())
            {
                for (int s = 0; s < ts.CorpseSlots.Length; s++)
                {
                    if (ts.CorpseSlots[s].IsEmpty) continue;
                    float tableWorldH = def.SpriteWorldHeight * obj.Scale * def.Scale;
                    // Lift = pivotY × 1.22 (slightly higher on the tabletop).
                    // Rotation = -π/12 (CCW ~15°) — small bump back from -π/15
                    // to align with the table's true long-axis angle.
                    float bagLift = tableWorldH * def.PivotY * 1.22f;
                    var bagScreen = _g._renderer.WorldToScreen(new Vec2(obj.X, obj.Y), bagLift, _g._camera);
                    var slot = ts.CorpseSlots[s];
                    // Same lift + rotation as the body bag, but render the actual
                    // corpse sprite when the bag is mothballed.
                    if (GameConstants.UseBodyBag)
                        DrawBaggedCorpseAt(bagScreen, slot.FacingAngle, -MathF.PI / 12f);
                    else
                        DrawCorpseSpriteAt(slot.SourceUnitDefID, bagScreen, slot.FacingAngle, slot.SpriteScale, -MathF.PI / 12f);
                    break;
                }
            }
        }
    }

    /// <summary>
    /// Render a corruption-transitioning tree via the dissolve shader. Returns
    /// true if drawn; false if the caller should fall back to the regular path
    /// (e.g. live or dead texture missing).
    /// </summary>
    private bool DrawDissolvingTree(SpriteScope scope, int i, in PlacedObjectRuntime rt)
    {
        var obj = _g._envSystem.Objects[i];
        var def = _g._envSystem.Defs[obj.DefIndex];

        var liveTex = _g._envSystem.GetDefTexture(obj.DefIndex);
        var deadTex = _g._envSystem.GetCorruptedTexture(i);
        if (liveTex == null || deadTex == null) return false;
        if (_g._envSystem.IsUsingPlaceholder(obj.DefIndex)) return false;

        // Frame 0 of the live spritesheet — we lock to frame 0 throughout the
        // dissolve so the live half doesn't keep animating as it fades.
        Rectangle frame0 = def.IsAnimated && def.AnimTotalFrames > 1
            ? def.GetAnimFrameRect(liveTex.Width, liveTex.Height, 0)
            : new Rectangle(0, 0, liveTex.Width, liveTex.Height);

        // Dest rect is sized to the dead texture (which should match per-frame
        // dimensions of the live sheet — see env_defs.json oak entries).
        float worldH = def.SpriteWorldHeight * obj.Scale * def.Scale;
        float pixelH = worldH * _g._camera.Zoom;
        float scale = pixelH / deadTex.Height;
        var screenPos = _g._renderer.WorldToScreen(new Vec2(obj.X, obj.Y), 0f, _g._camera);
        // Honor the per-instance random flip (same as the regular env-object path,
        // including the mirrored pivot) so a corrupting tree doesn't mirror-pop for
        // the duration of the dissolve. Flipping reverses the texCoord sweep, so
        // the live-frame UV remap and the noise pattern simply mirror with it.
        bool flipX = _g._envSystem.ShouldFlipObject(i);
        var origin = new Vector2((flipX ? (1f - def.PivotX) : def.PivotX) * deadTex.Width, def.PivotY * deadTex.Height);

        Color tint = MultiplyColor(Color.White, _g._ambientColor);

        // Set shader params. LiveFrameUV = frame 0 in normalized UV space.
        float u0 = frame0.X / (float)liveTex.Width;
        float v0 = frame0.Y / (float)liveTex.Height;
        float u1 = (frame0.X + frame0.Width)  / (float)liveTex.Width;
        float v1 = (frame0.Y + frame0.Height) / (float)liveTex.Height;
        float threshold = MathHelper.Clamp(rt.CorruptionTime / MathF.Max(_g._deathFog.CorruptionTransitionDuration, 0.01f), 0f, 1f);

        // Set effect parameters before PushMaterial (they upload at Apply time).
        // The s1 sampler state is declared on Materials.DissolveTree
        // (ExtraSamplerSlots), applied at every batch open — no hand-set needed.
        var liveParam = _g._dissolveTreeEffect!.Parameters["LiveSampler"];
        if (liveParam != null) liveParam.SetValue(liveTex);
        else _g.GraphicsDevice.Textures[1] = liveTex;
        _g._dissolveTreeEffect.Parameters["LiveFrameUV"]?.SetValue(new Vector4(u0, v0, u1, v1));
        _g._dissolveTreeEffect.Parameters["Threshold"]?.SetValue(threshold);
        _g._dissolveTreeEffect.Parameters["Seed"]?.SetValue(obj.Seed);
        _g._dissolveTreeEffect.Parameters["NoiseScale"]?.SetValue(6f);
        _g._dissolveTreeEffect.Parameters["EdgeSoftness"]?.SetValue(0.06f);
        _g._dissolveTreeEffect.Parameters["DebugMode"]?.SetValue(_g._deathFog.DebugVisible ? 1f : 0f);

        // Throttled per-instance log so we can confirm threshold animates over time.
        if (!_g._dissolveLoggedSeeds.TryGetValue(i, out var lastLogged) ||
            MathF.Abs(threshold - lastLogged) >= 0.1f || (threshold >= 0.99f && lastLogged < 0.99f))
        {
            _g._dissolveLoggedSeeds[i] = threshold;
            DebugLog.Log("startup", $"Dissolve frame: obj {i} ({def.Id}) t={threshold:F3} CorruptionTime={rt.CorruptionTime:F3} liveTex={liveTex.Width}x{liveTex.Height} deadTex={deadTex.Width}x{deadTex.Height}");
        }

        // Flush the open batch, draw through the dissolve material, resume —
        // the scope computes the resume state (no more guessed restores).
        scope.PushMaterial(Materials.DissolveTree!);
        // scope.Draw premultiplies the straight tint for the dissolve shader.
        scope.Draw(deadTex, screenPos, null, tint, 0f, origin, scale,
            flipX ? SpriteEffects.FlipHorizontally : SpriteEffects.None, 0f);
        scope.PopMaterial();
        return true;
    }

    /// <summary>
    /// Draw a unit's Idle (or fallback) first-frame sprite scaled to fit inside
    /// `dest`. Used by the table craft menu to show what's loaded in each corpse
    /// slot. Returns silently if the def is missing or its atlas isn't loaded —
    /// caller can render its own placeholder when nothing was drawn.
    /// </summary>
    internal void DrawUnitIdleSprite(string unitDefId, Rectangle dest)
    {
        if (string.IsNullOrEmpty(unitDefId) || _g._gameData == null)
        {
            DebugLog.Log("table", $"[DrawUnitIdleSprite] aborted: defId='{unitDefId}' gameData={_g._gameData != null}");
            return;
        }
        var unitDef = _g._gameData.Units.Get(unitDefId);
        if (unitDef.Sprite == null)
        {
            DebugLog.Log("table", $"[DrawUnitIdleSprite] '{unitDefId}': unitDef={unitDef != null} sprite={unitDef.Sprite != null}");
            return;
        }
        var atlasId = AtlasDefs.ResolveAtlasName(unitDef.Sprite.AtlasName);
        if (atlasId >= _g._atlases.Length)
        {
            DebugLog.Log("table", $"[DrawUnitIdleSprite] '{unitDefId}': atlasId={atlasId} out of range (atlases={_g._atlases.Length})");
            return;
        }
        var atlas = _g._atlases[atlasId];
        if (!atlas.IsLoaded)
        {
            DebugLog.Log("table", $"[DrawUnitIdleSprite] '{unitDefId}': atlas '{unitDef.Sprite.AtlasName}' not loaded");
            return;
        }

        var spriteData = atlas.GetUnit(unitDef.Sprite.SpriteName);
        if (spriteData == null)
        {
            DebugLog.Log("table", $"[DrawUnitIdleSprite] '{unitDefId}': spriteName '{unitDef.Sprite.SpriteName}' not in atlas");
            return;
        }

        // Prefer the dedicated "Icon" pose — a single camera-facing frame
        // (yaw 45 faces the viewer; verified visually against the atlas).
        // Units without an Icon pose fall back to Idle with the angle
        // preference list (different units author different angle keys —
        // old scheme: 30/60/300; new scheme: 0/45/90/270/315), then to ANY
        // authored angle (mirrors AnimController.ResolveFallbackAngle).
        var anim = spriteData.GetAnim("Icon");
        int[] anglePrefs = anim != null
            ? new[] { 45, 0, 315, 90, 270 }
            : new[] { 30, 0, 45, 60, 315, 90, 270, 300 };
        anim ??= spriteData.GetAnim("Idle");
        if (anim == null)
        {
            DebugLog.Log("table", $"[DrawUnitIdleSprite] '{unitDefId}': no Icon/Idle anim in spriteData");
            return;
        }
        System.Collections.Generic.List<Render.Keyframe>? kfs = null;
        foreach (int pref in anglePrefs)
        {
            kfs = anim.GetAngle(pref);
            if (kfs != null && kfs.Count > 0) break;
        }
        if (kfs == null || kfs.Count == 0)
        {
            // Last resort: take whatever is in the dictionary first.
            foreach (var (_, frames) in anim.AngleFrames)
            {
                if (frames.Count > 0) { kfs = frames; break; }
            }
        }
        if (kfs == null || kfs.Count == 0)
        {
            DebugLog.Log("table", $"[DrawUnitIdleSprite] '{unitDefId}': no usable angle keyframes (authored angles: {string.Join(",", anim.AngleFrames.Keys)})");
            return;
        }

        var frame = kfs[0].Frame;
        var tex = atlas.GetTextureForFrame(frame);
        if (tex == null)
        {
            DebugLog.Log("table", $"[DrawUnitIdleSprite] '{unitDefId}': frame texture null");
            return;
        }

        // Fit-inside scale: clamp to the smaller axis so the sprite preserves
        // aspect ratio and never crops out of the slot rect.
        float fitW = (float)(dest.Width - 4) / frame.Rect.Width;
        float fitH = (float)(dest.Height - 4) / frame.Rect.Height;
        float scale = MathF.Min(fitW, fitH);

        // Centered draw — origin at sprite center so we can position by box center.
        var origin = new Vector2(frame.Rect.Width / 2f, frame.Rect.Height / 2f);
        var center = new Vector2(dest.X + dest.Width / 2f, dest.Y + dest.Height / 2f);
        _g.Scope.Draw(tex, center, frame.Rect, Color.White, 0f, origin, scale,
            SpriteEffects.None, 0f);
    }

    private void DrawSpriteFrame(SpriteAtlas atlas, SpriteFrame frame, Vector2 screenPos,
                                  float scale, bool flipX, Color tint)
    {
        var tex = atlas.GetTextureForFrame(frame);
        if (tex == null) return;

        float pivotX = flipX ? (1f - frame.PivotX) : frame.PivotX;
        // Spritemeta pivots use bottom-left origin — Y needs to be flipped for top-left rendering
        float pivotY = 1f - frame.PivotY;

        var origin = new Vector2(pivotX * frame.Rect.Width, pivotY * frame.Rect.Height);
        var effects = flipX ? SpriteEffects.FlipHorizontally : SpriteEffects.None;
        _g.Scope.Draw(tex, screenPos, frame.Rect, tint, 0f, origin, scale, effects, 0f);
    }

    /// <summary>Ground-Y → SpriteBatch layerDepth, CAMERA-RELATIVE. Larger world Y (drawn in front /
    /// nearer the camera in the painter's sort) → SMALLER depth (occludes). Camera-relative because the
    /// old absolute mapping (1 - y*0.005) saturated to 0 at worldY ≥ 200 — a silent no-op across ~95%
    /// of a 4096-unit map. This maps ±2000 world units around the camera into (0,1). Units (the
    /// occluder stamp) and the fog puffs MUST use this same mapping so they compare —
    /// ReanimEffectSystem.FogDepth delegates here.</summary>
    internal static float FogDepthForY(float worldY, float cameraY)
        => MathHelper.Clamp(0.5f - (worldY - cameraY) * 0.00025f, 0f, 1f);

    /// <summary>Stamp each UNIT's sprite silhouette into the depth buffer (color-write off, depth-write
    /// on, cutout shader) so the additive reanimation fog can depth-test against them. UNITS ONLY, not
    /// corpses — during a morph there's no risen unit yet, so the fog still fully covers the morph;
    /// once the unit rises it can occlude its own lingering smoke. Gated by Performance.DepthSortedFog;
    /// runs after the color scene while the scene RT + its depth buffer are still bound.</summary>
    internal void DrawFogDepthOccluders()
    {
        if (Materials.DepthStamp == null) return;

        Materials.DepthStamp.Begin(_g._spriteBatch);

        for (int i = 0; i < _g._sim.Units.Count; i++)
        {
            var u = _g._sim.Units[i];
            if (!u.Alive) continue;
            // Match DrawSingleUnit's visibility so fog-hidden enemies don't stamp invisible occluders.
            if (u.Faction != Faction.Undead && !_g._fogOfWar.IsVisible(u.Position)) continue;
            if (!_g._unitAnims.TryGetValue(u.Id, out var animData)) continue;
            var unitDef = _g._gameData.Units.Get(u.UnitDefID);
            if (unitDef == null) continue;
            var atlas = _g._atlases[animData.AtlasID];
            if (!atlas.IsLoaded) continue;
            var fr = animData.Ctrl.GetCurrentFrame(u.FacingAngle);
            if (fr.Frame == null) continue;
            var frame = fr.Frame.Value;
            var tex = atlas.GetTextureForFrame(frame);
            if (tex == null) continue;

            float worldH = (unitDef.SpriteWorldHeight > 0 ? unitDef.SpriteWorldHeight : 1.8f) * u.SpriteScale;
            float scale = (worldH * _g._camera.Zoom) / animData.RefFrameHeight;
            var sp = _g._renderer.WorldToScreen(u.RenderPos, u.Z, _g._camera);

            float pivotX = fr.FlipX ? (1f - frame.PivotX) : frame.PivotX;
            float pivotY = 1f - frame.PivotY;
            var origin = new Vector2(pivotX * frame.Rect.Width, pivotY * frame.Rect.Height);
            var effects = fr.FlipX ? SpriteEffects.FlipHorizontally : SpriteEffects.None;
            // Depth from RenderPos.Y (not Position.Y) so a lunging unit's stamp is
            // depth-valued where its sprite is actually drawn.
            _g.Scope.Draw(tex, sp, frame.Rect, Color.White, 0f, origin, scale, effects,
                FogDepthForY(u.RenderPos.Y, _g._camera.Position.Y));
        }

        _g._spriteBatch.End();
    }

    /// <summary>Draw a unit sprite with the wading shader applied — fades alpha
    /// below <paramref name="waterlineV"/> (V coord, 0=top, 1=bottom) and adds a
    /// foam smear at the line. Wraps the call in End()/Begin(effect)/End()/
    /// Begin() so other sprites this frame keep using the default batch. Caller
    /// should only invoke when the unit is actually in shallow water — every
    /// invocation pays the cost of two batch transitions.</summary>
    /// <summary>F2 overlay: draws the body bbox and the computed waterline
    /// cut directly on top of the rendered unit, plus a small text label
    /// with the raw + remapped waterness, slope, and fractions in play. Used
    /// for tuning per-unit WadingFractionByDirection values without bouncing
    /// between game and JSON editor.</summary>
    private void DrawWaterDebugOverlay(int unitIdx, SpriteFrame frame, Vector2 sp,
                                        float pixelH, in WadingState wading)
    {
        var unit = _g._sim.Units[unitIdx];
        // Body bbox bounds in screen Y.
        float pivotFlippedV = 1f - frame.PivotY;
        float bodyTopY = sp.Y + (frame.BodyTopV - pivotFlippedV) * pixelH;
        float bodyBotY = sp.Y + (frame.BodyBottomV - pivotFlippedV) * pixelH;

        // Estimate body width in screen px (no real frame X bounds — use the
        // sprite frame's PixelW. Approximation: assume body roughly spans the
        // frame minus 20% padding on each side; close enough for an overlay).
        float pixelW = frame.Rect.Width * (pixelH / frame.Rect.Height);
        float bodyHalfW = pixelW * 0.4f;
        var bboxCol = new Color(80, 200, 255, 110);
        DrawRectOutline(new Rectangle(
            (int)(sp.X - bodyHalfW), (int)bodyTopY,
            (int)(bodyHalfW * 2),    (int)(bodyBotY - bodyTopY)),
            bboxCol);

        // Bottom waterline (with slope) as a short line across the body.
        if (wading.Active)
        {
            float waterY = sp.Y + (wading.WaterlineV - pivotFlippedV) * pixelH;
            // Slope is dV/dU in local frame UV; convert: dY/dX in screen.
            float slopeYpx = wading.Slope * pixelH / pixelW;
            float lineHalfW = bodyHalfW;
            var lineCol = new Color(255, 100, 100, 200);
            DrawLine(new Vector2(sp.X - lineHalfW, waterY - lineHalfW * slopeYpx),
                     new Vector2(sp.X + lineHalfW, waterY + lineHalfW * slopeYpx),
                     lineCol, 2);

            // Top waterline (if active).
            if (wading.TopWaterlineV >= 0f)
            {
                float topY = sp.Y + (wading.TopWaterlineV - pivotFlippedV) * pixelH;
                var topCol = new Color(255, 180, 100, 200);
                DrawLine(new Vector2(sp.X - lineHalfW, topY - lineHalfW * slopeYpx),
                         new Vector2(sp.X + lineHalfW, topY + lineHalfW * slopeYpx),
                         topCol, 2);
            }
        }

        // Text label above the sprite.
        var unitDef = _g._gameData.Units.Get(unit.UnitDefID);
        string topStr = wading.TopWaterlineV >= 0f ? $"topV={wading.TopWaterlineV:F2}" : "topV=-";
        string label = wading.Active
            ? $"w={wading.Waterness:F2} V={wading.WaterlineV:F2} {topStr} s={wading.Slope:F2} ang={wading.SpriteAngle}"
            : $"w=0  (dry)  ang={wading.SpriteAngle}";
        DrawText(_g._smallFont, label,
            new Vector2((int)(sp.X - 60), (int)(bodyTopY - 14)),
            new Color(255, 255, 255, 220));
    }

    /// <summary>Draw a 1px outline rectangle.</summary>
    private void DrawRectOutline(Rectangle r, Color c)
    {
        Necroking.Render.DrawUtils.DrawRectBorder(_g._spriteBatch, _g._pixel, r, c);
    }

    /// <summary>Screen-space AABB of a sprite frame drawn at <paramref name="sp"/>
    /// with the given uniform scale + horizontal flip. Mirrors DrawSpriteFrame's
    /// pivot math (spritemeta pivots are bottom-left origin → Y flipped) so the box
    /// matches the rendered sprite exactly.</summary>
    private static Rectangle SpriteFrameAABB(Vector2 sp, in SpriteFrame frame, float scale, bool flipX)
    {
        float pivotX = flipX ? (1f - frame.PivotX) : frame.PivotX;
        float pivotY = 1f - frame.PivotY;
        float w = frame.Rect.Width * scale, h = frame.Rect.Height * scale;
        return new Rectangle((int)(sp.X - pivotX * w), (int)(sp.Y - pivotY * h), (int)w, (int)h);
    }

    /// <summary>Draw the outline box around whichever world object is under the
    /// cursor (unit / corpse / building / ground item). Boxes are captured during
    /// the sprite pass with the exact sprite bounds; here we just stroke them,
    /// slightly inflated so the line sits just outside the art. World objects only
    /// — never UI. Gated by the ShowHoverHighlight tooltip setting.</summary>
    private void DrawHoverHighlights()
    {
        // Dev-marked units (via the 'mark' dev command) — always drawn, white, and
        // not gated by the ShowHoverHighlight setting (it's a tooling/debug aid).
        if (_g._devMarkBoxes.Count > 0)
        {
            var white = new Color(255, 255, 255, 235);
            foreach (var b in _g._devMarkBoxes) { var r = b; r.Inflate(2, 2); DrawRectVariant(r, white, 1); }
        }

        // Toast naming the active variant after a cycle press (drawn even when Off).
        DrawHoverVariantLabel();
        DrawDepthFogToast();   // 'H' depth-sorted-fog ON/OFF flash
        DrawGpuWarnToast();    // hybrid-GPU warning (integrated GPU while discrete idle)

        if (!_g._gameData.Settings.Tooltips.ShowHoverHighlight) return;

        // Stroke a captured sprite box ONLY for the screen-space shapes (Corners / Rectangle).
        // Ground shapes (Circle / Ground Box / Diamond Box) draw behind the sprites in
        // DrawHoverGroundMarkers. Variant is resolved per-category (building vs the rest).
        void Stroke(Rectangle? box, int variant)
        {
            if (variant < 0 || box is not { } b) return;
            int shape = variant / 4;
            if (shape != 1 && shape != 2) return;
            HoverStyle(variant % 4, out int thick, out byte alpha);
            // Straight alpha — the draw surface premultiplies for the open material.
            var c = new Color(255, 230, 120, (int)alpha);
            b.Inflate(2, 2);
            if (shape == 1) DrawCornersVariant(b, c, thick);
            else            DrawRectVariant(b, c, thick);
        }
        Stroke(_g._hoverBoxObject, HoverVariantFor(HoveredObjectIsBuilding()));
        Stroke(_g._hoverBoxCorpse, HoverVariantFor(false));
        Stroke(_g._hoverBoxUnit,   HoverVariantFor(false));
    }

    /// <summary>Is the env object currently under the cursor a building (vs a foragable / ground
    /// item)? Drives which hover-highlight category applies (buildings get their own marker style).</summary>
    private bool HoveredObjectIsBuilding()
        => _g._hoveredObjectIdx >= 0 && _g._hoveredObjectIdx < _g._envSystem.ObjectCount
           && _g._envSystem.Defs[_g._envSystem.GetObject(_g._hoveredObjectIdx).DefIndex].IsBuilding;

    /// <summary>Resolve the hover-highlight variant (shape*4 + lineStyle) for a category. The dev
    /// override (_g._hoverHighlightVariant >= 0, set via 'H' / hover_variant) forces a single variant on
    /// everything for design testing; otherwise the per-category Tooltips setting applies. -1 = draw
    /// nothing.</summary>
    private int HoverVariantFor(bool isBuilding)
    {
        if (_g._hoverHighlightVariant >= 0)
            return _g._hoverHighlightVariant >= 20 ? -1 : _g._hoverHighlightVariant;
        var t = _g._gameData.Settings.Tooltips;
        int v = isBuilding ? t.HoverHighlightBuilding : t.HoverHighlightRest;
        return (v >= 0 && v < 20) ? v : -1;
    }

    /// <summary>Pick the hoverable env object under the cursor for the HUD info tooltip + highlight.
    /// Buildings use the drawn marker footprint (diamond/box/circle) as the hit area so the whole
    /// visible shape is pickable; foragables/ground items use a simple radius around their origin.
    /// Returns the object index or -1. Per-kind gating honours the Tooltips toggles — unless
    /// <paramref name="anyObject"/> is set (map-editor hover-inspect), which makes EVERY visible
    /// env object pickable (trees, rocks, props included, via the radius pick).</summary>
    internal int PickHoveredObject(Vector2 cursorScreen, Vec2 cursorWorld, bool anyObject = false)
    {
        var tcfg = _g._gameData.Settings.Tooltips;
        int buildingShape = System.Math.Clamp(tcfg.HoverHighlightBuilding, 0, 19) / 4;
        float pr = tcfg.GroundPickRadius;
        float bestScore = float.MaxValue;
        int picked = -1;
        for (int oi = 0; oi < _g._envSystem.ObjectCount; oi++)
        {
            var obj = _g._envSystem.GetObject(oi);
            var d = _g._envSystem.Defs[obj.DefIndex];
            bool hoverable = anyObject
                           || (d.IsBuilding && tcfg.ShowBuildingInfo)
                           || ((d.IsForagable || d.IsBerryBush) && tcfg.ShowGroundItemInfo);
            if (!hoverable) continue;
            // Collected foragables (respawning) and destroyed objects are invisible — don't
            // surface a tooltip for something that isn't drawn.
            if (!_g._envSystem.IsObjectVisible(oi)) continue;

            float score;
            if (d.IsBuilding)
            {
                // Hover area = the drawn marker footprint; score by distance to the collision centre.
                if (!CursorInObjectMarker(oi, buildingShape, cursorScreen)) continue;
                var cc = EnvironmentSystem.GetCollisionCircle(d, in obj);
                float cx = cc.CX - cursorWorld.X;
                float cy = cc.CY - cursorWorld.Y;
                score = cx * cx + cy * cy;
            }
            else
            {
                // Anchor the forgiving radius pick at the collision centre (where the marker is
                // drawn), not the sprite origin — robust if a ground item ever gets a collision offset.
                var cc = EnvironmentSystem.GetCollisionCircle(d, in obj);
                float hdx = cc.CX - cursorWorld.X;
                float hdy = cc.CY - cursorWorld.Y;
                score = hdx * hdx + hdy * hdy;
                if (score >= pr * pr) continue;
            }
            if (score < bestScore) { bestScore = score; picked = oi; }
        }
        return picked;
    }

    /// <summary>Is the screen cursor inside the ground hover-marker AREA of env object
    /// <paramref name="oi"/>, for the given marker <paramref name="shape"/> (0 Circle, 3 Ground Box,
    /// 4 Diamond Box; screen-space shapes fall back to the circle)? Mirrors DrawHoverGroundMarkers'
    /// projection exactly so the pickable area matches the drawn footprint — letting a big building
    /// be hovered anywhere within its diamond, not just near its origin point.</summary>
    private bool CursorInObjectMarker(int oi, int shape, Vector2 cursorScreen)
    {
        var obj = _g._envSystem.GetObject(oi);
        var def = _g._envSystem.Defs[obj.DefIndex];
        float es = def.Scale * obj.Scale; // for the min-radius fallback only
        var cc = EnvironmentSystem.GetCollisionCircle(def, in obj);
        var ccen = new Vec2(cc.CX, cc.CY);
        float worldR = MathF.Max(cc.R, 0.45f * es) * HoverMarkerRadiusMul;
        var cenS  = _g._renderer.WorldToScreen(ccen, 0f, _g._camera);
        var edgeS = _g._renderer.WorldToScreen(ccen + new Vec2(worldR, 0f), 0f, _g._camera);
        float rx = MathF.Abs(edgeS.X - cenS.X);
        if (rx <= 0.001f) return false;
        float hh = rx * HoverMarkerFlatten;
        float nx = MathF.Abs(cursorScreen.X - cenS.X) / rx;
        float ny = MathF.Abs(cursorScreen.Y - cenS.Y) / hh;
        return shape switch
        {
            4 => nx + ny <= 1f,            // diamond (rhombus)
            3 => nx <= 1f && ny <= 1f,     // axis-aligned box
            _ => nx * nx + ny * ny <= 1f,  // circle / ellipse (and fallback)
        };
    }

    /// <summary>Hover-highlight ground variants, rendered BEHIND the sprites (RTS look). Three shapes
    /// share this path because all live on the ground plane: the <b>Circle</b> (flattened ellipse), the
    /// <b>Ground Box</b> (axis-aligned corner brackets on a flattened rectangle), and the
    /// <b>Diamond Box</b> (iso rhombus aligned to the world grid). Each hovered thing resolves its own
    /// variant per-category (building vs the rest), anchored to the object's CURRENT world position and
    /// a STABLE world-space radius (its collision footprint), projected fresh each frame — so they
    /// don't pulse as the sprite animates and stay locked under the object as the camera moves.</summary>
    private void DrawHoverGroundMarkers()
    {
        if (!_g._gameData.Settings.Tooltips.ShowHoverHighlight) return;

        const float RadiusMul = HoverMarkerRadiusMul;   // shared with the building hit-test
        const float Flatten   = HoverMarkerFlatten;
        void Mark(Vec2 worldPos, float worldRadius, int variant)
        {
            if (variant < 0 || worldRadius <= 0f) return;
            int shape = variant / 4;
            if (shape != 0 && shape != 3 && shape != 4) return;   // ground shapes only
            HoverStyle(variant % 4, out int thick, out byte alpha);
            // Straight alpha — the draw surface premultiplies for the open material.
            var c = new Color(255, 230, 120, (int)alpha);
            // Constant pixel thickness (the style's thick/thin px) — does NOT scale with the marker
            // size, so a big building's outline is the same weight as a small unit's. Matches the
            // screen-space Corners/Rectangle variants.
            float lineW = thick;
            // Project the centre + a world-radius offset; the screen delta is the on-screen radius,
            // so the marker scales correctly with camera zoom without depending on the sprite box.
            var cen  = _g._renderer.WorldToScreen(worldPos, 0f, _g._camera);
            var edge = _g._renderer.WorldToScreen(worldPos + new Vec2(worldRadius, 0f), 0f, _g._camera);
            float rx = MathF.Abs(edge.X - cen.X);
            float hh = rx * Flatten;
            if (shape == 4)
            {
                // Iso diamond aligned to the world grid (AoE2-style footprint): vertices at
                // N/S/E/W of the flattened ellipse, with corner brackets along the diamond edges.
                DrawGroundDiamondCorners(cen.X, cen.Y, rx, hh, c, lineW);
            }
            else if (shape == 3)
            {
                // Axis-aligned in screen space → reuse the clean filled-bar corner brackets
                // (DrawCornersVariant) so the L arms share each corner exactly. The box is the
                // flattened ground footprint: full width 2*rx, height squashed by Flatten.
                var r = new Rectangle((int)(cen.X - rx), (int)(cen.Y - hh), (int)(2f * rx), (int)(2f * hh));
                DrawCornersVariant(r, c, Math.Max(1, (int)MathF.Round(lineW)));
            }
            else DrawEllipseOutline(cen.X, cen.Y, rx, hh, c, lineW);
        }

        if (_g._hoveredUnitIdx >= 0 && _g._hoveredUnitIdx < _g._sim.Units.Count)
            Mark(_g._sim.Units[_g._hoveredUnitIdx].Position, _g._sim.Units[_g._hoveredUnitIdx].Radius * RadiusMul, HoverVariantFor(false));
        if (_g._hoveredObjectIdx >= 0 && _g._hoveredObjectIdx < _g._envSystem.ObjectCount)
        {
            var obj = _g._envSystem.GetObject(_g._hoveredObjectIdx);
            var def = _g._envSystem.Defs[obj.DefIndex];
            // Match the baked collision footprint exactly (GetCollisionCircle = the same circle
            // StampObjectCollisionInto bakes) — buildings have large offsets/def-scales, so using
            // obj.Scale + obj.X/Y alone left the marker mis-sized and shifted off the collision area.
            float es = def.Scale * obj.Scale; // for the min-radius fallback only
            var cc = EnvironmentSystem.GetCollisionCircle(def, in obj);
            var ccen = new Vec2(cc.CX, cc.CY);
            float cr = MathF.Max(cc.R, 0.45f * es);
            Mark(ccen, cr * RadiusMul, HoverVariantFor(def.IsBuilding));
        }
        if (_g._hoveredCorpseIdx >= 0 && _g._hoveredCorpseIdx < _g._sim.Corpses.Count)
        {
            var cp = _g._sim.Corpses[_g._hoveredCorpseIdx];
            float cr = _g._gameData.Units.Get(cp.UnitDefID)?.Radius ?? 0.5f;
            Mark(cp.Position, cr * RadiusMul, HoverVariantFor(false));
        }
    }

    /// <summary>AoE2-style iso diamond selection: a rhombus whose vertices sit at N/S/E/W of the
    /// flattened footprint (so it aligns to the world grid the way the buildings do), drawn as four
    /// corner brackets running a short way along each of the diamond's edges.</summary>
    private void DrawGroundDiamondCorners(float cx, float cy, float rx, float ry, Color c, float thickness)
    {
        var top    = new Vector2(cx, cy - ry);
        var right  = new Vector2(cx + rx, cy);
        var bottom = new Vector2(cx, cy + ry);
        var left   = new Vector2(cx - rx, cy);
        const float ArmFrac = 0.34f;   // bracket arm = fraction of each diamond edge
        // Both arms at a vertex start exactly at that vertex, so they meet cleanly (DrawThickLine
        // extends from its first point) — no need for the axis-aligned filled-bar trick here.
        void Bracket(Vector2 v, Vector2 a, Vector2 b)
        {
            DrawThickLine(v, v + (a - v) * ArmFrac, c, thickness);
            DrawThickLine(v, v + (b - v) * ArmFrac, c, thickness);
        }
        Bracket(top,    right, left);
        Bracket(right,  top,   bottom);
        Bracket(bottom, right, left);
        Bracket(left,   top,   bottom);
    }

    // Map a line style (variant % 4) to a thickness + alpha. 0=Thick-Solid 1=Thin-Solid
    // 2=Thick-Faint 3=Thin-Faint.
    private static void HoverStyle(int style, out int thickness, out byte alpha)
    {
        thickness = (style == 0 || style == 2) ? 3 : 1;
        alpha     = (style == 0 || style == 1) ? (byte)235 : (byte)115;   // Faint ≈ 45% opacity
    }

    private void FillRect(int x, int y, int w, int h, Color c)
        => _g.Scope.Draw(_g._pixel, new Rectangle(x, y, w, h), c);

    /// <summary>Full rectangle outline of thickness t (drawn as four filled bars).</summary>
    private void DrawRectVariant(Rectangle r, Color c, int t)
    {
        FillRect(r.X, r.Y, r.Width, t, c);            // top
        FillRect(r.X, r.Bottom - t, r.Width, t, c);   // bottom
        FillRect(r.X, r.Y, t, r.Height, c);           // left
        FillRect(r.Right - t, r.Y, t, r.Height, c);   // right
    }

    /// <summary>Just the four L-shaped corners of the box (Factorio-style).</summary>
    private void DrawCornersVariant(Rectangle r, Color c, int t)
    {
        int L = Math.Clamp(Math.Min(r.Width, r.Height) / 4, 6, 22);   // corner arm length
        FillRect(r.X, r.Y, L, t, c);                     FillRect(r.X, r.Y, t, L, c);                     // TL
        FillRect(r.Right - L, r.Y, L, t, c);             FillRect(r.Right - t, r.Y, t, L, c);             // TR
        FillRect(r.X, r.Bottom - t, L, t, c);            FillRect(r.X, r.Bottom - L, t, L, c);            // BL
        FillRect(r.Right - L, r.Bottom - t, L, t, c);    FillRect(r.Right - t, r.Bottom - L, t, L, c);    // BR
    }

    /// <summary>Ellipse outline approximated by line segments (used by the ground-ring variant).</summary>
    private void DrawEllipseOutline(float cx, float cy, float rx, float ry, Color c, float thickness)
    {
        const int N = 30;
        var prev = new Vector2(cx + rx, cy);
        for (int i = 1; i <= N; i++)
        {
            float a = (i / (float)N) * (MathF.PI * 2f);
            var cur = new Vector2(cx + rx * MathF.Cos(a), cy + ry * MathF.Sin(a));
            DrawThickLine(prev, cur, c, thickness);
            prev = cur;
        }
    }

    /// <summary>Like DrawLine but with a float line width, for smooth zoom-scaled ring thickness.
    /// Thickness is CENTRED on the a→b axis (origin Y = 0.5 of the 1px source), so segments meeting
    /// at a shared endpoint (e.g. diamond corner brackets) connect cleanly instead of each being
    /// offset to one perpendicular side.</summary>
    private void DrawThickLine(Vector2 a, Vector2 b, Color c, float thickness)
    {
        var d = b - a;
        float len = d.Length();
        if (len < 0.5f) return;
        float angle = MathF.Atan2(d.Y, d.X);
        _g.Scope.Draw(_g._pixel, a, null, c, angle, new Vector2(0f, 0.5f),
            new Vector2(len, thickness), SpriteEffects.None, 0f);
    }

    private static readonly string[] _hoverShapeNames = { "Circle", "Corners", "Rectangle", "Ground Box", "Diamond Box" };
    private static readonly string[] _hoverStyleNames = { "Thick Solid", "Thin Solid", "Thick Faint", "Thin Faint" };

    private void DrawHoverVariantLabel()
    {
        if (_g._hoverVariantLabelTimer <= 0f || _g._font == null) return;
        string label = _g._hoverHighlightVariant < 0
            ? "Hover override OFF — using Tooltips settings"
            : _g._hoverHighlightVariant >= 20
                ? "Hover override: highlight OFF"
                : $"Hover override {_g._hoverHighlightVariant + 1}/20: {_hoverShapeNames[_g._hoverHighlightVariant / 4]} - {_hoverStyleNames[_g._hoverHighlightVariant % 4]}";
        var pos = new Vector2((int)18, (int)112);
        _g.Scope.DrawString(_g._font, label, pos + new Vector2(1, 1), new Color(0, 0, 0, 190));
        _g.Scope.DrawString(_g._font, label, pos, new Color(255, 235, 150));
    }

    // One-time hybrid-GPU warning (set in LoadContent): the game is on the integrated
    // GPU while a discrete one is installed. Amber so it reads as a warning.
    private void DrawGpuWarnToast()
    {
        if (_g._gpuWarnToastTimer <= 0f || _g._font == null || _g._gpuWarnToastMsg == null) return;
        var pos = new Vector2((int)18, (int)152);
        _g.Scope.DrawString(_g._font, _g._gpuWarnToastMsg, pos + new Vector2(1, 1), new Color(0, 0, 0, 190));
        _g.Scope.DrawString(_g._font, _g._gpuWarnToastMsg, pos, new Color(255, 190, 90));
    }

    // Flash the depth-sorted-fog state (ON/OFF) for a couple seconds after the 'H' toggle.
    private void DrawDepthFogToast()
    {
        if (_g._depthFogToastTimer <= 0f || _g._font == null) return;
        bool on = _g._gameData.Settings.Performance.DepthSortedFog;
        string label = on ? "Depth-sorted fog: ON  (unit occludes smoke)"
                          : "Depth-sorted fog: OFF  (smoke on top)";
        var pos = new Vector2((int)18, (int)134);
        _g.Scope.DrawString(_g._font, label, pos + new Vector2(1, 1), new Color(0, 0, 0, 190));
        _g.Scope.DrawString(_g._font, label, pos, on ? new Color(150, 235, 180) : new Color(230, 200, 170));
    }

    /// <summary>Draw a 2D line by rotating the _g._pixel sprite. Cheap, AA-free.</summary>
    private void DrawLine(Vector2 a, Vector2 b, Color c, int thickness)
    {
        var d = b - a;
        float len = d.Length();
        if (len < 0.5f) return;
        float angle = MathF.Atan2(d.Y, d.X);
        _g.Scope.Draw(_g._pixel, a, null, c, angle, Vector2.Zero,
            new Vector2(len, thickness), SpriteEffects.None, 0f);
    }

    private void DrawWadingSpriteFrame(SpriteScope scope, SpriteAtlas atlas, SpriteFrame frame,
                                        Vector2 screenPos,
                                        float scale, bool flipX, Color tint,
                                        float waterlineCenterV, float topWaterlineCenterV,
                                        float waterlineSlope, float topWaterlineSlope)
    {
        var tex = atlas.GetTextureForFrame(frame);
        if (tex == null) return;
        if (_g._wadingEffect == null || Materials.Wading == null)
        {
            // Fall back to normal draw if shader missing — at least the unit is
            // still visible; just no waterline effect.
            DrawSpriteFrame(atlas, frame, screenPos, scale, flipX, tint);
            return;
        }

        // Atlas U/V range of this frame — shader uses them to normalize the
        // incoming atlas texCoord into local 0..1 frame UV.
        float atlasW = (float)tex.Width;
        float atlasH = (float)tex.Height;
        float frameLeftU = frame.Rect.X / atlasW;
        float frameRightU = (frame.Rect.X + frame.Rect.Width) / atlasW;
        float frameTopV = frame.Rect.Y / atlasH;
        float frameBotV = (frame.Rect.Y + frame.Rect.Height) / atlasH;
        _g._wadingEffect.Parameters["FrameLeftU"]?.SetValue(frameLeftU);
        _g._wadingEffect.Parameters["FrameRightU"]?.SetValue(frameRightU);
        _g._wadingEffect.Parameters["FrameTopV"]?.SetValue(frameTopV);
        _g._wadingEffect.Parameters["FrameBottomV"]?.SetValue(frameBotV);

        // No flipX correction on the slope: SpriteBatch flipping reverses
        // texCoord.x sweep through the atlas frame, which gives the shader a
        // naturally-reversed localU. Passing slope as-is and letting the
        // reversed localU flip it produces the right output diagonal for
        // mirrored sprites. (Earlier code negated here, which double-flipped
        // and caused NW to show the same diagonal direction as NE.)
        _g._wadingEffect.Parameters["WaterlineCenterV"]?.SetValue(waterlineCenterV);
        _g._wadingEffect.Parameters["WaterlineSlope"]?.SetValue(waterlineSlope);
        _g._wadingEffect.Parameters["TopWaterlineCenterV"]?.SetValue(topWaterlineCenterV);
        _g._wadingEffect.Parameters["TopWaterlineSlope"]?.SetValue(topWaterlineSlope);
        // FoamHalfWidth/TopFoamHalfWidth/UnderwaterAlpha/FoamColor are constants,
        // set once at load (Game1 LoadContent, Wading block).

        scope.PushMaterial(Materials.Wading);

        float pivotX = flipX ? (1f - frame.PivotX) : frame.PivotX;
        float pivotY = 1f - frame.PivotY;
        var origin = new Vector2(pivotX * frame.Rect.Width, pivotY * frame.Rect.Height);
        var effects = flipX ? SpriteEffects.FlipHorizontally : SpriteEffects.None;
        // scope.Draw premultiplies the straight tint for the wading shader.
        scope.Draw(tex, screenPos, frame.Rect, tint, 0f, origin, scale, effects, 0f);

        scope.PopMaterial();
    }

    /// <summary>Multiply two colors component-wise (for ambient tinting).</summary>
    private static Color MultiplyColor(Color a, Color b)
    {
        return ColorUtils.Multiply(a, b);
    }

    /// <summary>
    /// Draw a pulsing outline around a sprite using the OutlineFlat shader.
    /// Renders the sprite 8 times at directional offsets with a flat color.
    /// </summary>
    private void DrawSpriteOutline(SpriteScope scope, SpriteAtlas atlas, SpriteFrame frame,
                                    Vector2 screenPos,
                                    float scale, bool flipX, HdrColor color1, HdrColor color2,
                                    float outlineWidth, float pulseWidth, float pulseSpeed,
                                    int blendMode)
    {
        var tex = atlas.GetTextureForFrame(frame);
        if (tex == null || _g._outlineFlatEffect == null) return;
        var material = blendMode == 1 ? Materials.OutlineAdditive : Materials.OutlineAlpha;
        if (material == null) return;

        float t = 0.5f + 0.5f * MathF.Sin(_g._gameTime * pulseSpeed * 2f * MathF.PI);

        float offset = outlineWidth + (pulseWidth - outlineWidth) * t;
        if (offset < 0.5f) return;

        float colR = MathHelper.Lerp(color1.R / 255f, color2.R / 255f, t);
        float colG = MathHelper.Lerp(color1.G / 255f, color2.G / 255f, t);
        float colB = MathHelper.Lerp(color1.B / 255f, color2.B / 255f, t);
        float colA = MathHelper.Lerp(color1.A / 255f, color2.A / 255f, t);
        float intensity = MathHelper.Lerp(color1.Intensity, color2.Intensity, t);

        _g._outlineFlatEffect.Parameters["OutlineColor"]?.SetValue(
            new Vector4(colR * intensity, colG * intensity, colB * intensity, colA));

        // OutlineFlat outputs STRAIGHT alpha — the material picks the
        // NonPremultiplied/Additive blend variant (see the .fx header).
        scope.PushMaterial(material);

        float pivotX = flipX ? (1f - frame.PivotX) : frame.PivotX;
        float pivotY = 1f - frame.PivotY;
        var origin = new Vector2(pivotX * frame.Rect.Width, pivotY * frame.Rect.Height);
        var effects = flipX ? SpriteEffects.FlipHorizontally : SpriteEffects.None;

        for (int d = 0; d < 8; d++)
        {
            float dx = _outlineDirs[d][0] * offset;
            float dy = _outlineDirs[d][1] * offset;
            scope.Draw(tex, new Vector2(screenPos.X + dx, screenPos.Y + dy),
                frame.Rect, Color.White, 0f, origin, scale, effects, 0f);
        }

        scope.PopMaterial();
    }

    private void DrawUnitPulsingOutline(SpriteScope scope, int unitIdx, SpriteAtlas atlas,
                                         SpriteFrame frame,
                                         Vector2 screenPos, float scale, bool flipX)
    {
        foreach (var buff in _g._sim.Units[unitIdx].ActiveBuffs)
        {
            var buffDef = _g._gameData.Buffs.Get(buff.BuffDefID);
            if (buffDef != null && buffDef.HasPulsingOutline && buffDef.PulsingOutline != null)
            {
                var po = buffDef.PulsingOutline;
                DrawSpriteOutline(scope, atlas, frame, screenPos, scale, flipX,
                    po.Color, po.PulseColor, po.OutlineWidth, po.PulseWidth, po.PulseSpeed, po.BlendMode);
                return;
            }
        }
    }

    private void DrawGhostOutline(SpriteScope scope, SpriteAtlas atlas, SpriteFrame frame,
                                    Vector2 screenPos, float scale, bool flipX)
    {
        DrawSpriteOutline(scope, atlas, frame, screenPos, scale, flipX,
            _ghostColor1, _ghostColor2, 1.0f, 1.5f, 0.8f, 0);
    }

    private void DrawHPBar(int unitIdx, Vector2 screenPos)
    {
        var stats = _g._sim.Units[unitIdx].Stats;
        int maxHp = BuffSystem.EffectiveMaxHP(_g._sim.Units, unitIdx);
        if (maxHp <= 0) return;

        float hpRatio = (float)stats.HP / maxHp;
        if (hpRatio >= 1f) return; // don't show full HP bars

        // Position HP bar above the unit based on its sprite height
        var unitDef = _g._gameData.Units.Get(_g._sim.Units[unitIdx].UnitDefID);
        float spriteWorldH = (unitDef != null && unitDef.SpriteWorldHeight > 0)
            ? unitDef.SpriteWorldHeight : 1.8f;
        float spriteScale = _g._sim.Units[unitIdx].SpriteScale;
        float barOffset = spriteWorldH * spriteScale * _g._camera.Zoom * 0.9f + 5f;

        float barW = 30f;
        float barH = 3f;
        float barX = screenPos.X - barW / 2f;
        float barY = screenPos.Y - barOffset;

        _g.Scope.Draw(_g._pixel, new Rectangle((int)barX - 1, (int)barY - 1, (int)barW + 2, (int)barH + 2), new Color(0, 0, 0, 160));

        Color hpColor = hpRatio > 0.5f ? new Color(60, 180, 60) : (hpRatio > 0.25f ? new Color(200, 180, 40) : new Color(200, 40, 40));
        _g.Scope.Draw(_g._pixel, new Rectangle((int)barX, (int)barY, (int)(barW * hpRatio), (int)barH), hpColor);
    }

    private void DrawSpellCategoryIcon(string category, int cx, int cy)
    {
        switch (category)
        {
            case "Projectile":
                for (int dy2 = -6; dy2 <= 6; dy2++)
                    for (int dx2 = -6; dx2 <= 6; dx2++)
                        if (dx2 * dx2 + dy2 * dy2 <= 36)
                            _g.Scope.Draw(_g._pixel, new Rectangle(cx + dx2, cy + dy2, 1, 1), new Color(255, 140, 30, 200));
                break;
            case "Buff":
                for (int dy2 = -6; dy2 <= 6; dy2++)
                    for (int dx2 = -6; dx2 <= 6; dx2++)
                        if (dx2 * dx2 + dy2 * dy2 <= 36)
                            _g.Scope.Draw(_g._pixel, new Rectangle(cx + dx2, cy + dy2, 1, 1), new Color(60, 200, 60, 200));
                break;
            case "Strike":
            {
                var lc = new Color(255, 230, 50, 220);
                _g.Scope.Draw(_g._pixel, new Rectangle(cx + 2, cy - 8, 3, 5), lc);
                _g.Scope.Draw(_g._pixel, new Rectangle(cx - 2, cy - 3, 6, 2), lc);
                _g.Scope.Draw(_g._pixel, new Rectangle(cx - 3, cy - 1, 3, 5), lc);
                _g.Scope.Draw(_g._pixel, new Rectangle(cx - 4, cy + 4, 4, 2), lc);
                break;
            }
            case "Summon":
                for (int dy2 = -6; dy2 <= 6; dy2++)
                    for (int dx2 = -6; dx2 <= 6; dx2++)
                        if (dx2 * dx2 + dy2 * dy2 <= 36)
                            _g.Scope.Draw(_g._pixel, new Rectangle(cx + dx2, cy + dy2, 1, 1), new Color(160, 60, 200, 200));
                break;
            case "Beam":
                _g.Scope.Draw(_g._pixel, new Rectangle(cx - 8, cy - 1, 16, 3), new Color(60, 120, 255, 220));
                _g.Scope.Draw(_g._pixel, new Rectangle(cx - 6, cy - 2, 12, 1), new Color(100, 160, 255, 150));
                _g.Scope.Draw(_g._pixel, new Rectangle(cx - 6, cy + 2, 12, 1), new Color(100, 160, 255, 150));
                break;
            case "Drain":
                _g.Scope.Draw(_g._pixel, new Rectangle(cx - 8, cy - 1, 16, 3), new Color(220, 40, 40, 220));
                _g.Scope.Draw(_g._pixel, new Rectangle(cx - 6, cy - 2, 12, 1), new Color(255, 80, 80, 150));
                _g.Scope.Draw(_g._pixel, new Rectangle(cx - 6, cy + 2, 12, 1), new Color(255, 80, 80, 150));
                break;
            case "Cloud":
                // Poison cloud icon: hazy green circle
                for (int dy2 = -7; dy2 <= 7; dy2++)
                    for (int dx2 = -7; dx2 <= 7; dx2++)
                    {
                        int dsq = dx2 * dx2 + dy2 * dy2;
                        if (dsq <= 49)
                        {
                            int alpha = dsq < 16 ? 200 : (dsq < 36 ? 140 : 80);
                            _g.Scope.Draw(_g._pixel, new Rectangle(cx + dx2, cy + dy2, 1, 1),
                                new Color(80, 200, 60, alpha));
                        }
                    }
                break;
            default:
                _g.Scope.Draw(_g._pixel, new Rectangle(cx - 6, cy - 6, 12, 12), new Color(180, 180, 180, 150));
                break;
        }
    }
}
