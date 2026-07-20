using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Necroking.Core;
using Necroking.Data;
using Necroking.Lib;
using Necroking.Movement;

namespace Necroking.GameSystems;

public class LightningStyle
{
    public HdrColor CoreColor = new(255, 255, 255, 255, 4f);
    public HdrColor GlowColor = new(140, 180, 255, 200, 2.5f);
    public float CoreWidth = 2f;
    public float GlowWidth = 8f;
    /// <summary>How much the bolt's WIDTH follows the lifetime fade: 1 = width
    /// shrinks with fade (collapse-to-a-thread), 0 = constant width, only
    /// brightness fades. Alpha always fades regardless.</summary>
    public float WidthFade = 1f;
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

    // ScatterGlow sheath (0 radius = none): LightningRenderer registers a
    // polyline emitter along the bolt path, flicker-synced, so the air near the
    // bolt visibly lights up (world units; see Render/ScatterGlowSystem.cs).
    public float ScatterRadius;
    public Color ScatterRgb;
    public float ScatterStrength = 1f;
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
    /// <summary>Stable per-instance seed salt: the bolt only re-rolls its shape on
    /// the JitterHz clock, never because an endpoint (or the camera) moved.</summary>
    public uint Seed;
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
    /// <summary>Casting unit, propagated to each LightningDamage so the strike's kills
    /// attribute to the caster (LastAttackerID / kill tally). InvalidUnit = unattributed.</summary>
    public uint OwnerID = GameConstants.InvalidUnit;
}

public class ActiveZap
{
    /// <summary>Stable per-instance seed salt (see ActiveStrike.Seed).</summary>
    public uint Seed;
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
    /// <summary>Stable per-instance seed salt (see ActiveStrike.Seed). Without it a
    /// beam whose endpoints move (walking target) re-rolled its shape every frame,
    /// making JitterHz appear to "go crazy" mid-channel.</summary>
    public uint Seed;
    public uint CasterID;
    public uint TargetID;
    public string SpellID = "";
    public float DamageAccumulator;
    public float TickRate;
    public int DamagePerTick;
    public float RetargetRadius;
    /// <summary>Where the target was last seen alive — the retarget hop searches
    /// around the fallen target, not the caster.</summary>
    public Vec2 LastTargetPos;
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

    // ScatterGlow sheath along the drain's caster→target line (0 radius = none) —
    // same convention as LightningStyle.Scatter*; mapped from the shared SpellDef
    // SCATTER fields in BuildDrainVisuals.
    public float ScatterRadius;
    public Color ScatterRgb;
    public float ScatterStrength = 1f;
    // Visual flow direction. false = life flows target→caster (the normal drain);
    // true = caster→target. Resolved in SpellDef.BuildDrainVisuals from
    // DrainReversed XOR DrainVisualReversed, so the renderer never re-derives it.
    public bool FlowReversed;
    // Pugna-style funnel: width multiplier at the flow-SOURCE end (the end life is
    // pulled from — the target on a normal drain). 1 = uniform width; >1 = beam
    // fans out toward the source and stays narrow at the destination.
    public float SourceWidthScale = 1f;
    // Cloud puffs traveling source→destination along the beam. Count 0 = off.
    public int CloudCount;
    public float CloudSize = 10f;          // puff radius, screen px
    public float CloudSpeed = 0.5f;        // beam-lengths per second
    public HdrColor CloudColor = new(200, 255, 160, 255, 3f);
    // Beam color at the flow-SOURCE end (the target on a normal drain) — the
    // destination end uses Core/GlowColor, RGB+intensity lerping along the arc
    // (hotter/different at the victim, like the Dota reference). Defaults match
    // Core/GlowColor's defaults, i.e. no gradient until authored.
    public HdrColor SourceCoreColor = new(120, 255, 80, 255, 2.5f);
    public HdrColor SourceGlowColor = new(40, 120, 20, 160, 1.5f);
    // Scrolling streak-noise overlay traveling source→destination along the
    // beam. Speed is px/sec (0 = off), Scale is px per texture repeat.
    public float ScrollSpeed;
    public float ScrollScale = 90f;
    public float ScrollAlpha = 0.5f;
    // Impact cluster at the flow-source end: flipbook puffs that churn over the
    // beam/target junction (drawn in front of the beam, tinted SourceCoreColor)
    // plus an additive flare. PuffCount 0 = off; FlareScale 0 = no flare.
    public int ImpactPuffCount;
    public float ImpactSize = 14f;         // cluster radius, screen px
    public string ImpactFlipbookID = "cloud03";
    public float ImpactFlareScale = 1f;
}

public class ActiveDrain
{
    public uint CasterID;
    public uint TargetID;
    public int TargetCorpseIdx = -1;
    /// <summary>Stable corpse id (Corpse.CorpseID) for corpse-target drains — ticks
    /// re-resolve by this, never by the captured index (the corpse list compacts).</summary>
    public int TargetCorpseID = -1;
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
    /// <summary>Casting unit (from the strike/beam/drain) for attacker attribution. InvalidUnit = none.</summary>
    public uint OwnerID = GameConstants.InvalidUnit;
    /// <summary>True = Damage is a HEAL for UnitIdx (drain life transfer). Applied by
    /// Simulation as a clamped HP gain instead of going through DamageSystem.</summary>
    public bool IsHeal;
    /// <summary>Source spell id — lets Simulation resolve the SpellDef and route the
    /// damage through ApplySpellDamage (MR gate + opposed DRN roll + AN/DN flags).
    /// Empty = flat armor-negating damage (dev spawns, reversed-drain self-cost).</summary>
    public string SpellID = "";
    /// <summary>Drain coupling: when >= 0, heal this unit by HealPercent of the
    /// damage ACTUALLY dealt after MR/armor/toughness (a resisted or glancing tick
    /// drains nothing).</summary>
    public int HealTargetIdx = -1;
    public float HealPercent;
    /// <summary>True = apply the EXACT amount flat (no rolls/mitigation) — the
    /// reversed-drain self-cost, where the caster pays a known price per tick.</summary>
    public bool Flat;
}

public class LightningSystem
{
    private readonly List<ActiveStrike> _strikes = new();
    private readonly List<ActiveZap> _zaps = new();
    private readonly List<ActiveBeam> _beams = new();
    private readonly List<ActiveDrain> _drains = new();

    // Per-instance seed salts (visual only — never touches sim determinism).
    private uint _seedCounter = 0x9E3779B9;
    private uint NextSeed() => _seedCounter = _seedCounter * 1103515245u + 12345u;

    public IReadOnlyList<ActiveStrike> Strikes => _strikes;
    public IReadOnlyList<ActiveZap> Zaps => _zaps;
    public IReadOnlyList<ActiveBeam> Beams => _beams;
    public IReadOnlyList<ActiveDrain> Drains => _drains;

    public void SpawnStrike(Vec2 targetPos, float telegraphDuration, float effectDuration,
                            float aoeRadius, int damage, LightningStyle style, string spellID,
                            StrikeVisual visual = StrikeVisual.Lightning, GodRayParams? godRay = null,
                            SpellTargetFilter targetFilter = SpellTargetFilter.AnyEnemy,
                            bool telegraphVisible = true,
                            uint ownerID = GameConstants.InvalidUnit)
    {
        _strikes.Add(new ActiveStrike
        {
            Seed = NextSeed(),
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
            TargetFilter = targetFilter,
            OwnerID = ownerID
        });
    }

    public void SpawnBeam(uint casterID, uint targetID, string spellID,
                          int damagePerTick, float tickRate, float retargetRadius,
                          LightningStyle style, float maxDuration = 0f)
    {
        _beams.Add(new ActiveBeam
        {
            Seed = NextSeed(),
            CasterID = casterID, TargetID = targetID, SpellID = spellID,
            DamagePerTick = damagePerTick, TickRate = tickRate, RetargetRadius = retargetRadius,
            Style = style, MaxDuration = maxDuration
        });
    }

    public void SpawnDrain(uint casterID, uint targetID, string spellID,
                           int damagePerTick, float tickRate, float healPercent,
                           int corpseHP, bool reversed, float maxDuration,
                           DrainVisualParams visuals,
                           int targetCorpseIdx = -1, int targetCorpseID = -1,
                           Vec2 corpsePos = default)
    {
        _drains.Add(new ActiveDrain
        {
            CasterID = casterID, TargetID = targetID, SpellID = spellID,
            DamagePerTick = damagePerTick, TickRate = tickRate, HealPercent = healPercent,
            CorpseHP = corpseHP, Reversed = reversed, MaxDuration = maxDuration,
            Visuals = visuals,
            TargetCorpseIdx = targetCorpseIdx, TargetCorpseID = targetCorpseID,
            CorpsePos = corpsePos
        });
    }

    public void SpawnZap(Vec2 start, Vec2 end, float duration, LightningStyle style,
                         float startHeight = 0f, float endHeight = 0f)
    {
        _zaps.Add(new ActiveZap
        {
            Seed = NextSeed(),
            StartPos = start, EndPos = end, Duration = duration, Style = style,
            StartHeight = startHeight, EndHeight = endHeight
        });
    }

    public void Update(float dt, List<LightningDamage> outDamage,
                        Quadtree? quadtree = null, UnitArrays? units = null,
                        List<Corpse>? corpses = null)
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

                    // The strike LANDS this frame (telegraph elapsed) — fire the
                    // spell's hit effect at the impact point. Cosmetic, so the
                    // direct Game1 call is fine (project convention).
                    var g1 = Game1.Instance;
                    if (g1 != null && s.SpellID.Length > 0)
                    {
                        var hitSpell = g1._gameData.Spells.Get(s.SpellID);
                        if (hitSpell?.HitEffectFlipbook != null)
                            g1.SpawnFlipbookEffect(hitSpell.HitEffectFlipbook, s.TargetPos,
                                scatterRadius: hitSpell.ScatterRadius * 1.6f,
                                scatterRgb: hitSpell.ScatterRgb(),
                                scatterStrength: hitSpell.ScatterStrength);
                    }

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
                            outDamage.Add(new LightningDamage
                            { UnitIdx = j, Damage = s.Damage, OwnerID = s.OwnerID, SpellID = s.SpellID });
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
            // A channel is only as alive as its endpoints: a caster that dies or
            // gets disabled (stun/knockdown/knockback/paralysis/jump) drops the
            // beam. A dead target first tries the retarget hop below; only when
            // no candidate is in RetargetRadius does the beam die too.
            // This is the core channel-interrupt rule — it covers the player and
            // AI casters alike; their slot/timer bookkeeping syncs off the beam
            // disappearing (Game1's channel-hold block / Unit.ChannelTimer here).
            if (units != null && !ChannelSourceValid(units, b.CasterID)) b.Alive = false;
            if (units != null && b.Alive)
            {
                int ti = UnitUtil.ResolveUnitIndex(units, b.TargetID);
                if (ti >= 0 && units[ti].Alive)
                {
                    b.LastTargetPos = units[ti].Position;
                }
                else
                {
                    // Retarget hop: the target fell mid-channel — arc to the
                    // closest living enemy of the caster near where it fell.
                    ti = RetargetBeam(b, quadtree, units);
                    if (ti >= 0) { b.TargetID = units[ti].Id; b.LastTargetPos = units[ti].Position; }
                    else b.Alive = false;
                }

                // Damage ticks through the standard pipeline: emitted as
                // LightningDamage and applied by Simulation.DealDamage with
                // caster attribution — same as strike AoE damage. Base 0 still
                // ticks (the opposed DRN roll can land); negative = visual-only.
                if (b.Alive && b.DamagePerTick >= 0)
                {
                    b.DamageAccumulator += dt;
                    float interval = b.TickRate > 0.01f ? b.TickRate : 0.25f;
                    while (b.DamageAccumulator >= interval)
                    {
                        b.DamageAccumulator -= interval;
                        outDamage.Add(new LightningDamage
                        {
                            UnitIdx = ti, Damage = b.DamagePerTick, OwnerID = b.CasterID,
                            SpellID = b.SpellID
                        });
                    }
                }
            }
            if (!b.Alive) _beams.RemoveAt(i);
        }

        // Update drains
        for (int i = _drains.Count - 1; i >= 0; i--)
        {
            var d = _drains[i];
            d.Elapsed += dt;
            if (d.MaxDuration > 0 && d.Elapsed >= d.MaxDuration) d.Alive = false;
            // Same caster/target channel-interrupt rule as beams. Corpse-targeted
            // drains (TargetCorpseIdx >= 0) skip the unit-target aliveness check.
            if (units != null && !ChannelSourceValid(units, d.CasterID)) d.Alive = false;
            if (units != null && d.Alive && d.TargetCorpseIdx < 0)
            {
                int ti = UnitUtil.ResolveUnitIndex(units, d.TargetID);
                if (ti < 0 || !units[ti].Alive) d.Alive = false;
            }

            // Drain ticks. Damage goes out as LightningDamage (standard
            // DealDamage pipeline in Simulation); heals go out with IsHeal set
            // (clamped HP gain in Simulation). Three modes:
            //  - enemy unit:   damage target, heal caster by HealPercent of it
            //  - corpse:       consume the corpse's CorpseHP pool, heal caster;
            //                  the corpse dissolves when the pool empties
            //  - reversed:     transfer — damage the CASTER, heal the friendly
            //                  target; stops before the caster would die
            // Base 0 still ticks for the normal enemy mode — the emitted event goes
            // through ApplySpellDamage's opposed DRN roll, which can land damage on
            // its own. Negative = visual-only (dev spawn_lightning). Corpse and
            // reversed modes stay >0-gated: their amounts are flat (pool pull /
            // self-cost transfer), no roll is involved.
            bool zeroTicks = d.DamagePerTick == 0 && d.TargetCorpseIdx < 0 && !d.Reversed;
            if (units != null && d.Alive && (d.DamagePerTick > 0 || zeroTicks))
            {
                d.DamageAccumulator += dt;
                float interval = d.TickRate > 0.01f ? d.TickRate : 0.25f;
                while (d.DamageAccumulator >= interval && d.Alive)
                {
                    d.DamageAccumulator -= interval;
                    int ci = UnitUtil.ResolveUnitIndex(units, d.CasterID);
                    if (ci < 0 || !units[ci].Alive) { d.Alive = false; break; }
                    int heal = (int)MathF.Round(d.DamagePerTick * d.HealPercent);

                    if (d.TargetCorpseIdx >= 0)
                    {
                        int corpseIdx = FindCorpseIndex(corpses, d.TargetCorpseID);
                        if (corpseIdx < 0) { d.Alive = false; break; }
                        int pull = Math.Min(d.DamagePerTick, d.CorpseHP);
                        if (pull <= 0) { d.Alive = false; break; }
                        d.CorpseHP -= pull;
                        int corpseHeal = (int)MathF.Round(pull * d.HealPercent);
                        if (corpseHeal > 0)
                            outDamage.Add(new LightningDamage
                            { UnitIdx = ci, Damage = corpseHeal, OwnerID = d.CasterID, IsHeal = true });
                        if (d.CorpseHP <= 0)
                        {
                            // Pool exhausted — the husk crumbles (poison-cloud
                            // corpse-consumption convention).
                            corpses![corpseIdx].Dissolving = true;
                            d.Alive = false;
                        }
                    }
                    else if (d.Reversed)
                    {
                        int ti = UnitUtil.ResolveUnitIndex(units, d.TargetID);
                        if (ti < 0 || !units[ti].Alive) { d.Alive = false; break; }
                        // Never let the transfer kill the caster.
                        if (units[ci].Stats.HP <= d.DamagePerTick) { d.Alive = false; break; }
                        outDamage.Add(new LightningDamage
                        { UnitIdx = ci, Damage = d.DamagePerTick, OwnerID = d.CasterID, Flat = true });
                        if (heal > 0)
                            outDamage.Add(new LightningDamage
                            { UnitIdx = ti, Damage = heal, OwnerID = d.CasterID, IsHeal = true });
                    }
                    else
                    {
                        int ti = UnitUtil.ResolveUnitIndex(units, d.TargetID);
                        if (ti < 0 || !units[ti].Alive) { d.Alive = false; break; }
                        // Heal is coupled to the damage the tick ACTUALLY deals
                        // (post MR/armor/toughness) — Simulation resolves both.
                        outDamage.Add(new LightningDamage
                        {
                            UnitIdx = ti, Damage = d.DamagePerTick, OwnerID = d.CasterID,
                            SpellID = d.SpellID, HealTargetIdx = ci, HealPercent = d.HealPercent
                        });
                    }
                }
            }
            if (!d.Alive) _drains.RemoveAt(i);
        }
    }

    /// <summary>Resolve a corpse-list index from a stable CorpseID (the list
    /// compacts, so stored indices go stale). Skips dissolving/consumed corpses.</summary>
    private static int FindCorpseIndex(List<Corpse>? corpses, int corpseID)
    {
        if (corpses == null || corpseID < 0) return -1;
        for (int i = 0; i < corpses.Count; i++)
            if (corpses[i].CorpseID == corpseID)
                return corpses[i].Dissolving || corpses[i].ConsumedBySummon ? -1 : i;
        return -1;
    }

    /// <summary>Closest living enemy of the beam's caster within RetargetRadius of
    /// where the previous target fell; -1 when none (the beam ends).</summary>
    private static int RetargetBeam(ActiveBeam b, Quadtree? quadtree, UnitArrays units)
    {
        if (quadtree == null || b.RetargetRadius <= 0f) return -1;
        int ci = UnitUtil.ResolveUnitIndex(units, b.CasterID);
        if (ci < 0) return -1;
        var nearby = new List<uint>();
        quadtree.QueryRadiusByFaction(b.LastTargetPos, b.RetargetRadius,
            FactionMaskExt.AllExcept(units[ci].Faction), nearby);
        int best = -1;
        float bestDist = float.MaxValue;
        foreach (uint nid in nearby)
        {
            int j = UnitUtil.ResolveUnitIndex(units, nid);
            if (j < 0 || !units[j].Alive) continue;
            float dist = (units[j].Position - b.LastTargetPos).LengthSq();
            if (dist < bestDist) { bestDist = dist; best = j; }
        }
        return best;
    }

    /// <summary>True while the caster can keep sustaining its channel. As a side
    /// effect, zeroes a broken caster's ChannelTimer so an AI caster whose handler
    /// is skipped while it's disabled (UpdateAI skips Incap/InPhysics units) doesn't
    /// wake up still "channeling" a beam that no longer exists.</summary>
    private static bool ChannelSourceValid(UnitArrays units, uint casterID)
    {
        int ci = UnitUtil.ResolveUnitIndex(units, casterID);
        if (ci < 0 || !units[ci].Alive) return false;
        if (units[ci].IsChannelBroken())
        {
            units[ci].ChannelTimer = 0f;
            return false;
        }
        return true;
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
