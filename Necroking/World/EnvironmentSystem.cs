using System;
using System.Collections.Generic;
using System.Text.Json;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Necroking.Core;
using Necroking.Data;
using Necroking.Editor;
using Necroking.Lib;
using Necroking.Movement;

namespace Necroking.World;

public class ProcessSlot
{
    public string Kind { get; set; } = "";
    public string ResourceID { get; set; } = "";
    [System.Text.Json.Serialization.JsonIgnore]
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

/// <summary>
/// One corpse parked on a table. Captures the data needed to (a) render the body
/// bag at the table's spawn offset, (b) decide what zombie type to spawn when the
/// craft completes (UnitDefID → UnitDef.ZombieTypeID resolution), and (c) match
/// the spawned zombie's facing/scale to the corpse it came from.
///
/// Occupied is the *only* source of truth for slot state — checking SourceUnitDefID
/// emptiness conflates "empty slot" with "loaded but data is missing", which broke
/// craft validation when scenarios ran without the unit registry available.
/// </summary>
public struct TableCorpseSlot
{
    public bool Occupied;
    public string SourceUnitDefID;
    public float FacingAngle;
    public float SpriteScale;
    public bool IsEmpty => !Occupied;
}

/// <summary>One item parked on a table. Occupied flag distinguishes filled-with-blank-id
/// (rare, but shouldn't be silently dropped) from empty.</summary>
public struct TableItemSlot
{
    public bool Occupied;
    public string ItemID;
    public bool IsEmpty => !Occupied;
}

/// <summary>
/// Per-instance state for a craft-table env object (parallel-list-stored next to
/// PlacedObjectRuntime / BuildingProcessState). Slot-based, NOT queue-based — the
/// table UI shows fixed slots that the player drops corpses/items into; queues
/// belong to the obelisk-style streaming pattern.
///
/// Corpse and item arrays are sized to def.CorpseSlots / def.ItemSlots at
/// construction; resizing requires rebuilding the state (cheap — these arrays
/// are tiny, max 3 each per current design).
///
/// Crafting=true while a unit is channeling at this table; CraftTimer counts up
/// from 0 to def.ProcessTime. ChannelerUnitID identifies the assigned unit so we
/// only advance the timer while that specific unit is in range and channeling
/// (any other unit nearby is irrelevant).
/// </summary>
public class TableCraftState
{
    public TableCorpseSlot[] CorpseSlots = System.Array.Empty<TableCorpseSlot>();
    public TableItemSlot[] ItemSlots = System.Array.Empty<TableItemSlot>();

    public bool Crafting;
    public float CraftTimer;
    public uint ChannelerUnitID;   // 0 = no channeler assigned

    // The loop portion's real-time budget (seconds), computed render-side from the
    // ImbueTable Start/Loop/Finish anim durations so the WHOLE start+loop+finish
    // sequence fits the table's ProcessTime. Craft completes when CraftTimer hits
    // this (not ProcessTime). 0 = not set → fall back to ProcessTime.
    public float LoopBudget;

    /// <summary>Rebuild slot arrays to match def-declared counts. Idempotent.</summary>
    public void EnsureSized(int corpseSlots, int itemSlots)
    {
        if (CorpseSlots.Length != corpseSlots)
            CorpseSlots = new TableCorpseSlot[corpseSlots];
        if (ItemSlots.Length != itemSlots)
            ItemSlots = new TableItemSlot[itemSlots];
    }

    /// <summary>Index of first empty corpse slot, or -1 if all full.</summary>
    public int FindEmptyCorpseSlot()
    {
        for (int i = 0; i < CorpseSlots.Length; i++)
            if (CorpseSlots[i].IsEmpty) return i;
        return -1;
    }

    /// <summary>Index of first empty item slot, or -1 if all full.</summary>
    public int FindEmptyItemSlot()
    {
        for (int i = 0; i < ItemSlots.Length; i++)
            if (ItemSlots[i].IsEmpty) return i;
        return -1;
    }

    public bool HasAnyCorpse()
    {
        for (int i = 0; i < CorpseSlots.Length; i++)
            if (!CorpseSlots[i].IsEmpty) return true;
        return false;
    }

    /// <summary>Reset crafting progress and clear assigned channeler. Slot contents untouched.</summary>
    public void CancelChannel()
    {
        Crafting = false;
        CraftTimer = 0f;
        ChannelerUnitID = 0;
    }
}

/// <summary>
/// One env-object definition (env_defs.json entry). Serialized directly via
/// System.Text.Json (camelCase naming + HdrColor/HarmonizeSettings converters,
/// see MapData.EnvDefJson) — the old split hand-written reader/writer pair
/// (MapData.ParseEnvDef / WriteJson here) is gone, so a property added to this
/// class round-trips by construction.
/// </summary>
public class EnvironmentObjectDef : System.Text.Json.Serialization.IJsonOnDeserialized
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

    // ─────────────────────────────────────────────
    //  Craft table (slot-based recipe)
    // ─────────────────────────────────────────────
    /// <summary>Number of corpse slots on this craft-table. >0 marks the def as a table;
    /// 0 means the def is not a craft-table. See TableCraftState.</summary>
    public int CorpseSlots { get; set; }
    /// <summary>Number of item (potion) slots on this craft-table.</summary>
    public int ItemSlots { get; set; }
    /// <summary>Essence cost per craft. Consumed from PlayerResources.Essence on craft start.</summary>
    public int EssenceCost { get; set; }

    // ─────────────────────────────────────────────
    //  Worker job system (P0)
    // ─────────────────────────────────────────────
    /// <summary>JobDef.id this building enables ("" = none). See data/jobs.json.</summary>
    public string HostsJob { get; set; } = "";
    /// <summary>Resource id this building stockpiles ("" = none).</summary>
    public string StoredResource { get; set; } = "";
    /// <summary>Max units of StoredResource this building can hold (0 = unlimited).</summary>
    public int StorageCap { get; set; }
    /// <summary>Empty Grave: houses worker(s). The worker pool = filled grave slots.</summary>
    public bool IsWorkerHome { get; set; }
    /// <summary>Worker capacity contributed per instance (home slots, or job slots).</summary>
    public int WorkerSlots { get; set; } = 1;

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

    // Per-def overrides for the diffuse-ellipse shadow (ShadowType == 1).
    // Multiplied against the global Settings.Shadow.Opacity and the base
    // ellipse dimensions computed from the sprite size. Defaults give the
    // tuned values used when the feature shipped (outer 2.5× wider / 1.7×
    // taller than the base, inner 0.6× of base) so existing defs that don't
    // set these still render the same.
    public float ShadowOpacityScale { get; set; } = 1.0f;
    public float ShadowOuterWScale { get; set; } = 2.5f;
    public float ShadowOuterHScale { get; set; } = 1.7f;
    public float ShadowInnerWScale { get; set; } = 0.6f;
    public float ShadowInnerHScale { get; set; } = 0.6f;

    // Trap spell system
    public string TrapSpellId { get; set; } = "";   // spell to cast when enemy enters range
    public int TrapUses { get; set; }                // 0 = infinite uses
    public string TrapTriggeredSprite { get; set; } = ""; // sprite when firing
    public string TrapDeployedSprite { get; set; } = "";  // sprite after firing
    public float TrapTriggeredDuration { get; set; } = 0.3f; // seconds in triggered state
    public float TrapDeployedDuration { get; set; } = 2.0f;  // seconds in deployed state before fade/reset
    public float TrapFadeDuration { get; set; } = 1.0f;      // alpha fade-out duration for expended traps

    // Glyph trap: when true, placing this def spawns a MagicGlyph blueprint (shader-rendered
    // ground rune that builds to dormant, triggers on proximity, casts TrapSpellId). The def
    // itself is never instantiated as an env object.
    public bool IsGlyphTrap { get; set; }
    public float GlyphRadius { get; set; } = 1.5f;  // world-space radius of the glyph circle

    // Tint color (cheap multiply applied at draw)
    public HdrColor TintColor { get; set; } = new(255, 255, 255, 255, 1f);

    // Per-pixel color harmonization (non-destructive). null = disabled (the
    // presence of the object IS the enable toggle — no UI/bake/JSON when null).
    // Harmonize → main sprite texture; HarmonizeCorrupt → the corrupted variant.
    // The harmonized texture is baked once at load from the source PNG, so no
    // duplicate asset files exist on disk.
    public HarmonizeSettings? Harmonize { get; set; }
    public HarmonizeSettings? HarmonizeCorrupt { get; set; }

    // When true, each placed instance is deterministically (per-seed) flipped
    // horizontally ~50% of the time, adding natural variety. Default depends on
    // category (see DefaultRandomFlipForCategory) — on for organic props like
    // trees/bushes/rocks, off for directional/readable things like buildings.
    // The setter flag + OnDeserialized below apply the category default only
    // when the JSON carried no "randomFlip" field (older maps' embedded defs).
    private bool _randomFlip;
    private bool _randomFlipAuthored;
    public bool RandomFlip { get => _randomFlip; set { _randomFlip = value; _randomFlipAuthored = true; } }

    void System.Text.Json.Serialization.IJsonOnDeserialized.OnDeserialized()
    {
        if (!_randomFlipAuthored)
            _randomFlip = DefaultRandomFlipForCategory(Category);
    }

    /// <summary>Sensible default for <see cref="RandomFlip"/> based on category.
    /// Off for structured/directional/readable objects (they look wrong mirrored
    /// or need consistent silhouettes for quick reading); on for organic props
    /// where mirroring adds diversity.</summary>
    public static bool DefaultRandomFlipForCategory(string? category)
    {
        switch ((category ?? "").Trim().ToLowerInvariant())
        {
            case "building":
            case "crypt":
            case "wall":
            case "trap":
            case "tombstone":
            case "ground":
                return false;
            default:
                return true; // trees, bushes, rocks, foragables, grave, misc, etc.
        }
    }

    // Animation (spritesheet) properties
    public bool IsAnimated { get; set; }
    public int AnimFramesX { get; set; } = 1;    // columns in spritesheet
    public int AnimFramesY { get; set; } = 1;    // rows in spritesheet
    public float AnimFPS { get; set; } = 10f;    // playback frame rate
    public float AnimNoise { get; set; }         // 0-1, fraction of FPS affected by per-instance noise
    public float AnimWindSync { get; set; } = 0.5f;  // 0-1, spatial wind coherence (0=none, 1=full)

    // Death-fog source/sink. Both are units-per-second contributed at the cell
    // under this object. Zero (default) means the object is inert. See DeathFogSystem.
    public float FogEmitRate { get; set; }
    public float FogAbsorbRate { get; set; }

    // Foragable properties
    public bool IsForagable { get; set; }
    public string ForagableType { get; set; } = "";     // resource type name (e.g., "Mushroom", "Branch")
    public float RespawnTime { get; set; } = 180f;      // seconds (default 3 minutes)
    public float ScaleMin { get; set; } = 0.8f;         // random scale variation min
    public float ScaleMax { get; set; } = 1.2f;         // random scale variation max

    // Berry bush state. When IsBerryBush is true the instance carries a
    // three-state machine (Berries → NoBerry → Berries, with Poisoned as a
    // side-branch from Berries). Each state has its own sprite path.
    public bool IsBerryBush { get; set; }
    public string NoBerrySprite { get; set; } = "";     // sprite when berries have been eaten
    public string PoisonedSprite { get; set; } = "";    // sprite when berries have been treated with a potion
    public float BerryRespawnTime { get; set; } = 120f; // seconds NoBerry → Berries

    // Corruption: when IsCorruptable, instances accumulate stress in death-fog cells
    // (see DeathFogSystem.ApplySinks) and flip to CorruptedSprite once they cross
    // the system-wide threshold. Corrupted sprite is single-frame; animation is
    // suppressed for corrupted instances at render time.
    public bool IsCorruptable { get; set; }
    public string CorruptedSprite { get; set; } = "";

    /// <summary>Total frame count for animated spritesheets.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public int AnimTotalFrames => AnimFramesX * AnimFramesY;

    /// <summary>Get the source rectangle for a specific frame in the spritesheet.</summary>
    public Rectangle GetAnimFrameRect(int texWidth, int texHeight, int frame)
    {
        int fw = texWidth / Math.Max(AnimFramesX, 1);
        int fh = texHeight / Math.Max(AnimFramesY, 1);
        int col = frame % AnimFramesX;
        int row = frame / AnimFramesX;
        return new Rectangle(col * fw, row * fh, fw, fh);
    }

    // (The hand-written WriteJson that used to live here — the writer half of
    // the split reader/writer pair — is gone: env defs now serialize through
    // MapData.EnvDefJson, the same options the loader uses.)
}

public struct PlacedObject
{
    public ushort DefIndex;
    public float X, Y;
    public float Scale;
    public float Seed;
    public string ObjectID;
    /// <summary>True = written to the map JSON by the editor's Save Map: editor-placed,
    /// map-loaded, and player-built objects. False = gameplay spawns (zone foragables,
    /// village stamps, creature drops, dev/scenario placements) so saving the map
    /// doesn't accumulate them across sessions.</summary>
    public bool Persistent;
}

/// <summary>World-space collision circle of a placed env object. R &lt;= 0 = no collision.
/// Produced only by <see cref="EnvironmentSystem.GetCollisionCircle(EnvironmentObjectDef, in PlacedObject)"/> —
/// never recompute the centre/radius math inline.</summary>
public readonly struct EnvCollisionCircle
{
    public readonly float CX, CY, R;
    public EnvCollisionCircle(float cx, float cy, float r) { CX = cx; CY = cy; R = r; }
    public bool HasCollision => R > 0f;
}

public enum TrapVisualState : byte { Hidden, Triggered, Deployed, FadingOut }

public enum BerryState : byte { Berries, NoBerry, Poisoned }

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
    public float BuildProgress;    // 0 = blueprint, 1 = fully built (default 1 for non-buildable)
    public float AnimTime;         // accumulated animation time (frames), advanced at noise-modulated rate
    public bool AnimReversed;      // true = playing backward (weighted momentum)
    public float AnimHoldTime;     // remaining hold time in seconds before resuming (reversal cushion)
    public uint AnimRng;           // per-instance RNG state for reversal rolls
    public bool Corrupted;         // true once dissolve transition completed (final corrupted state)
    public float CorruptionStress; // accumulated absorbed-fog units net of healing; clamped to [0, threshold]
    public float CorruptionTime;   // 0 = healthy; >0 = transitioning (seconds elapsed in dissolve); reaches DeathFogSystem.CorruptionTransitionDuration when done

    // Berry bush state. Only meaningful when the def has IsBerryBush=true; ignored otherwise.
    public BerryState BerryState;
    public float BerryStateTimer;  // counts up in NoBerry; when >= def.BerryRespawnTime → Berries
    public string AppliedBuffID;   // buff to apply to the eater when bush is Poisoned (empty for vanilla Berries)

    // Worker job system (P0): how much of the def's StoredResource is stockpiled here.
    public int StoredAmount;

    public PlacedObjectRuntime() { HP = 0; Owner = 1; Alive = true; Collected = false; RespawnTimer = 0f; BuildProgress = 1f; AppliedBuffID = ""; }
}

/// <summary>
/// The environment-object store: defs (tree/rock/building/… templates) plus every placed
/// instance, with spatial/occupancy queries, foragable collect/respawn, and per-building
/// state (construction progress, craft processes). Per-game — owned and recreated by
/// <see cref="GameSession"/>. Rendering of these objects lives in GameRenderer; the job
/// logic that consumes them in WorkerSystem.
/// </summary>
public class EnvironmentSystem
{
    private float _worldMaxY = 1f;
    private readonly List<EnvironmentObjectDef> _defs = new();
    private readonly List<Texture2D?> _textures = new();
    private readonly Render.TextureCache _overrideTextures = new("startup"); // cached single-frame overrides (trap & corrupted sprites)
    // Per-def harmonized corrupt textures. Keyed by def index (NOT path) because
    // defs can share a corrupt sprite path but harmonize it differently. The
    // cached value is the texture to return (may be the shared raw on fallback);
    // _corruptHarmonizedOwned tracks which ones we created and must dispose.
    private readonly Dictionary<int, Texture2D?> _corruptHarmonized = new();
    private readonly HashSet<int> _corruptHarmonizedOwned = new();
    private GraphicsDevice? _device;
    private Texture2D? _placeholderTexture; // shared placeholder for defs with missing/failed sprites
    private readonly List<PlacedObject> _objects = new();
    private readonly List<PlacedObjectRuntime> _objectRuntime = new();
    private readonly List<BuildingProcessState> _processState = new();
    private readonly List<TableCraftState> _tableState = new();
    private int _nextObjectID;

    /// <summary>Called when collision state changes (object placed/removed/collected/destroyed/respawned).
    /// Wire this to RebuildPathfinder so the pathfinding grid stays in sync.
    /// Fallback-only when <see cref="OnCollisionRegionDirty"/> is wired: single-object
    /// changes then fire the region callback instead (see FireCollisionsDirty).</summary>
    public Action? OnCollisionsDirty;

    /// <summary>Region-scoped collision-dirty event: (minTX, minTY, maxTX, maxTY)
    /// tile AABB covering the changed object's collision circle at MAX tier
    /// inflation. Preferred over <see cref="OnCollisionsDirty"/> when wired —
    /// lets the sim do a dirty-region rebake + targeted pathfinder invalidation
    /// (~ms) instead of the full-map rebuild (~450ms on 4097²).</summary>
    public Action<int, int, int, int>? OnCollisionRegionDirty;

    /// <summary>Fire the collision-dirty signal for one object's circle.
    /// Prefers the region callback; falls back to the legacy full-rebuild one.</summary>
    private void FireCollisionsDirty(float cx, float cy, float collisionRadius)
    {
        if (OnCollisionRegionDirty == null)
        {
            OnCollisionsDirty?.Invoke();
            return;
        }
        // Cover the largest tier inflation plus a 1-tile safety rim so the
        // region reset provably contains every tile the stamp ever touched.
        float cr = collisionRadius + TerrainCosts.SizeTierRadius[TerrainCosts.NumSizeTiers - 1];
        OnCollisionRegionDirty(
            (int)MathF.Floor(cx - cr) - 1, (int)MathF.Floor(cy - cr) - 1,
            (int)MathF.Ceiling(cx + cr) + 1, (int)MathF.Ceiling(cy + cr) + 1);
    }

    /// <summary>Object-index convenience for <see cref="FireCollisionsDirty(float,float,float)"/>.
    /// The object must still be present in _objects.</summary>
    private void FireCollisionsDirty(int objIdx)
    {
        var c = GetCollisionCircle(objIdx);
        FireCollisionsDirty(c.CX, c.CY, c.R);
    }

    public void Init(float worldMaxY, GraphicsDevice? device = null) { _worldMaxY = worldMaxY; _device = device; }

    public int AddDef(EnvironmentObjectDef def) { _defs.Add(def); _textures.Add(null); return _defs.Count - 1; }
    public void RemoveDef(int index) { if (index >= 0 && index < _defs.Count) { _defs.RemoveAt(index); _textures.RemoveAt(index); } }
    public void ReplaceDef(int index, EnvironmentObjectDef def) { if (index >= 0 && index < _defs.Count) _defs[index] = def; }
    public int DefCount => _defs.Count;
    public EnvironmentObjectDef GetDef(int idx) => _defs[idx];
    public int FindDef(string id) { for (int i = 0; i < _defs.Count; i++) if (_defs[i].Id == id) return i; return -1; }

    /// <summary>Distinct env-def Category values in first-seen order (includes the
    /// empty category if any def has one). Callers add their own "All"/"Groups"/"Misc"
    /// sentinels, empty-filtering, and sorting. Consolidates the per-editor
    /// distinct-category scans (MapEditorWindow / EnvObjectEditorWindow).</summary>
    public List<string> DistinctCategories()
    {
        var list = new List<string>();
        var seen = new HashSet<string>();
        for (int i = 0; i < _defs.Count; i++)
        {
            string c = _defs[i].Category;
            if (seen.Add(c)) list.Add(c);
        }
        return list;
    }

    /// <summary>Distinct non-empty env-def Group names in first-seen order. Callers
    /// sort if they want alphabetical order. Consolidates the per-editor group
    /// distinct-scans.</summary>
    public List<string> DistinctGroups()
    {
        var list = new List<string>();
        var seen = new HashSet<string>();
        for (int i = 0; i < _defs.Count; i++)
        {
            string g = _defs[i].Group;
            if (!string.IsNullOrEmpty(g) && seen.Add(g)) list.Add(g);
        }
        return list;
    }

    /// <summary>THE collision-circle formula: es = def.Scale * obj.Scale;
    /// centre = (X,Y) + CollisionOffset*es; r = CollisionRadius*es. Single source
    /// for grid stamping, EnvSpatialIndex, dirty-region fires, placement checks,
    /// and debug draw — never inline this math.</summary>
    public static EnvCollisionCircle GetCollisionCircle(EnvironmentObjectDef def, in PlacedObject obj)
    {
        float es = def.Scale * obj.Scale;
        return new EnvCollisionCircle(
            obj.X + def.CollisionOffsetX * es,
            obj.Y + def.CollisionOffsetY * es,
            def.CollisionRadius * es);
    }

    /// <summary>Collision circle of object <paramref name="objIdx"/>. Purely
    /// geometric — no Alive/Collected gating (callers gate).</summary>
    public EnvCollisionCircle GetCollisionCircle(int objIdx)
    {
        var obj = _objects[objIdx];
        return GetCollisionCircle(_defs[obj.DefIndex], in obj);
    }

    public int AddObject(ushort defIndex, float x, float y, float scale = 1f, float seed = -1f, bool persistent = false)
    {
        var obj = new PlacedObject
        {
            DefIndex = defIndex, X = x, Y = y, Scale = scale,
            Seed = seed < 0 ? Random.Shared.NextSingle() : seed,
            ObjectID = $"obj_{_nextObjectID++}",
            Persistent = persistent
        };
        _objects.Add(obj);
        var def = _defs[defIndex];

        // Derive animation start frame and RNG seed from world position for deterministic variety
        float animStart = 0f;
        uint animRng = 0;
        if (def.IsAnimated && def.AnimTotalFrames > 1)
        {
            // Hash position into a seed
            uint hash = (uint)(x * 73856093f) ^ (uint)(y * 19349663f);
            hash ^= hash >> 16; hash *= 0x45d9f3b; hash ^= hash >> 16;
            animRng = hash;
            animStart = (hash % (uint)def.AnimTotalFrames);
        }

        _objectRuntime.Add(new PlacedObjectRuntime
        {
            HP = def.BuildingMaxHP, Owner = def.BuildingDefaultOwner, Alive = true,
            TrapUsesRemaining = !string.IsNullOrEmpty(def.TrapSpellId) ? (def.TrapUses == 0 ? 0 : def.TrapUses) : -1,
            AnimTime = animStart, AnimRng = animRng,
        });
        _processState.Add(new BuildingProcessState());
        var tableState = new TableCraftState();
        if (def.CorpseSlots > 0 || def.ItemSlots > 0)
            tableState.EnsureSized(def.CorpseSlots, def.ItemSlots);
        _tableState.Add(tableState);

        if (_defs[defIndex].CollisionRadius > 0)
            FireCollisionsDirty(_objects.Count - 1);

        return _objects.Count - 1;
    }

    public void RemoveObject(int index)
    {
        if (index < 0 || index >= _objects.Count) return;
        var remObj = _objects[index];
        var remDef = _defs[remObj.DefIndex];
        bool hadCollision = remDef.CollisionRadius > 0;
        // Capture the circle BEFORE removal — the region fire below needs it.
        var remCircle = GetCollisionCircle(remDef, in remObj);
        _objects.RemoveAt(index);
        if (index < _objectRuntime.Count) _objectRuntime.RemoveAt(index);
        if (index < _processState.Count) _processState.RemoveAt(index);
        if (index < _tableState.Count) _tableState.RemoveAt(index);

        if (hadCollision)
            FireCollisionsDirty(remCircle.CX, remCircle.CY, remCircle.R);
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
            FireCollisionsDirty(objIdx);
    }

    public void ClearObjects() { _objects.Clear(); _objectRuntime.Clear(); _processState.Clear(); _tableState.Clear(); }
    public void ClearDefs()
    {
        // Dispose owned GPU textures before dropping the lists — ClearDefs runs on every map
        // load (StartGame), so a bare Clear() orphaned the entire env-texture set on the GPU
        // each reload. Skip the shared _placeholderTexture (created once, reused across loads)
        // and dedupe in case two defs alias the same Texture2D.
        var disposed = new HashSet<Texture2D>();
        foreach (var tex in _textures)
            if (tex != null && tex != _placeholderTexture && disposed.Add(tex)) tex.Dispose();
        _defs.Clear(); _textures.Clear();
        // Corruption-harmonized variants are owned, per-def, and keyed by (now-invalid) def
        // index — dispose the ones we created and drop the maps so they don't leak or mis-key.
        foreach (var kv in _corruptHarmonized)
            if (kv.Value != null && _corruptHarmonizedOwned.Contains(kv.Key) && disposed.Add(kv.Value))
                kv.Value.Dispose();
        _corruptHarmonized.Clear();
        _corruptHarmonizedOwned.Clear();
    }
    public int ObjectCount => _objects.Count;
    public PlacedObject GetObject(int idx) => _objects[idx];
    public PlacedObjectRuntime GetObjectRuntime(int idx) => _objectRuntime[idx];
    public void SetObjectRuntime(int idx, PlacedObjectRuntime rt) => _objectRuntime[idx] = rt;
    public BuildingProcessState GetProcessState(int idx) => _processState[idx];
    public TableCraftState GetTableState(int idx) => _tableState[idx];

    /// <summary>Add an object as an unbuilt blueprint (BuildProgress = 0).</summary>
    public int AddObjectAsBlueprint(ushort defIndex, float x, float y, float scale = 1f, bool persistent = false)
    {
        int idx = AddObject(defIndex, x, y, scale, persistent: persistent);
        var rt = _objectRuntime[idx];
        rt.BuildProgress = 0f;
        _objectRuntime[idx] = rt;
        return idx;
    }

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
            FireCollisionsDirty(objIdx);

        return def.ForagableType;
    }

    /// <summary>
    /// Update foragable respawn timers. Call each frame with dt.
    /// </summary>
    public void UpdateForagables(float dt)
    {
        for (int i = 0; i < _objectRuntime.Count; i++)
        {
            if (!_objectRuntime[i].Collected) continue;
            // RespawnTime <= 0 means single-use: once collected it stays gone.
            if (_defs[_objects[i].DefIndex].RespawnTime <= 0f) continue;
            var rt = _objectRuntime[i];
            rt.RespawnTimer -= dt;
            if (rt.RespawnTimer <= 0f)
            {
                rt.Collected = false;
                rt.RespawnTimer = 0f;
                // Per-object region fire; the sim unions same-tick regions, so
                // several respawns in one tick still cost one rebake.
                if (_defs[_objects[i].DefIndex].CollisionRadius > 0)
                    FireCollisionsDirty(i);
            }
            _objectRuntime[i] = rt;
        }
    }

    /// <summary>Count objects of the given def inside the rect that still occupy a
    /// "spawned" slot for zone spawning: visible, or collected-but-pending-respawn
    /// (def RespawnTime &gt; 0, so it comes back on its own). Collected single-use
    /// objects don't count — they stay gone until revived. Pass
    /// <paramref name="positions"/> to also collect their world positions (used for
    /// keep-apart spacing when scattering new spawns).</summary>
    public int CountActiveOfDefInRect(int defIdx, float minX, float minY, float maxX, float maxY,
        List<Vec2>? positions = null)
    {
        int n = 0;
        for (int i = 0; i < _objects.Count; i++)
        {
            var obj = _objects[i];
            if (obj.DefIndex != defIdx) continue;
            if (obj.X < minX || obj.X > maxX || obj.Y < minY || obj.Y > maxY) continue;
            if (i < _objectRuntime.Count)
            {
                var rt = _objectRuntime[i];
                if (!rt.Alive) continue;
                if (rt.Collected && _defs[obj.DefIndex].RespawnTime <= 0f) continue;
            }
            n++;
            positions?.Add(new Vec2(obj.X, obj.Y));
        }
        return n;
    }

    /// <summary>Revive one collected single-use object of the given def inside the rect.
    /// Zone spawning prefers this over AddObject so long sessions reuse spent instances
    /// instead of growing the object list forever. Returns false when there is nothing
    /// to revive (spawn a fresh object instead).</summary>
    public bool TryReviveForagableInRect(int defIdx, float minX, float minY, float maxX, float maxY)
    {
        for (int i = 0; i < _objects.Count && i < _objectRuntime.Count; i++)
        {
            var obj = _objects[i];
            if (obj.DefIndex != defIdx) continue;
            if (_defs[obj.DefIndex].RespawnTime > 0f) continue; // self-respawns on its own timer
            var rt = _objectRuntime[i];
            if (!rt.Collected || !rt.Alive) continue;
            if (obj.X < minX || obj.X > maxX || obj.Y < minY || obj.Y > maxY) continue;
            rt.Collected = false;
            rt.RespawnTimer = 0f;
            _objectRuntime[i] = rt;
            if (_defs[obj.DefIndex].CollisionRadius > 0)
                FireCollisionsDirty(i);
            return true;
        }
        return false;
    }

    /// <summary>Tick berry-bush state machines. NoBerry bushes count up to
    /// BerryRespawnTime then return to Berries (clearing any prior AppliedBuffID).
    /// Poisoned bushes do not decay back on their own — they stay Poisoned until
    /// a deer eats them (consuming the poison) or they're destroyed.</summary>
    public void UpdateBerryBushes(float dt)
    {
        for (int i = 0; i < _objectRuntime.Count; i++)
        {
            var def = _defs[_objects[i].DefIndex];
            if (!def.IsBerryBush) continue;
            var rt = _objectRuntime[i];
            if (rt.BerryState != BerryState.NoBerry) continue;
            rt.BerryStateTimer += dt;
            if (rt.BerryStateTimer >= def.BerryRespawnTime)
            {
                rt.BerryState = BerryState.Berries;
                rt.BerryStateTimer = 0f;
                rt.AppliedBuffID = "";
            }
            _objectRuntime[i] = rt;
        }
    }

    /// <summary>Apply a poison/paralysis treatment to a berry bush. Only valid
    /// when the bush is in Berries state. Returns true if applied.</summary>
    public bool PoisonBerryBush(int objIdx, string buffID)
    {
        if (objIdx < 0 || objIdx >= _objectRuntime.Count) return false;
        var def = _defs[_objects[objIdx].DefIndex];
        if (!def.IsBerryBush) return false;
        var rt = _objectRuntime[objIdx];
        if (rt.BerryState != BerryState.Berries) return false;
        rt.BerryState = BerryState.Poisoned;
        rt.AppliedBuffID = buffID ?? "";
        rt.BerryStateTimer = 0f;
        _objectRuntime[objIdx] = rt;
        return true;
    }

    /// <summary>Consume berries (deer ate the bush). Returns the buff id that
    /// should apply to the eater — empty string if the bush was vanilla Berries
    /// (caller should apply satiation), or the AppliedBuffID if Poisoned.
    /// Returns null if the bush isn't a berry bush or has no berries to eat.</summary>
    public string? ConsumeBerryBush(int objIdx)
    {
        if (objIdx < 0 || objIdx >= _objectRuntime.Count) return null;
        var def = _defs[_objects[objIdx].DefIndex];
        if (!def.IsBerryBush) return null;
        var rt = _objectRuntime[objIdx];
        if (rt.BerryState == BerryState.NoBerry) return null;
        string result = rt.BerryState == BerryState.Poisoned ? (rt.AppliedBuffID ?? "") : "";
        rt.BerryState = BerryState.NoBerry;
        rt.BerryStateTimer = 0f;
        rt.AppliedBuffID = "";
        _objectRuntime[objIdx] = rt;
        return result;
    }

    /// <summary>
    /// Advance per-instance animation timers with noise-modulated playback speed,
    /// spatial wind sync, and weighted momentum direction changes.
    /// Call each frame with dt and the global game time (for noise/wind evaluation).
    /// </summary>
    public void UpdateAnimations(float dt, float gameTime)
    {
        for (int i = 0; i < _objectRuntime.Count; i++)
        {
            var def = _defs[_objects[i].DefIndex];
            if (!def.IsAnimated || def.AnimTotalFrames <= 1) continue;

            var rt = _objectRuntime[i];
            // Freeze animation once dissolve starts — the tree should stop swaying
            // as it dies. Sampling stays at whatever AnimTime was before; the
            // dissolve shader explicitly samples frame 0 anyway.
            if (rt.CorruptionTime > 0f) continue;
            var obj = _objects[i];
            int totalFrames = def.AnimTotalFrames;

            // --- Speed modulation: wind sync + per-instance noise ---
            float speed = 1f;
            float instanceSeed = obj.X * 7.13f + obj.Y * 13.37f;

            // Wind sync: gust envelope that sweeps across the map
            if (def.AnimWindSync > 0f)
            {
                float gust = SampleWind(obj.X, obj.Y, gameTime, out _);
                speed *= 1f - def.AnimWindSync * (1f - gust);
            }

            // Per-instance noise (layered sine waves, unique phase per object)
            if (def.AnimNoise > 0f)
            {
                float n = 0.5f * MathF.Sin(gameTime * 0.7f + instanceSeed)
                        + 0.3f * MathF.Sin(gameTime * 1.3f + instanceSeed * 2.1f)
                        + 0.2f * MathF.Sin(gameTime * 2.9f + instanceSeed * 0.6f);
                float t = (n + 1f) * 0.5f; // [0,1]
                speed *= 1f - def.AnimNoise * (1f - t);
            }

            // Skip all animation logic when speed is effectively zero (tree frozen)
            if (speed < 0.001f)
            {
                _objectRuntime[i] = rt;
                continue;
            }

            // --- Hold: pause at reversal point for a brief cushion ---
            // Number of frame-durations to hold (1 = one frame's worth of time, 2 = two, etc.)
            const int ReversalHoldCount = 1;

            if (rt.AnimHoldTime > 0f)
            {
                rt.AnimHoldTime -= dt;
                if (rt.AnimHoldTime < 0f) rt.AnimHoldTime = 0f;
                _objectRuntime[i] = rt;
                continue;
            }

            // --- Advance animation time ---
            float frameDelta = dt * def.AnimFPS * speed;
            float prevTime = rt.AnimTime;

            if (rt.AnimReversed)
                rt.AnimTime -= frameDelta;
            else
                rt.AnimTime += frameDelta;

            // --- Weighted momentum: consider reversal in turn zones ---
            // Turn zone = first or last 3 frames (or 1/5 of total, whichever is smaller)
            int turnZone = Math.Max(1, Math.Min(3, totalFrames / 5));
            int currentFrame = ((int)rt.AnimTime % totalFrames + totalFrames) % totalFrames;

            bool inTurnZone = currentFrame < turnZone || currentFrame >= totalFrames - turnZone;

            // Only check reversal when we've crossed into a new frame (avoid multiple rolls per frame)
            int prevFrame = ((int)prevTime % totalFrames + totalFrames) % totalFrames;
            if (inTurnZone && currentFrame != prevFrame)
            {
                // ~25% chance to reverse per frame in turn zone.
                // With turn zone of 3 frames, ~42% chance (0.75³) to pass through without reversing.
                rt.AnimRng = XorShift(rt.AnimRng);
                if ((rt.AnimRng % 100) < 25)
                {
                    rt.AnimReversed = !rt.AnimReversed;
                    rt.AnimHoldTime = ReversalHoldCount / MathF.Max(def.AnimFPS, 0.1f);
                }
            }

            // Clamp to valid range — bounce off ends if we overshoot
            if (rt.AnimTime < 0f)
            {
                rt.AnimTime = -rt.AnimTime;
                rt.AnimReversed = false;
                rt.AnimHoldTime = ReversalHoldCount / MathF.Max(def.AnimFPS, 0.1f);
            }
            else if (rt.AnimTime >= totalFrames)
            {
                rt.AnimTime = totalFrames * 2f - rt.AnimTime - 1f;
                if (rt.AnimTime < 0f) rt.AnimTime = 0f;
                rt.AnimReversed = true;
                rt.AnimHoldTime = ReversalHoldCount / MathF.Max(def.AnimFPS, 0.1f);
            }

            _objectRuntime[i] = rt;
        }
    }

    /// <summary>Simple xorshift32 PRNG for per-object reversal rolls.</summary>
    private static uint XorShift(uint state)
    {
        if (state == 0) state = 1;
        state ^= state << 13;
        state ^= state >> 17;
        state ^= state << 5;
        return state;
    }

    /// <summary>
    /// Compute wind gust value and direction at a world position. Shared by animation and debug.
    /// Returns gust intensity [0,1] and wind angle in radians.
    /// </summary>
    public static float SampleWind(float worldX, float worldY, float gameTime, out float windAngle)
    {
        // Wind direction: slow random walk
        windAngle = 0.78f
            + 0.30f * MathF.Sin(gameTime * 0.017f)
            + 0.15f * MathF.Sin(gameTime * 0.0091f)
            + 0.08f * MathF.Sin(gameTime * 0.031f);
        float dirX = MathF.Cos(windAngle);
        float dirY = MathF.Sin(windAngle);

        // Spatial offset along wind direction — perpendicular to the wind = the gust front.
        // Low frequency = wide bands (~15 tiles active, ~50 tiles gap)
        float spatial = (worldX * dirX + worldY * dirY) * 0.10f;

        // Warp the wavefront so it isn't perfectly straight
        float perp = (-worldX * dirY + worldY * dirX);
        spatial += 0.15f * MathF.Sin(perp * 0.06f + gameTime * 0.05f);

        float wave = 0.6f * MathF.Sin(gameTime * 0.0625f + spatial)
                   + 0.3f * MathF.Sin(gameTime * 0.1375f + spatial * 1.4f)
                   + 0.1f * MathF.Sin(gameTime * 0.275f + spatial * 0.5f);

        const float GustThreshold = 0.35f;
        if (wave < GustThreshold) return 0f;
        float gust = (wave - GustThreshold) / (1f - GustThreshold);
        return gust * gust;
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

    /// <summary>Trap target-detection range (world units) — slightly larger than
    /// trap_zap's 1.5 spell range. The actual nearest-enemy query is supplied by
    /// the caller (Game1 passes the canonical _sim.Query scan).</summary>
    public const float TrapDetectRange = 2.5f;

    /// <summary>Update trap cooldowns and find targets. Populates TrapFireEvents.
    /// <paramref name="findNearestEnemy"/>: (point, trapFaction) → nearest enemy
    /// unit index within <see cref="TrapDetectRange"/>, or -1.</summary>
    public void UpdateTraps(float dt, System.Func<Vec2, Faction, int> findNearestEnemy)
    {
        TrapFireEvents.Clear();

        for (int i = 0; i < _objectRuntime.Count; i++)
        {
            var rt = _objectRuntime[i];
            if (!rt.Alive || rt.Collected) continue;
            if (rt.TrapUsesRemaining < 0) continue; // -1 = not a trap
            if (rt.BuildProgress < 1f) continue; // unbuilt traps don't trigger

            var def = _defs[_objects[i].DefIndex];
            if (string.IsNullOrEmpty(def.TrapSpellId)) continue;

            // State machine for trap visuals
            switch (rt.TrapState)
            {
                case TrapVisualState.Hidden:
                {
                    if (rt.TrapCooldownTimer > 0f)
                    {
                        rt.TrapCooldownTimer -= dt;
                        _objectRuntime[i] = rt;
                        continue;
                    }

                    // Find first enemy in range. Faction rule: owner 0 = the
                    // player's (Undead) trap, anything else = Human-built.
                    var trapPos = new Vec2(_objects[i].X, _objects[i].Y);
                    var trapFaction = rt.Owner == 0 ? Faction.Undead : Faction.Human;
                    int target = findNearestEnemy(trapPos, trapFaction);
                    if (target < 0) continue;

                    TrapFireEvents.Add(new TrapFireEvent
                    {
                        ObjectIndex = i, SpellId = def.TrapSpellId,
                        TrapPos = trapPos, TargetUnitIdx = target, TrapOwner = rt.Owner
                    });

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
                        if (def.CollisionRadius > 0)
                            FireCollisionsDirty(i);
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
        // Placement check radius = collision circle + placementRadius (additive
        // spacing), using the same canonical circle that blocks movement and
        // pathfinding (GetCollisionCircle) so placement spacing matches what
        // actually collides.
        var cand = new PlacedObject { DefIndex = (ushort)defIndex, X = x, Y = y, Scale = scale };
        var candCircle = GetCollisionCircle(newDef, in cand);
        float newRadius = candCircle.R + newDef.PlacementRadius;

        // If both collision and placement radius are zero, always allow
        if (newRadius <= 0f) return true;

        for (int i = 0; i < _objects.Count; i++)
        {
            // Skip collected/destroyed objects
            if (i < _objectRuntime.Count && (_objectRuntime[i].Collected || !_objectRuntime[i].Alive))
                continue;

            var obj = _objects[i];
            var existDef = _defs[obj.DefIndex];
            var c = GetCollisionCircle(existDef, in obj);
            float existRadius = c.R + existDef.PlacementRadius;
            if (existRadius <= 0f) continue;

            float dx = candCircle.CX - c.CX;
            float dy = candCircle.CY - c.CY;
            float minDist = newRadius + existRadius;
            if (dx * dx + dy * dy < minDist * minDist)
                return false;
        }
        return true;
    }

    /// <summary>
    /// Stamp env objects into per-tier pathfinding cost fields, inflated by the
    /// tier's reference radius. Movement collision (walls-only _costField) is
    /// left untouched — runtime unit↔object collision goes through ORCA static
    /// obstacles, not the grid.
    ///
    /// Per-tier inflation amount = TerrainCosts.SizeTierRadius[tier] so a unit
    /// of size tier T plans paths with enough clearance for its body:
    ///   tier 0 (small, size ≤2):  +0.50 world units around each obstacle
    ///   tier 1 (medium, size ≤4): +1.25
    ///   tier 2 (large, size >4):  +1.75
    /// </summary>
    public void BakeCollisions(TileGrid grid)
    {
        // Reset tier fields to the walls-only base before stamping env.
        grid.RebuildTieredCostFields();

        for (int i = 0; i < _objects.Count; i++)
            StampObjectCollisionInto(grid, i);
    }

    /// <summary>Dirty-region version of <see cref="BakeCollisions"/>: reset the
    /// tile AABB back to the walls/terrain base cost, then re-stamp every
    /// still-colliding object whose (max-tier-inflated) circle intersects it.
    /// Stamps may write outside the region — harmless, those tiles were
    /// already stamped and stay stamped. O(region + N_objects AABB checks)
    /// instead of O(whole map × tiers + all stamps).</summary>
    public void RebakeCollisionRegion(TileGrid grid, int minTX, int minTY, int maxTX, int maxTY)
    {
        grid.RebuildTieredCostFieldsRegion(minTX, minTY, maxTX, maxTY);

        float maxInflate = TerrainCosts.SizeTierRadius[TerrainCosts.NumSizeTiers - 1];
        for (int i = 0; i < _objects.Count; i++)
        {
            var obj = _objects[i];
            var def = _defs[obj.DefIndex];
            if (def.CollisionRadius <= 0) continue;
            var c = GetCollisionCircle(def, in obj);
            float cr = c.R + maxInflate;
            // Tile-space AABB overlap test (tile t covers [t, t+1)).
            if (c.CX + cr < minTX || c.CX - cr > maxTX + 1 ||
                c.CY + cr < minTY || c.CY - cr > maxTY + 1) continue;
            StampObjectCollisionInto(grid, i); // skips Collected / !Alive internally
        }
    }

    /// <summary>Incremental version of <see cref="BakeCollisions"/> — stamps a
    /// single object's collision into the tier cost fields without touching
    /// existing stamps. Use when an object is added at runtime so we don't
    /// pay O(total_objects) per placement; the full bake is only needed when
    /// objects are removed (which clears the tier fields and requires
    /// re-stamping all remaining objects). The new tree blocks fresh
    /// pathing immediately without rebuilding everything else.</summary>
    public void StampObjectCollisionAt(TileGrid grid, int objectIndex)
    {
        if (objectIndex < 0 || objectIndex >= _objects.Count) return;
        // Defensive: caller may invoke before tier fields exist (fresh grid).
        // Bake-from-scratch falls back to the full path.
        StampObjectCollisionInto(grid, objectIndex);
    }

    private void StampObjectCollisionInto(TileGrid grid, int i)
    {
        if (i < _objectRuntime.Count)
        {
            var rt = _objectRuntime[i];
            if (rt.Collected || !rt.Alive) return;
        }
        var obj = _objects[i];
        var def = _defs[obj.DefIndex];
        if (def.CollisionRadius <= 0) return;

        var c = GetCollisionCircle(def, in obj);
        for (int tier = 0; tier < TerrainCosts.NumSizeTiers; tier++)
            grid.StampImpassableCircleTier(tier, c.CX, c.CY, c.R + TerrainCosts.SizeTierRadius[tier]);
    }

    public IReadOnlyList<EnvironmentObjectDef> Defs => _defs;
    public IReadOnlyList<PlacedObject> Objects => _objects;

    public void LoadTextures(GraphicsDevice device)
    {
        _device = device;
        // Ensure texture list matches defs
        while (_textures.Count < _defs.Count) _textures.Add(null);

        // Phase A (parallel, no GPU): read + decode each PNG to pixels, and apply
        // the def's harmonize recipe in CPU (TransformPixels is thread-safe), so a
        // harmonized def needs no GPU GetData round-trip. DebugLog isn't thread-safe,
        // so failures are captured here and logged serially in phase B.
        var decoded = new (Color[]? pixels, int w, int h, string? warn)[_defs.Count];
        System.Threading.Tasks.Parallel.For(0, _defs.Count, i =>
        {
            if (_textures[i] != null) return;
            var def = _defs[i];
            string path = def.TexturePath;
            if (string.IsNullOrEmpty(path))
            {
                decoded[i] = (null, 0, 0, $"Env def '{def.Id}' has no sprite path — using placeholder");
                return;
            }
            string resolved = Core.GamePaths.Resolve(path);
            if (!System.IO.File.Exists(resolved))
            {
                decoded[i] = (null, 0, 0, $"Env def '{def.Id}' sprite missing: '{path}' — using placeholder");
                return;
            }
            try
            {
                byte[] bytes = System.IO.File.ReadAllBytes(resolved);
                var (pixels, w, h) = Necroking.Render.TextureUtil.DecodePngPremultiplied(bytes);
                if (def.Harmonize != null && def.Harmonize.HasEffect)
                    ColorHarmonizer.TransformPixels(pixels, w, h, def.Harmonize);
                decoded[i] = (pixels, w, h, null);
            }
            catch (Exception ex)
            {
                decoded[i] = (null, 0, 0, $"Env def '{def.Id}' sprite load failed: '{path}' ({ex.Message}) — using placeholder");
            }
        });

        // Phase B (main thread): upload to GPU, or fall back to a placeholder.
        for (int i = 0; i < _defs.Count; i++)
        {
            if (_textures[i] != null) continue;
            var d = decoded[i];
            if (d.pixels != null)
            {
                _textures[i] = Necroking.Render.TextureUtil.CreateTextureFromPixels(device, d.pixels, d.w, d.h);
            }
            else
            {
                if (d.warn != null) Core.DebugLog.Log("startup", "  " + d.warn);
                _textures[i] = GetOrCreatePlaceholder(device);
            }
        }
    }

    /// <summary>
    /// Attempt to load the sprite for a def. Returns a placeholder texture on
    /// missing path / missing file / load failure so the object still renders.
    /// </summary>
    private Texture2D TryLoadDefTexture(GraphicsDevice device, EnvironmentObjectDef def)
    {
        string path = def.TexturePath;
        if (!string.IsNullOrEmpty(path))
        {
            string resolved = Core.GamePaths.Resolve(path);
            if (System.IO.File.Exists(resolved))
            {
                try
                {
                    using var stream = System.IO.File.OpenRead(resolved);
                    var raw = Necroking.Render.TextureUtil.LoadPremultiplied(device, stream);
                    return ApplyDefHarmonize(device, raw, def.Harmonize);
                }
                catch (Exception ex)
                {
                    Core.DebugLog.Log("startup", $"  Env def '{def.Id}' sprite load failed: '{path}' ({ex.Message}) — using placeholder");
                    return GetOrCreatePlaceholder(device);
                }
            }
            Core.DebugLog.Log("startup", $"  Env def '{def.Id}' sprite missing: '{path}' — using placeholder");
        }
        else
        {
            Core.DebugLog.Log("startup", $"  Env def '{def.Id}' has no sprite path — using placeholder");
        }
        return GetOrCreatePlaceholder(device);
    }

    /// <summary>
    /// If the def has an active harmonize recipe, bake a per-pixel color-shifted
    /// copy of <paramref name="raw"/> and return it (disposing the raw). Otherwise
    /// returns <paramref name="raw"/> untouched — zero cost for un-harmonized defs.
    /// </summary>
    private Texture2D ApplyDefHarmonize(GraphicsDevice device, Texture2D raw, HarmonizeSettings? settings)
    {
        if (settings == null || !settings.HasEffect) return raw;
        var harmonized = ColorHarmonizer.HarmonizeTexture(raw, device, settings);
        if (harmonized == null) return raw;
        raw.Dispose();
        return harmonized;
    }

    /// <summary>
    /// Lazily build a magenta/black checker placeholder with a white border so
    /// missing sprites are obvious but still visibly present on the map.
    /// </summary>
    private Texture2D GetOrCreatePlaceholder(GraphicsDevice device)
    {
        if (_placeholderTexture != null) return _placeholderTexture;

        const int size = 32;
        var tex = new Texture2D(device, size, size);
        var pixels = new Color[size * size];
        var magenta = new Color((byte)255, (byte)0, (byte)255, (byte)255);
        var dark = new Color((byte)40, (byte)0, (byte)40, (byte)255);
        var border = new Color((byte)255, (byte)255, (byte)255, (byte)255);
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                bool isBorder = x == 0 || y == 0 || x == size - 1 || y == size - 1;
                bool checker = ((x / 8) + (y / 8)) % 2 == 0;
                pixels[y * size + x] = isBorder ? border : (checker ? magenta : dark);
            }
        }
        tex.SetData(pixels);
        _placeholderTexture = tex;
        return tex;
    }

    public Texture2D? GetDefTexture(int defIdx)
    {
        if (defIdx < 0 || defIdx >= _textures.Count) return null;
        return _textures[defIdx];
    }

    /// <summary>
    /// True when this def's sprite failed to load and the shared placeholder is in use.
    /// Renderers should avoid animation frame slicing on placeholder textures (the 32x32
    /// placeholder divided by N frames produces invisibly thin slivers).
    /// </summary>
    public bool IsUsingPlaceholder(int defIdx)
    {
        if (_placeholderTexture == null) return false;
        if (defIdx < 0 || defIdx >= _textures.Count) return false;
        return _textures[defIdx] == _placeholderTexture;
    }

    /// <summary>Whether this placed object should render horizontally flipped.
    /// Deterministic per-instance (hashed from the object's seed) so it's stable
    /// across frames and reloads, ~50/50 when the def opts in via RandomFlip.
    /// Both the sprite and shadow render paths call this so they stay in sync.</summary>
    public bool ShouldFlipObject(int objIdx)
    {
        if (objIdx < 0 || objIdx >= _objects.Count) return false;
        var obj = _objects[objIdx];
        if (obj.DefIndex >= _defs.Count || !_defs[obj.DefIndex].RandomFlip) return false;
        // Hash the seed's bits so the flip doesn't correlate with the seed's other
        // uses (anim start frame, foragable wiggle).
        uint bits = unchecked((uint)BitConverter.SingleToInt32Bits(obj.Seed));
        bits ^= bits >> 16; bits *= 0x45d9f3bu; bits ^= bits >> 16;
        return (bits & 1u) != 0;
    }

    /// <summary>Get the correct texture for an object based on trap visual state. Returns alpha multiplier.</summary>
    public Texture2D? GetObjectTexture(int objIdx, out float alpha)
    {
        return GetObjectTexture(objIdx, out alpha, out _);
    }

    /// <summary>Get the correct texture for an object, signalling whether the result
    /// is a single-frame override (corrupted / trap / berry sprite) so callers
    /// can skip spritesheet slicing. Returns alpha multiplier (used for trap
    /// fade-out). State precedence lives in <see cref="TryStateOverride"/>.</summary>
    public Texture2D? GetObjectTexture(int objIdx, out float alpha, out bool isOverride)
    {
        alpha = 1f;
        isOverride = false;
        if (objIdx < 0 || objIdx >= _objects.Count) return null;
        var obj = _objects[objIdx];
        var def = _defs[obj.DefIndex];

        if (objIdx < _objectRuntime.Count
            && TryStateOverride(_objectRuntime[objIdx], obj.DefIndex, def, out var stateTex, out alpha))
        {
            isOverride = true;
            return stateTex;
        }
        return GetDefTexture(obj.DefIndex);
    }

    /// <summary>Check each per-instance state for a sprite override in
    /// precedence order: Corrupted → Berry → Trap. First hit wins. Returns
    /// false if the base def texture should be used. New states (Burning,
    /// Frozen, etc.) get added as another branch here.</summary>
    private bool TryStateOverride(PlacedObjectRuntime rt, int defIdx, EnvironmentObjectDef def,
        out Texture2D? tex, out float alpha)
    {
        tex = null;
        alpha = 1f;

        // Corrupted overrides any trap/berry state — corrupted trees stay corrupted.
        if (rt.Corrupted && !string.IsNullOrEmpty(def.CorruptedSprite))
        {
            tex = GetCorruptTexture(defIdx, def);
            if (tex != null) return true;
        }

        // Berry state. The default Berries state uses the base def texture.
        if (def.IsBerryBush)
        {
            string? path = rt.BerryState switch
            {
                BerryState.NoBerry  => def.NoBerrySprite,
                BerryState.Poisoned => def.PoisonedSprite,
                _ => null,
            };
            if (!string.IsNullOrEmpty(path))
            {
                tex = GetOrLoadOverrideTexture(path);
                if (tex != null) return true;
            }
        }

        // Trap visual state. Fade-out is the only state that touches alpha.
        if (rt.TrapUsesRemaining >= 0)
        {
            string? path = null;
            switch (rt.TrapState)
            {
                case TrapVisualState.Triggered: path = def.TrapTriggeredSprite; break;
                case TrapVisualState.Deployed:  path = def.TrapDeployedSprite;  break;
                case TrapVisualState.FadingOut:
                    path = def.TrapDeployedSprite;
                    alpha = MathF.Max(0f, rt.TrapStateTimer / MathF.Max(def.TrapFadeDuration, 0.01f));
                    break;
            }
            if (!string.IsNullOrEmpty(path))
            {
                tex = GetOrLoadOverrideTexture(path);
                if (tex != null) return true;
            }
        }

        return false;
    }

    /// <summary>Get the corrupted-variant texture for an object's def, loading it
    /// on demand. Returns null if the def has no CorruptedSprite or the load failed.
    /// Used by the dissolve renderer to draw a transitioning tree.</summary>
    public Texture2D? GetCorruptedTexture(int objIdx)
    {
        if (objIdx < 0 || objIdx >= _objects.Count) return null;
        int defIdx = _objects[objIdx].DefIndex;
        var def = _defs[defIdx];
        if (string.IsNullOrEmpty(def.CorruptedSprite)) return null;
        return GetCorruptTexture(defIdx, def);
    }

    /// <summary>Corrupted-variant texture for a def, applying the def's
    /// HarmonizeCorrupt recipe if active. Falls back to the shared (path-keyed)
    /// override texture when no corrupt harmonize is set — zero extra cost.
    /// Harmonized results are cached per def index (paths can be shared across
    /// defs with different recipes).</summary>
    private Texture2D? GetCorruptTexture(int defIdx, EnvironmentObjectDef def)
    {
        var settings = def.HarmonizeCorrupt;
        if (settings == null || !settings.HasEffect || string.IsNullOrEmpty(def.CorruptedSprite))
            return GetOrLoadOverrideTexture(def.CorruptedSprite);

        if (_corruptHarmonized.TryGetValue(defIdx, out var cached)) return cached;

        var raw = GetOrLoadOverrideTexture(def.CorruptedSprite);
        Texture2D? result = raw;
        if (raw != null && _device != null)
        {
            var h = ColorHarmonizer.HarmonizeTexture(raw, _device, settings);
            if (h != null) { result = h; _corruptHarmonizedOwned.Add(defIdx); }
        }
        _corruptHarmonized[defIdx] = result;
        return result;
    }

    /// <summary>Drop a def's cached harmonized corrupt texture so it re-bakes on
    /// next request (after the recipe or source changes in the editor).</summary>
    private void InvalidateCorruptHarmonized(int defIdx)
    {
        if (_corruptHarmonizedOwned.Remove(defIdx)
            && _corruptHarmonized.TryGetValue(defIdx, out var owned) && owned != null)
            owned.Dispose();
        _corruptHarmonized.Remove(defIdx);
    }

    private Texture2D? GetOrLoadOverrideTexture(string path)
        => _overrideTextures.GetOrLoad(_device, path);

    /// <summary>
    /// Reload texture for a single def (e.g. after changing TexturePath in the editor).
    /// </summary>
    public void ReloadDefTexture(int defIdx)
    {
        if (_device == null || defIdx < 0 || defIdx >= _defs.Count) return;
        while (_textures.Count <= defIdx) _textures.Add(null);
        // Don't dispose the shared placeholder — it's reused across defs
        var existing = _textures[defIdx];
        if (existing != null && existing != _placeholderTexture)
            existing.Dispose();
        _textures[defIdx] = TryLoadDefTexture(_device, _defs[defIdx]);
        // Re-bake the harmonized corrupt variant too (recipe may have changed).
        InvalidateCorruptHarmonized(defIdx);
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
