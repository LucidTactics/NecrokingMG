using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Necroking.Data.Registries;

/// <summary>Which horde-cap pool a permanent undead unit counts against.
/// Caps are enforced by HordeCapTracker; cosmetic-only undead / the player
/// necromancer / temporary summons should stay <see cref="None"/> so they
/// never consume a slot.</summary>
public enum UndeadCategory : byte
{
    /// <summary>Doesn't count against any cap. Default for living units, the
    /// player necromancer's evolutions, and any temporary summons we add later.</summary>
    None = 0,
    /// <summary>Undead raised from a human corpse — skeletons, abominations, etc.</summary>
    Human = 1,
    /// <summary>Undead raised from an animal/monster corpse — zombie wolves,
    /// deer, bears, boars, etc.</summary>
    Monster = 2,
}

public class SpriteRef
{
    [JsonPropertyName("atlas")] public string AtlasName { get; set; } = "";
    [JsonPropertyName("name")] public string SpriteName { get; set; } = "";
}

/// <summary>
/// A weapon slot on a unit definition: the weapon id plus optional per-unit-per-slot
/// animation override. Each slot tracks its own override — a unit that lists the same
/// weapon twice can give each copy a different anim.
///
/// JSON formats accepted on load (via UnitWeaponRefJsonConverter):
///   "weapon_id"                                  — bare string, no override
///   { "id": "weapon_id" }                        — object, no override
///   { "id": "weapon_id", "anim": "AttackBite" }  — object with override
/// On save, slots without an override are written as bare strings for a minimal diff
/// against the existing data/units.json.
/// </summary>
[JsonConverter(typeof(UnitWeaponRefJsonConverter))]
public class UnitWeaponRef
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    /// <summary>null / empty = "Default" (use WeaponDef.AnimName, else UnitDef.AttackAnim,
    /// else Attack1/Ranged1).</summary>
    [JsonPropertyName("anim")] public string? AnimOverride { get; set; }

    /// <summary>Cosmetic lunge distance (world units) — how far the unit's sprite
    /// visually translates forward toward the target at the hit frame of this
    /// weapon's attack. 0 = no lunge. Simulation position is unaffected; only
    /// Unit.RenderOffset is written, which every draw path inherits.</summary>
    [JsonPropertyName("lungeDist")] public float LungeDist { get; set; }

    public UnitWeaponRef() {}
    public UnitWeaponRef(string id) { Id = id; }
    public UnitWeaponRef(string id, string? anim) { Id = id; AnimOverride = string.IsNullOrEmpty(anim) ? null : anim; }
}

/// <summary>Accepts bare-string and object JSON forms on load; writes bare string
/// when AnimOverride is unset for minimal diff against existing data.</summary>
public class UnitWeaponRefJsonConverter : JsonConverter<UnitWeaponRef>
{
    public override UnitWeaponRef Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
            return new UnitWeaponRef(reader.GetString() ?? "");

        if (reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException($"Unexpected token {reader.TokenType} for UnitWeaponRef");

        var r = new UnitWeaponRef();
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject) return r;
            if (reader.TokenType != JsonTokenType.PropertyName) continue;
            string prop = reader.GetString() ?? "";
            reader.Read();
            switch (prop)
            {
                case "id":        r.Id = reader.GetString() ?? ""; break;
                case "anim":      r.AnimOverride = reader.TokenType == JsonTokenType.Null ? null : reader.GetString(); break;
                case "lungeDist": r.LungeDist = reader.GetSingle(); break;
                default:          reader.Skip(); break;
            }
        }
        return r;
    }

    public override void Write(Utf8JsonWriter writer, UnitWeaponRef value, JsonSerializerOptions options)
    {
        // Bare-string form only when nothing extra is set — keeps existing
        // data/units.json diff-free for unmodified slots.
        bool hasAnim = !string.IsNullOrEmpty(value.AnimOverride);
        bool hasLunge = value.LungeDist != 0f;
        if (!hasAnim && !hasLunge)
        {
            writer.WriteStringValue(value.Id);
            return;
        }
        writer.WriteStartObject();
        writer.WriteString("id", value.Id);
        if (hasAnim) writer.WriteString("anim", value.AnimOverride);
        if (hasLunge) writer.WriteNumber("lungeDist", value.LungeDist);
        writer.WriteEndObject();
    }
}

/// <summary>
/// A single weapon attachment point (hilt or tip) with position and behind-flag.
/// </summary>
public class WeaponPointData
{
    [JsonPropertyName("x")] public float X { get; set; }
    [JsonPropertyName("y")] public float Y { get; set; }
    [JsonPropertyName("behind")] public bool Behind { get; set; }
}

/// <summary>
/// Weapon point data for a single frame at a given yaw: hilt + tip positions.
/// </summary>
public class WeaponFrameData
{
    [JsonPropertyName("hilt")] public WeaponPointData Hilt { get; set; } = new();
    [JsonPropertyName("tip")] public WeaponPointData Tip { get; set; } = new();
}

/// <summary>
/// Per-animation timing override for a unit: frame durations and effect time.
/// </summary>
public class UnitAnimTimingOverride
{
    [JsonPropertyName("frameDurationsMs")] public List<int> FrameDurationsMs { get; set; } = new();
    [JsonPropertyName("effectTimeMs")] public int EffectTimeMs { get; set; } = -1;
}

public class UnitStatsJson
{
    [JsonPropertyName("maxHP")] public int MaxHP { get; set; } = 10;
    [JsonPropertyName("strength")] public int Strength { get; set; } = 10;
    [JsonPropertyName("attack")] public int Attack { get; set; } = 10;
    [JsonPropertyName("defense")] public int Defense { get; set; } = 10;
    [JsonPropertyName("magicResist")] public int MagicResist { get; set; } = 10;
    [JsonPropertyName("encumbrance")] public int Encumbrance { get; set; }
    [JsonPropertyName("naturalProt")] public int NaturalProt { get; set; }
    [JsonPropertyName("combatSpeed")] public float CombatSpeed { get; set; } = 8.0f;
}

public class UnitDef : IHasId
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string DisplayName { get; set; } = "";
    [JsonPropertyName("type")] public string UnitType { get; set; } = "Dynamic";
    [JsonPropertyName("faction")] public string Faction { get; set; } = "Undead";
    [JsonPropertyName("ai")] public string AI { get; set; } = "AttackClosest";
    [JsonPropertyName("orcaPriority")] public int OrcaPriority { get; set; }
    [JsonPropertyName("size")] public int Size { get; set; } = 2;
    [JsonPropertyName("radius")] public float Radius { get; set; } = 0.495f;
    [JsonPropertyName("spriteScale")] public float SpriteScale { get; set; } = 1.0f;
    [JsonPropertyName("spriteWorldHeight")] public float SpriteWorldHeight { get; set; } = 1.8f;

    /// <summary>Body length in world units along the unit's facing axis,
    /// used by the wading-wake system to spread particle spawn positions
    /// across the unit's silhouette instead of all clustering at the
    /// single pivot point. 0 (default) means "point source" — appropriate
    /// for humanoids whose body is roughly as wide as tall. Quadrupeds
    /// fall back to <see cref="Render.WadingWakeSystem.QuadrupedDefaultBodyLength"/>
    /// when this is 0, so most don't need an override. Set explicitly only
    /// for units with unusual proportions (very long: horses/snakes,
    /// very short: badgers, bear cubs).</summary>
    [JsonPropertyName("bodyLengthWorld")] public float BodyLengthWorld { get; set; } = 0f;
    /// <summary>Biped fallback: how far up the visible body the water cuts
    /// when this unit is wading. 0 = waterline at the feet, 1 = top of body.
    /// Default 0.35 reproduces the old hardcoded waterline. Only consulted
    /// when <see cref="IsQuadruped"/> is false AND
    /// <see cref="WadingFractionByDirection"/> is null — quadrupeds use
    /// <see cref="Render.WadingDefaults.QuadrupedBottom"/> instead.</summary>
    [JsonPropertyName("wadingWaterlineFraction")] public float WadingWaterlineFraction { get; set; } = 0.35f;

    /// <summary>Optional per-cardinal-direction bottom waterline override.
    /// LEAVE NULL for typical units — quadrupeds automatically get
    /// <see cref="Render.WadingDefaults.QuadrupedBottom"/> (wolf-tuned values
    /// that look right on most four-legged sprites), bipeds use the scalar
    /// <see cref="WadingWaterlineFraction"/>. Only set this when a specific
    /// unit's body proportions don't match the default — e.g. very tall
    /// quadrupeds like horses, or stubby ones like badgers.
    /// Smoothly interpolates between cardinals based on facing angle.</summary>
    [JsonPropertyName("wadingFractionByDirection")] public DirectionalFractions? WadingFractionByDirection { get; set; }

    /// <summary>Optional per-cardinal-direction top waterline override (for
    /// "back submerged" poses). LEAVE NULL for typical units — quadrupeds
    /// automatically get <see cref="Render.WadingDefaults.QuadrupedTop"/>
    /// (hides the wolf's back when facing the camera), bipeds get no top
    /// cut. Set explicitly only if the unit needs a different profile, or
    /// set to all zeros to disable the default top cut on a quadruped.</summary>
    [JsonPropertyName("wadingTopFractionByDirection")] public DirectionalFractions? WadingTopFractionByDirection { get; set; }
    [JsonPropertyName("color")] public ColorJson? Color { get; set; }
    [JsonPropertyName("sprite")] public SpriteRef? Sprite { get; set; }
    [JsonPropertyName("stats")] public UnitStatsJson? Stats { get; set; }
    [JsonPropertyName("zombieTypeID")] public string ZombieTypeID { get; set; } = "";
    [JsonPropertyName("spellID")] public string SpellID { get; set; } = "";

    /// <summary>Which horde-cap pool this unit consumes if spawned as a
    /// permanent undead minion. Stored as the enum's string name in JSON
    /// ("None" | "Human" | "Monster"). Living units, the player necromancer,
    /// and temporary summons should stay <see cref="UndeadCategory.None"/> so
    /// they never count against either cap. See HordeCapTracker.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    [JsonPropertyName("undeadCategory")] public UndeadCategory UndeadCategory { get; set; } = UndeadCategory.None;
    [JsonPropertyName("maxMana")] public float MaxMana { get; set; }
    [JsonPropertyName("manaRegen")] public float ManaRegen { get; set; }

    /// <summary>Marks this def as a valid form for the player necromancer.
    /// Metamorphosis evolutions only accept PlayerForm=true targets. Set in
    /// the unit editor; serialised as "playerForm" in JSON.</summary>
    [JsonPropertyName("playerForm")] public bool PlayerForm { get; set; }

    /// <summary>Per-path caster level for this unit. Sparse — only non-zero
    /// entries serialise. Keys are the lowercase MagicPath JsonId
    /// (e.g. "death", "fire"). Negative values are allowed in storage but
    /// MagicPathHelpers.Effective() treats them as zero for gameplay.</summary>
    [JsonPropertyName("paths")] public Dictionary<string, int> Paths { get; set; } = new();

    /// <summary>Convenience accessor used by gameplay code — returns the
    /// effective (clamp-to-zero) level for the given path.</summary>
    public int GetPathLevel(MagicPath p)
    {
        if (p == MagicPath.None) return 0;
        return Paths.TryGetValue(MagicPathHelpers.ToJsonId(p), out var v)
            ? MagicPathHelpers.Effective(v) : 0;
    }
    [JsonPropertyName("weapons")] public List<UnitWeaponRef> Weapons { get; set; } = new();
    [JsonPropertyName("armors")] public List<string> Armors { get; set; } = new();
    [JsonPropertyName("shields")] public List<string> Shields { get; set; } = new();

    /// <summary>Free-form classification tags ("wolf", "zombie", "monster", "humanoid", ...).
    /// Read by skill-tree intrinsic-buff matching: a skill that grants buff X "to all units
    /// tagged 'wolf' and 'zombie'" walks every UnitDef and applies the buff wherever both
    /// tags are present. Order-insensitive; comparison is case-sensitive.</summary>
    [JsonPropertyName("tags")] public List<string> Tags { get; set; } = new();

    /// <summary>True if every tag in <paramref name="required"/> is present.
    /// Empty/null required list returns true (no constraint).</summary>
    public bool HasAllTags(IEnumerable<string>? required)
    {
        if (required == null) return true;
        foreach (var t in required)
            if (!Tags.Contains(t)) return false;
        return true;
    }

    [JsonPropertyName("attackAnim")] public string? AttackAnim { get; set; }

    // Combat overrides (nullable = use global CombatSettings default)
    [JsonPropertyName("attackCooldown")] public float? AttackCooldown { get; set; }
    [JsonPropertyName("postAttackLockout")] public float? PostAttackLockout { get; set; }
    [JsonPropertyName("turnSpeed")] public float? TurnSpeed { get; set; }
    [JsonPropertyName("accelHalfTime")] public float? AccelHalfTime { get; set; }
    [JsonPropertyName("accel80Time")] public float? Accel80Time { get; set; }
    [JsonPropertyName("accelFullTime")] public float? AccelFullTime { get; set; }

    /// <summary>Forward acceleration cap (wu/s²) — how fast this unit can
    /// speed up. Lower for heavy/lumbering units, higher for nimble ones.
    /// Null = use system default from CombatSettings.</summary>
    [JsonPropertyName("maxAcceleration")] public float? MaxAcceleration { get; set; }

    /// <summary>Deceleration cap (wu/s²) — how fast this unit can brake.
    /// Typically 4-5× higher than MaxAcceleration for legged units (you can
    /// stop faster than you can start). Null = system default.</summary>
    [JsonPropertyName("maxDeceleration")] public float? MaxDeceleration { get; set; }

    /// <summary>Lateral acceleration cap (wu/s²) — how hard the unit can push
    /// sideways. Drives turn radius via r = v² / lateralAccel. Higher → tighter
    /// turns at speed. Bears: low (lumbering, wide turns). Deer: high (sharp
    /// pivots). Null = system default.</summary>
    [JsonPropertyName("maxLateralAccel")] public float? MaxLateralAccel { get; set; }

    /// <summary>
    /// Weapon attachment points: anim name -> yaw angle -> list of WeaponFrameData (one per frame).
    /// </summary>
    [JsonPropertyName("weaponPoints")]
    public Dictionary<string, Dictionary<string, List<WeaponFrameData>>> WeaponPoints { get; set; } = new();

    /// <summary>
    /// Per-animation timing overrides: anim name -> UnitAnimTimingOverride.
    /// </summary>
    [JsonPropertyName("animTimings")]
    public Dictionary<string, UnitAnimTimingOverride> AnimTimings { get; set; } = new();

    // AI behavior framework
    [JsonPropertyName("archetype")] public string Archetype { get; set; } = ""; // ArchetypeRegistry name (empty = legacy AI)

    // Awareness config (used by AwarenessSystem)
    [JsonPropertyName("detectionRange")] public float DetectionRange { get; set; }
    [JsonPropertyName("detectionBreakRange")] public float DetectionBreakRange { get; set; }
    [JsonPropertyName("alertDuration")] public float AlertDuration { get; set; } = 2f;
    [JsonPropertyName("alertEscalateRange")] public float AlertEscalateRange { get; set; }
    [JsonPropertyName("groupAlertRadius")] public float GroupAlertRadius { get; set; }

    // Locomotion animation tuning (new pixel-stride system, default ON).
    /// <summary>When true, this unit reverts to the original CombatSpeed-derived
    /// gait thresholds and clamped-Lerp playback scaling. Default false = use the
    /// pixel-stride calibration. Per-unit escape hatch for the cases where the
    /// new system mis-handles a specific sprite (long downward-pointing weapons,
    /// unusual silhouettes). See <see cref="Render.StrideCalibration"/>.</summary>
    [JsonPropertyName("legacyGaitMode")] public bool LegacyGaitMode { get; set; }

    /// <summary>Marks the unit as a four-legged creature (wolf, deer, bear,
    /// boar, etc.). When true, the locomotion profile subtracts the calibrated
    /// IdleFootSpreadPx (≈ body length, captured from Idle stance) from each
    /// gait's measured stride before computing feet-lock velocity — strips out
    /// the body-length component that a 4-legged silhouette unavoidably folds
    /// into the "stride spread" pixel measurement. Default false (biped),
    /// which leaves the measurement as-is.</summary>
    [JsonPropertyName("isQuadruped")] public bool IsQuadruped { get; set; }

    /// <summary>Per-leg duty cycle for the walk gait — what fraction of one
    /// cycle each leg spends planted on the ground. Drives the cycle-distance
    /// formula: <c>cycle_dist = stride / dutyCycle</c>. Biped walks use 0.5
    /// (each leg planted half the cycle, alternating). Quadruped lateral-
    /// sequence walks use ~0.75 (each leg planted 3/4 of the cycle, with all
    /// four legs phase-staggered). 0 falls back to the default
    /// <see cref="Render.StrideCalibration.DefaultDutyCycle"/> (0.5).
    ///
    /// In principle this varies per gait (gallops are ~0.25-0.4 due to airborne
    /// phases), but we apply one number per unit for simplicity. Tunes via the
    /// unit editor.</summary>
    [JsonPropertyName("dutyCycle")] public float DutyCycle { get; set; }

    /// <summary>Target walk-cycle time in seconds — used by the editor to
    /// compute the suggested CombatSpeed ("walk this fast and the cycle will
    /// take exactly this long while feet stay locked"). Sets aesthetic cadence:
    /// bigger creatures want longer cycles (e.g. bear 1.7s) to read as bulky;
    /// smaller creatures want shorter (e.g. wolf 0.8s). 0 = use the artist's
    /// authored cycle as the target (suggestion = grounded velocity at native
    /// cadence). Not directly used by gameplay — only by the editor's PropCS
    /// suggestion.</summary>
    [JsonPropertyName("targetWalkCycle")] public float TargetWalkCycle { get; set; }

    /// <summary>Max velocity at Hurry/Jog effort, as a multiplier of CombatSpeed.
    /// Biped default ≈ 2.0 (jog is roughly 2× walk). Quadrupeds run much faster
    /// than they walk: typical 3.0. Cheetah-class extremes up to 5+. Also drives
    /// the Walk→Jog gait threshold (midpoint between walk-max and jog-max).
    /// 0 = use system default of 2.0.</summary>
    [JsonPropertyName("jogSpeedMultiplier")] public float JogSpeedMultiplier { get; set; }

    /// <summary>Max velocity at Sprint effort, as a multiplier of CombatSpeed.
    /// Biped default ≈ 4.0 (sprint is roughly 4× walk). Quadrupeds: typical 9.0
    /// (horse, wolf, deer). Cheetah-class extremes 20-30+. Drives the Jog→Run
    /// gait threshold (midpoint between jog-max and sprint-max) AND the player
    /// necromancer's shift-sprint cap. 0 = use system default of 4.0.</summary>
    [JsonPropertyName("sprintSpeedMultiplier")] public float SprintSpeedMultiplier { get; set; }

    /// <summary>Hand-tuned override for the Walk feet-lock velocity (world units /
    /// sec). When set, replaces the pixel-stride-computed value at runtime. Null =
    /// use the auto-computed value. Persisted as "animWalkVelOverride" in JSON;
    /// edited in the unit editor.</summary>
    [JsonPropertyName("animWalkVelOverride")] public float? AnimWalkVelOverride { get; set; }

    /// <summary>Hand-tuned override for the Jog feet-lock velocity. Same semantics
    /// as <see cref="AnimWalkVelOverride"/>.</summary>
    [JsonPropertyName("animJogVelOverride")] public float? AnimJogVelOverride { get; set; }

    /// <summary>Hand-tuned override for the Run feet-lock velocity. Same semantics
    /// as <see cref="AnimWalkVelOverride"/>.</summary>
    [JsonPropertyName("animRunVelOverride")] public float? AnimRunVelOverride { get; set; }

    /// <summary>Runtime-only — resolved sprite-data reference (atlas → sprite name
    /// lookup), wired up by Game1 once both registries and atlases are loaded.
    /// Lets <see cref="Render.LocomotionProfile.FromUnit"/> reach calibration data
    /// without separately plumbing atlas access through the AI / render call sites.
    /// Marked [JsonIgnore] so editor-save passes don't try to serialize it.</summary>
    [JsonIgnore]
    public Render.UnitSpriteData? SpriteData { get; set; }
}

/// <summary>Four values keyed by cardinal compass direction. Used by per-
/// direction wading config: lerps between the two nearest cardinals based on
/// the unit's facing angle so transitions are smooth.</summary>
public class DirectionalFractions
{
    [JsonPropertyName("n")] public float N { get; set; }
    [JsonPropertyName("e")] public float E { get; set; }
    [JsonPropertyName("s")] public float S { get; set; }
    [JsonPropertyName("w")] public float W { get; set; }

    /// <summary>Interpolate the four values by facing angle. Necroking
    /// convention: 0° = E, 90° = S, 180° = W, 270° = N. Linear blend between
    /// the two nearest cardinals.</summary>
    public float Sample(float facingDeg)
    {
        // Normalize to [0, 360).
        float a = facingDeg % 360f;
        if (a < 0f) a += 360f;

        // Quartile [0, 4): 0=E, 1=S, 2=W, 3=N.
        float q = a / 90f;
        int lo = (int)MathF.Floor(q) % 4;
        int hi = (lo + 1) % 4;
        float t = q - MathF.Floor(q);

        // Lookup table in quartile order.
        float vLo = lo switch { 0 => E, 1 => S, 2 => W, 3 => N, _ => E };
        float vHi = hi switch { 0 => E, 1 => S, 2 => W, 3 => N, _ => E };
        return Microsoft.Xna.Framework.MathHelper.Lerp(vLo, vHi, t);
    }
}

public class UnitRegistry : RegistryBase<UnitDef>
{
    protected override string RootKey => "units";

    public int CountUnitsWithWeapon(string weaponID)
    {
        int count = 0;
        foreach (var def in _defs.Values)
            foreach (var w in def.Weapons)
                if (w.Id == weaponID) { count++; break; }
        return count;
    }

    public int CountUnitsWithArmor(string armorID)
    {
        int count = 0;
        foreach (var def in _defs.Values)
            if (def.Armors.Contains(armorID)) count++;
        return count;
    }

    public int CountUnitsWithShield(string shieldID)
    {
        int count = 0;
        foreach (var def in _defs.Values)
            if (def.Shields.Contains(shieldID)) count++;
        return count;
    }

    public void RemoveWeaponFromAll(string weaponID)
    {
        foreach (var def in _defs.Values)
            def.Weapons.RemoveAll(w => w.Id == weaponID);
    }

    public void RemoveArmorFromAll(string armorID)
    {
        foreach (var def in _defs.Values)
            def.Armors.Remove(armorID);
    }

    public void RemoveShieldFromAll(string shieldID)
    {
        foreach (var def in _defs.Values)
            def.Shields.Remove(shieldID);
    }

    /// <summary>
    /// Resolve a unit's equipment into final combat stats.
    /// Looks up weapons, armor, shields from registries and aggregates.
    /// </summary>
    public UnitStats BuildStats(string id, WeaponRegistry weapons, ArmorRegistry armors, ShieldRegistry shields)
    {
        var def = Get(id);
        if (def == null) return new UnitStats();

        var stats = def.Stats ?? new UnitStatsJson();
        var s = new UnitStats
        {
            MaxHP = stats.MaxHP,
            HP = stats.MaxHP,
            Strength = stats.Strength,
            Attack = stats.Attack,
            Defense = stats.Defense,
            MagicResist = stats.MagicResist,
            Encumbrance = stats.Encumbrance,
            NaturalProt = stats.NaturalProt,
            CombatSpeed = stats.CombatSpeed
        };

        // Resolve weapons. Each weapon slot can carry a per-unit-per-slot anim override
        // that wins over the WeaponDef's anim field. Fallback chain at resolve time:
        //   slot.AnimOverride  (new, per-unit-per-slot)
        //   → WeaponDef.AnimName  (weapon-level default, data/weapons.json "anim")
        //   → null (consumer falls through to UnitDef.AttackAnim or Attack1/Ranged1)
        foreach (var slot in def.Weapons)
        {
            var w = weapons.Get(slot.Id);
            if (w == null) continue;

            string? resolvedAnim = !string.IsNullOrEmpty(slot.AnimOverride)
                ? slot.AnimOverride
                : w.AnimName;

            var ws = new WeaponStats
            {
                Damage = w.Damage,
                AttackBonus = w.AttackBonus,
                DefenseBonus = w.DefenseBonus,
                Length = w.Length,
                Name = w.DisplayName,
                IsRanged = w.IsRanged,
                AnimName = resolvedAnim,
                CooldownRounds = w.CooldownRounds,
                Priority = w.Priority,
                LungeDist = slot.LungeDist,
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
            };
            if (System.Enum.TryParse<WeaponArchetype>(w.Archetype, true, out var arch))
                ws.Archetype = arch;

            if (w.IsRanged)
            {
                s.RangedWeapons.Add(ws);
                s.RangedRange.Add(w.Range);
                s.RangedDirectRange.Add(w.DirectRange);
                s.RangedCooldownTime.Add(w.Cooldown);
                s.RangedDmg.Add(w.RangedDamage);
            }
            else
            {
                s.MeleeWeapons.Add(ws);
            }

            foreach (var b in w.Bonuses)
            {
                // Aggregate on UnitStats for legacy consumers + write per-weapon on WeaponStats.
                if (b == "ArmorPiercing") { s.HasArmorPiercing = true; ws.HasArmorPiercing = true; }
                if (b == "ArmorNegating") { s.HasArmorNegating = true; ws.HasArmorNegating = true; }
                if (b == "Knockdown")     { s.HasKnockdown     = true; ws.HasKnockdown     = true; }
            }
        }

        // Stable-sort weapons by Priority descending so Simulation.UpdateCombat's
        // weapon scan picks higher-priority attacks first. LINQ OrderByDescending is
        // stable — equal Priority preserves the original list order (ties break by
        // weapon-list order, same as before the Priority field existed).
        if (s.MeleeWeapons.Count > 1)
            s.MeleeWeapons = System.Linq.Enumerable.ToList(
                System.Linq.Enumerable.OrderByDescending(s.MeleeWeapons, w => w.Priority));
        if (s.RangedWeapons.Count > 1)
            s.RangedWeapons = System.Linq.Enumerable.ToList(
                System.Linq.Enumerable.OrderByDescending(s.RangedWeapons, w => w.Priority));

        // Backward compat: populate primary weapon from first melee
        if (s.MeleeWeapons.Count > 0)
        {
            s.Damage = s.MeleeWeapons[0].Damage;
            s.Length = s.MeleeWeapons[0].Length;
        }

        // Resolve armor
        foreach (var aid in def.Armors)
        {
            var a = armors.Get(aid);
            if (a == null) continue;
            s.Armor.BodyProtection += a.BodyProtection;
            s.Armor.HeadProtection += a.HeadProtection;

            foreach (var b in a.Bonuses)
            {
                if (b == "TrueArmor") s.HasTrueArmor = true;
                if (b == "Barbed") s.HasBarbed = true;
            }
        }

        // Resolve shields — take best
        foreach (var sid in def.Shields)
        {
            var sh = shields.Get(sid);
            if (sh == null) continue;
            if (sh.Parry > s.ShieldParry)
            {
                s.ShieldProtection = sh.Protection;
                s.ShieldParry = sh.Parry;
                s.ShieldDefense = sh.Defense;
            }
        }

        return s;
    }

    /// <summary>
    /// Load weapon_points.json and populate WeaponPoints on each UnitDef.
    /// The file uses flat keys: hx, hy, hb, tx, ty, tb per frame.
    /// </summary>
    public bool LoadWeaponPoints(string path)
    {
        if (!File.Exists(path)) return true; // not an error if file doesn't exist

        try
        {
            string json = File.ReadAllText(path);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("units", out var arr) || arr.ValueKind != JsonValueKind.Array)
                return true;

            foreach (var uobj in arr.EnumerateArray())
            {
                string uid = uobj.TryGetProperty("unit", out var uidProp) ? uidProp.GetString() ?? "" : "";
                if (string.IsNullOrEmpty(uid)) continue;
                var def = Get(uid);
                if (def == null) continue;

                if (!uobj.TryGetProperty("anims", out var anims) || anims.ValueKind != JsonValueKind.Object)
                    continue;

                foreach (var animProp in anims.EnumerateObject())
                {
                    string animName = animProp.Name;
                    if (animProp.Value.ValueKind != JsonValueKind.Object) continue;

                    var yawDict = new Dictionary<string, List<WeaponFrameData>>();
                    foreach (var yawProp in animProp.Value.EnumerateObject())
                    {
                        string yawKey = yawProp.Name;
                        if (yawProp.Value.ValueKind != JsonValueKind.Array) continue;

                        var frameList = new List<WeaponFrameData>();
                        foreach (var f in yawProp.Value.EnumerateArray())
                        {
                            var fd = new WeaponFrameData
                            {
                                Hilt = new WeaponPointData
                                {
                                    X = f.TryGetProperty("hx", out var hx) ? hx.GetSingle() : 0f,
                                    Y = f.TryGetProperty("hy", out var hy) ? hy.GetSingle() : 0f,
                                    Behind = f.TryGetProperty("hb", out var hb) && hb.GetBoolean()
                                },
                                Tip = new WeaponPointData
                                {
                                    X = f.TryGetProperty("tx", out var tx) ? tx.GetSingle() : 0f,
                                    Y = f.TryGetProperty("ty", out var ty) ? ty.GetSingle() : 0f,
                                    Behind = f.TryGetProperty("tb", out var tb) && tb.GetBoolean()
                                }
                            };
                            frameList.Add(fd);
                        }
                        yawDict[yawKey] = frameList;
                    }
                    def.WeaponPoints[animName] = yawDict;
                }
            }
            return true;
        }
        catch (Exception ex) { Core.DebugLog.Log("error", $"Failed to load weapon points {path}: {ex.Message}"); return false; }
    }

    /// <summary>
    /// Save weapon_points.json from each UnitDef's WeaponPoints data.
    /// Uses the flat-key format: hx, hy, hb, tx, ty, tb.
    /// </summary>
    public bool SaveWeaponPoints(string path)
    {
        try
        {
            var unitArr = new List<object>();
            foreach (var id in _orderedIDs)
            {
                if (!_defs.TryGetValue(id, out var def)) continue;
                if (def.WeaponPoints.Count == 0) continue;

                var animMap = new Dictionary<string, object>();
                foreach (var (animName, yawDict) in def.WeaponPoints)
                {
                    var yawObj = new Dictionary<string, object>();
                    foreach (var (yawKey, frames) in yawDict)
                    {
                        var frameArr = new List<object>();
                        foreach (var fd in frames)
                        {
                            frameArr.Add(new Dictionary<string, object>
                            {
                                ["hx"] = fd.Hilt.X, ["hy"] = fd.Hilt.Y, ["hb"] = fd.Hilt.Behind,
                                ["tx"] = fd.Tip.X,   ["ty"] = fd.Tip.Y,   ["tb"] = fd.Tip.Behind
                            });
                        }
                        yawObj[yawKey] = frameArr;
                    }
                    animMap[animName] = yawObj;
                }

                unitArr.Add(new Dictionary<string, object>
                {
                    ["unit"] = def.Id,
                    ["anims"] = animMap
                });
            }

            var doc = new Dictionary<string, object> { ["units"] = unitArr };
            var options = new JsonSerializerOptions { WriteIndented = true };
            string json = JsonSerializer.Serialize(doc, options);
            // Atomic tmp+rename — protects against crash mid-write corruption.
            return Core.AtomicFile.WriteAllText(path, json);
        }
        catch (Exception ex) { Core.DebugLog.Log("error", $"Failed to save weapon points {path}: {ex.Message}"); return false; }
    }
}
