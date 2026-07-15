using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Audio;
using Microsoft.Xna.Framework.Graphics;
using Necroking.Core;
using Necroking.GameSystems;
using Necroking.Lib;
using Necroking.Render;
using Necroking.World;

namespace Necroking.Game;

/// <summary>
/// Owns the player's foragable collection mechanic: right-click or auto-pickup
/// pulls a nearby foragable env object into an arc-flight animation that drops
/// the resource into inventory on landing. Includes the auto-pickup cadence.
///
/// Extracted from Game1 (2026-05-13) as the first proof-of-concept Game1
/// subsystem split. Game1 holds a single instance and forwards trigger /
/// update / draw calls. The system caches long-lived references (env, sim,
/// camera, renderer, inventory, effects) up front; per-call dependencies
/// (skill-book auto-learn, damage numbers list) come in via callbacks.
/// </summary>
public class ForagableSystem
{
    private struct CollectingForagable
    {
        public int ObjIdx;             // source env object — used to restore it if inventory overflows on landing
        public Vec2 StartPos;          // world position where object was
        public Vec2 TargetPos;         // necromancer position at time of collection
        public float Timer;            // 0..ArcDuration
        public float ArcDuration;      // total flight time
        public string ResourceType;    // what to add to inventory on complete
        public Texture2D? Texture;     // cached texture for rendering
        public float BaseScale;        // original render scale
        public float PivotX, PivotY;   // texture pivot
    }

    public const float ArcDuration = 0.35f;
    public const float AutoPickupRange = 1.5f;

    private readonly List<CollectingForagable> _inFlight = new();
    private float _autoPickupCooldown;

    // Long-lived references — set once via Bind().
    // Live-read the Simulation AND EnvironmentSystem off Game1 instead of caching them:
    // BOTH follow the per-game GameSession (Game1._sim / Game1._envSystem are forwarding
    // properties), recreated on every map load, so a cached ref goes stale after the first
    // reload. Holding Game1 (a program-lifetime singleton) keeps this system on the live
    // session. The cached-_env variant of this bug shipped once: FindNearest scanned
    // session #0's disposed env → mushroom/log pickup silently dead while the (live-env)
    // renderer kept wiggling them.
    private Game1 _game = null!;
    private Simulation _sim => _game._sim;
    private EnvironmentSystem _env => _game._envSystem;
    private Render.Camera25D _camera = null!;
    private Render.Renderer _renderer = null!;
    private SpriteBatch _spriteBatch = null!;
    private Inventory _inventory = null!;
    private EffectManager _effects = null!;
    private SoundEffect? _pickupSound;

    // Per-pickup hooks (Game1 wires these — callbacks rather than direct refs
    // so the system doesn't reach into Game1-private state like the damage
    // numbers list or the skill book).
    private Action<Vec2, string>? _onPickup;       // (worldPos, resourceType) — Game1 spawns floating text
    private Action<string>? _onLearnTrigger;       // (resourceType) — Game1 runs skill-book triggers

    public void Bind(
        Game1 game,
        Render.Camera25D camera, Render.Renderer renderer, SpriteBatch spriteBatch,
        Inventory inventory, EffectManager effects, SoundEffect? pickupSound,
        Action<Vec2, string>? onPickup, Action<string>? onLearnTrigger)
    {
        _game = game;
        _camera = camera; _renderer = renderer; _spriteBatch = spriteBatch;
        _inventory = inventory; _effects = effects; _pickupSound = pickupSound;
        _onPickup = onPickup;
        _onLearnTrigger = onLearnTrigger;
    }

    /// <summary>Reset on new game / map reload.</summary>
    public void Clear() { _inFlight.Clear(); _autoPickupCooldown = 0f; }

    /// <summary>Nearest collectable foragable within <paramref name="maxDist"/>
    /// of <paramref name="fromPos"/>. Returns -1 if none.</summary>
    public int FindNearest(Vec2 fromPos, float maxDist)
        => _sim.Query.NearestEnvObject(fromPos, maxDist, new EnvForagables());

    /// <summary>Begin an arc-pickup animation on the env object at
    /// <paramref name="objIdx"/>. Marks the object collected immediately so
    /// it stops blocking new picks. No-op if the necromancer is missing or
    /// the object isn't a foragable.</summary>
    public void StartCollection(int objIdx)
    {
        if (_sim.NecromancerIndex < 0) return;
        // Refuse the pickup while the inventory is full — CollectForagable consumes
        // the world object, so collecting with nowhere to bank the item destroyed it.
        if (objIdx >= 0 && objIdx < _env.Objects.Count)
        {
            var fdef = _env.Defs[_env.Objects[objIdx].DefIndex];
            if (fdef.IsForagable && !_inventory.HasRoomFor(fdef.ForagableType)) return;
        }
        string? resourceType = _env.CollectForagable(objIdx);
        if (resourceType == null) return;

        var obj = _env.Objects[objIdx];
        var def = _env.Defs[obj.DefIndex];
        var tex = _env.GetDefTexture(obj.DefIndex);

        float worldH = def.SpriteWorldHeight * obj.Scale * def.Scale;
        float pixelH = worldH * _camera.Zoom;
        float baseScale = tex != null ? pixelH / tex.Height : 1f;

        _inFlight.Add(new CollectingForagable
        {
            ObjIdx = objIdx,
            StartPos = new Vec2(obj.X, obj.Y),
            TargetPos = _sim.Units[_sim.NecromancerIndex].Position,
            Timer = 0f,
            ArcDuration = ArcDuration,
            ResourceType = resourceType,
            Texture = tex,
            BaseScale = baseScale,
            PivotX = def.PivotX,
            PivotY = def.PivotY,
        });
    }

    /// <summary>Tick all in-flight arcs. On landing: add to inventory, fire
    /// learn-trigger hook, play sound, spawn dust puff, fire onPickup.</summary>
    public void Update(float dt)
    {
        for (int i = _inFlight.Count - 1; i >= 0; i--)
        {
            var cf = _inFlight[i];
            cf.Timer += dt;

            // Target tracks the necromancer in case they move during flight.
            if (_sim.NecromancerIndex >= 0)
                cf.TargetPos = _sim.Units[_sim.NecromancerIndex].Position;

            _inFlight[i] = cf;

            if (cf.Timer >= cf.ArcDuration)
            {
                int overflow = _inventory.AddItem(cf.ResourceType);
                if (overflow > 0)
                {
                    // Inventory filled up while the arc was in flight (StartCollection
                    // pre-checks room, so this is the failsafe): put the object back
                    // on the ground instead of destroying the resource.
                    _env.RestoreForagable(cf.ObjIdx);
                    _inFlight.RemoveAt(i);
                    continue;
                }
                _onLearnTrigger?.Invoke(cf.ResourceType);
                _effects.SpawnDustPuff(cf.TargetPos);
                _pickupSound?.Play(0.3f, 0f, 0f);
                _onPickup?.Invoke(cf.TargetPos, cf.ResourceType);
                _inFlight.RemoveAt(i);
            }
        }
    }

    /// <summary>Each frame the player is on the map and auto-pickup is enabled,
    /// pull the nearest in-range foragable. Cooldown-staggered so a tile of
    /// many objects gets picked over multiple frames.</summary>
    public void TickAutoPickup(float dt, bool enabled)
    {
        if (!enabled || _sim.NecromancerIndex < 0) return;
        _autoPickupCooldown -= dt;
        if (_autoPickupCooldown > 0f) return;
        int idx = FindNearest(_sim.Units[_sim.NecromancerIndex].Position, AutoPickupRange);
        if (idx >= 0)
        {
            StartCollection(idx);
            _autoPickupCooldown = 0.3f;
        }
    }

    /// <summary>Render all in-flight arcs. Sprites are flying world objects
    /// in screen-space, so this is a pass over the cached textures.</summary>
    public void Draw()
    {
        foreach (var cf in _inFlight)
        {
            if (cf.Texture == null) continue;
            float t = cf.Timer / cf.ArcDuration;

            Vec2 pos = cf.StartPos + (cf.TargetPos - cf.StartPos) * t;

            // Arc parabola peaking at t=0.3, max height ~2 world units.
            float arcHeight = 2f * (1f - (t - 0.3f) * (t - 0.3f) / 0.49f);
            if (arcHeight < 0f) arcHeight = 0f;

            float scale = cf.BaseScale * (1f - t * 0.6f);
            float rotation = t * t * 6f;

            var sp = _renderer.WorldToScreen(pos, arcHeight, _camera);
            var origin = new Vector2(cf.PivotX * cf.Texture.Width, cf.PivotY * cf.Texture.Height);
            _spriteBatch.Draw(cf.Texture, sp, null, Color.White, rotation, origin, scale, SpriteEffects.None, 0f);
        }
    }
}
