using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using Necroking.Core;

namespace Necroking.Data.Registries;

public class FlipbookRef
{
    [JsonPropertyName("flipbookID")] public string FlipbookID { get; set; } = "";
    [JsonPropertyName("fps")] public float FPS { get; set; } = -1.0f;
    [JsonPropertyName("scale")] public float Scale { get; set; } = 1.0f;
    [JsonPropertyName("color")] [JsonConverter(typeof(HdrColorJsonConverter))] public HdrColor Color { get; set; } = new(255, 255, 255, 255, 1.0f);
    [JsonPropertyName("rotation")] public float Rotation { get; set; }
    [JsonPropertyName("blendMode")] public string BlendMode { get; set; } = "Alpha";
    [JsonPropertyName("duration")] public float Duration { get; set; } = -1.0f;
    [JsonPropertyName("alignment")] public string Alignment { get; set; } = "Ground";
}

public class SpellDef : IHasId
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string DisplayName { get; set; } = "";
    [JsonPropertyName("category")] public string Category { get; set; } = "Projectile";

    // Common
    [JsonPropertyName("range")] public float Range { get; set; } = 20.0f;
    [JsonPropertyName("manaCost")] public float ManaCost { get; set; } = 2.0f;
    [JsonPropertyName("cooldown")] public float Cooldown { get; set; }
    [JsonPropertyName("castTime")] public float CastTime { get; set; }
    [JsonPropertyName("targetFilter")] public string TargetFilter { get; set; } = "AnyEnemy";
    [JsonPropertyName("castingBuffID")] public string CastingBuffID { get; set; } = "";

    // Projectile
    [JsonPropertyName("aoeType")] public string AoeType { get; set; } = "Single";
    [JsonPropertyName("quantity")] public int Quantity { get; set; } = 1;
    [JsonPropertyName("trajectory")] public string Trajectory { get; set; } = "Lob";
    [JsonPropertyName("projectileSpeed")] public float ProjectileSpeed { get; set; } = 28.29f;
    [JsonPropertyName("precisionBonus")] public int PrecisionBonus { get; set; }
    [JsonPropertyName("aoeRadius")] public float AoeRadius { get; set; }
    [JsonPropertyName("damage")] public int Damage { get; set; }
    [JsonPropertyName("projectileDelay")] public float ProjectileDelay { get; set; }
    [JsonPropertyName("projectileFlipbook")] public FlipbookRef? ProjectileFlipbook { get; set; }
    [JsonPropertyName("hitEffectFlipbook")] public FlipbookRef? HitEffectFlipbook { get; set; }

    // Buff/Debuff
    [JsonPropertyName("buffID")] public string BuffID { get; set; } = "";
    [JsonPropertyName("chainQuantity")] public int ChainQuantity { get; set; } = 1;
    [JsonPropertyName("chainRange")] public float ChainRange { get; set; } = 5.0f;
    [JsonPropertyName("chainDelay")] public float ChainDelay { get; set; } = 0.1f;
    [JsonPropertyName("friendlyOnly")] public bool FriendlyOnly { get; set; } = true;
    [JsonPropertyName("acceptableTargets")] public List<string>? AcceptableTargets { get; set; }
    [JsonPropertyName("castFlipbook")] public FlipbookRef? CastFlipbook { get; set; }

    // Summon
    [JsonPropertyName("summonTargetReq")] public string SummonTargetReq { get; set; } = "None";
    [JsonPropertyName("summonMode")] public string SummonMode { get; set; } = "Spawn";
    [JsonPropertyName("summonUnitID")] public string SummonUnitID { get; set; } = "";
    [JsonPropertyName("summonQuantity")] public int SummonQuantity { get; set; } = 1;
    [JsonPropertyName("spawnLocation")] public string SpawnLocation { get; set; } = "AdjacentToCaster";
    [JsonPropertyName("summonFlipbook")] public FlipbookRef? SummonFlipbook { get; set; }

    // Strike
    [JsonPropertyName("telegraphDuration")] public float TelegraphDuration { get; set; } = 0.2f;
    [JsonPropertyName("strikeDuration")] public float StrikeDuration { get; set; } = 0.2f;
    [JsonPropertyName("strikeDisplacement")] public float StrikeDisplacement { get; set; } = 0.35f;
    [JsonPropertyName("strikeBranches")] public int StrikeBranches { get; set; } = 3;
    [JsonPropertyName("strikeCoreColor")] [JsonConverter(typeof(HdrColorJsonConverter))] public HdrColor StrikeCoreColor { get; set; } = new(255, 255, 255, 255, 4.0f);
    [JsonPropertyName("strikeGlowColor")] [JsonConverter(typeof(HdrColorJsonConverter))] public HdrColor StrikeGlowColor { get; set; } = new(140, 180, 255, 200, 2.5f);
    [JsonPropertyName("strikeCoreWidth")] public float StrikeCoreWidth { get; set; } = 2.0f;
    [JsonPropertyName("strikeGlowWidth")] public float StrikeGlowWidth { get; set; } = 8.0f;
    [JsonPropertyName("strikeFlickerMin")] public float StrikeFlickerMin { get; set; } = 1.0f;
    [JsonPropertyName("strikeFlickerMax")] public float StrikeFlickerMax { get; set; } = 1.0f;
    [JsonPropertyName("strikeFlickerHz")] public float StrikeFlickerHz { get; set; }
    [JsonPropertyName("strikeJitterHz")] public float StrikeJitterHz { get; set; }
    [JsonPropertyName("strikeVisual")] public string StrikeVisualType { get; set; } = "Lightning";

    // God ray
    [JsonPropertyName("godRayEdgeSoftness")] public float GodRayEdgeSoftness { get; set; } = 0.4f;
    [JsonPropertyName("godRayNoiseSpeed")] public float GodRayNoiseSpeed { get; set; } = 1.5f;
    [JsonPropertyName("godRayNoiseStrength")] public float GodRayNoiseStrength { get; set; } = 0.35f;
    [JsonPropertyName("godRayNoiseScale")] public float GodRayNoiseScale { get; set; } = 3.0f;

    [JsonPropertyName("strikeTargetUnit")] public bool StrikeTargetUnit { get; set; }
    [JsonPropertyName("zapDuration")] public float ZapDuration { get; set; } = 0.12f;
    [JsonPropertyName("strikeChainBranches")] public int StrikeChainBranches { get; set; }
    [JsonPropertyName("strikeChainDepth")] public int StrikeChainDepth { get; set; }
    [JsonPropertyName("strikeChainRange")] public float StrikeChainRange { get; set; } = 8.0f;
    [JsonPropertyName("strikeChainWidthDecay")] public float StrikeChainWidthDecay { get; set; } = 0.8f;

    // Beam
    [JsonPropertyName("beamTickRate")] public float BeamTickRate { get; set; } = 0.25f;
    [JsonPropertyName("beamMaxDuration")] public float BeamMaxDuration { get; set; }
    [JsonPropertyName("beamRetargetRadius")] public float BeamRetargetRadius { get; set; } = 15.0f;
    [JsonPropertyName("beamDisplacement")] public float BeamDisplacement { get; set; } = 0.25f;
    [JsonPropertyName("beamBranches")] public int BeamBranches { get; set; } = 1;
    [JsonPropertyName("beamCoreColor")] [JsonConverter(typeof(HdrColorJsonConverter))] public HdrColor BeamCoreColor { get; set; } = new(255, 255, 255, 255, 3.0f);
    [JsonPropertyName("beamGlowColor")] [JsonConverter(typeof(HdrColorJsonConverter))] public HdrColor BeamGlowColor { get; set; } = new(100, 160, 255, 180, 2.0f);
    [JsonPropertyName("beamCoreWidth")] public float BeamCoreWidth { get; set; } = 1.5f;
    [JsonPropertyName("beamGlowWidth")] public float BeamGlowWidth { get; set; } = 6.0f;
    [JsonPropertyName("beamFlickerMin")] public float BeamFlickerMin { get; set; } = 1.0f;
    [JsonPropertyName("beamFlickerMax")] public float BeamFlickerMax { get; set; } = 1.0f;
    [JsonPropertyName("beamFlickerHz")] public float BeamFlickerHz { get; set; }
    [JsonPropertyName("beamJitterHz")] public float BeamJitterHz { get; set; }
    [JsonPropertyName("beamChainBranches")] public int BeamChainBranches { get; set; }
    [JsonPropertyName("beamChainDepth")] public int BeamChainDepth { get; set; }
    [JsonPropertyName("beamChainRange")] public float BeamChainRange { get; set; } = 8.0f;
    [JsonPropertyName("beamChainWidthDecay")] public float BeamChainWidthDecay { get; set; } = 0.8f;

    // Drain
    [JsonPropertyName("drainCorpseHP")] public int DrainCorpseHP { get; set; } = 10;
    [JsonPropertyName("drainTickRate")] public float DrainTickRate { get; set; } = 0.25f;
    [JsonPropertyName("drainHealPercent")] public float DrainHealPercent { get; set; } = 1.0f;
    [JsonPropertyName("drainMaxDuration")] public float DrainMaxDuration { get; set; }
    [JsonPropertyName("drainBreakRange")] public float DrainBreakRange { get; set; }
    [JsonPropertyName("drainReversed")] public bool DrainReversed { get; set; }
    [JsonPropertyName("drainVisualReversed")] public bool DrainVisualReversed { get; set; }
    [JsonPropertyName("drainTendrilCount")] public int DrainTendrilCount { get; set; } = 3;
    [JsonPropertyName("drainArcHeight")] public float DrainArcHeight { get; set; } = 40.0f;
    [JsonPropertyName("drainSwayAmplitude")] public float DrainSwayAmplitude { get; set; } = 8.0f;
    [JsonPropertyName("drainSwayHz")] public float DrainSwayHz { get; set; } = 1.5f;
    [JsonPropertyName("drainCoreWidth")] public float DrainCoreWidth { get; set; } = 1.5f;
    [JsonPropertyName("drainGlowWidth")] public float DrainGlowWidth { get; set; } = 5.0f;
    [JsonPropertyName("drainCoreColor")] [JsonConverter(typeof(HdrColorJsonConverter))] public HdrColor DrainCoreColor { get; set; } = new(120, 255, 80, 255, 2.5f);
    [JsonPropertyName("drainGlowColor")] [JsonConverter(typeof(HdrColorJsonConverter))] public HdrColor DrainGlowColor { get; set; } = new(40, 120, 20, 160, 1.5f);
    [JsonPropertyName("drainPulseHz")] public float DrainPulseHz { get; set; } = 2.0f;
    [JsonPropertyName("drainPulseStrength")] public float DrainPulseStrength { get; set; } = 0.4f;
    [JsonPropertyName("drainFlickerMin")] public float DrainFlickerMin { get; set; } = 0.8f;
    [JsonPropertyName("drainFlickerMax")] public float DrainFlickerMax { get; set; } = 1.2f;
    [JsonPropertyName("drainTargetEffect")] public FlipbookRef? DrainTargetEffect { get; set; }

    // Toggle
    [JsonPropertyName("toggleEffect")] public string ToggleEffect { get; set; } = "";
}

public class SpellRegistry : RegistryBase<SpellDef>
{
    protected override string RootKey => "spells";
}
