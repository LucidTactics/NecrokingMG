using System;
using System.Collections.Generic;
using Necroking.Core;
using Necroking.Data;
using Necroking.Data.Registries;
using Necroking.Movement;

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
}

public class HordeSystem
{
    private HordeSettings _settings = new();
    private Vec2 _circleCenter;
    private float _circleFacing;
    private bool _necroMoving;
    private float _globalTime;
    private readonly List<HordeUnitData> _hordeUnits = new();

    public Vec2 CircleCenter => _circleCenter;
    public float CircleFacing => _circleFacing;
    public bool IsNecroMoving => _necroMoving;
    public IReadOnlyList<HordeUnitData> HordeUnits => _hordeUnits;
    public HordeSettings Settings { get => _settings; set => _settings = value; }

    public void Init(HordeSettings settings) { _settings = settings; }

    public void AddUnit(uint id)
    {
        if (FindUnit(id) >= 0) return;
        _hordeUnits.Add(new HordeUnitData { UnitID = id });
    }

    public void RemoveUnit(uint id)
    {
        int idx = FindUnit(id);
        if (idx >= 0) _hordeUnits.RemoveAt(idx);
    }

    public bool GetTargetPosition(uint id, out Vec2 target)
    {
        int idx = FindUnit(id);
        if (idx < 0) { target = Vec2.Zero; return false; }
        // Simple: circle around necromancer
        float angle = (idx * 2f * MathF.PI / Math.Max(1, _hordeUnits.Count)) + _globalTime * 0.5f;
        float r = _settings.CircleRadius;
        target = _circleCenter + new Vec2(MathF.Cos(angle) * r, MathF.Sin(angle) * r);
        return true;
    }

    public void Tick(float dt, UnitArrays units, int necroIdx)
    {
        _globalTime += dt;
        if (necroIdx >= 0 && necroIdx < units.Count)
        {
            var necroPos = units.Position[necroIdx];
            var diff = necroPos - _circleCenter;
            _necroMoving = diff.LengthSq() > 0.1f;
            _circleCenter += diff * MathF.Min(1f, _settings.PositionLerp * dt);
        }
    }

    private int FindUnit(uint id)
    {
        for (int i = 0; i < _hordeUnits.Count; i++)
            if (_hordeUnits[i].UnitID == id) return i;
        return -1;
    }
}
