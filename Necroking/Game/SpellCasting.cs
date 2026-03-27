using System;
using System.Collections.Generic;
using Necroking.Core;
using Necroking.Data;
using Necroking.Data.Registries;
using Necroking.Movement;

namespace Necroking.GameSystems;

public enum CastResult { Success, NotEnoughMana, OnCooldown, NoValidTarget, OutOfRange, NoNecromancer }

public class PendingSpellCast
{
    public string SpellID = "";
    public string Category = "Projectile";
    public Vec2 TargetPos;
    public int TargetCorpseIdx = -1;
    public int TargetUnitIdx = -1;
    public uint TargetUnitID = GameConstants.InvalidUnit;
    public string SummonUnitID = "";
    public int RemainingProjectiles;
    public float ProjectileTimer;
    public string CastingBuffID = "";
    public bool Active;
}

public static class SpellCaster
{
    /// <summary>
    /// Check if a corpse has a valid zombieTypeID on its UnitDef.
    /// </summary>
    private static bool CorpseHasZombieType(Corpse corpse, GameData? gameData)
    {
        if (gameData == null) return true; // no validation possible, allow it
        var def = gameData.Units.Get(corpse.UnitDefID);
        return def != null && !string.IsNullOrEmpty(def.ZombieTypeID);
    }

    public static CastResult TryStartSpellCast(
        string spellID, SpellRegistry spells, NecromancerState necro,
        UnitArrays units, int necroIdx, Vec2 mouseWorld,
        IReadOnlyList<Corpse> corpses, PendingSpellCast outPending,
        GameData? gameData = null)
    {
        if (necroIdx < 0) return CastResult.NoNecromancer;

        var spell = spells.Get(spellID);
        if (spell == null) return CastResult.NoValidTarget;

        if (necro.GetCooldown(spellID) > 0f) return CastResult.OnCooldown;
        if (necro.Mana < spell.ManaCost) return CastResult.NotEnoughMana;

        var necroPos = units.Position[necroIdx];

        // Reset pending state
        outPending.SpellID = spellID;
        outPending.Category = spell.Category;
        outPending.TargetPos = mouseWorld;
        outPending.TargetCorpseIdx = -1;
        outPending.TargetUnitIdx = -1;
        outPending.TargetUnitID = GameConstants.InvalidUnit;
        outPending.SummonUnitID = "";
        outPending.RemainingProjectiles = 0;
        outPending.ProjectileTimer = 0f;
        outPending.CastingBuffID = spell.CastingBuffID;
        outPending.Active = true;

        switch (spell.Category)
        {
            case "Projectile":
            {
                float dist = (mouseWorld - necroPos).Length();
                if (dist > spell.Range) return CastResult.OutOfRange;
                outPending.TargetPos = mouseWorld;
                outPending.RemainingProjectiles = spell.Quantity;
                outPending.ProjectileTimer = 0f;
                break;
            }

            case "Buff":
            case "Debuff":
            {
                outPending.TargetPos = mouseWorld;
                if (spell.AoeType != "AOE")
                {
                    // Single target: find closest valid unit near mouse within spell range
                    float bestDist = float.MaxValue;
                    int bestUnit = -1;
                    for (int i = 0; i < units.Count; i++)
                    {
                        if (!units.Alive[i]) continue;
                        float distToNecro = (units.Position[i] - necroPos).Length();
                        if (distToNecro > spell.Range) continue;
                        if (spell.Category == "Buff" && units.Faction[i] != Faction.Undead) continue;
                        if (spell.Category == "Debuff" && units.Faction[i] == Faction.Undead) continue;

                        float distToCursor = (units.Position[i] - mouseWorld).Length();
                        if (distToCursor < bestDist)
                        {
                            bestDist = distToCursor;
                            bestUnit = i;
                        }
                    }
                    if (bestUnit < 0) return CastResult.NoValidTarget;
                    outPending.TargetUnitIdx = bestUnit;
                    outPending.TargetUnitID = units.Id[bestUnit];
                    outPending.TargetPos = units.Position[bestUnit];
                }
                break;
            }

            case "Summon":
            {
                if (spell.SummonTargetReq == "Corpse")
                {
                    // Find closest corpse to mouse within range of necromancer
                    // If summonUnitID is empty, corpse must have a valid zombieTypeID
                    bool needsZombieType = string.IsNullOrEmpty(spell.SummonUnitID);
                    float bestDist = float.MaxValue;
                    int bestCorpse = -1;
                    for (int i = 0; i < corpses.Count; i++)
                    {
                        if (corpses[i].Dissolving || corpses[i].ConsumedBySummon) continue;
                        if (corpses[i].DraggedByUnitID != GameConstants.InvalidUnit) continue;
                        if (needsZombieType && !CorpseHasZombieType(corpses[i], gameData)) continue;
                        float distToNecro = (corpses[i].Position - necroPos).Length();
                        if (distToNecro > spell.Range) continue;
                        float distToCursor = (corpses[i].Position - mouseWorld).Length();
                        if (distToCursor < bestDist)
                        {
                            bestDist = distToCursor;
                            bestCorpse = i;
                        }
                    }
                    if (bestCorpse < 0) return CastResult.NoValidTarget;
                    outPending.TargetCorpseIdx = bestCorpse;
                    outPending.TargetPos = corpses[bestCorpse].Position;
                    outPending.SummonUnitID = spell.SummonUnitID;
                }
                else if (spell.SummonTargetReq == "UnitType")
                {
                    // Find closest friendly unit matching acceptableTargets near mouse
                    float bestDist = float.MaxValue;
                    int bestUnit = -1;
                    for (int i = 0; i < units.Count; i++)
                    {
                        if (!units.Alive[i]) continue;
                        if (units.Faction[i] != Faction.Undead) continue;
                        float distToNecro = (units.Position[i] - necroPos).Length();
                        if (distToNecro > spell.Range) continue;

                        // Check if unit type matches acceptable targets using unitDefID
                        bool acceptable = spell.AcceptableTargets == null || spell.AcceptableTargets.Count == 0;
                        if (!acceptable)
                        {
                            string uid = units.UnitDefID[i];
                            foreach (var t in spell.AcceptableTargets!)
                            {
                                if (t == uid) { acceptable = true; break; }
                            }
                        }
                        if (!acceptable) continue;

                        float distToCursor = (units.Position[i] - mouseWorld).Length();
                        if (distToCursor < bestDist)
                        {
                            bestDist = distToCursor;
                            bestUnit = i;
                        }
                    }
                    if (bestUnit < 0) return CastResult.NoValidTarget;
                    outPending.TargetUnitIdx = bestUnit;
                    outPending.TargetUnitID = units.Id[bestUnit];
                    outPending.TargetPos = units.Position[bestUnit];
                    outPending.SummonUnitID = spell.SummonUnitID;
                }
                else if (spell.SummonTargetReq == "CorpseAOE")
                {
                    // AOE corpse targeting: validate at least one valid corpse with zombieTypeID in AOE
                    float dist = (mouseWorld - necroPos).Length();
                    if (dist > spell.Range) return CastResult.OutOfRange;

                    bool foundValid = false;
                    for (int i = 0; i < corpses.Count; i++)
                    {
                        if (corpses[i].Dissolving || corpses[i].ConsumedBySummon) continue;
                        if (corpses[i].DraggedByUnitID != GameConstants.InvalidUnit) continue;
                        float distToTarget = (corpses[i].Position - mouseWorld).Length();
                        if (distToTarget > spell.AoeRadius) continue;
                        if (!CorpseHasZombieType(corpses[i], gameData)) continue;
                        foundValid = true;
                        break;
                    }
                    if (!foundValid) return CastResult.NoValidTarget;
                    outPending.TargetPos = mouseWorld;
                }
                else
                {
                    // SummonTargetReq == "None" — just mana + cooldown
                    outPending.SummonUnitID = spell.SummonUnitID;
                    if (spell.SpawnLocation == "AtTargetLocation" ||
                        spell.SpawnLocation == "NearestTargetToMouse")
                    {
                        float dist = (mouseWorld - necroPos).Length();
                        if (spell.Range > 0 && dist > spell.Range) return CastResult.OutOfRange;
                        outPending.TargetPos = mouseWorld;
                    }
                    else
                    {
                        outPending.TargetPos = necroPos; // spawn near caster
                    }
                }
                break;
            }

            case "Strike":
            {
                if (spell.StrikeTargetUnit)
                {
                    // Zap: find closest enemy unit near mouse within range
                    float bestDist = float.MaxValue;
                    int bestUnit = -1;
                    for (int i = 0; i < units.Count; i++)
                    {
                        if (!units.Alive[i]) continue;
                        if (units.Faction[i] == Faction.Undead) continue;
                        float distToNecro = (units.Position[i] - necroPos).Length();
                        if (distToNecro > spell.Range) continue;
                        float distToCursor = (units.Position[i] - mouseWorld).Length();
                        if (distToCursor < bestDist)
                        {
                            bestDist = distToCursor;
                            bestUnit = i;
                        }
                    }
                    if (bestUnit < 0) return CastResult.NoValidTarget;
                    outPending.TargetUnitIdx = bestUnit;
                    outPending.TargetUnitID = units.Id[bestUnit];
                    outPending.TargetPos = units.Position[bestUnit];
                }
                else
                {
                    float dist = (mouseWorld - necroPos).Length();
                    if (dist > spell.Range) return CastResult.OutOfRange;
                    outPending.TargetPos = mouseWorld;
                }
                break;
            }

            case "Beam":
            {
                float bestDist = float.MaxValue;
                int bestUnit = -1;
                for (int i = 0; i < units.Count; i++)
                {
                    if (!units.Alive[i]) continue;
                    if (units.Faction[i] == Faction.Undead) continue;
                    float distToNecro = (units.Position[i] - necroPos).Length();
                    if (distToNecro > spell.Range) continue;
                    float distToCursor = (units.Position[i] - mouseWorld).Length();
                    if (distToCursor < bestDist)
                    {
                        bestDist = distToCursor;
                        bestUnit = i;
                    }
                }
                if (bestUnit < 0) return CastResult.NoValidTarget;
                outPending.TargetUnitIdx = bestUnit;
                outPending.TargetUnitID = units.Id[bestUnit];
                outPending.TargetPos = units.Position[bestUnit];
                break;
            }

            case "Drain":
            {
                // Try closest enemy unit near mouse first
                float bestDist = float.MaxValue;
                int bestUnit = -1;
                for (int i = 0; i < units.Count; i++)
                {
                    if (!units.Alive[i]) continue;
                    if (spell.DrainReversed)
                    {
                        if (units.Faction[i] != Faction.Undead) continue;
                        if (i == necroIdx) continue;
                    }
                    else
                    {
                        if (units.Faction[i] == Faction.Undead) continue;
                    }
                    float distToNecro = (units.Position[i] - necroPos).Length();
                    if (distToNecro > spell.Range) continue;
                    float distToCursor = (units.Position[i] - mouseWorld).Length();
                    if (distToCursor < bestDist)
                    {
                        bestDist = distToCursor;
                        bestUnit = i;
                    }
                }

                if (bestUnit >= 0)
                {
                    outPending.TargetUnitIdx = bestUnit;
                    outPending.TargetUnitID = units.Id[bestUnit];
                    outPending.TargetPos = units.Position[bestUnit];
                    outPending.TargetCorpseIdx = -1;
                }
                else if (!spell.DrainReversed)
                {
                    // Fallback: try corpses
                    float bestCorpseDist = float.MaxValue;
                    int bestCorpse = -1;
                    for (int i = 0; i < corpses.Count; i++)
                    {
                        if (corpses[i].Dissolving || corpses[i].ConsumedBySummon) continue;
                        float distToNecro = (corpses[i].Position - necroPos).Length();
                        if (distToNecro > spell.Range) continue;
                        float distToCursor = (corpses[i].Position - mouseWorld).Length();
                        if (distToCursor < bestCorpseDist)
                        {
                            bestCorpseDist = distToCursor;
                            bestCorpse = i;
                        }
                    }
                    if (bestCorpse < 0) return CastResult.NoValidTarget;
                    outPending.TargetCorpseIdx = bestCorpse;
                    outPending.TargetPos = corpses[bestCorpse].Position;
                    outPending.TargetUnitID = GameConstants.InvalidUnit;
                }
                else
                {
                    return CastResult.NoValidTarget;
                }
                break;
            }

            case "Command":
            {
                outPending.TargetPos = mouseWorld;
                break;
            }

            case "Toggle":
            {
                outPending.TargetPos = necroPos;
                break;
            }

            default:
            {
                float dist = (mouseWorld - necroPos).Length();
                if (dist > spell.Range) return CastResult.OutOfRange;
                outPending.TargetPos = mouseWorld;
                break;
            }
        }

        // Deduct mana and start cooldown
        necro.Mana -= spell.ManaCost;
        if (spell.Cooldown > 0f)
            necro.SpellCooldowns[spellID] = spell.Cooldown;

        return CastResult.Success;
    }
}
