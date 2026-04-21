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
        => ApplyBuffWithDuration(units, unitIdx, def, def.Duration);

    /// <summary>
    /// Apply a buff with an override duration (instead of the def's default).
    /// Used where duration is computed at runtime — e.g. knockdown-on-hit, where
    /// duration comes from the STR/Size/DRN roll difference.
    /// </summary>
    public static void ApplyBuffWithDuration(UnitArrays units, int unitIdx, BuffDef def, float durationSeconds)
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
                b.RemainingDuration = durationSeconds;
                buffs[i] = b;
                return;
            }
        }

        // New buff
        buffs.Add(new ActiveBuff
        {
            BuffDefID = def.Id,
            RemainingDuration = durationSeconds,
            Effects = def.Effects,
            StackCount = 1
        });

        // If this buff is incapacitating, set up the incap state
        if (def.Incapacitating)
        {
            // Parse the buff's requested hold/recover anim states. Enum.TryParse succeeds
            // on any string matching an enum name — it does NOT verify that this unit's
            // sprite actually authored that animation. A missing sprite anim silently
            // falls back to Idle at render time, making "incapacitated" units look fine.
            // We don't have the sprite data here (that's on the AnimController, which is
            // owned by Game1 via _unitAnims), so we can only sanity-check the parse.
            if (!Enum.TryParse<AnimState>(def.IncapHoldAnim, out var holdAnim))
            {
                Necroking.Core.DebugLog.Log("asset",
                    $"[BuffSystem] Buff '{def.Id}' IncapHoldAnim='{def.IncapHoldAnim}' does not match any AnimState — falling back to Idle");
                holdAnim = AnimState.Idle;
            }
            if (!Enum.TryParse<AnimState>(def.IncapRecoverAnim, out var recoverAnim))
            {
                Necroking.Core.DebugLog.Log("asset",
                    $"[BuffSystem] Buff '{def.Id}' IncapRecoverAnim='{def.IncapRecoverAnim}' does not match any AnimState — falling back to Idle");
                recoverAnim = AnimState.Idle;
            }
            units[unitIdx].Incap = new IncapState
            {
                Active = true,
                HoldAnim = holdAnim,
                RecoverAnim = recoverAnim,
                RecoverTime = def.IncapRecoverTime,
                // Sentinel -1 tells AnimResolver to initialize from real anim duration
                // when recovery actually starts. Using 0 here would bypass that path
                // (the "only init if < 0" guard) and cause instant recovery.
                RecoverTimer = -1f,
                Recovering = false,
                HoldAtEnd = def.IncapHoldAtEnd,
            };

            // Set the hold animation as a forced override
            AnimResolver.SetOverride(units[unitIdx], new AnimRequest
            {
                State = holdAnim, Priority = 3, Interrupt = true,
                Duration = -1, PlaybackSpeed = 1f
            });
        }
    }

    public static void TickBuffs(UnitArrays units, float dt, BuffRegistry? buffRegistry = null)
    {
        for (int i = 0; i < units.Count; i++)
        {
            if (!units[i].Alive) continue;

            // Tick recovery phase (after incap buff expires)
            if (units[i].Incap.Recovering && units[i].Incap.RecoverTimer > 0f)
            {
                var incap = units[i].Incap;
                incap.RecoverTimer -= dt;
                if (incap.RecoverTimer <= 0f)
                    incap = default; // Recovery complete — unit is free
                units[i].Incap = incap;
            }
            // RecoverTimer == -1 means waiting for animation system to set real duration

            var buffs = units[i].ActiveBuffs;
            for (int j = buffs.Count - 1; j >= 0; j--)
            {
                var b = buffs[j];
                if (b.Permanent) continue;
                b.RemainingDuration -= dt;

                if (b.RemainingDuration <= 0f)
                {
                    buffs.RemoveAt(j);
                    continue;
                }

                // Incapacitating buffs: start recovery animation early so it finishes as buff expires
                if (units[i].Incap.Active && !units[i].Incap.Recovering && buffRegistry != null)
                {
                    var def = buffRegistry.Get(b.BuffDefID);
                    if (def != null && def.Incapacitating && b.RemainingDuration <= def.IncapRecoverTime)
                    {
                        // Begin recovery phase — animation plays while buff is still active.
                        // RecoverTimer set to -1 as signal for animation system to fill in
                        // the real duration from the unit's actual sprite animation timing.
                        var incap = units[i].Incap;
                        incap.Recovering = true;
                        incap.RecoverTimer = -1f; // Animation system will set real duration
                        units[i].Incap = incap;

                        // Switch override to recovery animation. Forced (priority 3)
                        // because the hold anim is also Forced — a Combat (priority 2)
                        // override can't replace it, which would leave the unit stuck
                        // playing Knockdown forever after Incap cleanly finishes.
                        Enum.TryParse<AnimState>(def.IncapRecoverAnim, out var recoverAnim);
                        AnimResolver.SetOverride(units[i], AnimRequest.Forced(recoverAnim));
                    }
                }

                buffs[j] = b;
            }

            // If incap is active but the buff was removed (expired this frame), start recovery
            if (units[i].Incap.Active && !units[i].Incap.Recovering)
            {
                bool buffStillActive = false;
                for (int j = 0; j < buffs.Count; j++)
                {
                    if (buffRegistry != null)
                    {
                        var def = buffRegistry.Get(buffs[j].BuffDefID);
                        if (def != null && def.Incapacitating) { buffStillActive = true; break; }
                    }
                }
                if (!buffStillActive)
                {
                    // Buff expired without early recovery trigger — immediate recovery
                    var incap = units[i].Incap;
                    incap.Recovering = true;
                    incap.RecoverTimer = -1f; // Animation system will set real duration
                    units[i].Incap = incap;

                    // Forced so it can replace the Priority-3 Knockdown hold override.
                    AnimResolver.SetOverride(units[i], AnimRequest.Forced(incap.RecoverAnim));
                }
            }
        }
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

    public static void ApplyBuffLogged(UnitArrays units, int unitIdx, BuffDef def, string unitName)
    {
        if (unitIdx < 0 || unitIdx >= units.Count) return;

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

        foreach (var eff in def.Effects)
            DebugLog.Log("combat", $"           {eff.Type} {eff.Stat} {eff.Value:+0.##;-0.##;0}");

        ApplyBuff(units, unitIdx, def);
    }
}
