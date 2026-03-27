using System;
using System.Collections.Generic;
using Necroking.Core;
using Necroking.Data;
using Necroking.Movement;
using Necroking.Spatial;
using Necroking.World;

namespace Necroking.GameSystems;

public enum DamageType : byte { Normal, ArmorPiercing, ArmorNegating }

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
}

public class DamageEvent
{
    public Vec2 Position;
    public int Damage;
    public float Height;
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
    private const float AccelTime = 1.5f;
    private const float Rad2Deg = 57.29577951f;

    private TileGrid _grid = new();
    private FlowFieldManager _flowFields = new();
    private Pathfinder _pathfinder = new();
    private Quadtree _quadtree = new();
    private UnitArrays _units = new();
    private ProjectileManager _projectiles = new();
    private LightningSystem _lightning = new();
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

    // Public accessors
    public TileGrid Grid => _grid;
    public UnitArrays Units => _units;
    public UnitArrays UnitsMut => _units;
    public Quadtree Quadtree => _quadtree;
    public NecromancerState NecroState => _necroState;
    public IReadOnlyList<Corpse> Corpses => _corpses;
    public List<Corpse> CorpsesMut => _corpses;
    public IReadOnlyList<DamageEvent> DamageEvents => _damageEvents;
    public IReadOnlyList<SoulOrb> SoulOrbs => _soulOrbs;
    public ProjectileManager Projectiles => _projectiles;
    public LightningSystem Lightning => _lightning;
    public HordeSystem Horde => _horde;
    public CombatLog CombatLog => _combatLog;
    public float GameTime => _gameTime;
    public GameData? GameData => _gameData;
    public int NecromancerIndex => _necromancerIdx;
    public Pathfinder Pathfinder => _pathfinder;
    public EnvironmentSystem? EnvironmentSystem => _envSystem;
    public WallSystem? WallSystem => _wallSystem;

    public void Init(int gridWidth, int gridHeight, GameData? gameData = null)
    {
        _gameData = gameData;
        _grid.Init(gridWidth, gridHeight);
        _grid.RebuildCostField();
        _flowFields.Init(_grid);
        _pathfinder.Init(_grid);
        _units.Clear();
        _corpses.Clear();
        _damageEvents.Clear();
        _projectiles.Clear();
        _lightning.Clear();
        _combatLog.Clear();
        _gameTime = 0f;
        _frameNumber = 0;
        _nextCorpseID = 0;
        _necroState = new NecromancerState();
        _harassmentDecayTimer = CombatTickInterval;

        if (gameData?.Settings.Horde != null)
            _horde.Init(gameData.Settings.Horde);
    }

    public void SetEnvironmentSystem(EnvironmentSystem? es) { _envSystem = es; }
    public void SetWallSystem(WallSystem? ws) { _wallSystem = ws; }
    public void SetNecromancerIndex(int idx) { _necromancerIdx = idx; }

    public void Tick(float dt)
    {
        _frameNumber++;
        _gameTime += dt;
        _damageEvents.Clear();

        // Clear per-frame flags and tick timers
        for (int i = 0; i < _units.Count; i++)
        {
            _units.Dodging[i] = false;
            if (_units.HitShakeTimer[i] > 0f)
                _units.HitShakeTimer[i] = MathF.Max(0f, _units.HitShakeTimer[i] - dt);
        }

        // Update mana and cooldowns
        _necroState.Mana = MathF.Min(_necroState.MaxMana, _necroState.Mana + _necroState.ManaRegen * dt);
        _necroState.TickCooldowns(dt);

        // Tick buffs
        BuffSystem.TickBuffs(_units, dt);
        for (int i = 0; i < _units.Count; i++)
        {
            if (_units.ActiveBuffs[i].Count > 0)
                _units.MaxSpeed[i] = BuffSystem.GetModifiedStat(_units, i, BuffStat.CombatSpeed, _units.Stats[i].CombatSpeed);
            else
                _units.MaxSpeed[i] = _units.Stats[i].CombatSpeed;
        }

        // Tick knockdown timers
        for (int i = 0; i < _units.Count; i++)
        {
            if (_units.KnockdownTimer[i] > 0f)
                _units.KnockdownTimer[i] = MathF.Max(0f, _units.KnockdownTimer[i] - dt);
            if (_units.StandupTimer[i] > 0f)
                _units.StandupTimer[i] = MathF.Max(0f, _units.StandupTimer[i] - dt);
        }

        // Tick hit shake timers and clear dodge flags
        for (int i = 0; i < _units.Count; i++)
        {
            if (_units.HitShakeTimer[i] > 0f)
                _units.HitShakeTimer[i] = MathF.Max(0f, _units.HitShakeTimer[i] - dt);
            _units.Dodging[i] = false;
        }

        // Harassment decay
        _harassmentDecayTimer -= dt;
        if (_harassmentDecayTimer <= 0f)
        {
            _harassmentDecayTimer += CombatTickInterval;
            for (int i = 0; i < _units.Count; i++)
                if (_units.Harassment[i] > 0)
                    _units.Harassment[i] = (_units.Harassment[i] + 1) / 2;
        }

        // Rebuild quadtree
        RebuildQuadtree();

        // Horde
        _horde.Tick(dt, _units, _necromancerIdx);

        // Core subsystems
        UpdateAI(dt);
        UpdateMovement(dt);
        _horde.UpdateStates(_units, _quadtree, _necromancerIdx, dt);
        UpdateFacingAngles(dt);
        UpdateCombat(dt);

        // Projectiles (with quadtree collision)
        _projectiles.Update(dt, _units, _quadtree);
        foreach (var hit in _projectiles.Hits)
        {
            if (hit.UnitIdx >= 0 && hit.UnitIdx < _units.Count && _units.Alive[hit.UnitIdx])
                ApplyDamage(hit.UnitIdx, hit.Damage);
        }

        // Lightning
        var lightningDmg = new List<LightningDamage>();
        _lightning.Update(dt, lightningDmg, _quadtree, _units);
        foreach (var ld in lightningDmg)
            if (ld.UnitIdx >= 0 && ld.UnitIdx < _units.Count)
                ApplyDamage(ld.UnitIdx, ld.Damage);

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
            positions[i] = _units.Position[i];
            ids[i] = _units.Id[i];
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
            _units.FacingAngle[_necromancerIdx] = angleDeg;
    }

    // --- AI ---
    private void UpdateAI(float dt)
    {
        for (int i = 0; i < _units.Count; i++)
        {
            if (!_units.Alive[i]) continue;
            if (_units.Jumping[i] || _units.KnockdownTimer[i] > 0f) { _units.PreferredVel[i] = Vec2.Zero; continue; }
            if (!_units.PendingAttack[i].IsNone) { _units.PreferredVel[i] = Vec2.Zero; continue; }
            if (_units.InCombat[i] && _units.AI[i] != AIBehavior.PlayerControlled) { _units.PreferredVel[i] = Vec2.Zero; continue; }

            switch (_units.AI[i])
            {
                case AIBehavior.PlayerControlled:
                {
                    float speed = _units.MaxSpeed[i];
                    if (_units.GhostMode[i])
                        speed = 20.0f; // Ghost mode: fixed high speed, ignores collision
                    else if (_necroRunning)
                        speed *= 1.8f;
                    _units.PreferredVel[i] = _necroMoveInput * speed;
                    break;
                }

                case AIBehavior.ArcherAttack:
                {
                    if (!IsTargetAlive(_units.Target[i]) || !_units.Target[i].IsUnit)
                    {
                        int enemy = FindClosestEnemy(i);
                        _units.Target[i] = enemy >= 0 ? CombatTarget.Unit(_units.Id[enemy]) : CombatTarget.None;
                    }
                    if (_units.Target[i].IsUnit)
                    {
                        int targetIdx = ResolveUnitTarget(_units.Target[i]);
                        if (targetIdx >= 0)
                        {
                            float dist = (_units.Position[targetIdx] - _units.Position[i]).Length();
                            float bestRange = _units.Stats[i].RangedRange.Count > 0 ? _units.Stats[i].RangedRange[0] : 40f;

                            if (dist <= bestRange)
                            {
                                _units.PreferredVel[i] = Vec2.Zero;
                                if (_units.AttackCooldown[i] <= 0f && _units.PendingAttack[i].IsNone)
                                {
                                    int damage = _units.Stats[i].RangedDmg.Count > 0 ? _units.Stats[i].RangedDmg[0] : 8;
                                    bool volley = dist > bestRange * 0.4f;
                                    _projectiles.SpawnArrow(_units.Position[i], _units.Position[targetIdx],
                                        _units.Faction[i], _units.Id[i], damage, volley, 10);
                                    _units.AttackCooldown[i] = _units.Stats[i].RangedCooldownTime.Count > 0
                                        ? _units.Stats[i].RangedCooldownTime[0] : 2f;
                                }
                            }
                            else
                                MoveTowardUnit(i, targetIdx, _units.MaxSpeed[i]);
                        }
                        else _units.PreferredVel[i] = Vec2.Zero;
                    }
                    else _units.PreferredVel[i] = Vec2.Zero;
                    break;
                }

                case AIBehavior.AttackNecromancer:
                {
                    if (_necromancerIdx >= 0 && _necromancerIdx < _units.Count && _units.Alive[_necromancerIdx])
                        _units.Target[i] = CombatTarget.Unit(_units.Id[_necromancerIdx]);
                    else if (!IsTargetAlive(_units.Target[i]))
                        _units.Target[i] = FindBestEnemyTarget(i);
                    goto default;
                }

                case AIBehavior.GuardKnight:
                {
                    int guardTarget = -1;
                    float bestGuardDist = float.MaxValue;
                    for (int j = 0; j < _units.Count; j++)
                    {
                        if (j == i || !_units.Alive[j] || _units.Faction[j] != _units.Faction[i]) continue;
                        if (_units.AI[j] != AIBehavior.AttackNecromancer) continue;
                        float d = (_units.Position[j] - _units.Position[i]).LengthSq();
                        if (d < bestGuardDist) { bestGuardDist = d; guardTarget = j; }
                    }
                    if (guardTarget >= 0 && MathF.Sqrt(bestGuardDist) > 3f)
                    {
                        MoveTowardUnit(i, guardTarget, _units.MaxSpeed[i]);
                        break;
                    }
                    if (!IsTargetAlive(_units.Target[i]))
                        _units.Target[i] = FindBestEnemyTarget(i);
                    goto default;
                }

                case AIBehavior.MoveToPoint:
                {
                    var toTarget = _units.MoveTarget[i] - _units.Position[i];
                    if (toTarget.LengthSq() > 1f)
                    {
                        var dir = _pathfinder.GetDirection(_units.Position[i], _units.MoveTarget[i], _frameNumber);
                        _units.PreferredVel[i] = dir * _units.MaxSpeed[i];
                    }
                    else
                        _units.PreferredVel[i] = Vec2.Zero;
                    break;
                }

                case AIBehavior.IdleAtPoint:
                case AIBehavior.DefendPoint:
                {
                    // Idle near move target, fight nearby enemies, return
                    int enemy = FindClosestEnemy(i);
                    if (enemy >= 0)
                    {
                        float eDist = (_units.Position[enemy] - _units.Position[i]).Length();
                        if (eDist < 10f)
                        {
                            _units.Target[i] = CombatTarget.Unit(_units.Id[enemy]);
                            MoveTowardUnit(i, enemy, _units.MaxSpeed[i]);
                            break;
                        }
                    }
                    // Return to idle point
                    var toIdle = _units.MoveTarget[i] - _units.Position[i];
                    if (toIdle.LengthSq() > 4f)
                    {
                        var dir = toIdle.Normalized();
                        _units.PreferredVel[i] = dir * _units.MaxSpeed[i] * 0.5f;
                    }
                    else
                        _units.PreferredVel[i] = Vec2.Zero;
                    break;
                }

                case AIBehavior.Patrol:
                {
                    // Move along patrol route waypoints
                    // For now, just move to moveTarget (first waypoint)
                    var toTarget = _units.MoveTarget[i] - _units.Position[i];
                    if (toTarget.LengthSq() > 1f)
                    {
                        var dir = toTarget.Normalized();
                        _units.PreferredVel[i] = dir * _units.MaxSpeed[i];
                    }
                    else
                        _units.PreferredVel[i] = Vec2.Zero;

                    // Fight nearby enemies
                    int nearEnemy = FindClosestEnemy(i);
                    if (nearEnemy >= 0 && (_units.Position[nearEnemy] - _units.Position[i]).Length() < 8f)
                    {
                        _units.Target[i] = CombatTarget.Unit(_units.Id[nearEnemy]);
                        MoveTowardUnit(i, nearEnemy, _units.MaxSpeed[i]);
                    }
                    break;
                }

                case AIBehavior.Raid:
                {
                    // Attack-move toward target, call friends
                    if (!IsTargetAlive(_units.Target[i]))
                        _units.Target[i] = FindBestEnemyTarget(i);
                    if (_units.Target[i].IsUnit)
                    {
                        int ti = ResolveUnitTarget(_units.Target[i]);
                        if (ti >= 0) MoveTowardUnit(i, ti, _units.MaxSpeed[i]);
                        else _units.PreferredVel[i] = Vec2.Zero;
                    }
                    else
                    {
                        // Move toward moveTarget (raid destination)
                        var toTarget = _units.MoveTarget[i] - _units.Position[i];
                        if (toTarget.LengthSq() > 1f)
                            _units.PreferredVel[i] = toTarget.Normalized() * _units.MaxSpeed[i];
                        else
                            _units.PreferredVel[i] = Vec2.Zero;
                    }
                    break;
                }

                case AIBehavior.Caster:
                {
                    _units.PreferredVel[i] = Vec2.Zero;

                    // Tick mana regen
                    _units.Mana[i] = MathF.Min(_units.MaxMana[i], _units.Mana[i] + _units.ManaRegen[i] * dt);

                    // Tick spell cooldown
                    _units.SpellCooldownTimer[i] = MathF.Max(0f, _units.SpellCooldownTimer[i] - dt);

                    // No spell or no mana pool — idle
                    string spellId = _units.SpellID[i];
                    if (string.IsNullOrEmpty(spellId) || _units.MaxMana[i] <= 0f) break;

                    // Look up spell definition
                    var spell = _gameData?.Spells.Get(spellId);
                    if (spell == null) break;

                    // Find closest enemy within spell range
                    int enemy = FindClosestEnemy(i);
                    if (enemy >= 0)
                    {
                        float dist = (_units.Position[enemy] - _units.Position[i]).Length();
                        if (dist <= spell.Range &&
                            _units.Mana[i] >= spell.ManaCost &&
                            _units.SpellCooldownTimer[i] <= 0f)
                        {
                            // Cast the spell — spawn strike at target
                            var style = new LightningStyle
                            {
                                CoreColor = spell.StrikeCoreColor,
                                GlowColor = spell.StrikeGlowColor,
                                CoreWidth = spell.StrikeCoreWidth,
                                GlowWidth = spell.StrikeGlowWidth
                            };
                            var visual = spell.StrikeVisualType == "GodRay"
                                ? StrikeVisual.GodRay : StrikeVisual.Lightning;
                            var grp = new GodRayParams
                            {
                                EdgeSoftness = spell.GodRayEdgeSoftness,
                                NoiseSpeed = spell.GodRayNoiseSpeed,
                                NoiseStrength = spell.GodRayNoiseStrength,
                                NoiseScale = spell.GodRayNoiseScale
                            };
                            var tFilter = Enum.TryParse<SpellTargetFilter>(spell.TargetFilter, out var tf)
                                ? tf : SpellTargetFilter.AnyEnemy;
                            if (spell.StrikeTargetUnit)
                            {
                                // Instant zap: caster hand to target unit
                                Vec2 casterPos = _units.EffectSpawnPos2D[i];
                                float casterHeight = _units.EffectSpawnHeight[i];
                                Vec2 targetPos = _units.Position[enemy];

                                // Target center of sprite body instead of feet
                                float targetH = 1.8f;
                                if (_gameData != null)
                                {
                                    var tDef = _gameData.Units.Get(_units.UnitDefID[enemy]);
                                    if (tDef != null) targetH = tDef.SpriteWorldHeight;
                                }
                                targetH *= _units.SpriteScale[enemy];

                                _lightning.SpawnZap(casterPos, targetPos, spell.ZapDuration, style,
                                    casterHeight, targetH * 0.5f);

                                // Apply direct damage to target
                                ApplyDamage(enemy, spell.Damage);
                            }
                            else
                            {
                                // Sky lightning strike: telegraph then AOE
                                _lightning.SpawnStrike(_units.Position[enemy],
                                    spell.TelegraphDuration, spell.StrikeDuration,
                                    spell.AoeRadius, spell.Damage, style, spell.Id, visual, grp, tFilter);
                            }

                            _units.Mana[i] -= spell.ManaCost;
                            _units.SpellCooldownTimer[i] = spell.Cooldown;

                            // Apply casting buff
                            if (!string.IsNullOrEmpty(spell.CastingBuffID) && _gameData != null)
                            {
                                var castBuff = _gameData.Buffs.Get(spell.CastingBuffID);
                                if (castBuff != null) BuffSystem.ApplyBuff(_units, i, castBuff);
                            }

                            // Face target
                            var toEnemy = _units.Position[enemy] - _units.Position[i];
                            if (toEnemy.LengthSq() > 0.01f)
                                _units.FacingAngle[i] = MathF.Atan2(toEnemy.Y, toEnemy.X) * 180f / MathF.PI;
                        }
                    }
                    break;
                }

                case AIBehavior.CorpseWorker:
                {
                    // Find corpses, drag to buildings — simplified for now
                    _units.PreferredVel[i] = Vec2.Zero;
                    break;
                }

                case AIBehavior.OrderAttack:
                {
                    // Attack-move to moveTarget, fight enemies, rejoin horde
                    const float OrderEngageRange = 15f;
                    const float OrderArrivalDist = 5f;

                    Vec2 dest = _units.MoveTarget[i];
                    float distToDest = (_units.Position[i] - dest).Length();
                    bool atDest = distToDest < OrderArrivalDist;

                    // Acquire/validate target
                    if (!IsTargetAlive(_units.Target[i]))
                    {
                        int enemy = FindClosestEnemy(i);
                        if (enemy >= 0)
                        {
                            float eDist = (_units.Position[enemy] - _units.Position[i]).Length();
                            if (eDist < OrderEngageRange)
                                _units.Target[i] = CombatTarget.Unit(_units.Id[enemy]);
                            else
                                _units.Target[i] = CombatTarget.None;
                        }
                    }

                    if (IsTargetAlive(_units.Target[i]) && _units.Target[i].IsUnit)
                    {
                        int ti = ResolveUnitTarget(_units.Target[i]);
                        if (ti >= 0) MoveTowardUnit(i, ti, _units.MaxSpeed[i]);
                        else _units.PreferredVel[i] = Vec2.Zero;
                    }
                    else if (!atDest)
                    {
                        // March toward destination
                        var dir = (dest - _units.Position[i]).Normalized();
                        _units.PreferredVel[i] = dir * _units.MaxSpeed[i];
                    }
                    else
                    {
                        // At destination with no enemies — return to horde
                        _units.AI[i] = AIBehavior.AttackClosest;
                        _units.Target[i] = CombatTarget.None;
                        _horde.AddUnit(_units.Id[i]);
                        _units.PreferredVel[i] = Vec2.Zero;
                    }
                    break;
                }

                case AIBehavior.AttackClosestRetarget:
                {
                    _units.RetargetTimer[i] -= dt;
                    if (_units.RetargetTimer[i] <= 0f || !IsTargetAlive(_units.Target[i]))
                    {
                        _units.Target[i] = FindBestEnemyTarget(i);
                        _units.RetargetTimer[i] = 2f;
                    }
                    goto default;
                }

                default:
                {
                    if (!IsTargetAlive(_units.Target[i]))
                        _units.Target[i] = FindBestEnemyTarget(i);

                    if (_units.Target[i].IsUnit)
                    {
                        int targetIdx = ResolveUnitTarget(_units.Target[i]);
                        if (targetIdx >= 0)
                            MoveTowardUnit(i, targetIdx, _units.MaxSpeed[i]);
                        else
                            _units.PreferredVel[i] = Vec2.Zero;
                    }
                    else
                        _units.PreferredVel[i] = Vec2.Zero;
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

        for (int i = 0; i < _units.Count; i++)
        {
            if (!_units.Alive[i]) continue;
            if (_units.Jumping[i] || _units.KnockdownTimer[i] > 0f || !_units.PendingAttack[i].IsNone)
            {
                _units.Velocity[i] = Vec2.Zero;
                continue;
            }
            if (_units.InCombat[i] && _units.AI[i] != AIBehavior.PlayerControlled)
            {
                _units.Velocity[i] = Vec2.Zero;
                continue;
            }

            // Ghost mode: skip ORCA
            if (_units.GhostMode[i])
            {
                _units.Velocity[i] = _units.PreferredVel[i];
                _units.Position[i] += _units.Velocity[i] * dt;
                continue;
            }

            // Build ORCA neighbor list
            neighbors.Clear();
            nearbyIDs.Clear();
            float queryRadius = MathF.Max(_units.Radius[i] * 5f, 3f);
            _quadtree.QueryRadius(_units.Position[i], queryRadius, nearbyIDs);

            foreach (uint nid in nearbyIDs)
            {
                if (nid == _units.Id[i]) continue;
                int j = UnitUtil.ResolveUnitIndex(_units, nid);
                if (j < 0 || !_units.Alive[j]) continue;

                neighbors.Add(new ORCANeighbor
                {
                    Position = _units.Position[j],
                    Velocity = _units.Velocity[j],
                    Radius = _units.Radius[j],
                    Id = nid,
                    Priority = _units.Faction[j] != _units.Faction[i]
                        ? _units.OrcaPriority[i]  // cross-faction: equal
                        : _units.OrcaPriority[j]  // same-faction: respect hierarchy
                });
            }

            // Sort by distance, keep closest 10
            var myPos = _units.Position[i];
            neighbors.Sort((a, b) => (a.Position - myPos).LengthSq().CompareTo((b.Position - myPos).LengthSq()));
            if (neighbors.Count > 10) neighbors.RemoveRange(10, neighbors.Count - 10);

            var param = new ORCAParams
            {
                TimeHorizon = 3f,
                MaxSpeed = _units.MaxSpeed[i],
                Radius = _units.Radius[i],
                MaxNeighbors = 10,
                Priority = _units.OrcaPriority[i]
            };

            Vec2 newVel = Orca.ComputeORCAVelocity(
                _units.Position[i], _units.Velocity[i], _units.PreferredVel[i],
                neighbors, param, dt);

            // Apply acceleration limit
            float targetSpeed = newVel.Length();
            float currentSpeed = _units.Velocity[i].Length();
            float speedDiff = targetSpeed - currentSpeed;

            if (speedDiff > 0f)
            {
                float maxAccel = _units.MaxSpeed[i] / AccelTime;
                float maxDelta = maxAccel * dt;
                if (speedDiff > maxDelta) speedDiff = maxDelta;
            }

            float finalSpeed = currentSpeed + speedDiff;
            _units.Velocity[i] = targetSpeed > 0.001f
                ? (newVel * (1f / targetSpeed)) * finalSpeed
                : Vec2.Zero;

            // Move with basic wall collision
            Vec2 oldPos = _units.Position[i];
            Vec2 delta = _units.Velocity[i] * dt;
            float r = _units.Radius[i] * 0.7f;

            // Try X movement
            Vec2 testPos = new(oldPos.X + delta.X, oldPos.Y);
            if (!IsBlocked(testPos.X, testPos.Y, r))
                _units.Position[i] = new Vec2(testPos.X, _units.Position[i].Y);

            // Try Y movement
            testPos = new Vec2(_units.Position[i].X, oldPos.Y + delta.Y);
            if (!IsBlocked(testPos.X, testPos.Y, r))
                _units.Position[i] = new Vec2(_units.Position[i].X, testPos.Y);
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
        for (int i = 0; i < _units.Count; i++)
        {
            if (!_units.Alive[i]) continue;
            if (_units.StandupTimer[i] > 0f) continue;

            // PlayerControlled: facing is set by mouse in Game1, don't override
            if (_units.AI[i] == AIBehavior.PlayerControlled) continue;

            Vec2 vel = _units.Velocity[i];
            if (vel.LengthSq() > 0.1f)
            {
                float targetAngle = MathF.Atan2(vel.Y, vel.X) * Rad2Deg;
                float diff = AngleDiff(targetAngle, _units.FacingAngle[i]);
                float turnSpeed = 360f; // degrees/sec
                float maxTurn = turnSpeed * dt;
                _units.FacingAngle[i] += MathUtil.Clamp(diff, -maxTurn, maxTurn);
            }
            else if (_units.InCombat[i] && _units.Target[i].IsUnit)
            {
                int ti = ResolveUnitTarget(_units.Target[i]);
                if (ti >= 0)
                {
                    Vec2 dir = _units.Position[ti] - _units.Position[i];
                    if (dir.LengthSq() > 0.001f)
                    {
                        float targetAngle = MathF.Atan2(dir.Y, dir.X) * Rad2Deg;
                        float diff = AngleDiff(targetAngle, _units.FacingAngle[i]);
                        float maxTurn = 720f * dt;
                        _units.FacingAngle[i] += MathUtil.Clamp(diff, -maxTurn, maxTurn);
                    }
                }
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

    // --- Combat ---
    private void UpdateCombat(float dt)
    {
        // Clear inCombat flags
        for (int i = 0; i < _units.Count; i++)
            _units.InCombat[i] = false;

        // Mark units in melee range as in-combat
        for (int i = 0; i < _units.Count; i++)
        {
            if (!_units.Alive[i] || !_units.Target[i].IsUnit) continue;
            int targetIdx = ResolveUnitTarget(_units.Target[i]);
            if (targetIdx < 0) continue;

            float dist = (_units.Position[targetIdx] - _units.Position[i]).Length();
            float meleeRange = MeleeRangeBase + _units.Stats[i].Length * 0.15f;
            float engageRange = meleeRange + _units.Radius[i] + _units.Radius[targetIdx];

            if (dist <= engageRange)
            {
                _units.InCombat[i] = true;
                _units.InCombat[targetIdx] = true;
            }
        }

        // Retarget: if inCombat but target out of range, switch to closest in-range enemy
        for (int i = 0; i < _units.Count; i++)
        {
            if (!_units.Alive[i] || !_units.InCombat[i]) continue;
            if (_units.AI[i] == AIBehavior.PlayerControlled) continue;

            bool targetInRange = false;
            if (_units.Target[i].IsUnit)
            {
                int ti = ResolveUnitTarget(_units.Target[i]);
                if (ti >= 0)
                {
                    float dist = (_units.Position[ti] - _units.Position[i]).Length();
                    float range = MeleeRangeBase + _units.Stats[i].Length * 0.15f + _units.Radius[i] + _units.Radius[ti];
                    targetInRange = dist <= range;
                }
            }

            if (!targetInRange)
            {
                int bestIdx = FindClosestEnemy(i);
                if (bestIdx >= 0)
                {
                    _units.Target[i] = CombatTarget.Unit(_units.Id[bestIdx]);
                    Vec2 dir = _units.Position[bestIdx] - _units.Position[i];
                    if (dir.LengthSq() > 0.001f)
                        _units.FacingAngle[i] = MathF.Atan2(dir.Y, dir.X) * Rad2Deg;
                }
            }
        }

        // Attack cooldowns and queuing
        for (int i = 0; i < _units.Count; i++)
        {
            if (!_units.Alive[i]) continue;
            _units.AttackCooldown[i] = MathF.Max(0f, _units.AttackCooldown[i] - dt);

            if (_units.KnockdownTimer[i] > 0f) continue;
            if (!_units.PendingAttack[i].IsNone) continue;
            if (_units.AttackCooldown[i] > 0f) continue;

            var t = _units.Target[i];
            if (t.IsNone || !t.IsUnit) continue;
            if (!_units.InCombat[i]) continue;

            int targetIdx = ResolveUnitTarget(t);
            if (targetIdx < 0) continue;

            // Queue attack
            _units.PendingAttack[i] = t;
            _units.AttackCooldown[i] = CombatTickInterval;
        }
    }

    public void ResolvePendingAttack(int unitIdx)
    {
        if (unitIdx < 0 || unitIdx >= _units.Count || !_units.Alive[unitIdx]) return;
        var t = _units.PendingAttack[unitIdx];
        if (t.IsNone) return;
        _units.PendingAttack[unitIdx] = CombatTarget.None;

        if (!t.IsUnit) return;
        int defenderIdx = ResolveUnitTarget(t);
        if (defenderIdx < 0) return;

        ResolveMeleeAttack(unitIdx, defenderIdx);
    }

    private void ResolveMeleeAttack(int attackerIdx, int defenderIdx)
    {
        var atkStats = _units.Stats[attackerIdx];
        var defStats = _units.Stats[defenderIdx];

        int atkDRN = UnitUtil.RollDRN();
        int defDRN = UnitUtil.RollDRN();
        int modAtk = atkStats.Attack + atkDRN;
        int harassment = _units.Harassment[defenderIdx];
        int modDef = defStats.Defense - harassment + defDRN;

        var logEntry = new CombatLogEntry
        {
            Timestamp = _gameTime,
            AttackerName = GetUnitDisplayName(attackerIdx),
            DefenderName = GetUnitDisplayName(defenderIdx),
            AttackerFaction = _units.Faction[attackerIdx] == Faction.Undead ? 'A' : 'B',
            DefenderFaction = _units.Faction[defenderIdx] == Faction.Undead ? 'A' : 'B',
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
            _units.Harassment[defenderIdx]++;
            _units.Dodging[defenderIdx] = true;
            logEntry.Outcome = CombatLogOutcome.Miss;
            _combatLog.AddEntry(logEntry);
            return;
        }

        // Hit location
        int weaponLen = atkStats.MeleeWeapons.Count > 0 ? atkStats.MeleeWeapons[0].Length : atkStats.Length;
        var hitLoc = UnitUtil.RollHitLocation(_units.Size[attackerIdx], _units.Size[defenderIdx], weaponLen);

        // Damage roll — protection varies by hit location
        int baseDmg = atkStats.Strength + atkStats.Damage;
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

        ApplyDamage(defenderIdx, netDmg, attackerIdx);
    }

    private void ApplyDamage(int unitIdx, int damage, int attackerIdx = -1)
    {
        if (unitIdx < 0 || unitIdx >= _units.Count || !_units.Alive[unitIdx]) return;

        _units.Stats[unitIdx].HP -= damage;
        _units.HitShakeTimer[unitIdx] = 0.15f;
        _damageEvents.Add(new DamageEvent
        {
            Position = _units.Position[unitIdx],
            Damage = damage,
            Height = 1.5f
        });

        if (_units.Stats[unitIdx].HP <= 0)
        {
            _units.Alive[unitIdx] = false;
            _units.Stats[unitIdx].HP = 0;
        }
    }

    // --- Helpers ---
    private void MoveTowardUnit(int i, int targetIdx, float speed)
    {
        Vec2 dir = (_units.Position[targetIdx] - _units.Position[i]).Normalized();
        _units.PreferredVel[i] = dir * speed;
    }

    private CombatTarget FindBestEnemyTarget(int unitIdx)
    {
        int closest = FindClosestEnemy(unitIdx);
        return closest >= 0 ? CombatTarget.Unit(_units.Id[closest]) : CombatTarget.None;
    }

    private int FindClosestEnemy(int unitIdx)
    {
        float bestDist = float.MaxValue;
        int bestIdx = -1;
        for (int j = 0; j < _units.Count; j++)
        {
            if (j == unitIdx || !_units.Alive[j]) continue;
            if (_units.Faction[j] == _units.Faction[unitIdx]) continue;
            float dist = (_units.Position[j] - _units.Position[unitIdx]).LengthSq();
            if (dist < bestDist) { bestDist = dist; bestIdx = j; }
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
            if (!_units.Alive[i])
            {
                _corpses.Add(new Corpse
                {
                    Position = _units.Position[i],
                    UnitType = _units.Type[i],
                    UnitDefID = _units.UnitDefID[i],
                    FacingAngle = _units.FacingAngle[i],
                    SpriteScale = _units.SpriteScale[i],
                    CorpseID = _nextCorpseID++
                });
                _units.RemoveUnit(i);
                if (_necromancerIdx == i) _necromancerIdx = -1;
                else if (_necromancerIdx == _units.Count) _necromancerIdx = i;
            }
        }
    }

    private void UpdateCorpses(float dt)
    {
        // Update dragged corpse positions to follow their dragger
        for (int u = 0; u < _units.Count; u++)
        {
            int dragIdx = _units.DraggingCorpseIdx[u];
            if (dragIdx < 0 || dragIdx >= _corpses.Count) continue;
            if (!_units.Alive[u])
            {
                // Unit died while dragging — release the corpse
                _corpses[dragIdx].DraggedByUnitID = GameConstants.InvalidUnit;
                _units.DraggingCorpseIdx[u] = -1;
                continue;
            }
            // Corpse follows slightly behind the unit
            float facingRad = _units.FacingAngle[u] * MathF.PI / 180f;
            var behind = new Vec2(-MathF.Cos(facingRad), -MathF.Sin(facingRad)) * 1.2f;
            _corpses[dragIdx].Position = _units.Position[u] + behind;
        }

        for (int i = _corpses.Count - 1; i >= 0; i--)
        {
            _corpses[i].Age += dt;
            if (_corpses[i].Dissolving)
            {
                _corpses[i].DissolveTimer += dt;
                if (_corpses[i].DissolveTimer > 2f)
                {
                    // If someone was dragging this corpse, release them
                    for (int u = 0; u < _units.Count; u++)
                        if (_units.DraggingCorpseIdx[u] == i)
                            _units.DraggingCorpseIdx[u] = -1;
                    _corpses.RemoveAt(i);
                    // Fix up drag indices that pointed past the removed element
                    for (int u = 0; u < _units.Count; u++)
                        if (_units.DraggingCorpseIdx[u] > i)
                            _units.DraggingCorpseIdx[u]--;
                }
            }
        }
    }

    public int SpawnUnitByID(string unitID, Vec2 pos)
    {
        int idx = _units.AddUnit(pos, UnitType.Dynamic);
        _units.UnitDefID[idx] = unitID;
        return idx;
    }

    public string GetUnitDisplayName(int unitIdx)
    {
        if (unitIdx < 0 || unitIdx >= _units.Count) return "Unknown";
        return _units.UnitDefID[unitIdx];
    }

    public void RebuildPathfinder()
    {
        _grid.RebuildCostField();
        _envSystem?.BakeCollisions(_grid);
        _wallSystem?.BakeWalls(_grid);
        _pathfinder.Rebuild();
    }
}
