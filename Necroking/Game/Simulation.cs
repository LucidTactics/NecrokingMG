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

    // Bagging state
    public bool Bagged;
    public float BaggingProgress;
    public uint BaggedByUnitID = GameConstants.InvalidUnit;

    // Lerp anchor for pickup/putdown animation
    public Vec2 LerpStartPos;

    // Physics arc — corpse continues flying if unit died mid-knockback
    public bool InPhysics;
    public float Z;
    public Vec2 VelocityXY;
    public float VelocityZ;
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

    /// <summary>Build a floating-damage-number event. Pass a per-unit `height` only
    /// when you already have the correct value on hand (e.g. from a UnitDef's
    /// SpriteWorldHeight); otherwise leave it at DefaultHeight.</summary>
    public static DamageEvent Create(Vec2 position, int damage,
        float height = DefaultHeight, bool isPoison = false) => new()
    {
        Position = position, Damage = damage, Height = height, IsPoison = isPoison,
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
    private float _harassmentDecayTimer = CombatTickInterval;
    private int _nextCorpseID;
    private readonly List<PendingZombieRaise> _pendingZombieRaises = new();

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

    public void Init(int gridWidth, int gridHeight, GameData? gameData = null)
    {
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

        if (gameData?.Settings.Horde != null)
            _horde.Init(gameData.Settings.Horde);
    }

    public void SetEnvironmentSystem(EnvironmentSystem? es) { _envSystem = es; }
    public void SetWallSystem(WallSystem? ws) { _wallSystem = ws; }
    public void SetTriggerSystem(TriggerSystem? ts) { _triggerSystem = ts; }
    private TriggerSystem? _triggerSystem;
    public void SetNecromancerIndex(int idx) { _necromancerIdx = idx; }

    public void Tick(float dt)
    {
        _frameNumber++;
        _gameTime += dt;
        _damageEvents.Clear();

        // Clear per-frame flags (HitReacting cleared after AI, see below)
        for (int i = 0; i < _units.Count; i++)
        {
            _units[i].Dodging = false;
            _units[i].BlockReacting = false;
        }

        // Update mana and cooldowns
        _necroState.Mana = MathF.Min(_necroState.MaxMana, _necroState.Mana + _necroState.ManaRegen * dt);
        _necroState.TickCooldowns(dt);

        // Tick buffs
        BuffSystem.TickBuffs(_units, dt, _gameData?.Buffs);
        for (int i = 0; i < _units.Count; i++)
        {
            if (_units[i].ActiveBuffs.Count > 0)
                _units[i].MaxSpeed = BuffSystem.GetModifiedStat(_units, i, BuffStat.CombatSpeed, _units[i].Stats.CombatSpeed);
            else
                _units[i].MaxSpeed = _units[i].Stats.CombatSpeed;
        }

        // Standup/recovery timing now handled by IncapState inside BuffSystem.TickBuffs

        // Tick hit shake timers and clear dodge flags
        for (int i = 0; i < _units.Count; i++)
        {
            if (_units[i].HitShakeTimer > 0f)
                _units[i].HitShakeTimer = MathF.Max(0f, _units[i].HitShakeTimer - dt);
            _units[i].Dodging = false;
        }

        // Harassment decay
        _harassmentDecayTimer -= dt;
        if (_harassmentDecayTimer <= 0f)
        {
            _harassmentDecayTimer += CombatTickInterval;
            for (int i = 0; i < _units.Count; i++)
                if (_units[i].Harassment > 0)
                    _units[i].Harassment = (_units[i].Harassment + 1) / 2;
        }

        // Rebuild quadtree
        RebuildQuadtree();

        // Tick potion effects before AI so poison HitReacting is visible to flee logic
        PotionSystem.TickPotionEffects(_units, _damageEvents, dt);

        // Horde
        _horde.Tick(dt, _units, _necromancerIdx);

        // Core subsystems
        UpdateAI(dt);

        // Clear HitReacting AFTER AI has read it — this ensures flags set between frames
        // (e.g. spell AoE from Game1.Update) persist until the next AI tick sees them
        for (int i = 0; i < _units.Count; i++)
            _units[i].HitReacting = false;

        UpdateMovement(dt);         // Skips InPhysics units
        _physics.Update(dt, _units); // 2.5D impulse physics (flying units, collisions, landing)
        _horde.UpdateStates(_units, _quadtree, _necromancerIdx, dt);
        UpdateFacingAngles(dt);
        UpdateCombat(dt);

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
            int idx = SpawnUnitByID(spawnId, pos);
            if (idx >= 0)
            {
                _units[idx].Faction = Faction.Undead;
                _units[idx].FacingAngle = facing;
                _units[idx].StandupTimer = 1.5f;
                _units[idx].SpawnPosition = pos;
                // Set as horde minion and add to horde
                _units[idx].Archetype = AI.ArchetypeRegistry.HordeMinion;
                _units[idx].Routine = 0; // Following
                _horde.AddUnit(_units[idx].Id);
            }
        });

        // Projectiles (with quadtree collision, pass corpses for potion corpse-targeting)
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
            // Physics knockback before damage — units enter physics first so if
            // the damage kills them, the corpse inherits the knockback arc
            if (_gameData != null && !string.IsNullOrEmpty(hit.SpellID))
            {
                var spellDef = _gameData.Spells.Get(hit.SpellID);
                if (spellDef != null && spellDef.KnockbackForce > 0f)
                {
                    float kbRadius = spellDef.KnockbackRadius > 0f ? spellDef.KnockbackRadius : hit.AoeRadius;
                    _physics.ApplyRadialImpulse(_units, hit.ImpactPos, kbRadius,
                        spellDef.KnockbackForce, spellDef.KnockbackUpward, hit.OwnerFaction);
                }
            }

            if (hit.UnitIdx >= 0 && hit.UnitIdx < _units.Count && _units[hit.UnitIdx].Alive)
                DamageSystem.Apply(_units, hit.UnitIdx, hit.Damage,
                    GameSystems.DamageType.Physical, GameSystems.DamageFlags.ArmorNegating,
                    _damageEvents);
        }

        // Lightning
        var lightningDmg = new List<LightningDamage>();
        _lightning.Update(dt, lightningDmg, _quadtree, _units);
        foreach (var ld in lightningDmg)
            if (ld.UnitIdx >= 0 && ld.UnitIdx < _units.Count)
                DamageSystem.Apply(_units, ld.UnitIdx, ld.Damage,
                    GameSystems.DamageType.Physical, GameSystems.DamageFlags.ArmorNegating,
                    _damageEvents);

        // Poison clouds
        _poisonClouds.Update(dt, _units, _quadtree, _corpses, _damageEvents,
            _gameData?.Buffs);

        // Remove dead units
        RemoveDeadUnits();

        // Update corpses
        UpdateCorpses(dt);

        _flowFields.EvictIfNeeded();
    }

    private void RebuildQuadtree()
    {
        if (_units.Count <= 0) return;
        var positions = new Vec2[_units.Count];
        var ids = new uint[_units.Count];
        for (int i = 0; i < _units.Count; i++)
        {
            positions[i] = _units[i].Position;
            ids[i] = _units[i].Id;
        }
        _quadtree.Build(positions, ids, new AABB(0, 0, _grid.Width, _grid.Height));
    }

    // Player input (set by Game1 before Tick)
    private Vec2 _necroMoveInput;
    private bool _necroRunning;
    private float _necroFacingOverride = float.NaN;

    public void SetNecromancerInput(Vec2 moveDir, bool running)
    {
        _necroMoveInput = moveDir;
        _necroRunning = running;
    }

    public void SetNecromancerFacing(float angleDeg)
    {
        _necroFacingOverride = angleDeg;
        if (_necromancerIdx >= 0 && _necromancerIdx < _units.Count)
            _units[_necromancerIdx].FacingAngle = angleDeg;
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
        AI.AwarenessSystem.Update(_units, dt, (int)_frameNumber);

        for (int i = 0; i < _units.Count; i++)
        {
            if (!_units[i].Alive) continue;
            if (_units[i].InPhysics) continue; // Physics system owns this unit
            if (_units[i].Jumping || _units[i].Incap.IsLocked) { _units[i].PreferredVel = Vec2.Zero; continue; }

            // Clear AI-intent flags before the handler runs so each handler tick
            // is a fresh declaration. Idle sub-states re-set these inside the handler.
            _units[i].AnimIntentStill = false;

            // New archetype system: if Archetype > 0, dispatch to handler
            // (PlayerControlled units are handled in the legacy switch below)
            if (_units[i].Archetype > 0 && _units[i].AI != AIBehavior.PlayerControlled)
            {
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
                    };
                    handler.Update(ref ctx);
                }
                continue;
            }

            // Legacy AI: FleeWhenHit and wolf AIs handle their own combat/disengage logic
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
                    float speed = _units[i].Stats.CombatSpeed;
                    if (_units[i].GhostMode)
                        speed = 20.0f;
                    else if (_necroRunning && _units[i].CarryingCorpseID < 0)
                        speed *= 1.8f;
                    _units[i].MaxSpeed = speed; // update so ORCA + accel cap respect current speed

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
                            };
                            handler.Update(ref ctx);
                        }

                        // Player movement cancels active routines (WASD override)
                        if (_necroMoveInput.LengthSq() > 0.01f && _units[i].Routine != 0)
                        {
                            _units[i].Routine = 0;
                            _units[i].Subroutine = 0;
                            _units[i].CorpseInteractPhase = 0;
                            _units[i].BuildTargetIdx = -1;
                            _units[i].BuildGlyphIdx = -1;
                            _units[i].BuildTimer = 0f;
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
                        if (dist <= spell.Range &&
                            _units[i].Mana >= spell.ManaCost &&
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

                                // Apply direct damage to target
                                DamageSystem.Apply(_units, enemy, spell.Damage,
                                    GameSystems.DamageType.Physical, GameSystems.DamageFlags.ArmorNegating,
                                    _damageEvents);
                            }
                            else
                            {
                                // Sky lightning strike: telegraph then AOE
                                _lightning.SpawnStrike(_units[enemy].Position,
                                    spell.TelegraphDuration, spell.StrikeDuration,
                                    spell.AoeRadius, spell.Damage, style, spell.Id, visual, grp, tFilter,
                                    spell.TelegraphVisible);
                            }

                            _units[i].Mana -= spell.ManaCost;
                            _units[i].SpellCooldownTimer = spell.Cooldown;

                            // Apply casting buff
                            if (!string.IsNullOrEmpty(spell.CastingBuffID) && _gameData != null)
                            {
                                var castBuff = _gameData.Buffs.Get(spell.CastingBuffID);
                                if (castBuff != null) BuffSystem.ApplyBuff(_units, i, castBuff);
                            }

                            // Face target
                            var toEnemy = _units[enemy].Position - _units[i].Position;
                            if (toEnemy.LengthSq() > 0.01f)
                                _units[i].FacingAngle = MathF.Atan2(toEnemy.Y, toEnemy.X) * 180f / MathF.PI;
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
                    // Deer-like behavior: idle normally, flee when hit at effect time
                    if (_frameNumber % 120 == 0)
                        DebugLog.Log("ai", $"FleeWhenHit unit {i}: LastAttacker={_units[i].LastAttackerID}, FleeTimer={_units[i].FleeTimer:F1}, InCombat={_units[i].InCombat}, HP={_units[i].Stats.HP}");

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
                            if (_frameNumber % 60 == 0)
                            {
                                int tIdx = UnitUtil.ResolveUnitIndex(_units, chasingId);
                                float cDist = tIdx >= 0 ? (_units[tIdx].Position - _units[i].Position).Length() : -1;
                                Core.DebugLog.Log("horde", $"Unit {i} Chasing target={chasingId} tIdx={tIdx} dist={cDist:F1} vel={_units[i].PreferredVel.Length():F1}");
                            }
                        }
                    }

                    // Acquire new target (range-limited for horde units)
                    if (!IsTargetAlive(_units[i].Target))
                    {
                        if (inHorde)
                        {
                            int nearby = FindClosestEnemy(i, _horde.Settings.EngagementRange);
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
        }
    }

    // --- Movement (with ORCA) ---
    private void UpdateMovement(float dt)
    {
        var neighbors = new List<ORCANeighbor>();
        var nearbyIDs = new List<uint>();
        var envEntries = new List<EnvSpatialIndex.Entry>();

        for (int i = 0; i < _units.Count; i++)
        {
            if (!_units[i].Alive) continue;
            if (_units[i].InPhysics) continue; // Physics system owns this unit's movement
            // Movement blocked by: jumping, knockdown (buff), standup, pending attack, or post-attack lockout
            if (_units[i].Jumping || _units[i].Incap.IsLocked
                || !_units[i].PendingAttack.IsNone || _units[i].PostAttackTimer > 0f)
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

            // Build ORCA neighbor list
            neighbors.Clear();
            nearbyIDs.Clear();
            Vec2 newVel;
            var myPos = _units[i].Position;
            if (skipOrca)
            {
                // Player input goes straight through; other units still see the
                // necromancer via the quadtree so they dodge him normally.
                newVel = _units[i].PreferredVel;
            }
            else
            {
                float queryRadius = MathF.Max(_units[i].Radius * 5f, 3f);
                _quadtree.QueryRadius(_units[i].Position, queryRadius, nearbyIDs);

                foreach (uint nid in nearbyIDs)
                {
                    if (nid == _units[i].Id) continue;
                    int j = UnitUtil.ResolveUnitIndex(_units, nid);
                    if (j < 0 || !_units[j].Alive) continue;

                    neighbors.Add(new ORCANeighbor
                    {
                        Position = _units[j].Position,
                        Velocity = _units[j].Velocity,
                        Radius = _units[j].Radius,
                        Id = nid,
                        Priority = _units[j].Faction != _units[i].Faction
                            ? _units[i].OrcaPriority  // cross-faction: equal
                            : _units[j].OrcaPriority  // same-faction: respect hierarchy
                    });
                }

                // Sort dynamic neighbours by distance, keep closest 10
                neighbors.Sort((a, b) => (a.Position - myPos).LengthSq().CompareTo((b.Position - myPos).LengthSq()));
                if (neighbors.Count > 10) neighbors.RemoveRange(10, neighbors.Count - 10);

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

            // --- Turn penalty: reduce MoveTime when changing direction ---
            // Measures angle between previous velocity and new desired direction.
            // Small corrections (< threshold) are free; larger turns drain MoveTime
            // proportionally, so the unit must re-accelerate after sharp turns.
            // NOTE: To disable turn penalty for player, change this condition:
            float targetSpeed = newVel.Length();
            bool applyTurnPenalty = _units[i].AI != AIBehavior.PlayerControlled;
            if (applyTurnPenalty && targetSpeed > 0.001f && _units[i].Velocity.LengthSq() > 0.01f)
            {
                Vec2 oldDir = _units[i].Velocity.Normalized();
                Vec2 newDir = newVel * (1f / targetSpeed);
                // dot = cos(angle), clamp for safety
                float dot = MathUtil.Clamp(oldDir.X * newDir.X + oldDir.Y * newDir.Y, -1f, 1f);
                float angleDeg = MathF.Acos(dot) * Rad2Deg;

                const float TurnFreeThreshold = 20f;  // degrees — no penalty below this
                const float TurnFullPenalty = 180f;    // degrees — full MoveTime reset

                if (angleDeg > TurnFreeThreshold)
                {
                    // Blend: quadratic ramp for small turns, linear for large
                    // normalized 0..1 across the penalty range
                    float t = (angleDeg - TurnFreeThreshold) / (TurnFullPenalty - TurnFreeThreshold);
                    t = MathUtil.Clamp(t, 0f, 1f);
                    // Smoothstep-ish: gentle at small angles, aggressive at large
                    float penalty = t * t * (3f - 2f * t); // smoothstep [0,1]
                    _units[i].MoveTime *= (1f - penalty);
                }
            }

            // Apply exponential acceleration curve
            // speed = maxSpeed * (1 - e^(-k * moveTime))
            // k derived from accelHalfTime: k = -ln(0.5) / accelHalfTime
            if (targetSpeed > 0.001f)
            {
                _units[i].MoveTime += dt;

                var accelDef = _gameData?.Units.Get(_units[i].UnitDefID);
                float accelHalfTime = accelDef?.AccelHalfTime ?? _gameData?.Settings.Combat.AccelHalfTime ?? 1.2f;
                float accelK = 0.6931f / accelHalfTime; // ln(2) / halfTime
                // Player-controlled gets instant acceleration
                float speedFraction = _units[i].AI == AIBehavior.PlayerControlled
                    ? 1f
                    : 1f - MathF.Exp(-accelK * _units[i].MoveTime);
                float finalSpeed = MathF.Min(targetSpeed, _units[i].MaxSpeed * speedFraction);
                _units[i].Velocity = (newVel * (1f / targetSpeed)) * finalSpeed;
            }
            else
            {
                // Stopped — reset acceleration (no deceleration)
                _units[i].MoveTime = 0f;
                _units[i].Velocity = Vec2.Zero;
            }

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

            // Smoothed velocity for anim-layer "am I really moving" detection.
            // 0.5s time constant for actively-moving units — oscillations with
            // period < 1s damp toward zero; sustained motion (own locomotion, or
            // a persistent ORCA shove from a bigger neighbour) is preserved.
            //
            // When the AI declares still-intent we collapse the window to 0.1s so
            // the anim layer reaches Idle within a frame or two of arrival instead
            // of spending the full smoothing tail in Walk. A unit being genuinely
            // shoved has consistent directional velocity, so even with the short
            // window the EMA tracks the real motion and the anim still reads Walk.
            float emaTau = _units[i].AnimIntentStill ? 0.1f : 0.5f;
            float emaAlpha = 1f - MathF.Exp(-dt / emaTau);
            _units[i].VelocityEMA += (_units[i].Velocity - _units[i].VelocityEMA) * emaAlpha;
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
    private void UpdateFacingAngles(float dt)
    {
        float globalTurnSpeed = _gameData?.Settings.Combat.TurnSpeed ?? 360f;

        for (int i = 0; i < _units.Count; i++)
        {
            if (!_units[i].Alive) continue;
            if (_units[i].Incap.IsLocked) continue;

            // PlayerControlled: facing is set by mouse in Game1, don't override
            if (_units[i].AI == AIBehavior.PlayerControlled) continue;

            // Per-unit turn speed override
            float turnSpeed = globalTurnSpeed;
            var unitDef = _gameData?.Units.Get(_units[i].UnitDefID);
            if (unitDef?.TurnSpeed.HasValue == true)
                turnSpeed = unitDef.TurnSpeed.Value;

            // Priority 1: Always turn toward engaged target when one is set
            if (!_units[i].EngagedTarget.IsNone && _units[i].EngagedTarget.IsUnit)
            {
                int ti = ResolveUnitTarget(_units[i].EngagedTarget);
                if (ti >= 0)
                {
                    Vec2 dir = _units[ti].Position - _units[i].Position;
                    if (dir.LengthSq() > 0.001f)
                    {
                        float targetAngle = MathF.Atan2(dir.Y, dir.X) * Rad2Deg;
                        float diff = AngleDiff(targetAngle, _units[i].FacingAngle);
                        float maxTurn = turnSpeed * dt;
                        _units[i].FacingAngle += MathUtil.Clamp(diff, -maxTurn, maxTurn);
                    }
                    continue;
                }
            }

            // Priority 2: Face movement direction (actual velocity, or intended direction
            // if still accelerating from zero)
            Vec2 faceDir = _units[i].Velocity;
            if (faceDir.LengthSq() < 0.1f)
                faceDir = _units[i].PreferredVel; // use intended direction during acceleration ramp-up
            if (faceDir.LengthSq() > 0.1f)
            {
                float targetAngle = MathF.Atan2(faceDir.Y, faceDir.X) * Rad2Deg;
                float diff = AngleDiff(targetAngle, _units[i].FacingAngle);
                float maxTurn = turnSpeed * dt;
                _units[i].FacingAngle += MathUtil.Clamp(diff, -maxTurn, maxTurn);
            }
        }
    }

    private static float AngleDiff(float target, float current)
    {
        float diff = target - current;
        while (diff > 180f) diff -= 360f;
        while (diff < -180f) diff += 360f;
        return diff;
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
        float diff = MathF.Abs(AngleDiff(targetAngle, _units[i].FacingAngle));
        return diff <= threshold * 0.5f;
    }

    // --- Combat ---
    private void UpdateCombat(float dt)
    {
        float meleeRange = _gameData?.Settings.Combat.MeleeRange ?? MeleeRangeBase;
        float attackCooldownTime = _gameData?.Settings.Combat.AttackCooldown ?? CombatTickInterval;
        float postAttackLockout = _gameData?.Settings.Combat.PostAttackLockout ?? 1.0f;

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

        // Derive InCombat from EngagedTarget + range (read-only flag for AI/animation)
        for (int i = 0; i < _units.Count; i++)
        {
            _units[i].InCombat = false;
            if (!_units[i].Alive || _units[i].EngagedTarget.IsNone) continue;
            if (!_units[i].EngagedTarget.IsUnit) continue;
            int ti = ResolveUnitTarget(_units[i].EngagedTarget);
            if (ti < 0)
            {
                // Target dead or gone — clear engagement
                _units[i].EngagedTarget = CombatTarget.None;
                continue;
            }
            float dist = (_units[ti].Position - _units[i].Position).Length();
            float range = meleeRange + _units[i].Stats.Length * 0.15f + _units[i].Radius + _units[ti].Radius;
            if (dist <= range)
                _units[i].InCombat = true;
        }

        // Attack cooldowns and queuing
        for (int i = 0; i < _units.Count; i++)
        {
            if (!_units[i].Alive) continue;
            _units[i].AttackCooldown = MathF.Max(0f, _units[i].AttackCooldown - dt);

            if (_units[i].Incap.IsLocked) continue;
            if (!_units[i].PendingAttack.IsNone) continue;
            if (_units[i].AttackCooldown > 0f) continue;
            if (_units[i].PostAttackTimer > 0f) continue;

            // Must have an engaged target in melee range
            if (!_units[i].InCombat) continue;
            if (_units[i].EngagedTarget.IsNone || !_units[i].EngagedTarget.IsUnit) continue;

            int targetIdx = ResolveUnitTarget(_units[i].EngagedTarget);
            if (targetIdx < 0) continue;

            // Must be facing the target
            if (!IsFacingTarget(i, targetIdx)) continue;

            // Queue attack — set lockout and cooldown (per-unit overrides)
            var unitDef = _gameData?.Units.Get(_units[i].UnitDefID);
            _units[i].PendingAttack = _units[i].EngagedTarget;
            _units[i].PendingWeaponIdx = _units[i].Stats.MeleeWeapons.Count > 0 ? 0 : -1;
            _units[i].PendingWeaponIsRanged = false;
            _units[i].PendingRangedTarget = GameConstants.InvalidUnit;
            _units[i].AttackCooldown = unitDef?.AttackCooldown ?? attackCooldownTime;
            _units[i].PostAttackTimer = unitDef?.PostAttackLockout ?? postAttackLockout;
        }
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
        if (meleeDefenderIdx < 0) return;

        ResolveMeleeAttack(unitIdx, meleeDefenderIdx);
    }

    private void ResolveMeleeAttack(int attackerIdx, int defenderIdx)
    {
        var atkStats = _units[attackerIdx].Stats;
        var defStats = _units[defenderIdx].Stats;

        int atkDRN = UnitUtil.RollDRN();
        int defDRN = UnitUtil.RollDRN();

        // Apply paralysis reduction to attack and defense
        float atkParalysis = PotionSystem.GetParalysisFraction(_units, attackerIdx);
        float defParalysis = PotionSystem.GetParalysisFraction(_units, defenderIdx);
        int effectiveAtk = (int)(atkStats.Attack * atkParalysis);
        int effectiveDef = (int)(defStats.Defense * defParalysis);

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
            WeaponName = atkStats.MeleeWeapons.Count > 0 ? atkStats.MeleeWeapons[0].Name : "Unarmed",
            AttackBase = atkStats.Attack,
            AttackDRN = atkDRN,
            DefenseBase = defStats.Defense,
            DefenseDRN = defDRN,
            HarassmentPenalty = harassment
        };

        bool hit = modAtk >= modDef;

        if (!hit)
        {
            _units[defenderIdx].Harassment++;
            _units[defenderIdx].Dodging = true;
            _units[defenderIdx].OverrideAnim = new Render.AnimRequest
                { State = Render.AnimState.Dodge, Priority = 1, Interrupt = true, Duration = 0, PlaybackSpeed = 1f };
            logEntry.Outcome = CombatLogOutcome.Miss;
            _combatLog.AddEntry(logEntry);
            return;
        }

        // Hit location
        int weaponLen = atkStats.MeleeWeapons.Count > 0 ? atkStats.MeleeWeapons[0].Length : atkStats.Length;
        var hitLoc = UnitUtil.RollHitLocation(_units[attackerIdx].Size, _units[defenderIdx].Size, weaponLen);

        // Damage roll — protection varies by hit location (paralysis reduces strength)
        int baseDmg = (int)((atkStats.Strength + atkStats.Damage) * atkParalysis);
        int dmgDRN = UnitUtil.RollDRN();
        int protDRN = UnitUtil.RollDRN();
        int dmgRoll = baseDmg + dmgDRN;
        int armorProt = hitLoc == HitLocation.Head ? defStats.Armor.HeadProtection : defStats.Armor.BodyProtection;
        int prot = defStats.NaturalProt + armorProt + protDRN;
        int netDmg = Math.Max(1, dmgRoll - prot);

        logEntry.Outcome = CombatLogOutcome.Hit;
        logEntry.HitLoc = hitLoc;
        logEntry.HitLocationName = hitLoc.ToString();
        logEntry.DamageBase = baseDmg;
        logEntry.DamageDRN = dmgDRN;
        logEntry.ProtBase = defStats.NaturalProt + armorProt;
        logEntry.ProtDRN = protDRN;
        logEntry.NetDamage = netDmg;
        _combatLog.AddEntry(logEntry);

        if (netDmg > 0)
        {
            _units[defenderIdx].HitReacting = true;
            _units[defenderIdx].OverrideAnim = Render.AnimRequest.Combat(Render.AnimState.BlockReact);
        }
        else
        {
            _units[defenderIdx].BlockReacting = true;
            _units[defenderIdx].OverrideAnim = Render.AnimRequest.Combat(Render.AnimState.BlockReact);
        }

        // Melee uses ApplyDirect — armor already calculated above with DRN rolls
        DamageSystem.ApplyDirect(_units, defenderIdx, netDmg, _damageEvents, attackerIdx);

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
        }
    }

    /// <summary>
    /// Deal a fixed amount of armor-negating physical damage — used by magical strike
    /// spells and trap triggers that bypass armor by design. For normal combat damage
    /// go through DamageSystem.Apply directly so the caller controls the flags; this
    /// shortcut hard-codes ArmorNegating and should not be used for melee / ranged weapons.
    /// </summary>
    public void DealDamage(int unitIdx, int damage) =>
        DamageSystem.Apply(_units, unitIdx, damage,
            GameSystems.DamageType.Physical, GameSystems.DamageFlags.ArmorNegating,
            _damageEvents);

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

        // Debug logging every 2 seconds
        if (_frameNumber % 120 == 0)
        {
            int tIdx = ResolveUnitTarget(_units[i].Target);
            float dbgDist = tIdx >= 0 ? (_units[tIdx].Position - _units[i].Position).Length() : -1;
            DebugLog.Log("ai", $"Wolf unit {i}: phase={_units[i].WolfPhase} timer={_units[i].WolfPhaseTimer:F1} " +
                $"target={_units[i].Target} dist={dbgDist:F1} cooldown={_units[i].AttackCooldown:F2} " +
                $"inCombat={_units[i].InCombat} vel={_units[i].PreferredVel.Length():F1}");
        }

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
        // Use same engage range as the combat system
        float attackRange = MeleeRangeBase + _units[i].Stats.Length * 0.15f + _units[i].Radius + _units[targetIdx].Radius;
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

                // If unit died mid-knockback, transfer physics state to corpse
                bool wasInPhysics = _units[i].InPhysics;
                Vec2 corpseVelXY = Vec2.Zero;
                float corpseVelZ = 0f;
                if (wasInPhysics)
                    _physics.TryGetBodyVelocity(i, out corpseVelXY, out corpseVelZ);

                _corpses.Add(new Corpse
                {
                    Position = _units[i].Position,
                    UnitType = _units[i].Type,
                    UnitDefID = _units[i].UnitDefID,
                    FacingAngle = _units[i].FacingAngle,
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
                });
                _units.RemoveUnit(i);
                if (_necromancerIdx == i) _necromancerIdx = -1;
                else if (_necromancerIdx == _units.Count) _necromancerIdx = i;
            }
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
            c.VelocityZ -= _physics.Gravity * dt;
            float drag = 1f - _physics.DefaultDrag * dt;
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
            }
        }
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
        _grid.RebuildCostField();
        _envSystem?.BakeCollisions(_grid);
        _wallSystem?.BakeWalls(_grid);
        if (_envSystem != null)
            _envIndex.Rebuild(_envSystem, _grid.Width, _grid.Height);
        _pathfinder.Rebuild();
    }
}
