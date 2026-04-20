using System;
using System.Collections.Generic;
using Necroking.Core;
using Necroking.Data;
using Necroking.Data.Registries;
using Necroking.Movement;
using Necroking.Spatial;

namespace Necroking.GameSystems;

public enum HordeUnitState : byte { Following, Chasing, Engaged, Returning }

public class HordeUnitData
{
    public uint UnitID = GameConstants.InvalidUnit;
    public int SlotIndex = -1;
    public HordeUnitState State = HordeUnitState.Following;
    public uint ChasingTarget = GameConstants.InvalidUnit;
    public float NoisePhase;
    public float LeashCheckTimer;

    // Discrete slot offset — added to the Fibonacci slot position to break up the
    // uniform pattern. Stays constant between shifts so the unit's target tile is
    // stable (and the pathfinder flow-cache entry keeps hitting) until the next
    // shuffle. Replaces the old per-frame continuous drift that thrashed the cache.
    public Vec2 DiscreteOffset;
    public float NextShiftAt;
}

public class HordeSystem
{
    private const float GoldenAngle = 2.39996322972865f; // pi * (3 - sqrt(5))
    private const float CombatTickInterval = 2.0f;
    private static readonly Random _rng = new();

    private HordeSettings _settings = new();
    private Vec2 _circleCenter;
    private Vec2 _circleVel;
    private float _circleFacing;
    private float _movementAngle;
    private bool _necroMoving;
    private float _globalTime;
    private float _aggroScanTimer;
    private readonly List<HordeUnitData> _hordeUnits = new();
    // O(1) Id -> _hordeUnits index, mirrored on Add/Remove. FindUnit was the hot
    // path for every HordeMinion's per-tick GetUnitState/GetTargetPosition calls
    // — at u=628, ~1.2M linear comparisons per tick.
    private readonly Dictionary<uint, int> _idToIndex = new();

    // Slot indices are permanent per unit. Free slots freed by death are reused
    // (lowest first) before extending the spiral outward, so inner rings stay
    // dense and only the outer boundary grows/shrinks with the population.
    // _nextSlot is the smallest slot that has never been assigned; we keep the
    // "no trailing free slots" invariant by compacting inside FreeSlot.
    private readonly SortedSet<int> _freeSlots = new();
    private int _nextSlot;

    // "Baseline" horde size: the population at which the formation matches the
    // configured Settings.CircleRadius. SlotSpacing is derived so that slot
    // (BaselineUnits - 1) sits at radius CircleRadius. At other sizes the
    // formation scales automatically while per-slot positions stay put.
    private const int BaselineUnits = 40;

    public Vec2 CircleCenter => _circleCenter;
    public float CircleFacing => _circleFacing;
    public bool IsNecroMoving => _necroMoving;
    public IReadOnlyList<HordeUnitData> HordeUnits => _hordeUnits;
    public HordeSettings Settings { get => _settings; set => _settings = value; }

    /// <summary>Per-slot spacing derived from the current CircleRadius setting.</summary>
    private float SlotSpacing => _settings.CircleRadius / MathF.Sqrt(BaselineUnits);

    /// <summary>
    /// Outermost occupied slot's radius. Scales as √N so density stays roughly
    /// constant as the horde grows, without moving existing units' slots.
    /// </summary>
    public float EffectiveRadius => _nextSlot > 0
        ? SlotSpacing * MathF.Sqrt(_nextSlot - 1 + 0.5f)
        : 0f;

    /// <summary>
    /// Radius used for aggro scans and the chase-leash check — floored at
    /// Settings.MinAggroRadius so a small horde (few minions) still reacts when
    /// the necromancer walks near an enemy, instead of needing to be almost on
    /// top of them. Formation/positioning uses EffectiveRadius unchanged.
    /// </summary>
    public float AggroRadius => MathF.Max(EffectiveRadius, _settings.MinAggroRadius);

    public void Init(HordeSettings settings) { _settings = settings; }

    public void AddUnit(uint id)
    {
        if (_idToIndex.ContainsKey(id)) return;
        int slot = AllocSlot();
        int idx = _hordeUnits.Count;
        _hordeUnits.Add(new HordeUnitData
        {
            UnitID = id,
            SlotIndex = slot,
            NoisePhase = (float)(_rng.Next(10000)) / 100f,
            LeashCheckTimer = CombatTickInterval,
            DiscreteOffset = RandomShiftOffset(),
            // Stagger first shift so fresh units don't all re-roll on the same tick.
            NextShiftAt = _globalTime + RandomShiftInterval(),
        });
        _idToIndex[id] = idx;
    }

    private int AllocSlot()
    {
        if (_freeSlots.Count > 0)
        {
            int s = _freeSlots.Min;
            _freeSlots.Remove(s);
            return s;
        }
        return _nextSlot++;
    }

    private void FreeSlot(int slot)
    {
        _freeSlots.Add(slot);
        // Shrink the spiral when the top slot is freed — consume any contiguous
        // free range at the top so EffectiveRadius and the aggro queries see the
        // real extent, not the high-water mark.
        while (_nextSlot > 0 && _freeSlots.Contains(_nextSlot - 1))
        {
            _freeSlots.Remove(_nextSlot - 1);
            _nextSlot--;
        }
    }

    // Tunables for discrete slot shifting.
    private const float ShiftIntervalMin = 3.0f;  // seconds
    private const float ShiftIntervalMax = 6.0f;
    private const float ShiftDistanceMin = 1.0f;  // tiles (= world units)
    private const float ShiftDistanceMax = 2.0f;

    private static Vec2 RandomShiftOffset()
    {
        float angle = (float)(_rng.NextDouble() * Math.PI * 2);
        float dist = ShiftDistanceMin + (float)_rng.NextDouble() * (ShiftDistanceMax - ShiftDistanceMin);
        return new Vec2(MathF.Cos(angle) * dist, MathF.Sin(angle) * dist);
    }
    private static float RandomShiftInterval()
        => ShiftIntervalMin + (float)_rng.NextDouble() * (ShiftIntervalMax - ShiftIntervalMin);

    public void RemoveUnit(uint id)
    {
        if (!_idToIndex.TryGetValue(id, out int idx)) return;
        // Free the dead unit's slot before it disappears from the list; other
        // units keep their SlotIndex unchanged — no reshuffle on death.
        FreeSlot(_hordeUnits[idx].SlotIndex);
        int last = _hordeUnits.Count - 1;
        if (idx != last)
        {
            _hordeUnits[idx] = _hordeUnits[last];
            // The unit that was at `last` now lives at `idx` (SlotIndex unchanged).
            _idToIndex[_hordeUnits[idx].UnitID] = idx;
        }
        _hordeUnits.RemoveAt(last);
        _idToIndex.Remove(id);
    }

    public bool GetTargetPosition(uint id, out Vec2 target)
    {
        int hi = FindUnit(id);
        if (hi < 0) { target = Vec2.Zero; return false; }
        var hu = _hordeUnits[hi];

        // Engaged or Chasing: AI handles movement
        if (hu.State == HordeUnitState.Engaged || hu.State == HordeUnitState.Chasing)
        {
            target = Vec2.Zero;
            return false;
        }

        target = ComputeSlotPosition(hu.SlotIndex);
        // Apply the unit's discrete offset. This value is stable between shifts,
        // so the target tile (and therefore the pathfinder cache key) doesn't
        // change every frame like the old continuous sin-wave drift did.
        target += hu.DiscreteOffset;

        return true;
    }

    public bool IsInHorde(uint id) => FindUnit(id) >= 0;

    public HordeUnitState GetUnitState(uint id)
    {
        int hi = FindUnit(id);
        return hi >= 0 ? _hordeUnits[hi].State : HordeUnitState.Following;
    }

    public uint GetChasingTarget(uint id)
    {
        int hi = FindUnit(id);
        return hi >= 0 ? _hordeUnits[hi].ChasingTarget : GameConstants.InvalidUnit;
    }

    public void Tick(float dt, UnitArrays units, int necroIdx)
    {
        _globalTime += dt;
        if (necroIdx < 0 || necroIdx >= units.Count) return;

        var necroPos = units[necroIdx].Position;
        var necroVel = units[necroIdx].Velocity;
        float necroSpeed = necroVel.Length();
        _necroMoving = necroSpeed > 0.5f;

        // Update movement angle only when moving
        if (_necroMoving)
            _movementAngle = MathF.Atan2(necroVel.Y, necroVel.X);

        // Velocity-driven circle center
        float velAlpha = 1f - MathF.Exp(-_settings.PositionLerp * dt);
        _circleVel += (necroVel - _circleVel) * velAlpha;
        _circleCenter += _circleVel * dt;

        // Weak spring correction toward ideal offset position
        var moveDir = new Vec2(MathF.Cos(_movementAngle), MathF.Sin(_movementAngle));
        float offsetScale = _necroMoving ? 1f : 0f;
        Vec2 idealCenter = necroPos + moveDir * _settings.CircleOffset * offsetScale;

        float springAlpha = 1f - MathF.Exp(-_settings.PositionLerp * 0.3f * dt);
        _circleCenter += (idealCenter - _circleCenter) * springAlpha;

        // Smoothly lerp circle rotation toward movement angle
        float rotAlpha = 1f - MathF.Exp(-_settings.RotationLerp * dt);
        _circleFacing = LerpAngle(_circleFacing, _movementAngle, rotAlpha);

        // Discrete per-unit slot shift. Each unit rolls its own next-shift time on
        // spawn so the horde as a whole doesn't "breathe" in sync — shifts happen
        // sparsely and at staggered moments. No-op on frames where no unit is due.
        for (int i = 0; i < _hordeUnits.Count; i++)
        {
            var hu = _hordeUnits[i];
            if (_globalTime >= hu.NextShiftAt)
            {
                hu.DiscreteOffset = RandomShiftOffset();
                hu.NextShiftAt = _globalTime + RandomShiftInterval();
            }
        }
    }

    /// <summary>
    /// Updates horde state machine: engagement, leashing, aggro scanning.
    /// Called from Simulation after movement.
    /// </summary>
    public void UpdateStates(UnitArrays units, Quadtree qt, int necroIdx, float dt)
    {
        if (necroIdx < 0) return;

        var nearbyIDs = new List<uint>();

        // Per-unit state transitions
        foreach (var hu in _hordeUnits)
        {
            int idx = UnitUtil.ResolveUnitIndex(units, hu.UnitID);
            if (idx < 0) continue;

            switch (hu.State)
            {
                case HordeUnitState.Following:
                {
                    // Check if any enemy is within engagement range
                    nearbyIDs.Clear();
                    qt.QueryRadiusByFaction(units[idx].Position, _settings.EngagementRange,
                        FactionMaskExt.AllExcept(Faction.Undead), nearbyIDs);
                    foreach (uint nid in nearbyIDs)
                    {
                        int ni = UnitUtil.ResolveUnitIndex(units, nid);
                        if (ni < 0) continue;
                        hu.State = HordeUnitState.Engaged;
                        break;
                    }
                    break;
                }

                case HordeUnitState.Chasing:
                {
                    int targetIdx = UnitUtil.ResolveUnitIndex(units, hu.ChasingTarget);
                    if (targetIdx < 0)
                    {
                        hu.State = HordeUnitState.Following;
                        hu.ChasingTarget = GameConstants.InvalidUnit;
                        break;
                    }
                    if (units[idx].InCombat)
                    {
                        hu.State = HordeUnitState.Engaged;
                        hu.ChasingTarget = GameConstants.InvalidUnit;
                        break;
                    }
                    float targetDistToCircle = (units[targetIdx].Position - _circleCenter).Length();
                    if (targetDistToCircle > AggroRadius)
                    {
                        hu.State = HordeUnitState.Following;
                        hu.ChasingTarget = GameConstants.InvalidUnit;
                    }
                    break;
                }

                case HordeUnitState.Engaged:
                {
                    // Target killed — the unit's AI clears Target/EngagedTarget when
                    // its target dies. If we're "Engaged" but not actually pursuing
                    // anyone, return to horde regardless of other enemies nearby.
                    // (The horde-level aggro scan below will re-engage us if a new
                    // threat wanders into range.)
                    if (units[idx].Target.IsNone && units[idx].EngagedTarget.IsNone)
                    {
                        hu.State = HordeUnitState.Returning;
                        break;
                    }

                    // Leash check: distance from horde slot
                    Vec2 slotPos = ComputeSlotPosition(hu.SlotIndex);
                    float distToSlot = (units[idx].Position - slotPos).Length();

                    // Hard leash: if way beyond leash radius, force return immediately
                    if (distToSlot > _settings.LeashRadius * 1.5f)
                    {
                        hu.State = HordeUnitState.Returning;
                        break;
                    }

                    // Soft leash: probabilistic return check
                    if (distToSlot > _settings.LeashRadius)
                    {
                        hu.LeashCheckTimer -= dt;
                        if (hu.LeashCheckTimer <= 0f)
                        {
                            hu.LeashCheckTimer = CombatTickInterval;
                            if ((float)_rng.NextDouble() < _settings.LeashChance)
                            {
                                hu.State = HordeUnitState.Returning;
                                break;
                            }
                        }
                    }

                    // Check if any enemy still nearby
                    nearbyIDs.Clear();
                    qt.QueryRadiusByFaction(units[idx].Position, _settings.EngagementRange * 1.5f,
                        FactionMaskExt.AllExcept(Faction.Undead), nearbyIDs);
                    bool anyEnemy = false;
                    foreach (uint nid in nearbyIDs)
                    {
                        int ni = UnitUtil.ResolveUnitIndex(units, nid);
                        if (ni < 0 || !units[ni].Alive) continue;
                        anyEnemy = true;
                        break;
                    }
                    if (!anyEnemy) hu.State = HordeUnitState.Returning;
                    break;
                }

                case HordeUnitState.Returning:
                {
                    Vec2 slotPos = ComputeSlotPosition(hu.SlotIndex);
                    float distToSlot = (units[idx].Position - slotPos).Length();
                    if (distToSlot < 2f) hu.State = HordeUnitState.Following;
                    break;
                }
            }
        }

        // Horde aggro scan (~2/sec)
        _aggroScanTimer -= dt;
        if (_aggroScanTimer <= 0f)
        {
            _aggroScanTimer = 0.5f;

            nearbyIDs.Clear();
            qt.QueryRadiusByFaction(_circleCenter, AggroRadius,
                FactionMaskExt.AllExcept(Faction.Undead), nearbyIDs);

            var enemiesInCircle = new List<int>();
            foreach (uint nid in nearbyIDs)
            {
                int ni = UnitUtil.ResolveUnitIndex(units, nid);
                if (ni < 0) continue;
                enemiesInCircle.Add(ni);
            }

            if (enemiesInCircle.Count > 0)
            {
                foreach (var hu in _hordeUnits)
                {
                    if (hu.State != HordeUnitState.Following && hu.State != HordeUnitState.Returning) continue;
                    if ((float)_rng.NextDouble() >= 0.25f) continue;

                    int unitIdx = UnitUtil.ResolveUnitIndex(units, hu.UnitID);
                    if (unitIdx < 0) continue;

                    // Pick closest enemy
                    int bestEnemy = -1;
                    float bestDist2 = float.MaxValue;
                    foreach (int e in enemiesInCircle)
                    {
                        float d2 = (units[e].Position - units[unitIdx].Position).LengthSq();
                        if (d2 < bestDist2) { bestDist2 = d2; bestEnemy = e; }
                    }

                    if (bestEnemy >= 0)
                    {
                        hu.State = HordeUnitState.Chasing;
                        hu.ChasingTarget = units[bestEnemy].Id;
                    }
                }
            }
        }
    }

    private Vec2 ComputeSlotPosition(int slotIndex)
    {
        // Vogel spiral (sunflower packing) with a per-slot radius that depends
        // only on the slot index — not on horde size. Each unit keeps its slot
        // forever; growth extends the spiral outward and deaths leave holes
        // that the next spawn fills before extending further. Density stays
        // constant because the radius of slot k is SlotSpacing * √(k + 0.5).
        float r = SlotSpacing * MathF.Sqrt(slotIndex + 0.5f);
        float slotAngle = _circleFacing + slotIndex * GoldenAngle;
        return _circleCenter + new Vec2(MathF.Cos(slotAngle) * r, MathF.Sin(slotAngle) * r);
    }

    private static float LerpAngle(float from, float to, float t)
    {
        float diff = ((to - from + MathF.PI) % (2f * MathF.PI)) - MathF.PI;
        return from + diff * t;
    }

    private int FindUnit(uint id)
        => _idToIndex.TryGetValue(id, out int idx) ? idx : -1;
}
