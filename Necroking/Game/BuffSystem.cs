using System;
using System.Collections.Generic;
using Necroking.Data;
using Necroking.Data.Registries;
using Necroking.Movement;

namespace Necroking.GameSystems;

public static class BuffSystem
{
    public static void ApplyBuff(UnitArrays units, int unitIdx, BuffDef def)
    {
        if (unitIdx < 0 || unitIdx >= units.Count) return;
        var buffs = units.ActiveBuffs[unitIdx];

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

    public static void TickBuffs(UnitArrays units, float dt)
    {
        for (int i = 0; i < units.Count; i++)
        {
            if (!units.Alive[i]) continue;
            var buffs = units.ActiveBuffs[i];
            for (int j = buffs.Count - 1; j >= 0; j--)
            {
                var b = buffs[j];
                if (b.Permanent) continue;
                b.RemainingDuration -= dt;
                if (b.RemainingDuration <= 0f)
                    buffs.RemoveAt(j);
                else
                    buffs[j] = b;
            }
        }
    }

    public static void RemoveBuffStack(UnitArrays units, int unitIdx, string buffDefID)
    {
        if (unitIdx < 0 || unitIdx >= units.Count) return;
        var buffs = units.ActiveBuffs[unitIdx];
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

        foreach (var buff in units.ActiveBuffs[unitIdx])
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
}
