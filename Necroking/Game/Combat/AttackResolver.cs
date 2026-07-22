using System;
using System.Collections.Generic;
using Necroking.Core;
using Necroking.Data;
using Necroking.Data.Registries;
using Necroking.Game;
using Necroking.Lib;
using Necroking.Movement;

namespace Necroking.GameSystems.Combat;

/// <summary>
/// THE attack-resolution funnel — every way an attack turns into damage lives here:
/// pending-attack dispatch (melee / ranged / sweep), the melee formula, arrow and
/// spell-projectile impacts, spell damage (single + AoE), lightning and poison-cloud
/// damage events, and the on-hit riders (knockdown, limb chop, weapon bonus effects).
///
/// Contract: callers hand over BIG objects (the <see cref="Simulation"/>, a
/// <see cref="ProjectileHit"/>, a <see cref="SpellDef"/>, a <see cref="LightningDamage"/> …)
/// and the resolver digs out everything else itself — damage flags, caster DRN tiers,
/// weapon stats, MR gates — and owns ALL combat logging. Call sites never assemble
/// flags or write <see cref="CombatLogEntry"/> rows by hand.
///
/// Sim-level only (like DamageSystem/PotionSystem): no Game1, no rendering, no
/// wall-clock — it must behave identically in headless scenarios. Roll ORDER inside
/// each resolver is load-bearing (shared RNG in <see cref="UnitUtil.RollDRN"/>);
/// don't reorder dice without re-baselining the balance scenarios.
/// </summary>
public static class AttackResolver
{
    // ===================== Pending-attack dispatch =====================

    /// <summary>Tolerance added to melee reach at the impact frame. The swing was
    /// stamped in range, so the target gets this much escape allowance before the
    /// hit becomes a whiff. Generous on purpose: a whiff should mean "visibly ran
    /// out of the bite," not pixel-shaving a target drifting in place — at 1.5u,
    /// only a target already near full flight speed when the swing started can
    /// escape the windup.</summary>
    private const float ImpactWhiffTolerance = 1.5f;

    /// <summary>
    /// Resolve a queued attack at its animation's impact frame, re-checking that a
    /// melee target is still plausibly in reach. Range is gated at stamp time; this
    /// is the impact-side honesty check — a target that outran the swing during the
    /// windup gets a logged Whiff (cooldown stays spent, no dice) instead of
    /// impossible-looking damage at a distance. Ranged pending attacks resolve
    /// unchecked (the projectile flies and leads on its own). Pounce never comes
    /// through here: JumpSystem's landing callback has its own combined-radius
    /// check and calls ResolvePendingAttack directly.
    /// </summary>
    public static void TryResolvePendingAttackAtImpact(Simulation sim, int unitIdx)
    {
        var units = sim.UnitsMut;
        if (unitIdx < 0 || unitIdx >= units.Count || !units[unitIdx].Alive) return;
        var t = units[unitIdx].PendingAttack;
        if (t.IsNone) return;

        if (!units[unitIdx].PendingWeaponIsRanged && t.IsUnit)
        {
            int ti = UnitUtil.ResolveUnitIndex(units, t.UnitID);
            if (ti >= 0)
            {
                int w = units[unitIdx].PendingWeaponIdx;
                var weapons = units[unitIdx].Stats.MeleeWeapons;
                var ws = (w >= 0 && w < weapons.Count) ? weapons[w] : null;
                // Sweep stamps at its own (larger) radius; everything else at melee reach.
                float reach = (ws != null && ws.Archetype == WeaponArchetype.Sweep)
                    ? ws.SweepRadius
                    : MeleeRangeUtil.Compute(units, unitIdx, ti, sim.GameData);
                reach += ImpactWhiffTolerance;
                float dist = (units[ti].Position - units[unitIdx].Position).Length();
                if (dist > reach)
                {
                    sim.CombatLog.AddEntry(new CombatLogEntry
                    {
                        Timestamp = sim.GameTime,
                        AttackerName = sim.GetUnitDisplayName(unitIdx),
                        DefenderName = sim.GetUnitDisplayName(ti),
                        AttackerFaction = FactionChar(units, unitIdx),
                        DefenderFaction = FactionChar(units, ti),
                        WeaponName = ws?.Name ?? "?",
                        Outcome = CombatLogOutcome.Whiff,
                        Note = $"Out of reach at impact: dist {dist:F2} > reach {reach:F2}",
                    });
                    units[unitIdx].PendingAttack = CombatTarget.None;
                    units[unitIdx].PendingWeaponIdx = -1;
                    units[unitIdx].PendingWeaponIsRanged = false;
                    units[unitIdx].PendingRangedTarget = GameConstants.InvalidUnit;
                    return;
                }
            }
        }
        ResolvePendingAttack(sim, unitIdx);
    }

    /// <summary>Resolve a unit's stamped PendingAttack now: snapshot + clear the
    /// pending state, then dispatch to the ranged, sweep, or single-melee path.</summary>
    public static void ResolvePendingAttack(Simulation sim, int unitIdx)
    {
        var units = sim.UnitsMut;
        if (unitIdx < 0 || unitIdx >= units.Count || !units[unitIdx].Alive) return;
        var t = units[unitIdx].PendingAttack;
        if (t.IsNone) return;

        // Snapshot pending weapon state, then clear so animation can re-queue cleanly.
        int weaponIdx = units[unitIdx].PendingWeaponIdx;
        bool isRanged = units[unitIdx].PendingWeaponIsRanged;
        uint rangedTargetID = units[unitIdx].PendingRangedTarget;

        units[unitIdx].PendingAttack = CombatTarget.None;
        units[unitIdx].PendingWeaponIdx = -1;
        units[unitIdx].PendingWeaponIsRanged = false;
        units[unitIdx].PendingRangedTarget = GameConstants.InvalidUnit;

        // Ranged path: prefer stored target ID (target may have died/moved between queue and action moment).
        if (isRanged || units[unitIdx].Archetype == AI.ArchetypeRegistry.ArcherUnit)
        {
            int defenderIdx = -1;
            if (rangedTargetID != GameConstants.InvalidUnit)
                defenderIdx = UnitUtil.ResolveUnitIndex(units, rangedTargetID);
            if (defenderIdx < 0 && t.IsUnit)
                defenderIdx = UnitUtil.ResolveUnitIndex(units, t.UnitID);
            if (defenderIdx < 0) return;

            FireArrowAt(sim, unitIdx, defenderIdx, weaponIdx);
            return;
        }

        if (!t.IsUnit) return;
        int meleeDefenderIdx = UnitUtil.ResolveUnitIndex(units, t.UnitID);

        // Sweep: dispatch even if the primary target died — the cone may still
        // catch other victims. ResolveMeleeSweep handles missing-primary gracefully.
        WeaponStats? pendingWeapon = (weaponIdx >= 0 && weaponIdx < units[unitIdx].Stats.MeleeWeapons.Count)
            ? units[unitIdx].Stats.MeleeWeapons[weaponIdx] : null;
        if (pendingWeapon != null && pendingWeapon.Archetype == WeaponArchetype.Sweep)
        {
            ResolveMeleeSweep(sim, unitIdx, meleeDefenderIdx, weaponIdx);
            return;
        }

        if (meleeDefenderIdx < 0)
        {
            // Target died between queue and resolve. Refund the commitment so the
            // attacker can immediately line up another target instead of standing
            // frozen through a full cooldown + PostAttackTimer for an unresolved
            // ghost-swing. Per-weapon Cooldown stays (the swing was "thrown" even
            // if it missed the dead target) but the post-attack movement lockout
            // clears so the unit can reorient.
            units[unitIdx].PostAttackTimer = 0f;
            DebugLog.Log("ai",
                $"[ResolvePendingAttack] unit#{unitIdx} target vanished (id={t.UnitID}); refunding PostAttackTimer");
            return;
        }

        ResolveMeleeAttack(sim, unitIdx, meleeDefenderIdx, weaponIdx);
    }

    // ===================== Ranged launch =====================

    /// <summary>
    /// Fire one arrow from attacker at defender using ranged weapon <paramref name="weaponIdx"/> —
    /// the single source of truth for arrow ballistics, shared by the archetype path
    /// (ResolvePendingAttack action moment) and the legacy ArcherAttack behavior.
    /// Ports the C++ resolveRangedAttack:
    ///  - lead a moving target by the arrow's straight-line flight time,
    ///  - direct (flat 5°) shot only when the target is inside the weapon's DirectRange
    ///    AND no friendly stands in the fire lane — otherwise lob a ballistic arc that
    ///    sails over allies' heads (lobbed arrows only turn lethal below head height
    ///    on the way down),
    ///  - per-weapon Precision drives the lob scatter / direct wobble in SpawnArrow.
    /// </summary>
    private static void FireArrowAt(Simulation sim, int attackerIdx, int defenderIdx, int weaponIdx)
    {
        var units = sim.UnitsMut;
        ref var stats = ref units[attackerIdx].Stats;
        int wIdx = (weaponIdx >= 0 && weaponIdx < stats.RangedDmg.Count) ? weaponIdx : 0;
        int damage = stats.RangedDmg.Count > 0 ? stats.RangedDmg[wIdx] : 8;
        float maxRange = stats.RangedRange.Count > 0 ? stats.RangedRange[wIdx] : 18f;
        float directRange = (wIdx < stats.RangedDirectRange.Count && stats.RangedDirectRange[wIdx] > 0f)
            ? stats.RangedDirectRange[wIdx] : maxRange * 0.4f;
        int precision = (wIdx < stats.RangedPrecision.Count && stats.RangedPrecision[wIdx] > 0)
            ? stats.RangedPrecision[wIdx] : 10;
        string weaponName = wIdx < stats.RangedWeapons.Count ? stats.RangedWeapons[wIdx].Name : "";

        Vec2 from = units[attackerIdx].Position;
        // Lead the shot (shared intercept helper; 2 iterations converge the
        // flight-time estimate — the old inline one-pass lead under-led fast
        // strafers). dist re-measured to the led point for direct-vs-lob.
        Vec2 aim = InterceptUtil.PredictPosition(
            from, units[defenderIdx].Position, units[defenderIdx].Velocity,
            ProjectileManager.ArrowSpeed);
        float dist = (aim - from).Length();

        bool direct = dist <= directRange && IsFireLaneClear(sim, attackerIdx, aim);
        sim.Projectiles.Spawn(from, aim, units[attackerIdx].Faction, units[attackerIdx].Id,
            ProjectileType.RegularHit, damage, ProjectileManager.ArrowSpeed, lob: !direct,
            precision: precision, weaponName: weaponName,
            spawnHeight: units[attackerIdx].EffectSpawnHeight);
    }

    // A friendly within this distance of the attacker→aim line forces a lob.
    private const float FireLaneBlockRadius = 1.0f;
    private static readonly List<uint> _fireLaneScratch = new(32);

    /// <summary>True when no living friendly stands within <see cref="FireLaneBlockRadius"/>
    /// of the straight segment from the attacker to the aim point — i.e. a flat direct
    /// shot wouldn't skewer an ally on the way. Enemies in the lane don't block (the
    /// arrow simply hits them instead).</summary>
    private static bool IsFireLaneClear(Simulation sim, int attackerIdx, Vec2 target)
    {
        var units = sim.UnitsMut;
        Vec2 from = units[attackerIdx].Position;
        Vec2 seg = target - from;
        float segLenSq = seg.LengthSq();
        if (segLenSq < 1e-4f) return true;

        Vec2 mid = (from + target) * 0.5f;
        float queryRadius = 0.5f * MathF.Sqrt(segLenSq) + FireLaneBlockRadius;
        _fireLaneScratch.Clear();
        sim.Quadtree.QueryRadiusByFaction(mid, queryRadius,
            units[attackerIdx].Faction.Bit(), _fireLaneScratch);
        foreach (uint nid in _fireLaneScratch)
        {
            if (nid == units[attackerIdx].Id) continue;
            int j = UnitUtil.ResolveUnitIndex(units, nid);
            if (j < 0) continue;
            float tSeg = Math.Clamp((units[j].Position - from).Dot(seg) / segLenSq, 0f, 1f);
            Vec2 closest = from + seg * tSeg;
            if ((units[j].Position - closest).LengthSq() < FireLaneBlockRadius * FireLaneBlockRadius)
                return false;
        }
        return true;
    }

    // ===================== Melee =====================

    private static readonly List<uint> _sweepScratch = new(32);

    /// <summary>
    /// Sweep AOE melee: forward cone centered on attacker's facing. Queries the
    /// quadtree within SweepRadius, filters by cone arc (and faction unless
    /// SweepHitsAllies), then runs ResolveMeleeAttack against each victim. Each
    /// target rolls independently — hit/miss, damage, knockdown, coats all
    /// resolved per defender. Primary target is always included if still alive.
    /// </summary>
    private static void ResolveMeleeSweep(Simulation sim, int attackerIdx, int primaryDefenderIdx, int weaponIdx)
    {
        var units = sim.UnitsMut;
        var atkStats = units[attackerIdx].Stats;
        if (weaponIdx < 0 || weaponIdx >= atkStats.MeleeWeapons.Count) return;
        var weapon = atkStats.MeleeWeapons[weaponIdx];

        Vec2 origin = units[attackerIdx].Position;
        // FacingAngle is stored in DEGREES (see Movement/FacingUtil.cs) — convert
        // before handing to the radian-expecting Cos/Sin.
        float facingRad = units[attackerIdx].FacingAngle * (MathF.PI / 180f);
        float halfArcRad = weapon.SweepArcDegrees * 0.5f * (MathF.PI / 180f);
        float cosThreshold = MathF.Cos(halfArcRad);
        float facingCos = MathF.Cos(facingRad);
        float facingSin = MathF.Sin(facingRad);
        float radius = weapon.SweepRadius;

        Faction atkFaction = units[attackerIdx].Faction;
        FactionMask mask = weapon.SweepHitsAllies
            ? FactionMask.All
            : FactionMaskExt.AllExcept(atkFaction);

        _sweepScratch.Clear();
        sim.Quadtree.QueryRadiusByFaction(origin, radius, mask, _sweepScratch);

        int hitCount = 0;
        uint primaryID = primaryDefenderIdx >= 0 ? units[primaryDefenderIdx].Id : GameConstants.InvalidUnit;
        bool primaryResolved = false;

        for (int k = 0; k < _sweepScratch.Count; k++)
        {
            int defIdx = UnitUtil.ResolveUnitIndex(units, _sweepScratch[k]);
            if (defIdx < 0 || defIdx == attackerIdx || !units[defIdx].Alive) continue;

            // Cone check: dot product between facing dir and (defender-origin) dir.
            Vec2 d = units[defIdx].Position - origin;
            float dLen = d.Length();
            if (dLen < 0.0001f) { /* on top of us — count as in-cone */ }
            else
            {
                float dx = d.X / dLen;
                float dy = d.Y / dLen;
                float dot = facingCos * dx + facingSin * dy;
                if (dot < cosThreshold) continue;
            }

            if (units[defIdx].Id == primaryID) primaryResolved = true;
            ResolveMeleeAttack(sim, attackerIdx, defIdx, weaponIdx);
            hitCount++;
        }

        // If the primary target is still alive and wasn't picked up by the cone
        // scan (e.g. quadtree rebuild lag, edge rounding), resolve against them
        // anyway so the AI's chosen target always gets swung at.
        if (!primaryResolved && primaryDefenderIdx >= 0 && units[primaryDefenderIdx].Alive)
        {
            ResolveMeleeAttack(sim, attackerIdx, primaryDefenderIdx, weaponIdx);
            hitCount++;
        }

        if (hitCount == 0 && primaryDefenderIdx < 0)
        {
            // No victims and primary already dead — refund post-attack timer so the
            // bear isn't locked out swinging at air.
            units[attackerIdx].PostAttackTimer = 0f;
        }

        DebugLog.Log("combat",
            $"[Sweep] unit#{attackerIdx} ({weapon.Name}) hit {hitCount} target(s) " +
            $"arc={weapon.SweepArcDegrees:F0}° r={weapon.SweepRadius:F1}");
    }

    /// <summary>
    /// The full melee formula: opposed tier-4 DRN attack-vs-defense (fatigue,
    /// paralysis, buffs, harassment, shield parry), hit location, Strength+weapon
    /// damage roll vs location armor + DRN, toughness halving, blunt/slashing
    /// modifiers, limb cap — then damage application and the on-hit riders
    /// (limb chop, knockdown, weapon bonus effects). Owns the combat-log entry.
    /// Returns whether the swing HIT — non-standard dispatchers (TrampleSystem)
    /// consume the return value instead of peeking at sim state.
    /// </summary>
    /// <param name="peekOnly">Roll the dice and return hit/miss, but apply no side
    /// effects (no damage, no anim, no buffs, no log entry). Trample uses this to
    /// decide hit/miss BEFORE applying knockback — the actual damage call follows
    /// with forceHit so the corpse inherits the velocity if the unit dies.</param>
    public static bool ResolveMeleeAttack(Simulation sim, int attackerIdx, int defenderIdx,
        int weaponIdx, bool suppressDodgeAnim = false, bool forceHit = false, bool peekOnly = false)
    {
        var units = sim.UnitsMut;
        var atkStats = units[attackerIdx].Stats;
        var defStats = units[defenderIdx].Stats;

        // Resolve per-weapon stats so multi-weapon units (e.g. wolf's Bite + Pounce)
        // use the correct weapon's damage/length/name/bonuses. Falls back to the
        // aggregated unit stats for single-weapon or unarmed units.
        WeaponStats? weapon = (weaponIdx >= 0 && weaponIdx < atkStats.MeleeWeapons.Count)
            ? atkStats.MeleeWeapons[weaponIdx] : null;
        int weaponDamage = weapon?.Damage ?? atkStats.Damage;
        int weaponAttackBonus = weapon?.AttackBonus ?? 0;
        int weaponLength = weapon?.Length ?? atkStats.Length;
        string weaponName = weapon?.Name
            ?? (atkStats.MeleeWeapons.Count > 0 ? atkStats.MeleeWeapons[0].Name : "Unarmed");
        bool weaponKnockdown = weapon?.HasKnockdown ?? atkStats.HasKnockdown;

        // Every attempted melee swing fatigues the attacker by their Encumbrance.
        // Fatigue caps at 100 (that's the ceiling of the (100 - Fatigue) knockdown-
        // recovery formula). Regens at 1/tick every FatigueRegenInterval seconds.
        float buffedEnc = BuffSystem.GetModifiedStat(units, attackerIdx, BuffStat.Encumbrance, atkStats.Encumbrance);
        units[attackerIdx].Fatigue = MathF.Min(100f, units[attackerIdx].Fatigue + buffedEnc);

        // Hit resolution always rolls tier-4 (d6 open-ended) dice for BOTH sides,
        // regardless of unit tier — the exploding die means any attacker can
        // eventually connect with any defender (no impossible-to-hit stat walls;
        // female deer mirrors used to be literal stalemates at 4+d3 vs 6+d3).
        // Damage/protection rolls below still use each unit's own Drn tier.
        int atkDRN = UnitUtil.RollDRN(4);
        int defDRN = UnitUtil.RollDRN(4);

        // Fatigue penalties (manual p.61): attack −1 per 20 fatigue, defense −1 per
        // 10 fatigue (rounded down). This is what makes a tired unit easier to hit
        // and less likely to land its own blows — the master clock of melee.
        int atkFatiguePenalty = (int)(units[attackerIdx].Fatigue / 20f);
        int defFatiguePenalty = (int)(units[defenderIdx].Fatigue / 10f);

        // The defender's wielded weapon contributes its DefenseBonus, and its shield
        // contributes ShieldDefense (a penalty in the data, e.g. −1) — both were
        // previously dropped on the floor while the attacker's AttackBonus was applied.
        int defWeaponDefBonus = defStats.MeleeWeapons.Count > 0 ? defStats.MeleeWeapons[0].DefenseBonus : 0;

        // Apply paralysis reduction + buff modifiers (e.g. knockdown reduces defense by 70%)
        float atkParalysis = PotionSystem.GetParalysisFraction(units, attackerIdx);
        float defParalysis = PotionSystem.GetParalysisFraction(units, defenderIdx);
        float buffedAtk = BuffSystem.GetModifiedStat(units, attackerIdx, BuffStat.Attack, atkStats.Attack + weaponAttackBonus);
        float buffedDef = BuffSystem.GetModifiedStat(units, defenderIdx, BuffStat.Defense, defStats.Defense);
        int effectiveAtk = (int)(buffedAtk * atkParalysis) - atkFatiguePenalty;
        int effectiveDef = (int)(buffedDef * defParalysis) + defWeaponDefBonus + defStats.ShieldDefense - defFatiguePenalty;

        int modAtk = effectiveAtk + atkDRN;
        int harassment = units[defenderIdx].Harassment;
        int modDef = effectiveDef - harassment + defDRN;

        // Weapon damage type + two-handedness (inferred from weapon name) and AP/AN.
        WeaponDamageType wType = weapon?.DamageType
            ?? (atkStats.MeleeWeapons.Count > 0 ? atkStats.MeleeWeapons[0].DamageType : WeaponDamageType.Slashing);
        bool twoHanded = weapon?.TwoHanded
            ?? (atkStats.MeleeWeapons.Count > 0 && atkStats.MeleeWeapons[0].TwoHanded);
        bool weaponAP = weapon?.HasArmorPiercing ?? atkStats.HasArmorPiercing;
        bool weaponAN = weapon?.HasArmorNegating ?? atkStats.HasArmorNegating;

        // Log the EFFECTIVE attack/defense (buffs, weapon bonuses, paralysis, fatigue,
        // shield/weapon defense) — the values the dice were actually added to. The log
        // used to print the raw stats, so a Hit could show losing numbers.
        var logEntry = new CombatLogEntry
        {
            Timestamp = sim.GameTime,
            AttackerName = sim.GetUnitDisplayName(attackerIdx),
            DefenderName = sim.GetUnitDisplayName(defenderIdx),
            AttackerFaction = FactionChar(units, attackerIdx),
            DefenderFaction = FactionChar(units, defenderIdx),
            WeaponName = weaponName,
            AttackBase = effectiveAtk,
            AttackDRN = atkDRN,
            DefenseBase = effectiveDef,
            DefenseDRN = defDRN,
            HarassmentPenalty = harassment,
            DamageFlags = weaponAN ? DamageFlags.ArmorNegating : default,
        };

        bool hit = forceHit ? true : (modAtk >= modDef);
        // peekOnly: caller wants only the hit/miss decision (above); skip all
        // side effects below. Useful for callers that need to apply physics
        // BEFORE damage so corpses inherit knockback velocity (see TrampleSystem).
        if (peekOnly) return hit;

        if (!hit)
        {
            units[defenderIdx].Harassment++;
            units[defenderIdx].Dodging = true;
            // Visual dodge reaction — gated centrally in ApplyDodgeAnim (prone,
            // mid-jump, fleeing/routing, shared reaction cooldown) at Reaction
            // priority so it can't cancel the defender's own in-progress swing.
            // suppressDodgeAnim: trample owns its own dodge anim (snappy 0.4s hop)
            // and only plays it after confirming a safe tile exists, so the standard
            // dodge anim must not flash here.
            if (!suppressDodgeAnim)
                DamageSystem.ApplyDodgeAnim(units, defenderIdx);
            // Record the swing so retarget-on-hit AI still fires on misses — a missed
            // attack is still active combat engagement. Without this, a low-attack unit
            // (e.g. zombie deer vs high-defense wolf) whiffs repeatedly and the wolf
            // never realizes it's being attacked.
            units[defenderIdx].LastAttackerID = units[attackerIdx].Id;
            units[defenderIdx].LastHitTime = sim.GameTime;
            if (units[defenderIdx].Archetype == AI.ArchetypeRegistry.WolfPack)
            {
                DebugLog.Log("wolf_retarget",
                    $"[Wolf {units[defenderIdx].Id}] swung at by id={units[attackerIdx].Id} " +
                    $"({units[attackerIdx].UnitDefID}) → MISS at t={sim.GameTime:F2}s (LastHitTime + LastAttackerID set)");
            }
            logEntry.Outcome = CombatLogOutcome.Miss;
            sim.CombatLog.AddEntry(logEntry);
            return false;
        }

        // Shield hit (manual p.59-60): a shield interposes unless the attack also
        // beats Defense + Parry. On a shield hit the shield's Protection is added to
        // the defender's protection roll; a clean hit (attack beat def+parry) ignores it.
        bool hasShield = defStats.ShieldProtection > 0 || defStats.ShieldParry > 0;
        bool shieldHit = hasShield && (modAtk < modDef + defStats.ShieldParry);

        // Hit location
        var hitLoc = UnitUtil.RollHitLocation(units[attackerIdx].Size, units[defenderIdx].Size, weaponLength);

        // Damage roll: Strength (×1.25 if two-handed, manual p.61) + weapon damage + DRN.
        float buffedStr = BuffSystem.GetModifiedStat(units, attackerIdx, BuffStat.Strength, atkStats.Strength);
        int strContribution = (int)(twoHanded ? buffedStr * 1.25f : buffedStr);
        int baseDmg = (int)((strContribution + weaponDamage) * atkParalysis);
        int dmgDRN = UnitUtil.RollDRN(atkStats.Drn);
        int dmgRoll = baseDmg + dmgDRN;
        // Blunt: +25% on HEAD hits, BEFORE protection is deducted.
        if (wType == WeaponDamageType.Blunt && hitLoc == HitLocation.Head)
            dmgRoll = (int)(dmgRoll * 1.25f);

        // Protection roll: location armor (head→helmet) + shield-on-shield-hit + DRN,
        // subtracted from damage; what's left is then halved by Toughness (hide/flesh,
        // up to its value — see DamageSystem.MitigateByToughness). The piercing /
        // armor-piercing / armor-defeating fraction cuts armor AND toughness alike.
        int protDRN = UnitUtil.RollDRN(defStats.Drn);
        int armorProt = hitLoc == HitLocation.Head ? defStats.Armor.HeadProtection : defStats.Armor.BodyProtection;
        float protStat = armorProt + (shieldHit ? defStats.ShieldProtection : 0);
        float toughness = BuffSystem.GetModifiedStat(units, defenderIdx,
            BuffStat.Toughness, defStats.Toughness);

        // Armor-defeating hit: a protection roll of 1 bypasses 25% of armor. Every
        // DRN tier rolls a single die first, so a 1 is possible for everyone (d3: 1/3,
        // d6 tiers: 1/6). A tired defender (fatigue >= 50) is also defeated by a 2.
        float defFatigue = units[defenderIdx].Fatigue;
        bool armorDefeating = protDRN == 1
            || (protDRN == 2 && defFatigue >= 50f);

        if (weaponAN)
        {
            protStat = 0f;   // armor-negating ignores protection entirely
            toughness = 0f;  // ...and hide with it
        }
        else
        {
            float reduction = 0f;
            if (wType == WeaponDamageType.Piercing) reduction += 0.15f; // piercing weapon type
            if (weaponAP) reduction += 0.50f;                            // armor-piercing ability
            if (armorDefeating) reduction += 0.25f;                      // low protection roll
            reduction = MathF.Min(reduction, 1f);
            protStat *= (1f - reduction);
            toughness *= (1f - reduction);
        }
        int prot = (int)protStat + protDRN;

        int postArmor = dmgRoll - prot;
        int netDmg = DamageSystem.MitigateByToughness(postArmor, toughness);
        int toughnessMit = Math.Max(0, postArmor) - netDmg;
        // Slashing: +25% AFTER protection is deducted (manual p.61).
        if (wType == WeaponDamageType.Slashing && netDmg > 0)
            netDmg = (int)(netDmg * 1.25f);
        // Limb cap (manual p.62): an arm/leg hit can't deal more than half max HP —
        // the limb is maimed instead of the whole body destroyed.
        if (hitLoc == HitLocation.Arms || hitLoc == HitLocation.Legs)
            netDmg = Math.Min(netDmg, Math.Max(1, BuffSystem.EffectiveMaxHP(units, defenderIdx) / 2));
        // Dominions allows a glancing blow to deal zero damage when protection wins.
        if (netDmg < 0) netDmg = 0;

        logEntry.Outcome = CombatLogOutcome.Hit;
        logEntry.HitLoc = hitLoc;
        logEntry.HitLocationName = hitLoc.ToString();
        logEntry.DamageBase = baseDmg;
        logEntry.DamageDRN = dmgDRN;
        logEntry.ProtBase = (int)protStat;
        logEntry.ProtDRN = protDRN;
        logEntry.ToughnessMit = toughnessMit;
        logEntry.NetDamage = netDmg;
        sim.CombatLog.AddEntry(logEntry);

        if (netDmg > 0)
        {
            units[defenderIdx].HitReacting = true;
            units[defenderIdx].LastHitTime = sim.GameTime;
            // Flinch gated by ApplyHitReactAnim (skips fleeing / prone / mid-jump /
            // refractory). HitReacting/LastHitTime stay set for AI reactions.
            DamageSystem.ApplyHitReactAnim(units, defenderIdx);
        }
        else
        {
            units[defenderIdx].BlockReacting = true;
            units[defenderIdx].LastHitTime = sim.GameTime;
            DamageSystem.ApplyHitReactAnim(units, defenderIdx);
        }

        // Diagnostic: log every melee hit on a wolf-archetype unit so we can verify
        // the retarget pipeline is seeing attackers.
        if (units[defenderIdx].Archetype == AI.ArchetypeRegistry.WolfPack)
        {
            DebugLog.Log("wolf_retarget",
                $"[Wolf {units[defenderIdx].Id}] hit by attacker id={units[attackerIdx].Id} " +
                $"({units[attackerIdx].UnitDefID}) netDmg={netDmg} at t={sim.GameTime:F2}s — " +
                $"LastHitTime set, LastAttackerID will be set in ApplyDirect");
        }

        // Melee uses ApplyDirect — armor already calculated above with DRN rolls
        DamageSystem.ApplyDirect(units, defenderIdx, netDmg, sim.DamageEventsMut, attackerIdx);

        // Limb loss / decapitation: a slashing blow that costs >= 50% max HP to a
        // limb or head maims it (manual p.60). Runs after damage so HP is current.
        if (netDmg > 0)
            TryApplyLimbChop(sim, attackerIdx, defenderIdx, hitLoc, wType, netDmg);

        // On-hit: knockdown check if the SPECIFIC weapon used has the Knockdown bonus.
        // (Reading per-weapon means a wolf's Pounce can carry Knockdown without its
        // Bite also triggering it.) Triggers on any successful hit (including shield-
        // blocked hits — a block is not a full dodge), constrained to targets of size
        // ≤ attacker.size + 1.
        if (weaponKnockdown && units[defenderIdx].Alive)
            TryApplyKnockdownOnHit(sim, attackerIdx, defenderIdx);

        // On-hit weapon bonus effects (potion coats + table-crafted bonuses).
        if (units[defenderIdx].Alive)
        {
            // Per-unit weapon bonus effects (potion weapon coats, table-crafted
            // permanent buffs — all expressed as WeaponBonusEffect entries).
            // Each effect runs independently and uses DamageSystem.Apply with its own
            // DamageType/Flags — that path does not re-enter this block, so an
            // effect like "5 poison on hit" cannot recurse and stack on itself.
            ApplyBonusEffectsOnHit(sim, attackerIdx, defenderIdx);
        }

        return true;
    }

    // ===================== On-hit riders =====================

    /// <summary>
    /// Iterate the attacker's per-unit BonusEffects list (lazy-allocated; null=empty)
    /// at the moment a melee hit lands and the defender is still alive. Roll any
    /// chance-gated entries; apply BonusDamage via DamageSystem.Apply; set
    /// ZombieOnDeath on the defender for ZombieOnDeath rolls that succeed.
    /// </summary>
    private static void ApplyBonusEffectsOnHit(Simulation sim, int attackerIdx, int defenderIdx)
    {
        var units = sim.UnitsMut;
        var effects = units[attackerIdx].BonusEffects;
        if (effects == null || effects.Count == 0) return;

        for (int i = 0; i < effects.Count; i++)
        {
            var e = effects[i];

            // Chance roll. 0 or 100 → always (0 default treated as "always" — explicit
            // BonusEffect.Damage() / ZombieOnDeath() factories set ChancePct=100).
            // Rolled on the shared combat RNG — Random.Shared is banned on sim paths.
            if (e.ChancePct > 0 && e.ChancePct < 100)
            {
                int roll = UnitUtil.RollPercent();
                if (roll >= e.ChancePct) continue;
            }

            switch (e.Kind)
            {
                case BonusEffectKind.BonusDamage:
                    if (e.Amount > 0 && units[defenderIdx].Alive)
                    {
                        DebugLog.Log("table",
                            $"[BonusEffect] attacker#{attackerIdx} ({units[attackerIdx].UnitDefID}) → " +
                            $"defender#{defenderIdx} ({units[defenderIdx].UnitDefID}) : " +
                            $"{e.Amount} {e.DmgType} dmg flags={e.DmgFlags}");
                        DamageSystem.Apply(units, defenderIdx, e.Amount,
                            e.DmgType, e.DmgFlags, sim.DamageEventsMut, attackerIdx);
                    }
                    break;

                case BonusEffectKind.ZombieOnDeath:
                    DebugLog.Log("table",
                        $"[BonusEffect] attacker#{attackerIdx} ({units[attackerIdx].UnitDefID}) → " +
                        $"defender#{defenderIdx} ({units[defenderIdx].UnitDefID}) : ZombieOnDeath set (chance={e.ChancePct}%)");
                    units[defenderIdx].ZombieOnDeath = true;
                    break;
            }
        }
    }

    // --- Knockdown (weapon bonus) ---

    internal const float KnockdownCheckInitialDelay = 2.0f;
    internal const float KnockdownCheckInterval = 1.0f; // recovery re-roll cadence (Simulation.UpdateKnockdownRecovery)
    internal const int KnockdownMinDuration = 3;        // seconds on a successful check

    /// <summary>
    /// Attempt a knockdown on the defender. Called after a successful melee hit
    /// when the attacker's weapon has the Knockdown bonus. Size-gated (target size
    /// must be ≤ attacker.size + 1), then an opposed STR + Size×2 + DRN roll;
    /// ties go to the attacker. On success, applies buff_knockdown with duration
    /// equal to the roll difference (min KnockdownMinDuration seconds).
    /// </summary>
    private static void TryApplyKnockdownOnHit(Simulation sim, int attackerIdx, int defenderIdx)
    {
        var units = sim.UnitsMut;
        if (units[defenderIdx].Size > units[attackerIdx].Size + 1) return; // too big to knock down
        // Chargers are immune to knockdown mid-charge — a stray hit from a
        // smaller victim shouldn't cancel the commit. Still applies on transit
        // trample hits the OTHER way (charger knocking down victims), just not
        // FROM victims TO the charger. Both active charge (1) and follow-through (3).
        if (units[defenderIdx].ChargePhase == 1 || units[defenderIdx].ChargePhase == 3) return;

        int atkStr = (int)BuffSystem.GetModifiedStat(units, attackerIdx,
            BuffStat.Strength, units[attackerIdx].Stats.Strength);
        int defStr = (int)BuffSystem.GetModifiedStat(units, defenderIdx,
            BuffStat.Strength, units[defenderIdx].Stats.Strength);
        int atkScore = atkStr
                     + units[attackerIdx].Size * 2
                     + UnitUtil.RollDRN(units[attackerIdx].Stats.Drn);
        int defScore = defStr
                     + units[defenderIdx].Size * 2
                     + UnitUtil.RollDRN(units[defenderIdx].Stats.Drn);

        int diff = atkScore - defScore;
        if (diff < 0) return; // defender won (ties go to attacker)

        int durationSec = Math.Max(KnockdownMinDuration, diff);
        var knockdownBuff = sim.GameData.Buffs.Get("buff_knockdown");
        if (knockdownBuff == null) return;

        BuffSystem.ApplyBuffWithDuration(units, defenderIdx, knockdownBuff, durationSec);
        units[defenderIdx].KnockdownCheckTimer = KnockdownCheckInitialDelay;

        DebugLog.Log("combat", $"[Knockdown] atk#{attackerIdx}(str={atkStr}" +
            $",sz={units[attackerIdx].Size},score={atkScore}) vs def#{defenderIdx}" +
            $"(str={defStr},sz={units[defenderIdx].Size},score={defScore})" +
            $" → diff={diff} duration={durationSec}s");
    }

    /// <summary>Fraction of max HP a single slashing limb/head hit must cost to
    /// sever that part (manual p.60). Tunable — with low-HP/high-damage units this
    /// triggers often; raise it if dismemberment feels too frequent.</summary>
    private const float LimbChopHpFraction = 0.5f;

    /// <summary>
    /// Slashing limb/head dismemberment (manual p.60). A slashing hit to an arm,
    /// leg, or head that costs >= 50% of max HP severs that part: arms/legs become
    /// permanent afflictions (stat penalties), a head is severed → instant death.
    /// </summary>
    private static void TryApplyLimbChop(Simulation sim, int attackerIdx, int defenderIdx,
        HitLocation loc, WeaponDamageType wType, int netDmg)
    {
        var units = sim.UnitsMut;
        if (wType != WeaponDamageType.Slashing) return;
        if (!units[defenderIdx].Alive) return; // already killed by the HP loss
        int threshold = Math.Max(1, (int)(BuffSystem.EffectiveMaxHP(units, defenderIdx) * LimbChopHpFraction));
        if (netDmg < threshold) return;

        switch (loc)
        {
            case HitLocation.Head:
                // Decapitation — lethal. Kill handles HP=0/Alive/Death anim +
                // prone-snap (a knocked-down unit is easier to hit, so prone
                // decapitation is a live case).
                DamageSystem.Kill(units, defenderIdx);
                DebugLog.Log("combat", $"[Decapitate] unit#{defenderIdx} ({units[defenderIdx].UnitDefID}) beheaded by " +
                    $"unit#{attackerIdx} ({units[attackerIdx].UnitDefID}) — netDmg={netDmg} >= {threshold}");
                break;
            case HitLocation.Arms:
                ApplyAffliction(units, defenderIdx, Affliction.LostArm);
                break;
            case HitLocation.Legs:
                ApplyAffliction(units, defenderIdx, Affliction.LostLeg);
                break;
        }
    }

    /// <summary>
    /// Apply a permanent affliction, baking its stat penalty straight into the
    /// unit's Stats (battle wounds persist for the fight). Each affliction applies
    /// at most once per unit.
    /// </summary>
    private static void ApplyAffliction(UnitArrays units, int idx, Affliction a)
    {
        if ((units[idx].Afflictions & a) != 0) return; // already maimed there
        units[idx].Afflictions |= a;
        var s = units[idx].Stats;
        switch (a)
        {
            case Affliction.LostArm:
                s.Attack = Math.Max(0, s.Attack - 4);
                s.Strength = Math.Max(0, s.Strength - 2);
                break;
            case Affliction.LostLeg:
                s.Defense = Math.Max(0, s.Defense - 4);
                s.CombatSpeed *= 0.6f;
                break;
            case Affliction.LostEye:
                s.Attack = Math.Max(0, s.Attack - 2);
                break;
        }
        DebugLog.Log("combat", $"[Affliction] unit#{idx} ({units[idx].UnitDefID}) suffered {a} " +
            $"→ Att={s.Attack} Def={s.Defense} Str={s.Strength} Spd={s.CombatSpeed:F1}");
    }

    // ===================== Projectile impacts =====================

    /// <summary>
    /// Resolve one combat projectile impact (arrow, spell projectile, or dev
    /// fireball) from the ProjectileManager hit list. Handles knockback physics
    /// BEFORE damage (so corpses inherit the arc), then dispatches:
    ///   - precision projectiles (plain arrows AND aimed spell projectiles) →
    ///     the Dominions-style shield/hit-location resolution (ResolveArrowHit),
    ///   - non-precision spell projectiles → the standard spell pipeline
    ///     (ApplySpellDamage: MR gate + opposed damage-vs-prot roll + AN/AP flags),
    ///   - no spell record → flat dev damage.
    /// The spell def, damage flags, and caster DRN tier are all derived HERE from
    /// the hit — callers just forward the ProjectileHit. Potion projectiles are
    /// NOT combat and stay with PotionSystem (Simulation.Tick).
    /// </summary>
    public static void ResolveProjectileImpact(Simulation sim, ProjectileHit hit)
    {
        var units = sim.UnitsMut;
        var spellDef = (sim.GameData != null && !string.IsNullOrEmpty(hit.SpellID))
            ? sim.GameData.Spells.Get(hit.SpellID) : null;

        // Physics knockback before damage — units enter physics first so if
        // the damage kills them, the corpse inherits the knockback arc
        if (spellDef != null && spellDef.KnockbackForce > 0f)
        {
            float kbRadius = spellDef.KnockbackRadius > 0f ? spellDef.KnockbackRadius : hit.AoeRadius;
            sim.Physics.ApplyRadialImpulse(units, hit.ImpactPos, kbRadius,
                spellDef.KnockbackForce, spellDef.KnockbackUpward);
        }

        // Directional impact: shove the struck unit along the projectile's flight
        // path (per-hit, unlike the radial knockback above). Also before damage so
        // a killed unit's corpse inherits the arc.
        if (hit.ImpactForce > 0f && hit.UnitIdx >= 0 && hit.UnitIdx < units.Count)
            sim.Physics.ApplyImpulse(units, hit.UnitIdx, hit.FlightDir,
                hit.ImpactForce, hit.ImpactUpward);

        if (hit.UnitIdx < 0 || hit.UnitIdx >= units.Count || !units[hit.UnitIdx].Alive)
            return;

        // Resolve the casting unit so the hit is attributed to them
        // (LastAttackerID) — drives flee/aggro reactions and the skill-book
        // kill tally (monster_kill / human_kill), exactly like a melee blow.
        int casterIdx = UnitUtil.ResolveUnitIndex(units, hit.OwnerID);

        if (hit.ProjectileType == ProjectileType.RegularHit && hit.Precision > 0)
        {
            ResolveArrowHit(sim, hit, spellDef);
        }
        else if (spellDef != null)
        {
            ApplySpellDamage(sim, hit.UnitIdx, hit.Damage, spellDef, casterIdx);
        }
        else
        {
            // No spellDef: a dev fireball (flat damage, no spell record).
            sim.CombatLog.AddEntry(new CombatLogEntry
            {
                Outcome = CombatLogOutcome.NoteOnly,
                Timestamp = sim.GameTime,
                Note = $"Unattributed projectile hit {sim.GetUnitDisplayName(hit.UnitIdx)} ({hit.Damage} dmg)",
            });
            sim.DealDamage(hit.UnitIdx, hit.Damage, casterIdx); // dev fireball: flat
        }
    }

    /// <summary>
    /// Ranged hit resolution — deliberately simpler than melee (and than Dominions):
    /// physics already decided the arrow touched the hitbox, so the only save left is
    /// the shield. Opposed roll: Precision (+2 magic weapon) + tier-4 DRN vs
    /// ShieldParry×2 + tier-4 DRN. Defense/harassment/fatigue play no part — those
    /// matter in melee. A connecting arrow is then mitigated by hit-location armor
    /// (head vs body, rolled from the projectile's arc in ProjectileManager: plunging
    /// lobs strike the head half the time, flat shots rarely) + natural protection.
    /// Arrows count as piercing (15% armor reduction), and damage is weapon-only —
    /// no Strength behind a bowshot. Damage flags and the caster's roll tier derive
    /// from <paramref name="spellDef"/> (null for plain arrows).
    /// </summary>
    private static void ResolveArrowHit(Simulation sim, ProjectileHit hit, SpellDef? spellDef)
    {
        var units = sim.UnitsMut;
        int defenderIdx = hit.UnitIdx;
        var defStats = units[defenderIdx].Stats;
        // Shooter may have died while the arrow was in flight (attackerIdx == -1).
        int attackerIdx = UnitUtil.ResolveUnitIndex(units, hit.OwnerID);

        DamageFlags flags = spellDef != null ? SpellEffectSystem.SpellDamageFlags(spellDef) : default;
        // Aimed spell projectiles roll on the spell's caster tier; plain arrows on
        // the shooter's own tier (fallback 2 when the shooter is gone).
        int atkDrn = spellDef != null
            ? SpellPenetration.CasterRollTier(spellDef, units, attackerIdx)
            : (attackerIdx >= 0 && attackerIdx < units.Count ? units[attackerIdx].Stats.Drn : 2);

        // Hit resolution rolls tier-4 dice for both sides (same rule as melee); the
        // damage/prot rolls below keep each unit's own Drn tier. Roll order preserved
        // (shared RNG in RollDRN): attack, defense, then — on a hit — protection and
        // damage. Split out so each roll's DRN component can be recorded in the log.
        int atkDrnRoll = UnitUtil.RollDRN(4);
        int defShield = defStats.ShieldParry * 2;
        int defBase = defShield;
        int defDrnRoll = UnitUtil.RollDRN(4);
        // Precision is guaranteed > 0 here (the RegularHit dispatch gates on it).
        int attackBase = hit.Precision + (flags.HasFlag(DamageFlags.MagicWeapon) ? 2 : 0);
        int atkRoll = attackBase + atkDrnRoll;
        int defRoll = defBase + defDrnRoll;

        // Ranged impacts reuse the melee combat-log schema so they appear in the same
        // panel. Precision stands in for the attack stat; an arrow has no shield-parry
        // stage beyond the defense roll, so DamageBase/Prot map straight across.
        var logEntry = new CombatLogEntry
        {
            Timestamp = sim.GameTime,
            AttackerName = sim.GetUnitDisplayName(attackerIdx),
            DefenderName = sim.GetUnitDisplayName(defenderIdx),
            AttackerFaction = attackerIdx >= 0 && attackerIdx < units.Count ? FactionChar(units, attackerIdx) : '?',
            DefenderFaction = FactionChar(units, defenderIdx),
            WeaponName = hit.WeaponName,
            AttackBase = attackBase,
            AttackDRN = atkDrnRoll,
            DefenseBase = defBase,
            DefenseDRN = defDrnRoll,
            DamageFlags = flags,
        };

        if (atkRoll < defRoll)
        {
            if (atkRoll + defShield < defRoll)
            {
                logEntry.Outcome = CombatLogOutcome.Miss;
                sim.CombatLog.AddEntry(logEntry);
                return;
            }
            logEntry.Outcome = CombatLogOutcome.Blocked;
        }
        else
        {
            logEntry.Outcome = CombatLogOutcome.Hit;
        }

        // Arrows count as piercing: 15% off armor AND toughness — shield included
        // (the melee formula reduces shield protection by the piercing fraction too;
        // this path used to exempt the shield). ArmorNegating follows the shared AN
        // convention (melee + spells): zero the protection VALUE and toughness but
        // keep the opposed rolls — the old code skipped even the protection roll
        // while logging mitigation numbers that were never applied.
        int armorProt = hit.HitLocation == HitLocation.Head
            ? defStats.Armor.HeadProtection : defStats.Armor.BodyProtection;
        float protStat = armorProt
            + (logEntry.Outcome == CombatLogOutcome.Blocked ? defStats.ShieldProtection : 0);
        float toughness = BuffSystem.GetModifiedStat(units, defenderIdx,
            BuffStat.Toughness, defStats.Toughness);
        if (flags.HasFlag(DamageFlags.ArmorNegating))
        {
            protStat = 0f;
            toughness = 0f;
        }
        else
        {
            protStat *= 0.85f;
            toughness *= 0.85f;
        }
        int protDrnRoll = UnitUtil.RollDRN(defStats.Drn);
        int prot = (int)protStat + protDrnRoll;
        int dmgDrnRoll = UnitUtil.RollDRN(atkDrn);
        int postArmor = hit.Damage + dmgDrnRoll - prot;
        int netDmg = DamageSystem.MitigateByToughness(postArmor, toughness);
        int toughnessMit = Math.Max(0, postArmor) - netDmg;
        if (netDmg < 0) netDmg = 0;

        logEntry.HitLoc = hit.HitLocation;
        logEntry.HitLocationName = hit.HitLocation.ToString();
        logEntry.DamageBase = hit.Damage;
        logEntry.DamageDRN = dmgDrnRoll;
        logEntry.ProtBase = (int)protStat;
        logEntry.ProtDRN = protDrnRoll;
        logEntry.ToughnessMit = toughnessMit;
        logEntry.NetDamage = netDmg;
        sim.CombatLog.AddEntry(logEntry);

        if (netDmg > 0)
            sim.DealDamage(defenderIdx, netDmg, attackerIdx);
    }

    // ===================== Spell damage =====================

    /// <summary>
    /// Standard SPELL damage entry point — every magical damage proc (projectile
    /// impact, zap, strike AoE, beam/drain tick, cloud burst/tick, glyph trap)
    /// funnels through here so all spell damage shares one resolution:
    ///   1. MR gate — opposed DRN roll, only when the spell ChecksMagicResist; a
    ///      resisted hit/tick does nothing (logged as a resist note).
    ///   2. Damage roll, mirroring the melee formula minus its weapon concepts
    ///      (hit locations, shields, weapon types): damage + caster DRN vs
    ///      BodyProtection + target DRN, remainder halved by Toughness. Spells
    ///      auto-hit, so DefenseNegating plays no part here. ArmorNegating zeroes
    ///      the protection VALUE and toughness but keeps the opposed rolls (melee
    ///      AN convention); ArmorPiercing halves both instead, rolls untouched.
    ///   3. Poison type: the rolled net amount becomes poison STACKS (the roll IS
    ///      the application; the flat 3s DoT ticks that later convert stacks to
    ///      HP stay outside the formula by design). Physical: net HP damage.
    /// Null spell = no MR gate, no flags — a generic rolled hit where armor
    /// counts in full (glyph trap flat damage). Exact/flat sources (dev tools,
    /// reversed-drain self-cost, sacrifice kills) use Simulation.DealDamage directly.
    /// Owns the combat-log entry (full roll breakdown — spell damage used to log
    /// at most a NoteOnly line at one call site, so lightning/cloud/glyph procs
    /// were invisible in the log).
    /// </summary>
    public static void ApplySpellDamage(Simulation sim, int targetIdx, int damage,
        SpellDef? spell, int casterIdx, DamageType type = DamageType.Physical)
    {
        var units = sim.UnitsMut;
        if (targetIdx < 0 || targetIdx >= units.Count || !units[targetIdx].Alive) return;
        // Base 0 still resolves: the opposed DRN roll below can land damage on its
        // own (a 0-damage armor-negating drain chips roughly half its ticks —
        // the Dominions rule). Negative = explicitly no damage component.
        if (damage < 0) return;

        if (spell != null && !SpellPenetration.Affects(sim.GameData, units, casterIdx, targetIdx, spell))
        {
            // MR won — surface it (a resisted proc used to vanish without a trace).
            sim.CombatLog.AddEntry(new CombatLogEntry
            {
                Outcome = CombatLogOutcome.NoteOnly,
                Timestamp = sim.GameTime,
                Note = $"{spell.DisplayName}: {sim.GetUnitDisplayName(targetIdx)} resists (MR)",
            });
            return;
        }

        bool an = spell?.ArmorNegating ?? false;
        bool ap = spell?.ArmorPiercing ?? false;

        // Caster-side tier honors the spell's drn override (SpellDef.Drn > 0).
        int casterDrn = SpellPenetration.CasterRollTier(spell, units, casterIdx);
        int dmgDRN = UnitUtil.RollDRN(casterDrn);
        int dmgRoll = damage + dmgDRN;
        int protDRN = UnitUtil.RollDRN(units[targetIdx].Stats.Drn);

        float protStat = an ? 0f : units[targetIdx].Stats.Armor.BodyProtection;
        float toughness = an ? 0f : BuffSystem.GetModifiedStat(units, targetIdx,
            BuffStat.Toughness, units[targetIdx].Stats.Toughness);
        if (!an && ap) { protStat *= 0.5f; toughness *= 0.5f; }

        int postArmor = dmgRoll - ((int)protStat + protDRN);
        int netDmg = DamageSystem.MitigateByToughness(postArmor, toughness);
        if (netDmg < 0) netDmg = 0; // glancing spell hits can deal zero, like melee
        int toughnessMit = Math.Max(0, postArmor) - netDmg;

        // Spells auto-hit: no attack/defense fields (CombatLog skips the roll line
        // when both are zero), but the damage-vs-prot breakdown is real.
        sim.CombatLog.AddEntry(new CombatLogEntry
        {
            Timestamp = sim.GameTime,
            AttackerName = sim.GetUnitDisplayName(casterIdx),
            DefenderName = sim.GetUnitDisplayName(targetIdx),
            AttackerFaction = casterIdx >= 0 && casterIdx < units.Count ? FactionChar(units, casterIdx) : '?',
            DefenderFaction = FactionChar(units, targetIdx),
            WeaponName = spell?.DisplayName ?? "Magic",
            Outcome = CombatLogOutcome.Hit,
            HitLocationName = type == DamageType.Poison ? "Poison" : "",
            DamageBase = damage,
            DamageDRN = dmgDRN,
            ProtBase = (int)protStat,
            ProtDRN = protDRN,
            ToughnessMit = toughnessMit,
            NetDamage = netDmg,
            DamageFlags = spell != null ? SpellEffectSystem.SpellDamageFlags(spell) : default,
        });

        if (type == DamageType.Poison)
        {
            // Mitigation already rolled above — apply the net as stacks with
            // ArmorNegating so DamageSystem doesn't mitigate a second time.
            if (netDmg > 0)
                DamageSystem.Apply(units, targetIdx, netDmg, DamageType.Poison,
                    DamageFlags.ArmorNegating, sim.DamageEventsMut, casterIdx);
            return;
        }

        if (netDmg > 0) units[targetIdx].HitReacting = true;
        else units[targetIdx].BlockReacting = true;
        units[targetIdx].LastHitTime = sim.GameTime;
        DamageSystem.ApplyHitReactAnim(units, targetIdx);

        DamageSystem.ApplyDirect(units, targetIdx, netDmg, sim.DamageEventsMut, casterIdx);
    }

    /// <summary>Selection half of the spell-hit pattern: every living enemy of
    /// ownerFaction within the radius funnels through ApplySpellDamage. Cloud
    /// bursts, glyph traps, and any future AoE damage source use this — target
    /// picking in one place, per-unit resolution in the standard pipeline.</summary>
    public static void ApplySpellDamageAoE(Simulation sim, Vec2 center, float radius, int damage,
        SpellDef? spell, int casterIdx, Faction ownerFaction,
        DamageType type = DamageType.Physical)
    {
        if (damage <= 0) return;
        var units = sim.UnitsMut;
        var nearby = new List<uint>();
        sim.Quadtree.QueryRadiusByFaction(center, radius, FactionMaskExt.AllExcept(ownerFaction), nearby);
        foreach (uint uid in nearby)
        {
            int idx = UnitUtil.ResolveUnitIndex(units, uid);
            if (idx < 0 || !units[idx].Alive) continue;
            ApplySpellDamage(sim, idx, damage, spell, casterIdx, type);
        }
    }

    // ===================== Lightning / cloud damage events =====================

    /// <summary>
    /// Resolve one LightningSystem damage event (strike / zap / beam / drain tick).
    /// Heals apply as clamped HP gain; Flat events (reversed-drain self-cost) skip
    /// the rolls; everything else routes through the standard spell pipeline —
    /// spell def resolved HERE from the event's SpellID. Drain coupling heals the
    /// caster by a share of what ACTUALLY landed (a resisted or fully-absorbed
    /// tick drains nothing).
    /// </summary>
    public static void ResolveLightningDamage(Simulation sim, LightningDamage ld)
    {
        var units = sim.UnitsMut;
        if (ld.UnitIdx < 0 || ld.UnitIdx >= units.Count) return;
        if (ld.IsHeal)
        {
            ApplyDrainHeal(sim, ld.UnitIdx, ld.Damage);
            return;
        }
        if (ld.Flat)
        {
            // Exact-amount source (reversed-drain self-cost) — no rolls.
            sim.DealDamage(ld.UnitIdx, ld.Damage, UnitUtil.ResolveUnitIndex(units, ld.OwnerID));
            return;
        }

        int attackerIdx = UnitUtil.ResolveUnitIndex(units, ld.OwnerID);
        var ldSpell = sim.GameData != null && ld.SpellID.Length > 0
            ? sim.GameData.Spells.Get(ld.SpellID) : null;
        int hpBefore = units[ld.UnitIdx].Alive ? units[ld.UnitIdx].Stats.HP : 0;
        ApplySpellDamage(sim, ld.UnitIdx, ld.Damage, ldSpell, attackerIdx);

        // Drain coupling: the caster heals a share of what ACTUALLY landed —
        // a resisted or fully-absorbed tick drains nothing.
        if (ld.HealTargetIdx >= 0 && ld.HealPercent > 0f)
        {
            int dealt = hpBefore - Math.Max(0, units[ld.UnitIdx].Stats.HP);
            int heal = (int)MathF.Round(dealt * ld.HealPercent);
            ApplyDrainHeal(sim, ld.HealTargetIdx, heal);
        }
    }

    /// <summary>
    /// Resolve one poison-cloud tick/burst event. Paralysis procs pass the spell's
    /// MR gate per victim; damage procs run the standard spell pipeline as Poison
    /// (rolled net → stacks). Spell def and caster resolved HERE from the event.
    /// </summary>
    public static void ResolveCloudDamage(Simulation sim, CloudDamageEvent cd)
    {
        var units = sim.UnitsMut;
        if (cd.UnitIdx < 0 || cd.UnitIdx >= units.Count || !units[cd.UnitIdx].Alive) return;
        var cdSpell = sim.GameData != null && !string.IsNullOrEmpty(cd.SpellID)
            ? sim.GameData.Spells.Get(cd.SpellID) : null;
        int cdCaster = UnitUtil.ResolveUnitIndex(units, cd.CasterID);
        if (cd.Paralysis)
        {
            if (cdSpell == null || SpellPenetration.Affects(sim.GameData, units, cdCaster, cd.UnitIdx, cdSpell))
                PotionSystem.ApplyParalysis(cd.UnitIdx, units);
        }
        else
        {
            ApplySpellDamage(sim, cd.UnitIdx, cd.Damage, cdSpell, cdCaster, DamageType.Poison);
        }
    }

    /// <summary>Drain life transfer heal: clamped HP gain (sacrifice-heal
    /// convention) + a green floating number via the heal-flagged damage event.</summary>
    private static void ApplyDrainHeal(Simulation sim, int unitIdx, int amount)
    {
        var units = sim.UnitsMut;
        if (unitIdx < 0 || unitIdx >= units.Count) return;
        if (amount <= 0 || !units[unitIdx].Alive) return;
        int max = BuffSystem.EffectiveMaxHP(units, unitIdx);
        int before = units[unitIdx].Stats.HP;
        int after = Math.Min(max, before + amount);
        if (after <= before) return;
        units[unitIdx].Stats.HP = after;
        var he = DamageEvent.Create(units[unitIdx].Position, after - before);
        he.IsHeal = true;
        sim.DamageEventsMut.Add(he);
    }

    // ===================== Shared =====================

    /// <summary>Combat-log faction tag: A=undead, B=human, C=animal.</summary>
    private static char FactionChar(UnitArrays units, int idx) =>
        units[idx].Faction == Faction.Undead ? 'A'
        : units[idx].Faction == Faction.Animal ? 'C' : 'B';
}
