using System;
using System.Collections.Generic;
using Necroking.Core;
using Necroking.Data;
using Necroking.Game;
using Necroking.Movement;
using Necroking.Spatial;
using Necroking.World;

namespace Necroking.GameSystems;

public class Corpse
{
    public Vec2 Position;
    public float DissolveTimer;
    public bool Dissolving;
    public bool ConsumedBySummon;
    public float Age;
    public int CorpseID;
    public UnitType UnitType = UnitType.Skeleton;
    public string UnitDefID = "";
    public float FacingAngle = 90f;
    public float SpriteScale = 1f;
    public uint DraggedByUnitID = GameConstants.InvalidUnit;

    // Editor-placed corpses (and other pre-existing-at-load bodies) should appear
    // already collapsed on the final death frame, NOT replay the death animation
    // when the map starts. The renderer snaps these straight to the end of Death.
    public bool PreSettled;

    // Bagging state
    public bool Bagged;
    public float BaggingProgress;
    public uint BaggedByUnitID = GameConstants.InvalidUnit;

    // Lerp anchor for pickup/putdown animation
    public Vec2 LerpStartPos;

    // Frozen carry orientation: the exact resolved death-sprite angle + flip the
    // corpse was last shown at while carried. -1 = never carried. Used so a
    // dropped corpse keeps the precise pose it had in-hand (no re-resolve jump),
    // and so the carried/putdown/settled renders all agree.
    public int CarryDisplayAngle = -1;
    public bool CarryDisplayFlip;

    // Physics arc — corpse continues flying if unit died mid-knockback.
    // GravityMul/DragMul inherit from the unit's PhysicsBody (e.g. trample
    // launches use 0.3× gravity / 0.5× drag for visible arcs); without these
    // the corpse drops with full gravity and the dramatic flight collapses.
    public bool InPhysics;
    public float Z;
    public Vec2 VelocityXY;
    public float VelocityZ;
    public float GravityMul = 1f;
    public float DragMul = 1f;
}

public class DamageEvent
{
    /// <summary>Default world-unit height above the unit's position where the floating
    /// damage number renders. Matches a typical humanoid torso height; used when the
    /// caller doesn't have a per-unit height to offer.</summary>
    public const float DefaultHeight = 1.5f;

    public Vec2 Position;
    public int Damage;
    public float Height;
    public bool IsPoison;
    public bool IsFatigue;

    /// <summary>Build a floating-damage-number event. Pass a per-unit `height` only
    /// when you already have the correct value on hand (e.g. from a UnitDef's
    /// SpriteWorldHeight); otherwise leave it at DefaultHeight.</summary>
    public static DamageEvent Create(Vec2 position, int damage,
        float height = DefaultHeight, bool isPoison = false, bool isFatigue = false) => new()
    {
        Position = position, Damage = damage, Height = height,
        IsPoison = isPoison, IsFatigue = isFatigue,
    };
}

public struct SoulOrb
{
    public Vec2 Position;
    public float Timer;
    public float Lifetime;
}

public class Simulation
{
    private const float MeleeRangeBase = 0.8f;
    private const float CombatTickInterval = 2.0f;
    private const float Rad2Deg = 57.29577951f;

    private TileGrid _grid = new();
    private FlowFieldManager _flowFields = new();
    private Pathfinder _pathfinder = new();
    private Quadtree _quadtree = new();
    // Spatial index over env objects (trees, rocks, etc.) — rebuilt only on
    // OnCollisionsDirty, so ORCA static-obstacle queries are free per-frame.
    private readonly EnvSpatialIndex _envIndex = new();
    private UnitArrays _units = new();
    private ProjectileManager _projectiles = new();
    private LightningSystem _lightning = new();
    private PoisonCloudSystem _poisonClouds = new();
    private PhysicsSystem _physics = new();
    private MagicGlyphSystem _magicGlyphs = new();
    private HordeSystem _horde = new();
    private NecromancerState _necroState = new();
    private CombatLog _combatLog = new();
    private readonly List<Corpse> _corpses = new();
    private readonly List<DamageEvent> _damageEvents = new();
    private readonly List<SoulOrb> _soulOrbs = new();
    private GameData? _gameData;
    private EnvironmentSystem? _envSystem;
    private WallSystem? _wallSystem;
    private float _gameTime;
    private uint _frameNumber;
    private int _necromancerIdx = -1;
    // Cached per-tick so AIContext construction in the archetype loop doesn't
    // chase GameData.Settings 600+ times per frame.
    private bool _amortizedAI;
    private int _aiUpdateInterval = 6;
    private float _harassmentDecayTimer = CombatTickInterval;
    private const float FatigueRegenInterval = 3.0f; // 1 fatigue point recovered per this many seconds
    private float _fatigueRegenTimer = FatigueRegenInterval;
    private const float UnconsciousFatigueRegen = 5.0f;   // fatigue recovered per interval while collapsed (manual: ~5/turn)
    private const float UnconsciousWakeThreshold = 50.0f; // wake once rested below this (hysteresis vs the 100 collapse point)

    // --- Morale / rout ---
    private const float MoraleCheckInterval = 1.5f;     // how often in-combat units roll morale
    private float _moraleCheckTimer = MoraleCheckInterval;
    private const float MoraleBaseThreshold = 10f;      // baseline rout difficulty
    private const float MoraleRoutMinDuration = 4f;     // min seconds a broken unit keeps fleeing
    private const float ArmyRoutHpFraction = 0.25f;     // a faction below this fraction of peak HP routs en masse
    private const float MindlessMoraleThreshold = 50;   // Morale >= this (or Undead faction) = fearless, never routs
    private const float MoraleLocalRadius = 8f;         // radius for local outnumbering + rally checks
    private const float RoutFleeDistance = 18f;         // how far a routing unit aims to flee
    private readonly float[] _factionCurHP = new float[3];
    private readonly float[] _factionPeakHP = new float[3];
    private readonly System.Collections.Generic.List<uint> _moraleScratch = new();
    private int _nextCorpseID;
    private readonly List<PendingZombieRaise> _pendingZombieRaises = new();
    private readonly PlayerResources _playerResources = new() { Essence = 100, MaxEssence = 100 };

    // Public accessors
    public TileGrid Grid => _grid;
    public UnitArrays Units => _units;
    public UnitArrays UnitsMut => _units;
    public Quadtree Quadtree => _quadtree;
    public NecromancerState NecroState => _necroState;
    public IReadOnlyList<Corpse> Corpses => _corpses;
    public List<Corpse> CorpsesMut => _corpses;

    public int FindCorpseIndexByID(int corpseID)
    {
        for (int i = 0; i < _corpses.Count; i++)
            if (_corpses[i].CorpseID == corpseID) return i;
        return -1;
    }

    public Corpse? FindCorpseByID(int corpseID)
    {
        int idx = FindCorpseIndexByID(corpseID);
        return idx >= 0 ? _corpses[idx] : null;
    }

    /// <summary>Convert a living unit into a corpse in place and remove it from the
    /// unit array. Mirrors the corpse-creation in the death path (see UpdateCombat)
    /// minus the combat/physics/zombie state — used for editor-placed corpses so a
    /// dead body appears exactly as one that died naturally. Returns the new corpse.</summary>
    public Corpse? SpawnCorpseFromUnit(int unitIdx)
    {
        if (unitIdx < 0 || unitIdx >= _units.Count) return null;
        var corpse = new Corpse
        {
            Position = _units[unitIdx].Position,
            UnitType = _units[unitIdx].Type,
            UnitDefID = _units[unitIdx].UnitDefID,
            FacingAngle = _units[unitIdx].FacingAngle,
            SpriteScale = _units[unitIdx].SpriteScale,
            CorpseID = _nextCorpseID++,
            PreSettled = true, // appear already-dead, don't replay the death anim
        };
        _corpses.Add(corpse);
        _units.RemoveUnit(unitIdx);
        // Keep the necromancer index valid across the swap-and-pop remove.
        if (_necromancerIdx == unitIdx) _necromancerIdx = -1;
        else if (_necromancerIdx == _units.Count) _necromancerIdx = unitIdx;
        return corpse;
    }
    public IReadOnlyList<DamageEvent> DamageEvents => _damageEvents;
    public List<DamageEvent> DamageEventsMut => _damageEvents;
    public void AddDamageEvent(DamageEvent evt) => _damageEvents.Add(evt);
    public IReadOnlyList<SoulOrb> SoulOrbs => _soulOrbs;
    public ProjectileManager Projectiles => _projectiles;
    public LightningSystem Lightning => _lightning;
    public PoisonCloudSystem PoisonClouds => _poisonClouds;
    public PhysicsSystem Physics => _physics;
    public MagicGlyphSystem MagicGlyphs => _magicGlyphs;
    public HordeSystem Horde => _horde;
    public CombatLog CombatLog => _combatLog;
    public float GameTime => _gameTime;
    public GameData? GameData => _gameData;
    public int NecromancerIndex => _necromancerIdx;
    public bool NecroRunning => _necroRunning;
    public Pathfinder Pathfinder => _pathfinder;
    public EnvironmentSystem? EnvironmentSystem => _envSystem;
    public WallSystem? WallSystem => _wallSystem;
    public List<PendingZombieRaise> PendingZombieRaises => _pendingZombieRaises;
    public PlayerResources PlayerResources => _playerResources;

    // Anim metadata. Populated by Game1 once the atlases load so AI can look up
    // effect_time_ms timings (e.g. pounce needs JumpTakeoff's effect_time to know
    // how many ms of ground travel to allow before liftoff).
    private Dictionary<string, Render.AnimationMeta>? _animMeta;
    public void SetAnimMeta(Dictionary<string, Render.AnimationMeta> animMeta)
    {
        _animMeta = animMeta;
        Core.DebugLog.Log("jump", $"[SetAnimMeta] {animMeta.Count} entries");
    }

    public void Init(int gridWidth, int gridHeight, GameData? gameData = null)
    {
        // Fresh perf log per session so the file doesn't grow unbounded across runs.
        DebugLog.Clear("perf");
        _gameData = gameData;
        if (gameData?.Buffs != null)
            _physics.Init(gameData.Buffs);
        _grid.Init(gridWidth, gridHeight);
        _grid.RebuildCostField();
        _flowFields.Init(_grid);
        _pathfinder.Init(_grid);
        _units.Clear();
        _corpses.Clear();
        _damageEvents.Clear();
        _projectiles.Clear();
        _lightning.Clear();
        _poisonClouds.Clear();
        _magicGlyphs.Clear();
        _combatLog.Clear();
        _gameTime = 0f;
        _frameNumber = 0;
        _nextCorpseID = 0;
        _necromancerIdx = -1;
        _necroState = new NecromancerState();
        _harassmentDecayTimer = CombatTickInterval;
        _fatigueRegenTimer = FatigueRegenInterval;

        if (gameData?.Settings.Horde != null)
            _horde.Init(gameData.Settings.Horde);
    }

    public void SetEnvironmentSystem(EnvironmentSystem? es) { _envSystem = es; }
    public void SetWallSystem(WallSystem? ws) { _wallSystem = ws; }
    public void SetTriggerSystem(TriggerSystem? ts) { _triggerSystem = ts; }
    private TriggerSystem? _triggerSystem;
    public void SetNecromancerIndex(int idx) { _necromancerIdx = idx; }

    /// <summary>Reference to the per-game skill-book state. When non-null,
    /// SpawnUnitByID consults SkillBookState.IntrinsicBuffs and applies any
    /// buff whose required tag set matches the spawning unit's UnitDef.Tags.
    /// Null in scenario / test paths that don't run the full skill system.</summary>
    private SkillBookState? _skillBookState;
    public SkillBookState? SkillBook => _skillBookState;
    public void SetSkillBook(SkillBookState state)
    {
        _skillBookState = state;
        // Make the skill book share the central player-event tracker so its
        // "event"-cost checks read the same counts every system tallies into.
        state?.UseEventTracker(PlayerEvents);
    }

    /// <summary>Central per-game tally of things that happen to the player
    /// (kills, casts, corpses eaten, ...). The canonical home for "record a
    /// player event" — call <c>PlayerEvents.Tally(PlayerEventTracker.Keys.X)</c>
    /// from anywhere with a sim. Always non-null (unlike <see cref="SkillBook"/>),
    /// so it's safe to tally in scenario/test paths too.</summary>
    public PlayerEventTracker PlayerEvents { get; } = new();

    /// <summary>Wall-clock milliseconds the most recent Tick() took. Written every
    /// Tick; readable from scenarios/tests to chart per-tick cost. Not intended to
    /// drive game logic — it's a diagnostic probe.</summary>
    public double LastTickMs { get; private set; }

    /// <summary>Per-subsystem wall-clock milliseconds for the most recent Tick. Keys
    /// are phase names: "quadtree", "ai", "movement", "physics", "horde_tick",
    /// "horde_states", "combat", "projectiles", "lightning", "clouds", "corpses",
    /// "misc". Diagnostic only.</summary>
    public readonly Dictionary<string, double> LastPhaseMs = new();
    private readonly System.Diagnostics.Stopwatch _tickStopwatch = new();
    private readonly System.Diagnostics.Stopwatch _phaseStopwatch = new();

    private void PhaseStart() => _phaseStopwatch.Restart();
    private void PhaseEnd(string name)
    {
        _phaseStopwatch.Stop();
        LastPhaseMs[name] = _phaseStopwatch.Elapsed.TotalMilliseconds;
    }

    public void Tick(float dt)
    {
        _tickStopwatch.Restart();
        Necroking.World.Pathfinder.DiagCallsThisTick = 0;
        Necroking.World.Pathfinder.DiagTotalMsThisTick = 0;
        Necroking.World.Pathfinder.DiagDijkstraInvocations = 0;
        Necroking.World.Pathfinder.DiagFlowCacheHits = 0;
        Necroking.World.Pathfinder.DiagFlowCacheMisses = 0;
        Necroking.World.Pathfinder.DiagImagChunkComputes = 0;
        Necroking.World.Pathfinder.DiagImagChunkRecomputes = 0;
        Necroking.World.Pathfinder.DiagImagChunkMs = 0;
        Necroking.World.Pathfinder.DiagMissNewKey = 0;
        Necroking.World.Pathfinder.DiagMissEvicted = 0;
        Necroking.World.Pathfinder.DiagCacheEvictions = 0;
        Necroking.World.Pathfinder.DiagCacheSize = _pathfinder.FlowCacheSize;
        Necroking.World.Pathfinder.DiagMissTile = 0;
        Necroking.World.Pathfinder.DiagMissBorder = 0;
        Necroking.World.Pathfinder.DiagMissMultiBorder = 0;
        _frameNumber++;
        _gameTime += dt;
        _damageEvents.Clear();

        // Clear per-frame flags (HitReacting cleared after AI, see below) and tick
        // the flinch timers (set later this frame in UpdateCombat / projectiles;
        // ticking here means they show for at least the frame after the hit).
        for (int i = 0; i < _units.Count; i++)
        {
            _units[i].Dodging = false;
            _units[i].BlockReacting = false;
            if (_units[i].HitReactTimer > 0f)
                _units[i].HitReactTimer = MathF.Max(0f, _units[i].HitReactTimer - dt);
            if (_units[i].FlinchRefractoryTimer > 0f)
                _units[i].FlinchRefractoryTimer = MathF.Max(0f, _units[i].FlinchRefractoryTimer - dt);
        }

        // Update mana and cooldowns. BonusManaRegen is the dynamic add (e.g.
        // Death Fog Consumption while in fog) refreshed by Game1 each tick.
        // Buff overrides (e.g. god-mode Add MaxMana / Add ManaRegen) are looked
        // up against the necromancer unit's active buffs each tick — applying
        // and removing the buff transparently bumps both the cap and the
        // refill rate without ever mutating the base NecromancerState fields.
        float maxManaEff = _necroState.MaxMana
            + (_necromancerIdx >= 0 ? BuffSystem.SumExtraAdd(_units, _necromancerIdx, "MaxMana") : 0f);
        float regenEff = _necroState.ManaRegen + _necroState.BonusManaRegen
            + (_necromancerIdx >= 0 ? BuffSystem.SumExtraAdd(_units, _necromancerIdx, "ManaRegen") : 0f);
        _necroState.Mana = MathF.Min(maxManaEff, _necroState.Mana + regenEff * dt);
        // Effective cooldown rate = base × buff modifiers (god mode multiplies it by
        // 10). Looked up live so applying/removing the buff speeds up / restores the
        // recharge without touching the base CooldownRate.
        float cooldownRate = _necromancerIdx >= 0
            ? BuffSystem.GetModifiedExtra(_units, _necromancerIdx, "CooldownRate", _necroState.CooldownRate)
            : _necroState.CooldownRate;
        _necroState.TickCooldowns(dt, cooldownRate);

        // Knockdown recovery rolls — runs BEFORE BuffSystem.TickBuffs so a successful
        // recovery roll can zero the buff's duration in the same tick it gets decremented.
        UpdateKnockdownRecovery(dt);

        // Morale checks (amortized): units that are losing badly or locally swarmed
        // may break and rout. Routing movement itself is steered in the AI pass.
        UpdateMorale(dt);

        // Tick buffs
        BuffSystem.TickBuffs(_units, dt, _gameData?.Buffs);
        for (int i = 0; i < _units.Count; i++)
        {
            float baseSpeed = _units[i].ActiveBuffs.Count > 0
                ? BuffSystem.GetModifiedStat(_units, i, BuffStat.CombatSpeed, _units[i].Stats.CombatSpeed)
                : _units[i].Stats.CombatSpeed;
            // Bake in the unit's persisted MoveEffort so the velocity cap stays
            // correct even for AI that doesn't re-issue SetEffort every frame
            // (amortized horde followers; early-return handler states). Without
            // this, resetting MaxSpeed to base here clobbered a Jog/Sprint intent
            // on skipped frames — the root cause of "minions follow too slowly".
            // This makes effort→speed a single source of truth (shared with the
            // AI's SetEffort via EffortMultiplier). Only Hurry/Sprint need the def
            // lookup; Walk/Normal is 1× so the common case stays a field read.
            var eff = _units[i].MoveEffort;
            if (eff == Movement.MoveEffort.Hurry || eff == Movement.MoveEffort.Sprint)
                baseSpeed *= AI.SubroutineSteps.EffortMultiplier(_gameData?.Units.Get(_units[i].UnitDefID), eff);
            _units[i].MaxSpeed = baseSpeed;
        }

        // Standup/recovery timing now handled by IncapState inside BuffSystem.TickBuffs

        // Clear per-tick dodge flags (HitReacting/BlockReacting are cleared later
        // after AI reads them; Dodging is single-tick and safe to reset here).
        for (int i = 0; i < _units.Count; i++)
            _units[i].Dodging = false;

        // Harassment decay
        _harassmentDecayTimer -= dt;
        if (_harassmentDecayTimer <= 0f)
        {
            _harassmentDecayTimer += CombatTickInterval;
            for (int i = 0; i < _units.Count; i++)
                if (_units[i].Harassment > 0)
                    _units[i].Harassment = (_units[i].Harassment + 1) / 2;
        }

        // Fatigue regen: every FatigueRegenInterval seconds, every unit recovers
        // fatigue (clamped at 0). Melee swings add Encumbrance in ResolveMeleeAttack.
        // Collapsed (unconscious) units recover faster and stay down until rested
        // below the wake threshold (manual p.61).
        _fatigueRegenTimer -= dt;
        if (_fatigueRegenTimer <= 0f)
        {
            _fatigueRegenTimer += FatigueRegenInterval;
            for (int i = 0; i < _units.Count; i++)
            {
                if (!_units[i].Alive) continue;
                float regen = _units[i].Unconscious ? UnconsciousFatigueRegen : 1f;
                if (_units[i].Fatigue > 0f)
                    _units[i].Fatigue = MathF.Max(0f, _units[i].Fatigue - regen);
                UpdateUnconsciousState(i);
            }
        }

        // Rebuild quadtree
        PhaseStart(); RebuildQuadtree(); PhaseEnd("quadtree");

        // Tick potion effects before AI so poison HitReacting is visible to flee logic
        PhaseStart();
        PotionSystem.TickPotionEffects(_units, _damageEvents, dt);
        PhaseEnd("potions");

        // Table crafting timer + completion. Runs after AI-derived state from the
        // previous frame is visible (units' Routine/Subroutine fields drive whether
        // a table's channeler is "actually channeling" right now).
        if (_envSystem != null && _gameData != null)
        {
            PhaseStart();
            TableCraftingSystem.Tick(this, _envSystem, _gameData, dt);
            PhaseEnd("table_craft");
        }

        // Horde
        PhaseStart(); _horde.Tick(dt, _units, _necromancerIdx); PhaseEnd("horde_tick");

        // Push optional budgeted-pathfinding settings each tick so edits in the
        // settings window take effect live. BeginTick resets the per-tick Dijkstra
        // budget and drains the deferred queue (priority-sorted) up to that budget,
        // before any AI tries to read fresh flow fields.
        if (_gameData != null)
        {
            _pathfinder.BudgetedPathfinding = _gameData.Settings.Performance.BudgetedPathfinding;
            _pathfinder.DijkstraBudgetMsPerTick = _gameData.Settings.Performance.DijkstraBudgetMsPerTick;
            _amortizedAI = _gameData.Settings.Performance.AmortizedAI;
            _aiUpdateInterval = Math.Max(1, _gameData.Settings.Performance.AIUpdateInterval);
            // Live-link gravity from settings — UI changes apply immediately to
            // both unit-physics integration and corpse-arc continuation.
            _physics.Gravity = _gameData.Settings.General.Gravity;
        }
        _pathfinder.BeginTick(_frameNumber);

        // Core subsystems
        PhaseStart(); UpdateAI(dt); PhaseEnd("ai");

        // Clear HitReacting AFTER AI has read it — this ensures flags set between frames
        // (e.g. spell AoE from Game1.Update) persist until the next AI tick sees them
        // Decay floating action labels in the same pass.
        for (int i = 0; i < _units.Count; i++)
        {
            _units[i].HitReacting = false;
            if (_units[i].ActionLabelTimer > 0f)
            {
                _units[i].ActionLabelTimer -= dt;
                if (_units[i].ActionLabelTimer <= 0f)
                {
                    _units[i].ActionLabelTimer = 0f;
                    _units[i].ActionLabel = "";
                }
            }
        }

        // Trample-dodge: brief snappy hop to a free tile (set by trample miss).
        // Ticked BEFORE UpdateMovement so the position interpolation happens
        // before ORCA / wall-collision passes; the dodging unit's body moves
        // smoothly from start to end over DodgeDuration.
        for (int i = 0; i < _units.Count; i++)
        {
            if (_units[i].DodgeTimer <= 0f) continue;
            _units[i].DodgeTimer -= dt;
            if (_units[i].DodgeTimer <= 0f)
            {
                _units[i].Position = _units[i].DodgeEndPos;
                _units[i].DodgeTimer = 0f;
                _units[i].Velocity = Vec2.Zero;
            }
            else
            {
                float remaining = _units[i].DodgeTimer;
                float total = _units[i].DodgeDuration;
                float t = total > 0.0001f ? 1f - remaining / total : 1f;
                _units[i].Position = Vec2.Lerp(_units[i].DodgeStartPos, _units[i].DodgeEndPos, t);
            }
        }

        // Trample: write charge velocity + scan for trampled victims BEFORE
        // UpdateMovement so velocity writes take effect this frame. Transit
        // damage + knockback impulse are applied here; wall collision during
        // UpdateMovement can still stop the charge (TickCharge checks position
        // movement next frame).
        PhaseStart(); GameSystems.TrampleSystem.TickAll(this, dt); PhaseEnd("trample");
        // Slow units in water (and other costly terrain). Applied after AI's
        // SetEffort/potion mults so ORCA's velocity cap sees the final value.
        ApplyTerrainSpeedModulation();
        PhaseStart(); UpdateMovement(dt); PhaseEnd("movement");
        PhaseStart(); _physics.Update(dt, _units); PhaseEnd("physics");
        PhaseStart(); _horde.UpdateStates(_units, _quadtree, _necromancerIdx, dt); PhaseEnd("horde_states");
        PhaseStart(); UpdateFacingAngles(dt); PhaseEnd("facing");
        PhaseStart(); UpdateCombat(dt); PhaseEnd("combat");

        // Tick pending zombie raises
        PotionSystem.TickZombieRaises(_pendingZombieRaises, dt, (defId, pos, facing, scale) =>
        {
            // Resolve zombie type from the dead unit's def
            string spawnId = "skeleton"; // fallback
            if (_gameData != null)
            {
                var unitDef = _gameData.Units.Get(defId);
                if (unitDef != null && !string.IsNullOrEmpty(unitDef.ZombieTypeID))
                {
                    // ZombieTypeID can be a direct unit def or a unit group — resolve it
                    if (_gameData.Units.Get(unitDef.ZombieTypeID) != null)
                        spawnId = unitDef.ZombieTypeID;
                    else
                        spawnId = _gameData.UnitGroups.PickRandom(unitDef.ZombieTypeID) ?? "skeleton";
                }
            }
            // Canonical raise-into-horde path: wires the HordeMinion archetype so
            // the zombie respects the leash (see SpawnZombieMinion).
            int idx = SpawnZombieMinion(spawnId, pos);
            if (idx >= 0)
            {
                _units[idx].FacingAngle = facing;
                BuffSystem.BeginReanimationRise(_units, idx);
                _units[idx].SpawnPosition = pos;
            }
        });

        // Projectiles (with quadtree collision, pass corpses for potion corpse-targeting)
        PhaseStart();
        _projectiles.Update(dt, _units, _quadtree, _corpses);
        foreach (var hit in _projectiles.Hits)
        {
            // Potion projectiles apply effects instead of damage
            if (!string.IsNullOrEmpty(hit.PotionID))
            {
                if (_gameData != null)
                {
                    // Direct corpse hit — raise it
                    if (hit.CorpseHitIdx >= 0 && hit.CorpseHitIdx < _corpses.Count)
                    {
                        var potion = _gameData.Potions.Get(hit.PotionID);
                        if (potion != null)
                        {
                            var corpse = _corpses[hit.CorpseHitIdx];
                            _pendingZombieRaises.Add(new PendingZombieRaise
                            {
                                Position = corpse.Position,
                                UnitDefID = corpse.UnitDefID,
                                FacingAngle = corpse.FacingAngle,
                                SpriteScale = corpse.SpriteScale,
                                Timer = 1.0f
                            });
                            corpse.Dissolving = true;
                            corpse.ConsumedBySummon = true;
                        }
                    }
                    else
                    {
                        Vec2 impactPos = hit.UnitIdx >= 0 && hit.UnitIdx < _units.Count ? _units[hit.UnitIdx].Position : hit.ImpactPos;
                        PotionSystem.ApplyPotionEffect(hit.PotionID, _gameData.Potions, _gameData.Buffs,
                            hit.UnitIdx, _units, hit.OwnerFaction, _pendingZombieRaises, _corpses, impactPos,
                            _damageEvents);
                    }
                }
                continue;
            }
            var spellDef = (_gameData != null && !string.IsNullOrEmpty(hit.SpellID))
                ? _gameData.Spells.Get(hit.SpellID) : null;

            // Physics knockback before damage — units enter physics first so if
            // the damage kills them, the corpse inherits the knockback arc
            if (spellDef != null && spellDef.KnockbackForce > 0f)
            {
                float kbRadius = spellDef.KnockbackRadius > 0f ? spellDef.KnockbackRadius : hit.AoeRadius;
                _physics.ApplyRadialImpulse(_units, hit.ImpactPos, kbRadius,
                    spellDef.KnockbackForce, spellDef.KnockbackUpward, hit.OwnerFaction);
            }

            if (hit.UnitIdx >= 0 && hit.UnitIdx < _units.Count && _units[hit.UnitIdx].Alive)
            {
                // Resolve the casting unit so the hit is attributed to them
                // (LastAttackerID) — drives flee/aggro reactions and the skill-book
                // kill tally (monster_kill / human_kill), exactly like a melee blow.
                int casterIdx = UnitUtil.ResolveUnitIndex(_units, hit.OwnerID);

                // Magic-resistance gate: an MR-checked spell projectile only damages
                // a target whose MR its caster penetrates.
                bool affects = true;
                if (spellDef != null && spellDef.ChecksMagicResist)
                {
                    affects = GameSystems.SpellPenetration.Penetrates(_units, hit.UnitIdx,
                        GameSystems.SpellPenetration.Compute(_gameData, _units, casterIdx, spellDef));
                }
                if (affects)
                    DamageSystem.Apply(_units, hit.UnitIdx, hit.Damage,
                        GameSystems.DamageType.Physical, GameSystems.DamageFlags.ArmorNegating,
                        _damageEvents, casterIdx);
            }
        }
        PhaseEnd("projectiles");

        // Lightning
        PhaseStart();
        var lightningDmg = new List<LightningDamage>();
        _lightning.Update(dt, lightningDmg, _quadtree, _units);
        foreach (var ld in lightningDmg)
            if (ld.UnitIdx >= 0 && ld.UnitIdx < _units.Count)
                DamageSystem.Apply(_units, ld.UnitIdx, ld.Damage,
                    GameSystems.DamageType.Physical, GameSystems.DamageFlags.ArmorNegating,
                    _damageEvents, UnitUtil.ResolveUnitIndex(_units, ld.OwnerID));
        PhaseEnd("lightning");

        // Poison clouds
        PhaseStart();
        _poisonClouds.Update(dt, _units, _quadtree, _corpses, _damageEvents,
            _gameData?.Buffs);
        PhaseEnd("clouds");

        // Corpse Eater AI — passive eat by wolves/bears once corpses age past
        // the threshold. Runs before RemoveDeadUnits / UpdateCorpses so a
        // corpse marked for consumption this frame still dissolves on schedule.
        PhaseStart();
        AI.CorpseEatAI.Update(this, dt);
        PhaseEnd("corpse_eat");

        // Remove dead units
        PhaseStart();
        RemoveDeadUnits();
        // Update corpses
        UpdateCorpses(dt);
        _flowFields.EvictIfNeeded();
        // Age out unused pathfinder flow fields. An entry stays hot as long as any
        // unit is still reading it (FrameAccessed is bumped on every cache hit).
        // Stale == "no unit has hit this in 10 seconds" — safe to drop because
        // recomputing is the same cost whether the field was thrown out yesterday
        // or right before it was needed again.
        _pathfinder.EvictStaleFlowFields(_frameNumber, 600);
        PhaseEnd("cleanup");

        _tickStopwatch.Stop();
        LastTickMs = _tickStopwatch.Elapsed.TotalMilliseconds;

        // Perf-spike logger: whenever a tick exceeds the threshold, dump a single-line
        // phase breakdown to log/perf.log. Baseline ticks cost <1ms on an idle map, so
        // a 3ms threshold triggers only during real work (large summons, combat bursts,
        // pathfinder cache misses) and never spams in the quiescent state. Tag format:
        //   gt=<gameTime>  u=<unitCount>  t=<tickMs>  <phase=ms>... pf={calls,hits,miss,imag}
        if (LastTickMs >= 3.0)
        {
            var sb = new System.Text.StringBuilder(256);
            sb.Append($"gt={_gameTime:F2} u={_units.Count} t={LastTickMs:F2}ms");
            // List phases in fixed order so columns align in the log.
            string[] phaseOrder = {
                "ai", "ai_archetype", "ai_legacy", "ai_awareness",
                "movement", "physics", "horde_tick", "horde_states",
                "combat", "facing", "quadtree", "potions",
                "projectiles", "lightning", "clouds", "cleanup",
                "pathfinder",
            };
            foreach (var p in phaseOrder)
            {
                if (LastPhaseMs.TryGetValue(p, out double v) && v >= 0.2)
                    sb.Append($"  {p}={v:F2}");
            }
            sb.Append($"  pf={{calls:{Necroking.World.Pathfinder.DiagCallsThisTick}")
              .Append($",hits:{Necroking.World.Pathfinder.DiagFlowCacheHits}")
              .Append($",miss:{Necroking.World.Pathfinder.DiagFlowCacheMisses}")
              .Append($"(tile:{Necroking.World.Pathfinder.DiagMissTile}")
              .Append($",bord:{Necroking.World.Pathfinder.DiagMissBorder}")
              .Append($",mult:{Necroking.World.Pathfinder.DiagMissMultiBorder}")
              .Append($",new:{Necroking.World.Pathfinder.DiagMissNewKey}")
              .Append($",evict:{Necroking.World.Pathfinder.DiagMissEvicted})")
              .Append($",imag:{Necroking.World.Pathfinder.DiagImagChunkComputes}")
              .Append($"+{Necroking.World.Pathfinder.DiagImagChunkRecomputes}")
              .Append($",cache:{Necroking.World.Pathfinder.DiagCacheSize}")
              .Append($",cevict:{Necroking.World.Pathfinder.DiagCacheEvictions}")
              .Append($",dj_ms:{_pathfinder.DiagDijkstraMsThisTick:F2}")
              .Append($",pend:{_pathfinder.DiagPendingRequestCount}")
              .Append($",stale:{_pathfinder.DiagStaleCacheSize}}}");
            DebugLog.Log("perf", sb.ToString());
        }
    }

    private void RebuildQuadtree()
    {
        if (_units.Count <= 0) return;
        var positions = new Vec2[_units.Count];
        var ids = new uint[_units.Count];
        var factions = new byte[_units.Count];
        for (int i = 0; i < _units.Count; i++)
        {
            positions[i] = _units[i].Position;
            ids[i] = _units[i].Id;
            factions[i] = (byte)_units[i].Faction;
        }
        _quadtree.Build(positions, ids, factions, new AABB(0, 0, _grid.Width, _grid.Height));
    }

    // Player input (set by Game1 before Tick)
    private Vec2 _necroMoveInput;
    private bool _necroRunning;
    private float _necroFacingOverride = float.NaN;

    // Sprint ramp: 0.0 = walking at base CombatSpeed, 1.0 = full sprint at 4×.
    // Tracks how far we've ramped through the build-up while shift is held; gives
    // the unit a deliberate 3-second wind-up to full speed (not an instant snap)
    // and a faster ~1-second decay on release so it doesn't keep coasting at
    // sprint speed after letting go. Lerped into _sprintMultiplier each AI tick
    // in the PlayerControlled case; multiplier scales MaxSpeed and biases the
    // unit's MoveEffort toward Sprint so the gait picker shows Run earlier than
    // raw velocity would warrant (= unit visibly cycles Walk → Jog → Run during
    // the ramp instead of staying in Walk until ~mid-ramp).
    private const float SprintRampUpSeconds = 3.0f;
    private const float SprintRampDownSeconds = 1.0f;
    private const float SprintMaxMultiplier = 4.0f;
    private float _sprintRampValue; // 0..1

    /// <summary>Public accessor for debug / HUD readout — current sprint ramp
    /// fraction (0 = walking, 1 = full sprint).</summary>
    public float SprintRampValue => _sprintRampValue;

    public void SetNecromancerInput(Vec2 moveDir, bool running)
    {
        _necroMoveInput = moveDir;
        _necroRunning = running;
    }

    /// <summary>Sets the mouse-driven target facing angle for the necromancer.
    /// The actual rotation is applied by <see cref="UpdateFacingAngles"/> at the
    /// unit's turn rate. While jogging or running the player branch picks the
    /// velocity direction over this override; once velocity drops back into walk
    /// gait, this becomes the target again and the body swings to face it.</summary>
    public void SetNecromancerFacing(float angleDeg)
    {
        _necroFacingOverride = angleDeg;
    }

    // --- AI ---
    private void UpdateAI(float dt)
    {
        // Awareness pass (runs before AI updates)
        float dayFraction;
        bool isNight;
        var dnSettings = _gameData?.Settings.DayNight;
        if (dnSettings != null && dnSettings.Enabled)
        {
            // Use DayNightSystem phases: Night and Dusk count as "night" for animal AI
            float totalCycle = dnSettings.DawnDuration + dnSettings.DayDuration +
                               dnSettings.DuskDuration + dnSettings.NightDuration;
            dayFraction = totalCycle > 0f ? (_gameTime % totalCycle) / totalCycle : 0f;
            float elapsed = _gameTime % (totalCycle > 0f ? totalCycle : 1f);
            isNight = elapsed >= (dnSettings.DawnDuration + dnSettings.DayDuration);
        }
        else
        {
            float dayCycleLength = 360f; // 6 minutes fallback
            dayFraction = (_gameTime % dayCycleLength) / dayCycleLength;
            isNight = dayFraction >= 0.5f;
        }
        PhaseStart();
        AI.AwarenessSystem.Update(_units, _quadtree, dt, (int)_frameNumber, _amortizedAI, _aiUpdateInterval);
        PhaseEnd("ai_awareness");

        double archetypeMs = 0, legacyMs = 0;
        var subSw = new System.Diagnostics.Stopwatch();

        for (int i = 0; i < _units.Count; i++)
        {
            if (!_units[i].Alive) continue;
            if (_units[i].InPhysics) continue; // Physics system owns this unit
            if (_units[i].Jumping || _units[i].Incap.IsLocked) { _units[i].PreferredVel = Vec2.Zero; continue; }

            // Routing overrides all AI: a broken unit flees the field regardless of
            // archetype/legacy behavior, until it rallies. Runs before the dispatch
            // so it applies uniformly to every unit type.
            if (_units[i].Routing) { SteerRout(i, dt); continue; }

            // New archetype system: if Archetype > 0, dispatch to handler
            // (PlayerControlled units are handled in the legacy switch below)
            if (_units[i].Archetype > 0 && _units[i].AI != AIBehavior.PlayerControlled)
            {
                subSw.Restart();
                var handler = AI.ArchetypeRegistry.Get(_units[i].Archetype);
                if (handler != null)
                {
                    var ctx = new AI.AIContext
                    {
                        UnitIndex = i, Units = _units, Dt = dt, FrameNumber = (int)_frameNumber,
                        GameData = _gameData, Pathfinder = _pathfinder, Quadtree = _quadtree,
                        Horde = _horde, TriggerSystem = _triggerSystem, EnvSystem = _envSystem,
                        Projectiles = _projectiles, MagicGlyphs = _magicGlyphs,
                        GameTime = _gameTime, DayTime = dayFraction, IsNight = isNight,
                        AmortizedAI = _amortizedAI, AmortizationInterval = _aiUpdateInterval,
                        AnimMeta = _animMeta,
                        DamageEvents = _damageEvents,
                        NecroSprintT = _sprintRampValue,
                    };
                    handler.Update(ref ctx);
                }
                subSw.Stop();
                archetypeMs += subSw.Elapsed.TotalMilliseconds;
                continue;
            }
            subSw.Restart(); // legacy switch from here down

            // Legacy AI path: only reached when Archetype == 0 (see `continue` above for
            // archetype units). Wolf-flavored AIBehaviors here don't collide with
            // WolfPackHandler because the archetype dispatcher short-circuits. The only
            // live user of these WolfX AIBehaviors is WolfHitAndRunScenario (test); the
            // "Wolf" unit-def itself uses Archetype=WolfPack which never enters this branch.
            // FleeWhenHit and wolf AIs self-manage combat/disengage logic.
            bool selfManagesCombat = _units[i].AI == AIBehavior.FleeWhenHit
                || _units[i].AI == AIBehavior.WolfHitAndRun || _units[i].AI == AIBehavior.WolfHitAndRunIsolated
                || _units[i].AI == AIBehavior.WolfOpportunist || _units[i].AI == AIBehavior.WolfOpportunistIsolated;
            // Units with pending attacks stop moving (except self-managed AIs)
            // FleeWhenHit always bypasses; wolf AIs bypass during disengage/wait phases
            if (!_units[i].PendingAttack.IsNone && !(_units[i].AI == AIBehavior.FleeWhenHit
                || (selfManagesCombat && _units[i].WolfPhase >= WolfDisengage)))
            { _units[i].PreferredVel = Vec2.Zero; continue; }
            if (_units[i].InCombat && _units[i].AI != AIBehavior.PlayerControlled && !selfManagesCombat)
            { _units[i].PreferredVel = Vec2.Zero; continue; }

            switch (_units[i].AI)
            {
                case AIBehavior.PlayerControlled:
                {
                    // Sprint ramp: integrate _sprintRampValue toward 1 while shift
                    // is held + the unit is allowed to sprint, otherwise toward 0.
                    // Carrying a corpse disqualifies sprinting (preserves the prior
                    // behavior where carrying suppressed the run bonus).
                    bool canSprint = _necroRunning && _units[i].CarryingCorpseID < 0 && !_units[i].GhostMode;
                    float rampRate = canSprint
                        ? dt / SprintRampUpSeconds
                        : -dt / SprintRampDownSeconds;
                    _sprintRampValue = Necroking.Core.MathUtil.Clamp(_sprintRampValue + rampRate, 0f, 1f);
                    // Per-unit sprint cap: necromancer evolutions and other player
                    // forms can have different sprint multipliers. Falls back to
                    // the system default (4× biped sprint) when the def doesn't
                    // specify. Each different player form (Lich, GrandNecromancer,
                    // etc.) can have its own sprint character via this knob.
                    var playerDef = _gameData?.Units.Get(_units[i].UnitDefID);
                    float maxSprintMult = (playerDef?.SprintSpeedMultiplier > 0f)
                        ? playerDef.SprintSpeedMultiplier
                        : SprintMaxMultiplier;
                    float sprintMultiplier = 1f + (maxSprintMult - 1f) * _sprintRampValue;

                    float speed = _units[i].Stats.CombatSpeed;
                    if (_units[i].GhostMode)
                        speed = 20.0f;
                    else
                        speed *= sprintMultiplier;
                    _units[i].MaxSpeed = speed; // update so ORCA + accel cap respect current speed

                    // Bias the gait picker toward Sprint while ramping so the player
                    // sees the gait transition early (Walk → Jog → Run cycles through
                    // during the 3-second ramp) rather than the gait lagging actual
                    // velocity. Tiny ramp values (<5%) snap back to Normal so a
                    // single-frame shift-tap doesn't lock the unit into Sprint intent.
                    _units[i].MoveEffort = _sprintRampValue > 0.05f
                        ? Movement.MoveEffort.Sprint
                        : Movement.MoveEffort.Normal;

                    // Dispatch to PlayerControlledHandler for structured activities
                    if (_units[i].Routine != 0)
                    {
                        var handler = AI.ArchetypeRegistry.Get(AI.ArchetypeRegistry.PlayerControlled);
                        if (handler != null)
                        {
                            var ctx = new AI.AIContext
                            {
                                UnitIndex = i, Units = _units, Dt = dt, FrameNumber = (int)_frameNumber,
                                GameData = _gameData, Pathfinder = _pathfinder, Quadtree = _quadtree,
                                Horde = _horde, TriggerSystem = _triggerSystem, EnvSystem = _envSystem,
                                Projectiles = _projectiles, MagicGlyphs = _magicGlyphs,
                                GameTime = _gameTime, DayTime = dayFraction, IsNight = isNight,
                                // Player is always every-frame anyway, but pass for consistency.
                                AmortizedAI = _amortizedAI, AmortizationInterval = _aiUpdateInterval,
                                NecroSprintT = _sprintRampValue,
                            };
                            handler.Update(ref ctx);
                        }

                        // Player movement cancels active routines (WASD override).
                        // Note: CraftTableIdx is cleared too, but ts.Crafting / ts.CraftTimer
                        // on the env-side TableCraftState are left intact so the player can
                        // resume the craft from where it paused by clicking Start again.
                        // Bush-work routine cancellation here means the potion is NOT
                        // consumed (consumption only happens on successful WorkLoop completion).
                        if (_necroMoveInput.LengthSq() > 0.01f && _units[i].Routine != 0)
                        {
                            _units[i].Routine = 0;
                            _units[i].Subroutine = 0;
                            _units[i].CorpseInteractPhase = 0;
                            _units[i].BuildTargetIdx = -1;
                            _units[i].BuildGlyphId = -1;
                            _units[i].BuildTimer = 0f;
                            _units[i].CraftTableIdx = -1;
                            _units[i].BushWorkObjIdx = -1;
                            _units[i].BushWorkBuffID = "";
                            _units[i].BushWorkItemID = "";
                        }
                    }
                    else
                    {
                        // Normal player control
                        if (_units[i].CorpseInteractPhase != 0)
                            _units[i].PreferredVel = Vec2.Zero;
                        else
                            _units[i].PreferredVel = _necroMoveInput * speed;
                    }
                    break;
                }

                case AIBehavior.ArcherAttack:
                {
                    if (!IsTargetAlive(_units[i].Target) || !_units[i].Target.IsUnit)
                    {
                        int enemy = FindClosestEnemy(i);
                        _units[i].Target = enemy >= 0 ? CombatTarget.Unit(_units[enemy].Id) : CombatTarget.None;
                    }
                    if (_units[i].Target.IsUnit)
                    {
                        int targetIdx = ResolveUnitTarget(_units[i].Target);
                        if (targetIdx >= 0)
                        {
                            float dist = (_units[targetIdx].Position - _units[i].Position).Length();
                            float bestRange = _units[i].Stats.RangedRange.Count > 0 ? _units[i].Stats.RangedRange[0] : 40f;

                            if (dist <= bestRange)
                            {
                                _units[i].PreferredVel = Vec2.Zero;
                                if (_units[i].AttackCooldown <= 0f && _units[i].PendingAttack.IsNone)
                                {
                                    int damage = _units[i].Stats.RangedDmg.Count > 0 ? _units[i].Stats.RangedDmg[0] : 8;
                                    bool volley = dist > bestRange * 0.4f;
                                    _projectiles.SpawnArrow(_units[i].Position, _units[targetIdx].Position,
                                        _units[i].Faction, _units[i].Id, damage, volley, 10,
                                        spawnHeight: _units[i].EffectSpawnHeight);
                                    _units[i].AttackCooldown = _units[i].Stats.RangedCooldownTime.Count > 0
                                        ? _units[i].Stats.RangedCooldownTime[0] : 2f;
                                }
                            }
                            else
                                MoveTowardUnit(i, targetIdx, _units[i].MaxSpeed);
                        }
                        else _units[i].PreferredVel = Vec2.Zero;
                    }
                    else _units[i].PreferredVel = Vec2.Zero;
                    break;
                }

                case AIBehavior.AttackNecromancer:
                {
                    if (_necromancerIdx >= 0 && _necromancerIdx < _units.Count && _units[_necromancerIdx].Alive)
                        _units[i].Target = CombatTarget.Unit(_units[_necromancerIdx].Id);
                    else if (!IsTargetAlive(_units[i].Target))
                        _units[i].Target = FindBestEnemyTarget(i);
                    goto default;
                }

                case AIBehavior.GuardKnight:
                {
                    int guardTarget = -1;
                    float bestGuardDist = float.MaxValue;
                    for (int j = 0; j < _units.Count; j++)
                    {
                        if (j == i || !_units[j].Alive || _units[j].Faction != _units[i].Faction) continue;
                        if (_units[j].AI != AIBehavior.AttackNecromancer) continue;
                        float d = (_units[j].Position - _units[i].Position).LengthSq();
                        if (d < bestGuardDist) { bestGuardDist = d; guardTarget = j; }
                    }
                    if (guardTarget >= 0 && MathF.Sqrt(bestGuardDist) > 3f)
                    {
                        MoveTowardUnit(i, guardTarget, _units[i].MaxSpeed);
                        break;
                    }
                    if (!IsTargetAlive(_units[i].Target))
                        _units[i].Target = FindBestEnemyTarget(i);
                    goto default;
                }

                case AIBehavior.MoveToPoint:
                {
                    var toTarget = _units[i].MoveTarget - _units[i].Position;
                    if (toTarget.LengthSq() > 1f)
                    {
                        int sizeTier = TerrainCosts.SizeToTier(_units[i].Size);
                        var dir = _pathfinder.GetDirection(_units[i].Position, _units[i].MoveTarget, _frameNumber, sizeTier, i);
                        _units[i].PreferredVel = dir * _units[i].MaxSpeed;
                    }
                    else
                        _units[i].PreferredVel = Vec2.Zero;
                    break;
                }

                case AIBehavior.IdleAtPoint:
                case AIBehavior.DefendPoint:
                {
                    // Idle near move target, fight nearby enemies, return
                    int enemy = FindClosestEnemy(i);
                    if (enemy >= 0)
                    {
                        float eDist = (_units[enemy].Position - _units[i].Position).Length();
                        if (eDist < 10f)
                        {
                            _units[i].Target = CombatTarget.Unit(_units[enemy].Id);
                            MoveTowardUnit(i, enemy, _units[i].MaxSpeed);
                            break;
                        }
                    }
                    // Return to idle point
                    var toIdle = _units[i].MoveTarget - _units[i].Position;
                    if (toIdle.LengthSq() > 4f)
                        MoveTowardPosition(i, _units[i].MoveTarget, _units[i].MaxSpeed * 0.5f);
                    else
                        _units[i].PreferredVel = Vec2.Zero;
                    break;
                }

                case AIBehavior.Patrol:
                {
                    // Move along patrol route waypoints
                    // For now, just move to moveTarget (first waypoint)
                    var toTarget = _units[i].MoveTarget - _units[i].Position;
                    if (toTarget.LengthSq() > 1f)
                        MoveTowardPosition(i, _units[i].MoveTarget, _units[i].MaxSpeed);
                    else
                        _units[i].PreferredVel = Vec2.Zero;

                    // Fight nearby enemies
                    int nearEnemy = FindClosestEnemy(i);
                    if (nearEnemy >= 0 && (_units[nearEnemy].Position - _units[i].Position).Length() < 8f)
                    {
                        _units[i].Target = CombatTarget.Unit(_units[nearEnemy].Id);
                        MoveTowardUnit(i, nearEnemy, _units[i].MaxSpeed);
                    }
                    break;
                }

                case AIBehavior.Raid:
                {
                    // Attack-move toward target, call friends
                    if (!IsTargetAlive(_units[i].Target))
                        _units[i].Target = FindBestEnemyTarget(i);
                    if (_units[i].Target.IsUnit)
                    {
                        int ti = ResolveUnitTarget(_units[i].Target);
                        if (ti >= 0) MoveTowardUnit(i, ti, _units[i].MaxSpeed);
                        else _units[i].PreferredVel = Vec2.Zero;
                    }
                    else
                    {
                        // Move toward moveTarget (raid destination)
                        var toTarget = _units[i].MoveTarget - _units[i].Position;
                        if (toTarget.LengthSq() > 1f)
                            MoveTowardPosition(i, _units[i].MoveTarget, _units[i].MaxSpeed);
                        else
                            _units[i].PreferredVel = Vec2.Zero;
                    }
                    break;
                }

                case AIBehavior.Caster:
                {
                    _units[i].PreferredVel = Vec2.Zero;

                    // Tick mana regen
                    _units[i].Mana = MathF.Min(_units[i].MaxMana, _units[i].Mana + _units[i].ManaRegen * dt);

                    // Tick spell cooldown
                    _units[i].SpellCooldownTimer = MathF.Max(0f, _units[i].SpellCooldownTimer - dt);

                    // No spell or no mana pool — idle
                    string spellId = _units[i].SpellID;
                    if (string.IsNullOrEmpty(spellId) || _units[i].MaxMana <= 0f) break;

                    // Look up spell definition
                    var spell = _gameData?.Spells.Get(spellId);
                    if (spell == null) break;

                    // Find closest enemy within spell range
                    int enemy = FindClosestEnemy(i);
                    if (enemy >= 0)
                    {
                        float dist = (_units[enemy].Position - _units[i].Position).Length();
                        // Path-aware gating: caster must meet primary+secondary
                        // path requirements; cost is scaled down by primary mastery.
                        var casterDef = _gameData?.Units.Get(_units[i].UnitDefID);
                        System.Func<Data.Registries.MagicPath, int> casterLevel = casterDef != null
                            ? casterDef.GetPathLevel
                            : _ => 0;
                        float effectiveCost = spell.EffectiveManaCost(casterLevel);
                        if (dist <= spell.Range &&
                            spell.MeetsPathRequirements(casterLevel) &&
                            _units[i].Mana >= effectiveCost &&
                            _units[i].SpellCooldownTimer <= 0f)
                        {
                            // Cast the spell — spawn strike at target
                            var style = spell.BuildStrikeStyle();
                            var visual = spell.StrikeVisualType == "GodRay"
                                ? StrikeVisual.GodRay : StrikeVisual.Lightning;
                            var grp = spell.BuildGodRayParams();
                            var tFilter = Enum.TryParse<SpellTargetFilter>(spell.TargetFilter, out var tf)
                                ? tf : SpellTargetFilter.AnyEnemy;
                            if (spell.StrikeTargetUnit)
                            {
                                // Instant zap: caster hand to target unit
                                Vec2 casterPos = _units[i].EffectSpawnPos2D;
                                float casterHeight = _units[i].EffectSpawnHeight;
                                Vec2 targetPos = _units[enemy].Position;

                                // Target center of sprite body instead of feet
                                float targetH = 1.8f;
                                if (_gameData != null)
                                {
                                    var tDef = _gameData.Units.Get(_units[enemy].UnitDefID);
                                    if (tDef != null) targetH = tDef.SpriteWorldHeight;
                                }
                                targetH *= _units[enemy].SpriteScale;

                                _lightning.SpawnZap(casterPos, targetPos, spell.ZapDuration, style,
                                    casterHeight, targetH * 0.5f);

                                // Apply direct damage to target, attributed to the caster.
                                DamageSystem.Apply(_units, enemy, spell.Damage,
                                    GameSystems.DamageType.Physical, GameSystems.DamageFlags.ArmorNegating,
                                    _damageEvents, i);
                            }
                            else
                            {
                                // Sky lightning strike: telegraph then AOE
                                _lightning.SpawnStrike(_units[enemy].Position,
                                    spell.TelegraphDuration, spell.StrikeDuration,
                                    spell.AoeRadius, spell.Damage, style, spell.Id, visual, grp, tFilter,
                                    spell.TelegraphVisible, _units[i].Id);
                            }

                            _units[i].Mana -= effectiveCost;
                            _units[i].SpellCooldownTimer = spell.Cooldown;

                            // Apply casting buff
                            if (!string.IsNullOrEmpty(spell.CastingBuffID) && _gameData != null)
                            {
                                var castBuff = _gameData.Buffs.Get(spell.CastingBuffID);
                                if (castBuff != null) BuffSystem.ApplyBuff(_units, i, castBuff);
                            }

                            // Face target (rate-capped by unit TurnSpeed — no
                            // instant snap, so the caster visibly turns to its
                            // target as the spell winds up).
                            Movement.FacingUtil.TurnTowardPosition(
                                _units[i], _units[enemy].Position, dt, _gameData);
                        }
                    }
                    break;
                }

                case AIBehavior.CorpseWorker:
                {
                    // Find corpses, drag to buildings — simplified for now
                    _units[i].PreferredVel = Vec2.Zero;
                    break;
                }

                case AIBehavior.OrderAttack:
                {
                    // Attack-move to moveTarget, fight enemies, rejoin horde
                    const float OrderEngageRange = 15f;
                    const float OrderArrivalDist = 5f;

                    Vec2 dest = _units[i].MoveTarget;
                    float distToDest = (_units[i].Position - dest).Length();
                    bool atDest = distToDest < OrderArrivalDist;

                    // Acquire/validate target
                    if (!IsTargetAlive(_units[i].Target))
                    {
                        int enemy = FindClosestEnemy(i);
                        if (enemy >= 0)
                        {
                            float eDist = (_units[enemy].Position - _units[i].Position).Length();
                            if (eDist < OrderEngageRange)
                                _units[i].Target = CombatTarget.Unit(_units[enemy].Id);
                            else
                                _units[i].Target = CombatTarget.None;
                        }
                    }

                    if (IsTargetAlive(_units[i].Target) && _units[i].Target.IsUnit)
                    {
                        int ti = ResolveUnitTarget(_units[i].Target);
                        if (ti >= 0) MoveTowardUnit(i, ti, _units[i].MaxSpeed);
                        else _units[i].PreferredVel = Vec2.Zero;
                    }
                    else if (!atDest)
                    {
                        // March toward destination
                        MoveTowardPosition(i, dest, _units[i].MaxSpeed);
                    }
                    else
                    {
                        // At destination with no enemies — return to horde
                        _units[i].AI = AIBehavior.AttackClosest;
                        _units[i].Target = CombatTarget.None;
                        _horde.AddUnit(_units[i].Id);
                        _units[i].PreferredVel = Vec2.Zero;
                    }
                    break;
                }

                case AIBehavior.AttackClosestRetarget:
                {
                    _units[i].RetargetTimer -= dt;
                    if (_units[i].RetargetTimer <= 0f || !IsTargetAlive(_units[i].Target))
                    {
                        _units[i].Target = FindBestEnemyTarget(i);
                        _units[i].RetargetTimer = 2f;
                    }
                    goto default;
                }

                case AIBehavior.FleeWhenHit:
                {
                    // Deer-like behavior: idle normally, flee when hit at effect time.
                    // Mirror the flee state so the hit-react flinch is suppressed while
                    // running (a fleeing unit keeps its run anim when hit again).
                    _units[i].Fleeing = _units[i].FleeTimer > 0f;
                    if (_units[i].FleeTimer > 0)
                    {
                        // Currently fleeing — force disengage
                        Disengage(i);
                        _units[i].EngagedTarget = CombatTarget.None;

                        _units[i].FleeTimer -= dt;
                        int attackerIdx = UnitUtil.ResolveUnitIndex(_units, _units[i].LastAttackerID);
                        if (attackerIdx >= 0)
                        {
                            Vec2 awayDir = (_units[i].Position - _units[attackerIdx].Position);
                            float dist = awayDir.Length();
                            if (dist > 15f)
                            {
                                _units[i].FleeTimer = 0;
                                _units[i].LastAttackerID = GameConstants.InvalidUnit;
                                _units[i].PreferredVel = Vec2.Zero;
                            }
                            else
                            {
                                awayDir = dist > 0.01f ? awayDir * (1f / dist) : new Vec2(1, 0);
                                // Pathfind to a point far away from the attacker
                                Vec2 fleeDest = _units[i].Position + awayDir * 15f;
                                MoveTowardPosition(i, fleeDest, _units[i].MaxSpeed);
                            }
                        }
                        else
                        {
                            // No attacker found (e.g. poison damage) — flee using current facing
                            float angle = _units[i].FacingAngle;
                            Vec2 fleeDest = _units[i].Position + new Vec2(MathF.Cos(angle), MathF.Sin(angle)) * 15f;
                            MoveTowardPosition(i, fleeDest, _units[i].MaxSpeed);
                        }
                    }
                    else if (_units[i].LastAttackerID != GameConstants.InvalidUnit || _units[i].HitReacting)
                    {
                        // Got hit (by attacker or poison/environmental damage) — flee
                        if (_units[i].PostAttackTimer > 0f || !_units[i].PendingAttack.IsNone)
                        {
                            // Mid-attack: queue flee for when we're free
                            _units[i].QueuedAction = QueuedUnitAction.Flee;
                        }
                        else
                        {
                            // Free to flee now
                            _units[i].FleeTimer = 5f;
                            Disengage(i);
                            _units[i].EngagedTarget = CombatTarget.None;
                            _units[i].ShowStatusSymbol(UnitStatusSymbol.React, 1.5f);
                        }
                    }
                    else if (_units[i].QueuedAction == QueuedUnitAction.Flee
                             && _units[i].PostAttackTimer <= 0f
                             && _units[i].PendingAttack.IsNone)
                    {
                        // Queued flee — now free to execute
                        _units[i].QueuedAction = QueuedUnitAction.None;
                        _units[i].FleeTimer = 5f;
                        Disengage(i);
                        _units[i].EngagedTarget = CombatTarget.None;
                        _units[i].ShowStatusSymbol(UnitStatusSymbol.React, 1.5f);
                    }
                    else
                    {
                        _units[i].PreferredVel = Vec2.Zero; // idle
                    }
                    break;
                }

                case AIBehavior.NeutralFightBack:
                {
                    // Neutral: ignore enemies unless hit, then fight attacker
                    if (_units[i].LastAttackerID != GameConstants.InvalidUnit)
                    {
                        // We've been hit before — fight the attacker (or any enemy if attacker dead)
                        if (!IsTargetAlive(_units[i].Target))
                        {
                            int attackerIdx = UnitUtil.ResolveUnitIndex(_units, _units[i].LastAttackerID);
                            if (attackerIdx >= 0 && _units[attackerIdx].Alive)
                                _units[i].Target = CombatTarget.Unit(_units[i].LastAttackerID);
                            else
                                _units[i].Target = FindBestEnemyTarget(i);
                            if (_units[i].Target.IsUnit)
                                _units[i].ShowStatusSymbol(UnitStatusSymbol.React, 1.5f);
                        }

                        if (_units[i].Target.IsUnit)
                        {
                            int targetIdx = ResolveUnitTarget(_units[i].Target);
                            if (targetIdx >= 0)
                            {
                                MoveTowardUnit(i, targetIdx, _units[i].MaxSpeed);
                                // Engage when in range
                                float dist = (_units[targetIdx].Position - _units[i].Position).Length();
                                float engageRange = (_gameData?.Settings.Combat.MeleeRange ?? MeleeRangeBase)
                                    + _units[i].Stats.Length * 0.15f + _units[i].Radius + _units[targetIdx].Radius;
                                if (dist <= engageRange && _units[i].EngagedTarget.IsNone)
                                    _units[i].EngagedTarget = _units[i].Target;
                            }
                            else
                                _units[i].PreferredVel = Vec2.Zero;
                        }
                        else
                            _units[i].PreferredVel = Vec2.Zero;
                    }
                    else
                    {
                        _units[i].PreferredVel = Vec2.Zero; // peaceful until provoked
                    }
                    break;
                }

                case AIBehavior.WolfHitAndRun:
                case AIBehavior.WolfHitAndRunIsolated:
                case AIBehavior.WolfOpportunist:
                case AIBehavior.WolfOpportunistIsolated:
                {
                    UpdateWolfAI(i, dt);
                    break;
                }

                default:
                {
                    bool inHorde = _units[i].Faction == Faction.Undead && _horde.IsInHorde(_units[i].Id);
                    var hordeState = inHorde ? _horde.GetUnitState(_units[i].Id) : HordeUnitState.Following;

                    // Returning horde units: drop target, move back to slot
                    if (inHorde && hordeState == HordeUnitState.Returning)
                    {
                        _units[i].Target = CombatTarget.None;
                        _units[i].EngagedTarget = CombatTarget.None;
                        _units[i].InCombat = false;
                        _units[i].PendingAttack = CombatTarget.None;
                        if (_horde.GetTargetPosition(_units[i].Id, out var returnSlot))
                        {
                            float distToSlot = (_units[i].Position - returnSlot).Length();
                            if (distToSlot > 0.5f)
                                MoveTowardPosition(i, returnSlot, _units[i].MaxSpeed * _horde.Settings.ReturnSpeedMult);
                            else
                                _units[i].PreferredVel = Vec2.Zero;
                        }
                        else
                            _units[i].PreferredVel = Vec2.Zero;
                        break;
                    }

                    // Sync horde chasing target to unit target
                    if (inHorde && hordeState == HordeUnitState.Chasing)
                    {
                        uint chasingId = _horde.GetChasingTarget(_units[i].Id);
                        if (chasingId != GameConstants.InvalidUnit)
                        {
                            _units[i].Target = CombatTarget.Unit(chasingId);
                        }
                    }

                    // Acquire new target (range-limited for horde units)
                    if (!IsTargetAlive(_units[i].Target))
                    {
                        if (inHorde)
                        {
                            int nearby = FindClosestEnemy(i, _horde.EngagementRange);
                            _units[i].Target = nearby >= 0 ? CombatTarget.Unit(_units[nearby].Id) : CombatTarget.None;
                        }
                        else
                            _units[i].Target = FindBestEnemyTarget(i);
                    }

                    if (_units[i].Target.IsUnit)
                    {
                        int targetIdx = ResolveUnitTarget(_units[i].Target);
                        if (targetIdx >= 0)
                        {
                            MoveTowardUnit(i, targetIdx, _units[i].MaxSpeed);
                            // Auto-engage when in melee range
                            float dist = (_units[targetIdx].Position - _units[i].Position).Length();
                            float engageRange = (_gameData?.Settings.Combat.MeleeRange ?? MeleeRangeBase)
                                + _units[i].Stats.Length * 0.15f + _units[i].Radius + _units[targetIdx].Radius;
                            if (dist <= engageRange && _units[i].EngagedTarget.IsNone)
                                _units[i].EngagedTarget = _units[i].Target;
                        }
                        else
                            _units[i].PreferredVel = Vec2.Zero;
                    }
                    else
                    {
                        // No combat target — clear engagement
                        _units[i].EngagedTarget = CombatTarget.None;
                        // Follow horde slot position if in horde
                        if (inHorde && _horde.GetTargetPosition(_units[i].Id, out var slotPos))
                        {
                            float distToSlot = (_units[i].Position - slotPos).Length();
                            if (distToSlot > 0.5f)
                                MoveTowardPosition(i, slotPos, _units[i].MaxSpeed);
                            else
                                _units[i].PreferredVel = Vec2.Zero;
                        }
                        else
                            _units[i].PreferredVel = Vec2.Zero;
                    }
                    break;
                }
            }
            subSw.Stop();
            legacyMs += subSw.Elapsed.TotalMilliseconds;
        }

        LastPhaseMs["ai_archetype"] = archetypeMs;
        LastPhaseMs["ai_legacy"] = legacyMs;
        LastPhaseMs["pathfinder"] = Necroking.World.Pathfinder.DiagTotalMsThisTick;
        LastPhaseMs["pathfinder_calls"] = Necroking.World.Pathfinder.DiagCallsThisTick;
        LastPhaseMs["pf_dijkstras"] = Necroking.World.Pathfinder.DiagDijkstraInvocations;
        LastPhaseMs["pf_cache_hits"] = Necroking.World.Pathfinder.DiagFlowCacheHits;
        LastPhaseMs["pf_cache_misses"] = Necroking.World.Pathfinder.DiagFlowCacheMisses;
        LastPhaseMs["pf_imag_new"] = Necroking.World.Pathfinder.DiagImagChunkComputes;
        LastPhaseMs["pf_imag_recompute"] = Necroking.World.Pathfinder.DiagImagChunkRecomputes;
        LastPhaseMs["pf_imag_ms"] = Necroking.World.Pathfinder.DiagImagChunkMs;
    }

    // Apply a multiplicative speed penalty for the terrain the unit's currently
    // standing on (e.g. shallow water = 0.5×). Runs between AI/effort writes and
    // UpdateMovement so ORCA's velocity cap sees the slowed value. Skips units
    // not currently controlled by the movement system (physics-owned, dodging,
    // charging) — those have their own velocity authorities.
    private void ApplyTerrainSpeedModulation()
    {
        int w = _grid.Width;
        int h = _grid.Height;
        if (w <= 0 || h <= 0) return;

        for (int i = 0; i < _units.Count; i++)
        {
            if (!_units[i].Alive) continue;
            if (_units[i].InPhysics) continue;
            if (_units[i].DodgeTimer > 0f) continue;
            if (_units[i].ChargePhase == 1 || _units[i].ChargePhase == 3) continue;

            int gx = (int)MathF.Floor(_units[i].Position.X);
            int gy = (int)MathF.Floor(_units[i].Position.Y);
            if (gx < 0 || gx >= w || gy < 0 || gy >= h) continue;

            var terrain = _grid.GetTerrain(gx, gy);
            if (terrain == TerrainType.Open) continue; // fast path: most tiles

            float mult = TerrainCosts.GetSpeedMultiplier(terrain);
            if (mult < 1f) _units[i].MaxSpeed *= mult;
        }
    }

    // --- Movement (with ORCA) ---
    private void UpdateMovement(float dt)
    {
        var neighbors = new List<ORCANeighbor>();
        var nearbyIDs = new List<uint>();
        var envEntries = new List<EnvSpatialIndex.Entry>();

        // Top-K neighbor scratch buffers — hoisted outside the loop so the
        // stackalloc happens once per UpdateMovement call, not per unit.
        const int TopK = 10;
        Span<float> topDist = stackalloc float[TopK];
        Span<int>   topIdx  = stackalloc int[TopK];

        for (int i = 0; i < _units.Count; i++)
        {
            if (!_units[i].Alive) continue;
            if (_units[i].InPhysics) continue; // Physics system owns this unit's movement
            // Charging (Trample): TrampleSystem.TickCharge wrote Velocity this frame
            // at the desired charge speed. Skip ORCA / acceleration ramp / env-circle
            // clipping — the charger phases through smaller units by design, and
            // impact handling (larger-unit block, reach target) is in TickCharge.
            // Wall collision at the bottom of the loop still applies, so solid
            // geometry stops the charge. MoveTime saturated so if charge ends and
            // normal movement resumes, there's no spin-up lag.
            if (_units[i].ChargePhase == 1 || _units[i].ChargePhase == 3)
            {
                _units[i].MoveTime = 10f;
                goto __ChargeWallCollision;
            }
            // Dodge hop owns this unit's position interpolation — set in main tick
            // before UpdateMovement runs. Skip everything (ORCA, wall, env) so the
            // hop arrives at exactly the chosen safe tile.
            if (_units[i].DodgeTimer > 0f)
            {
                _units[i].Velocity = Vec2.Zero;
                _units[i].PreferredVel = Vec2.Zero;
                continue;
            }
            // Movement blocked by: jumping, knockdown (buff), standup, pending attack,
            // post-attack lockout, or being engaged in melee. The InCombat clause
            // plants a unit for the WHOLE attack cycle (incl. the pre-roll windup that
            // starts the swing anim before PendingAttack is set, and the cooldown
            // between swings) so melee units "stop to fight" instead of sliding while
            // the attack animation plays. PlayerControlled is exempt — the player is
            // never frozen by their own combat. Ranged units never set EngagedTarget
            // so they're not InCombat and keep their kite.
            if (_units[i].Jumping || _units[i].Incap.IsLocked
                || !_units[i].PendingAttack.IsNone || _units[i].PostAttackTimer > 0f
                || (_units[i].InCombat && _units[i].AI != AIBehavior.PlayerControlled))
            {
                _units[i].Velocity = Vec2.Zero;
                _units[i].MoveTime = 0f;
                continue;
            }

            // Ghost mode: skip ORCA
            if (_units[i].GhostMode)
            {
                _units[i].Velocity = _units[i].PreferredVel;
                _units[i].Position += _units[i].Velocity * dt;
                continue;
            }

            // Necromancer (player-controlled): skip the ORCA gather/compute so the
            // player's input isn't deflected by other units or trees. Other units
            // still see him via their own ORCA queries on the quadtree and dodge
            // normally. The wall + env-circle collision steps below still run, so
            // he can't walk through walls or clip through trees.
            bool skipOrca = _units[i].AI == AIBehavior.PlayerControlled;

            // Idle shortcut: a unit whose AI wants 0 velocity and whose current
            // velocity is already 0 will get 0 back from ORCA anyway — it's a
            // goal-less solver with no incoming motion. Skipping the gather +
            // solve for these saves the dense-horde cost when most skeletons
            // are parked in their formation slots (PreferredVel zeroed by
            // SetIdle). Other units still see this one in the quadtree, so
            // movers avoid it normally. The 0.01 threshold is well below any
            // intended movement speed.
            bool idleShortcut = !skipOrca
                && _units[i].PreferredVel.LengthSq() < 0.0001f
                && _units[i].Velocity.LengthSq() < 0.0001f;

            // Build ORCA neighbor list
            neighbors.Clear();
            nearbyIDs.Clear();
            Vec2 newVel;
            var myPos = _units[i].Position;
            if (skipOrca || idleShortcut)
            {
                // Player input goes straight through; other units still see the
                // necromancer via the quadtree so they dodge him normally.
                // Idle shortcut: preferred velocity was zero; return zero.
                newVel = _units[i].PreferredVel;
            }
            else
            {
                float queryRadius = MathF.Max(_units[i].Radius * 5f, 3f);
                _quadtree.QueryRadius(_units[i].Position, queryRadius, nearbyIDs);

                // Top-K (10) closest dynamic neighbours, inline without allocating a
                // lambda sort or a full throwaway list. At u=600+ with dense queries
                // (~80 nearby units), the prior full sort cost 5M+ compares/tick and
                // the capturing lambda allocated ~40B per unit (~24KB GC/tick).
                // Buffers hoisted outside the outer for loop; we just reset contents
                // at the start of each iteration. Insertion with early-exit on the
                // worst-kept distance turns this into O(N) once top-10 is full.
                for (int k = 0; k < TopK; k++) { topDist[k] = float.MaxValue; topIdx[k] = -1; }
                int topCount = 0;

                foreach (uint nid in nearbyIDs)
                {
                    if (nid == _units[i].Id) continue;
                    int j = UnitUtil.ResolveUnitIndex(_units, nid);
                    if (j < 0 || !_units[j].Alive) continue;

                    float dx = _units[j].Position.X - myPos.X;
                    float dy = _units[j].Position.Y - myPos.Y;
                    float d2 = dx * dx + dy * dy;

                    if (topCount == TopK && d2 >= topDist[TopK - 1]) continue;

                    int ins = Math.Min(topCount, TopK - 1);
                    while (ins > 0 && topDist[ins - 1] > d2)
                    {
                        topDist[ins] = topDist[ins - 1];
                        topIdx[ins]  = topIdx[ins - 1];
                        ins--;
                    }
                    topDist[ins] = d2;
                    topIdx[ins]  = j;
                    if (topCount < TopK) topCount++;
                }

                for (int k = 0; k < topCount; k++)
                {
                    int j = topIdx[k];
                    neighbors.Add(new ORCANeighbor
                    {
                        Position = _units[j].Position,
                        Velocity = _units[j].Velocity,
                        Radius = _units[j].Radius,
                        Id = _units[j].Id,
                        Priority = _units[j].Faction != _units[i].Faction
                            ? _units[i].OrcaPriority  // cross-faction: equal
                            : _units[j].OrcaPriority  // same-faction: respect hierarchy
                    });
                }

                // Append up to 6 nearest static env obstacles (trees, rocks). They
                // participate in ORCA as zero-velocity immovable circles with 100%
                // responsibility on the unit — the canonical ORCA handling for
                // circular static obstacles.
                envEntries.Clear();
                _envIndex.QueryRadius(myPos, queryRadius, envEntries);
                if (envEntries.Count > 0)
                {
                    envEntries.Sort((a, b) =>
                    {
                        float da = (a.CX - myPos.X) * (a.CX - myPos.X) + (a.CY - myPos.Y) * (a.CY - myPos.Y);
                        float db = (b.CX - myPos.X) * (b.CX - myPos.X) + (b.CY - myPos.Y) * (b.CY - myPos.Y);
                        return da.CompareTo(db);
                    });
                    int staticKept = 0;
                    const int MaxStatic = 6;
                    foreach (var e in envEntries)
                    {
                        if (staticKept >= MaxStatic) break;
                        float dx = e.CX - myPos.X, dy = e.CY - myPos.Y;
                        float combined = _units[i].Radius + e.Radius + 0.1f;
                        float reach = _units[i].MaxSpeed * 3f + combined;
                        if (dx * dx + dy * dy > reach * reach) continue;

                        neighbors.Add(new ORCANeighbor
                        {
                            Position = new Vec2(e.CX, e.CY),
                            Velocity = Vec2.Zero,
                            Radius = e.Radius,
                            Id = 0x80000000u | (uint)e.ObjectIndex,
                            Priority = int.MaxValue,
                            IsStatic = true,
                        });
                        staticKept++;
                    }
                }

                var param = new ORCAParams
                {
                    TimeHorizon = 3f,
                    MaxSpeed = _units[i].MaxSpeed,
                    Radius = _units[i].Radius,
                    MaxNeighbors = 16,
                    Priority = _units[i].OrcaPriority
                };

                newVel = Orca.ComputeORCAVelocity(
                    _units[i].Position, _units[i].Velocity, _units[i].PreferredVel,
                    neighbors, param, dt);
            }

            // Stuck detection + perpendicular nudge
            float speed = newVel.Length();
            float prefSpeed = _units[i].PreferredVel.Length();
            if (prefSpeed > 0.1f && speed < 0.1f * _units[i].MaxSpeed)
            {
                _units[i].StuckFrames++;
                if (_units[i].StuckFrames > 20)
                {
                    // Compute perpendicular to preferred velocity
                    Vec2 prefDir = _units[i].PreferredVel * (1f / prefSpeed);
                    Vec2 perp = (i % 2 == 0)
                        ? new Vec2(-prefDir.Y, prefDir.X)
                        : new Vec2(prefDir.Y, -prefDir.X);

                    // Blend ramps from 30% at 1s (frame 60) to 80% at 2s (frame 120)
                    float t = MathUtil.Clamp((_units[i].StuckFrames - 20) / 100f, 0f, 1f);
                    float blend = 0.3f + t * 0.5f;

                    newVel = _units[i].PreferredVel * (1f - blend) + perp * (prefSpeed * blend);
                    speed = newVel.Length();
                }
            }
            else
            {
                _units[i].StuckFrames = 0;
            }

            // --- Newtonian acceleration model ---
            // Treat newVel (ORCA-resolved, MaxSpeed-capped) as the desired velocity.
            // Decompose (desired - current) into forward (along current velocity)
            // and lateral (perpendicular) components, cap each independently:
            //   - Forward+: maxAcceleration  (speeding up)
            //   - Forward-: maxDeceleration  (braking; typically ~5× accel)
            //   - Lateral:  maxLateralAccel  (turn capacity; r = v² / lat)
            // This gives realistic legged locomotion: 180° reversal must pass
            // through 0 (decel then accel), sharp turns at speed are impossible
            // without first slowing down, slight turns at speed cost only the
            // lateral budget.
            var accelDef = _gameData?.Units.Get(_units[i].UnitDefID);
            float maxAccel = accelDef?.MaxAcceleration
                ?? _gameData?.Settings.Combat.MaxAcceleration ?? 6f;
            float maxDecel = accelDef?.MaxDeceleration
                ?? _gameData?.Settings.Combat.MaxDeceleration ?? 25f;
            float maxLateral = accelDef?.MaxLateralAccel
                ?? _gameData?.Settings.Combat.MaxLateralAccel ?? 15f;

            Vec2 curVel = _units[i].Velocity;
            Vec2 deltaVel = newVel - curVel;
            float curSpeedSq = curVel.LengthSq();

            // Pick the "forward" axis: current velocity direction if moving,
            // else the desired direction (so a unit starting from rest still
            // applies accel along its intended path).
            Vec2 fwdDir;
            if (curSpeedSq > 0.0001f)
                fwdDir = curVel * (1f / MathF.Sqrt(curSpeedSq));
            else if (newVel.LengthSq() > 0.0001f)
                fwdDir = newVel.Normalized();
            else
                fwdDir = new Vec2(1, 0);
            Vec2 latDir = new Vec2(-fwdDir.Y, fwdDir.X);

            float fwdComp = deltaVel.X * fwdDir.X + deltaVel.Y * fwdDir.Y;
            float latComp = deltaVel.X * latDir.X + deltaVel.Y * latDir.Y;

            float fwdCap = (fwdComp > 0f ? maxAccel : maxDecel) * dt;
            if (fwdComp > 0f) fwdComp = MathF.Min(fwdComp, fwdCap);
            else fwdComp = MathF.Max(fwdComp, -fwdCap);

            float latCap = maxLateral * dt;
            if (latComp > latCap) latComp = latCap;
            else if (latComp < -latCap) latComp = -latCap;

            _units[i].Velocity = curVel + fwdDir * fwdComp + latDir * latComp;

            // For player-controlled (skipOrca) units: clip velocity against nearby
            // env circles so the necromancer stops / slides at tree edges instead
            // of walking through and relying on stuck-escape to shove him back.
            if (skipOrca && _units[i].Velocity.LengthSq() > 0.0001f)
            {
                Vec2 vel = _units[i].Velocity;
                Vec2 pos = _units[i].Position;
                float selfR = _units[i].Radius;

                envEntries.Clear();
                // Look far enough to catch any circle we might hit this frame.
                float look = selfR + vel.Length() * dt + 0.1f;
                _envIndex.QueryRadius(pos, look, envEntries);

                foreach (var e in envEntries)
                {
                    float dx = pos.X - e.CX, dy = pos.Y - e.CY;
                    float combined = selfR + e.Radius;
                    float distSq = dx * dx + dy * dy;
                    // Skip if already clear of this obstacle and not heading into it.
                    if (distSq >= combined * combined)
                    {
                        // Project velocity onto the obstacle-to-unit vector; if
                        // we're moving *toward* the circle AND would overlap this
                        // step, remove the inward component (slide along tangent).
                        float dist = MathF.Sqrt(distSq);
                        float nx = dx / dist, ny = dy / dist;
                        float vNorm = vel.X * nx + vel.Y * ny; // + = away, - = toward
                        if (vNorm < 0f)
                        {
                            float step = -vNorm * dt;
                            float slack = dist - combined;
                            if (step > slack)
                                vel = new Vec2(vel.X - nx * vNorm, vel.Y - ny * vNorm);
                        }
                    }
                    else
                    {
                        // Already inside the combined circle — kill any inward
                        // component. The env stuck-escape will push us out
                        // proactively on the next tick.
                        float dist = MathF.Sqrt(MathF.Max(0.0001f, distSq));
                        float nx = dx / dist, ny = dy / dist;
                        float vNorm = vel.X * nx + vel.Y * ny;
                        if (vNorm < 0f)
                            vel = new Vec2(vel.X - nx * vNorm, vel.Y - ny * vNorm);
                    }
                }
                _units[i].Velocity = vel;
            }

            // Move with wall collision (axis-independent, gap probes, wall sliding)
            __ChargeWallCollision:
            Vec2 oldPos = _units[i].Position;
            Vec2 delta = _units[i].Velocity * dt;
            // Wall collision uses smaller radius than ORCA for 1-tile gap clearance
            float r = _units[i].Radius * 0.7f;

            // --- Stuck-inside-blocked-tile escape ---
            // If unit's current position overlaps an impassable tile, push toward nearest free tile
            if (IsBlocked(oldPos.X, oldPos.Y, r))
            {
                int unitGX = (int)MathF.Floor(oldPos.X / GameConstants.TileSize);
                int unitGY = (int)MathF.Floor(oldPos.Y / GameConstants.TileSize);
                float bestDist2 = 1e18f;
                Vec2 bestPos = oldPos;
                bool found = false;

                int searchRadius = 20;
                for (int dy = -searchRadius; dy <= searchRadius && !found; dy++)
                {
                    for (int dx = -searchRadius; dx <= searchRadius; dx++)
                    {
                        int gx = unitGX + dx, gy = unitGY + dy;
                        if (!_grid.InBounds(gx, gy)) continue;
                        if (_grid.GetCost(gx, gy) == 255) continue;

                        float tx = (gx + 0.5f) * GameConstants.TileSize;
                        float ty = (gy + 0.5f) * GameConstants.TileSize;
                        if (IsBlocked(tx, ty, r)) continue;

                        float d2 = (tx - oldPos.X) * (tx - oldPos.X) + (ty - oldPos.Y) * (ty - oldPos.Y);
                        if (d2 < bestDist2)
                        {
                            bestDist2 = d2;
                            bestPos = new Vec2(tx, ty);
                            found = true;
                        }
                    }
                }

                if (found)
                {
                    float dist = MathF.Sqrt(bestDist2);
                    float pushSpeed = _units[i].MaxSpeed * 3f;
                    float step = pushSpeed * dt;
                    if (dist <= step || dist < 0.1f)
                    {
                        oldPos = bestPos;
                    }
                    else
                    {
                        Vec2 dir = new((bestPos.X - oldPos.X) / dist, (bestPos.Y - oldPos.Y) / dist);
                        oldPos = new Vec2(oldPos.X + dir.X * step, oldPos.Y + dir.Y * step);
                    }
                    _units[i].Position = oldPos;
                }
            }

            // --- Stuck-inside-env-object escape ---
            // Env objects aren't on the walls-only grid anymore, so the wall escape
            // above won't catch a unit that spawned on a tree or had one placed on
            // it. Push outward along the vector from the closest overlapping
            // object's centre, same feel as the grid escape (MaxSpeed × 3).
            {
                envEntries.Clear();
                float selfR = _units[i].Radius;
                _envIndex.QueryRadius(oldPos, selfR, envEntries);
                float bestPenDist2 = 0f;
                Vec2 pushDir = Vec2.Zero;
                foreach (var e in envEntries)
                {
                    float dx = oldPos.X - e.CX, dy = oldPos.Y - e.CY;
                    float combined = selfR + e.Radius;
                    float d2 = dx * dx + dy * dy;
                    if (d2 >= combined * combined) continue;
                    // Penetration depth: how far inside the combined circle we are.
                    float pen = combined - MathF.Sqrt(d2);
                    if (pen * pen > bestPenDist2)
                    {
                        bestPenDist2 = pen * pen;
                        float len = MathF.Max(0.001f, MathF.Sqrt(d2));
                        pushDir = new Vec2(dx / len, dy / len);
                    }
                }
                if (bestPenDist2 > 0f)
                {
                    float pushSpeed = _units[i].MaxSpeed * 3f;
                    float step = pushSpeed * dt;
                    oldPos = new Vec2(oldPos.X + pushDir.X * step, oldPos.Y + pushDir.Y * step);
                    _units[i].Position = oldPos;
                }
            }

            // --- Axis-independent movement with gap probing ---
            Vec2 newPos = oldPos;

            // Try X movement, with perpendicular Y probe if blocked
            if (delta.X != 0f)
            {
                if (!IsBlocked(oldPos.X + delta.X, oldPos.Y, r))
                {
                    newPos = new Vec2(oldPos.X + delta.X, newPos.Y);
                }
                else
                {
                    // Probe small Y offsets to find a gap center
                    float[] probes = { 0.1f, -0.1f, 0.2f, -0.2f, 0.3f, -0.3f };
                    foreach (float off in probes)
                    {
                        float probeY = oldPos.Y + off;
                        if (!IsBlocked(oldPos.X, probeY, r) &&
                            !IsBlocked(oldPos.X + delta.X, probeY, r))
                        {
                            newPos = new Vec2(oldPos.X + delta.X, probeY);
                            break;
                        }
                    }
                }
            }

            // Try Y movement, with perpendicular X probe if blocked
            if (delta.Y != 0f)
            {
                if (!IsBlocked(newPos.X, oldPos.Y + delta.Y, r))
                {
                    newPos = new Vec2(newPos.X, oldPos.Y + delta.Y);
                }
                else if (newPos.Y == oldPos.Y) // only probe if Y wasn't already adjusted
                {
                    float[] probes = { 0.1f, -0.1f, 0.2f, -0.2f, 0.3f, -0.3f };
                    foreach (float off in probes)
                    {
                        float probeX = newPos.X + off;
                        if (!IsBlocked(probeX, oldPos.Y, r) &&
                            !IsBlocked(probeX, oldPos.Y + delta.Y, r))
                        {
                            newPos = new Vec2(probeX, oldPos.Y + delta.Y);
                            break;
                        }
                    }
                }
            }

            // --- Wall sliding ---
            // If both axes blocked but velocity is nonzero, compute wall normal
            // from nearby impassable tiles and slide along the tangent
            if (newPos.X == oldPos.X && newPos.Y == oldPos.Y &&
                (delta.X != 0f || delta.Y != 0f))
            {
                Vec2 wallNormal = Vec2.Zero;
                int gx0 = (int)MathF.Floor((oldPos.X - r * 2f) / GameConstants.TileSize);
                int gy0 = (int)MathF.Floor((oldPos.Y - r * 2f) / GameConstants.TileSize);
                int gx1 = (int)MathF.Floor((oldPos.X + r * 2f) / GameConstants.TileSize);
                int gy1 = (int)MathF.Floor((oldPos.Y + r * 2f) / GameConstants.TileSize);

                for (int gy = gy0; gy <= gy1; gy++)
                {
                    for (int gx = gx0; gx <= gx1; gx++)
                    {
                        if (!_grid.InBounds(gx, gy)) continue;
                        if (_grid.GetCost(gx, gy) != 255) continue;
                        float tileX = (gx + 0.5f) * GameConstants.TileSize;
                        float tileY = (gy + 0.5f) * GameConstants.TileSize;
                        float awayX = oldPos.X - tileX;
                        float awayY = oldPos.Y - tileY;
                        float d2 = awayX * awayX + awayY * awayY;
                        if (d2 > 0.0001f)
                        {
                            wallNormal = new Vec2(wallNormal.X + awayX / d2, wallNormal.Y + awayY / d2);
                        }
                    }
                }

                float nLen = wallNormal.Length();
                if (nLen > 0.001f)
                {
                    wallNormal = wallNormal * (1f / nLen);
                    // Project velocity onto wall tangent: slide = v - (v.n)*n
                    float dot = delta.X * wallNormal.X + delta.Y * wallNormal.Y;
                    Vec2 slide = new(delta.X - dot * wallNormal.X, delta.Y - dot * wallNormal.Y);

                    if (slide.LengthSq() > 0.0001f)
                    {
                        Vec2 slidePos = oldPos;
                        if (slide.X != 0f && !IsBlocked(oldPos.X + slide.X, oldPos.Y, r))
                            slidePos = new Vec2(oldPos.X + slide.X, slidePos.Y);
                        if (slide.Y != 0f && !IsBlocked(slidePos.X, oldPos.Y + slide.Y, r))
                            slidePos = new Vec2(slidePos.X, oldPos.Y + slide.Y);
                        newPos = slidePos;
                    }
                }
            }

            _units[i].Position = newPos;
        }
    }

    private bool IsBlocked(float px, float py, float r)
    {
        int gx0 = (int)MathF.Floor(px - r);
        int gy0 = (int)MathF.Floor(py - r);
        int gx1 = (int)MathF.Floor(px + r);
        int gy1 = (int)MathF.Floor(py + r);
        for (int gy = gy0; gy <= gy1; gy++)
            for (int gx = gx0; gx <= gx1; gx++)
                if (_grid.InBounds(gx, gy) && _grid.GetCost(gx, gy) == 255) return true;
        return false;
    }

    // --- Facing Angles ---
    // Delegates the per-unit rotation math to Movement.FacingUtil so the turn
    // rate cap is applied identically everywhere (UpdateFacingAngles, handler
    // FacePosition calls, legacy caster snap). Only the TARGET-ANGLE-SELECTION
    // priority logic lives here — the rotation step is shared.
    private void UpdateFacingAngles(float dt)
    {
        for (int i = 0; i < _units.Count; i++)
        {
            if (!_units[i].Alive) continue;
            // FacingUtil.TurnToward enforces these guards too, but short-circuiting
            // at this level saves the target-angle resolution for units that can't
            // rotate anyway.
            if (_units[i].Incap.IsLocked) continue;
            if (_units[i].JumpPhase >= 2) continue;
            if (_units[i].ChargePhase > 0) continue; // TrampleSystem owns facing during charge

            // Player-controlled (necromancer): two facing sources, hysteresis between
            // them. Walk gait → face the mouse (cursor angle stored in
            // _necroFacingOverride). Jog/Run gait → face actual velocity direction so
            // sprinting backward doesn't reverse-play the animation. Turn rate is
            // applied either way.
            if (_units[i].AI == AIBehavior.PlayerControlled)
            {
                if (_units[i].IsLockedByAction()) continue;

                var def = _gameData?.Units.Get(_units[i].UnitDefID);
                if (def != null)
                {
                    var profile = Render.LocomotionProfile.FromUnit(def);
                    float speed = _units[i].Velocity.Length();
                    float enterT = profile.JogThreshold + profile.JogHysteresis;
                    float exitT  = profile.JogThreshold - profile.JogHysteresis;
                    if (_units[i].FaceVelocityMode)
                    {
                        if (speed <= exitT) _units[i].FaceVelocityMode = false;
                    }
                    else
                    {
                        if (speed >= enterT) _units[i].FaceVelocityMode = true;
                    }
                }

                float targetAngle;
                if (_units[i].FaceVelocityMode && _units[i].Velocity.LengthSq() > 0.01f)
                {
                    targetAngle = MathF.Atan2(_units[i].Velocity.Y, _units[i].Velocity.X) * Rad2Deg;
                }
                else if (!float.IsNaN(_necroFacingOverride))
                {
                    targetAngle = _necroFacingOverride;
                }
                else
                {
                    continue; // nothing to aim at yet (pre-input frames)
                }
                Movement.FacingUtil.TurnToward(_units[i], targetAngle, dt, _gameData);
                continue;
            }

            // Priority 1: turn toward the engaged target — UNLESS we're actively
            // fleeing it. A unit retreating from its engaged target (e.g. a deer
            // bolting from an attacker) should face where it's GOING, not look back
            // over its shoulder; otherwise it runs away while facing the threat,
            // which reads as a backwards run under a forward-run animation. When the
            // velocity points away from the target we fall through to Priority 2
            // (face movement direction). Stationary-but-engaged units (a wolf waiting
            // out its cooldown) keep facing the target since velocity ~ 0.
            if (!_units[i].EngagedTarget.IsNone && _units[i].EngagedTarget.IsUnit)
            {
                int ti = ResolveUnitTarget(_units[i].EngagedTarget);
                if (ti >= 0)
                {
                    Vec2 toTarget = _units[ti].Position - _units[i].Position;
                    Vec2 vel = _units[i].Velocity;
                    bool fleeingTarget = vel.LengthSq() > 0.25f && vel.Dot(toTarget) < 0f;
                    if (!fleeingTarget)
                    {
                        Movement.FacingUtil.TurnTowardPosition(_units[i], _units[ti].Position, dt, _gameData);
                        continue;
                    }
                }
            }

            // Priority 2: Face movement direction. Prefer actual velocity over
            // intended direction so the body matches motion — important under
            // the Newtonian movement model where Velocity lags PreferredVel
            // during turns / decel. Only fall back to PreferredVel when the
            // unit is essentially stopped (about-to-start-moving anticipation).
            // The old threshold (0.316 wu/s mag) was safe only when Velocity
            // and PreferredVel were instantly aligned; under accel/decel it
            // caused the body to snap to PreferredVel direction while still
            // drifting along Velocity.
            Vec2 faceDir = _units[i].Velocity;
            if (faceDir.LengthSq() < 0.0025f) // < 0.05 wu/s — essentially stopped
                faceDir = _units[i].PreferredVel;

            // Priority 3: Stationary with a combat target (e.g. wolf waiting for
            // cooldown) — keep facing the target so the idle frame reads naturally.
            if (faceDir.LengthSq() < 0.0025f && _units[i].Target.IsUnit)
            {
                int ti = ResolveUnitTarget(_units[i].Target);
                if (ti >= 0)
                    faceDir = _units[ti].Position - _units[i].Position;
            }

            if (faceDir.LengthSq() > 0.0025f)
            {
                float targetAngle = MathF.Atan2(faceDir.Y, faceDir.X) * Rad2Deg;
                Movement.FacingUtil.TurnToward(_units[i], targetAngle, dt, _gameData);
            }
        }
    }

    /// <summary>
    /// Check if unit i is approximately facing unit target index ti.
    /// Returns true if the angle between facing direction and direction-to-target
    /// is within half of FacingThreshold.
    /// </summary>
    private bool IsFacingTarget(int i, int ti)
    {
        float threshold = _gameData?.Settings.Combat.FacingThreshold ?? 60f;
        Vec2 dir = _units[ti].Position - _units[i].Position;
        if (dir.LengthSq() < 0.001f) return true;
        float targetAngle = MathF.Atan2(dir.Y, dir.X) * Rad2Deg;
        float diff = MathF.Abs(Movement.FacingUtil.AngleDiff(targetAngle, _units[i].FacingAngle));
        return diff <= threshold * 0.5f;
    }

    // --- Combat ---
    private void UpdateCombat(float dt)
    {
        float meleeRange = _gameData?.Settings.Combat.MeleeRange ?? MeleeRangeBase;
        float roundDuration = _gameData?.Settings.Combat.RoundDuration ?? 3.0f;

        // Tick down post-attack timers
        for (int i = 0; i < _units.Count; i++)
        {
            if (_units[i].PostAttackTimer > 0f)
                _units[i].PostAttackTimer = MathF.Max(0f, _units[i].PostAttackTimer - dt);

            // Status symbol (? / !) above head
            if (_units[i].StatusSymbolTimer > 0f)
            {
                _units[i].StatusSymbolTimer -= dt;
                if (_units[i].StatusSymbolTimer <= 0f)
                {
                    _units[i].StatusSymbolTimer = 0f;
                    _units[i].StatusSymbol = 0;
                }
            }
        }

        // Derive InCombat from EngagedTarget + range. This is the ONLY writer of
        // InCombat — handlers and other systems read it as a stable flag (one-frame-
        // stale because this runs after AI; handlers see last frame's value).
        // Edge flags (JustEnteredCombat / JustLeftCombat) are set exactly on the
        // frame of transition so event-driven systems can react without polling.
        for (int i = 0; i < _units.Count; i++)
        {
            bool wasInCombat = _units[i].InCombat;
            _units[i].InCombat = false;
            _units[i].JustEnteredCombat = false;
            _units[i].JustLeftCombat = false;
            if (!_units[i].Alive || _units[i].EngagedTarget.IsNone)
            {
                if (wasInCombat) _units[i].JustLeftCombat = true;
                continue;
            }
            if (!_units[i].EngagedTarget.IsUnit)
            {
                if (wasInCombat) _units[i].JustLeftCombat = true;
                continue;
            }
            int ti = ResolveUnitTarget(_units[i].EngagedTarget);
            if (ti < 0)
            {
                // Target dead or gone — clear engagement
                _units[i].EngagedTarget = CombatTarget.None;
                if (wasInCombat) _units[i].JustLeftCombat = true;
                continue;
            }
            float dist = (_units[ti].Position - _units[i].Position).Length();
            float range = meleeRange + _units[i].Stats.Length * 0.15f + _units[i].Radius + _units[ti].Radius;
            if (dist <= range)
                _units[i].InCombat = true;

            if (_units[i].InCombat && !wasInCombat) _units[i].JustEnteredCombat = true;
            else if (!_units[i].InCombat && wasInCombat) _units[i].JustLeftCombat = true;
        }

        // Attack cooldowns and queuing
        for (int i = 0; i < _units.Count; i++)
        {
            if (!_units[i].Alive) continue;

            // Tick per-weapon cooldowns, then set the legacy AttackCooldown to the min
            // (= "time until SOME weapon is ready"). Per-weapon is what actually gates
            // each weapon's re-use; Unit.AttackCooldown is kept in sync for legacy
            // consumers (AI transitions, UI) that read it. For units with no melee
            // weapons (unarmed / test scenarios) we fall back to the legacy decay path.
            if (_units[i].Stats.MeleeWeapons.Count > 0)
            {
                float minCooldown = float.MaxValue;
                foreach (var w in _units[i].Stats.MeleeWeapons)
                {
                    if (w.Cooldown > 0f) w.Cooldown = MathF.Max(0f, w.Cooldown - dt);
                    if (w.Cooldown < minCooldown) minCooldown = w.Cooldown;
                }
                _units[i].AttackCooldown = minCooldown;
            }
            else
            {
                _units[i].AttackCooldown = MathF.Max(0f, _units[i].AttackCooldown - dt);
            }

            if (_units[i].Incap.IsLocked) continue;
            if (_units[i].Unconscious || _units[i].Fatigue >= 100f) continue; // exhausted: can't attack
            if (_units[i].Routing) continue; // broken units flee, they don't fight
            if (!_units[i].PendingAttack.IsNone) continue;
            if (_units[i].PostAttackTimer > 0f) continue; // one attack at a time
            if (_units[i].JumpPhase != 0) continue;        // already airborne / mid-pounce
            if (_units[i].ChargePhase != 0) continue;      // already charging / recovering

            // Unified attack selection: scan weapons in list order. First weapon that is
            // off cooldown AND has its target in its own range AND the unit is facing
            // (with a target acquired) fires. "One at a time" is enforced by
            // PostAttackTimer above. Weapon-list order is the priority — e.g. wolves list
            // Bite (index 0) first, Pounce (index 1) second: on approach only Pounce is
            // in range so it fires; after landing only Bite is in range so it fires.
            if (_units[i].Stats.MeleeWeapons.Count == 0)
            {
                // Unarmed / test scenario fallback: legacy Unit.AttackCooldown gate.
                if (_units[i].AttackCooldown > 0f) continue;
                if (!_units[i].InCombat) continue;
                if (_units[i].EngagedTarget.IsNone || !_units[i].EngagedTarget.IsUnit) continue;
                int tUnarmed = ResolveUnitTarget(_units[i].EngagedTarget);
                if (tUnarmed < 0 || !IsFacingTarget(i, tUnarmed)) continue;
                float dCycle = roundDuration;
                _units[i].PendingAttack = _units[i].EngagedTarget;
                _units[i].PendingWeaponIdx = -1;
                _units[i].PendingWeaponIsRanged = false;
                _units[i].PendingRangedTarget = GameConstants.InvalidUnit;
                _units[i].AttackCooldown = dCycle;
                _units[i].PostAttackTimer = MathF.Min(dCycle, GetAttackAnimDurationSec(i, -1));
                continue;
            }

            // Need a target (combat Target — for pounce's pre-melee check — or the
            // engagement target for normal melee).
            var attackTarget = !_units[i].Target.IsNone ? _units[i].Target : _units[i].EngagedTarget;
            if (attackTarget.IsNone || !attackTarget.IsUnit) continue;
            int ti = ResolveUnitTarget(attackTarget);
            if (ti < 0) continue;
            if (!IsFacingTarget(i, ti)) continue;

            float dist = (_units[ti].Position - _units[i].Position).Length();

            bool queued = false;
            for (int w = 0; w < _units[i].Stats.MeleeWeapons.Count && !queued; w++)
            {
                var ws = _units[i].Stats.MeleeWeapons[w];
                if (ws.Cooldown > 0f) continue;

                if (ws.Archetype == WeaponArchetype.Pounce)
                {
                    // In its pounce range? (Strict window, exclusive of melee-reach.)
                    if (dist < ws.PounceMinRange || dist > ws.PounceMaxRange) continue;
                    InitiatePounceWithWeapon(i, ti, w, roundDuration);
                    queued = true;
                }
                else if (ws.Archetype == WeaponArchetype.Trample)
                {
                    // Trample: can only charge a smaller-sized target in the range window.
                    if (_units[ti].Size >= _units[i].Size) continue;
                    if (dist < ws.TrampleMinRange || dist > ws.TrampleMaxRange) continue;
                    float cycle = Math.Max(1, ws.CooldownRounds) * roundDuration;
                    // No PendingAttack — TrampleSystem drives resolution continuously.
                    // Just lock the weapon cooldown and begin the charge.
                    GameSystems.TrampleSystem.BeginCharge(_units, i, ti, w, this);
                    ws.Cooldown = cycle;
                    _units[i].AttackCooldown = cycle;
                    queued = true;
                }
                else if (ws.Archetype == WeaponArchetype.Sweep)
                {
                    // Sweep: primary target must be in sweep radius AND inside the
                    // cone (we're already facing it via IsFacingTarget above, so the
                    // arc check here is just a sanity gate against edge cases).
                    if (dist > ws.SweepRadius) continue;
                    float cycle = Math.Max(1, ws.CooldownRounds) * roundDuration;
                    float animDur = MathF.Min(cycle, GetAttackAnimDurationSec(i, w));
                    _units[i].PendingAttack = CombatTarget.Unit(_units[ti].Id);
                    _units[i].PendingWeaponIdx = w;
                    _units[i].PendingWeaponIsRanged = false;
                    _units[i].PendingRangedTarget = GameConstants.InvalidUnit;
                    _units[i].CurrentAttackLungeDist = ws.LungeDist;
                    ws.Cooldown = cycle;
                    _units[i].AttackCooldown = cycle;
                    _units[i].PostAttackTimer = animDur;
                    _units[i].ActionLabel = ws.Name;
                    _units[i].ActionLabelTimer = animDur;
                    queued = true;
                }
                else
                {
                    // Normal melee: unit must be in combat (derived earlier from
                    // EngagedTarget+meleeRange+weapon length).
                    if (!_units[i].InCombat) continue;
                    if (_units[i].EngagedTarget.IsNone || !_units[i].EngagedTarget.IsUnit) continue;
                    float cycle = Math.Max(1, ws.CooldownRounds) * roundDuration;
                    float animDur = MathF.Min(cycle, GetAttackAnimDurationSec(i, w));
                    _units[i].PendingAttack = _units[i].EngagedTarget;
                    _units[i].PendingWeaponIdx = w;
                    _units[i].PendingWeaponIsRanged = false;
                    _units[i].PendingRangedTarget = GameConstants.InvalidUnit;
                    _units[i].CurrentAttackLungeDist = ws.LungeDist;
                    ws.Cooldown = cycle;
                    _units[i].AttackCooldown = cycle;
                    _units[i].PostAttackTimer = animDur;
                    _units[i].ActionLabel = ws.Name;
                    _units[i].ActionLabelTimer = animDur;
                    queued = true;
                }
            }
        }
    }

    /// <summary>
    /// Initiate a pounce using the specified weapon. Caller has already validated
    /// the weapon's archetype, cooldown, range, and facing. Locks the landing spot,
    /// calls JumpSystem.BeginPounce, and queues a melee attack so the landing
    /// callback resolves damage with the pounce weapon's stats.
    /// </summary>
    private void InitiatePounceWithWeapon(int i, int ti, int weaponIdx, float roundDuration)
    {
        var weapon = _units[i].Stats.MeleeWeapons[weaponIdx];

        // Locked landing spot: just short of the target, in melee range with a small margin.
        Vec2 toTarget = _units[ti].Position - _units[i].Position;
        float len = toTarget.Length();
        float standoff = _units[ti].Radius + _units[i].Radius + 0.2f;
        Vec2 landingPos = len > 0.01f
            ? _units[ti].Position - toTarget * (standoff / len)
            : _units[ti].Position;

        var def = _gameData?.Units.Get(_units[i].UnitDefID);
        string spriteName = def?.Sprite?.SpriteName ?? "";
        // Pounce traverses at sprint-top-speed regardless of current MaxSpeed
        // (a predator springing from idle still leaps fast). Falls back to
        // default biped sprint mult (4×) if def doesn't specify.
        float pounceSprintMult = (def?.SprintSpeedMultiplier > 0f)
            ? def.SprintSpeedMultiplier
            : Render.LocomotionProfile.DefaultSprintMult;
        float pounceSpeed = _units[i].Stats.CombatSpeed * pounceSprintMult;
        JumpSystem.BeginPounce(_units, i, landingPos, _units[ti].Id,
            _animMeta, spriteName, weapon.PounceArcPeak, speedOverride: pounceSpeed);

        // Queue the melee attack; JumpSystem resolves it at landing with this weapon.
        float cycle = Math.Max(1, weapon.CooldownRounds) * roundDuration;
        float animDur = MathF.Min(cycle, GetAttackAnimDurationSec(i, weaponIdx));
        _units[i].PendingAttack = CombatTarget.Unit(_units[ti].Id);
        _units[i].PendingWeaponIdx = weaponIdx;
        _units[i].PendingWeaponIsRanged = false;
        _units[i].PendingRangedTarget = GameConstants.InvalidUnit;
        weapon.Cooldown = cycle;
        _units[i].AttackCooldown = cycle;
        _units[i].PostAttackTimer = animDur;
        _units[i].ActionLabel = weapon.Name;
        _units[i].ActionLabelTimer = animDur;
    }

    /// <summary>Look up the ms-based anim duration for a unit's attack, using the
    /// weapon's AnimName override or the unit's AttackAnim (e.g. "AttackBite"),
    /// falling back to "Attack1".</summary>
    private float GetAttackAnimDurationSec(int unitIdx, int weaponIdx)
    {
        if (_animMeta == null) return 1.0f;
        var def = _gameData?.Units.Get(_units[unitIdx].UnitDefID);
        if (def?.Sprite == null) return 1.0f;

        string? animName = null;
        if (weaponIdx >= 0 && weaponIdx < _units[unitIdx].Stats.MeleeWeapons.Count)
            animName = _units[unitIdx].Stats.MeleeWeapons[weaponIdx].AnimName;
        if (string.IsNullOrEmpty(animName))
            animName = string.IsNullOrEmpty(def.AttackAnim) ? "Attack1" : def.AttackAnim;

        string key = Render.AnimMetaLoader.MetaKey(def.Sprite.SpriteName, animName);
        if (_animMeta.TryGetValue(key, out var meta))
        {
            int ms = meta.TotalDurationMs();
            if (ms > 0) return ms / 1000f;
        }
        return 1.0f;
    }

    public void ResolvePendingAttack(int unitIdx)
    {
        if (unitIdx < 0 || unitIdx >= _units.Count || !_units[unitIdx].Alive) return;
        var t = _units[unitIdx].PendingAttack;
        if (t.IsNone) return;

        // Snapshot pending weapon state, then clear so animation can re-queue cleanly.
        int weaponIdx = _units[unitIdx].PendingWeaponIdx;
        bool isRanged = _units[unitIdx].PendingWeaponIsRanged;
        uint rangedTargetID = _units[unitIdx].PendingRangedTarget;

        _units[unitIdx].PendingAttack = CombatTarget.None;
        _units[unitIdx].PendingWeaponIdx = -1;
        _units[unitIdx].PendingWeaponIsRanged = false;
        _units[unitIdx].PendingRangedTarget = GameConstants.InvalidUnit;

        // Ranged path: prefer stored target ID (target may have died/moved between queue and action moment).
        if (isRanged || _units[unitIdx].Archetype == AI.ArchetypeRegistry.ArcherUnit)
        {
            int defenderIdx = -1;
            if (rangedTargetID != GameConstants.InvalidUnit)
                defenderIdx = UnitUtil.ResolveUnitIndex(_units, rangedTargetID);
            if (defenderIdx < 0 && t.IsUnit)
                defenderIdx = ResolveUnitTarget(t);
            if (defenderIdx < 0) return;

            ref var stats = ref _units[unitIdx].Stats;
            int wIdx = (weaponIdx >= 0 && weaponIdx < stats.RangedDmg.Count) ? weaponIdx : 0;
            int damage = stats.RangedDmg.Count > 0 ? stats.RangedDmg[wIdx] : 8;
            float maxRange = stats.RangedRange.Count > 0 ? stats.RangedRange[wIdx] : 18f;
            float dist = (_units[defenderIdx].Position - _units[unitIdx].Position).Length();
            bool volley = dist > maxRange * 0.4f;
            _projectiles.SpawnArrow(_units[unitIdx].Position, _units[defenderIdx].Position,
                _units[unitIdx].Faction, _units[unitIdx].Id, damage, volley, 10,
                spawnHeight: _units[unitIdx].EffectSpawnHeight);
            return;
        }

        if (!t.IsUnit) return;
        int meleeDefenderIdx = ResolveUnitTarget(t);

        // Sweep: dispatch even if the primary target died — the cone may still
        // catch other victims. ResolveMeleeSweep handles missing-primary gracefully.
        WeaponStats? pendingWeapon = (weaponIdx >= 0 && weaponIdx < _units[unitIdx].Stats.MeleeWeapons.Count)
            ? _units[unitIdx].Stats.MeleeWeapons[weaponIdx] : null;
        if (pendingWeapon != null && pendingWeapon.Archetype == WeaponArchetype.Sweep)
        {
            ResolveMeleeSweep(unitIdx, meleeDefenderIdx, weaponIdx);
            return;
        }

        if (meleeDefenderIdx < 0)
        {
            // Target died between queue and resolve. Refund the commitment so the
            // attacker can immediately line up another target instead of standing
            // frozen through a full cooldown + PostAttackTimer for an unresolved
            // ghost-swing. Per-weapon Cooldown stays (the swing was "thrown" even
            // if it missed the dead target) but the post-attack movement lockout
            // clears so the unit can reorient.
            _units[unitIdx].PostAttackTimer = 0f;
            DebugLog.Log("ai",
                $"[ResolvePendingAttack] unit#{unitIdx} target vanished (id={t.UnitID}); refunding PostAttackTimer");
            return;
        }

        ResolveMeleeAttack(unitIdx, meleeDefenderIdx, weaponIdx);
    }

    private static readonly List<uint> _sweepScratch = new(32);

    /// <summary>
    /// Sweep AOE melee: forward cone centered on attacker's facing. Queries the
    /// quadtree within SweepRadius, filters by cone arc (and faction unless
    /// SweepHitsAllies), then runs ResolveMeleeAttack against each victim. Each
    /// target rolls independently — hit/miss, damage, knockdown, coats all
    /// resolved per defender. Primary target is always included if still alive.
    /// </summary>
    private void ResolveMeleeSweep(int attackerIdx, int primaryDefenderIdx, int weaponIdx)
    {
        var atkStats = _units[attackerIdx].Stats;
        if (weaponIdx < 0 || weaponIdx >= atkStats.MeleeWeapons.Count) return;
        var weapon = atkStats.MeleeWeapons[weaponIdx];

        Vec2 origin = _units[attackerIdx].Position;
        // FacingAngle is stored in DEGREES (see Movement/FacingUtil.cs) — convert
        // before handing to the radian-expecting Cos/Sin.
        float facingRad = _units[attackerIdx].FacingAngle * (MathF.PI / 180f);
        float halfArcRad = weapon.SweepArcDegrees * 0.5f * (MathF.PI / 180f);
        float cosThreshold = MathF.Cos(halfArcRad);
        float facingCos = MathF.Cos(facingRad);
        float facingSin = MathF.Sin(facingRad);
        float radius = weapon.SweepRadius;

        Faction atkFaction = _units[attackerIdx].Faction;
        FactionMask mask = weapon.SweepHitsAllies
            ? FactionMask.All
            : FactionMaskExt.AllExcept(atkFaction);

        _sweepScratch.Clear();
        _quadtree.QueryRadiusByFaction(origin, radius, mask, _sweepScratch);

        int hitCount = 0;
        uint primaryID = primaryDefenderIdx >= 0 ? _units[primaryDefenderIdx].Id : GameConstants.InvalidUnit;
        bool primaryResolved = false;

        for (int k = 0; k < _sweepScratch.Count; k++)
        {
            int defIdx = UnitUtil.ResolveUnitIndex(_units, _sweepScratch[k]);
            if (defIdx < 0 || defIdx == attackerIdx || !_units[defIdx].Alive) continue;

            // Cone check: dot product between facing dir and (defender-origin) dir.
            Vec2 d = _units[defIdx].Position - origin;
            float dLen = d.Length();
            if (dLen < 0.0001f) { /* on top of us — count as in-cone */ }
            else
            {
                float dx = d.X / dLen;
                float dy = d.Y / dLen;
                float dot = facingCos * dx + facingSin * dy;
                if (dot < cosThreshold) continue;
            }

            if (_units[defIdx].Id == primaryID) primaryResolved = true;
            ResolveMeleeAttack(attackerIdx, defIdx, weaponIdx);
            hitCount++;
        }

        // If the primary target is still alive and wasn't picked up by the cone
        // scan (e.g. quadtree rebuild lag, edge rounding), resolve against them
        // anyway so the AI's chosen target always gets swung at.
        if (!primaryResolved && primaryDefenderIdx >= 0 && _units[primaryDefenderIdx].Alive)
        {
            ResolveMeleeAttack(attackerIdx, primaryDefenderIdx, weaponIdx);
            hitCount++;
        }

        if (hitCount == 0 && primaryDefenderIdx < 0)
        {
            // No victims and primary already dead — refund post-attack timer so the
            // bear isn't locked out swinging at air.
            _units[attackerIdx].PostAttackTimer = 0f;
        }

        DebugLog.Log("combat",
            $"[Sweep] unit#{attackerIdx} ({weapon.Name}) hit {hitCount} target(s) " +
            $"arc={weapon.SweepArcDegrees:F0}° r={weapon.SweepRadius:F1}");
    }

    /// <summary>Public entrypoint for non-standard melee dispatchers (TrampleSystem,
    /// SweepSystem, etc.) that resolve multiple hits from a single archetype action
    /// without routing through PendingAttack. Delegates to the core resolver.</summary>
    public void ResolveMeleeAttackExternal(int attackerIdx, int defenderIdx, int weaponIdx,
        bool suppressDodgeAnim = false, bool forceHit = false, bool peekOnly = false)
        => ResolveMeleeAttack(attackerIdx, defenderIdx, weaponIdx, suppressDodgeAnim, forceHit, peekOnly);

    /// <summary>True if the previous ResolveMeleeAttackExternal call was a hit.
    /// Captured per-thread; trample uses this to decide between knockback vs dodge
    /// without re-rolling the dice or peeking at HP deltas.</summary>
    public bool LastMeleeAttackHit { get; private set; }

    /// <param name="peekOnly">Roll the dice and set LastMeleeAttackHit, but apply
    /// no side effects (no damage, no anim, no buffs, no log entry). Trample uses
    /// this to decide hit/miss BEFORE applying knockback — the actual damage call
    /// follows with forceHit so the corpse inherits the velocity if the unit dies.</param>
    private void ResolveMeleeAttack(int attackerIdx, int defenderIdx, int weaponIdx,
        bool suppressDodgeAnim = false, bool forceHit = false, bool peekOnly = false)
    {
        var atkStats = _units[attackerIdx].Stats;
        var defStats = _units[defenderIdx].Stats;

        // Resolve per-weapon stats so multi-weapon units (e.g. wolf's Bite + Pounce)
        // use the correct weapon's damage/length/name/bonuses. Falls back to the
        // aggregated unit stats for single-weapon or unarmed units.
        WeaponStats? weapon = (weaponIdx >= 0 && weaponIdx < atkStats.MeleeWeapons.Count)
            ? atkStats.MeleeWeapons[weaponIdx] : null;
        int weaponDamage = weapon?.Damage ?? atkStats.Damage;
        int weaponAttackBonus = weapon?.AttackBonus ?? 0;
        int weaponLength = weapon?.Length ?? atkStats.Length;
        string weaponName = weapon?.Name
            ?? (atkStats.MeleeWeapons.Count > 0 ? atkStats.MeleeWeapons[0].Name : "Unarmed");
        bool weaponKnockdown = weapon?.HasKnockdown ?? atkStats.HasKnockdown;

        // Every attempted melee swing fatigues the attacker by their Encumbrance.
        // Fatigue caps at 100 (that's the ceiling of the (100 - Fatigue) knockdown-
        // recovery formula). Regens at 1/tick every FatigueRegenInterval seconds.
        _units[attackerIdx].Fatigue = MathF.Min(100f, _units[attackerIdx].Fatigue + atkStats.Encumbrance);

        int atkDRN = UnitUtil.RollDRN();
        int defDRN = UnitUtil.RollDRN();

        // Fatigue penalties (manual p.61): attack −1 per 20 fatigue, defense −1 per
        // 10 fatigue (rounded down). This is what makes a tired unit easier to hit
        // and less likely to land its own blows — the master clock of melee.
        int atkFatiguePenalty = (int)(_units[attackerIdx].Fatigue / 20f);
        int defFatiguePenalty = (int)(_units[defenderIdx].Fatigue / 10f);

        // The defender's wielded weapon contributes its DefenseBonus, and its shield
        // contributes ShieldDefense (a penalty in the data, e.g. −1) — both were
        // previously dropped on the floor while the attacker's AttackBonus was applied.
        int defWeaponDefBonus = defStats.MeleeWeapons.Count > 0 ? defStats.MeleeWeapons[0].DefenseBonus : 0;

        // Apply paralysis reduction + buff modifiers (e.g. knockdown reduces defense by 70%)
        float atkParalysis = PotionSystem.GetParalysisFraction(_units, attackerIdx);
        float defParalysis = PotionSystem.GetParalysisFraction(_units, defenderIdx);
        float buffedAtk = BuffSystem.GetModifiedStat(_units, attackerIdx, BuffStat.Attack, atkStats.Attack + weaponAttackBonus);
        float buffedDef = BuffSystem.GetModifiedStat(_units, defenderIdx, BuffStat.Defense, defStats.Defense);
        int effectiveAtk = (int)(buffedAtk * atkParalysis) - atkFatiguePenalty;
        int effectiveDef = (int)(buffedDef * defParalysis) + defWeaponDefBonus + defStats.ShieldDefense - defFatiguePenalty;

        int modAtk = effectiveAtk + atkDRN;
        int harassment = _units[defenderIdx].Harassment;
        int modDef = effectiveDef - harassment + defDRN;

        var logEntry = new CombatLogEntry
        {
            Timestamp = _gameTime,
            AttackerName = GetUnitDisplayName(attackerIdx),
            DefenderName = GetUnitDisplayName(defenderIdx),
            AttackerFaction = _units[attackerIdx].Faction == Faction.Undead ? 'A' : _units[attackerIdx].Faction == Faction.Animal ? 'C' : 'B',
            DefenderFaction = _units[defenderIdx].Faction == Faction.Undead ? 'A' : _units[defenderIdx].Faction == Faction.Animal ? 'C' : 'B',
            WeaponName = weaponName,
            AttackBase = atkStats.Attack,
            AttackDRN = atkDRN,
            DefenseBase = defStats.Defense,
            DefenseDRN = defDRN,
            HarassmentPenalty = harassment
        };

        bool hit = forceHit ? true : (modAtk >= modDef);
        LastMeleeAttackHit = hit;
        // peekOnly: caller wants only the hit/miss decision (above); skip all
        // side effects below. Useful for callers that need to apply physics
        // BEFORE damage so corpses inherit knockback velocity (see TrampleSystem).
        if (peekOnly) return;

        if (!hit)
        {
            _units[defenderIdx].Harassment++;
            _units[defenderIdx].Dodging = true;
            // Don't play a Dodge anim on a prone target — they can't dodge while
            // knocked down. Leaving the knockdown hold in place. Also skip mid-jump
            // so the dodge doesn't visually pop the jumper out of the arc.
            // suppressDodgeAnim: trample owns its own dodge anim (snappy 0.4s hop)
            // and only plays it after confirming a safe tile exists, so the standard
            // dodge anim must not flash here.
            if (!suppressDodgeAnim && !_units[defenderIdx].Incap.Active && _units[defenderIdx].JumpPhase == 0)
            {
                // Dodge one-shot at Priority=1 (intentionally below Combat=2 so a
                // mid-attack dodge doesn't cancel its own swing — the attack anim
                // continues, the swing just didn't land on the defender because of
                // the hit/miss roll this method is returning from).
                Render.AnimResolver.SetOverride(_units[defenderIdx], new Render.AnimRequest
                {
                    State = Render.AnimState.Dodge, Priority = 1, Interrupt = true,
                    Kind = Render.OverrideKind.OneShot, Duration = 0, PlaybackSpeed = 1f
                });
            }
            // Record the swing so retarget-on-hit AI still fires on misses — a missed
            // attack is still active combat engagement. Without this, a low-attack unit
            // (e.g. zombie deer vs high-defense wolf) whiffs repeatedly and the wolf
            // never realizes it's being attacked.
            _units[defenderIdx].LastAttackerID = _units[attackerIdx].Id;
            _units[defenderIdx].LastHitTime = _gameTime;
            if (_units[defenderIdx].Archetype == AI.ArchetypeRegistry.WolfPack)
            {
                DebugLog.Log("wolf_retarget",
                    $"[Wolf {_units[defenderIdx].Id}] swung at by id={_units[attackerIdx].Id} " +
                    $"({_units[attackerIdx].UnitDefID}) → MISS at t={_gameTime:F2}s (LastHitTime + LastAttackerID set)");
            }
            logEntry.Outcome = CombatLogOutcome.Miss;
            _combatLog.AddEntry(logEntry);
            return;
        }

        // Shield hit (manual p.59-60): a shield interposes unless the attack also
        // beats Defense + Parry. On a shield hit the shield's Protection is added to
        // the defender's protection roll; a clean hit (attack beat def+parry) ignores it.
        bool hasShield = defStats.ShieldProtection > 0 || defStats.ShieldParry > 0;
        bool shieldHit = hasShield && (modAtk < modDef + defStats.ShieldParry);

        // Hit location
        var hitLoc = UnitUtil.RollHitLocation(_units[attackerIdx].Size, _units[defenderIdx].Size, weaponLength);

        // Weapon damage type + two-handedness (inferred from weapon name) and AP/AN.
        WeaponDamageType wType = weapon?.DamageType
            ?? (atkStats.MeleeWeapons.Count > 0 ? atkStats.MeleeWeapons[0].DamageType : WeaponDamageType.Slashing);
        bool twoHanded = weapon?.TwoHanded
            ?? (atkStats.MeleeWeapons.Count > 0 && atkStats.MeleeWeapons[0].TwoHanded);
        bool weaponAP = weapon?.HasArmorPiercing ?? atkStats.HasArmorPiercing;
        bool weaponAN = weapon?.HasArmorNegating ?? atkStats.HasArmorNegating;

        // Damage roll: Strength (×1.25 if two-handed, manual p.61) + weapon damage + DRN.
        int strContribution = twoHanded ? (int)(atkStats.Strength * 1.25f) : atkStats.Strength;
        int baseDmg = (int)((strContribution + weaponDamage) * atkParalysis);
        int dmgDRN = UnitUtil.RollDRN();
        int dmgRoll = baseDmg + dmgDRN;
        // Blunt: +25% on HEAD hits, BEFORE protection is deducted.
        if (wType == WeaponDamageType.Blunt && hitLoc == HitLocation.Head)
            dmgRoll = (int)(dmgRoll * 1.25f);

        // Protection roll: location armor (head→helmet) + natural + shield-on-shield-hit,
        // with piercing / armor-piercing / armor-defeating reductions, then + DRN.
        int protDRN = UnitUtil.RollDRN();
        int armorProt = hitLoc == HitLocation.Head ? defStats.Armor.HeadProtection : defStats.Armor.BodyProtection;
        float protStat = defStats.NaturalProt + armorProt + (shieldHit ? defStats.ShieldProtection : 0);

        // Armor-defeating hit (manual p.60): a very low protection roll bypasses 25%
        // of armor, gated by defender fatigue. Hard to land on a fresh unit.
        float defFatigue = _units[defenderIdx].Fatigue;
        bool armorDefeating = protDRN == 2
            || (protDRN == 3 && defFatigue >= 50f)
            || (protDRN == 4 && defFatigue >= 100f);

        if (weaponAN)
        {
            protStat = 0f; // armor-negating ignores protection entirely
        }
        else
        {
            float reduction = 0f;
            if (wType == WeaponDamageType.Piercing) reduction += 0.15f; // piercing weapon type
            if (weaponAP) reduction += 0.50f;                            // armor-piercing ability
            if (armorDefeating) reduction += 0.25f;                      // low protection roll
            reduction = MathF.Min(reduction, 1f);
            protStat *= (1f - reduction);
        }
        int prot = (int)protStat + protDRN;

        int netDmg = dmgRoll - prot;
        // Slashing: +25% AFTER protection is deducted (manual p.61).
        if (wType == WeaponDamageType.Slashing && netDmg > 0)
            netDmg = (int)(netDmg * 1.25f);
        // Limb cap (manual p.62): an arm/leg hit can't deal more than half max HP —
        // the limb is maimed instead of the whole body destroyed.
        if (hitLoc == HitLocation.Arms || hitLoc == HitLocation.Legs)
            netDmg = Math.Min(netDmg, Math.Max(1, BuffSystem.EffectiveMaxHP(_units, defenderIdx) / 2));
        // Dominions allows a glancing blow to deal zero damage when protection wins.
        if (netDmg < 0) netDmg = 0;

        logEntry.Outcome = CombatLogOutcome.Hit;
        logEntry.HitLoc = hitLoc;
        logEntry.HitLocationName = hitLoc.ToString();
        logEntry.DamageBase = baseDmg;
        logEntry.DamageDRN = dmgDRN;
        logEntry.ProtBase = (int)protStat;
        logEntry.ProtDRN = protDRN;
        logEntry.NetDamage = netDmg;
        _combatLog.AddEntry(logEntry);

        if (netDmg > 0)
        {
            _units[defenderIdx].HitReacting = true;
            _units[defenderIdx].LastHitTime = _gameTime;
            // Flinch gated by ApplyHitReactAnim (skips fleeing / prone / mid-jump /
            // refractory). HitReacting/LastHitTime stay set for AI reactions.
            DamageSystem.ApplyHitReactAnim(_units, defenderIdx);
        }
        else
        {
            _units[defenderIdx].BlockReacting = true;
            _units[defenderIdx].LastHitTime = _gameTime;
            DamageSystem.ApplyHitReactAnim(_units, defenderIdx);
        }

        // Diagnostic: log every melee hit on a wolf-archetype unit so we can verify
        // the retarget pipeline is seeing attackers.
        if (_units[defenderIdx].Archetype == AI.ArchetypeRegistry.WolfPack)
        {
            DebugLog.Log("wolf_retarget",
                $"[Wolf {_units[defenderIdx].Id}] hit by attacker id={_units[attackerIdx].Id} " +
                $"({_units[attackerIdx].UnitDefID}) netDmg={netDmg} at t={_gameTime:F2}s — " +
                $"LastHitTime set, LastAttackerID will be set in ApplyDirect");
        }

        // Melee uses ApplyDirect — armor already calculated above with DRN rolls
        DamageSystem.ApplyDirect(_units, defenderIdx, netDmg, _damageEvents, attackerIdx);

        // Limb loss / decapitation: a slashing blow that costs >= 50% max HP to a
        // limb or head maims it (manual p.60). Runs after damage so HP is current.
        if (netDmg > 0)
            TryApplyLimbChop(attackerIdx, defenderIdx, hitLoc, wType, netDmg);

        // On-hit: knockdown check if the SPECIFIC weapon used has the Knockdown bonus.
        // (Reading per-weapon means a wolf's Pounce can carry Knockdown without its
        // Bite also triggering it.) Triggers on any successful hit (including shield-
        // blocked hits — a block is not a full dodge), constrained to targets of size
        // ≤ attacker.size + 1.
        if (weaponKnockdown && _units[defenderIdx].Alive)
            TryApplyKnockdownOnHit(attackerIdx, defenderIdx);

        // Weapon coats: apply poison and/or zombie-on-death to defender
        if (hit && defenderIdx >= 0 && defenderIdx < _units.Count && _units[defenderIdx].Alive)
        {
            if (_units[attackerIdx].WeaponPoisonCoatTimer > 0f && _units[attackerIdx].WeaponPoisonAmount > 0)
            {
                // Weapon poison goes through armor (no AN flag)
                DamageSystem.Apply(_units, defenderIdx, _units[attackerIdx].WeaponPoisonAmount,
                    GameSystems.DamageType.Poison, GameSystems.DamageFlags.None, _damageEvents, attackerIdx);
            }
            if (_units[attackerIdx].WeaponZombieCoatTimer > 0f)
            {
                _units[defenderIdx].ZombieOnDeath = true;
            }

            // Per-unit weapon bonus effects (e.g. table-crafted permanent buffs).
            // Each effect runs independently and uses DamageSystem.Apply with its own
            // DamageType/Flags — that path does not re-enter this block, so an
            // effect like "5 poison on hit" cannot recurse and stack on itself.
            ApplyBonusEffectsOnHit(attackerIdx, defenderIdx);
        }
    }

    /// <summary>
    /// Iterate the attacker's per-unit BonusEffects list (lazy-allocated; null=empty)
    /// at the moment a melee hit lands and the defender is still alive. Roll any
    /// chance-gated entries; apply BonusDamage via DamageSystem.Apply; set
    /// ZombieOnDeath on the defender for ZombieOnDeath rolls that succeed.
    /// </summary>
    private void ApplyBonusEffectsOnHit(int attackerIdx, int defenderIdx)
    {
        var effects = _units[attackerIdx].BonusEffects;
        if (effects == null || effects.Count == 0) return;

        for (int i = 0; i < effects.Count; i++)
        {
            var e = effects[i];

            // Chance roll. 0 or 100 → always (0 default treated as "always" — explicit
            // BonusEffect.Damage() / ZombieOnDeath() factories set ChancePct=100).
            if (e.ChancePct > 0 && e.ChancePct < 100)
            {
                int roll = Random.Shared.Next(100);
                if (roll >= e.ChancePct) continue;
            }

            switch (e.Kind)
            {
                case GameSystems.BonusEffectKind.BonusDamage:
                    if (e.Amount > 0 && _units[defenderIdx].Alive)
                    {
                        DebugLog.Log("table",
                            $"[BonusEffect] attacker#{attackerIdx} ({_units[attackerIdx].UnitDefID}) → " +
                            $"defender#{defenderIdx} ({_units[defenderIdx].UnitDefID}) : " +
                            $"{e.Amount} {e.DmgType} dmg flags={e.DmgFlags}");
                        DamageSystem.Apply(_units, defenderIdx, e.Amount,
                            e.DmgType, e.DmgFlags, _damageEvents, attackerIdx);
                    }
                    break;

                case GameSystems.BonusEffectKind.ZombieOnDeath:
                    DebugLog.Log("table",
                        $"[BonusEffect] attacker#{attackerIdx} ({_units[attackerIdx].UnitDefID}) → " +
                        $"defender#{defenderIdx} ({_units[defenderIdx].UnitDefID}) : ZombieOnDeath set (chance={e.ChancePct}%)");
                    _units[defenderIdx].ZombieOnDeath = true;
                    break;
            }
        }
    }

    // --- Knockdown (weapon bonus) ---

    private const float KnockdownCheckInitialDelay = 2.0f;
    private const float KnockdownCheckInterval = 1.0f;
    private const int KnockdownMinDuration = 3; // seconds on a successful check

    /// <summary>
    /// Attempt a knockdown on the defender. Called after a successful melee hit
    /// when the attacker's weapon has the Knockdown bonus. Size-gated (target size
    /// must be ≤ attacker.size + 1), then an opposed STR + Size×2 + DRN roll;
    /// ties go to the attacker. On success, applies buff_knockdown with duration
    /// equal to the roll difference (min KnockdownMinDuration seconds).
    /// </summary>
    private void TryApplyKnockdownOnHit(int attackerIdx, int defenderIdx)
    {
        if (_units[defenderIdx].Size > _units[attackerIdx].Size + 1) return; // too big to knock down
        // Chargers are immune to knockdown mid-charge — a stray hit from a
        // smaller victim shouldn't cancel the commit. Still applies on transit
        // trample hits the OTHER way (charger knocking down victims), just not
        // FROM victims TO the charger. Both active charge (1) and follow-through (3).
        if (_units[defenderIdx].ChargePhase == 1 || _units[defenderIdx].ChargePhase == 3) return;

        int atkScore = _units[attackerIdx].Stats.Strength
                     + _units[attackerIdx].Size * 2
                     + UnitUtil.RollDRN();
        int defScore = _units[defenderIdx].Stats.Strength
                     + _units[defenderIdx].Size * 2
                     + UnitUtil.RollDRN();

        int diff = atkScore - defScore;
        if (diff < 0) return; // defender won (ties go to attacker)

        int durationSec = Math.Max(KnockdownMinDuration, diff);
        var knockdownBuff = _gameData?.Buffs.Get("buff_knockdown");
        if (knockdownBuff == null) return;

        BuffSystem.ApplyBuffWithDuration(_units, defenderIdx, knockdownBuff, durationSec);
        _units[defenderIdx].KnockdownCheckTimer = KnockdownCheckInitialDelay;

        DebugLog.Log("combat", $"[Knockdown] atk#{attackerIdx}(str={_units[attackerIdx].Stats.Strength}" +
            $",sz={_units[attackerIdx].Size},score={atkScore}) vs def#{defenderIdx}" +
            $"(str={_units[defenderIdx].Stats.Strength},sz={_units[defenderIdx].Size},score={defScore})" +
            $" → diff={diff} duration={durationSec}s");
    }

    /// <summary>
    /// Drive the unconscious (collapsed-from-exhaustion) state from Fatigue.
    /// At 100 fatigue the unit collapses into the Incap prone hold — reusing the
    /// knockdown mechanism so every existing Incap gate (movement/facing/attack)
    /// applies for free. It wakes once rested below the wake threshold. Called
    /// each fatigue tick (after regen) so the transition is hysteretic and cheap.
    /// </summary>
    private void UpdateUnconsciousState(int i)
    {
        if (!_units[i].Unconscious)
        {
            if (_units[i].Fatigue >= 100f)
            {
                _units[i].Unconscious = true;
                _units[i].PreferredVel = Vec2.Zero;
                _units[i].PendingAttack = CombatTarget.None;
                _units[i].EngagedTarget = CombatTarget.None;
                InstallCollapseHold(i);
                DebugLog.Log("combat", $"[Unconscious] unit#{i} ({_units[i].UnitDefID}) collapsed at fatigue={_units[i].Fatigue:F0}");
            }
        }
        else if (_units[i].Fatigue < UnconsciousWakeThreshold)
        {
            _units[i].Unconscious = false;
            // Release ONLY the hold the collapse owns — a coincident buff knockdown
            // (which also uses HoldAnim=Knockdown) must manage its own lifecycle.
            if (_units[i].Incap.Active && _units[i].Incap.CollapseOwned)
            {
                var incap = _units[i].Incap;
                incap.Active = false;
                incap.Recovering = false;
                incap.CollapseOwned = false;
                _units[i].Incap = incap;
            }
            DebugLog.Log("combat", $"[Unconscious] unit#{i} ({_units[i].UnitDefID}) recovered at fatigue={_units[i].Fatigue:F0}");
        }
        else if (!_units[i].Incap.IsLocked)
        {
            // Still unconscious and above the wake threshold, but nothing is holding us
            // prone — e.g. a buff knockdown active at collapse time has since expired.
            // Reinstall the collapse hold so the exhausted unit stays down for the rest of
            // the collapse instead of walking (the original install only ran on the
            // !Unconscious→Unconscious edge and never re-fired).
            InstallCollapseHold(i);
        }
    }

    /// <summary>Install the fatigue-collapse prone hold (Knockdown→Standup), tagged
    /// CollapseOwned so the wake path releases exactly this hold. No-op if a hold (buff or
    /// our own) is already active — that one owns its lifecycle.</summary>
    private void InstallCollapseHold(int i)
    {
        if (_units[i].Incap.Active) return;
        var incap = _units[i].Incap;
        incap.Active = true;
        incap.Recovering = false;
        incap.HoldAtEnd = false;
        incap.HoldAnim = Render.AnimState.Knockdown;
        incap.RecoverAnim = Render.AnimState.Standup;
        incap.CollapseOwned = true;
        _units[i].Incap = incap;
    }

    /// <summary>Fraction of max HP a single slashing limb/head hit must cost to
    /// sever that part (manual p.60). Tunable — with low-HP/high-damage units this
    /// triggers often; raise it if dismemberment feels too frequent.</summary>
    private const float LimbChopHpFraction = 0.5f;

    /// <summary>
    /// Slashing limb/head dismemberment (manual p.60). A slashing hit to an arm,
    /// leg, or head that costs >= 50% of max HP severs that part: arms/legs become
    /// permanent afflictions (stat penalties), a head is severed → instant death.
    /// </summary>
    private void TryApplyLimbChop(int attackerIdx, int defenderIdx, HitLocation loc, WeaponDamageType wType, int netDmg)
    {
        if (wType != WeaponDamageType.Slashing) return;
        if (!_units[defenderIdx].Alive) return; // already killed by the HP loss
        int threshold = Math.Max(1, (int)(BuffSystem.EffectiveMaxHP(_units, defenderIdx) * LimbChopHpFraction));
        if (netDmg < threshold) return;

        switch (loc)
        {
            case HitLocation.Head:
                // Decapitation — lethal.
                _units[defenderIdx].Stats.HP = 0;
                _units[defenderIdx].Alive = false;
                Render.AnimResolver.SetOverride(_units[defenderIdx], Render.AnimRequest.Forced(Render.AnimState.Death));
                DebugLog.Log("combat", $"[Decapitate] unit#{defenderIdx} ({_units[defenderIdx].UnitDefID}) beheaded by " +
                    $"unit#{attackerIdx} ({_units[attackerIdx].UnitDefID}) — netDmg={netDmg} >= {threshold}");
                break;
            case HitLocation.Arms:
                ApplyAffliction(defenderIdx, Data.Affliction.LostArm);
                break;
            case HitLocation.Legs:
                ApplyAffliction(defenderIdx, Data.Affliction.LostLeg);
                break;
        }
    }

    /// <summary>
    /// Apply a permanent affliction, baking its stat penalty straight into the
    /// unit's Stats (battle wounds persist for the fight). Each affliction applies
    /// at most once per unit.
    /// </summary>
    private void ApplyAffliction(int idx, Data.Affliction a)
    {
        if ((_units[idx].Afflictions & a) != 0) return; // already maimed there
        _units[idx].Afflictions |= a;
        var s = _units[idx].Stats;
        switch (a)
        {
            case Data.Affliction.LostArm:
                s.Attack = Math.Max(0, s.Attack - 4);
                s.Strength = Math.Max(0, s.Strength - 2);
                break;
            case Data.Affliction.LostLeg:
                s.Defense = Math.Max(0, s.Defense - 4);
                s.CombatSpeed *= 0.6f;
                break;
            case Data.Affliction.LostEye:
                s.Attack = Math.Max(0, s.Attack - 2);
                break;
        }
        DebugLog.Log("combat", $"[Affliction] unit#{idx} ({_units[idx].UnitDefID}) suffered {a} " +
            $"→ Att={s.Attack} Def={s.Defense} Str={s.Strength} Spd={s.CombatSpeed:F1}");
    }

    /// <summary>
    /// Morale check pass (Dominions-style, manual p.57). Amortized: every
    /// MoraleCheckInterval seconds each in-combat unit rolls Morale + 2*DRN vs a
    /// threshold that rises with its faction's casualties, local outnumbering, and
    /// its own wounds. A failure breaks the unit and it routs; once a faction drops
    /// below the army-rout HP fraction every one of its units breaks. Undead are
    /// mindless (will-bound to the necromancer) and never rout.
    /// </summary>
    private void UpdateMorale(float dt)
    {
        _moraleCheckTimer -= dt;
        if (_moraleCheckTimer > 0f) return;
        _moraleCheckTimer += MoraleCheckInterval;
        if (_quadtree == null) return;

        // Per-faction live HP and running peak (peak ≈ the faction's full strength).
        System.Array.Clear(_factionCurHP, 0, _factionCurHP.Length);
        for (int i = 0; i < _units.Count; i++)
        {
            if (!_units[i].Alive) continue;
            int f = (int)_units[i].Faction;
            if (f >= 0 && f < _factionCurHP.Length) _factionCurHP[f] += _units[i].Stats.HP;
        }
        for (int f = 0; f < _factionCurHP.Length; f++)
        {
            if (_factionCurHP[f] <= 0f) _factionPeakHP[f] = 0f;             // wiped → reset baseline
            else if (_factionCurHP[f] > _factionPeakHP[f]) _factionPeakHP[f] = _factionCurHP[f];
        }

        for (int i = 0; i < _units.Count; i++)
        {
            if (!_units[i].Alive || _units[i].Routing) continue;
            if (!_units[i].InCombat) continue;   // only units in the thick of it check
            if (IsFearless(i)) continue;

            int f = (int)_units[i].Faction;
            float peak = (f >= 0 && f < _factionPeakHP.Length) ? _factionPeakHP[f] : 0f;
            float lossFraction = peak > 0f ? 1f - _factionCurHP[f] / peak : 0f;

            // Army-wide collapse: once the side is below the rout HP fraction, break.
            if (lossFraction >= 1f - ArmyRoutHpFraction) { StartRouting(i); continue; }

            int effMaxHp = BuffSystem.EffectiveMaxHP(_units, i);
            float ownHpFrac = effMaxHp > 0 ? (float)_units[i].Stats.HP / effMaxHp : 1f;

            // Fresh, whole units hold the line — morale is driven by CASUALTIES, not
            // by being outnumbered at the outset. Only check once the side is taking
            // losses or this unit is personally wounded (Dominions: checks fire on
            // casualties, not at deployment).
            bool takingLosses = lossFraction > 0.10f;
            bool wounded = ownHpFrac < 0.6f;
            if (!takingLosses && !wounded) continue;

            // Local pressure: enemies vs allies within MoraleLocalRadius (a mild
            // modifier on top of the casualty-driven term).
            int enemies = _quadtree.QueryRadiusByFaction(_units[i].Position, MoraleLocalRadius,
                FactionMaskExt.AllExcept(_units[i].Faction), _moraleScratch);
            int allies = _quadtree.QueryRadiusByFaction(_units[i].Position, MoraleLocalRadius,
                _units[i].Faction.Bit(), _moraleScratch);
            int outnumber = Math.Max(0, enemies - allies);

            float threshold = MoraleBaseThreshold
                + lossFraction * 30f
                + outnumber * 0.5f
                + (ownHpFrac < 0.5f ? (0.5f - ownHpFrac) * 25f : 0f);

            int roll = _units[i].Stats.Morale + 2 * UnitUtil.RollDRN();
            if (roll < threshold)
            {
                DebugLog.Log("combat", $"[Morale] unit#{i} ({_units[i].UnitDefID}) BROKE — " +
                    $"morale={_units[i].Stats.Morale} roll={roll} < thr={threshold:F0} " +
                    $"(loss={lossFraction:P0} outnum={outnumber} hp={ownHpFrac:P0})");
                StartRouting(i);
            }
        }
    }

    /// <summary>Undead are mindless (will-bound to the necromancer) and never rout;
    /// any unit with Morale &gt;= the mindless threshold is likewise fearless.</summary>
    private bool IsFearless(int i)
        => _units[i].Faction == Faction.Undead || _units[i].Stats.Morale >= MindlessMoraleThreshold;

    /// <summary>Break a unit: it disengages and begins fleeing for at least the
    /// minimum rout duration.</summary>
    private void StartRouting(int i)
    {
        _units[i].Routing = true;
        _units[i].RoutTimer = MoraleRoutMinDuration;
        Disengage(i);
        _units[i].EngagedTarget = CombatTarget.None;
        _units[i].Target = CombatTarget.None;
        _units[i].PendingAttack = CombatTarget.None;
        _units[i].ShowStatusSymbol(UnitStatusSymbol.React, 1.5f);
    }

    /// <summary>Per-frame steering for a routing unit: sprint away from the nearest
    /// threat; rally once the min-rout timer elapses and no enemy is close.</summary>
    private void SteerRout(int i, float dt)
    {
        _units[i].EngagedTarget = CombatTarget.None;
        _units[i].Target = CombatTarget.None;
        _units[i].PendingAttack = CombatTarget.None;
        _units[i].MoveEffort = MoveEffort.Sprint;
        _units[i].RoutTimer -= dt;

        int threat = FindNearestEnemyIndex(i, MoraleLocalRadius * 1.5f);
        if (threat < 0 && _units[i].RoutTimer <= 0f)
        {
            // Safe and rested — rally back into the fight.
            _units[i].Routing = false;
            _units[i].MoveEffort = MoveEffort.Normal;
            _units[i].PreferredVel = Vec2.Zero;
            _units[i].ShowStatusSymbol(UnitStatusSymbol.Notice, 1f);
            return;
        }

        Vec2 away;
        if (threat >= 0)
        {
            away = _units[i].Position - _units[threat].Position;
            float d = away.Length();
            away = d > 0.01f ? away * (1f / d) : FleeFallbackDir(i);
        }
        else
        {
            away = FleeFallbackDir(i);
        }
        // Direct steering away from the threat — fleeing doesn't need a pathfind
        // (and pathfinding every frame for a whole routed army is a perf trap).
        // ORCA/movement handles obstacle avoidance from the velocity.
        _units[i].MoveTarget = _units[i].Position + away * RoutFleeDistance;
        _units[i].PreferredVel = away * _units[i].MaxSpeed;
    }

    /// <summary>Flee heading when no specific threat is resolvable: keep current
    /// momentum, else a default direction.</summary>
    private Vec2 FleeFallbackDir(int i)
    {
        Vec2 v = _units[i].Velocity;
        return v.LengthSq() > 0.01f ? v.Normalized() : new Vec2(0f, 1f);
    }

    private int FindNearestEnemyIndex(int i, float radius)
    {
        _quadtree.QueryRadiusByFaction(_units[i].Position, radius,
            FactionMaskExt.AllExcept(_units[i].Faction), _moraleScratch);
        int best = -1; float bestD = float.MaxValue;
        for (int k = 0; k < _moraleScratch.Count; k++)
        {
            int idx = UnitUtil.ResolveUnitIndex(_units, _moraleScratch[k]);
            if (idx < 0 || !_units[idx].Alive) continue;
            float d = (_units[idx].Position - _units[i].Position).LengthSq();
            if (d < bestD) { bestD = d; best = idx; }
        }
        return best;
    }

    /// <summary>
    /// Per-frame tick for the knockdown recovery-roll system. After the initial
    /// 2 s window, rolls every 1 s: SecondsLeft + DRN vs (100 − Fatigue) + DRN.
    /// If the defender wins the recovery roll, the buff is expired early.
    /// </summary>
    private void UpdateKnockdownRecovery(float dt)
    {
        for (int i = 0; i < _units.Count; i++)
        {
            if (!_units[i].Alive) continue;
            if (_units[i].KnockdownCheckTimer <= 0f) continue;

            // Find the active knockdown buff (if still present)
            int kdIdx = -1;
            for (int b = 0; b < _units[i].ActiveBuffs.Count; b++)
            {
                if (_units[i].ActiveBuffs[b].BuffDefID == "buff_knockdown") { kdIdx = b; break; }
            }
            if (kdIdx < 0)
            {
                // Buff already gone (natural expire / externally removed). Clear our timer.
                _units[i].KnockdownCheckTimer = 0f;
                continue;
            }

            _units[i].KnockdownCheckTimer -= dt;
            if (_units[i].KnockdownCheckTimer > 0f) continue;

            // Run the recovery check: knockdown side = secondsLeft + DRN, defender = (100-fatigue) + DRN.
            var buff = _units[i].ActiveBuffs[kdIdx];
            int secondsLeft = Math.Max(0, (int)MathF.Ceiling(buff.RemainingDuration));
            int kdScore = secondsLeft + UnitUtil.RollDRN();
            int defScore = (int)(100f - _units[i].Fatigue) + UnitUtil.RollDRN();

            if (defScore > kdScore)
            {
                // Recovery roll won — stand up immediately (expire the buff).
                buff.RemainingDuration = 0f;
                _units[i].ActiveBuffs[kdIdx] = buff;
                _units[i].KnockdownCheckTimer = 0f;
                DebugLog.Log("combat", $"[KnockdownRecovery] unit#{i} STANDUP kd={kdScore} def={defScore} left={secondsLeft}s");
            }
            else
            {
                // Stayed down — another check in 1 s.
                _units[i].KnockdownCheckTimer = KnockdownCheckInterval;
                DebugLog.Log("combat", $"[KnockdownRecovery] unit#{i} stays down kd={kdScore} def={defScore} left={secondsLeft}s");
            }
        }
    }

    /// <summary>
    /// Deal a fixed amount of armor-negating physical damage — used by magical strike
    /// spells and trap triggers that bypass armor by design. For normal combat damage
    /// go through DamageSystem.Apply directly so the caller controls the flags; this
    /// shortcut hard-codes ArmorNegating and should not be used for melee / ranged weapons.
    /// </summary>
    public void DealDamage(int unitIdx, int damage, int attackerIdx = -1) =>
        DamageSystem.Apply(_units, unitIdx, damage,
            GameSystems.DamageType.Physical, GameSystems.DamageFlags.ArmorNegating,
            _damageEvents, attackerIdx);

    // --- Helpers ---
    private void MoveTowardUnit(int i, int targetIdx, float speed)
    {
        MoveTowardPosition(i, _units[targetIdx].Position, speed);
    }

    /// <summary>Move unit toward a world position using pathfinding for longer distances.</summary>
    private void MoveTowardPosition(int i, Vec2 targetPos, float speed)
    {
        Vec2 myPos = _units[i].Position;
        float dist = (targetPos - myPos).Length();

        if (dist > 3f && _pathfinder != null && _pathfinder.Grid != null)
        {
            int sizeTier = TerrainCosts.SizeToTier(_units[i].Size);
            Vec2 dir = _pathfinder.GetDirection(myPos, targetPos, _frameNumber, sizeTier, i);
            _units[i].PreferredVel = dir * speed;
        }
        else
        {
            Vec2 dir = dist > 0.01f ? (targetPos - myPos) * (1f / dist) : Vec2.Zero;
            _units[i].PreferredVel = dir * speed;
        }
    }

    // Wolf AI phases
    private const byte WolfEngage = 0;
    private const byte WolfAttacking = 1;
    private const byte WolfDisengage = 2;
    private const byte WolfWaitCooldown = 3;
    private const float WolfAggroRange = 10f;
    private const float WolfAggroBreakRange = 15f;

    private void UpdateWolfAI(int i, float dt)
    {
        var ai = _units[i].AI;
        bool isolated = ai == AIBehavior.WolfHitAndRunIsolated || ai == AIBehavior.WolfOpportunistIsolated;
        bool opportunist = ai == AIBehavior.WolfOpportunist || ai == AIBehavior.WolfOpportunistIsolated;

        // Find/validate target with aggro range
        if (!IsTargetAlive(_units[i].Target))
        {
            if (isolated)
            {
                _units[i].Target = FindMostIsolatedEnemy(i); // returns CombatTarget directly
            }
            else
            {
                int found = FindClosestEnemy(i, WolfAggroRange);
                _units[i].Target = found >= 0 ? CombatTarget.Unit(_units[found].Id) : CombatTarget.None;
            }
            _units[i].WolfPhase = WolfEngage;
            _units[i].WolfPhaseTimer = 0;
        }

        int targetIdx = ResolveUnitTarget(_units[i].Target);
        if (targetIdx < 0)
        {
            _units[i].PreferredVel = Vec2.Zero;
            return;
        }

        // Drop target if beyond break range
        float breakDist = (_units[targetIdx].Position - _units[i].Position).Length();
        if (breakDist > WolfAggroBreakRange)
        {
            _units[i].Target = CombatTarget.None;
            _units[i].EngagedTarget = CombatTarget.None;
            _units[i].WolfPhase = WolfEngage;
            _units[i].WolfPhaseTimer = 0;
            _units[i].PreferredVel = Vec2.Zero;
            return;
        }

        Vec2 myPos = _units[i].Position;
        Vec2 targetPos = _units[targetIdx].Position;
        float dist = (targetPos - myPos).Length();
        // Use the same engage range as the combat system. This was hardcoded to
        // MeleeRangeBase, so it ignored a tuned Settings.Combat.MeleeRange and the
        // wolf would engage/disengage at a different distance than it attacks the
        // moment the setting diverges from the base (latent kiting bug).
        float meleeBase = _gameData?.Settings.Combat.MeleeRange ?? MeleeRangeBase;
        float attackRange = meleeBase + _units[i].Stats.Length * 0.15f + _units[i].Radius + _units[targetIdx].Radius;
        float disengageDist = attackRange + 2f; // back off 2 units beyond attack range
        float attackCooldown = _units[i].AttackCooldown;

        switch (_units[i].WolfPhase)
        {
            case WolfEngage:
            {
                // Move toward target
                if (dist <= attackRange)
                {
                    if (opportunist)
                    {
                        // Check if target is facing away (>100° from us)
                        if (IsTargetFacingAway(i, targetIdx, 100f) || _units[i].WolfPhaseTimer > attackCooldown)
                        {
                            // Opportunity! Or timeout — attack
                            _units[i].WolfPhase = WolfAttacking;
                            _units[i].WolfPhaseTimer = 0;
                            _units[i].EngagedTarget = _units[i].Target;
                        }
                        else
                        {
                            // Wait for opportunity, circle at edge of range
                            _units[i].WolfPhaseTimer += dt;
                            Vec2 perp = new Vec2(-(targetPos.Y - myPos.Y), targetPos.X - myPos.X);
                            if (perp.LengthSq() > 0.01f) perp = perp.Normalized();
                            _units[i].PreferredVel = perp * _units[i].MaxSpeed * 0.5f;
                        }
                    }
                    else
                    {
                        // Non-opportunist: attack immediately
                        _units[i].WolfPhase = WolfAttacking;
                        _units[i].WolfPhaseTimer = 0;
                        _units[i].EngagedTarget = _units[i].Target;
                    }
                }
                else
                {
                    MoveTowardUnit(i, targetIdx, _units[i].MaxSpeed);
                    if (opportunist) _units[i].WolfPhaseTimer += dt;
                }
                break;
            }

            case WolfAttacking:
            {
                // In melee range attacking — let combat system handle the attack
                // Once attack cooldown starts (we just attacked), transition to disengage
                if (attackCooldown > 0 && _units[i].WolfPhaseTimer > 0.2f)
                {
                    // We've been in attack phase long enough and cooldown started → disengage
                    _units[i].WolfPhase = WolfDisengage;
                    _units[i].WolfPhaseTimer = 0;
                    _units[i].EngagedTarget = CombatTarget.None;
                }
                else
                {
                    _units[i].WolfPhaseTimer += dt;
                    // Stay near target
                    if (dist > attackRange * 1.5f)
                        MoveTowardUnit(i, targetIdx, _units[i].MaxSpeed);
                    else
                        _units[i].PreferredVel = Vec2.Zero;
                }
                break;
            }

            case WolfDisengage:
            {
                // Force-clear engagement and lockout — wolf needs to move immediately
                _units[i].EngagedTarget = CombatTarget.None;
                _units[i].PendingAttack = CombatTarget.None;
                _units[i].PostAttackTimer = 0f;

                // Back away from target to maintain disengageDist
                if (dist < disengageDist)
                {
                    Vec2 awayDir = dist > 0.01f ? (myPos - targetPos) * (1f / dist) : new Vec2(1, 0);
                    _units[i].PreferredVel = awayDir * _units[i].MaxSpeed;
                }
                else
                {
                    // Reached safe distance, wait for cooldown
                    _units[i].WolfPhase = WolfWaitCooldown;
                    _units[i].WolfPhaseTimer = 0;
                    _units[i].PreferredVel = Vec2.Zero;
                }
                break;
            }

            case WolfWaitCooldown:
            {
                // Force-clear engagement but KEEP our target
                _units[i].EngagedTarget = CombatTarget.None;
                _units[i].PendingAttack = CombatTarget.None;

                // Maintain distance, wait for attack cooldown to expire
                if (dist < disengageDist - 0.5f)
                {
                    Vec2 awayDir = dist > 0.01f ? (myPos - targetPos) * (1f / dist) : new Vec2(1, 0);
                    _units[i].PreferredVel = awayDir * _units[i].MaxSpeed * 0.5f;
                }
                else if (attackCooldown <= 0)
                {
                    // Cooldown done — re-engage
                    _units[i].WolfPhase = WolfEngage;
                    _units[i].WolfPhaseTimer = 0;
                }
                else
                {
                    // Circle while waiting (emergent flanking when multiple wolves)
                    Vec2 toTarget = targetPos - myPos;
                    Vec2 perp = new Vec2(-toTarget.Y, toTarget.X);
                    if (perp.LengthSq() > 0.01f) perp = perp.Normalized();
                    _units[i].PreferredVel = perp * _units[i].MaxSpeed * 0.3f;
                }
                break;
            }
        }
    }

    /// <summary>Check if the target is facing away from us by more than angleDeg degrees.</summary>
    private bool IsTargetFacingAway(int unitIdx, int targetIdx, float angleDeg)
    {
        float targetFacing = _units[targetIdx].FacingAngle * MathF.PI / 180f;
        Vec2 targetFacingDir = new Vec2(MathF.Cos(targetFacing), MathF.Sin(targetFacing));
        Vec2 toUs = _units[unitIdx].Position - _units[targetIdx].Position;
        if (toUs.LengthSq() < 0.01f) return false;
        toUs = toUs.Normalized();
        // dot product: 1 = facing toward us, -1 = facing away
        float dot = targetFacingDir.X * toUs.X + targetFacingDir.Y * toUs.Y;
        float angleRad = angleDeg * MathF.PI / 180f;
        return dot < MathF.Cos(angleRad); // facing more than angleDeg away from us
    }

    /// <summary>Find the most isolated enemy (fewest allies nearby).</summary>
    private CombatTarget FindMostIsolatedEnemy(int unitIdx)
    {
        int bestIdx = -1;
        int bestAllyCount = int.MaxValue;
        float bestDist = float.MaxValue;

        for (int j = 0; j < _units.Count; j++)
        {
            if (!_units[j].Alive || _units[j].Faction == _units[unitIdx].Faction) continue;
            float dist = (_units[j].Position - _units[unitIdx].Position).LengthSq();
            if (dist > 40f * 40f) continue; // max acquisition range

            // Count allies near this enemy
            int allyCount = 0;
            for (int k = 0; k < _units.Count; k++)
            {
                if (k == j || !_units[k].Alive || _units[k].Faction != _units[j].Faction) continue;
                float allyDist = (_units[k].Position - _units[j].Position).LengthSq();
                if (allyDist < 8f * 8f) allyCount++; // allies within 8 units
            }

            // Prefer fewest allies, break ties by distance
            if (allyCount < bestAllyCount || (allyCount == bestAllyCount && dist < bestDist))
            {
                bestAllyCount = allyCount;
                bestDist = dist;
                bestIdx = j;
            }
        }

        return bestIdx >= 0 ? CombatTarget.Unit(_units[bestIdx].Id) : CombatTarget.None;
    }

    /// <summary>
    /// Cleanly disengage a unit from combat. Clears combat state and makes
    /// the unit's former target retarget to another enemy in range.
    /// </summary>
    private void Disengage(int unitIdx)
    {
        _units[unitIdx].EngagedTarget = CombatTarget.None;
        _units[unitIdx].InCombat = false;
        _units[unitIdx].PendingAttack = CombatTarget.None;
        _units[unitIdx].PostAttackTimer = 0f;

        // Make the former target retarget if possible
        var oldTarget = _units[unitIdx].Target;
        _units[unitIdx].Target = CombatTarget.None;

        if (oldTarget.IsUnit)
        {
            int targetIdx = ResolveUnitTarget(oldTarget);
            if (targetIdx >= 0 && _units[targetIdx].Alive)
            {
                // If the target was fighting us, make it pick a new target
                var theirTarget = _units[targetIdx].Target;
                if (theirTarget.IsUnit)
                {
                    int theirTargetIdx = ResolveUnitTarget(theirTarget);
                    if (theirTargetIdx == unitIdx)
                    {
                        // They were targeting us — find them a new target
                        int newTarget = FindClosestEnemy(targetIdx);
                        if (newTarget >= 0)
                            _units[targetIdx].Target = CombatTarget.Unit(_units[newTarget].Id);
                        else
                        {
                            _units[targetIdx].Target = CombatTarget.None;
                            _units[targetIdx].InCombat = false;
                        }
                    }
                }
            }
        }
    }

    private CombatTarget FindBestEnemyTarget(int unitIdx)
    {
        int closest = FindClosestEnemy(unitIdx);
        return closest >= 0 ? CombatTarget.Unit(_units[closest].Id) : CombatTarget.None;
    }

    private int FindClosestEnemy(int unitIdx, float maxRange = 0f)
    {
        float bestDist = float.MaxValue;
        float maxDist2 = maxRange > 0f ? maxRange * maxRange : float.MaxValue;
        int bestIdx = -1;
        for (int j = 0; j < _units.Count; j++)
        {
            if (j == unitIdx || !_units[j].Alive) continue;
            if (_units[j].Faction == _units[unitIdx].Faction) continue;
            float dist = (_units[j].Position - _units[unitIdx].Position).LengthSq();
            if (dist < bestDist && dist <= maxDist2) { bestDist = dist; bestIdx = j; }
        }
        return bestIdx;
    }

    private bool IsTargetAlive(CombatTarget t)
    {
        if (t.IsNone) return false;
        if (t.IsUnit) return ResolveUnitTarget(t) >= 0;
        return false;
    }

    private int ResolveUnitTarget(CombatTarget t) => UnitUtil.ResolveUnitIndex(_units, t.UnitID);

    private void RemoveDeadUnits()
    {
        for (int i = _units.Count - 1; i >= 0; i--)
        {
            if (!_units[i].Alive)
            {
                // Skill-tree event ticks. monster_kill / human_kill gate the
                // Monster Summoner / Brothers Keeper root nodes. Use UnitDef.Tags
                // when available — falls back to Faction so untagged units still
                // count. We only want kills by *us* / our undead, not random
                // peasant-vs-wolf scuffles, so require LastAttackerID to belong
                // to a player-aligned unit (Undead faction or PlayerControlled).
                if (_skillBookState != null && _gameData != null)
                {
                    uint attackerId = _units[i].LastAttackerID;
                    bool playerCausedKill = false;
                    if (attackerId != GameConstants.InvalidUnit)
                    {
                        int aIdx = UnitUtil.ResolveUnitIndex(_units, attackerId);
                        if (aIdx >= 0)
                            playerCausedKill = _units[aIdx].Faction == Faction.Undead
                                || _units[aIdx].AI == AIBehavior.PlayerControlled;
                    }
                    if (playerCausedKill)
                    {
                        var killedDef = _gameData.Units.Get(_units[i].UnitDefID);
                        bool isMonster = killedDef != null && killedDef.Tags.Contains("monster")
                            || _units[i].Faction == Faction.Animal;
                        bool isHuman = killedDef != null && killedDef.Tags.Contains("humanoid")
                            || _units[i].Faction == Faction.Human;
                        if (isMonster) PlayerEvents.Tally(PlayerEventTracker.Keys.MonsterKill);
                        if (isHuman)   PlayerEvents.Tally(PlayerEventTracker.Keys.HumanKill);
                    }
                }

                // Queue zombie raise if unit had ZombieOnDeath
                bool zombieRaise = _units[i].ZombieOnDeath;
                if (zombieRaise)
                {
                    _pendingZombieRaises.Add(new PendingZombieRaise
                    {
                        Position = _units[i].Position,
                        UnitDefID = _units[i].UnitDefID,
                        FacingAngle = _units[i].FacingAngle,
                        SpriteScale = _units[i].SpriteScale,
                        Timer = 1.0f
                    });
                }

                // If unit died mid-knockback, transfer physics state to corpse —
                // including the per-body gravity/drag scales so e.g. a trample-
                // killed corpse keeps its low-gravity arc instead of slamming
                // down with full Gravity at the speed of a dropped brick.
                bool wasInPhysics = _units[i].InPhysics;
                Vec2 corpseVelXY = Vec2.Zero;
                float corpseVelZ = 0f;
                float corpseGravityMul = 1f;
                float corpseDragMul = 1f;
                if (wasInPhysics)
                {
                    _physics.TryGetBodyVelocity(i, out corpseVelXY, out corpseVelZ);
                    _physics.TryGetBodyTuning(i, out corpseGravityMul, out corpseDragMul);
                }

                _corpses.Add(new Corpse
                {
                    Position = _units[i].Position,
                    UnitType = _units[i].Type,
                    UnitDefID = _units[i].UnitDefID,
                    FacingAngle = _units[i].FacingAngle,
                    // The dead-body sprite inherits the unit's scale (so a big bear
                    // dies as a big bear visual). The body BAG renders at a uniform
                    // size in DrawBaggedCorpse* paths (independent of corpse.SpriteScale)
                    // — that's where the "all bags same size" invariant lives.
                    SpriteScale = _units[i].SpriteScale,
                    CorpseID = _nextCorpseID++,
                    // Mark corpse as consumed so it dissolves while the zombie rises
                    Dissolving = zombieRaise,
                    ConsumedBySummon = zombieRaise,
                    // Continue physics arc if unit was mid-flight
                    InPhysics = wasInPhysics,
                    Z = _units[i].Z,
                    VelocityXY = corpseVelXY,
                    VelocityZ = corpseVelZ,
                    GravityMul = corpseGravityMul,
                    DragMul = corpseDragMul,
                });
                // Clean up the physics body now that the corpse has captured its
                // velocity — physics tick keeps bodies alive on death so the
                // velocity can be read here, but we must remove it before the
                // unit array shifts (otherwise UnitIdx points to wrong unit).
                if (wasInPhysics) _physics.RemoveBody(i);

                // RatPack fear: a dying rat spooks its nearby packmates (longer skitter
                // retreats for a moment). Done before the swap-and-pop remove so the
                // dead unit's position/faction are still valid.
                BroadcastRatPanicOnDeath(i);

                _units.RemoveUnit(i);
                if (_necromancerIdx == i) _necromancerIdx = -1;
                else if (_necromancerIdx == _units.Count) _necromancerIdx = i;
            }
        }
    }

    /// <summary>When a <see cref="AI.ArchetypeRegistry.RatPack"/> unit dies, pulse a
    /// short panic onto same-faction rats nearby so they recoil (their reflexive
    /// skitter retreats farther while PanicTimer is active). Linear scan — deaths are
    /// infrequent and rat counts are small.</summary>
    private void BroadcastRatPanicOnDeath(int deadIdx)
    {
        if (_units[deadIdx].Archetype != AI.ArchetypeRegistry.RatPack) return;
        const float PanicRadius = 8f;
        const float PanicDuration = 1.5f;
        float r2 = PanicRadius * PanicRadius;
        var pos = _units[deadIdx].Position;
        var fac = _units[deadIdx].Faction;
        for (int j = 0; j < _units.Count; j++)
        {
            if (j == deadIdx || !_units[j].Alive) continue;
            if (_units[j].Archetype != AI.ArchetypeRegistry.RatPack) continue;
            if (_units[j].Faction != fac) continue;
            if ((_units[j].Position - pos).LengthSq() > r2) continue;
            _units[j].PanicTimer = PanicDuration;
        }
    }

    private void UpdateCorpses(float dt)
    {
        // Update carried corpse positions to follow their carrier
        for (int u = 0; u < _units.Count; u++)
        {
            int carryID = _units[u].CarryingCorpseID;
            if (carryID < 0) continue;
            var corpse = FindCorpseByID(carryID);
            if (corpse == null) { _units[u].CarryingCorpseID = -1; continue; }
            if (!_units[u].Alive)
            {
                // Unit died while carrying — release the corpse
                corpse.DraggedByUnitID = GameConstants.InvalidUnit;
                _units[u].CarryingCorpseID = -1;
                _units[u].CorpseInteractPhase = 0;
                continue;
            }
            // Carried corpse follows at unit position (visual offset handled in rendering)
            corpse.Position = _units[u].Position;
        }

        // Release bagging if unit died
        for (int u = 0; u < _units.Count; u++)
        {
            int bagID = _units[u].BaggingCorpseID;
            if (bagID < 0) continue;
            if (!_units[u].Alive)
            {
                var corpse = FindCorpseByID(bagID);
                if (corpse != null) corpse.BaggedByUnitID = GameConstants.InvalidUnit;
                _units[u].BaggingCorpseID = -1;
                _units[u].CorpseInteractPhase = 0;
            }
        }

        // Tick corpse physics — corpses that died mid-knockback continue their arc
        for (int i = 0; i < _corpses.Count; i++)
        {
            if (!_corpses[i].InPhysics) continue;
            var c = _corpses[i];
            c.Position += c.VelocityXY * dt;
            c.Z += c.VelocityZ * dt;
            c.VelocityZ -= _physics.Gravity * c.GravityMul * dt;
            float drag = 1f - _physics.DefaultDrag * c.DragMul * dt;
            if (drag < 0f) drag = 0f;
            c.VelocityXY *= drag;
            if (c.Z <= 0f)
            {
                c.Z = 0f;
                c.InPhysics = false;
                c.VelocityXY = Vec2.Zero;
                c.VelocityZ = 0f;
            }
        }

        for (int i = _corpses.Count - 1; i >= 0; i--)
        {
            // Bagged corpses don't age or dissolve
            if (_corpses[i].Bagged && !_corpses[i].Dissolving && !_corpses[i].ConsumedBySummon)
                continue;

            _corpses[i].Age += dt;
            if (_corpses[i].Dissolving)
            {
                _corpses[i].DissolveTimer += dt;
                if (_corpses[i].DissolveTimer > 2f)
                {
                    int cid = _corpses[i].CorpseID;
                    // Release any unit carrying or bagging this corpse
                    for (int u = 0; u < _units.Count; u++)
                    {
                        if (_units[u].CarryingCorpseID == cid)
                        { _units[u].CarryingCorpseID = -1; _units[u].CorpseInteractPhase = 0; }
                        if (_units[u].BaggingCorpseID == cid)
                        { _units[u].BaggingCorpseID = -1; _units[u].CorpseInteractPhase = 0; }
                    }
                    _corpses.RemoveAt(i);
                }
            }
        }
    }

    public int SpawnUnitByID(string unitID, Vec2 pos)
    {
        int idx = _units.AddUnit(pos, UnitType.Dynamic);
        _units[idx].UnitDefID = unitID;

        // Apply UnitDef properties
        if (_gameData != null)
        {
            var def = _gameData.Units.Get(unitID);
            if (def != null)
            {
                _units[idx].SpriteScale = def.SpriteScale;
                _units[idx].Radius = def.Radius;
                _units[idx].Size = def.Size;
                _units[idx].OrcaPriority = def.OrcaPriority;

                if (!string.IsNullOrEmpty(def.Faction))
                    _units[idx].Faction = def.Faction == "Human" ? Faction.Human
                        : def.Faction == "Animal" ? Faction.Animal : Faction.Undead;

                if (Enum.TryParse<AIBehavior>(def.AI, out var ai))
                    _units[idx].AI = ai;

                var stats = _gameData.Units.BuildStats(unitID, _gameData.Weapons, _gameData.Armors, _gameData.Shields);
                _units[idx].Stats = stats;
                _units[idx].MaxSpeed = stats.CombatSpeed;

                // Apply any skill-tree intrinsic buffs whose tag filter matches
                // this unit's def. Skipped when SkillBook isn't wired (scenarios,
                // tests) — those paths get raw UnitDef behaviour with no skill
                // overlay.
                if (_skillBookState != null)
                {
                    foreach (var entry in _skillBookState.IntrinsicBuffs)
                    {
                        if (!def.HasAllTags(entry.RequiredTags)) continue;
                        var buffDef = _gameData.Buffs.Get(entry.BuffID);
                        if (buffDef == null) continue;
                        GameSystems.BuffSystem.ApplyBuff(_units, idx, buffDef, _gameData);
                    }
                }
            }
        }
        return idx;
    }

    /// <summary>
    /// Canonical "raise a corpse into the necromancer's horde" spawn. Use this
    /// for every reanimation that should join the horde (potion raise, table
    /// crafting, future paths) — NOT bare <see cref="SpawnUnitByID"/>.
    ///
    /// Why this exists: SpawnUnitByID only applies the def's *legacy* AI enum
    /// (e.g. ZombieFemaleDeer's "AttackClosest"); it does not wire the def's
    /// Archetype. A zombie spawned with bare SpawnUnitByID therefore runs the
    /// leash-less legacy AttackClosest AI, which — when chasing a fleeing enemy
    /// that outruns the horde's AggroRadius — pursues forever instead of
    /// returning at the leash. This helper applies the def's archetype
    /// (HordeMinion for the animal zombies) so the unit runs HordeMinionHandler
    /// with its proper leash, and enrolls it in the horde. Returns the unit
    /// index, or -1 on failure. Callers set facing / standup / bonuses after.
    /// </summary>
    public int SpawnZombieMinion(string unitID, Vec2 pos)
    {
        int idx = SpawnUnitByID(unitID, pos);
        if (idx < 0) return idx;

        _units[idx].Faction = Faction.Undead;

        // Resolve the def's archetype (HordeMinion for deer/wolf/bear zombies),
        // falling back to HordeMinion for any undead def that didn't specify one.
        byte arch = AI.ArchetypeRegistry.HordeMinion;
        var def = _gameData?.Units.Get(unitID);
        if (def != null && !string.IsNullOrEmpty(def.Archetype))
        {
            byte resolved = AI.ArchetypeRegistry.FromName(def.Archetype);
            if (resolved != AI.ArchetypeRegistry.None) arch = resolved;
        }
        _units[idx].Archetype = arch;
        _units[idx].Routine = 0; // Following

        // Cap-count safeguard. The horde count isn't an incremented counter — it's
        // derived live by HordeCapTracker, which counts undead units whose def
        // UndeadCategory is Monster/Human. So a minion only shows in the count (and
        // obeys the cap) if its def carries a category. A None category means it's
        // invisible to the count AND bypasses the cap — almost always a forgotten
        // undeadCategory on a new zombie def (this is exactly why reanimated rats
        // didn't count). Flag it loudly at the single raise-into-horde choke point so
        // it's caught at author time instead of discovered in-game.
        if (def != null && def.UndeadCategory == Data.Registries.UndeadCategory.None)
            DebugLog.Log("horde",
                $"[SpawnZombieMinion] '{unitID}' raised into the horde with undeadCategory=None — " +
                "it will NOT count toward any cap. Set undeadCategory (Monster/Human) on its def.");

        _horde.AddUnit(_units[idx].Id);
        return idx;
    }

    /// <summary>
    /// Mark a corpse as dissolving/consumed so it fades out and is removed.
    /// </summary>
    public void ConsumeCorpse(int corpseIdx)
    {
        if (corpseIdx >= 0 && corpseIdx < _corpses.Count)
        {
            _corpses[corpseIdx].Dissolving = true;
            _corpses[corpseIdx].ConsumedBySummon = true;
        }
    }

    /// <summary>
    /// Transform an existing unit into a new unit type, preserving position and facing.
    /// Matches C++ Simulation::transformUnit.
    /// </summary>
    public void TransformUnit(int unitIdx, string newUnitID)
    {
        if (_gameData == null || unitIdx < 0 || unitIdx >= _units.Count) return;
        var def = _gameData.Units.Get(newUnitID);
        if (def == null) return;

        // Rebuild stats from the new unit definition
        var newStats = _gameData.Units.BuildStats(newUnitID, _gameData.Weapons, _gameData.Armors, _gameData.Shields);
        _units[unitIdx].Stats = newStats;
        _units[unitIdx].UnitDefID = newUnitID;
        _units[unitIdx].Size = def.Size;
        _units[unitIdx].Radius = def.Radius;
        _units[unitIdx].MaxSpeed = newStats.CombatSpeed;
        _units[unitIdx].OrcaPriority = def.OrcaPriority;
        _units[unitIdx].Faction = def.Faction == "Human" ? Faction.Human : Faction.Undead;
        _units[unitIdx].SpriteScale = def.SpriteScale;
        // Reset HP to new max
        _units[unitIdx].Stats.HP = newStats.MaxHP;

        // Reset AI behavior from the new definition
        _units[unitIdx].AI = def.AI switch
        {
            "PlayerControlled" => AIBehavior.PlayerControlled,
            "AttackClosest" => AIBehavior.AttackClosest,
            "AttackNecromancer" => AIBehavior.AttackNecromancer,
            "GuardKnight" => AIBehavior.GuardKnight,
            "ArcherAttack" => AIBehavior.ArcherAttack,
            "CorpseWorker" => AIBehavior.CorpseWorker,
            _ => AIBehavior.AttackClosest
        };
    }

    /// <summary>
    /// Resolve a stable UnitID back to an index in the current UnitArrays.
    /// Returns -1 if not found.
    /// </summary>
    public int ResolveUnitID(uint unitID)
    {
        return UnitUtil.ResolveUnitIndex(_units, unitID);
    }

    public string GetUnitDisplayName(int unitIdx)
    {
        if (unitIdx < 0 || unitIdx >= _units.Count) return "Unknown";
        return _units[unitIdx].UnitDefID;
    }

    public void RebuildPathfinder()
    {
        // Walls first: they write TerrainType.Wall into the grid's terrain, which
        // RebuildCostField then reads into the cost field (and RebuildTieredCostFields
        // copies into the per-size tier fields). Baking walls after the cost field
        // would leave walls out of it on a from-scratch terrain. This ordering makes
        // the rebuild self-contained, so the caller no longer needs a separate bake.
        _wallSystem?.BakeWalls(_grid);
        _grid.RebuildCostField();
        _envSystem?.BakeCollisions(_grid);
        if (_envSystem != null)
            _envIndex.Rebuild(_envSystem, _grid.Width, _grid.Height);
        _pathfinder.Rebuild();
    }
}
