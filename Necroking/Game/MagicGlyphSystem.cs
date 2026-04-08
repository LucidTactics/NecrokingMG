using System;
using System.Collections.Generic;
using Necroking.Core;
using Necroking.Data;
using Necroking.Data.Registries;
using Necroking.Movement;
using Necroking.Spatial;

namespace Necroking.GameSystems;

public enum GlyphState : byte { Blueprint, Dormant, Triggering, Active, Fading }

public class MagicGlyph
{
    public Vec2 Position;
    public float Radius = 1f;           // World-space radius
    public float Age;
    public GlyphState State = GlyphState.Dormant;
    public float StateTimer;

    // Visual config
    public HdrColor Color = new(140, 80, 200, 255, 1.5f);      // Primary color
    public HdrColor Color2 = new(200, 160, 255, 255, 2.0f);     // Secondary / inner color
    public float PulseSpeed = 2f;
    public float RotationSpeed = 0f;
    public int SymbolCount = 6;

    // Timing
    public float TriggerDuration = 1.0f;    // Charge-up: glyph brightens, no ribbons
    public float ActiveDuration = 4f;       // Burst then decay over this time
    public float FadeDuration = 1f;         // Glyph fades out after ribbons done

    // Gameplay
    public int Damage;
    public float DamageRadius;              // Can differ from visual radius
    public Faction OwnerFaction;
    public bool DamageApplied;
    public bool Alive = true;
    public string TriggerSpellID = "";      // If set, spawns this spell on trigger instead of flat damage

    // Derived
    // Build progress: 0 when placed as blueprint, set to 1 when building completes
    public float BuildProgress;

    public float Activation => State switch
    {
        GlyphState.Blueprint => 0f,
        GlyphState.Dormant => 0f,
        GlyphState.Triggering => MathF.Min(StateTimer / TriggerDuration, 1f),
        GlyphState.Active => 1f,
        GlyphState.Fading => MathF.Max(0f, 1f - StateTimer / FadeDuration),
        _ => 0f
    };

    public float Intensity => State switch
    {
        GlyphState.Blueprint => 0.15f + 0.15f * BuildProgress, // dim, brightens as built
        GlyphState.Dormant => 0.8f,
        GlyphState.Triggering => 0.6f + 3.4f * Activation,
        GlyphState.Active => MathF.Max(0.8f, 4f - StateTimer / ActiveDuration * 3.2f),
        GlyphState.Fading => MathF.Max(0.05f, 1f - StateTimer / FadeDuration),
        _ => 0f
    };

    /// <summary>0 during dormant/triggering, 1.0 at burst start, decays to 0 over ActiveDuration.</summary>
    public float RibbonIntensity => State switch
    {
        GlyphState.Active => MathF.Max(0f, 1f - StateTimer / ActiveDuration),
        _ => 0f
    };
}

public class MagicGlyphSystem
{
    private readonly List<MagicGlyph> _glyphs = new();

    public IReadOnlyList<MagicGlyph> Glyphs => _glyphs;

    public MagicGlyph SpawnGlyph(Vec2 position, float radius, Faction owner)
    {
        var glyph = new MagicGlyph
        {
            Position = position,
            Radius = radius,
            OwnerFaction = owner,
        };
        _glyphs.Add(glyph);
        return glyph;
    }

    public MagicGlyph SpawnBlueprint(Vec2 position, float radius, Faction owner)
    {
        var glyph = new MagicGlyph
        {
            Position = position,
            Radius = radius,
            OwnerFaction = owner,
            State = GlyphState.Blueprint,
            BuildProgress = 0f,
        };
        _glyphs.Add(glyph);
        return glyph;
    }

    /// <summary>Get the list index of a glyph, or -1.</summary>
    public int IndexOf(MagicGlyph glyph)
    {
        for (int i = 0; i < _glyphs.Count; i++)
            if (_glyphs[i] == glyph) return i;
        return -1;
    }

    public MagicGlyph? GetGlyph(int index) =>
        index >= 0 && index < _glyphs.Count ? _glyphs[index] : null;

    /// <summary>Check if a glyph can be placed (no overlap with existing glyphs).</summary>
    public bool CanPlace(Vec2 position, float radius)
    {
        for (int i = 0; i < _glyphs.Count; i++)
        {
            if (!_glyphs[i].Alive) continue;
            float minDist = _glyphs[i].Radius + radius;
            float dx = _glyphs[i].Position.X - position.X;
            float dy = _glyphs[i].Position.Y - position.Y;
            if (dx * dx + dy * dy < minDist * minDist) return false;
        }
        return true;
    }

    private readonly List<DamageEvent> _damageEvents = new();
    public IReadOnlyList<DamageEvent> DamageEvents => _damageEvents;

    public void Update(float dt, UnitArrays units, Quadtree qt,
                       PoisonCloudSystem? poisonClouds = null, SpellRegistry? spells = null)
    {
        _damageEvents.Clear();
        var nearbyIDs = new List<uint>();

        for (int i = _glyphs.Count - 1; i >= 0; i--)
        {
            var g = _glyphs[i];
            if (!g.Alive) { _glyphs.RemoveAt(i); continue; }

            g.Age += dt;
            if (g.State != GlyphState.Blueprint)
                g.StateTimer += dt;

            switch (g.State)
            {
                case GlyphState.Blueprint:
                    // Inert until built — rendering uses Intensity/BuildProgress
                    break;

                case GlyphState.Dormant:
                    // Check for enemy units stepping on it
                    nearbyIDs.Clear();
                    qt.QueryRadius(g.Position, g.Radius, nearbyIDs);
                    foreach (uint uid in nearbyIDs)
                    {
                        int idx = UnitUtil.ResolveUnitIndex(units, uid);
                        if (idx < 0 || !units[idx].Alive) continue;
                        if (units[idx].Faction == g.OwnerFaction) continue;

                        // Enemy stepped on glyph — trigger it
                        g.State = GlyphState.Triggering;
                        g.StateTimer = 0f;
                        break;
                    }
                    break;

                case GlyphState.Triggering:
                    if (g.StateTimer >= g.TriggerDuration)
                    {
                        g.State = GlyphState.Active;
                        g.StateTimer = 0f;
                    }
                    break;

                case GlyphState.Active:
                    if (!g.DamageApplied)
                    {
                        g.DamageApplied = true;

                        // Spawn trigger spell (poison cloud) if configured
                        if (!string.IsNullOrEmpty(g.TriggerSpellID) && poisonClouds != null && spells != null)
                        {
                            var spell = spells.Get(g.TriggerSpellID);
                            if (spell != null)
                            {
                                poisonClouds.SpawnCloud(g.Position, spell, g.OwnerFaction);

                                // Apply instant AoE poison through unified damage system
                                if (spell.Damage > 0)
                                {
                                    var flags = DamageFlags.None;
                                    if (spell.ArmorNegating) flags |= DamageFlags.ArmorNegating;
                                    if (spell.DefenseNegating) flags |= DamageFlags.DefenseNegating;
                                    float aoeR = spell.AoeRadius > 0 ? spell.AoeRadius : spell.CloudRadius;
                                    DamageSystem.ApplyAoE(units, qt, g.Position, aoeR,
                                        spell.Damage, DamageType.Poison, flags, g.OwnerFaction, _damageEvents);
                                }
                            }
                        }

                        // Apply flat physical damage to nearby enemies
                        if (g.Damage > 0)
                        {
                            float dmgR = g.DamageRadius > 0 ? g.DamageRadius : g.Radius;
                            DamageSystem.ApplyAoE(units, qt, g.Position, dmgR,
                                g.Damage, DamageType.Physical, DamageFlags.None,
                                g.OwnerFaction, _damageEvents);
                        }
                    }

                    if (g.StateTimer >= g.ActiveDuration)
                    {
                        g.State = GlyphState.Fading;
                        g.StateTimer = 0f;
                    }
                    break;

                case GlyphState.Fading:
                    if (g.StateTimer >= g.FadeDuration)
                    {
                        g.Alive = false;
                        _glyphs.RemoveAt(i);
                    }
                    break;
            }
        }
    }

    public void Clear() => _glyphs.Clear();
}
