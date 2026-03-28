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
    public bool NecroRunning => _necroRunning;
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
            _units.HitReacting[i] = false;
            _units.BlockReacting[i] = false;
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
            // FleeWhenHit and wolf AIs handle their own combat/disengage logic
            bool selfManagesCombat = _units.AI[i] == AIBehavior.FleeWhenHit
                || _units.AI[i] == AIBehavior.WolfHitAndRun || _units.AI[i] == AIBehavior.WolfHitAndRunIsolated
                || _units.AI[i] == AIBehavior.WolfOpportunist || _units.AI[i] == AIBehavior.WolfOpportunistIsolated;
            // Units with pending attacks stop moving (except self-managed AIs)
            // FleeWhenHit always bypasses; wolf AIs bypass during disengage/wait phases
            if (!_units.PendingAttack[i].IsNone && !(_units.AI[i] == AIBehavior.FleeWhenHit
                || (selfManagesCombat && _units.WolfPhase[i] >= WolfDisengage)))
            { _units.PreferredVel[i] = Vec2.Zero; continue; }
            if (_units.InCombat[i] && _units.AI[i] != AIBehavior.PlayerControlled && !selfManagesCombat)
            { _units.PreferredVel[i] = Vec2.Zero; continue; }

            switch (_units.AI[i])
            {
                case AIBehavior.PlayerControlled:
                {
                    float speed = _units.Stats[i].CombatSpeed;
                    if (_units.GhostMode[i])
                        speed = 20.0f;
                    else if (_necroRunning)
                        speed *= 1.8f;
                    _units.MaxSpeed[i] = speed; // update so ORCA + accel cap respect current speed
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
                        int sizeTier = TerrainCosts.SizeToTier(_units.Size[i]);
                        var dir = _pathfinder.GetDirection(_units.Position[i], _units.MoveTarget[i], _frameNumber, sizeTier, i);
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

                case AIBehavior.FleeWhenHit:
                {
                    // Deer-like behavior: idle normally, flee when hit at effect time
                    if (_frameNumber % 120 == 0)
                        DebugLog.Log("ai", $"FleeWhenHit unit {i}: LastAttacker={_units.LastAttackerID[i]}, FleeTimer={_units.FleeTimer[i]:F1}, InCombat={_units.InCombat[i]}, HP={_units.Stats[i].HP}");

                    if (_units.FleeTimer[i] > 0)
                    {
                        // Currently fleeing — force disengage
                        Disengage(i);
                        _units.EngagedTarget[i] = CombatTarget.None;

                        _units.FleeTimer[i] -= dt;
                        int attackerIdx = UnitUtil.ResolveUnitIndex(_units, _units.LastAttackerID[i]);
                        if (attackerIdx >= 0)
                        {
                            Vec2 awayDir = (_units.Position[i] - _units.Position[attackerIdx]);
                            float dist = awayDir.Length();
                            if (dist > 15f)
                            {
                                _units.FleeTimer[i] = 0;
                                _units.LastAttackerID[i] = GameConstants.InvalidUnit;
                                _units.PreferredVel[i] = Vec2.Zero;
                            }
                            else
                            {
                                awayDir = dist > 0.01f ? awayDir * (1f / dist) : new Vec2(1, 0);
                                _units.PreferredVel[i] = awayDir * _units.MaxSpeed[i];
                            }
                        }
                        else
                        {
                            _units.FleeTimer[i] = 0;
                            _units.LastAttackerID[i] = GameConstants.InvalidUnit;
                            _units.PreferredVel[i] = Vec2.Zero;
                        }
                    }
                    else if (_units.LastAttackerID[i] != GameConstants.InvalidUnit)
                    {
                        // Got hit — check if we can flee now or need to queue
                        if (_units.PostAttackTimer[i] > 0f || !_units.PendingAttack[i].IsNone)
                        {
                            // Mid-attack: queue flee for when we're free
                            _units.QueuedAction[i] = QueuedUnitAction.Flee;
                        }
                        else
                        {
                            // Free to flee now
                            _units.FleeTimer[i] = 5f;
                            Disengage(i);
                            _units.EngagedTarget[i] = CombatTarget.None;
                        }
                    }
                    else if (_units.QueuedAction[i] == QueuedUnitAction.Flee
                             && _units.PostAttackTimer[i] <= 0f
                             && _units.PendingAttack[i].IsNone)
                    {
                        // Queued flee — now free to execute
                        _units.QueuedAction[i] = QueuedUnitAction.None;
                        _units.FleeTimer[i] = 5f;
                        Disengage(i);
                        _units.EngagedTarget[i] = CombatTarget.None;
                    }
                    else
                    {
                        _units.PreferredVel[i] = Vec2.Zero; // idle
                    }
                    break;
                }

                case AIBehavior.NeutralFightBack:
                {
                    // Neutral: ignore enemies unless hit, then fight attacker
                    if (_units.LastAttackerID[i] != GameConstants.InvalidUnit)
                    {
                        // We've been hit before — fight the attacker (or any enemy if attacker dead)
                        if (!IsTargetAlive(_units.Target[i]))
                        {
                            int attackerIdx = UnitUtil.ResolveUnitIndex(_units, _units.LastAttackerID[i]);
                            if (attackerIdx >= 0 && _units.Alive[attackerIdx])
                                _units.Target[i] = CombatTarget.Unit(_units.LastAttackerID[i]);
                            else
                                _units.Target[i] = FindBestEnemyTarget(i);
                        }

                        if (_units.Target[i].IsUnit)
                        {
                            int targetIdx = ResolveUnitTarget(_units.Target[i]);
                            if (targetIdx >= 0)
                            {
                                MoveTowardUnit(i, targetIdx, _units.MaxSpeed[i]);
                                // Engage when in range
                                float dist = (_units.Position[targetIdx] - _units.Position[i]).Length();
                                float engageRange = (_gameData?.Settings.Combat.MeleeRange ?? MeleeRangeBase)
                                    + _units.Stats[i].Length * 0.15f + _units.Radius[i] + _units.Radius[targetIdx];
                                if (dist <= engageRange && _units.EngagedTarget[i].IsNone)
                                    _units.EngagedTarget[i] = _units.Target[i];
                            }
                            else
                                _units.PreferredVel[i] = Vec2.Zero;
                        }
                        else
                            _units.PreferredVel[i] = Vec2.Zero;
                    }
                    else
                    {
                        _units.PreferredVel[i] = Vec2.Zero; // peaceful until provoked
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
                    bool inHorde = _units.Faction[i] == Faction.Undead && _horde.IsInHorde(_units.Id[i]);
                    var hordeState = inHorde ? _horde.GetUnitState(_units.Id[i]) : HordeUnitState.Following;

                    // Returning horde units: drop target, move back to slot
                    if (inHorde && hordeState == HordeUnitState.Returning)
                    {
                        _units.Target[i] = CombatTarget.None;
                        _units.EngagedTarget[i] = CombatTarget.None;
                        _units.InCombat[i] = false;
                        _units.PendingAttack[i] = CombatTarget.None;
                        if (_horde.GetTargetPosition(_units.Id[i], out var returnSlot))
                        {
                            float distToSlot = (_units.Position[i] - returnSlot).Length();
                            if (distToSlot > 0.5f)
                            {
                                var dir = (returnSlot - _units.Position[i]).Normalized();
                                _units.PreferredVel[i] = dir * _units.MaxSpeed[i] * _horde.Settings.ReturnSpeedMult;
                            }
                            else
                                _units.PreferredVel[i] = Vec2.Zero;
                        }
                        else
                            _units.PreferredVel[i] = Vec2.Zero;
                        break;
                    }

                    // Acquire new target (range-limited for horde units)
                    if (!IsTargetAlive(_units.Target[i]))
                    {
                        if (inHorde)
                        {
                            // Horde units only target enemies within engagement range
                            int nearby = FindClosestEnemy(i, _horde.Settings.EngagementRange);
                            _units.Target[i] = nearby >= 0 ? CombatTarget.Unit(_units.Id[nearby]) : CombatTarget.None;
                        }
                        else
                            _units.Target[i] = FindBestEnemyTarget(i);
                    }

                    if (_units.Target[i].IsUnit)
                    {
                        int targetIdx = ResolveUnitTarget(_units.Target[i]);
                        if (targetIdx >= 0)
                        {
                            MoveTowardUnit(i, targetIdx, _units.MaxSpeed[i]);
                            // Auto-engage when in melee range
                            float dist = (_units.Position[targetIdx] - _units.Position[i]).Length();
                            float engageRange = (_gameData?.Settings.Combat.MeleeRange ?? MeleeRangeBase)
                                + _units.Stats[i].Length * 0.15f + _units.Radius[i] + _units.Radius[targetIdx];
                            if (dist <= engageRange && _units.EngagedTarget[i].IsNone)
                                _units.EngagedTarget[i] = _units.Target[i];
                        }
                        else
                            _units.PreferredVel[i] = Vec2.Zero;
                    }
                    else
                    {
                        // No combat target — clear engagement
                        _units.EngagedTarget[i] = CombatTarget.None;
                        // Follow horde slot position if in horde
                        if (inHorde && _horde.GetTargetPosition(_units.Id[i], out var slotPos))
                        {
                            float distToSlot = (_units.Position[i] - slotPos).Length();
                            if (distToSlot > 0.5f)
                            {
                                var dir = (slotPos - _units.Position[i]).Normalized();
                                _units.PreferredVel[i] = dir * _units.MaxSpeed[i];
                            }
                            else
                                _units.PreferredVel[i] = Vec2.Zero;
                        }
                        else
                            _units.PreferredVel[i] = Vec2.Zero;
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

        for (int i = 0; i < _units.Count; i++)
        {
            if (!_units.Alive[i]) continue;
            // Movement blocked by: jumping, knockdown, pending attack, or post-attack lockout
            if (_units.Jumping[i] || _units.KnockdownTimer[i] > 0f
                || !_units.PendingAttack[i].IsNone || _units.PostAttackTimer[i] > 0f)
            {
                _units.Velocity[i] = Vec2.Zero;
                _units.MoveTime[i] = 0f;
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

                // Necromancer ignores friendly units for ORCA — they dodge around it instead
                if (_units.AI[i] == AIBehavior.PlayerControlled &&
                    _units.Faction[j] == _units.Faction[i])
                    continue;

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

            // Stuck detection + perpendicular nudge
            float speed = newVel.Length();
            float prefSpeed = _units.PreferredVel[i].Length();
            if (prefSpeed > 0.1f && speed < 0.1f * _units.MaxSpeed[i])
            {
                _units.StuckFrames[i]++;
                if (_units.StuckFrames[i] > 20)
                {
                    // Compute perpendicular to preferred velocity
                    Vec2 prefDir = _units.PreferredVel[i] * (1f / prefSpeed);
                    Vec2 perp = (i % 2 == 0)
                        ? new Vec2(-prefDir.Y, prefDir.X)
                        : new Vec2(prefDir.Y, -prefDir.X);

                    // Blend ramps from 30% at 1s (frame 60) to 80% at 2s (frame 120)
                    float t = MathUtil.Clamp((_units.StuckFrames[i] - 20) / 100f, 0f, 1f);
                    float blend = 0.3f + t * 0.5f;

                    newVel = _units.PreferredVel[i] * (1f - blend) + perp * (prefSpeed * blend);
                    speed = newVel.Length();
                }
            }
            else
            {
                _units.StuckFrames[i] = 0;
            }

            // --- Turn penalty: reduce MoveTime when changing direction ---
            // Measures angle between previous velocity and new desired direction.
            // Small corrections (< threshold) are free; larger turns drain MoveTime
            // proportionally, so the unit must re-accelerate after sharp turns.
            // NOTE: To disable turn penalty for player, change this condition:
            float targetSpeed = newVel.Length();
            bool applyTurnPenalty = _units.AI[i] != AIBehavior.PlayerControlled;
            if (applyTurnPenalty && targetSpeed > 0.001f && _units.Velocity[i].LengthSq() > 0.01f)
            {
                Vec2 oldDir = _units.Velocity[i].Normalized();
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
                    _units.MoveTime[i] *= (1f - penalty);
                }
            }

            // Apply exponential acceleration curve
            // speed = maxSpeed * (1 - e^(-k * moveTime))
            // k derived from accelHalfTime: k = -ln(0.5) / accelHalfTime
            if (targetSpeed > 0.001f)
            {
                _units.MoveTime[i] += dt;

                var accelDef = _gameData?.Units.Get(_units.UnitDefID[i]);
                float accelHalfTime = accelDef?.AccelHalfTime ?? _gameData?.Settings.Combat.AccelHalfTime ?? 1.2f;
                float accelK = 0.6931f / accelHalfTime; // ln(2) / halfTime
                // Player-controlled gets instant acceleration
                float speedFraction = _units.AI[i] == AIBehavior.PlayerControlled
                    ? 1f
                    : 1f - MathF.Exp(-accelK * _units.MoveTime[i]);
                float finalSpeed = MathF.Min(targetSpeed, _units.MaxSpeed[i] * speedFraction);
                _units.Velocity[i] = (newVel * (1f / targetSpeed)) * finalSpeed;
            }
            else
            {
                // Stopped — reset acceleration (no deceleration)
                _units.MoveTime[i] = 0f;
                _units.Velocity[i] = Vec2.Zero;
            }

            // Move with wall collision (axis-independent, gap probes, wall sliding)
            Vec2 oldPos = _units.Position[i];
            Vec2 delta = _units.Velocity[i] * dt;
            // Wall collision uses smaller radius than ORCA for 1-tile gap clearance
            float r = _units.Radius[i] * 0.7f;

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
                    float pushSpeed = _units.MaxSpeed[i] * 3f;
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
                    _units.Position[i] = oldPos;
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

            _units.Position[i] = newPos;
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
            if (!_units.Alive[i]) continue;
            if (_units.StandupTimer[i] > 0f) continue;

            // PlayerControlled: facing is set by mouse in Game1, don't override
            if (_units.AI[i] == AIBehavior.PlayerControlled) continue;

            // Per-unit turn speed override
            float turnSpeed = globalTurnSpeed;
            var unitDef = _gameData?.Units.Get(_units.UnitDefID[i]);
            if (unitDef?.TurnSpeed.HasValue == true)
                turnSpeed = unitDef.TurnSpeed.Value;

            // Priority 1: Always turn toward engaged target when one is set
            if (!_units.EngagedTarget[i].IsNone && _units.EngagedTarget[i].IsUnit)
            {
                int ti = ResolveUnitTarget(_units.EngagedTarget[i]);
                if (ti >= 0)
                {
                    Vec2 dir = _units.Position[ti] - _units.Position[i];
                    if (dir.LengthSq() > 0.001f)
                    {
                        float targetAngle = MathF.Atan2(dir.Y, dir.X) * Rad2Deg;
                        float diff = AngleDiff(targetAngle, _units.FacingAngle[i]);
                        float maxTurn = turnSpeed * dt;
                        _units.FacingAngle[i] += MathUtil.Clamp(diff, -maxTurn, maxTurn);
                    }
                    continue;
                }
            }

            // Priority 2: Face movement direction (actual velocity, or intended direction
            // if still accelerating from zero)
            Vec2 faceDir = _units.Velocity[i];
            if (faceDir.LengthSq() < 0.1f)
                faceDir = _units.PreferredVel[i]; // use intended direction during acceleration ramp-up
            if (faceDir.LengthSq() > 0.1f)
            {
                float targetAngle = MathF.Atan2(faceDir.Y, faceDir.X) * Rad2Deg;
                float diff = AngleDiff(targetAngle, _units.FacingAngle[i]);
                float maxTurn = turnSpeed * dt;
                _units.FacingAngle[i] += MathUtil.Clamp(diff, -maxTurn, maxTurn);
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
        Vec2 dir = _units.Position[ti] - _units.Position[i];
        if (dir.LengthSq() < 0.001f) return true;
        float targetAngle = MathF.Atan2(dir.Y, dir.X) * Rad2Deg;
        float diff = MathF.Abs(AngleDiff(targetAngle, _units.FacingAngle[i]));
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
            if (_units.PostAttackTimer[i] > 0f)
                _units.PostAttackTimer[i] = MathF.Max(0f, _units.PostAttackTimer[i] - dt);
        }

        // Derive InCombat from EngagedTarget + range (read-only flag for AI/animation)
        for (int i = 0; i < _units.Count; i++)
        {
            _units.InCombat[i] = false;
            if (!_units.Alive[i] || _units.EngagedTarget[i].IsNone) continue;
            if (!_units.EngagedTarget[i].IsUnit) continue;
            int ti = ResolveUnitTarget(_units.EngagedTarget[i]);
            if (ti < 0)
            {
                // Target dead or gone — clear engagement
                _units.EngagedTarget[i] = CombatTarget.None;
                continue;
            }
            float dist = (_units.Position[ti] - _units.Position[i]).Length();
            float range = meleeRange + _units.Stats[i].Length * 0.15f + _units.Radius[i] + _units.Radius[ti];
            if (dist <= range)
                _units.InCombat[i] = true;
        }

        // Attack cooldowns and queuing
        for (int i = 0; i < _units.Count; i++)
        {
            if (!_units.Alive[i]) continue;
            _units.AttackCooldown[i] = MathF.Max(0f, _units.AttackCooldown[i] - dt);

            if (_units.KnockdownTimer[i] > 0f) continue;
            if (!_units.PendingAttack[i].IsNone) continue;
            if (_units.AttackCooldown[i] > 0f) continue;
            if (_units.PostAttackTimer[i] > 0f) continue;

            // Must have an engaged target in melee range
            if (!_units.InCombat[i]) continue;
            if (_units.EngagedTarget[i].IsNone || !_units.EngagedTarget[i].IsUnit) continue;

            int targetIdx = ResolveUnitTarget(_units.EngagedTarget[i]);
            if (targetIdx < 0) continue;

            // Must be facing the target
            if (!IsFacingTarget(i, targetIdx)) continue;

            // Queue attack — set lockout and cooldown (per-unit overrides)
            var unitDef = _gameData?.Units.Get(_units.UnitDefID[i]);
            _units.PendingAttack[i] = _units.EngagedTarget[i];
            _units.AttackCooldown[i] = unitDef?.AttackCooldown ?? attackCooldownTime;
            _units.PostAttackTimer[i] = unitDef?.PostAttackLockout ?? postAttackLockout;
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
            AttackerFaction = _units.Faction[attackerIdx] == Faction.Undead ? 'A' : _units.Faction[attackerIdx] == Faction.Animal ? 'C' : 'B',
            DefenderFaction = _units.Faction[defenderIdx] == Faction.Undead ? 'A' : _units.Faction[defenderIdx] == Faction.Animal ? 'C' : 'B',
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

        if (netDmg > 0)
            _units.HitReacting[defenderIdx] = true;
        else
            _units.BlockReacting[defenderIdx] = true;

        ApplyDamage(defenderIdx, netDmg, attackerIdx);
    }

    private void ApplyDamage(int unitIdx, int damage, int attackerIdx = -1)
    {
        if (unitIdx < 0 || unitIdx >= _units.Count || !_units.Alive[unitIdx]) return;

        _units.Stats[unitIdx].HP -= damage;
        if (attackerIdx >= 0 && attackerIdx < _units.Count)
        {
            _units.LastAttackerID[unitIdx] = _units.Id[attackerIdx];

            // Auto-engage with attacker if not already engaged (AI decides in its update)
            // Most AIs will engage; FleeWhenHit will queue flee instead
            if (_units.EngagedTarget[unitIdx].IsNone
                && _units.AI[unitIdx] != AIBehavior.FleeWhenHit
                && _units.AI[unitIdx] != AIBehavior.PlayerControlled)
            {
                _units.EngagedTarget[unitIdx] = CombatTarget.Unit(_units.Id[attackerIdx]);
                _units.Target[unitIdx] = _units.EngagedTarget[unitIdx];
            }
        }
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
        Vec2 targetPos = _units.Position[targetIdx];
        Vec2 myPos = _units.Position[i];
        float dist = (targetPos - myPos).Length();

        // Use pathfinder for longer distances to navigate around obstacles
        if (dist > 3f && _pathfinder != null && _pathfinder.Grid != null)
        {
            int sizeTier = TerrainCosts.SizeToTier(_units.Size[i]);
            Vec2 dir = _pathfinder.GetDirection(myPos, targetPos, _frameNumber, sizeTier, i);
            _units.PreferredVel[i] = dir * speed;
        }
        else
        {
            // Close range: beeline directly
            Vec2 dir = dist > 0.01f ? (targetPos - myPos) * (1f / dist) : Vec2.Zero;
            _units.PreferredVel[i] = dir * speed;
        }
    }

    // Wolf AI phases
    private const byte WolfEngage = 0;
    private const byte WolfAttacking = 1;
    private const byte WolfDisengage = 2;
    private const byte WolfWaitCooldown = 3;

    private void UpdateWolfAI(int i, float dt)
    {
        var ai = _units.AI[i];
        bool isolated = ai == AIBehavior.WolfHitAndRunIsolated || ai == AIBehavior.WolfOpportunistIsolated;
        bool opportunist = ai == AIBehavior.WolfOpportunist || ai == AIBehavior.WolfOpportunistIsolated;

        // Debug logging every 2 seconds
        if (_frameNumber % 120 == 0)
        {
            int tIdx = ResolveUnitTarget(_units.Target[i]);
            float dbgDist = tIdx >= 0 ? (_units.Position[tIdx] - _units.Position[i]).Length() : -1;
            DebugLog.Log("ai", $"Wolf unit {i}: phase={_units.WolfPhase[i]} timer={_units.WolfPhaseTimer[i]:F1} " +
                $"target={_units.Target[i]} dist={dbgDist:F1} cooldown={_units.AttackCooldown[i]:F2} " +
                $"inCombat={_units.InCombat[i]} vel={_units.PreferredVel[i].Length():F1}");
        }

        // Find/validate target
        if (!IsTargetAlive(_units.Target[i]))
        {
            _units.Target[i] = isolated ? FindMostIsolatedEnemy(i) : FindBestEnemyTarget(i);
            _units.WolfPhase[i] = WolfEngage;
            _units.WolfPhaseTimer[i] = 0;
        }

        int targetIdx = ResolveUnitTarget(_units.Target[i]);
        if (targetIdx < 0)
        {
            _units.PreferredVel[i] = Vec2.Zero;
            return;
        }

        Vec2 myPos = _units.Position[i];
        Vec2 targetPos = _units.Position[targetIdx];
        float dist = (targetPos - myPos).Length();
        // Use same engage range as the combat system
        float attackRange = MeleeRangeBase + _units.Stats[i].Length * 0.15f + _units.Radius[i] + _units.Radius[targetIdx];
        float disengageDist = attackRange + 2f; // back off 2 units beyond attack range
        float attackCooldown = _units.AttackCooldown[i];

        switch (_units.WolfPhase[i])
        {
            case WolfEngage:
            {
                // Move toward target
                if (dist <= attackRange)
                {
                    if (opportunist)
                    {
                        // Check if target is facing away (>100° from us)
                        if (IsTargetFacingAway(i, targetIdx, 100f) || _units.WolfPhaseTimer[i] > attackCooldown)
                        {
                            // Opportunity! Or timeout — attack
                            _units.WolfPhase[i] = WolfAttacking;
                            _units.WolfPhaseTimer[i] = 0;
                            _units.EngagedTarget[i] = _units.Target[i];
                        }
                        else
                        {
                            // Wait for opportunity, circle at edge of range
                            _units.WolfPhaseTimer[i] += dt;
                            Vec2 perp = new Vec2(-(targetPos.Y - myPos.Y), targetPos.X - myPos.X);
                            if (perp.LengthSq() > 0.01f) perp = perp.Normalized();
                            _units.PreferredVel[i] = perp * _units.MaxSpeed[i] * 0.5f;
                        }
                    }
                    else
                    {
                        // Non-opportunist: attack immediately
                        _units.WolfPhase[i] = WolfAttacking;
                        _units.WolfPhaseTimer[i] = 0;
                        _units.EngagedTarget[i] = _units.Target[i];
                    }
                }
                else
                {
                    MoveTowardUnit(i, targetIdx, _units.MaxSpeed[i]);
                    if (opportunist) _units.WolfPhaseTimer[i] += dt;
                }
                break;
            }

            case WolfAttacking:
            {
                // In melee range attacking — let combat system handle the attack
                // Once attack cooldown starts (we just attacked), transition to disengage
                if (attackCooldown > 0 && _units.WolfPhaseTimer[i] > 0.2f)
                {
                    // We've been in attack phase long enough and cooldown started → disengage
                    _units.WolfPhase[i] = WolfDisengage;
                    _units.WolfPhaseTimer[i] = 0;
                    _units.EngagedTarget[i] = CombatTarget.None;
                }
                else
                {
                    _units.WolfPhaseTimer[i] += dt;
                    // Stay near target
                    if (dist > attackRange * 1.5f)
                        MoveTowardUnit(i, targetIdx, _units.MaxSpeed[i]);
                    else
                        _units.PreferredVel[i] = Vec2.Zero;
                }
                break;
            }

            case WolfDisengage:
            {
                // Force-clear engagement and lockout — wolf needs to move immediately
                _units.EngagedTarget[i] = CombatTarget.None;
                _units.PendingAttack[i] = CombatTarget.None;
                _units.PostAttackTimer[i] = 0f;

                // Back away from target to maintain disengageDist
                if (dist < disengageDist)
                {
                    Vec2 awayDir = dist > 0.01f ? (myPos - targetPos) * (1f / dist) : new Vec2(1, 0);
                    _units.PreferredVel[i] = awayDir * _units.MaxSpeed[i];
                }
                else
                {
                    // Reached safe distance, wait for cooldown
                    _units.WolfPhase[i] = WolfWaitCooldown;
                    _units.WolfPhaseTimer[i] = 0;
                    _units.PreferredVel[i] = Vec2.Zero;
                }
                break;
            }

            case WolfWaitCooldown:
            {
                // Force-clear engagement but KEEP our target
                _units.EngagedTarget[i] = CombatTarget.None;
                _units.PendingAttack[i] = CombatTarget.None;

                // Maintain distance, wait for attack cooldown to expire
                if (dist < disengageDist - 0.5f)
                {
                    Vec2 awayDir = dist > 0.01f ? (myPos - targetPos) * (1f / dist) : new Vec2(1, 0);
                    _units.PreferredVel[i] = awayDir * _units.MaxSpeed[i] * 0.5f;
                }
                else if (attackCooldown <= 0)
                {
                    // Cooldown done — re-engage
                    _units.WolfPhase[i] = WolfEngage;
                    _units.WolfPhaseTimer[i] = 0;
                }
                else
                {
                    // Circle while waiting (emergent flanking when multiple wolves)
                    Vec2 toTarget = targetPos - myPos;
                    Vec2 perp = new Vec2(-toTarget.Y, toTarget.X);
                    if (perp.LengthSq() > 0.01f) perp = perp.Normalized();
                    _units.PreferredVel[i] = perp * _units.MaxSpeed[i] * 0.3f;
                }
                break;
            }
        }
    }

    /// <summary>Check if the target is facing away from us by more than angleDeg degrees.</summary>
    private bool IsTargetFacingAway(int unitIdx, int targetIdx, float angleDeg)
    {
        float targetFacing = _units.FacingAngle[targetIdx] * MathF.PI / 180f;
        Vec2 targetFacingDir = new Vec2(MathF.Cos(targetFacing), MathF.Sin(targetFacing));
        Vec2 toUs = _units.Position[unitIdx] - _units.Position[targetIdx];
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
            if (!_units.Alive[j] || _units.Faction[j] == _units.Faction[unitIdx]) continue;
            float dist = (_units.Position[j] - _units.Position[unitIdx]).LengthSq();
            if (dist > 40f * 40f) continue; // max acquisition range

            // Count allies near this enemy
            int allyCount = 0;
            for (int k = 0; k < _units.Count; k++)
            {
                if (k == j || !_units.Alive[k] || _units.Faction[k] != _units.Faction[j]) continue;
                float allyDist = (_units.Position[k] - _units.Position[j]).LengthSq();
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

        return bestIdx >= 0 ? CombatTarget.Unit(_units.Id[bestIdx]) : CombatTarget.None;
    }

    /// <summary>
    /// Cleanly disengage a unit from combat. Clears combat state and makes
    /// the unit's former target retarget to another enemy in range.
    /// </summary>
    private void Disengage(int unitIdx)
    {
        _units.EngagedTarget[unitIdx] = CombatTarget.None;
        _units.InCombat[unitIdx] = false;
        _units.PendingAttack[unitIdx] = CombatTarget.None;
        _units.PostAttackTimer[unitIdx] = 0f;

        // Make the former target retarget if possible
        var oldTarget = _units.Target[unitIdx];
        _units.Target[unitIdx] = CombatTarget.None;

        if (oldTarget.IsUnit)
        {
            int targetIdx = ResolveUnitTarget(oldTarget);
            if (targetIdx >= 0 && _units.Alive[targetIdx])
            {
                // If the target was fighting us, make it pick a new target
                var theirTarget = _units.Target[targetIdx];
                if (theirTarget.IsUnit)
                {
                    int theirTargetIdx = ResolveUnitTarget(theirTarget);
                    if (theirTargetIdx == unitIdx)
                    {
                        // They were targeting us — find them a new target
                        int newTarget = FindClosestEnemy(targetIdx);
                        if (newTarget >= 0)
                            _units.Target[targetIdx] = CombatTarget.Unit(_units.Id[newTarget]);
                        else
                        {
                            _units.Target[targetIdx] = CombatTarget.None;
                            _units.InCombat[targetIdx] = false;
                        }
                    }
                }
            }
        }
    }

    private CombatTarget FindBestEnemyTarget(int unitIdx)
    {
        int closest = FindClosestEnemy(unitIdx);
        return closest >= 0 ? CombatTarget.Unit(_units.Id[closest]) : CombatTarget.None;
    }

    private int FindClosestEnemy(int unitIdx, float maxRange = 0f)
    {
        float bestDist = float.MaxValue;
        float maxDist2 = maxRange > 0f ? maxRange * maxRange : float.MaxValue;
        int bestIdx = -1;
        for (int j = 0; j < _units.Count; j++)
        {
            if (j == unitIdx || !_units.Alive[j]) continue;
            if (_units.Faction[j] == _units.Faction[unitIdx]) continue;
            float dist = (_units.Position[j] - _units.Position[unitIdx]).LengthSq();
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

        // Apply UnitDef properties
        if (_gameData != null)
        {
            var def = _gameData.Units.Get(unitID);
            if (def != null)
            {
                _units.SpriteScale[idx] = def.SpriteScale;
                _units.Radius[idx] = def.Radius;
                _units.Size[idx] = def.Size;
                _units.OrcaPriority[idx] = def.OrcaPriority;

                if (!string.IsNullOrEmpty(def.Faction))
                    _units.Faction[idx] = def.Faction == "Human" ? Faction.Human
                        : def.Faction == "Animal" ? Faction.Animal : Faction.Undead;

                if (Enum.TryParse<AIBehavior>(def.AI, out var ai))
                    _units.AI[idx] = ai;

                var stats = _gameData.Units.BuildStats(unitID, _gameData.Weapons, _gameData.Armors, _gameData.Shields);
                _units.Stats[idx] = stats;
                _units.MaxSpeed[idx] = stats.CombatSpeed;
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
        _units.Stats[unitIdx] = newStats;
        _units.UnitDefID[unitIdx] = newUnitID;
        _units.Size[unitIdx] = def.Size;
        _units.Radius[unitIdx] = def.Radius;
        _units.MaxSpeed[unitIdx] = newStats.CombatSpeed;
        _units.OrcaPriority[unitIdx] = def.OrcaPriority;
        _units.Faction[unitIdx] = def.Faction == "Human" ? Faction.Human : Faction.Undead;
        _units.SpriteScale[unitIdx] = def.SpriteScale;
        // Reset HP to new max
        _units.Stats[unitIdx].HP = newStats.MaxHP;

        // Reset AI behavior from the new definition
        _units.AI[unitIdx] = def.AI switch
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
