using System;
using System.Collections.Generic;
using Necroking.Core;
using Necroking.Data;
using Necroking.Data.Registries;
using Necroking.Game;
using Necroking.Movement;

namespace Necroking.GameSystems;

public enum CastResult { Success, NotEnoughMana, OnCooldown, NoValidTarget, OutOfRange, NoNecromancer, HordeCapFull }

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
        // Path-aware cost: caster's UnitDef carries Paths, the spell's primary
        // path scales the mana cost downward when caster level exceeds the
        // requirement. Secondary path is a hard gate only — meeting it doesn't
        // reduce cost. Without a UnitDef we fall back to flat ManaCost so
        // existing test paths keep working. Buff "AllPaths" Set effects floor
        // every path level (e.g. god mode = 9 everywhere) without overwriting
        // higher native levels.
        var casterDef = gameData?.Units.Get(units[necroIdx].UnitDefID);
        Func<MagicPath, int> casterLevel = ResolveCasterLevel(casterDef, units, necroIdx);
        if (!spell.MeetsPathRequirements(casterLevel)) return CastResult.NotEnoughMana;
        float effectiveCost = spell.EffectiveManaCost(casterLevel);
        if (necro.Mana < effectiveCost) return CastResult.NotEnoughMana;

        var necroPos = units[necroIdx].Position;

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

        // Floating spell-name label above the necromancer for the cast duration.
        // Generic-action label written by every commit point — see UnitData.ActionLabel.
        units[necroIdx].ActionLabel = string.IsNullOrEmpty(spell.DisplayName) ? spell.Id : spell.DisplayName;
        units[necroIdx].ActionLabelTimer = 1.5f;

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
                        if (!units[i].Alive) continue;
                        float distToNecro = (units[i].Position - necroPos).Length();
                        if (distToNecro > spell.Range) continue;
                        if (spell.Category == "Buff" && units[i].Faction != Faction.Undead) continue;
                        if (spell.Category == "Debuff" && units[i].Faction == Faction.Undead) continue;

                        float distToCursor = (units[i].Position - mouseWorld).Length();
                        if (distToCursor < bestDist)
                        {
                            bestDist = distToCursor;
                            bestUnit = i;
                        }
                    }
                    if (bestUnit < 0) return CastResult.NoValidTarget;
                    outPending.TargetUnitIdx = bestUnit;
                    outPending.TargetUnitID = units[bestUnit].Id;
                    outPending.TargetPos = units[bestUnit].Position;
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
                        if (corpses[i].BaggedByUnitID != GameConstants.InvalidUnit) continue;
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
                        if (!units[i].Alive) continue;
                        if (units[i].Faction != Faction.Undead) continue;
                        float distToNecro = (units[i].Position - necroPos).Length();
                        if (distToNecro > spell.Range) continue;

                        // Check if unit type matches acceptable targets using unitDefID
                        bool acceptable = spell.AcceptableTargets == null || spell.AcceptableTargets.Count == 0;
                        if (!acceptable)
                        {
                            string uid = units[i].UnitDefID;
                            foreach (var t in spell.AcceptableTargets!)
                            {
                                if (t == uid) { acceptable = true; break; }
                            }
                        }
                        if (!acceptable) continue;

                        float distToCursor = (units[i].Position - mouseWorld).Length();
                        if (distToCursor < bestDist)
                        {
                            bestDist = distToCursor;
                            bestUnit = i;
                        }
                    }
                    if (bestUnit < 0) return CastResult.NoValidTarget;
                    outPending.TargetUnitIdx = bestUnit;
                    outPending.TargetUnitID = units[bestUnit].Id;
                    outPending.TargetPos = units[bestUnit].Position;
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
                        if (corpses[i].BaggedByUnitID != GameConstants.InvalidUnit) continue;
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

                // Horde-cap pre-check. Refuses the cast outright (no mana spent,
                // no cooldown) when the target category is full. Skipped for
                // Transform mode — it replaces an existing unit, doesn't grow
                // the army — and for CorpseAOE-without-a-fixed-summonUnitID,
                // since per-corpse category resolution happens at execute time
                // (different corpses may produce different categories).
                if (spell.SummonMode != "Transform")
                {
                    string predictId = outPending.SummonUnitID;
                    if (string.IsNullOrEmpty(predictId)
                        && spell.SummonTargetReq == "Corpse"
                        && outPending.TargetCorpseIdx >= 0)
                    {
                        predictId = TableCraftingSystem.ResolveZombieUnitID(
                            gameData!, corpses[outPending.TargetCorpseIdx].UnitDefID);
                    }
                    if (!string.IsNullOrEmpty(predictId))
                    {
                        var cat = HordeCapTracker.CategoryFor(gameData, predictId);
                        if (cat != UndeadCategory.None
                            && HordeCapTracker.Available(units, gameData, necro, cat) <= 0)
                        {
                            return CastResult.HordeCapFull;
                        }
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
                        if (!units[i].Alive) continue;
                        if (units[i].Faction == Faction.Undead) continue;
                        float distToNecro = (units[i].Position - necroPos).Length();
                        if (distToNecro > spell.Range) continue;
                        float distToCursor = (units[i].Position - mouseWorld).Length();
                        if (distToCursor < bestDist)
                        {
                            bestDist = distToCursor;
                            bestUnit = i;
                        }
                    }
                    if (bestUnit < 0) return CastResult.NoValidTarget;
                    outPending.TargetUnitIdx = bestUnit;
                    outPending.TargetUnitID = units[bestUnit].Id;
                    outPending.TargetPos = units[bestUnit].Position;
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
                    if (!units[i].Alive) continue;
                    if (units[i].Faction == Faction.Undead) continue;
                    float distToNecro = (units[i].Position - necroPos).Length();
                    if (distToNecro > spell.Range) continue;
                    float distToCursor = (units[i].Position - mouseWorld).Length();
                    if (distToCursor < bestDist)
                    {
                        bestDist = distToCursor;
                        bestUnit = i;
                    }
                }
                if (bestUnit < 0) return CastResult.NoValidTarget;
                outPending.TargetUnitIdx = bestUnit;
                outPending.TargetUnitID = units[bestUnit].Id;
                outPending.TargetPos = units[bestUnit].Position;
                break;
            }

            case "Drain":
            {
                // Try closest enemy unit near mouse first
                float bestDist = float.MaxValue;
                int bestUnit = -1;
                for (int i = 0; i < units.Count; i++)
                {
                    if (!units[i].Alive) continue;
                    if (spell.DrainReversed)
                    {
                        if (units[i].Faction != Faction.Undead) continue;
                        if (i == necroIdx) continue;
                    }
                    else
                    {
                        if (units[i].Faction == Faction.Undead) continue;
                    }
                    float distToNecro = (units[i].Position - necroPos).Length();
                    if (distToNecro > spell.Range) continue;
                    float distToCursor = (units[i].Position - mouseWorld).Length();
                    if (distToCursor < bestDist)
                    {
                        bestDist = distToCursor;
                        bestUnit = i;
                    }
                }

                if (bestUnit >= 0)
                {
                    outPending.TargetUnitIdx = bestUnit;
                    outPending.TargetUnitID = units[bestUnit].Id;
                    outPending.TargetPos = units[bestUnit].Position;
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

            case "Sacrifice":
            {
                // Target the friendly undead nearest the cursor within range (never
                // the caster). A non-empty AcceptableTargets list restricts it to
                // specific UnitDef ids (e.g. skeletons only).
                float bestDist = float.MaxValue;
                int bestUnit = -1;
                bool restrict = spell.AcceptableTargets != null && spell.AcceptableTargets.Count > 0;
                for (int i = 0; i < units.Count; i++)
                {
                    if (!units[i].Alive) continue;
                    if (i == necroIdx) continue;
                    if (units[i].Faction != Faction.Undead) continue;
                    if (restrict && !spell.AcceptableTargets!.Contains(units[i].UnitDefID)) continue;
                    float distToNecro = (units[i].Position - necroPos).Length();
                    if (distToNecro > spell.Range) continue;
                    float distToCursor = (units[i].Position - mouseWorld).Length();
                    if (distToCursor < bestDist)
                    {
                        bestDist = distToCursor;
                        bestUnit = i;
                    }
                }
                if (bestUnit < 0) return CastResult.NoValidTarget;
                outPending.TargetUnitIdx = bestUnit;
                outPending.TargetUnitID = units[bestUnit].Id;
                outPending.TargetPos = units[bestUnit].Position;
                break;
            }

            case "Cloud":
            {
                float dist = (mouseWorld - necroPos).Length();
                if (dist > spell.Range) return CastResult.OutOfRange;
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

        // Deduct mana and start cooldown. Recompute the effective cost rather
        // than capturing it from the eligibility check so we stay in sync if a
        // future refactor moves the gate around.
        var castingDef = gameData?.Units.Get(units[necroIdx].UnitDefID);
        Func<MagicPath, int> castingLevel = ResolveCasterLevel(castingDef, units, necroIdx);
        necro.Mana -= spell.EffectiveManaCost(castingLevel);
        if (spell.Cooldown > 0f)
            necro.SpellCooldowns[spellID] = spell.Cooldown;

        return CastResult.Success;
    }

    /// <summary>True if the necromancer meets a spell's magic-path requirements
    /// (primary + secondary path/level). PATH GATING ONLY — deliberately does
    /// NOT check mana, cooldown, or targeting: "do I have this spell" is about
    /// unlocked paths, not current resources (players don't expect a spell to
    /// vanish from the book just because they're low on mana). Used to filter
    /// the grimoire so spells whose paths the player hasn't unlocked don't show.
    /// necroIdx &lt; 0 (no caster context, e.g. editor/tests) => true (show all).</summary>
    public static bool HasSpellRequirements(SpellDef? spell, GameData? gameData,
        UnitArrays units, int necroIdx)
    {
        if (spell == null) return false;
        if (necroIdx < 0) return true;
        var def = gameData?.Units.Get(units[necroIdx].UnitDefID);
        return spell.MeetsPathRequirements(ResolveCasterLevel(def, units, necroIdx));
    }

    /// <summary>Build the magic-path level lookup used for both requirement
    /// gating and mana-cost scaling. Native def levels are floored by buff
    /// "AllPaths" Set effects (e.g. god mode pinning every path to 9). Higher
    /// native levels win over the buff floor so investing further in a path
    /// isn't wasted while the buff is up.</summary>
    private static Func<MagicPath, int> ResolveCasterLevel(UnitDef? def, UnitArrays units, int necroIdx)
        => p => BuffSystem.EffectivePathLevel(units, necroIdx, def, p);
}
