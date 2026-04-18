using System;
using System.Collections.Generic;
using Necroking.Core;
using Necroking.Data;
using Necroking.Movement;
using Necroking.Spatial;

namespace Necroking.GameSystems;

public class LightningStyle
{
    public HdrColor CoreColor = new(255, 255, 255, 255, 4f);
    public HdrColor GlowColor = new(140, 180, 255, 200, 2.5f);
    public float CoreWidth = 2f;
    public float GlowWidth = 8f;
    public float FlickerMin = 1f;
    public float FlickerMax = 1f;
    public float FlickerHz;
    public float JitterHz;
    // Bolt shape
    public int Subdivisions = 5;        // recursive midpoint displacement iterations
    public float Displacement = 0.35f;  // midpoint offset as fraction of segment length
    // Branching
    public float BranchChance = 0.3f;   // probability per point of spawning a branch
    public float BranchLength = 0.4f;   // branch extends this fraction of remaining main bolt
    public float BranchDecay = 0.6f;    // width multiplier per branch level
    public int MaxBranches = 3;         // max branches per bolt
}

public class GodRayParams
{
    public float EdgeSoftness = 0.4f;
    public float NoiseSpeed = 1.5f;
    public float NoiseStrength = 0.35f;
    public float NoiseScale = 3f;
}

public class ActiveStrike
{
    public Vec2 TargetPos;
    public float TelegraphTimer;
    public float TelegraphDuration;
    public bool TelegraphVisible = true;
    public float EffectTimer;
    public float EffectDuration;
    public float AoeRadius;
    public int Damage;
    public bool DamageApplied;
    public bool Alive = true;
    public string SpellID = "";
    public LightningStyle Style = new();
    public StrikeVisual Visual = StrikeVisual.Lightning;
    public GodRayParams GodRay = new();
    public SpellTargetFilter TargetFilter = SpellTargetFilter.AnyEnemy;
}

public class ActiveZap
{
    public Vec2 StartPos;
    public Vec2 EndPos;
    public float StartHeight;
    public float EndHeight;
    public float Timer;
    public float Duration;
    public bool Alive = true;
    public LightningStyle Style = new();
}

public class ActiveBeam
{
    public uint CasterID;
    public uint TargetID;
    public string SpellID = "";
    public float DamageAccumulator;
    public float TickRate;
    public int DamagePerTick;
    public float RetargetRadius;
    public LightningStyle Style = new();
    public float MaxDuration;
    public float Elapsed;
    public bool Alive = true;
}

/// <summary>Visual parameters for drain tendrils. Built from SpellDef.BuildDrainVisuals().</summary>
public class DrainVisualParams
{
    public int TendrilCount = 3;
    public float ArcHeight = 40f;
    public float SwayAmplitude = 8f;
    public float SwayHz = 1.5f;
    public float CoreWidth = 1.5f;
    public float GlowWidth = 5f;
    public float PulseHz = 2f;
    public float PulseStrength = 0.4f;
    public HdrColor CoreColor = new(120, 255, 80, 255, 2.5f);
    public HdrColor GlowColor = new(40, 120, 20, 160, 1.5f);
}

public class ActiveDrain
{
    public uint CasterID;
    public uint TargetID;
    public int TargetCorpseIdx = -1;
    public Vec2 CorpsePos;
    public string SpellID = "";
    public float DamageAccumulator;
    public float TickRate;
    public int DamagePerTick;
    public float HealPercent;
    public int CorpseHP;
    public bool Reversed;
    public float MaxDuration;
    public float Elapsed;
    public bool Alive = true;
    public DrainVisualParams Visuals = new();
}

public class LightningDamage
{
    public int UnitIdx;
    public int Damage;
}

public class LightningSystem
{
    private readonly List<ActiveStrike> _strikes = new();
    private readonly List<ActiveZap> _zaps = new();
    private readonly List<ActiveBeam> _beams = new();
    private readonly List<ActiveDrain> _drains = new();

    public IReadOnlyList<ActiveStrike> Strikes => _strikes;
    public IReadOnlyList<ActiveZap> Zaps => _zaps;
    public IReadOnlyList<ActiveBeam> Beams => _beams;
    public IReadOnlyList<ActiveDrain> Drains => _drains;

    public void SpawnStrike(Vec2 targetPos, float telegraphDuration, float effectDuration,
                            float aoeRadius, int damage, LightningStyle style, string spellID,
                            StrikeVisual visual = StrikeVisual.Lightning, GodRayParams? godRay = null,
                            SpellTargetFilter targetFilter = SpellTargetFilter.AnyEnemy,
                            bool telegraphVisible = true)
    {
        _strikes.Add(new ActiveStrike
        {
            TargetPos = targetPos,
            TelegraphDuration = telegraphDuration,
            TelegraphVisible = telegraphVisible,
            EffectDuration = effectDuration,
            AoeRadius = aoeRadius,
            Damage = damage,
            Style = style,
            SpellID = spellID,
            Visual = visual,
            GodRay = godRay ?? new GodRayParams(),
            TargetFilter = targetFilter
        });
    }

    public void SpawnBeam(uint casterID, uint targetID, string spellID,
                          int damagePerTick, float tickRate, float retargetRadius,
                          LightningStyle style, float maxDuration = 0f)
    {
        _beams.Add(new ActiveBeam
        {
            CasterID = casterID, TargetID = targetID, SpellID = spellID,
            DamagePerTick = damagePerTick, TickRate = tickRate, RetargetRadius = retargetRadius,
            Style = style, MaxDuration = maxDuration
        });
    }

    public void SpawnDrain(uint casterID, uint targetID, string spellID,
                           int damagePerTick, float tickRate, float healPercent,
                           int corpseHP, bool reversed, float maxDuration,
                           DrainVisualParams visuals)
    {
        _drains.Add(new ActiveDrain
        {
            CasterID = casterID, TargetID = targetID, SpellID = spellID,
            DamagePerTick = damagePerTick, TickRate = tickRate, HealPercent = healPercent,
            CorpseHP = corpseHP, Reversed = reversed, MaxDuration = maxDuration,
            Visuals = visuals
        });
    }

    public void SpawnZap(Vec2 start, Vec2 end, float duration, LightningStyle style,
                         float startHeight = 0f, float endHeight = 0f)
    {
        _zaps.Add(new ActiveZap
        {
            StartPos = start, EndPos = end, Duration = duration, Style = style,
            StartHeight = startHeight, EndHeight = endHeight
        });
    }

    public void Update(float dt, List<LightningDamage> outDamage,
                        Quadtree? quadtree = null, UnitArrays? units = null)
    {
        // Update strikes
        for (int i = _strikes.Count - 1; i >= 0; i--)
        {
            var s = _strikes[i];
            if (!s.Alive) { _strikes.RemoveAt(i); continue; }
            s.TelegraphTimer += dt;
            if (s.TelegraphTimer >= s.TelegraphDuration)
            {
                s.EffectTimer += dt;
                if (!s.DamageApplied)
                {
                    s.DamageApplied = true;

                    // AOE damage: query quadtree for units in radius
                    if (quadtree != null && units != null && s.AoeRadius > 0f)
                    {
                        var nearby = new List<uint>();
                        // Translate SpellTargetFilter directly to a FactionMask so the
                        // quadtree skips non-matching factions at leaf level. Falls
                        // through to an unfiltered QueryRadius for "everything".
                        FactionMask mask = s.TargetFilter switch
                        {
                            SpellTargetFilter.AnyEnemy   => FactionMaskExt.AllExcept(Faction.Undead),
                            SpellTargetFilter.LivingOnly => FactionMaskExt.AllExcept(Faction.Undead),
                            SpellTargetFilter.UndeadOnly => Faction.Undead.Bit(),
                            _ => FactionMask.All,
                        };
                        if (mask == FactionMask.All)
                            quadtree.QueryRadius(s.TargetPos, s.AoeRadius, nearby);
                        else
                            quadtree.QueryRadiusByFaction(s.TargetPos, s.AoeRadius, mask, nearby);
                        foreach (uint nid in nearby)
                        {
                            int j = UnitUtil.ResolveUnitIndex(units, nid);
                            if (j < 0 || !units[j].Alive) continue;
                            outDamage.Add(new LightningDamage { UnitIdx = j, Damage = s.Damage });
                        }
                    }
                }
                if (s.EffectTimer >= s.EffectDuration) s.Alive = false;
            }
        }

        // Update zaps
        for (int i = _zaps.Count - 1; i >= 0; i--)
        {
            _zaps[i].Timer += dt;
            if (_zaps[i].Timer >= _zaps[i].Duration) _zaps.RemoveAt(i);
        }

        // Update beams
        for (int i = _beams.Count - 1; i >= 0; i--)
        {
            var b = _beams[i];
            b.Elapsed += dt;
            if (b.MaxDuration > 0 && b.Elapsed >= b.MaxDuration) b.Alive = false;
            if (!b.Alive) _beams.RemoveAt(i);
        }

        // Update drains
        for (int i = _drains.Count - 1; i >= 0; i--)
        {
            var d = _drains[i];
            d.Elapsed += dt;
            if (d.MaxDuration > 0 && d.Elapsed >= d.MaxDuration) d.Alive = false;
            if (!d.Alive) _drains.RemoveAt(i);
        }
    }

    public void CancelBeamsForCaster(uint casterID)
    {
        for (int i = _beams.Count - 1; i >= 0; i--)
            if (_beams[i].CasterID == casterID) _beams.RemoveAt(i);
    }

    public void CancelDrainsForCaster(uint casterID)
    {
        for (int i = _drains.Count - 1; i >= 0; i--)
            if (_drains[i].CasterID == casterID) _drains.RemoveAt(i);
    }

    public void Clear()
    {
        _strikes.Clear(); _zaps.Clear(); _beams.Clear(); _drains.Clear();
    }
}
