using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Necroking.Core;
using Necroking.Data.Registries;
using Necroking.Lib;
using Necroking.Movement;

namespace Necroking.Render;

/// <summary>
/// Weapon-attached particle for buff weapon particle effects.
/// </summary>
public struct WPParticle
{
    public Vec2 Pos;       // world 2D
    public float Height;   // world height
    public float Age;      // seconds since spawn
    public bool Behind;    // render behind or in front of sprite
}

/// <summary>
/// Runtime weapon attachment data computed at render time.
/// </summary>
public struct WeaponAttachRuntime
{
    public Vec2 HiltWorld;
    public Vec2 TipWorld;
    public float HiltHeight;
    public float TipHeight;
    public bool HiltBehind;
    public bool TipBehind;
    /// <summary>Unit is facing NE/N/NW (away from the camera). The casting
    /// hand is usually obscured behind the body on those rows, so hand-anchored
    /// particles (attached flames, weapon-particle spawns) force Behind — an
    /// effect floating over the middle of the unit's back reads wrong.</summary>
    public bool FacingAway;
    public bool Valid;
}

/// <summary>
/// Per-buff weapon particle emitter state for one unit.
/// </summary>
public class WPEmitterState
{
    public List<WPParticle> Particles = new();
    public float SpawnAccum;
}

/// <summary>
/// Renders buff visual effects on units: orbitals, ground auras, upright effects,
/// image behind, lightning aura, weapon particles, pulsing outline.
/// Ported from C++ BuffVisualSystem.
/// </summary>
public class BuffVisualSystem
{
    private static readonly Random _rand = new();

    // Per-unit merged orbital ring state
    private struct MergedOrbRing
    {
        public struct Orb { public string BuffDefID; }
        public List<Orb> Orbs;
        public float BaseAngle;
        public float MoonBaseAngle;
        public float AvgSunRadius;
        public float AvgMoonRadius;
        public float AvgSunSpeed;
        public float AvgMoonSpeed;
    }

    // All per-unit state is keyed by Unit.Id, NOT sim index: UnitArrays.RemoveUnit
    // swap-and-pops, so index-keyed stores handed a dead caster's arcs/particles/
    // orbital phase to whichever unit got swapped into the slot. Entries are
    // removed when the unit loses the relevant buff, and PruneDead (end of
    // Update) drops entries whose unit died.
    private readonly Dictionary<uint, MergedOrbRing> _mergedRings = new();

    // Per-unit weapon particle emitters
    private readonly Dictionary<uint, Dictionary<string, WPEmitterState>> _wpEmitters = new();

    // Per-unit lightning arc state
    private readonly Dictionary<uint, List<Vector2[]>> _lightningArcs = new();
    private readonly Dictionary<uint, float> _lightningTimers = new();

    private readonly List<uint> _pruneScratch = new();

    public bool HasEmitters(uint unitId) =>
        _wpEmitters.TryGetValue(unitId, out var em) && em.Count > 0;

    public void Clear()
    {
        _mergedRings.Clear();
        _wpEmitters.Clear();
        _lightningArcs.Clear();
        _lightningTimers.Clear();
    }

    /// <summary>
    /// Update orbital angles, weapon particle emitters, and lightning arcs.
    /// Call once per frame before drawing.
    /// </summary>
    public void Update(float dt, UnitArrays units, BuffRegistry buffReg, float globalTime)
    {
        for (int i = 0; i < units.Count; i++)
        {
            if (!units[i].Alive) continue;

            uint uid = units[i].Id;
            var activeBuffs = units[i].ActiveBuffs;

            // Build merged orbital ring
            if (!_mergedRings.TryGetValue(uid, out var ring) || ring.Orbs == null)
                ring = new MergedOrbRing { Orbs = new List<MergedOrbRing.Orb>() };
            ring.Orbs.Clear();
            float totalSunR = 0, totalSunSpd = 0, totalMoonR = 0, totalMoonSpd = 0;
            int numOrbBuffs = 0;

            foreach (var ab in activeBuffs)
            {
                var def = buffReg.Get(ab.BuffDefID);
                if (def == null || !def.HasOrbital || def.Orbital == null) continue;

                int count = def.Orbital.OrbCountMatchesStacks ? ab.StackCount : def.Orbital.OrbCount;
                count = Math.Max(0, count);
                for (int o = 0; o < count; o++)
                    ring.Orbs.Add(new MergedOrbRing.Orb { BuffDefID = ab.BuffDefID });

                totalSunR += def.Orbital.SunOrbitRadius;
                totalSunSpd += def.Orbital.SunOrbitSpeed;
                totalMoonR += def.Orbital.MoonOrbitRadius;
                totalMoonSpd += def.Orbital.MoonOrbitSpeed;
                numOrbBuffs++;
            }

            if (numOrbBuffs > 0)
            {
                ring.AvgSunRadius = totalSunR / numOrbBuffs;
                ring.AvgSunSpeed = totalSunSpd / numOrbBuffs;
                ring.AvgMoonRadius = totalMoonR / numOrbBuffs;
                ring.AvgMoonSpeed = totalMoonSpd / numOrbBuffs;
                ring.BaseAngle += ring.AvgSunSpeed * dt;
                ring.MoonBaseAngle += ring.AvgMoonSpeed * dt;
                _mergedRings[uid] = ring;
            }
            else
            {
                // No orbital buffs → no ring entry (dropping it resets the orbit
                // phase, which is fine — a re-applied buff starts a fresh ring).
                _mergedRings.Remove(uid);
            }

            // Update lightning arcs
            bool hasLightning = false;
            foreach (var ab in activeBuffs)
            {
                var def = buffReg.Get(ab.BuffDefID);
                if (def == null || !def.HasLightningAura || def.LightningAura == null) continue;
                var la = def.LightningAura;
                if (la.JitterHz <= 0) continue;

                hasLightning = true;
                if (!_lightningTimers.ContainsKey(uid)) _lightningTimers[uid] = 0;
                _lightningTimers[uid] += dt;
                float interval = 1f / la.JitterHz;
                if (_lightningTimers[uid] >= interval)
                {
                    _lightningTimers[uid] -= interval;
                    RegenerateLightningArcs(uid, la);
                }
            }
            if (!hasLightning)
            {
                _lightningTimers.Remove(uid);
                _lightningArcs.Remove(uid);
            }
        }

        // Drop state whose unit died this frame — id-keyed stores only shrink here
        // (the per-unit loops above never see dead units again).
        PruneKeys(_mergedRings, units);
        PruneKeys(_wpEmitters, units);
        PruneKeys(_lightningArcs, units);
        PruneKeys(_lightningTimers, units);
    }

    private void PruneKeys<T>(Dictionary<uint, T> store, UnitArrays units)
    {
        if (store.Count == 0) return;
        _pruneScratch.Clear();
        foreach (var kv in store)
            if (UnitUtil.ResolveUnitIndex(units, kv.Key) < 0)
                _pruneScratch.Add(kv.Key);
        foreach (uint id in _pruneScratch) store.Remove(id);
    }

    /// <summary>
    /// Update weapon particle emitters for a unit. Call during the draw pass (phase 0).
    /// </summary>
    public void UpdateWeaponParticles(uint unitId, float dt, float globalTime,
        List<BuffDef> wpDefs, WeaponAttachRuntime weaponAttach, BuffRegistry buffReg)
    {
        if (!_wpEmitters.TryGetValue(unitId, out var emitters))
        {
            emitters = new Dictionary<string, WPEmitterState>();
            _wpEmitters[unitId] = emitters;
        }
        float clampedDt = Math.Clamp(dt, 0f, 0.1f);

        // Update existing emitters (age particles, remove dead)
        var toRemove = new List<string>();
        foreach (var (buffId, state) in emitters)
        {
            var def = buffReg.Get(buffId);
            if (def == null || !def.HasWeaponParticle || def.WeaponParticle == null)
            {
                toRemove.Add(buffId);
                continue;
            }
            var vis = def.WeaponParticle;

            if (vis.AttachedFlame)
            {
                // Persistent flame: positioned in the spawn loop below (needs the
                // current attach). Here only handle the buff expiring — the flame
                // vanishes with the buff instead of fading out world-space litter.
                bool flameActive = false;
                foreach (var d in wpDefs)
                    if (d.Id == buffId) { flameActive = true; break; }
                if (!flameActive)
                {
                    state.Particles.Clear();
                    toRemove.Add(buffId);
                }
                continue;
            }

            // Normalize move direction
            float dirLen = MathF.Sqrt(vis.MoveDirX * vis.MoveDirX + vis.MoveDirY * vis.MoveDirY + vis.MoveDirZ * vis.MoveDirZ);
            float ndx = 0, ndy = 0, ndz = 0;
            if (dirLen > 0.001f)
            {
                ndx = vis.MoveDirX / dirLen;
                ndy = vis.MoveDirY / dirLen;
                ndz = vis.MoveDirZ / dirLen;
            }

            // Age and move particles
            for (int p = state.Particles.Count - 1; p >= 0; p--)
            {
                var particle = state.Particles[p];
                particle.Age += clampedDt;
                if (particle.Age >= vis.ParticleLifetime)
                {
                    state.Particles.RemoveAt(p);
                    continue;
                }
                particle.Pos.X += ndx * vis.MoveSpeed * clampedDt;
                particle.Pos.Y += ndy * vis.MoveSpeed * clampedDt;
                particle.Height += ndz * vis.MoveSpeed * clampedDt;
                state.Particles[p] = particle;
            }

            // Remove emitter if empty and buff no longer active
            if (state.Particles.Count == 0)
            {
                bool stillActive = false;
                foreach (var d in wpDefs)
                    if (d.Id == buffId) { stillActive = true; break; }
                if (!stillActive) toRemove.Add(buffId);
            }
        }
        foreach (var id in toRemove)
            emitters.Remove(id);

        // Spawn new particles
        foreach (var def in wpDefs)
        {
            if (def.WeaponParticle == null) continue;
            var vis = def.WeaponParticle;
            if (!emitters.TryGetValue(def.Id, out var state))
            {
                state = new WPEmitterState();
                emitters[def.Id] = state;
            }

            if (vis.AttachedFlame)
            {
                // Single looping flame re-anchored to the weapon attach every
                // frame — reads as fire held in the hand, never a world trail.
                // Age only drives the flipbook frame loop (draw ignores lifetime).
                if (!weaponAttach.Valid) { state.Particles.Clear(); continue; }
                float ft = vis.RangeMax;
                var flame = state.Particles.Count > 0 ? state.Particles[0] : new WPParticle();
                flame.Age += clampedDt;
                flame.Pos = new Vec2(
                    weaponAttach.HiltWorld.X + (weaponAttach.TipWorld.X - weaponAttach.HiltWorld.X) * ft,
                    weaponAttach.HiltWorld.Y + (weaponAttach.TipWorld.Y - weaponAttach.HiltWorld.Y) * ft);
                flame.Height = weaponAttach.HiltHeight + (weaponAttach.TipHeight - weaponAttach.HiltHeight) * ft;
                flame.Behind = weaponAttach.FacingAway
                    || (ft < 0.5f ? weaponAttach.HiltBehind : weaponAttach.TipBehind);
                if (state.Particles.Count == 0) state.Particles.Add(flame);
                else state.Particles[0] = flame;
                if (state.Particles.Count > 1) // leftovers from a live mode switch in the editor
                    state.Particles.RemoveRange(1, state.Particles.Count - 1);
                continue;
            }

            if (weaponAttach.Valid && vis.SpawnRate > 0f)
            {
                if (state.Particles.Count == 0)
                    state.SpawnAccum = 1f; // seed so first particle spawns immediately

                state.SpawnAccum += vis.SpawnRate * clampedDt;
                int toSpawn = (int)state.SpawnAccum;
                state.SpawnAccum -= toSpawn;

                for (int s = 0; s < toSpawn && state.Particles.Count < 200; s++)
                {
                    float t = vis.RangeMin + (float)_rand.NextDouble() * (vis.RangeMax - vis.RangeMin);
                    var p = new WPParticle
                    {
                        Pos = new Vec2(
                            weaponAttach.HiltWorld.X + (weaponAttach.TipWorld.X - weaponAttach.HiltWorld.X) * t,
                            weaponAttach.HiltWorld.Y + (weaponAttach.TipWorld.Y - weaponAttach.HiltWorld.Y) * t),
                        Height = weaponAttach.HiltHeight + (weaponAttach.TipHeight - weaponAttach.HiltHeight) * t,
                        Age = 0f,
                        Behind = weaponAttach.FacingAway
                            || (t < 0.5f ? weaponAttach.HiltBehind : weaponAttach.TipBehind)
                    };
                    state.Particles.Add(p);
                }
            }
        }
    }

    /// <summary>
    /// Draw all buff visuals for a unit.
    /// phase 0 = behind sprite, phase 1 = in front.
    /// </summary>
    public void DrawUnit(int unitIdx, Vec2 unitPos, int phase, float globalTime,
        SpriteBatch spriteBatch, Camera25D camera, Renderer renderer,
        Dictionary<string, Flipbook> flipbooks, BuffRegistry buffReg,
        UnitArrays units,
        // For image behind / pulsing outline:
        SpriteAtlas? atlas = null, SpriteFrame? frame = null, float spriteScale = 1f, bool flipX = false,
        // For upright effect:
        Vec2 effectSpawnPos = default, float effectSpawnHeight = 0f)
    {
        if (unitIdx < 0 || unitIdx >= units.Count) return;
        if (!units[unitIdx].Alive) return;

        uint uid = units[unitIdx].Id;
        var activeBuffs = units[unitIdx].ActiveBuffs;
        if (activeBuffs.Count == 0) return;

        // Collect buff defs by visual type
        List<BuffDef>? groundAuraDefs = null, behindEffectDefs = null, frontEffectDefs = null;
        List<BuffDef>? lightningAuraDefs = null, imageBehindDefs = null;

        foreach (var ab in activeBuffs)
        {
            var def = buffReg.Get(ab.BuffDefID);
            if (def == null) continue;
            if (def.HasGroundAura && def.GroundAura != null) (groundAuraDefs ??= new()).Add(def);
            if (def.HasBehindEffect && def.BehindEffect != null) (behindEffectDefs ??= new()).Add(def);
            if (def.HasFrontEffect && def.FrontEffect != null) (frontEffectDefs ??= new()).Add(def);
            if (def.HasLightningAura && def.LightningAura != null) (lightningAuraDefs ??= new()).Add(def);
            if (def.HasImageBehind && def.ImageBehind != null) (imageBehindDefs ??= new()).Add(def);
        }

        int cycle = (int)(globalTime / 2f);

        if (phase == 0)
        {
            // --- Phase 0: Behind sprite ---

            // Image Behind (exclusive)
            if (imageBehindDefs != null && atlas != null && frame != null)
            {
                var chosen = imageBehindDefs[cycle % imageBehindDefs.Count];
                DrawImageBehind(chosen.ImageBehind!, unitPos, spriteBatch, camera, renderer,
                    atlas, frame.Value, spriteScale, flipX, globalTime);
            }

            // Ground Aura (exclusive)
            if (groundAuraDefs != null)
            {
                var chosen = groundAuraDefs[cycle % groundAuraDefs.Count];
                DrawGroundAura(chosen.GroundAura!, unitPos, spriteBatch, camera, renderer,
                    flipbooks, globalTime);
            }

            // Behind Effect (exclusive)
            if (behindEffectDefs != null)
            {
                var chosen = behindEffectDefs[cycle % behindEffectDefs.Count];
                DrawUprightEffect(chosen.BehindEffect!, unitPos, spriteBatch, camera, renderer,
                    flipbooks, globalTime, effectSpawnPos, effectSpawnHeight);
            }

            // Orbitals behind
            DrawMergedOrbs(uid, unitPos, 0, spriteBatch, camera, renderer,
                flipbooks, buffReg, globalTime);

            // Weapon particles behind (attached flames draw via their own
            // HdrAdditive queue items — see DrawAttachedFlames — not here)
            if (_wpEmitters.TryGetValue(uid, out var emittersBehind))
            {
                foreach (var (buffId, state) in emittersBehind)
                {
                    if (state.Particles.Count == 0) continue;
                    var def = buffReg.Get(buffId);
                    if (def?.WeaponParticle == null || def.WeaponParticle.AttachedFlame) continue;
                    DrawWeaponParticles(def.WeaponParticle, state.Particles, true,
                        spriteBatch, camera, renderer, flipbooks);
                }
            }
        }
        else
        {
            // --- Phase 1: In front of sprite ---

            // Orbitals front
            DrawMergedOrbs(uid, unitPos, 1, spriteBatch, camera, renderer,
                flipbooks, buffReg, globalTime);

            // Front Effect (exclusive)
            if (frontEffectDefs != null)
            {
                var chosen = frontEffectDefs[cycle % frontEffectDefs.Count];
                DrawUprightEffect(chosen.FrontEffect!, unitPos, spriteBatch, camera, renderer,
                    flipbooks, globalTime, effectSpawnPos, effectSpawnHeight);
            }

            // Lightning Aura (exclusive)
            if (lightningAuraDefs != null)
            {
                var chosen = lightningAuraDefs[cycle % lightningAuraDefs.Count];
                DrawLightningAura(chosen.LightningAura!, uid, unitPos, spriteBatch,
                    camera, renderer, globalTime);
            }

            // Weapon particles front (attached flames excluded — HdrAdditive items)
            if (_wpEmitters.TryGetValue(uid, out var emittersFront))
            {
                foreach (var (buffId, state) in emittersFront)
                {
                    if (state.Particles.Count == 0) continue;
                    var def = buffReg.Get(buffId);
                    if (def?.WeaponParticle == null || def.WeaponParticle.AttachedFlame) continue;
                    DrawWeaponParticles(def.WeaponParticle, state.Particles, false,
                        spriteBatch, camera, renderer, flipbooks);
                }
            }
        }
    }

    // ==========================================
    // Ground Aura
    // ==========================================
    private void DrawGroundAura(GroundAuraVisual ga, Vec2 unitPos,
        SpriteBatch batch, Camera25D cam, Renderer renderer,
        Dictionary<string, Flipbook> flipbooks, float globalTime)
    {
        if (!flipbooks.TryGetValue(ga.FlipbookID, out var fb) || !fb.IsLoaded || fb.Texture == null) return;

        float pulse = 1f;
        if (ga.PulseSpeed > 0f)
            pulse = 1f + ga.PulseAmount * MathF.Sin(globalTime * ga.PulseSpeed * 2f * MathF.PI);

        float pixelSize = ga.Scale * cam.Zoom * pulse;
        var sp = renderer.WorldToScreen(unitPos, 0f, cam);

        int frameIdx = fb.GetFrameAtTime(globalTime);
        var src = fb.GetFrameRect(frameIdx);

        float destW = pixelSize;
        float destH = pixelSize * cam.YRatio;
        var origin = new Vector2(src.Width * 0.5f, src.Height * 0.5f);
        float scaleX = destW / src.Width;
        float scaleY = destH / src.Height;

        var color = EncodeColor(ga.Color, 1f, ga.BlendMode);
        batch.Draw(fb.Texture, sp, src, color, 0f, origin,
            new Vector2(scaleX, scaleY), SpriteEffects.None, 0f);
    }

    // ==========================================
    // Orbital Orbs (merged ring)
    // ==========================================
    private void DrawMergedOrbs(uint unitId, Vec2 unitPos, int phase,
        SpriteBatch batch, Camera25D cam, Renderer renderer,
        Dictionary<string, Flipbook> flipbooks, BuffRegistry buffReg, float globalTime)
    {
        if (!_mergedRings.TryGetValue(unitId, out var ring)) return;
        if (ring.Orbs == null || ring.Orbs.Count == 0) return;
        int totalOrbs = ring.Orbs.Count;

        for (int o = 0; o < totalOrbs; o++)
        {
            var srcDef = buffReg.Get(ring.Orbs[o].BuffDefID);
            if (srcDef?.Orbital == null) continue;
            var vis = srcDef.Orbital;

            if (!flipbooks.TryGetValue(vis.FlipbookID, out var fb) || !fb.IsLoaded || fb.Texture == null) continue;

            float planetAngle = ring.BaseAngle + (2f * MathF.PI * o) / totalOrbs;
            float moonAngle = ring.MoonBaseAngle + (2f * MathF.PI * o) / totalOrbs * 0.7f;

            var orbWorldPos = new Vec2(
                unitPos.X + ring.AvgSunRadius * MathF.Cos(planetAngle) + ring.AvgMoonRadius * MathF.Cos(moonAngle),
                unitPos.Y + ring.AvgSunRadius * MathF.Sin(planetAngle) + ring.AvgMoonRadius * MathF.Sin(moonAngle));

            // Depth: an orb south of the unit (larger world Y) draws in front;
            // north of it (smaller Y) draws behind (phase 0).
            bool isFront = orbWorldPos.Y >= unitPos.Y;
            if ((phase == 0 && isFront) || (phase == 1 && !isFront)) continue;

            var sp = renderer.WorldToScreen(orbWorldPos, 0f, cam);
            int frameIdx = fb.GetFrameAtTime(globalTime);
            var src = fb.GetFrameRect(frameIdx);

            float pixelSize = vis.OrbScale * cam.Zoom;
            float destW = pixelSize;
            float destH = pixelSize * cam.YRatio;
            var origin = new Vector2(src.Width * 0.5f, src.Height * 0.5f);

            var color = EncodeColor(vis.OrbColor, 1f, 1); // orbitals always additive
            batch.Draw(fb.Texture, sp, src, color, 0f, origin,
                new Vector2(destW / src.Width, destH / src.Height), SpriteEffects.None, 0f);
        }
    }

    // ==========================================
    // Upright Effect (behind or front)
    // ==========================================
    private void DrawUprightEffect(UprightEffectVisual ue, Vec2 unitPos,
        SpriteBatch batch, Camera25D cam, Renderer renderer,
        Dictionary<string, Flipbook> flipbooks, float globalTime,
        Vec2 effectSpawnPos, float effectSpawnHeight)
    {
        if (!flipbooks.TryGetValue(ue.FlipbookID, out var fb) || !fb.IsLoaded || fb.Texture == null) return;

        float pixelSize = ue.Scale * cam.Zoom;
        // EffectSpawnPos2D is recomputed for every alive unit each frame, so it's always
        // valid when PinToEffectSpawn is set — don't gate on a magic (0,0) "unset" check,
        // which would mis-treat a unit legitimately at the world origin as unset.
        var drawPos = ue.PinToEffectSpawn ? effectSpawnPos : unitPos;
        float drawHeight = ue.PinToEffectSpawn ? effectSpawnHeight : 0f;
        var sp = renderer.WorldToScreen(drawPos, drawHeight, cam);
        sp.Y += ue.YOffset * cam.Zoom;

        int frameIdx = fb.GetFrameAtTime(globalTime);
        var src = fb.GetFrameRect(frameIdx);

        // Upright: no yRatio squash, bottom-center anchor
        var origin = new Vector2(src.Width * 0.5f, src.Height);
        float scale = pixelSize / src.Width;

        var color = EncodeColor(ue.Color, 1f, ue.BlendMode);
        batch.Draw(fb.Texture, sp, src, color, 0f, origin,
            new Vector2(scale, scale), SpriteEffects.None, 0f);
    }

    // ==========================================
    // Image Behind (scaled sprite silhouette)
    // ==========================================
    private void DrawImageBehind(ImageBehindVisual ib, Vec2 unitPos,
        SpriteBatch batch, Camera25D cam, Renderer renderer,
        SpriteAtlas atlas, SpriteFrame frame, float spriteScale, bool flipX, float globalTime)
    {
        var tex = atlas.GetTextureForFrame(frame);
        if (tex == null) return;

        float pulse = ib.Scale + ib.PulseAmount * MathF.Sin(globalTime * ib.PulseSpeed * 2f * MathF.PI);
        var sp = renderer.WorldToScreen(unitPos, 0f, cam);

        var src = frame.Rect;
        float baseDestW = src.Width * spriteScale;
        float baseDestH = src.Height * spriteScale;
        float destW = baseDestW * pulse;
        float destH = baseDestH * pulse;

        float anchorX = flipX ? (1f - frame.PivotX) : frame.PivotX;
        float anchorY = 1f - frame.PivotY;

        var dest = new Rectangle(
            (int)(sp.X - destW * anchorX),
            (int)(sp.Y - destH * anchorY),
            (int)destW, (int)destH);

        var effects = flipX ? SpriteEffects.FlipHorizontally : SpriteEffects.None;
        var color = EncodeColor(ib.Color, 1f, ib.BlendMode);
        batch.Draw(tex, dest, src, color, 0f, Vector2.Zero, effects, 0f);
    }

    // ==========================================
    // Lightning Aura
    // ==========================================
    private void DrawLightningAura(LightningAuraVisual la, uint unitId, Vec2 unitPos,
        SpriteBatch batch, Camera25D cam, Renderer renderer, float globalTime)
    {
        if (!_lightningArcs.TryGetValue(unitId, out var arcs)) return;

        var sp = renderer.WorldToScreen(unitPos, 0f, cam);
        float radiusPixels = la.ArcRadius * cam.Zoom;

        float flicker = 1f;
        if (la.FlickerHz > 0)
            flicker = MathF.Sin(globalTime * la.FlickerHz * 2f * MathF.PI) > 0 ? 1f : 0.3f;

        var coreColor = EncodeColor(la.CoreColor, flicker, 1);
        var glowColor = EncodeColor(la.GlowColor, 0.5f * flicker, 1);

        // Arc widths are px authored at zoom 32 — scale with zoom like the arc
        // radius does (one policy per effect; DrawThickLine's 1px floor keeps
        // MinZoom legibility). Round-2 sweep: widths were constant px.
        float arcWidthScale = System.Math.Clamp(cam.Zoom / 32f, 0f, 4f);
        float arcGlowW = la.GlowWidth * arcWidthScale;
        float arcCoreW = la.CoreWidth * arcWidthScale;
        foreach (var points in arcs)
        {
            if (points.Length < 2) continue;
            for (int i = 0; i < points.Length - 1; i++)
            {
                var p1 = sp + points[i] * radiusPixels;
                var p2 = sp + points[i + 1] * radiusPixels;
                DrawThickLine(batch, p1, p2, arcGlowW, glowColor);
                DrawThickLine(batch, p1, p2, arcCoreW, coreColor);
            }
        }
    }

    // ==========================================
    // Weapon Particles
    // ==========================================
    private void DrawWeaponParticles(WeaponParticleVisual vis, List<WPParticle> particles,
        bool drawBehind, SpriteBatch batch, Camera25D cam, Renderer renderer,
        Dictionary<string, Flipbook> flipbooks)
    {
        if (!flipbooks.TryGetValue(vis.FlipbookID, out var fb) || !fb.IsLoaded || fb.Texture == null
            || fb.TotalFrames <= 0) return; // TotalFrames guard: a 0-frame def would div-by-zero below

        var baseTint = vis.Color.ToColor();

        foreach (var p in particles)
        {
            if (p.Behind != drawBehind) continue;

            float lifeFrac = Math.Clamp(p.Age / vis.ParticleLifetime, 0f, 1f);
            float alpha = 1f - lifeFrac;

            // Height must use the sprite's vertical convention (height × Zoom,
            // NOT foreshortened by YRatio). The weapon-attach points these
            // particles spawn from are authored against the sprite rig, whose
            // body height renders at worldH × Zoom (see DrawSingleUnit). Using
            // WorldToScreen here would foreshorten the height by YRatio (0.5),
            // dropping the casting glow to ~half height — well below the hand.
            // Matches the carried-body-bag / weapon-attach debug overlay.
            var sp = renderer.WorldToScreenPx(p.Pos, p.Height * cam.Zoom, cam);

            float effectiveFps = vis.FPS > 0f ? vis.FPS : fb.FPS;
            int frameIdx = ((int)(p.Age * effectiveFps)) % fb.TotalFrames;
            if (frameIdx < 0) frameIdx = 0;
            var src = fb.GetFrameRect(frameIdx);

            float pixelSize = vis.ParticleScale * cam.Zoom;
            var origin = new Vector2(src.Width * 0.5f, src.Height * 0.5f);
            float scale = pixelSize / src.Width;

            var color = EncodeColor(vis.Color, alpha, vis.BlendMode);
            batch.Draw(fb.Texture, sp, src, color, 0f, origin,
                new Vector2(scale, scale), SpriteEffects.None, 0f);
        }
    }

    /// <summary>
    /// Draw a unit's attached-flame weapon particles (the persistent hand fire).
    /// Runs inside its own YSort queue item carrying Materials.HdrAdditive, so
    /// colors use the HdrSprite.fx sqrt-alpha encode — real intensity ceiling 16,
    /// feeds bloom — instead of the Scene batch's LDR min(I,4) clamp. Drawn via
    /// the scope (HDR materials register premultiplyTint:false, so the vertex
    /// encoding passes through untouched).
    /// </summary>
    public void DrawAttachedFlames(uint unitId, bool behind, SpriteScope scope,
        Camera25D cam, Renderer renderer, Dictionary<string, Flipbook> flipbooks,
        BuffRegistry buffReg, List<Movement.ActiveBuff>? activeBuffs = null)
    {
        if (!_wpEmitters.TryGetValue(unitId, out var emitters)) return;

        foreach (var (buffId, state) in emitters)
        {
            var vis = buffReg.Get(buffId)?.WeaponParticle;
            if (vis == null || !vis.AttachedFlame || state.Particles.Count == 0) continue;

            // Fade window: a non-permanent buff inside its FadeDuration draws
            // at RemainingDuration/FadeDuration alpha (channel-end fade-out).
            float fade = BuffFadeAlpha(activeBuffs, buffId);
            if (fade <= 0.003f) continue;

            var p = state.Particles[0];
            if (p.Behind != behind) continue;

            if (!flipbooks.TryGetValue(vis.FlipbookID, out var fb) || !fb.IsLoaded
                || fb.Texture == null || fb.TotalFrames <= 0) continue;

            // Same sprite-rig height convention as DrawWeaponParticles: h × Zoom,
            // no YRatio foreshortening (see comment there).
            var sp = renderer.WorldToScreenPx(p.Pos, p.Height * cam.Zoom, cam);

            float effectiveFps = vis.FPS > 0f ? vis.FPS : fb.FPS;
            int frameIdx = ((int)(p.Age * effectiveFps)) % fb.TotalFrames;
            if (frameIdx < 0) frameIdx = 0;
            var src = fb.GetFrameRect(frameIdx);

            float pixelSize = vis.ParticleScale * cam.Zoom;
            var origin = new Vector2(src.Width * 0.5f, src.Height * 0.5f);
            float scale = pixelSize / src.Width;

            // Additive mode ignored the color's A byte in the LDR path; keep that
            // (A=255) so existing data reads the same, just brighter. The fade
            // multiplier is 1 outside a fade window.
            var color = HdrColor.ToHdrVertex(
                new Color(vis.Color.R, vis.Color.G, vis.Color.B), fade, vis.Color.Intensity);
            // HDR (EXR) sheets need the LinearTexture material variant
            bool hdrTex = fb.IsHdr && Materials.HdrTexAdditive != null;
            if (hdrTex) scope.PushMaterial(Materials.HdrTexAdditive!);
            scope.Draw(fb.Texture, sp, src, color, 0f, origin, scale, SpriteEffects.None, 0f);
            if (hdrTex) scope.PopMaterial();
        }
    }

    /// <summary>Fade multiplier for a buff's visuals: 1 while permanent or
    /// outside the fade window, RemainingDuration/FadeDuration inside it,
    /// 1 when the buff (or the list) is missing — spawn-mode particles keep
    /// draining on their own per-particle lifetimes after the buff dies.</summary>
    public static float BuffFadeAlpha(List<Movement.ActiveBuff>? buffs, string buffId)
    {
        if (buffs == null) return 1f;
        for (int i = 0; i < buffs.Count; i++)
        {
            if (buffs[i].BuffDefID != buffId) continue;
            var b = buffs[i];
            if (!b.Permanent && b.FadeDuration > 0.001f && b.RemainingDuration < b.FadeDuration)
                return Math.Clamp(b.RemainingDuration / b.FadeDuration, 0f, 1f);
            return 1f;
        }
        return 1f;
    }

    // ==========================================
    // Lightning arc generation
    // ==========================================
    private void RegenerateLightningArcs(uint unitId, LightningAuraVisual la)
    {
        if (!_lightningArcs.TryGetValue(unitId, out var arcs))
        {
            arcs = new List<Vector2[]>();
            _lightningArcs[unitId] = arcs;
        }
        arcs.Clear();

        for (int a = 0; a < la.ArcCount; a++)
        {
            float startAngle = (float)(_rand.NextDouble() * 2 * MathF.PI);
            int segments = 4 + _rand.Next(4);
            var points = new Vector2[segments];
            float curAngle = startAngle;
            for (int i = 0; i < segments; i++)
            {
                float t = i / (float)(segments - 1);
                curAngle += (float)(_rand.NextDouble() - 0.5) * 1.2f;
                points[i] = new Vector2(MathF.Cos(curAngle) * t, MathF.Sin(curAngle) * t * 0.5f); // yRatio
            }
            arcs.Add(points);
        }
    }

    // ==========================================
    // Color encoding helpers
    // ==========================================

    /// <summary>
    /// Encode color for rendering inside a premultiplied AlphaBlend spritebatch.
    /// NATIVE-ENCODING ISLAND: blendMode is per-instance data, so these colors
    /// are drawn through the RAW batch (never SpriteScope.Draw — the scope's
    /// straight-alpha conversion would zero the A=0 additive trick).
    /// For additive (blendMode=1): alpha=0 makes premultiplied blend act as additive.
    /// For alpha (blendMode=0): standard premultiplied alpha color.
    /// </summary>
    private static Color EncodeColor(HdrColor hdr, float alpha, int blendMode)
    {
        if (blendMode == 1)
        {
            // Additive via premultiplied alpha trick: set A=0, RGB carries glow color
            float scale = Math.Min(hdr.Intensity, 4f);
            return new Color(
                (byte)Math.Min(255f, hdr.R * scale * alpha),
                (byte)Math.Min(255f, hdr.G * scale * alpha),
                (byte)Math.Min(255f, hdr.B * scale * alpha),
                (byte)0);
        }
        else
        {
            // Premultiply RGB by the effective alpha (the old straight-alpha
            // return washed the hue out in the premult batch).
            float a = (hdr.A / 255f) * Math.Clamp(alpha, 0f, 1f);
            return new Color(
                (byte)(hdr.R * a), (byte)(hdr.G * a), (byte)(hdr.B * a),
                (byte)(255f * a));
        }
    }

    // ==========================================
    // Drawing primitives
    // ==========================================
    private Texture2D? _pixel;

    public void SetPixel(Texture2D pixel) => _pixel = pixel;

    private void DrawThickLine(SpriteBatch batch, Vector2 a, Vector2 b, float thickness, Color color)
    {
        if (_pixel == null) return;
        var diff = b - a;
        float length = diff.Length();
        if (length < 0.5f) return;
        float angle = MathF.Atan2(diff.Y, diff.X);
        batch.Draw(_pixel,
            new Rectangle((int)a.X, (int)a.Y, (int)length, Math.Max(1, (int)thickness)),
            null, color, angle, new Vector2(0, 0.5f), SpriteEffects.None, 0f);
    }
}
