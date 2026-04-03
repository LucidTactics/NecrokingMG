using System;
using System.Collections.Generic;
using System.Text.Json;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Necroking.Core;
using Necroking.Data;
using Necroking.Movement;

namespace Necroking.World;

public class ProcessSlot
{
    public string Kind { get; set; } = "";
    public string ResourceID { get; set; } = "";
    public bool IsEmpty => string.IsNullOrEmpty(Kind);
}

public class QueueEntry
{
    public string ResourceID { get; set; } = "";
    public int Count { get; set; }
}

public class BuildingProcessState
{
    public List<QueueEntry> InputQueue1 { get; set; } = new();
    public List<QueueEntry> InputQueue2 { get; set; } = new();
    public List<QueueEntry> OutputQueue { get; set; } = new();
    public float ProcessTimer { get; set; }
    public bool Processing { get; set; }

    public int TotalInput1() { int t = 0; foreach (var e in InputQueue1) t += e.Count; return t; }
    public int TotalInput2() { int t = 0; foreach (var e in InputQueue2) t += e.Count; return t; }
    public int TotalOutput() { int t = 0; foreach (var e in OutputQueue) t += e.Count; return t; }
}

public class EnvironmentObjectDef
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Category { get; set; } = "Misc";
    public string TexturePath { get; set; } = "";
    public string HeightMapPath { get; set; } = "";
    public float SpriteWorldHeight { get; set; } = 4f;
    public float WorldHeight { get; set; }
    public float PivotX { get; set; } = 0.5f;
    public float PivotY { get; set; } = 1f;
    public float CollisionRadius { get; set; }
    public float CollisionOffsetX { get; set; }
    public float CollisionOffsetY { get; set; }
    public float Scale { get; set; } = 1f;
    public float PlacementScale { get; set; } = 1f;
    public string Group { get; set; } = "";
    public float GroupWeight { get; set; } = 1f;
    public bool IsBuilding { get; set; }
    public bool PlayerBuildable { get; set; }
    public int BuildingMaxHP { get; set; } = 100;
    public int BuildingProtection { get; set; }
    public int BuildingDefaultOwner { get; set; } = 1;
    public string BoundTriggerID { get; set; } = "";
    public ProcessSlot Input1 { get; set; } = new();
    public ProcessSlot Input2 { get; set; } = new();
    public ProcessSlot Output { get; set; } = new();
    public float ProcessTime { get; set; } = 10f;
    public int MaxInputQueue { get; set; } = 10;
    public int MaxOutputQueue { get; set; } = 10;
    public bool AutoSpawn { get; set; }
    public float SpawnOffsetX { get; set; }
    public float SpawnOffsetY { get; set; } = 1.5f;

    // Building costs (legacy)
    public int CostWood { get; set; }
    public int CostStone { get; set; }
    public int CostGold { get; set; }

    // Item-based building costs (references ItemRegistry IDs)
    public string Cost1ItemId { get; set; } = "";
    public int Cost1Amount { get; set; }
    public string Cost2ItemId { get; set; } = "";
    public int Cost2Amount { get; set; }

    // Placement radius: additive to collisionRadius for placement spacing checks
    public float PlacementRadius { get; set; }

    // Shadow type: 0=SpriteProjection (default), 1=DiffuseEllipse, 2=None
    public int ShadowType { get; set; }

    // Trap spell system
    public string TrapSpellId { get; set; } = "";   // spell to cast when enemy enters range
    public int TrapUses { get; set; }                // 0 = infinite uses
    public string TrapTriggeredSprite { get; set; } = ""; // sprite when firing
    public string TrapDeployedSprite { get; set; } = "";  // sprite after firing
    public float TrapTriggeredDuration { get; set; } = 0.3f; // seconds in triggered state
    public float TrapDeployedDuration { get; set; } = 2.0f;  // seconds in deployed state before fade/reset
    public float TrapFadeDuration { get; set; } = 1.0f;      // alpha fade-out duration for expended traps

    // Tint color (used by color harmonizer M04)
    public HdrColor TintColor { get; set; } = new(255, 255, 255, 255, 1f);

    // Foragable properties
    public bool IsForagable { get; set; }
    public string ForagableType { get; set; } = "";     // resource type name (e.g., "Mushroom", "Branch")
    public float RespawnTime { get; set; } = 180f;      // seconds (default 3 minutes)
    public float ScaleMin { get; set; } = 0.8f;         // random scale variation min
    public float ScaleMax { get; set; } = 1.2f;         // random scale variation max

    /// <summary>
    /// Write all properties of this def to a Utf8JsonWriter.
    /// Caller must call WriteStartObject/WriteEndObject around this.
    /// </summary>
    public void WriteJson(Utf8JsonWriter writer)
    {
        writer.WriteString("id", Id);
        writer.WriteString("name", Name);
        writer.WriteString("category", Category);
        writer.WriteString("texturePath", TexturePath);
        writer.WriteString("heightMapPath", HeightMapPath);
        writer.WriteNumber("spriteWorldHeight", SpriteWorldHeight);
        writer.WriteNumber("worldHeight", WorldHeight);
        writer.WriteNumber("pivotX", PivotX);
        writer.WriteNumber("pivotY", PivotY);
        writer.WriteNumber("collisionRadius", CollisionRadius);
        writer.WriteNumber("collisionOffsetX", CollisionOffsetX);
        writer.WriteNumber("collisionOffsetY", CollisionOffsetY);
        writer.WriteNumber("scale", Scale);
        writer.WriteNumber("placementScale", PlacementScale);
        writer.WriteString("group", Group);
        writer.WriteNumber("groupWeight", GroupWeight);
        writer.WriteBoolean("isBuilding", IsBuilding);
        writer.WriteBoolean("playerBuildable", PlayerBuildable);
        writer.WriteNumber("buildingMaxHP", BuildingMaxHP);
        writer.WriteNumber("buildingProtection", BuildingProtection);
        writer.WriteNumber("buildingDefaultOwner", BuildingDefaultOwner);
        writer.WriteNumber("costWood", CostWood);
        writer.WriteNumber("costStone", CostStone);
        writer.WriteNumber("costGold", CostGold);
        writer.WriteString("cost1ItemId", Cost1ItemId);
        writer.WriteNumber("cost1Amount", Cost1Amount);
        writer.WriteString("cost2ItemId", Cost2ItemId);
        writer.WriteNumber("cost2Amount", Cost2Amount);
        writer.WriteNumber("placementRadius", PlacementRadius);
        writer.WriteNumber("shadowType", ShadowType);
        writer.WriteString("trapSpellId", TrapSpellId);
        writer.WriteNumber("trapUses", TrapUses);
        writer.WriteString("trapTriggeredSprite", TrapTriggeredSprite);
        writer.WriteString("trapDeployedSprite", TrapDeployedSprite);
        writer.WriteNumber("trapTriggeredDuration", TrapTriggeredDuration);
        writer.WriteNumber("trapDeployedDuration", TrapDeployedDuration);
        writer.WriteNumber("trapFadeDuration", TrapFadeDuration);
        writer.WriteString("boundTriggerID", BoundTriggerID);
        // Processing slots
        writer.WriteStartObject("input1");
        writer.WriteString("kind", Input1.Kind);
        writer.WriteString("resourceID", Input1.ResourceID);
        writer.WriteEndObject();
        writer.WriteStartObject("input2");
        writer.WriteString("kind", Input2.Kind);
        writer.WriteString("resourceID", Input2.ResourceID);
        writer.WriteEndObject();
        writer.WriteStartObject("output");
        writer.WriteString("kind", Output.Kind);
        writer.WriteString("resourceID", Output.ResourceID);
        writer.WriteEndObject();
        writer.WriteNumber("processTime", ProcessTime);
        writer.WriteNumber("maxInputQueue", MaxInputQueue);
        writer.WriteNumber("maxOutputQueue", MaxOutputQueue);
        writer.WriteBoolean("autoSpawn", AutoSpawn);
        writer.WriteNumber("spawnOffsetX", SpawnOffsetX);
        writer.WriteNumber("spawnOffsetY", SpawnOffsetY);
        // Foragable
        writer.WriteBoolean("isForagable", IsForagable);
        writer.WriteString("foragableType", ForagableType);
        writer.WriteNumber("respawnTime", RespawnTime);
        writer.WriteNumber("scaleMin", ScaleMin);
        writer.WriteNumber("scaleMax", ScaleMax);
        // Tint color
        writer.WriteStartObject("tintColor");
        writer.WriteNumber("r", TintColor.R);
        writer.WriteNumber("g", TintColor.G);
        writer.WriteNumber("b", TintColor.B);
        writer.WriteNumber("a", TintColor.A);
        writer.WriteNumber("intensity", TintColor.Intensity);
        writer.WriteEndObject();
    }
}

public struct PlacedObject
{
    public ushort DefIndex;
    public float X, Y;
    public float Scale;
    public float Seed;
    public string ObjectID;
}

public enum TrapVisualState : byte { Hidden, Triggered, Deployed, FadingOut }

public struct PlacedObjectRuntime
{
    public int HP;
    public int Owner;
    public bool Alive;
    public bool Collected;         // foragable has been picked up
    public float RespawnTimer;     // countdown to respawn after collection
    public int TrapUsesRemaining;   // trap uses left (-1 = not a trap, 0 = infinite)
    public float TrapCooldownTimer; // time until trap can fire again
    public TrapVisualState TrapState; // current visual state
    public float TrapStateTimer;    // time remaining in current state
    public bool TrapExpended;       // true when uses depleted (fading out)

    public PlacedObjectRuntime() { HP = 0; Owner = 1; Alive = true; Collected = false; RespawnTimer = 0f; }
}

public class EnvironmentSystem
{
    private float _worldMaxY = 1f;
    private readonly List<string> _categories = new();
    private readonly List<string> _groups = new();
    private readonly List<EnvironmentObjectDef> _defs = new();
    private readonly List<Texture2D?> _textures = new();
    private readonly Dictionary<string, Texture2D?> _trapTextures = new(); // cached trap sprite textures
    private GraphicsDevice? _device;
    private readonly List<PlacedObject> _objects = new();
    private readonly List<PlacedObjectRuntime> _objectRuntime = new();
    private readonly List<BuildingProcessState> _processState = new();
    private int _nextObjectID;

    /// <summary>Called when collision state changes (object placed/removed/collected/destroyed/respawned).
    /// Wire this to RebuildPathfinder so the pathfinding grid stays in sync.</summary>
    public Action? OnCollisionsDirty;

    public void Init(float worldMaxY, GraphicsDevice? device = null) { _worldMaxY = worldMaxY; _device = device; }

    public int AddDef(EnvironmentObjectDef def) { _defs.Add(def); _textures.Add(null); return _defs.Count - 1; }
    public void RemoveDef(int index) { if (index >= 0 && index < _defs.Count) { _defs.RemoveAt(index); _textures.RemoveAt(index); } }
    public void ReplaceDef(int index, EnvironmentObjectDef def) { if (index >= 0 && index < _defs.Count) _defs[index] = def; }
    public int DefCount => _defs.Count;
    public EnvironmentObjectDef GetDef(int idx) => _defs[idx];
    public int FindDef(string id) { for (int i = 0; i < _defs.Count; i++) if (_defs[i].Id == id) return i; return -1; }

    public int AddObject(ushort defIndex, float x, float y, float scale = 1f, float seed = -1f)
    {
        var obj = new PlacedObject
        {
            DefIndex = defIndex, X = x, Y = y, Scale = scale,
            Seed = seed < 0 ? Random.Shared.NextSingle() : seed,
            ObjectID = $"obj_{_nextObjectID++}"
        };
        _objects.Add(obj);
        var def = _defs[defIndex];
        _objectRuntime.Add(new PlacedObjectRuntime
        {
            HP = def.BuildingMaxHP, Owner = def.BuildingDefaultOwner, Alive = true,
            TrapUsesRemaining = !string.IsNullOrEmpty(def.TrapSpellId) ? (def.TrapUses == 0 ? 0 : def.TrapUses) : -1,
        });
        _processState.Add(new BuildingProcessState());

        if (_defs[defIndex].CollisionRadius > 0)
            OnCollisionsDirty?.Invoke();

        return _objects.Count - 1;
    }

    public void RemoveObject(int index)
    {
        if (index < 0 || index >= _objects.Count) return;
        bool hadCollision = _defs[_objects[index].DefIndex].CollisionRadius > 0;
        _objects.RemoveAt(index);
        if (index < _objectRuntime.Count) _objectRuntime.RemoveAt(index);
        if (index < _processState.Count) _processState.RemoveAt(index);

        if (hadCollision)
            OnCollisionsDirty?.Invoke();
    }

    /// <summary>Mark an object as destroyed (Alive=false). Clears collision and hides it.
    /// Unlike RemoveObject, this preserves array indices.</summary>
    public void DestroyObject(int objIdx)
    {
        if (objIdx < 0 || objIdx >= _objectRuntime.Count) return;
        var rt = _objectRuntime[objIdx];
        if (!rt.Alive) return;
        rt.Alive = false;
        _objectRuntime[objIdx] = rt;

        if (_defs[_objects[objIdx].DefIndex].CollisionRadius > 0)
            OnCollisionsDirty?.Invoke();
    }

    public void ClearObjects() { _objects.Clear(); _objectRuntime.Clear(); _processState.Clear(); }
    public void ClearDefs() { _defs.Clear(); _textures.Clear(); }
    public int ObjectCount => _objects.Count;
    public PlacedObject GetObject(int idx) => _objects[idx];
    public PlacedObjectRuntime GetObjectRuntime(int idx) => _objectRuntime[idx];
    public BuildingProcessState GetProcessState(int idx) => _processState[idx];

    /// <summary>
    /// Collect a foragable object. Returns the ForagableType string, or null if not foragable/already collected.
    /// </summary>
    public string? CollectForagable(int objIdx)
    {
        if (objIdx < 0 || objIdx >= _objects.Count) return null;
        var def = _defs[_objects[objIdx].DefIndex];
        if (!def.IsForagable) return null;
        if (objIdx >= _objectRuntime.Count) return null;
        var rt = _objectRuntime[objIdx];
        if (rt.Collected) return null;

        rt.Collected = true;
        rt.RespawnTimer = def.RespawnTime;
        _objectRuntime[objIdx] = rt;

        if (def.CollisionRadius > 0)
            OnCollisionsDirty?.Invoke();

        return def.ForagableType;
    }

    /// <summary>
    /// Update foragable respawn timers. Call each frame with dt.
    /// </summary>
    public void UpdateForagables(float dt)
    {
        bool anyRespawned = false;
        for (int i = 0; i < _objectRuntime.Count; i++)
        {
            if (!_objectRuntime[i].Collected) continue;
            var rt = _objectRuntime[i];
            rt.RespawnTimer -= dt;
            if (rt.RespawnTimer <= 0f)
            {
                rt.Collected = false;
                rt.RespawnTimer = 0f;
                if (_defs[_objects[i].DefIndex].CollisionRadius > 0)
                    anyRespawned = true;
            }
            _objectRuntime[i] = rt;
        }
        if (anyRespawned)
            OnCollisionsDirty?.Invoke();
    }

    /// <summary>Event emitted when a trap fires a spell.</summary>
    public struct TrapFireEvent
    {
        public int ObjectIndex;
        public string SpellId;
        public Vec2 TrapPos;
        public int TargetUnitIdx;
        public int TrapOwner;
    }

    /// <summary>Pending trap fire events from the last UpdateTraps call. Consumed by Game1.</summary>
    public readonly List<TrapFireEvent> TrapFireEvents = new();

    /// <summary>Update trap cooldowns and find targets. Populates TrapFireEvents.</summary>
    public void UpdateTraps(float dt, UnitArrays units)
    {
        TrapFireEvents.Clear();

        for (int i = 0; i < _objectRuntime.Count; i++)
        {
            var rt = _objectRuntime[i];
            if (!rt.Alive || rt.Collected) continue;
            if (rt.TrapUsesRemaining < 0) continue; // -1 = not a trap

            var def = _defs[_objects[i].DefIndex];
            if (string.IsNullOrEmpty(def.TrapSpellId)) continue;

            // State machine for trap visuals
            switch (rt.TrapState)
            {
                case TrapVisualState.Hidden:
                {
                    // Tick cooldown
                    if (rt.TrapCooldownTimer > 0f)
                    {
                        rt.TrapCooldownTimer -= dt;
                        _objectRuntime[i] = rt;
                        continue;
                    }

                    // Find first enemy in range
                    var trapPos = new Vec2(_objects[i].X, _objects[i].Y);
                    int target = FindTrapTarget(trapPos, def, rt.Owner, units);
                    if (target < 0) continue;

                    // Fire! Transition to Triggered
                    TrapFireEvents.Add(new TrapFireEvent
                    {
                        ObjectIndex = i, SpellId = def.TrapSpellId,
                        TrapPos = trapPos, TargetUnitIdx = target, TrapOwner = rt.Owner
                    });

                    // Decrement uses
                    if (rt.TrapUsesRemaining > 0)
                    {
                        rt.TrapUsesRemaining--;
                        if (rt.TrapUsesRemaining <= 0)
                            rt.TrapExpended = true;
                    }

                    rt.TrapState = TrapVisualState.Triggered;
                    rt.TrapStateTimer = def.TrapTriggeredDuration;
                    break;
                }

                case TrapVisualState.Triggered:
                {
                    rt.TrapStateTimer -= dt;
                    if (rt.TrapStateTimer <= 0f)
                    {
                        rt.TrapState = TrapVisualState.Deployed;
                        rt.TrapStateTimer = def.TrapDeployedDuration;
                    }
                    break;
                }

                case TrapVisualState.Deployed:
                {
                    rt.TrapStateTimer -= dt;
                    if (rt.TrapStateTimer <= 0f)
                    {
                        if (rt.TrapExpended)
                        {
                            // Start fade-out
                            rt.TrapState = TrapVisualState.FadingOut;
                            rt.TrapStateTimer = def.TrapFadeDuration;
                        }
                        else
                        {
                            // Return to hidden, start cooldown
                            rt.TrapState = TrapVisualState.Hidden;
                            rt.TrapCooldownTimer = 0.5f; // overridden by Game1 with spell cooldown
                        }
                    }
                    break;
                }

                case TrapVisualState.FadingOut:
                {
                    rt.TrapStateTimer -= dt;
                    if (rt.TrapStateTimer <= 0f)
                    {
                        rt.Alive = false;
                        OnCollisionsDirty?.Invoke();
                    }
                    break;
                }
            }

            _objectRuntime[i] = rt;
        }
    }

    /// <summary>Set the trap cooldown for an object (called by Game1 after looking up spell cooldown).</summary>
    public void SetTrapCooldown(int objIdx, float cooldown)
    {
        if (objIdx < 0 || objIdx >= _objectRuntime.Count) return;
        var rt = _objectRuntime[objIdx];
        rt.TrapCooldownTimer = cooldown;
        _objectRuntime[objIdx] = rt;
    }

    private static int FindTrapTarget(Vec2 trapPos, EnvironmentObjectDef def, int trapOwner,
        UnitArrays units)
    {
        // Use the spell range from the def name — but we don't have SpellRegistry here.
        // Use a fixed detection range based on typical trap spell range.
        // Game1 can override with actual spell range if needed.
        float rangeSq = 2.5f * 2.5f; // slightly larger than trap_zap range of 1.5

        float bestDist = rangeSq;
        int bestIdx = -1;
        Faction trapFaction = trapOwner == 0 ? Faction.Undead : Faction.Human;

        for (int u = 0; u < units.Count; u++)
        {
            if (!units.Alive[u]) continue;
            if (units.Faction[u] == trapFaction) continue; // skip friendlies
            float d = (units.Position[u] - trapPos).LengthSq();
            if (d < bestDist) { bestDist = d; bestIdx = u; }
        }
        return bestIdx;
    }

    /// <summary>
    /// Check if an object is currently visible (not collected).
    /// </summary>
    public bool IsObjectVisible(int objIdx)
    {
        if (objIdx < 0 || objIdx >= _objects.Count) return false;
        if (objIdx < _objectRuntime.Count && _objectRuntime[objIdx].Collected) return false;
        if (objIdx < _objectRuntime.Count && !_objectRuntime[objIdx].Alive) return false;
        return true;
    }

    /// <summary>
    /// Check whether a new object of the given def can be placed at (x,y) without
    /// overlapping the collision radius of any existing object.
    /// Returns true if placement is valid (no overlap or def has no collision).
    /// </summary>
    public bool CanPlaceObject(int defIndex, float x, float y, float scale = 1f)
    {
        if (defIndex < 0 || defIndex >= _defs.Count) return false;
        var newDef = _defs[defIndex];
        // Placement check radius = collisionRadius + placementRadius (additive spacing)
        float newRadius = newDef.CollisionRadius * scale + newDef.PlacementRadius;
        float newCX = x + newDef.CollisionOffsetX;
        float newCY = y + newDef.CollisionOffsetY;

        // If both collision and placement radius are zero, always allow
        if (newRadius <= 0f) return true;

        for (int i = 0; i < _objects.Count; i++)
        {
            // Skip collected/destroyed objects
            if (i < _objectRuntime.Count && (_objectRuntime[i].Collected || !_objectRuntime[i].Alive))
                continue;

            var obj = _objects[i];
            var existDef = _defs[obj.DefIndex];
            float existRadius = existDef.CollisionRadius * obj.Scale + existDef.PlacementRadius;
            if (existRadius <= 0f) continue;

            float existCX = obj.X + existDef.CollisionOffsetX;
            float existCY = obj.Y + existDef.CollisionOffsetY;

            float dx = newCX - existCX;
            float dy = newCY - existCY;
            float minDist = newRadius + existRadius;
            if (dx * dx + dy * dy < minDist * minDist)
                return false;
        }
        return true;
    }

    public void BakeCollisions(TileGrid grid)
    {
        // Reset tiered cost fields to base terrain before stamping obstacles
        grid.RebuildTieredCostFields();

        for (int i = 0; i < _objects.Count; i++)
        {
            // Skip collected foragables and destroyed objects
            if (i < _objectRuntime.Count)
            {
                var rt = _objectRuntime[i];
                if (rt.Collected || !rt.Alive) continue;
            }

            var obj = _objects[i];
            var def = _defs[obj.DefIndex];
            if (def.CollisionRadius > 0)
            {
                float es = def.Scale * obj.Scale;
                float cx = obj.X + def.CollisionOffsetX * es;
                float cy = obj.Y + def.CollisionOffsetY * es;
                float cr = def.CollisionRadius * es;

                grid.StampImpassableCircle(cx, cy, cr);
                for (int tier = 0; tier < TerrainCosts.NumSizeTiers; tier++)
                {
                    grid.StampImpassableCircleTier(tier, cx, cy, cr);
                }
            }
        }
    }

    public IReadOnlyList<EnvironmentObjectDef> Defs => _defs;
    public IReadOnlyList<PlacedObject> Objects => _objects;
    public IReadOnlyList<string> Categories => _categories;
    public IReadOnlyList<string> Groups => _groups;

    public void LoadTextures(GraphicsDevice device)
    {
        _device = device;
        // Ensure texture list matches defs
        while (_textures.Count < _defs.Count) _textures.Add(null);

        for (int i = 0; i < _defs.Count; i++)
        {
            if (_textures[i] != null) continue;
            string path = _defs[i].TexturePath;
            if (string.IsNullOrEmpty(path)) continue;
            string resolved = Core.GamePaths.Resolve(path);
            if (!System.IO.File.Exists(resolved)) continue;
            try
            {
                using var stream = System.IO.File.OpenRead(resolved);
                _textures[i] = Necroking.Render.TextureUtil.LoadPremultiplied(device, stream);
            }
            catch { /* skip failed loads */ }
        }
    }

    public Texture2D? GetDefTexture(int defIdx)
    {
        if (defIdx < 0 || defIdx >= _textures.Count) return null;
        return _textures[defIdx];
    }

    /// <summary>Get the correct texture for an object based on trap visual state. Returns alpha multiplier.</summary>
    public Texture2D? GetObjectTexture(int objIdx, out float alpha)
    {
        alpha = 1f;
        if (objIdx < 0 || objIdx >= _objects.Count) return null;
        var obj = _objects[objIdx];
        var def = _defs[obj.DefIndex];

        if (objIdx >= _objectRuntime.Count || _objectRuntime[objIdx].TrapUsesRemaining < 0)
            return GetDefTexture(obj.DefIndex); // not a trap

        var rt = _objectRuntime[objIdx];
        switch (rt.TrapState)
        {
            case TrapVisualState.Triggered:
                if (!string.IsNullOrEmpty(def.TrapTriggeredSprite))
                    return GetOrLoadTrapTexture(def.TrapTriggeredSprite);
                return GetDefTexture(obj.DefIndex);

            case TrapVisualState.Deployed:
                if (!string.IsNullOrEmpty(def.TrapDeployedSprite))
                    return GetOrLoadTrapTexture(def.TrapDeployedSprite);
                return GetDefTexture(obj.DefIndex);

            case TrapVisualState.FadingOut:
                alpha = MathF.Max(0f, rt.TrapStateTimer / MathF.Max(def.TrapFadeDuration, 0.01f));
                if (!string.IsNullOrEmpty(def.TrapDeployedSprite))
                    return GetOrLoadTrapTexture(def.TrapDeployedSprite);
                return GetDefTexture(obj.DefIndex);

            default: // Hidden
                return GetDefTexture(obj.DefIndex);
        }
    }

    private Texture2D? GetOrLoadTrapTexture(string path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        if (_trapTextures.TryGetValue(path, out var cached)) return cached;
        string resolved = Core.GamePaths.Resolve(path);
        if (_device == null || !System.IO.File.Exists(resolved)) { _trapTextures[path] = null; return null; }
        try
        {
            var tex = Render.TextureUtil.LoadPremultiplied(_device, path);
            _trapTextures[path] = tex;
            return tex;
        }
        catch { _trapTextures[path] = null; return null; }
    }

    /// <summary>
    /// Reload texture for a single def (e.g. after changing TexturePath in the editor).
    /// </summary>
    public void ReloadDefTexture(int defIdx)
    {
        if (_device == null || defIdx < 0 || defIdx >= _defs.Count) return;
        while (_textures.Count <= defIdx) _textures.Add(null);
        _textures[defIdx]?.Dispose();
        _textures[defIdx] = null;
        string path = _defs[defIdx].TexturePath;
        if (string.IsNullOrEmpty(path)) return;
        string resolved = Core.GamePaths.Resolve(path);
        if (!System.IO.File.Exists(resolved)) return;
        try
        {
            using var stream = System.IO.File.OpenRead(resolved);
            _textures[defIdx] = Necroking.Render.TextureUtil.LoadPremultiplied(_device, stream);
        }
        catch { /* skip failed loads */ }
    }

    /// <summary>
    /// Get render-ordered indices (sorted by Y position for depth).
    /// </summary>
    public List<int> GetRenderOrder()
    {
        var order = new List<int>(_objects.Count);
        for (int i = 0; i < _objects.Count; i++) order.Add(i);
        order.Sort((a, b) => _objects[a].Y.CompareTo(_objects[b].Y));
        return order;
    }
}
