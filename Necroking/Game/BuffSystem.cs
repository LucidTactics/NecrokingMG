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
    public static void ApplyBuff(UnitArrays units, int unitIdx, BuffDef def, GameData gameData = null)
        => ApplyBuffWithDuration(units, unitIdx, def, def.Duration, gameData);

    /// <summary>
    /// Apply a buff by ID with null-guard and registry lookup. Returns the resolved BuffDef
    /// if successfully applied, or null if the buffId is empty or not found. Returns null
    /// for invalid unit indices. The returned def is useful for callers that need to examine
    /// buff properties (e.g., ApplyFrenzy needs to mark the buff as Permanent).
    /// </summary>
    public static BuffDef? ApplyBuffById(UnitArrays units, int unitIdx, BuffRegistry buffs, string? buffId)
    {
        if (string.IsNullOrEmpty(buffId)) return null;
        var def = buffs.Get(buffId);
        if (def != null) ApplyBuff(units, unitIdx, def);
        return def;
    }

    /// <summary>
    /// Apply a buff with an override duration (instead of the def's default).
    /// Used where duration is computed at runtime — e.g. knockdown-on-hit, where
    /// duration comes from the STR/Size/DRN roll difference.
    /// </summary>
    public static void ApplyBuffWithDuration(UnitArrays units, int unitIdx, BuffDef def, float durationSeconds, GameData gameData = null)
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
                {
                    b.StackCount++;
                    GrantMaxHpDelta(units, unitIdx, def); // one more stack of +MaxHP → real HP
                }
                b.RemainingDuration = durationSeconds;
                buffs[i] = b;
                return;
            }
        }

        // First-time apply for this buff on this unit: layer in any weapons the
        // buff grants. Skipped on stack increments (above) so a single intrinsic
        // weapon doesn't multiply across stacks.
        if (def.GrantedWeapons.Count > 0 && gameData != null)
            ApplyGrantedWeapons(units, unitIdx, def, gameData);

        // New buff. A duration of 0 (or below) at apply time means "permanent
        // until explicitly removed" — used for toggles like god mode. The
        // tick-down path in TickBuffs respects ActiveBuff.Permanent so the
        // buff never expires on its own. Buffs with a positive duration that
        // tick to 0 follow the normal removal path.
        buffs.Add(new ActiveBuff
        {
            BuffDefID = def.Id,
            RemainingDuration = durationSeconds,
            Effects = def.Effects,
            StackCount = 1,
            Permanent = durationSeconds <= 0f,
        });

        // A +MaxHP buff is a REAL, larger health pool: grant the added MaxHP as
        // current HP on first apply so the unit actually gets tougher. It used to
        // be cosmetic — the max read bigger in the HUD but HP and combat used the
        // base. EffectiveMaxHP keeps every consumer consistent with the grant.
        GrantMaxHpDelta(units, unitIdx, def);

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

            // Set the hold animation as a forced permanent hold. AnimRequest.Hold
            // expresses "stay until replaced"; the buff owns the lifetime and will
            // call SetOverride(Forced(RecoverAnim)) when it's time to exit.
            AnimResolver.SetOverride(units[unitIdx], AnimRequest.Hold(holdAnim, priority: 3));
        }
    }

    /// <summary>Begin a reanimated unit's slow "rise from the dead" standup. Reuses the
    /// Incap recovery lock (while IsLocked the unit can't move, turn, run AI, or attack —
    /// see the Incap.IsLocked gates in Simulation) and plays the Standup anim at
    /// <paramref name="playbackSpeed"/> (0.5 = half speed). Entered directly in the
    /// recovery phase — there is no preceding hold/incap. The recover timer is filled by
    /// AnimResolver from the real Standup clip length ÷ speed, so the lock lasts exactly
    /// as long as the slowed animation. Falls back gracefully (Standup→Idle) for any unit
    /// whose sprite lacks a Standup clip.</summary>
    public static void BeginReanimationRise(UnitArrays units, int idx, float playbackSpeed = 0.5f)
    {
        if (idx < 0 || idx >= units.Count) return;
        units[idx].Incap = new IncapState
        {
            Active = false,
            Recovering = true,
            HoldAnim = AnimState.Standup,
            RecoverAnim = AnimState.Standup,
            RecoverPlaybackSpeed = playbackSpeed,
            RecoverTime = 1.5f,    // fallback only if the Standup clip duration is unavailable
            RecoverTimer = -1f,    // AnimResolver fills the real (clip ÷ speed) duration
            HoldAtEnd = false,
        };
        AnimResolver.SetOverride(units[idx], AnimRequest.Forced(AnimState.Standup, playbackSpeed));
    }

    /// <summary>The unit's TRUE maximum HP including +MaxHP buff effects. Combat
    /// (limb damage cap, limb-sever threshold, morale) and HP-bar rendering read
    /// through this so a +MaxHP buff is a real, larger pool, not a cosmetic HUD
    /// number. Single source of truth shared by all consumers.</summary>
    public static int EffectiveMaxHP(UnitArrays units, int unitIdx)
        => (int)GetModifiedStat(units, unitIdx, BuffStat.MaxHP, units[unitIdx].Stats.MaxHP);

    /// <summary>Raise current HP by the MaxHP a buff (one stack) adds, so applying
    /// a +MaxHP buff grants that HP for real. Only "Add MaxHP" effects count (the
    /// only kind in use); no-op otherwise. HP stays ≤ the new effective max
    /// because it was already ≤ the old max before this stack was added.</summary>
    private static void GrantMaxHpDelta(UnitArrays units, int unitIdx, BuffDef def)
    {
        int add = 0;
        foreach (var eff in def.Effects)
            if (eff.Type == "Add" && eff.Stat == nameof(BuffStat.MaxHP)) add += (int)eff.Value;
        if (add <= 0) return;
        var s = units[unitIdx].Stats;
        s.HP += add;
        units[unitIdx].Stats = s;
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
                    string expiringId = b.BuffDefID;
                    buffs.RemoveAt(j);
                    StripGrantedWeapons(units, i, expiringId);
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
                {
                    buffs.RemoveAt(i);
                    StripGrantedWeapons(units, unitIdx, buffDefID);
                }
                else
                    buffs[i] = b;
                return;
            }
        }
    }

    /// <summary>Remove every stack of the named buff in one call. Returns true
    /// if anything was actually removed. Used by toggle-style buffs (e.g. god
    /// mode) where partial removal doesn't make sense.</summary>
    public static bool RemoveBuff(UnitArrays units, int unitIdx, string buffDefID)
    {
        if (unitIdx < 0 || unitIdx >= units.Count) return false;
        var buffs = units[unitIdx].ActiveBuffs;
        bool removed = false;
        for (int i = buffs.Count - 1; i >= 0; i--)
        {
            if (buffs[i].BuffDefID == buffDefID)
            {
                buffs.RemoveAt(i);
                removed = true;
            }
        }
        if (removed) StripGrantedWeapons(units, unitIdx, buffDefID);
        return removed;
    }

    /// <summary>Append the weapons in <paramref name="def"/>.GrantedWeapons to the
    /// unit's effective weapon list. Each pushed WeaponStats is tagged with
    /// SourceBuffID = def.Id so removal can scrub exactly the entries this buff
    /// contributed (without touching base-equipment slots that happen to share
    /// an ID). Caller is responsible for skipping this on stack increments —
    /// granted weapons don't multiply across stacks.</summary>
    private static void ApplyGrantedWeapons(UnitArrays units, int unitIdx, BuffDef def, GameData gameData)
    {
        var stats = units[unitIdx].Stats;
        bool anyMelee = false, anyRanged = false;
        foreach (var weaponId in def.GrantedWeapons)
        {
            var w = gameData.Weapons.Get(weaponId);
            if (w == null)
            {
                Necroking.Core.DebugLog.Log("skillbook", $"[BuffSystem] buff '{def.Id}' grants weapon '{weaponId}' that isn't in WeaponRegistry — skipped.");
                continue;
            }

            var ws = new WeaponStats
            {
                Damage = w.Damage,
                AttackBonus = w.AttackBonus,
                DefenseBonus = w.DefenseBonus,
                Length = w.Length,
                Name = w.DisplayName,
                DamageTypeOverride = WeaponClassifier.ParseDamageType(w.DamageType),
                TwoHandedOverride = w.TwoHanded,
                IsRanged = w.IsRanged,
                AnimName = w.AnimName,
                CooldownRounds = w.CooldownRounds,
                Priority = w.Priority,
                PounceMinRange = w.PounceMinRange,
                PounceMaxRange = w.PounceMaxRange,
                PounceArcPeak = w.PounceArcPeak,
                PounceAirSpeed = w.PounceAirSpeed,
                SweepArcDegrees = w.SweepArcDegrees,
                SweepRadius = w.SweepRadius,
                SweepHitsAllies = w.SweepHitsAllies,
                TrampleMinRange = w.TrampleMinRange,
                TrampleMaxRange = w.TrampleMaxRange,
                TrampleMaxChaseDistance = w.TrampleMaxChaseDistance,
                TrampleImpactRange = w.TrampleImpactRange,
                TrampleSpeedBonus = w.TrampleSpeedBonus,
                TrampleRadius = w.TrampleRadius,
                TrampleKnockbackForce = w.TrampleKnockbackForce,
                TrampleImpactForce = w.TrampleImpactForce,
                SourceBuffID = def.Id,
            };
            if (System.Enum.TryParse<WeaponArchetype>(w.Archetype, true, out var arch))
                ws.Archetype = arch;
            foreach (var b in w.Bonuses)
            {
                if (b == "ArmorPiercing") ws.HasArmorPiercing = true;
                if (b == "ArmorNegating") ws.HasArmorNegating = true;
                if (b == "Knockdown")     ws.HasKnockdown     = true;
            }

            if (w.IsRanged)
            {
                stats.RangedWeapons.Add(ws);
                stats.RangedRange.Add(w.Range);
                stats.RangedDirectRange.Add(w.DirectRange);
                stats.RangedCooldownTime.Add(w.Cooldown);
                stats.RangedDmg.Add(w.RangedDamage);
                stats.RangedPrecision.Add(w.Precision);
                anyRanged = true;
            }
            else
            {
                stats.MeleeWeapons.Add(ws);
                anyMelee = true;
            }
        }

        // Keep priority-ordered scan invariant intact — UnitRegistry.BuildStats
        // sorts both lists by Priority desc; granted weapons must slot into
        // their priority position too so combat selection stays deterministic.
        if (anyMelee && stats.MeleeWeapons.Count > 1)
            stats.MeleeWeapons = System.Linq.Enumerable.ToList(
                System.Linq.Enumerable.OrderByDescending(stats.MeleeWeapons, w => w.Priority));
        if (anyRanged && stats.RangedWeapons.Count > 1)
        {
            // Same desync fix as UnitRegistry.BuildStats: permute all six parallel ranged
            // lists by one shared Priority order (sorting only RangedWeapons would misalign
            // the RangedRange/Dmg/Cooldown side-lists that StripGrantedWeapons relies on).
            var order = System.Linq.Enumerable.ToList(
                System.Linq.Enumerable.OrderByDescending(
                    System.Linq.Enumerable.Range(0, stats.RangedWeapons.Count),
                    idx => stats.RangedWeapons[idx].Priority));
            var rw = stats.RangedWeapons; var rr = stats.RangedRange; var rdr = stats.RangedDirectRange;
            var rct = stats.RangedCooldownTime; var rd = stats.RangedDmg; var rp = stats.RangedPrecision;
            stats.RangedWeapons      = order.ConvertAll(o => rw[o]);
            stats.RangedRange        = order.ConvertAll(o => rr[o]);
            stats.RangedDirectRange  = order.ConvertAll(o => rdr[o]);
            stats.RangedCooldownTime = order.ConvertAll(o => rct[o]);
            stats.RangedDmg          = order.ConvertAll(o => rd[o]);
            stats.RangedPrecision    = order.ConvertAll(o => rp[o]);
        }
    }

    /// <summary>Inverse of ApplyGrantedWeapons — drop any WeaponStats tagged with
    /// SourceBuffID == buffDefID from the unit's effective weapon list. Safe to
    /// call when the buff didn't grant weapons (no-op in that case).</summary>
    private static void StripGrantedWeapons(UnitArrays units, int unitIdx, string buffDefID)
    {
        if (string.IsNullOrEmpty(buffDefID)) return;
        var stats = units[unitIdx].Stats;

        // Ranged removal needs to keep the parallel side-lists (RangedRange,
        // RangedDirectRange, RangedCooldownTime, RangedDmg, RangedPrecision)
        // in lockstep, so walk the indexes manually rather than using RemoveAll.
        for (int i = stats.RangedWeapons.Count - 1; i >= 0; i--)
        {
            if (stats.RangedWeapons[i].SourceBuffID == buffDefID)
            {
                stats.RangedWeapons.RemoveAt(i);
                if (i < stats.RangedRange.Count) stats.RangedRange.RemoveAt(i);
                if (i < stats.RangedDirectRange.Count) stats.RangedDirectRange.RemoveAt(i);
                if (i < stats.RangedCooldownTime.Count) stats.RangedCooldownTime.RemoveAt(i);
                if (i < stats.RangedDmg.Count) stats.RangedDmg.RemoveAt(i);
                if (i < stats.RangedPrecision.Count) stats.RangedPrecision.RemoveAt(i);
            }
        }
        stats.MeleeWeapons.RemoveAll(w => w.SourceBuffID == buffDefID);
    }

    /// <summary>Is the named buff currently active on this unit?</summary>
    public static bool HasBuff(UnitArrays units, int unitIdx, string buffDefID)
    {
        if (unitIdx < 0 || unitIdx >= units.Count) return false;
        var buffs = units[unitIdx].ActiveBuffs;
        for (int i = 0; i < buffs.Count; i++)
            if (buffs[i].BuffDefID == buffDefID) return true;
        return false;
    }

    /// <summary>Sum of all "Add"-type buff effects on this unit whose Stat
    /// name matches <paramref name="stat"/>. Used for resource fields that
    /// aren't unit stats (MaxMana, ManaRegen, MonsterCap, HumanCap, etc.) —
    /// the unit-stat path goes through <see cref="GetModifiedStat"/>.</summary>
    public static float SumExtraAdd(UnitArrays units, int unitIdx, string stat)
    {
        if (unitIdx < 0 || unitIdx >= units.Count) return 0f;
        float sum = 0f;
        foreach (var buff in units[unitIdx].ActiveBuffs)
            foreach (var eff in buff.Effects)
                if (eff.Stat == stat && eff.Type == "Add")
                    sum += eff.Value * buff.StackCount;
        return sum;
    }

    /// <summary>Apply Add/Multiply/Set buff effects with a raw string stat name to a
    /// base value — the string-keyed sibling of <see cref="GetModifiedStat"/>, for
    /// NecromancerState stats (MaxMana, CooldownRate, …) that live outside the
    /// <see cref="BuffStat"/> enum. Combination: (base + ΣAdd) × ∏Multiply, unless a
    /// Set effect is present (last Set wins, overriding everything). Returns the base
    /// unchanged when there are no matching effects.</summary>
    public static float GetModifiedExtra(UnitArrays units, int unitIdx, string stat, float baseValue)
    {
        if (unitIdx < 0 || unitIdx >= units.Count) return baseValue;
        float additive = 0f;
        float multiplicative = 1f;
        float? setValue = null;
        foreach (var buff in units[unitIdx].ActiveBuffs)
            foreach (var eff in buff.Effects)
            {
                if (eff.Stat != stat) continue;
                switch (eff.Type)
                {
                    case "Add": additive += eff.Value * buff.StackCount; break;
                    case "Multiply": multiplicative *= MathF.Pow(eff.Value, buff.StackCount); break;
                    case "Set": setValue = eff.Value; break;
                }
            }
        return setValue ?? (baseValue + additive) * multiplicative;
    }

    /// <summary>Largest "Set" buff effect value on this unit with the named
    /// stat, or null when no such effect is active. Used for floor-style
    /// overrides (e.g. god-mode "AllPaths = 9" raises every magic path level
    /// to at least 9, leaving higher native levels untouched).</summary>
    public static float? MaxSetExtra(UnitArrays units, int unitIdx, string stat)
    {
        if (unitIdx < 0 || unitIdx >= units.Count) return null;
        float? result = null;
        foreach (var buff in units[unitIdx].ActiveBuffs)
            foreach (var eff in buff.Effects)
                if (eff.Stat == stat && eff.Type == "Set")
                    result = result.HasValue ? MathF.Max(result.Value, eff.Value) : eff.Value;
        return result;
    }

    /// <summary>The buff "stat" name that grants levels in a specific magic path.
    /// A buff effect of {Type:"Add", Stat:PathStat(Shock), Value:1} raises the
    /// bearer's Shock path by 1 (see <see cref="EffectivePathLevel"/>). Prefixed
    /// with "Path" so it can't collide with ordinary stat names ("Order", "Body",
    /// "Nature" are also magic-path enum names).</summary>
    public static string PathStat(Data.Registries.MagicPath p) => "Path" + p;

    /// <summary>Sum of additive path-level buffs on a unit for one path (honors
    /// stack count). This is what lets buffs grant magic paths to the caster on
    /// top of its native UnitDef levels.</summary>
    public static int PathLevelBonus(UnitArrays units, int unitIdx, Data.Registries.MagicPath p)
    {
        if (unitIdx < 0 || unitIdx >= units.Count) return 0;
        string stat = PathStat(p);
        float sum = 0f;
        foreach (var buff in units[unitIdx].ActiveBuffs)
            foreach (var eff in buff.Effects)
                if (eff.Type == "Add" && eff.Stat == stat)
                    sum += eff.Value * buff.StackCount;
        return (int)sum;
    }

    /// <summary>A unit's effective caster level in a magic path: its native
    /// UnitDef level plus any additive path buffs (<see cref="PathLevelBonus"/>),
    /// then floored by an active "Set AllPaths" buff (god mode). Single source of
    /// truth shared by spell casting (whether a unit may cast a spell), mana-cost
    /// scaling, and the unit-sheet path display.</summary>
    public static int EffectivePathLevel(UnitArrays units, int unitIdx,
        Data.Registries.UnitDef? def, Data.Registries.MagicPath p)
    {
        int total = (def?.GetPathLevel(p) ?? 0) + PathLevelBonus(units, unitIdx, p);
        float? floor = MaxSetExtra(units, unitIdx, "AllPaths");
        return floor.HasValue && floor.Value > total ? (int)floor.Value : total;
    }

    public static float GetModifiedStat(UnitArrays units, int unitIdx, BuffStat stat, float baseValue)
    {
        if (unitIdx < 0 || unitIdx >= units.Count) return baseValue;
        return GetModifiedStat(units[unitIdx].ActiveBuffs, stat, baseValue);
    }

    /// <summary>Apply a unit's active-buff modifiers to a base stat value.
    /// Overload that takes the buff list directly so UI code can compute an
    /// effective stat from a <see cref="Movement.Unit"/> without a unit index.
    /// Combination: (base + sum of Add) * product of Multiply, unless a Set
    /// effect is present (last Set wins, overriding everything).</summary>
    public static float GetModifiedStat(IReadOnlyList<ActiveBuff> buffs, BuffStat stat, float baseValue)
    {
        string statName = stat.ToString();
        float additive = 0f;
        float multiplicative = 1f;
        float? setValue = null;

        foreach (var buff in buffs)
        {
            foreach (var eff in buff.Effects)
            {
                if (eff.Stat != statName) continue;
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
