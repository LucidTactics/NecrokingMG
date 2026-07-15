using System;
using System.Collections.Generic;
using Necroking.Core;
using Necroking.Data;
using Necroking.Data.Registries;
using Necroking.Game;
using Necroking.Lib;
using Necroking.Movement;

namespace Necroking.GameSystems;

public enum CastResult { Success, NotEnoughMana, OnCooldown, NoValidTarget, OutOfRange, NoNecromancer, HordeCapFull, MissingPath, SummonLocked }

public class PendingSpellCast
{
    public string SpellID = "";
    public string Category = "Projectile";
    public Vec2 TargetPos;
    public int TargetCorpseIdx = -1;
    // Stable CorpseID of the targeted corpse (-1 = none). Re-resolved to an index at
    // execution so a channel-time _corpses compaction can't rebind a stale index.
    public int TargetCorpseID = -1;
    public int TargetUnitIdx = -1;
    public uint TargetUnitID = GameConstants.InvalidUnit;
    public string SummonUnitID = "";
    public int RemainingProjectiles;
    public float ProjectileTimer;
    public string CastingBuffID = "";
    public bool Active;
}

/// <summary>An AI unit asking to cast a spell through the SAME pipeline as the player
/// (SpellCaster.TryStartSpellCast → SpellEffectSystem.Execute). Handlers enqueue these
/// during the sim's AI pass (AIContext.SpellCasts); Game1.DrainAISpellCasts validates,
/// pays, and executes them right after the tick — the effect layer (reanim FX, summon
/// effects, death fog) lives on Game1, which is why the sim can't execute them itself.
/// The queue is cleared at the start of every Simulation.Tick, so requests never pile
/// up in headless runs where nothing drains them (headless casters simply don't cast).</summary>
public struct AISpellCastRequest
{
    public uint CasterId;     // stable unit id — re-resolved at drain time
    public string SpellID;
    public Vec2 Target;       // world-space aim point (the AI's "mouse")
}

public static class SpellCaster
{
    /// <summary>
    /// Check if a corpse has a valid zombieTypeID on its UnitDef.
    /// </summary>
    private static bool CorpseHasZombieType(Corpse corpse, GameData gameData)
    {
        if (gameData == null) return true; // no validation possible, allow it
        var def = gameData.Units.Get(corpse.UnitDefID);
        return def != null && !string.IsNullOrEmpty(def.ZombieTypeID);
    }

    /// <summary>Targeting + gates for ANY caster — the player necromancer (pass
    /// <see cref="NecromancerState"/>) or an AI caster unit (pass a
    /// <see cref="UnitCasterResources"/> over its unit fields). "Ally"/"enemy" in the
    /// per-category targeting means same/different faction as the caster, so a Human
    /// priest's Debuff finds undead and its Buff finds fellow humans. Horde-cap
    /// gating only applies to the player (the caps live on NecromancerState).
    /// <paramref name="targetWorld"/> is the aim point — the mouse for the player,
    /// the AI's chosen cast point for units.</summary>
    public static CastResult TryStartSpellCast(
        string spellID, SpellRegistry spells, ICasterResources caster,
        UnitArrays units, int casterIdx, Vec2 targetWorld,
        IReadOnlyList<Corpse> corpses, PendingSpellCast outPending,
        GameData gameData = null)
    {
        if (casterIdx < 0) return CastResult.NoNecromancer;

        var spell = spells.Get(spellID);
        if (spell == null) return CastResult.NoValidTarget;

        if (caster.GetCooldown(spellID) > 0f) return CastResult.OnCooldown;
        // Path-aware cost: caster's UnitDef carries Paths; levels above the
        // spell's primary requirement only reduce the cost through the spell's
        // own masteryBonuses lines (fatigue -N% / free) — no blanket discount.
        // Secondary path is a hard gate only. Buff "AllPaths" Set effects floor
        // every path level (e.g. god mode = 9 everywhere) without overwriting
        // higher native levels.
        var casterDef = gameData.Units.Get(units[casterIdx].UnitDefID);
        Func<MagicPath, int> casterLevel = ResolveCasterLevel(casterDef, units, casterIdx);
        // Path gate is a DISTINCT failure from mana — don't conflate them, or the
        // feedback lies ("not enough mana" when the caster simply lacks the path).
        if (!spell.MeetsPathRequirements(casterLevel)) return CastResult.MissingPath;
        float effectiveCost = spell.EffectiveManaCost(casterLevel);
        if (caster.Mana < effectiveCost) return CastResult.NotEnoughMana;

        // Mastery (levels above the primary requirement) can stretch the spell's
        // targeting range/area via its masteryBonuses lines. All the per-category
        // range gates below use these scaled values, never range directly.
        int mastery = spell.MasteryLevels(casterLevel);
        float range = spell.ScaledRange(mastery);
        float aoeRadius = spell.ScaledAoeRadius(mastery);

        var casterPos = units[casterIdx].Position;
        var casterFaction = units[casterIdx].Faction;

        // Reset pending state
        outPending.SpellID = spellID;
        outPending.Category = spell.Category;
        outPending.TargetPos = targetWorld;
        outPending.TargetCorpseIdx = -1;
        outPending.TargetCorpseID = -1;
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
                float dist = (targetWorld - casterPos).Length();
                if (dist > range) return CastResult.OutOfRange;
                outPending.TargetPos = targetWorld;
                outPending.RemainingProjectiles = spell.Quantity;
                outPending.ProjectileTimer = 0f;
                break;
            }

            case "Buff":
            case "Debuff":
            {
                outPending.TargetPos = targetWorld;
                if (spell.AoeType != "AOE")
                {
                    // Single target: find closest valid unit near mouse within spell range
                    float bestDist = float.MaxValue;
                    int bestUnit = -1;
                    for (int i = 0; i < units.Count; i++)
                    {
                        if (!units[i].Alive) continue;
                        float distToCaster = (units[i].Position - casterPos).Length();
                        if (distToCaster > range) continue;
                        if (spell.Category == "Buff" && units[i].Faction != casterFaction) continue;
                        if (spell.Category == "Debuff" && units[i].Faction == casterFaction) continue;

                        float distToCursor = (units[i].Position - targetWorld).Length();
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
                    // Skill-tree raise gate (player only): a corpse whose zombie type
                    // the player hasn't unlocked (unlock_summon) isn't a valid target.
                    // God mode bypasses. AI casters and fixed-summon spells skip this.
                    SkillBookState? raiseBook = null;
                    bool raiseGod = false;
                    if (needsZombieType && caster is NecromancerState)
                    {
                        raiseBook = Necroking.Game1.Instance?._skillBookState;
                        raiseGod = raiseBook != null && BuffSystem.HasBuff(units, casterIdx, "buff_god_mode");
                    }
                    bool sawLockedCorpse = false;
                    float bestDist = float.MaxValue;
                    int bestCorpse = -1;
                    for (int i = 0; i < corpses.Count; i++)
                    {
                        if (corpses[i].Dissolving || corpses[i].ConsumedBySummon) continue;
                        if (corpses[i].DraggedByUnitID != GameConstants.InvalidUnit) continue;
                        if (corpses[i].BaggedByUnitID != GameConstants.InvalidUnit) continue;
                        if (needsZombieType && !CorpseHasZombieType(corpses[i], gameData)) continue;
                        float distToCaster = (corpses[i].Position - casterPos).Length();
                        if (distToCaster > range) continue;
                        if (raiseBook != null && !TableCraftingSystem.IsRaiseUnlocked(
                                gameData, raiseBook, corpses[i].UnitDefID, raiseGod))
                        {
                            // In-range but locked — remember so the failure reads as
                            // "not studied" instead of a generic "no target".
                            sawLockedCorpse = true;
                            continue;
                        }
                        float distToCursor = (corpses[i].Position - targetWorld).Length();
                        if (distToCursor < bestDist)
                        {
                            bestDist = distToCursor;
                            bestCorpse = i;
                        }
                    }
                    if (bestCorpse < 0)
                        return sawLockedCorpse ? CastResult.SummonLocked : CastResult.NoValidTarget;
                    outPending.TargetCorpseIdx = bestCorpse;
                    outPending.TargetCorpseID = corpses[bestCorpse].CorpseID;
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
                        if (units[i].Faction != casterFaction) continue;
                        float distToCaster = (units[i].Position - casterPos).Length();
                        if (distToCaster > range) continue;

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

                        float distToCursor = (units[i].Position - targetWorld).Length();
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
                    float dist = (targetWorld - casterPos).Length();
                    if (dist > range) return CastResult.OutOfRange;

                    bool foundValid = false;
                    for (int i = 0; i < corpses.Count; i++)
                    {
                        if (corpses[i].Dissolving || corpses[i].ConsumedBySummon) continue;
                        if (corpses[i].DraggedByUnitID != GameConstants.InvalidUnit) continue;
                        if (corpses[i].BaggedByUnitID != GameConstants.InvalidUnit) continue;
                        float distToTarget = (corpses[i].Position - targetWorld).Length();
                        if (distToTarget > aoeRadius) continue;
                        if (!CorpseHasZombieType(corpses[i], gameData)) continue;
                        foundValid = true;
                        break;
                    }
                    if (!foundValid) return CastResult.NoValidTarget;
                    outPending.TargetPos = targetWorld;
                }
                else
                {
                    // SummonTargetReq == "None" — just mana + cooldown
                    outPending.SummonUnitID = spell.SummonUnitID;
                    if (spell.SpawnLocation == "AtTargetLocation" ||
                        spell.SpawnLocation == "NearestTargetToMouse")
                    {
                        float dist = (targetWorld - casterPos).Length();
                        if (range > 0 && dist > range) return CastResult.OutOfRange;
                        outPending.TargetPos = targetWorld;
                    }
                    else
                    {
                        outPending.TargetPos = casterPos; // spawn near caster
                    }
                }

                // Horde-cap pre-check. Refuses the cast outright (no mana spent,
                // no cooldown) when the target category is full. Skipped for
                // Transform mode — it replaces an existing unit, doesn't grow
                // the army — and for CorpseAOE-without-a-fixed-summonUnitID,
                // since per-corpse category resolution happens at execute time
                // (different corpses may produce different categories).
                // Player-only: the caps live on NecromancerState — AI casters'
                // summons aren't part of the player's horde and aren't capped.
                if (spell.SummonMode != "Transform" && caster is NecromancerState hordeState)
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
                            && HordeCapTracker.Available(units, gameData, hordeState, cat) <= 0)
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
                        if (units[i].Faction == casterFaction) continue;
                        float distToCaster = (units[i].Position - casterPos).Length();
                        if (distToCaster > range) continue;
                        float distToCursor = (units[i].Position - targetWorld).Length();
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
                    float dist = (targetWorld - casterPos).Length();
                    if (dist > range) return CastResult.OutOfRange;
                    outPending.TargetPos = targetWorld;
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
                    if (units[i].Faction == casterFaction) continue;
                    float distToCaster = (units[i].Position - casterPos).Length();
                    if (distToCaster > range) continue;
                    float distToCursor = (units[i].Position - targetWorld).Length();
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
                        if (units[i].Faction != casterFaction) continue;
                        if (i == casterIdx) continue;
                    }
                    else
                    {
                        if (units[i].Faction == casterFaction) continue;
                    }
                    float distToCaster = (units[i].Position - casterPos).Length();
                    if (distToCaster > range) continue;
                    float distToCursor = (units[i].Position - targetWorld).Length();
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
                        float distToCaster = (corpses[i].Position - casterPos).Length();
                        if (distToCaster > range) continue;
                        float distToCursor = (corpses[i].Position - targetWorld).Length();
                        if (distToCursor < bestCorpseDist)
                        {
                            bestCorpseDist = distToCursor;
                            bestCorpse = i;
                        }
                    }
                    if (bestCorpse < 0) return CastResult.NoValidTarget;
                    outPending.TargetCorpseIdx = bestCorpse;
                    outPending.TargetCorpseID = corpses[bestCorpse].CorpseID;
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
                    if (i == casterIdx) continue;
                    if (units[i].Faction != casterFaction) continue;
                    if (restrict && !spell.AcceptableTargets!.Contains(units[i].UnitDefID)) continue;
                    float distToCaster = (units[i].Position - casterPos).Length();
                    if (distToCaster > range) continue;
                    float distToCursor = (units[i].Position - targetWorld).Length();
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
                float dist = (targetWorld - casterPos).Length();
                if (dist > range) return CastResult.OutOfRange;
                outPending.TargetPos = targetWorld;
                break;
            }

            case "Toggle":
            {
                outPending.TargetPos = casterPos;
                break;
            }

            default:
            {
                float dist = (targetWorld - casterPos).Length();
                if (dist > range) return CastResult.OutOfRange;
                outPending.TargetPos = targetWorld;
                break;
            }
        }

        // Deduct mana and start cooldown. Recompute the effective cost rather
        // than capturing it from the eligibility check so we stay in sync if a
        // future refactor moves the gate around.
        var castingDef = gameData.Units.Get(units[casterIdx].UnitDefID);
        Func<MagicPath, int> castingLevel = ResolveCasterLevel(castingDef, units, casterIdx);
        caster.Mana -= spell.EffectiveManaCost(castingLevel);
        if (spell.Cooldown > 0f)
            caster.SetCooldown(spellID, spell.Cooldown);

        // Floating spell-name label above the necromancer — only on confirmed
        // cast, never on a failed validation, or the label lies (says "Fireball"
        // when nothing fired). See UnitData.ActionLabel.
        units[casterIdx].ActionLabel = gameData.Spells.NameOf(spellID);
        units[casterIdx].ActionLabelTimer = 1.5f;

        return CastResult.Success;
    }

    /// <summary>Refund a cast whose mana/cooldown were committed at press time but
    /// whose effect never fired — a hard interrupt (knockdown/knockback) cancelled
    /// the pending cast mid-wind-up. Recomputes the effective mana cost the same
    /// way the deduction did so the two always match. Mana may transiently exceed
    /// max (regen ticked during the wind-up); the per-frame clamp in Game1 corrects
    /// it next tick.</summary>
    public static void RefundSpellCast(string spellID, SpellRegistry spells, ICasterResources caster,
        UnitArrays units, int casterIdx, GameData gameData)
    {
        var spell = spells.Get(spellID);
        if (spell == null || casterIdx < 0 || casterIdx >= units.Count) return;
        var castingDef = gameData.Units.Get(units[casterIdx].UnitDefID);
        Func<MagicPath, int> castingLevel = ResolveCasterLevel(castingDef, units, casterIdx);
        caster.Mana += spell.EffectiveManaCost(castingLevel);
        caster.ClearCooldown(spellID);
    }

    /// <summary>True if the necromancer meets a spell's magic-path requirements
    /// (primary + secondary path/level). PATH GATING ONLY — deliberately does
    /// NOT check mana, cooldown, or targeting: "do I have this spell" is about
    /// unlocked paths, not current resources (players don't expect a spell to
    /// vanish from the book just because they're low on mana). Used to filter
    /// the grimoire so spells whose paths the player hasn't unlocked don't show.
    /// casterIdx &lt; 0 (no caster context, e.g. editor/tests) => true (show all).</summary>
    public static bool HasSpellRequirements(SpellDef? spell, GameData gameData,
        UnitArrays units, int casterIdx)
    {
        if (spell == null) return false;
        if (casterIdx < 0) return true;
        var def = gameData.Units.Get(units[casterIdx].UnitDefID);
        return spell.MeetsPathRequirements(ResolveCasterLevel(def, units, casterIdx));
    }

    /// <summary>Human-readable description of which path requirement(s) the caster
    /// fails for a spell — e.g. "Death 1 (have 0)" or "Death 1 (have 0), Body 2
    /// (have 1)". Returns "" when the caster meets the requirements (or there are
    /// none). Powers the cast-failure feedback so a <see cref="CastResult.MissingPath"/>
    /// names the path the player still needs, instead of misreporting it as a mana
    /// shortfall.</summary>
    public static string DescribeMissingPath(SpellDef? spell, GameData gameData,
        UnitArrays units, int casterIdx)
    {
        if (spell == null || casterIdx < 0) return "";
        var def = gameData.Units.Get(units[casterIdx].UnitDefID);
        var casterLevel = ResolveCasterLevel(def, units, casterIdx);

        var parts = new List<string>();
        var pri = spell.GetPrimary();
        if (pri.HasRequirement && casterLevel(pri.Path) < pri.Level)
            parts.Add($"{pri.Path} {pri.Level} (have {casterLevel(pri.Path)})");
        var sec = spell.GetSecondary();
        if (sec.HasRequirement && casterLevel(sec.Path) < sec.Level)
            parts.Add($"{sec.Path} {sec.Level} (have {casterLevel(sec.Path)})");
        return string.Join(", ", parts);
    }

    /// <summary>Build the magic-path level lookup used for both requirement
    /// gating and mana-cost scaling. Native def levels are floored by buff
    /// "AllPaths" Set effects (e.g. god mode pinning every path to 9). Higher
    /// native levels win over the buff floor so investing further in a path
    /// isn't wasted while the buff is up.</summary>
    /// <summary>The caster's effective level per magic path (native def level +
    /// buff bonuses). The one level source behind the cast gate, cost scaling,
    /// and the HUD's castability cues — reuse it, don't re-derive.</summary>
    public static Func<MagicPath, int> ResolveCasterLevel(UnitDef def, UnitArrays units, int casterIdx)
        => p => BuffSystem.EffectivePathLevel(units, casterIdx, def, p);
}
