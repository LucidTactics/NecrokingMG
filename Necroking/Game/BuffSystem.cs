using System;
using System.Collections.Generic;
using Necroking.Core;
using Necroking.Data;
using Necroking.Data.Registries;
using Necroking.Movement;
using Necroking.Render;

namespace Necroking.GameSystems;

public static class BuffSystem
{
    public static void ApplyBuff(UnitArrays units, int unitIdx, BuffDef def)
    {
        if (unitIdx < 0 || unitIdx >= units.Count) return;
        var buffs = units[unitIdx].ActiveBuffs;

        // Check for existing stack
        for (int i = 0; i < buffs.Count; i++)
        {
            if (buffs[i].BuffDefID == def.Id)
            {
                var b = buffs[i];
                if (b.StackCount < def.MaxStacks)
                    b.StackCount++;
                b.RemainingDuration = def.Duration;
                buffs[i] = b;
                return;
            }
        }

        // New buff
        buffs.Add(new ActiveBuff
        {
            BuffDefID = def.Id,
            RemainingDuration = def.Duration,
            Effects = def.Effects,
            StackCount = 1
        });
    }

    public const string KnockdownBuffID = "buff_knockdown";
    private const float StandupAnimTime = 0.8f; // Start standup this long before buff expires

    public static void TickBuffs(UnitArrays units, float dt)
    {
        for (int i = 0; i < units.Count; i++)
        {
            if (!units[i].Alive) continue;
            var buffs = units[i].ActiveBuffs;
            for (int j = buffs.Count - 1; j >= 0; j--)
            {
                var b = buffs[j];
                if (b.Permanent) continue;
                b.RemainingDuration -= dt;
                if (b.RemainingDuration <= 0f)
                {
                    // Knockdown: standup already started early (below), nothing to do on expiry
                    buffs.RemoveAt(j);
                }
                else
                {
                    // Knockdown: start standup animation early so it finishes as buff expires.
                    // StandupTimer extends past buff expiry to keep AI/movement blocked
                    // until the standup animation actually completes.
                    if (b.BuffDefID == KnockdownBuffID
                        && b.RemainingDuration <= StandupAnimTime
                        && units[i].StandupTimer <= 0f)
                    {
                        // Timer = anim duration, not remaining buff time.
                        // This ensures StandupTimer outlasts the buff and blocks AI
                        // until standup visually completes.
                        units[i].StandupTimer = StandupAnimTime + 0.1f;
                        units[i].OverrideAnim = AnimRequest.Combat(AnimState.Standup);
                        units[i].OverrideStarted = false;
                    }
                    buffs[j] = b;
                }
            }
        }
    }

    /// <summary>Check if a unit has the knockdown buff active.</summary>
    public static bool IsKnockedDown(Unit unit)
    {
        for (int i = 0; i < unit.ActiveBuffs.Count; i++)
            if (unit.ActiveBuffs[i].BuffDefID == KnockdownBuffID) return true;
        return false;
    }

    public static void RemoveBuffStack(UnitArrays units, int unitIdx, string buffDefID)
    {
        if (unitIdx < 0 || unitIdx >= units.Count) return;
        var buffs = units[unitIdx].ActiveBuffs;
        for (int i = 0; i < buffs.Count; i++)
        {
            if (buffs[i].BuffDefID == buffDefID)
            {
                var b = buffs[i];
                b.StackCount--;
                if (b.StackCount <= 0)
                    buffs.RemoveAt(i);
                else
                    buffs[i] = b;
                return;
            }
        }
    }

    public static float GetModifiedStat(UnitArrays units, int unitIdx, BuffStat stat, float baseValue)
    {
        if (unitIdx < 0 || unitIdx >= units.Count) return baseValue;
        float additive = 0f;
        float multiplicative = 1f;
        float? setValue = null;

        foreach (var buff in units[unitIdx].ActiveBuffs)
        {
            foreach (var eff in buff.Effects)
            {
                if (eff.Stat != stat.ToString()) continue;
                switch (eff.Type)
                {
                    case "Add": additive += eff.Value * buff.StackCount; break;
                    case "Multiply": multiplicative *= MathF.Pow(eff.Value, buff.StackCount); break;
                    case "Set": setValue = eff.Value; break;
                }
            }
        }

        return setValue ?? (baseValue + additive) * multiplicative;
    }

    /// <summary>
    /// Apply buff with combat log output showing stat changes.
    /// </summary>
    public static void ApplyBuffLogged(UnitArrays units, int unitIdx, BuffDef def, string unitName)
    {
        if (unitIdx < 0 || unitIdx >= units.Count) return;

        // Log the application
        var buffs = units[unitIdx].ActiveBuffs;
        bool stacking = false;
        for (int i = 0; i < buffs.Count; i++)
        {
            if (buffs[i].BuffDefID == def.Id) { stacking = true; break; }
        }

        if (stacking)
            DebugLog.Log("combat", $"         Buff '{def.Id}' stacked on {unitName}");
        else
            DebugLog.Log("combat", $"         Buff '{def.Id}' applied to {unitName}");

        // Log stat changes
        foreach (var eff in def.Effects)
            DebugLog.Log("combat", $"           {eff.Type} {eff.Stat} {eff.Value:+0.##;-0.##;0}");

        ApplyBuff(units, unitIdx, def);
    }
}
