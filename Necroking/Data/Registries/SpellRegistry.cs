using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using Necroking.Core;
using Necroking.Editor;
using Necroking.GameSystems;

namespace Necroking.Data.Registries;

public class FlipbookRef
{
    [EditorField(Label = "Flipbook", Order = 0)]
    [EditorRegistryDropdown("Flipbooks")]
    [JsonPropertyName("flipbookID")] public string FlipbookID { get; set; } = "";

    [EditorField(Label = "FPS", Order = 1, Step = 0.1f, Decimals = 1)]
    [JsonPropertyName("fps")] public float FPS { get; set; } = -1.0f;

    [EditorField(Label = "Scale", Order = 2, Step = 0.01f, Decimals = 2)]
    [JsonPropertyName("scale")] public float Scale { get; set; } = 1.0f;

    [EditorField(Label = "Color", Order = 3, Compact = true)]
    [JsonPropertyName("color")] [JsonConverter(typeof(HdrColorJsonConverter))] public HdrColor Color { get; set; } = new(255, 255, 255, 255, 1.0f);

    [EditorField(Label = "Rotation", Order = 4, Step = 1f)]
    [JsonPropertyName("rotation")] public float Rotation { get; set; }

    [EditorField(Label = "Blend", Order = 5)]
    [EditorCombo("Alpha", "Additive")]
    [JsonPropertyName("blendMode")] public string BlendMode { get; set; } = "Alpha";

    [EditorField(Label = "Alignment", Order = 6)]
    [EditorCombo("Ground", "Upright")]
    [JsonPropertyName("alignment")] public string Alignment { get; set; } = "Ground";

    [EditorField(Label = "Duration", Order = 7, Step = 0.01f, Decimals = 2)]
    [JsonPropertyName("duration")] public float Duration { get; set; } = -1.0f;
}

public class SpellDef : IHasId
{
    // ============ Top fields (ungrouped) ============
    [EditorField(Label = "Name", Order = 0)]
    [JsonPropertyName("name")] public string DisplayName { get; set; } = "";

    [EditorField(Label = "ID", Order = 1, ReadOnly = true)]
    [JsonPropertyName("id")] public string Id { get; set; } = "";

    [EditorField(Label = "Category", Order = 2)]
    [EditorCombo("Projectile", "Buff", "Debuff", "Summon", "Strike", "Beam", "Drain", "Cloud", "Sacrifice", "Blight", "WolfHunt")]
    [JsonPropertyName("category")] public string Category { get; set; } = "Projectile";

    // ===== Grimoire presentation (tab school + tile template + icon) =====
    [EditorField(Label = "School", Order = 3)]
    [EditorCombo("Conjuration", "Alteration", "Evocation", "Construction")]
    [JsonPropertyName("school")] public string School { get; set; } = "";

    [EditorField(Label = "Tile Template", Order = 4)]
    [EditorCombo("summon", "evocation", "buff", "debuff")]
    [JsonPropertyName("tileTemplate")] public string TileTemplate { get; set; } = "";

    [EditorField(Label = "Icon", Order = 5)]
    [JsonPropertyName("icon")] public string Icon { get; set; } = "";

    /// <summary>Hide from the grimoire (debug/internal spells).</summary>
    [JsonPropertyName("hidden")] public bool Hidden { get; set; }

    /// <summary>Inventory item this spell consumes on cast (e.g. a potion). When
    /// set, the spell needs &gt;=1 of the item in the inventory and a use decrements
    /// it; the spell bar shows the remaining count. Potions are surfaced as such
    /// spells (id == the potion id) so they live in the grimoire / spell bar.</summary>
    [JsonPropertyName("consumesItem")] public string ConsumesItem { get; set; } = "";

    // ============ COMMON ============
    [EditorField(Label = "Range", Group = "COMMON", Order = 100, Step = 0.1f, Decimals = 1, GroupColorR = 200, GroupColorG = 200, GroupColorB = 220)]
    [JsonPropertyName("range")] public float Range { get; set; } = 20.0f;

    [EditorField(Label = "Mana Cost", Group = "COMMON", Order = 101, Step = 0.1f, Decimals = 1)]
    [JsonPropertyName("manaCost")] public float ManaCost { get; set; } = 2.0f;

    // Path requirements. A spell may have a primary and an optional secondary.
    // Both must be met by the caster to cast; only the primary's level is used
    // when computing the cost reduction (mastery beyond the primary requirement).
    [EditorField(Label = "Primary Path",  Group = "PATHS", Order = 120, GroupColorR = 200, GroupColorG = 180, GroupColorB = 240)]
    [EditorCombo("", "metal", "shock", "fire", "water", "heavens", "earth", "chaos", "order", "spirit", "body", "nature", "death")]
    [JsonPropertyName("primaryPath")]    public string PrimaryPath    { get; set; } = "";

    [EditorField(Label = "Primary Level", Group = "PATHS", Order = 121)]
    [JsonPropertyName("primaryLevel")]   public int    PrimaryLevel   { get; set; }

    [EditorField(Label = "Secondary Path",  Group = "PATHS", Order = 122)]
    [EditorCombo("", "metal", "shock", "fire", "water", "heavens", "earth", "chaos", "order", "spirit", "body", "nature", "death")]
    [JsonPropertyName("secondaryPath")]  public string SecondaryPath  { get; set; } = "";

    [EditorField(Label = "Secondary Level", Group = "PATHS", Order = 123)]
    [JsonPropertyName("secondaryLevel")] public int    SecondaryLevel { get; set; }

    /// <summary>Strongly-typed primary path requirement, resolved from the
    /// string id. Path == None means "no primary requirement".</summary>
    public SpellPathReq GetPrimary() => new()
    {
        Path = MagicPathHelpers.FromJsonId(PrimaryPath),
        Level = PrimaryLevel,
    };

    public SpellPathReq GetSecondary() => new()
    {
        Path = MagicPathHelpers.FromJsonId(SecondaryPath),
        Level = SecondaryLevel,
    };

    /// <summary>True if the caster meets both primary and secondary path
    /// requirements (if set). Each effective caster level must be >= the
    /// spell's requirement on that path.</summary>
    public bool MeetsPathRequirements(Func<MagicPath, int> casterLevel)
    {
        var pri = GetPrimary();
        if (pri.HasRequirement && casterLevel(pri.Path) < pri.Level) return false;
        var sec = GetSecondary();
        if (sec.HasRequirement && casterLevel(sec.Path) < sec.Level) return false;
        return true;
    }

    /// <summary>Effective mana cost after primary-path mastery scaling.
    /// Formula: cost × 1 / (casterLevel − reqLevel + 1). Same-level casters get
    /// full cost (N=1); each additional level of mastery in the primary path
    /// shaves another fraction off. Secondary path requirement does not affect
    /// cost — it's a gate only. Returns ManaCost unchanged if there's no
    /// primary requirement.</summary>
    public float EffectiveManaCost(Func<MagicPath, int> casterLevel)
    {
        var pri = GetPrimary();
        if (!pri.HasRequirement) return ManaCost;
        int n = casterLevel(pri.Path) - pri.Level + 1;
        if (n <= 0) return ManaCost; // caster lacks primary; cost is moot but return base for safety
        return ManaCost / n;
    }

    [EditorField(Label = "Cooldown", Group = "COMMON", Order = 102, Step = 0.1f, Decimals = 1)]
    [JsonPropertyName("cooldown")] public float Cooldown { get; set; }

    [EditorField(Label = "Cast Time", Group = "COMMON", Order = 103, Step = 0.1f, Decimals = 1)]
    [JsonPropertyName("castTime")] public float CastTime { get; set; }

    [EditorField(Label = "Casting Buff", Group = "COMMON", Order = 104)]
    [EditorRegistryDropdown("Buffs")]
    [JsonPropertyName("castingBuffID")] public string CastingBuffID { get; set; } = "";

    // Caster animation while casting. "Spell1" (default) = the single-shot cast
    // anim (effect fires at the anim's effect frame). "ImbueGround"/"Raise" are
    // channeled Start->Loop->Finish casts (Raise has no Finish); the effect fires
    // at the END of the loop. See the channeled-cast machine in Game1.
    [EditorField(Label = "Cast Anim", Group = "COMMON", Order = 105)]
    [EditorCombo("Spell1", "ImbueGround", "Raise")]
    [JsonPropertyName("castAnim")] public string CastAnim { get; set; } = "Spell1";

    // Target Filter — only shown for Strike
    [EditorVisible("Category", "Strike")]
    [EditorField(Label = "Target Filter", Group = "COMMON", Order = 105)]
    [EditorCombo("AnyEnemy", "UndeadOnly", "LivingOnly")]
    [JsonPropertyName("targetFilter")] public string TargetFilter { get; set; } = "AnyEnemy";

    [EditorField(Label = "Armor Negating", Group = "COMMON", Order = 106)]
    [JsonPropertyName("armorNegating")] public bool ArmorNegating { get; set; }

    [EditorField(Label = "Defense Negating", Group = "COMMON", Order = 107)]
    [JsonPropertyName("defenseNegating")] public bool DefenseNegating { get; set; } = true;

    // Magic Resistance — when ChecksMagicResist is set, the spell must penetrate the
    // target's MR to affect it: (penetration + DRN) vs (MR + DRN). ResistDifficulty
    // shifts penetration like a Dominions spell difficulty: Hard +4, Easy −4.
    [EditorField(Label = "Checks Magic Resist", Group = "COMMON", Order = 108)]
    [JsonPropertyName("checksMagicResist")] public bool ChecksMagicResist { get; set; }

    [EditorVisible("ChecksMagicResist", "True")]
    [EditorField(Label = "Resist Difficulty", Group = "COMMON", Order = 109)]
    [EditorCombo("Normal", "Easy", "Hard")]
    [JsonPropertyName("resistDifficulty")] public string ResistDifficulty { get; set; } = "Normal";

    /// <summary>Penetration modifier from the easy/hard tag: Hard +4 (harder to
    /// resist), Easy −4 (easier to resist), Normal 0.</summary>
    public int ResistDifficultyMod => ResistDifficulty switch
    {
        "Hard" => 4,
        "Easy" => -4,
        _ => 0,
    };

    // Physics knockback (applied on AoE impact)
    [EditorVisible("Category", "Projectile", "Cloud", "Strike")]
    [EditorField(Label = "Knockback Force", Group = "PHYSICS", Order = 110, Step = 1f, Decimals = 0)]
    [JsonPropertyName("knockbackForce")] public float KnockbackForce { get; set; }

    [EditorVisible("Category", "Projectile", "Cloud", "Strike")]
    [EditorField(Label = "Knockback Up", Group = "PHYSICS", Order = 111, Step = 1f, Decimals = 0)]
    [JsonPropertyName("knockbackUpward")] public float KnockbackUpward { get; set; }

    [EditorVisible("Category", "Projectile", "Cloud", "Strike")]
    [EditorField(Label = "Knockback Radius", Group = "PHYSICS", Order = 112, Step = 0.5f, Decimals = 1)]
    [JsonPropertyName("knockbackRadius")] public float KnockbackRadius { get; set; }

    // Directional impact (applied only to units the projectile actually hits,
    // shoving them along the projectile's flight direction — unlike Knockback,
    // which blasts radially outward from the impact point)
    [EditorVisible("Category", "Projectile")]
    [EditorField(Label = "Impact Force", Group = "PHYSICS", Order = 113, Step = 1f, Decimals = 0)]
    [JsonPropertyName("impactForce")] public float ImpactForce { get; set; }

    [EditorVisible("Category", "Projectile")]
    [EditorField(Label = "Impact Up", Group = "PHYSICS", Order = 114, Step = 1f, Decimals = 0)]
    [JsonPropertyName("impactUpward")] public float ImpactUpward { get; set; }

    // ============ Shared fields (ungrouped, between COMMON and category sections) ============
    // AoeType — used in Projectile and Buff/Debuff
    [EditorVisible("Category", "Projectile", "Buff", "Debuff")]
    [EditorField(Label = "AOE Type", Order = 199)]
    [EditorCombo("Single", "AOE", "Chain")]
    [JsonPropertyName("aoeType")] public string AoeType { get; set; } = "Single";

    // Damage — used in Projectile, Strike, Beam, Drain
    [EditorVisible("Category", "Projectile", "Strike", "Beam", "Drain")]
    [EditorField(Label = "Damage", Order = 198)]
    [JsonPropertyName("damage")] public int Damage { get; set; }

    // AoeRadius — compound conditions per category, handled manually
    [EditorHide]
    [JsonPropertyName("aoeRadius")] public float AoeRadius { get; set; }

    // WolfHunt — how long the commanded pack-hunt stays active after the cast.
    [EditorField(Label = "Hunt Duration", Order = 250, Step = 0.5f, Decimals = 1)]
    [EditorVisible("Category", "WolfHunt")]
    [JsonPropertyName("wolfHuntDuration")] public float WolfHuntDuration { get; set; } = 0;

    // HitEffectFlipbook — shown in Projectile and Strike(!target), handled manually for Strike
    [EditorHide]
    [JsonPropertyName("hitEffectFlipbook")] public FlipbookRef? HitEffectFlipbook { get; set; }

    // ============ PROJECTILE ============
    [EditorVisible("Category", "Projectile")]
    [EditorField(Label = "Quantity", Group = "PROJECTILE", Order = 200, GroupColorR = 255, GroupColorG = 160, GroupColorB = 100)]
    [JsonPropertyName("quantity")] public int Quantity { get; set; } = 1;

    [EditorVisible("Category", "Projectile")]
    [EditorField(Label = "Trajectory", Group = "PROJECTILE", Order = 201)]
    [EditorCombo("Lob", "DirectFire", "Homing", "Swirly", "HomingSwirly")]
    [JsonPropertyName("trajectory")] public string Trajectory { get; set; } = "Lob";

    [EditorVisible("Category", "Projectile")]
    [EditorField(Label = "Proj Speed", Group = "PROJECTILE", Order = 202, Step = 0.1f, Decimals = 1)]
    [JsonPropertyName("projectileSpeed")] public float ProjectileSpeed { get; set; } = 28.29f;

    [EditorVisible("Category", "Projectile")]
    [EditorField(Label = "Precision Bonus", Group = "PROJECTILE", Order = 203)]
    [JsonPropertyName("precisionBonus")] public int PrecisionBonus { get; set; }

    // Burst exactly at the aimed point once the arc passes it, instead of wherever
    // the ballistics happen to land (Blight bombs always do this regardless of the flag)
    [EditorVisible("Category", "Projectile")]
    [EditorField(Label = "Detonate At Target", Group = "PROJECTILE", Order = 204)]
    [JsonPropertyName("detonateAtTarget")] public bool DetonateAtTarget { get; set; }

    [EditorVisible("Category", "Projectile")]
    [EditorField(Label = "Proj Delay", Group = "PROJECTILE", Order = 205, Step = 0.01f, Decimals = 2)]
    [JsonPropertyName("projectileDelay")] public float ProjectileDelay { get; set; }

    [EditorVisible("Category", "Projectile")]
    [EditorField(Label = "Projectile Effect", Group = "PROJECTILE", Order = 206)]
    [JsonPropertyName("projectileFlipbook")] public FlipbookRef? ProjectileFlipbook { get; set; }

    // ============ BUFF / DEBUFF ============
    [EditorVisible("Category", "Buff", "Debuff")]
    [EditorField(Label = "Buff ID", Group = "BUFF", Order = 300, GroupColorR = 100, GroupColorG = 255, GroupColorB = 150)]
    [EditorRegistryDropdown("Buffs")]
    [JsonPropertyName("buffID")] public string BuffID { get; set; } = "";

    [EditorVisible("Category", "Buff", "Debuff")]
    [EditorField(Label = "Friendly Only", Group = "BUFF", Order = 301)]
    [JsonPropertyName("friendlyOnly")] public bool FriendlyOnly { get; set; } = true;

    [EditorVisible("Category", "Buff", "Debuff")]
    [EditorVisible("AoeType", "Chain")]
    [EditorField(Label = "Chain Qty", Group = "BUFF", Order = 302)]
    [JsonPropertyName("chainQuantity")] public int ChainQuantity { get; set; } = 1;

    [EditorVisible("Category", "Buff", "Debuff")]
    [EditorVisible("AoeType", "Chain")]
    [EditorField(Label = "Chain Range", Group = "BUFF", Order = 303, Step = 0.1f, Decimals = 1)]
    [JsonPropertyName("chainRange")] public float ChainRange { get; set; } = 5.0f;

    [EditorVisible("Category", "Buff", "Debuff")]
    [EditorVisible("AoeType", "Chain")]
    [EditorField(Label = "Chain Delay", Group = "BUFF", Order = 304, Step = 0.01f, Decimals = 2)]
    [JsonPropertyName("chainDelay")] public float ChainDelay { get; set; } = 0.1f;

    [EditorVisible("Category", "Buff", "Debuff")]
    [EditorField(Label = "", Group = "BUFF", Order = 305)]
    [EditorCheckboxGrid("Units", Columns = 2, Header = "ACCEPTABLE TARGETS", HeaderColorR = 200, HeaderColorG = 180, HeaderColorB = 255)]
    [JsonPropertyName("acceptableTargets")] public List<string>? AcceptableTargets { get; set; }

    [EditorVisible("Category", "Buff", "Debuff")]
    [EditorField(Label = "Cast Effect", Group = "BUFF", Order = 306)]
    [JsonPropertyName("castFlipbook")] public FlipbookRef? CastFlipbook { get; set; }

    // ============ SUMMON ============
    [EditorVisible("Category", "Summon")]
    [EditorField(Label = "Target Req", Group = "SUMMON", Order = 400, GroupColorR = 80, GroupColorG = 200, GroupColorB = 255)]
    [EditorCombo("None", "Corpse", "UnitType", "CorpseAOE")]
    [JsonPropertyName("summonTargetReq")] public string SummonTargetReq { get; set; } = "None";

    [EditorVisible("Category", "Summon")]
    [EditorField(Label = "Mode", Group = "SUMMON", Order = 401)]
    [EditorCombo("Spawn", "Transform")]
    [JsonPropertyName("summonMode")] public string SummonMode { get; set; } = "Spawn";

    [EditorVisible("Category", "Summon")]
    [EditorField(Label = "Unit", Group = "SUMMON", Order = 402)]
    [EditorRegistryDropdown("Units")]
    [JsonPropertyName("summonUnitID")] public string SummonUnitID { get; set; } = "";

    /// <summary>Corpse-puppet raise: raise the ZOMBIE variant of the target corpse (leave
    /// <see cref="SummonUnitID"/> empty so the type resolves per-corpse), but override its AI
    /// with the CorpsePuppet archetype (walk to the nearest Corpse Pile and deposit itself),
    /// and pile as the ORIGINAL corpse type — not the zombie it wears.</summary>
    [EditorVisible("Category", "Summon")]
    [EditorField(Label = "As Corpse Puppet", Group = "SUMMON", Order = 403)]
    [JsonPropertyName("summonAsPuppet")] public bool SummonAsPuppet { get; set; } = false;

    [EditorVisible("Category", "Summon")]
    [EditorField(Label = "Quantity", Group = "SUMMON", Order = 403)]
    [JsonPropertyName("summonQuantity")] public int SummonQuantity { get; set; } = 1;

    [EditorVisible("Category", "Summon")]
    [EditorField(Label = "Spawn At", Group = "SUMMON", Order = 404)]
    [EditorCombo("NearestTargetToMouse", "NearestTargetToCaster", "AdjacentToCaster", "AtTargetLocation")]
    [JsonPropertyName("spawnLocation")] public string SpawnLocation { get; set; } = "AdjacentToCaster";

    [EditorVisible("Category", "Summon")]
    [EditorField(Label = "Summon Effect", Group = "SUMMON", Order = 406)]
    [JsonPropertyName("summonFlipbook")] public FlipbookRef? SummonFlipbook { get; set; }

    /// <summary>Which composite reanimation effect variant plays as a raised corpse rises
    /// (ReanimEffectSystem preset id, e.g. "reanim_classic"). Empty = default/none.</summary>
    [EditorVisible("Category", "Summon")]
    [EditorField(Label = "Reanim Effect ID", Group = "SUMMON", Order = 407)]
    [JsonPropertyName("reanimationEffectID")] public string ReanimationEffectID { get; set; } = "";

    /// <summary>How fast the body rises after this spell raises it: scales the standup animation
    /// (gets up quicker), the spawn delay, and the outline-morph build-up so the corpse-morph stays
    /// synced to the rise. Base default 2 (the tuned reanimation feel); a per-spell override — higher
    /// = quicker. Decoupled from the smoke (see <see cref="TestFogSpeed"/>). Clamped to a floor.</summary>
    [EditorVisible("Category", "Summon")]
    [EditorField(Label = "Rise Speed (test)", Group = "SUMMON", Order = 408)]
    [JsonPropertyName("_test_riseSpeed")] public float TestRiseSpeed { get; set; } = 2f;

    /// <summary>TEMPORARY/debug tuning knob. How fast the reanimation SMOKE (the green
    /// cloud + dust puffs) builds and dissipates, independent of <see cref="TestRiseSpeed"/>
    /// — so the body can pop up fast while the fog lingers, or vice versa. 1 = default.
    /// Clamped to a floor.</summary>
    [EditorVisible("Category", "Summon")]
    [EditorField(Label = "Fog Speed (test)", Group = "SUMMON", Order = 409)]
    [JsonPropertyName("_test_fogSpeed")] public float TestFogSpeed { get; set; } = 1f;

    // ============ STRIKE ============
    [EditorVisible("Category", "Strike")]
    [EditorField(Label = "Target: Unit (Zap)", Group = "STRIKE", Order = 500, GroupColorR = 255, GroupColorG = 255, GroupColorB = 100)]
    [JsonPropertyName("strikeTargetUnit")] public bool StrikeTargetUnit { get; set; }

    [EditorVisible("Category", "Strike")]
    [EditorField(Label = "Visual", Group = "STRIKE", Order = 501)]
    [EditorCombo("Lightning", "GodRay")]
    [JsonPropertyName("strikeVisual")] public string StrikeVisualType { get; set; } = "Lightning";

    // Ground strike fields
    [EditorVisible("Category", "Strike")]
    [EditorVisible("StrikeTargetUnit", "False")]
    [EditorField(Label = "Telegraph", Group = "STRIKE", Order = 503, Step = 0.01f, Decimals = 2)]
    [JsonPropertyName("telegraphDuration")] public float TelegraphDuration { get; set; } = 0.2f;

    [EditorVisible("Category", "Strike")]
    [EditorVisible("StrikeTargetUnit", "False")]
    [EditorField(Label = "Telegraph Visible", Group = "STRIKE", Order = 504)]
    [JsonPropertyName("telegraphVisible")] public bool TelegraphVisible { get; set; } = true;

    [EditorVisible("Category", "Strike")]
    [EditorVisible("StrikeTargetUnit", "False")]
    [EditorField(Label = "Duration", Group = "STRIKE", Order = 505, Step = 0.01f, Decimals = 2)]
    [JsonPropertyName("strikeDuration")] public float StrikeDuration { get; set; } = 0.2f;

    // Zap fields
    [EditorVisible("Category", "Strike")]
    [EditorVisible("StrikeTargetUnit", "True")]
    [EditorField(Label = "Zap Duration", Group = "STRIKE", Order = 505, Step = 0.01f, Decimals = 2)]
    [JsonPropertyName("zapDuration")] public float ZapDuration { get; set; } = 0.12f;

    [EditorVisible("Category", "Strike")]
    [EditorVisible("StrikeTargetUnit", "True")]
    [EditorHeader("Chain Lightning", ColorR = 120, ColorG = 120, ColorB = 140)]
    [EditorField(Label = "Chain Branches", Group = "STRIKE", Order = 506)]
    [JsonPropertyName("strikeChainBranches")] public int StrikeChainBranches { get; set; }

    [EditorVisible("Category", "Strike")]
    [EditorVisible("StrikeTargetUnit", "True")]
    [EditorField(Label = "Chain Depth", Group = "STRIKE", Order = 507)]
    [JsonPropertyName("strikeChainDepth")] public int StrikeChainDepth { get; set; }

    [EditorVisible("Category", "Strike")]
    [EditorVisible("StrikeTargetUnit", "True")]
    [EditorField(Label = "Chain Range", Group = "STRIKE", Order = 508, Step = 0.1f, Decimals = 1)]
    [JsonPropertyName("strikeChainRange")] public float StrikeChainRange { get; set; } = 8.0f;

    [EditorVisible("Category", "Strike")]
    [EditorVisible("StrikeTargetUnit", "True")]
    [EditorField(Label = "Chain W.Decay", Group = "STRIKE", Order = 509, Step = 0.01f, Decimals = 2)]
    [JsonPropertyName("strikeChainWidthDecay")] public float StrikeChainWidthDecay { get; set; } = 0.8f;

    // Common lightning style
    [EditorVisible("Category", "Strike")]
    [EditorField(Label = "Displacement", Group = "STRIKE", Order = 510, Step = 0.01f, Decimals = 2)]
    [JsonPropertyName("strikeDisplacement")] public float StrikeDisplacement { get; set; } = 0.35f;

    [EditorVisible("Category", "Strike")]
    [EditorField(Label = "Branches", Group = "STRIKE", Order = 511)]
    [JsonPropertyName("strikeBranches")] public int StrikeBranches { get; set; } = 3;

    [EditorVisible("Category", "Strike")]
    [EditorField(Label = "Core Width", Group = "STRIKE", Order = 512, Step = 0.1f, Decimals = 1)]
    [JsonPropertyName("strikeCoreWidth")] public float StrikeCoreWidth { get; set; } = 2.0f;

    [EditorVisible("Category", "Strike")]
    [EditorField(Label = "Glow Width", Group = "STRIKE", Order = 513, Step = 0.1f, Decimals = 1)]
    [JsonPropertyName("strikeGlowWidth")] public float StrikeGlowWidth { get; set; } = 8.0f;

    [EditorVisible("Category", "Strike")]
    [EditorField(Label = "Flicker Min", Group = "STRIKE", Order = 514, Step = 0.01f, Decimals = 2)]
    [JsonPropertyName("strikeFlickerMin")] public float StrikeFlickerMin { get; set; } = 1.0f;

    [EditorVisible("Category", "Strike")]
    [EditorField(Label = "Flicker Max", Group = "STRIKE", Order = 515, Step = 0.01f, Decimals = 2)]
    [JsonPropertyName("strikeFlickerMax")] public float StrikeFlickerMax { get; set; } = 1.0f;

    [EditorVisible("Category", "Strike")]
    [EditorField(Label = "Flicker Hz", Group = "STRIKE", Order = 516, Step = 0.1f, Decimals = 1)]
    [JsonPropertyName("strikeFlickerHz")] public float StrikeFlickerHz { get; set; }

    [EditorVisible("Category", "Strike")]
    [EditorField(Label = "Jitter Hz", Group = "STRIKE", Order = 517, Step = 0.1f, Decimals = 1)]
    [JsonPropertyName("strikeJitterHz")] public float StrikeJitterHz { get; set; }

    [EditorVisible("Category", "Strike")]
    [EditorField(Label = "Core Color", Group = "STRIKE", Order = 518, Compact = true)]
    [JsonPropertyName("strikeCoreColor")] [JsonConverter(typeof(HdrColorJsonConverter))] public HdrColor StrikeCoreColor { get; set; } = new(255, 255, 255, 255, 4.0f);

    [EditorVisible("Category", "Strike")]
    [EditorField(Label = "Glow Color", Group = "STRIKE", Order = 519, Compact = true)]
    [JsonPropertyName("strikeGlowColor")] [JsonConverter(typeof(HdrColorJsonConverter))] public HdrColor StrikeGlowColor { get; set; } = new(140, 180, 255, 200, 2.5f);

    // God Ray sub-fields
    [EditorVisible("Category", "Strike")]
    [EditorVisible("StrikeVisualType", "GodRay")]
    [EditorHeader("GOD RAY", ColorR = 255, ColorG = 220, ColorB = 100)]
    [EditorField(Label = "Edge Softness", Group = "STRIKE", Order = 520, Step = 0.01f, Decimals = 2)]
    [JsonPropertyName("godRayEdgeSoftness")] public float GodRayEdgeSoftness { get; set; } = 0.4f;

    [EditorVisible("Category", "Strike")]
    [EditorVisible("StrikeVisualType", "GodRay")]
    [EditorField(Label = "Noise Strength", Group = "STRIKE", Order = 521, Step = 0.01f, Decimals = 2)]
    [JsonPropertyName("godRayNoiseStrength")] public float GodRayNoiseStrength { get; set; } = 0.35f;

    [EditorVisible("Category", "Strike")]
    [EditorVisible("StrikeVisualType", "GodRay")]
    [EditorField(Label = "Noise Speed", Group = "STRIKE", Order = 522, Step = 0.1f, Decimals = 1)]
    [JsonPropertyName("godRayNoiseSpeed")] public float GodRayNoiseSpeed { get; set; } = 1.5f;

    [EditorVisible("Category", "Strike")]
    [EditorVisible("StrikeVisualType", "GodRay")]
    [EditorField(Label = "Noise Scale", Group = "STRIKE", Order = 523, Step = 0.1f, Decimals = 1)]
    [JsonPropertyName("godRayNoiseScale")] public float GodRayNoiseScale { get; set; } = 3.0f;

    // ============ BEAM ============
    [EditorVisible("Category", "Beam")]
    [EditorField(Label = "Tick Rate", Group = "BEAM", Order = 601, Step = 0.01f, Decimals = 2, GroupColorR = 100, GroupColorG = 220, GroupColorB = 255)]
    [JsonPropertyName("beamTickRate")] public float BeamTickRate { get; set; } = 0.25f;

    [EditorVisible("Category", "Beam")]
    [EditorField(Label = "Max Duration", Group = "BEAM", Order = 602, Step = 0.1f, Decimals = 1)]
    [JsonPropertyName("beamMaxDuration")] public float BeamMaxDuration { get; set; }

    [EditorVisible("Category", "Beam")]
    [EditorField(Label = "Retarget Radius", Group = "BEAM", Order = 603, Step = 0.1f, Decimals = 1)]
    [JsonPropertyName("beamRetargetRadius")] public float BeamRetargetRadius { get; set; } = 15.0f;

    [EditorVisible("Category", "Beam")]
    [EditorField(Label = "Displacement", Group = "BEAM", Order = 604, Step = 0.01f, Decimals = 2)]
    [JsonPropertyName("beamDisplacement")] public float BeamDisplacement { get; set; } = 0.25f;

    [EditorVisible("Category", "Beam")]
    [EditorField(Label = "Branches", Group = "BEAM", Order = 605)]
    [JsonPropertyName("beamBranches")] public int BeamBranches { get; set; } = 1;

    [EditorVisible("Category", "Beam")]
    [EditorField(Label = "Core Width", Group = "BEAM", Order = 606, Step = 0.1f, Decimals = 1)]
    [JsonPropertyName("beamCoreWidth")] public float BeamCoreWidth { get; set; } = 1.5f;

    [EditorVisible("Category", "Beam")]
    [EditorField(Label = "Glow Width", Group = "BEAM", Order = 607, Step = 0.1f, Decimals = 1)]
    [JsonPropertyName("beamGlowWidth")] public float BeamGlowWidth { get; set; } = 6.0f;

    [EditorVisible("Category", "Beam")]
    [EditorField(Label = "Flicker Min", Group = "BEAM", Order = 608, Step = 0.01f, Decimals = 2)]
    [JsonPropertyName("beamFlickerMin")] public float BeamFlickerMin { get; set; } = 1.0f;

    [EditorVisible("Category", "Beam")]
    [EditorField(Label = "Flicker Max", Group = "BEAM", Order = 609, Step = 0.01f, Decimals = 2)]
    [JsonPropertyName("beamFlickerMax")] public float BeamFlickerMax { get; set; } = 1.0f;

    [EditorVisible("Category", "Beam")]
    [EditorField(Label = "Flicker Hz", Group = "BEAM", Order = 610, Step = 0.1f, Decimals = 1)]
    [JsonPropertyName("beamFlickerHz")] public float BeamFlickerHz { get; set; }

    [EditorVisible("Category", "Beam")]
    [EditorField(Label = "Jitter Hz", Group = "BEAM", Order = 611, Step = 0.1f, Decimals = 1)]
    [JsonPropertyName("beamJitterHz")] public float BeamJitterHz { get; set; }

    [EditorVisible("Category", "Beam")]
    [EditorField(Label = "Core Color", Group = "BEAM", Order = 612, Compact = true)]
    [JsonPropertyName("beamCoreColor")] [JsonConverter(typeof(HdrColorJsonConverter))] public HdrColor BeamCoreColor { get; set; } = new(255, 255, 255, 255, 3.0f);

    [EditorVisible("Category", "Beam")]
    [EditorField(Label = "Glow Color", Group = "BEAM", Order = 613, Compact = true)]
    [JsonPropertyName("beamGlowColor")] [JsonConverter(typeof(HdrColorJsonConverter))] public HdrColor BeamGlowColor { get; set; } = new(100, 160, 255, 180, 2.0f);

    [EditorVisible("Category", "Beam")]
    [EditorHeader("Chain Lightning", ColorR = 120, ColorG = 120, ColorB = 140)]
    [EditorField(Label = "Chain Branches", Group = "BEAM", Order = 614)]
    [JsonPropertyName("beamChainBranches")] public int BeamChainBranches { get; set; }

    [EditorVisible("Category", "Beam")]
    [EditorField(Label = "Chain Depth", Group = "BEAM", Order = 615)]
    [JsonPropertyName("beamChainDepth")] public int BeamChainDepth { get; set; }

    [EditorVisible("Category", "Beam")]
    [EditorField(Label = "Chain Range", Group = "BEAM", Order = 616, Step = 0.1f, Decimals = 1)]
    [JsonPropertyName("beamChainRange")] public float BeamChainRange { get; set; } = 8.0f;

    [EditorVisible("Category", "Beam")]
    [EditorField(Label = "Chain W.Decay", Group = "BEAM", Order = 617, Step = 0.01f, Decimals = 2)]
    [JsonPropertyName("beamChainWidthDecay")] public float BeamChainWidthDecay { get; set; } = 0.8f;

    // ============ DRAIN ============
    [EditorVisible("Category", "Drain")]
    [EditorField(Label = "Tick Rate", Group = "DRAIN", Order = 701, Step = 0.01f, Decimals = 2, GroupColorR = 80, GroupColorG = 255, GroupColorB = 80)]
    [JsonPropertyName("drainTickRate")] public float DrainTickRate { get; set; } = 0.25f;

    [EditorVisible("Category", "Drain")]
    [EditorField(Label = "Heal %", Group = "DRAIN", Order = 702, Step = 0.01f, Decimals = 2)]
    [JsonPropertyName("drainHealPercent")] public float DrainHealPercent { get; set; } = 1.0f;

    [EditorVisible("Category", "Drain")]
    [EditorField(Label = "Corpse HP", Group = "DRAIN", Order = 703)]
    [JsonPropertyName("drainCorpseHP")] public int DrainCorpseHP { get; set; } = 10;

    [EditorVisible("Category", "Drain")]
    [EditorField(Label = "Max Duration", Group = "DRAIN", Order = 704, Step = 0.1f, Decimals = 1)]
    [JsonPropertyName("drainMaxDuration")] public float DrainMaxDuration { get; set; }

    [EditorVisible("Category", "Drain")]
    [EditorField(Label = "Break Range", Group = "DRAIN", Order = 705, Step = 0.1f, Decimals = 1)]
    [JsonPropertyName("drainBreakRange")] public float DrainBreakRange { get; set; }

    [EditorVisible("Category", "Drain")]
    [EditorField(Label = "Reversed", Group = "DRAIN", Order = 706)]
    [JsonPropertyName("drainReversed")] public bool DrainReversed { get; set; }

    [EditorVisible("Category", "Drain")]
    [EditorField(Label = "Visual Reversed", Group = "DRAIN", Order = 707)]
    [JsonPropertyName("drainVisualReversed")] public bool DrainVisualReversed { get; set; }

    [EditorVisible("Category", "Drain")]
    [EditorHeader("Visuals", ColorR = 120, ColorG = 120, ColorB = 140)]
    [EditorField(Label = "Tendrils", Group = "DRAIN", Order = 708)]
    [JsonPropertyName("drainTendrilCount")] public int DrainTendrilCount { get; set; } = 3;

    [EditorVisible("Category", "Drain")]
    [EditorField(Label = "Arc Height", Group = "DRAIN", Order = 709, Step = 0.1f, Decimals = 1)]
    [JsonPropertyName("drainArcHeight")] public float DrainArcHeight { get; set; } = 40.0f;

    [EditorVisible("Category", "Drain")]
    [EditorField(Label = "Sway Amp", Group = "DRAIN", Order = 710, Step = 0.1f, Decimals = 1)]
    [JsonPropertyName("drainSwayAmplitude")] public float DrainSwayAmplitude { get; set; } = 8.0f;

    [EditorVisible("Category", "Drain")]
    [EditorField(Label = "Sway Hz", Group = "DRAIN", Order = 711, Step = 0.1f, Decimals = 1)]
    [JsonPropertyName("drainSwayHz")] public float DrainSwayHz { get; set; } = 1.5f;

    [EditorVisible("Category", "Drain")]
    [EditorField(Label = "Core Width", Group = "DRAIN", Order = 712, Step = 0.1f, Decimals = 1)]
    [JsonPropertyName("drainCoreWidth")] public float DrainCoreWidth { get; set; } = 1.5f;

    [EditorVisible("Category", "Drain")]
    [EditorField(Label = "Glow Width", Group = "DRAIN", Order = 713, Step = 0.1f, Decimals = 1)]
    [JsonPropertyName("drainGlowWidth")] public float DrainGlowWidth { get; set; } = 5.0f;

    [EditorVisible("Category", "Drain")]
    [EditorField(Label = "Pulse Hz", Group = "DRAIN", Order = 714, Step = 0.1f, Decimals = 1)]
    [JsonPropertyName("drainPulseHz")] public float DrainPulseHz { get; set; } = 2.0f;

    [EditorVisible("Category", "Drain")]
    [EditorField(Label = "Pulse Str", Group = "DRAIN", Order = 715, Step = 0.01f, Decimals = 2)]
    [JsonPropertyName("drainPulseStrength")] public float DrainPulseStrength { get; set; } = 0.4f;

    [EditorVisible("Category", "Drain")]
    [EditorField(Label = "Flicker Min", Group = "DRAIN", Order = 716, Step = 0.01f, Decimals = 2)]
    [JsonPropertyName("drainFlickerMin")] public float DrainFlickerMin { get; set; } = 0.8f;

    [EditorVisible("Category", "Drain")]
    [EditorField(Label = "Flicker Max", Group = "DRAIN", Order = 717, Step = 0.01f, Decimals = 2)]
    [JsonPropertyName("drainFlickerMax")] public float DrainFlickerMax { get; set; } = 1.2f;

    [EditorVisible("Category", "Drain")]
    [EditorField(Label = "Core Color", Group = "DRAIN", Order = 718, Compact = true)]
    [JsonPropertyName("drainCoreColor")] [JsonConverter(typeof(HdrColorJsonConverter))] public HdrColor DrainCoreColor { get; set; } = new(120, 255, 80, 255, 2.5f);

    [EditorVisible("Category", "Drain")]
    [EditorField(Label = "Glow Color", Group = "DRAIN", Order = 719, Compact = true)]
    [JsonPropertyName("drainGlowColor")] [JsonConverter(typeof(HdrColorJsonConverter))] public HdrColor DrainGlowColor { get; set; } = new(40, 120, 20, 160, 1.5f);

    [EditorVisible("Category", "Drain")]
    [EditorField(Label = "Target Effect", Group = "DRAIN", Order = 720)]
    [JsonPropertyName("drainTargetEffect")] public FlipbookRef? DrainTargetEffect { get; set; }

    // ============ CLOUD ============
    [EditorVisible("Category", "Cloud")]
    [EditorField(Label = "Radius", Group = "CLOUD", Order = 800, Step = 0.1f, Decimals = 1, GroupColorR = 80, GroupColorG = 200, GroupColorB = 80)]
    [JsonPropertyName("cloudRadius")] public float CloudRadius { get; set; } = 5.0f;

    [EditorVisible("Category", "Cloud")]
    [EditorField(Label = "Max Radius", Group = "CLOUD", Order = 801, Step = 0.1f, Decimals = 1)]
    [JsonPropertyName("cloudMaxRadius")] public float CloudMaxRadius { get; set; } = 10.0f;

    [EditorVisible("Category", "Cloud")]
    [EditorField(Label = "Duration", Group = "CLOUD", Order = 802, Step = 0.1f, Decimals = 1)]
    [JsonPropertyName("cloudDuration")] public float CloudDuration { get; set; } = 12.0f;

    [EditorVisible("Category", "Cloud")]
    [EditorField(Label = "Tick Rate", Group = "CLOUD", Order = 803, Step = 0.1f, Decimals = 1)]
    [JsonPropertyName("cloudTickRate")] public float CloudTickRate { get; set; } = 1.0f;

    [EditorVisible("Category", "Cloud")]
    [EditorField(Label = "Tick Damage", Group = "CLOUD", Order = 804)]
    [JsonPropertyName("cloudTickDamage")] public int CloudTickDamage { get; set; } = 3;

    [EditorVisible("Category", "Cloud")]
    [EditorHeader("CORPSE FEEDING", ColorR = 160, ColorG = 255, ColorB = 120)]
    [EditorField(Label = "Corpse Bonus", Group = "CLOUD", Order = 810)]
    [JsonPropertyName("cloudCorpseBonus")] public bool CloudCorpseBonus { get; set; } = true;

    [EditorVisible("Category", "Cloud")]
    [EditorField(Label = "Consume Rate", Group = "CLOUD", Order = 811, Step = 0.1f, Decimals = 1)]
    [JsonPropertyName("cloudCorpseConsumeRate")] public float CloudCorpseConsumeRate { get; set; } = 2.0f;

    [EditorVisible("Category", "Cloud")]
    [EditorField(Label = "Radius Bonus", Group = "CLOUD", Order = 812, Step = 0.1f, Decimals = 1)]
    [JsonPropertyName("cloudCorpseRadiusBonus")] public float CloudCorpseRadiusBonus { get; set; } = 1.5f;

    [EditorVisible("Category", "Cloud")]
    [EditorField(Label = "Duration Bonus", Group = "CLOUD", Order = 813, Step = 0.1f, Decimals = 1)]
    [JsonPropertyName("cloudCorpseDurationBonus")] public float CloudCorpseDurationBonus { get; set; } = 3.0f;

    [EditorVisible("Category", "Cloud")]
    [EditorField(Label = "Potency Bonus", Group = "CLOUD", Order = 814, Step = 0.01f, Decimals = 2)]
    [JsonPropertyName("cloudCorpsePotencyBonus")] public float CloudCorpsePotencyBonus { get; set; } = 0.25f;

    [EditorVisible("Category", "Cloud")]
    [EditorHeader("PHASES", ColorR = 180, ColorG = 140, ColorB = 255)]
    [EditorField(Label = "Eruption Dur", Group = "CLOUD", Order = 820, Step = 0.1f, Decimals = 1)]
    [JsonPropertyName("cloudEruptionDuration")] public float CloudEruptionDuration { get; set; } = 2.0f;

    [EditorVisible("Category", "Cloud")]
    [EditorField(Label = "Spread Dur", Group = "CLOUD", Order = 821, Step = 0.1f, Decimals = 1)]
    [JsonPropertyName("cloudSpreadDuration")] public float CloudSpreadDuration { get; set; } = 7.0f;

    [EditorVisible("Category", "Cloud")]
    [EditorHeader("DEBUFFS", ColorR = 255, ColorG = 120, ColorB = 120)]
    [EditorField(Label = "Slow Buff", Group = "CLOUD", Order = 830)]
    [EditorRegistryDropdown("Buffs")]
    [JsonPropertyName("cloudSlowBuffID")] public string CloudSlowBuffID { get; set; } = "";

    [EditorVisible("Category", "Cloud")]
    [EditorField(Label = "Plagued Buff", Group = "CLOUD", Order = 831)]
    [EditorRegistryDropdown("Buffs")]
    [JsonPropertyName("cloudPlaguedBuffID")] public string CloudPlaguedBuffID { get; set; } = "";

    [EditorVisible("Category", "Cloud")]
    [EditorField(Label = "Plague Threshold", Group = "CLOUD", Order = 832, Step = 0.1f, Decimals = 1)]
    [JsonPropertyName("cloudPlagueThreshold")] public float CloudPlagueThreshold { get; set; } = 3.0f;

    [EditorVisible("Category", "Cloud")]
    [EditorField(Label = "Applies Paralysis", Group = "CLOUD", Order = 833)]
    [JsonPropertyName("cloudAppliesParalysis")] public bool CloudAppliesParalysis { get; set; } = false;

    [EditorVisible("Category", "Cloud")]
    [EditorField(Label = "Cloud Color", Group = "CLOUD", Order = 835, Compact = true)]
    [JsonPropertyName("cloudColor")]
    [JsonConverter(typeof(HdrColorJsonConverter))]
    public HdrColor CloudColor { get; set; } = new(90, 180, 55, 255, 1.0f);

    [EditorVisible("Category", "Cloud")]
    [EditorField(Label = "Glow Color", Group = "CLOUD", Order = 836, Compact = true)]
    [JsonPropertyName("cloudGlowColor")]
    [JsonConverter(typeof(HdrColorJsonConverter))]
    public HdrColor CloudGlowColor { get; set; } = new(80, 255, 40, 255, 1.0f);

    // ============ SACRIFICE ============
    // Sacrifice the friendly undead nearest the cursor: it dies (crumbles into a
    // corpse) and the caster is healed by HealFlat + HealPercent × the victim's
    // effective max HP. The resource spent is the unit, not (necessarily) mana.
    // Power-user: a non-empty `acceptableTargets` list in the JSON restricts which
    // UnitDef ids can be sacrificed (e.g. skeletons only); empty = any friendly
    // undead except the caster. It isn't surfaced in the editor for this category.
    [EditorVisible("Category", "Sacrifice")]
    [EditorField(Label = "Heal Flat", Group = "SACRIFICE", Order = 900, GroupColorR = 255, GroupColorG = 90, GroupColorB = 90)]
    [JsonPropertyName("sacrificeHealFlat")] public int SacrificeHealFlat { get; set; } = 20;

    [EditorVisible("Category", "Sacrifice")]
    [EditorField(Label = "Heal % MaxHP", Group = "SACRIFICE", Order = 901, Step = 0.05f, Decimals = 2)]
    [JsonPropertyName("sacrificeHealPercent")] public float SacrificeHealPercent { get; set; }

    // ============ BLIGHT ============
    // Manipulate the death-fog ("blight") density field at the target cell.
    //  • Add    — dump BlightAmount blight into the single target cell (a blight bomb).
    //  • Purify — remove blight across a 5×5 cell kernel centered on the target (a
    //    purifying bomb). BlightAmount is the center cleanse; the four orthogonal
    //    neighbors lose ½, the four inner diagonals ¼, and the outer ring (minus the
    //    4 far corners) ⅛ — so BlightAmount=4 gives the 4 / 2 / 1 / 0.5 pattern over
    //    the 21 affected cells (5×5 − 4 corners). Faint cells (< 1 blight) are
    //    cleansed gently — see DeathFogSystem.PurifyArea.
    [EditorVisible("Category", "Blight")]
    [EditorField(Label = "Blight Mode", Group = "BLIGHT", Order = 920, GroupColorR = 120, GroupColorG = 200, GroupColorB = 110)]
    [EditorCombo("Add", "Purify")]
    [JsonPropertyName("blightMode")] public string BlightMode { get; set; } = "Add";

    [EditorVisible("Category", "Blight")]
    [EditorField(Label = "Blight Amount", Group = "BLIGHT", Order = 921, Decimals = 2)]
    [JsonPropertyName("blightAmount")] public float BlightAmount { get; set; } = 100f;

    // Toggle (hidden from editor — internal)
    [EditorHide]
    [JsonPropertyName("toggleEffect")] public string ToggleEffect { get; set; } = "";

    // ═══════════════════════════════════════
    //  Style builders — single source of truth
    //  Used by SpellEffectSystem (game) and SpellPreview (editor)
    // ═══════════════════════════════════════

    /// <summary>Build a LightningStyle from this spell's strike fields.</summary>
    public LightningStyle BuildStrikeStyle() => new()
    {
        CoreColor = StrikeCoreColor,
        GlowColor = StrikeGlowColor,
        CoreWidth = StrikeCoreWidth,
        GlowWidth = StrikeGlowWidth,
        Displacement = StrikeDisplacement,
        MaxBranches = StrikeBranches,
        FlickerMin = StrikeFlickerMin,
        FlickerMax = StrikeFlickerMax,
        FlickerHz = StrikeFlickerHz,
        JitterHz = StrikeJitterHz,
    };

    /// <summary>Build a LightningStyle from this spell's beam fields.</summary>
    public LightningStyle BuildBeamStyle() => new()
    {
        CoreColor = BeamCoreColor,
        GlowColor = BeamGlowColor,
        CoreWidth = BeamCoreWidth,
        GlowWidth = BeamGlowWidth,
        Displacement = BeamDisplacement,
        MaxBranches = BeamBranches,
        FlickerMin = BeamFlickerMin,
        FlickerMax = BeamFlickerMax,
        FlickerHz = BeamFlickerHz,
        JitterHz = BeamJitterHz,
    };

    /// <summary>Build drain visual params from this spell's drain fields.</summary>
    public DrainVisualParams BuildDrainVisuals() => new()
    {
        TendrilCount = Math.Max(1, DrainTendrilCount),
        ArcHeight = DrainArcHeight,
        SwayAmplitude = DrainSwayAmplitude,
        SwayHz = DrainSwayHz,
        CoreWidth = Math.Max(0.5f, DrainCoreWidth),
        GlowWidth = Math.Max(1f, DrainGlowWidth),
        PulseHz = DrainPulseHz,
        PulseStrength = DrainPulseStrength,
        CoreColor = DrainCoreColor,
        GlowColor = DrainGlowColor,
    };

    /// <summary>Build GodRayParams from this spell's god ray fields.</summary>
    public GodRayParams BuildGodRayParams() => new()
    {
        EdgeSoftness = GodRayEdgeSoftness,
        NoiseSpeed = GodRayNoiseSpeed,
        NoiseStrength = GodRayNoiseStrength,
        NoiseScale = GodRayNoiseScale,
    };
}

/// <summary>
/// Loads and holds the immutable <see cref="SpellDef"/>s from data/spells.json, keyed by id.
/// Definitions only — the cast pipeline lives in SpellCasting/SpellEffectSystem and
/// Game1.Spells.cs.
/// </summary>
public class SpellRegistry : RegistryBase<SpellDef>
{
    protected override string RootKey => "spells";
}
