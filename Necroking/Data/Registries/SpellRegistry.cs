using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using Necroking.Core;
using Necroking.Editor;

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
    [EditorCombo("Projectile", "Buff", "Debuff", "Summon", "Strike", "Beam", "Drain")]
    [JsonPropertyName("category")] public string Category { get; set; } = "Projectile";

    // ============ COMMON ============
    [EditorField(Label = "Range", Group = "COMMON", Order = 100, Step = 0.1f, Decimals = 1, GroupColorR = 200, GroupColorG = 200, GroupColorB = 220)]
    [JsonPropertyName("range")] public float Range { get; set; } = 20.0f;

    [EditorField(Label = "Mana Cost", Group = "COMMON", Order = 101, Step = 0.1f, Decimals = 1)]
    [JsonPropertyName("manaCost")] public float ManaCost { get; set; } = 2.0f;

    [EditorField(Label = "Cooldown", Group = "COMMON", Order = 102, Step = 0.1f, Decimals = 1)]
    [JsonPropertyName("cooldown")] public float Cooldown { get; set; }

    [EditorField(Label = "Cast Time", Group = "COMMON", Order = 103, Step = 0.1f, Decimals = 1)]
    [JsonPropertyName("castTime")] public float CastTime { get; set; }

    [EditorField(Label = "Casting Buff", Group = "COMMON", Order = 104)]
    [EditorRegistryDropdown("Buffs")]
    [JsonPropertyName("castingBuffID")] public string CastingBuffID { get; set; } = "";

    // Target Filter — only shown for Strike
    [EditorVisible("Category", "Strike")]
    [EditorField(Label = "Target Filter", Group = "COMMON", Order = 105)]
    [EditorCombo("AnyEnemy", "UndeadOnly", "LivingOnly")]
    [JsonPropertyName("targetFilter")] public string TargetFilter { get; set; } = "AnyEnemy";

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
    [EditorField(Label = "Duration", Group = "STRIKE", Order = 504, Step = 0.01f, Decimals = 2)]
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

    // Toggle (hidden from editor — internal)
    [EditorHide]
    [JsonPropertyName("toggleEffect")] public string ToggleEffect { get; set; } = "";
}

public class SpellRegistry : RegistryBase<SpellDef>
{
    protected override string RootKey => "spells";
}
