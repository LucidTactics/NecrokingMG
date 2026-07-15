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
    [EditorField(Label = "Flipbook", Order = 0, Tooltip = "Which effect animation plays.")]
    [EditorRegistryDropdown("Flipbooks")]
    [JsonPropertyName("flipbookID")] public string FlipbookID { get; set; } = "";

    [EditorField(Label = "FPS", Order = 1, Step = 0.1f, Decimals = 1, Tooltip = "Playback speed (frames/sec). -1 = the flipbook's own rate.")]
    [JsonPropertyName("fps")] public float FPS { get; set; } = -1.0f;

    [EditorField(Label = "Scale", Order = 2, Step = 0.01f, Decimals = 2, Tooltip = "Size multiplier for the effect.")]
    [JsonPropertyName("scale")] public float Scale { get; set; } = 1.0f;

    [EditorField(Label = "Color", Order = 3, Compact = true, Tooltip = "Tint color; intensity above 1 makes it glow.")]
    [JsonPropertyName("color")] [JsonConverter(typeof(HdrColorJsonConverter))] public HdrColor Color { get; set; } = new(255, 255, 255, 255, 1.0f);

    [EditorField(Label = "Rotation", Order = 4, Step = 1f, Tooltip = "Rotates the effect sprite (degrees).")]
    [JsonPropertyName("rotation")] public float Rotation { get; set; }

    [EditorField(Label = "Blend", Order = 5, Tooltip = "Alpha = solid/smoky look; Additive = glowing light look.")]
    [EditorCombo("Alpha", "Additive")]
    [JsonPropertyName("blendMode")] public string BlendMode { get; set; } = "Alpha";

    [EditorField(Label = "Alignment", Order = 6, Tooltip = "Ground = flat on the terrain; Upright = stands facing the camera.")]
    [EditorCombo("Ground", "Upright")]
    [JsonPropertyName("alignment")] public string Alignment { get; set; } = "Ground";

    [EditorField(Label = "Duration", Order = 7, Step = 0.01f, Decimals = 2, Tooltip = "Seconds the effect lasts. -1 = one full playthrough.")]
    [JsonPropertyName("duration")] public float Duration { get; set; } = -1.0f;
}

public class SpellDef : INamedDef
{
    // ============ Top fields (ungrouped) ============
    [EditorField(Label = "Name", Order = 0, Tooltip = "Display name shown in the grimoire and on the spell bar.")]
    [JsonPropertyName("name")] public string DisplayName { get; set; } = "";

    [EditorField(Label = "ID", Order = 1, ReadOnly = true, Tooltip = "Internal id other data refers to. Not editable.")]
    [JsonPropertyName("id")] public string Id { get; set; } = "";

    [EditorField(Label = "Category", Order = 2, Tooltip = "The spell's core mechanic. Changing it swaps which\nsections appear below.")]
    [EditorCombo("Projectile", "Buff", "Debuff", "Summon", "Strike", "Beam", "Drain", "Cloud", "Sacrifice", "Blight", "WolfHunt")]
    [JsonPropertyName("category")] public string Category { get; set; } = "Projectile";

    // ===== Grimoire presentation (tab school + tile template + icon) =====
    [EditorField(Label = "School", Order = 3, Tooltip = "Grimoire tab this spell is listed under.")]
    [EditorCombo("Conjuration", "Alteration", "Evocation", "Construction")]
    [JsonPropertyName("school")] public string School { get; set; } = "";

    [EditorField(Label = "Tile Template", Order = 4, Tooltip = "Art template for the spell's grimoire tile.")]
    [EditorCombo("summon", "evocation", "buff", "debuff")]
    [JsonPropertyName("tileTemplate")] public string TileTemplate { get; set; } = "";

    [EditorField(Label = "Icon", Order = 5, Tooltip = "Icon image path for the grimoire tile and spell bar.")]
    [JsonPropertyName("icon")] public string Icon { get; set; } = "";

    /// <summary>Hide from the grimoire (debug/internal spells).</summary>
    [JsonPropertyName("hidden")] public bool Hidden { get; set; }

    /// <summary>Inventory item this spell consumes on cast (e.g. a potion). When
    /// set, the spell needs &gt;=1 of the item in the inventory and a use decrements
    /// it; the spell bar shows the remaining count. Potions are surfaced as such
    /// spells (id == the potion id) so they live in the grimoire / spell bar.</summary>
    [JsonPropertyName("consumesItem")] public string ConsumesItem { get; set; } = "";

    // ============ COMMON ============
    [EditorField(Label = "Range", Group = "COMMON", Order = 100, Step = 0.1f, Decimals = 1, GroupColorR = 200, GroupColorG = 200, GroupColorB = 220, Tooltip = "Max cast distance from the caster (world units).")]
    [JsonPropertyName("range")] public float Range { get; set; } = 20.0f;

    [EditorField(Label = "Mana Cost", Group = "COMMON", Order = 101, Step = 0.1f, Decimals = 1, Tooltip = "Fatigue (mana) spent per cast. Only reduced by this\nspell's own mastery bonuses (fatigue -N% / free).")]
    [JsonPropertyName("manaCost")] public float ManaCost { get; set; } = 2.0f;

    // Path requirements. A spell may have a primary and an optional secondary.
    // Both must be met by the caster to cast; only the primary's level feeds the
    // mastery bonuses (x = caster level above the primary requirement).
    [EditorField(Label = "Primary Path",  Group = "PATHS", Order = 120, GroupColorR = 200, GroupColorG = 180, GroupColorB = 240, Tooltip = "Magic path required to cast. Levels beyond the\nrequirement feed the spell's mastery bonuses.")]
    [EditorCombo("", "metal", "shock", "fire", "water", "heavens", "earth", "chaos", "order", "spirit", "body", "nature", "death")]
    [JsonPropertyName("primaryPath")]    public string PrimaryPath    { get; set; } = "";

    [EditorField(Label = "Primary Level", Group = "PATHS", Order = 121, Tooltip = "Path level needed to cast.")]
    [JsonPropertyName("primaryLevel")]   public int    PrimaryLevel   { get; set; }

    [EditorField(Label = "Secondary Path",  Group = "PATHS", Order = 122, Tooltip = "Optional second path requirement. A gate only -\ndoes not affect cost.")]
    [EditorCombo("", "metal", "shock", "fire", "water", "heavens", "earth", "chaos", "order", "spirit", "body", "nature", "death")]
    [JsonPropertyName("secondaryPath")]  public string SecondaryPath  { get; set; } = "";

    [EditorField(Label = "Secondary Level", Group = "PATHS", Order = 123, Tooltip = "Level needed in the secondary path.")]
    [JsonPropertyName("secondaryLevel")] public int    SecondaryLevel { get; set; }

    // Mastery mini-language: bonuses for casting with primary-path levels above
    // the requirement (x = caster level - required level). Grammar + semantics
    // live on SpellMastery; the tooltip and the cast pipeline both read the
    // parsed form via GetMasteryBonuses().
    [EditorField(Label = "", Group = "PATHS", Order = 124, Tooltip = "Bonuses per level above the primary requirement.\n\"+N: effect\" unlocks at N above; \"x: effect\" scales\nper level. Effects: fatigue -30%, free, damage +5,\nrange +2, aoe +1.5, duration +50%, buff <id> [self].")]
    [EditorStringList(Header = "MASTERY BONUSES", AddLabel = "+ Add bonus")]
    [JsonPropertyName("masteryBonuses")] public List<string>? MasteryBonuses { get; set; }

    // Parse cache for MasteryBonuses — invalidated when the list instance or any
    // line changes (the in-game editor mutates the def live).
    private List<MasteryBonus>? _masteryCache;
    private string[]? _masteryCacheSource;

    /// <summary>The parsed mastery bonuses (cached; re-parses when the editor
    /// changes the lines). Empty list when the spell has none.</summary>
    public List<MasteryBonus> GetMasteryBonuses()
    {
        var src = MasteryBonuses;
        if (src == null || src.Count == 0)
        {
            _masteryCache = null; _masteryCacheSource = null;
            return _emptyMastery;
        }
        bool fresh = _masteryCache != null && _masteryCacheSource != null
            && _masteryCacheSource.Length == src.Count;
        if (fresh)
            for (int i = 0; i < src.Count; i++)
                if (!ReferenceEquals(_masteryCacheSource![i], src[i])) { fresh = false; break; }
        if (!fresh)
        {
            _masteryCache = SpellMastery.ParseAll(src, Id);
            _masteryCacheSource = src.ToArray();
        }
        return _masteryCache!;
    }
    private static readonly List<MasteryBonus> _emptyMastery = new();

    /// <summary>x — how many levels the caster is above the primary-path
    /// requirement (0 when merely meeting it, or when there's no requirement).
    /// The one input to every mastery bonus.</summary>
    public int MasteryLevels(Func<MagicPath, int> casterLevel)
    {
        var pri = GetPrimary();
        if (!pri.HasRequirement) return 0;
        return Math.Max(0, casterLevel(pri.Path) - pri.Level);
    }

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

    /// <summary>Effective mana/fatigue cost after mastery bonuses. There is NO
    /// blanket discount for out-leveling a spell anymore — only the spell's own
    /// masteryBonuses lines (fatigue -N% / free) reduce the cost. Secondary path
    /// requirement never affects cost — it's a gate only.</summary>
    public float EffectiveManaCost(Func<MagicPath, int> casterLevel)
        => EffectiveManaCostAt(MasteryLevels(casterLevel));

    /// <summary>Cost at a known x (levels above the primary requirement) — the
    /// tooltip path, which resolves x once for the whole tooltip.</summary>
    public float EffectiveManaCostAt(int masteryLevels)
        => ManaCost * SpellMastery.FatigueCostMultiplier(GetMasteryBonuses(), masteryLevels);

    /// <summary>Cast range after mastery bonuses at x levels above the requirement.</summary>
    public float ScaledRange(int masteryLevels)
        => SpellMastery.ApplyStat(GetMasteryBonuses(), MasteryEffect.Range, Range, masteryLevels);

    /// <summary>Damage after mastery bonuses (rounded — the damage pipeline is int).</summary>
    public int ScaledDamage(int masteryLevels)
        => (int)MathF.Round(SpellMastery.ApplyStat(GetMasteryBonuses(), MasteryEffect.Damage,
            Damage, masteryLevels));

    /// <summary>AoE radius after mastery bonuses at x levels above the requirement.</summary>
    public float ScaledAoeRadius(int masteryLevels)
        => SpellMastery.ApplyStat(GetMasteryBonuses(), MasteryEffect.Aoe, AoeRadius, masteryLevels);

    [EditorField(Label = "Cooldown", Group = "COMMON", Order = 102, Step = 0.1f, Decimals = 1, Tooltip = "Seconds before the spell can be cast again.")]
    [JsonPropertyName("cooldown")] public float Cooldown { get; set; }

    [EditorField(Label = "Cast Time", Group = "COMMON", Order = 103, Step = 0.1f, Decimals = 1, Tooltip = "Wind-up seconds before the effect fires.")]
    [JsonPropertyName("castTime")] public float CastTime { get; set; }

    [EditorField(Label = "Casting Buff", Group = "COMMON", Order = 104, Tooltip = "Buff applied to the caster for the cast's duration.")]
    [EditorRegistryDropdown("Buffs")]
    [JsonPropertyName("castingBuffID")] public string CastingBuffID { get; set; } = "";

    // Caster animation while casting. "Spell1" (default) = the single-shot cast
    // anim (effect fires at the anim's effect frame). "ImbueGround"/"Raise" are
    // channeled Start->Loop->Finish casts (Raise has no Finish); the effect fires
    // at the END of the loop. See the channeled-cast machine in Game1.
    [EditorField(Label = "Cast Anim", Group = "COMMON", Order = 105, Tooltip = "Caster animation. Spell1 = single shot;\nImbueGround/Raise = channeled loop, fires at loop end.")]
    [EditorCombo("Spell1", "ImbueGround", "Raise")]
    [JsonPropertyName("castAnim")] public string CastAnim { get; set; } = "Spell1";

    // Target Filter — only shown for Strike
    [EditorVisible("Category", "Strike")]
    [EditorField(Label = "Target Filter", Group = "COMMON", Order = 105, Tooltip = "Which units the strike is allowed to hit.")]
    [EditorCombo("AnyEnemy", "UndeadOnly", "LivingOnly")]
    [JsonPropertyName("targetFilter")] public string TargetFilter { get; set; } = "AnyEnemy";

    [EditorField(Label = "Armor Negating", Group = "COMMON", Order = 106, Tooltip = "Target's Protection and Toughness count as 0 for the hit.")]
    [JsonPropertyName("armorNegating")] public bool ArmorNegating { get; set; }

    // Halves the target's protection VALUE and toughness; the protection DRN
    // still rolls in full (melee armor-piercing convention). Ignored when
    // ArmorNegating already zeroes protection.
    [EditorField(Label = "Armor Piercing", Group = "COMMON", Order = 107, Tooltip = "Halves the target's Protection and Toughness.")]
    [JsonPropertyName("armorPiercing")] public bool ArmorPiercing { get; set; }

    [EditorField(Label = "Defense Negating", Group = "COMMON", Order = 108, Tooltip = "Legacy flag - spell damage already always hits,\nso this has no effect.")]
    [JsonPropertyName("defenseNegating")] public bool DefenseNegating { get; set; } = true;

    // Magic Resistance — when ChecksMagicResist is set, the spell must penetrate the
    // target's MR to affect it: (penetration + DRN) vs (MR + DRN). ResistDifficulty
    // shifts penetration like a Dominions spell difficulty: Hard +4, Easy −4.
    [EditorField(Label = "Checks Magic Resist", Group = "COMMON", Order = 109, Tooltip = "Spell must beat the target's Magic Resist roll\nor it does nothing to them.")]
    [JsonPropertyName("checksMagicResist")] public bool ChecksMagicResist { get; set; }

    [EditorVisible("ChecksMagicResist", "True")]
    [EditorField(Label = "Resist Difficulty", Group = "COMMON", Order = 110, Tooltip = "Penetration shift: Hard +4 (harder to resist),\nEasy -4 (easier to resist).")]
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
    [EditorField(Label = "Knockback Force", Group = "PHYSICS", Order = 111, Step = 1f, Decimals = 0, Tooltip = "Radial shove strength outward from the impact point.")]
    [JsonPropertyName("knockbackForce")] public float KnockbackForce { get; set; }

    [EditorVisible("Category", "Projectile", "Cloud", "Strike")]
    [EditorField(Label = "Knockback Up", Group = "PHYSICS", Order = 112, Step = 1f, Decimals = 0, Tooltip = "Upward pop added to the knockback.")]
    [JsonPropertyName("knockbackUpward")] public float KnockbackUpward { get; set; }

    [EditorVisible("Category", "Projectile", "Cloud", "Strike")]
    [EditorField(Label = "Knockback Radius", Group = "PHYSICS", Order = 113, Step = 0.5f, Decimals = 1, Tooltip = "How far from the impact units get shoved.")]
    [JsonPropertyName("knockbackRadius")] public float KnockbackRadius { get; set; }

    // Directional impact (applied only to units the projectile actually hits,
    // shoving them along the projectile's flight direction — unlike Knockback,
    // which blasts radially outward from the impact point)
    [EditorVisible("Category", "Projectile")]
    [EditorField(Label = "Impact Force", Group = "PHYSICS", Order = 114, Step = 1f, Decimals = 0, Tooltip = "Shove along the flight direction, applied only\nto units the projectile actually hits.")]
    [JsonPropertyName("impactForce")] public float ImpactForce { get; set; }

    [EditorVisible("Category", "Projectile")]
    [EditorField(Label = "Impact Up", Group = "PHYSICS", Order = 115, Step = 1f, Decimals = 0, Tooltip = "Upward pop added to the direct-hit shove.")]
    [JsonPropertyName("impactUpward")] public float ImpactUpward { get; set; }

    // ============ Shared fields (ungrouped, between COMMON and category sections) ============
    // AoeType — used in Projectile and Buff/Debuff
    [EditorVisible("Category", "Projectile", "Buff", "Debuff")]
    [EditorField(Label = "AOE Type", Order = 199, Tooltip = "Single = one target; AOE = hits an area.\n(Chain is not wired up yet.)")]
    [EditorCombo("Single", "AOE", "Chain")]
    [JsonPropertyName("aoeType")] public string AoeType { get; set; } = "Single";

    // Damage — used in Projectile, Strike, Beam, Drain
    [EditorVisible("Category", "Projectile", "Strike", "Beam", "Drain")]
    [EditorField(Label = "Damage", Order = 198, Tooltip = "Base damage per hit, rolled vs the target's Protection.")]
    [JsonPropertyName("damage")] public int Damage { get; set; }

    /// <summary>Caster-side DRN tier override for this spell's contests (the
    /// damage-vs-protection roll AND the MR penetration roll): 1-4, same tiers
    /// as UnitStats.Drn (1=d3, 2=d6, 3=d6+one extra on 6, 4=open-ended d6).
    /// 0 = unset, use the caster's own tier. Editor-visible for Evocation only
    /// for now (by request) — the field works on any spell.</summary>
    [EditorVisible("School", "Evocation")]
    [EditorField(Label = "DRN (0=caster)", Order = 197, Tooltip = "Caster dice tier for damage/penetration rolls: 1=d3,\n2=d6, 3=d6 exploding, 4=open-ended. 0 = caster's own.")]
    [JsonPropertyName("drn")] public int Drn { get; set; }

    // AoeRadius — compound conditions per category, handled manually
    [EditorHide]
    [JsonPropertyName("aoeRadius")] public float AoeRadius { get; set; }

    // WolfHunt — how long the commanded pack-hunt stays active after the cast.
    [EditorField(Label = "Hunt Duration", Order = 250, Step = 0.5f, Decimals = 1, Tooltip = "Seconds the commanded pack hunt stays active.")]
    [EditorVisible("Category", "WolfHunt")]
    [JsonPropertyName("wolfHuntDuration")] public float WolfHuntDuration { get; set; } = 0;

    // HitEffectFlipbook — shown in Projectile and Strike(!target), handled manually for Strike
    [EditorHide]
    [JsonPropertyName("hitEffectFlipbook")] public FlipbookRef? HitEffectFlipbook { get; set; }

    // ============ PROJECTILE ============
    [EditorVisible("Category", "Projectile")]
    [EditorField(Label = "Quantity", Group = "PROJECTILE", Order = 200, GroupColorR = 255, GroupColorG = 160, GroupColorB = 100, Tooltip = "Shots fired per cast (volley size).")]
    [JsonPropertyName("quantity")] public int Quantity { get; set; } = 1;

    [EditorVisible("Category", "Projectile")]
    [EditorField(Label = "Trajectory", Group = "PROJECTILE", Order = 201, Tooltip = "Flight path: Lob/HighLob arc, DirectFire flies flat,\nHoming tracks the target.")]
    [EditorCombo("Lob", "HighLob", "DirectFire", "Homing", "Swirly", "HomingSwirly")]
    [JsonPropertyName("trajectory")] public string Trajectory { get; set; } = "Lob";

    [EditorVisible("Category", "Projectile")]
    [EditorField(Label = "TrajectoryMods", Group = "PROJECTILE", Order = 201, Tooltip = "Extra corkscrew wobble layered on the flight path.")]
    [EditorCombo("", "Swirly", "Swirly3d")]
    [JsonPropertyName("trajectoryMods")] public string TrajectoryMods { get; set; } = "";

    [EditorVisible("Category", "Projectile")]
    [EditorField(Label = "Proj Speed", Group = "PROJECTILE", Order = 202, Step = 0.1f, Decimals = 1, Tooltip = "Flight speed. Higher = flatter arc, lands sooner.")]
    [JsonPropertyName("projectileSpeed")] public float ProjectileSpeed { get; set; } = 28.29f;

    // Multiplier on gravity for this projectile: 1 = normal, <1 floats/flattens the arc,
    // >1 steepens it, 0 = flies dead flat. The launch solve uses the same scaled gravity,
    // so a lob still lands on target — only the arc shape (and airtime) changes.
    [EditorVisible("Category", "Projectile")]
    [EditorField(Label = "Gravity Scale", Group = "PROJECTILE", Order = 209, Step = 0.1f, Decimals = 2, Tooltip = "Arc shape: 1 = normal, below 1 floats flatter,\n0 = dead flat. Landing point is unchanged.")]
    [JsonPropertyName("gravityScale")] public float GravityScale { get; set; } = 1f;

    [EditorVisible("Category", "Projectile")]
    [EditorField(Label = "Precision Bonus", Group = "PROJECTILE", Order = 203, Tooltip = "Tightens shot scatter; higher = lands closer to the aim.")]
    [JsonPropertyName("precisionBonus")] public int PrecisionBonus { get; set; }

    // Burst exactly at the aimed point once the arc passes it, instead of wherever
    // the ballistics happen to land (Blight bombs always do this regardless of the flag)
    [EditorVisible("Category", "Projectile")]
    [EditorField(Label = "Detonate At Target", Group = "PROJECTILE", Order = 204, Tooltip = "Burst exactly at the aimed point instead of\nwherever the arc happens to land.")]
    [JsonPropertyName("detonateAtTarget")] public bool DetonateAtTarget { get; set; }

    [EditorVisible("Category", "Projectile")]
    [EditorField(Label = "Proj Delay", Group = "PROJECTILE", Order = 205, Step = 0.01f, Decimals = 2, Tooltip = "Seconds between shots of a volley.")]
    [JsonPropertyName("projectileDelay")] public float ProjectileDelay { get; set; }

    // Barrage spread: each shot in a Quantity>1 volley lands at a uniform-random point
    // within this radius of the aim, scattering evenly over the disc (π·r² area). 0 =
    // every shot on the exact aim point (the default single-target behavior).
    [EditorVisible("Category", "Projectile")]
    [EditorField(Label = "Spread Radius", Group = "PROJECTILE", Order = 206, Step = 0.1f, Decimals = 1, Tooltip = "Each volley shot lands within this radius of the aim.\n0 = all shots dead-on.")]
    [JsonPropertyName("projectileSpread")] public float ProjectileSpread { get; set; }

    // When the PLAYER casts a multi-shot volley, follow-up shots re-aim at the live
    // cursor each shot (the cursor updates the aim; an invalid cursor holds the last
    // valid point). Turn OFF for barrages so the volley scatters around the ORIGINAL
    // cast point instead of homing the whole spread onto the cursor. No effect on AI
    // casts (they never track the player's cursor).
    [EditorVisible("Category", "Projectile")]
    [EditorField(Label = "Track Cursor (player)", Group = "PROJECTILE", Order = 207, Tooltip = "Volley follow-up shots re-aim at your cursor.\nOff = they scatter around the original point.")]
    [JsonPropertyName("tracksCursor")] public bool TracksCursor { get; set; } = true;

    [EditorVisible("Category", "Projectile")]
    [EditorField(Label = "Projectile Effect", Group = "PROJECTILE", Order = 208, Tooltip = "Effect drawn as the projectile in flight.")]
    [JsonPropertyName("projectileFlipbook")] public FlipbookRef? ProjectileFlipbook { get; set; }

    // ============ BUFF / DEBUFF ============
    [EditorVisible("Category", "Buff", "Debuff")]
    [EditorField(Label = "Buff ID", Group = "BUFF", Order = 300, GroupColorR = 100, GroupColorG = 255, GroupColorB = 150, Tooltip = "The buff or debuff this spell applies.")]
    [EditorRegistryDropdown("Buffs")]
    [JsonPropertyName("buffID")] public string BuffID { get; set; } = "";

    [EditorVisible("Category", "Buff", "Debuff")]
    [EditorField(Label = "Friendly Only", Group = "BUFF", Order = 301, Tooltip = "Only your own units can be targeted.")]
    [JsonPropertyName("friendlyOnly")] public bool FriendlyOnly { get; set; } = true;

    [EditorVisible("Category", "Buff", "Debuff")]
    [EditorVisible("AoeType", "Chain")]
    [EditorField(Label = "Chain Qty", Group = "BUFF", Order = 302, Tooltip = "Planned chain-jump count. Not implemented yet - no effect.")]
    [JsonPropertyName("chainQuantity")] public int ChainQuantity { get; set; } = 1;

    [EditorVisible("Category", "Buff", "Debuff")]
    [EditorVisible("AoeType", "Chain")]
    [EditorField(Label = "Chain Range", Group = "BUFF", Order = 303, Step = 0.1f, Decimals = 1, Tooltip = "Planned max jump distance. Not implemented yet - no effect.")]
    [JsonPropertyName("chainRange")] public float ChainRange { get; set; } = 5.0f;

    [EditorVisible("Category", "Buff", "Debuff")]
    [EditorVisible("AoeType", "Chain")]
    [EditorField(Label = "Chain Delay", Group = "BUFF", Order = 304, Step = 0.01f, Decimals = 2, Tooltip = "Planned delay between jumps. Not implemented yet - no effect.")]
    [JsonPropertyName("chainDelay")] public float ChainDelay { get; set; } = 0.1f;

    [EditorVisible("Category", "Buff", "Debuff")]
    [EditorField(Label = "", Group = "BUFF", Order = 305, Tooltip = "Restrict which unit types this spell can target.\nNothing checked = any unit.")]
    [EditorCheckboxGrid("Units", Columns = 2, Header = "ACCEPTABLE TARGETS", HeaderColorR = 200, HeaderColorG = 180, HeaderColorB = 255)]
    [JsonPropertyName("acceptableTargets")] public List<string>? AcceptableTargets { get; set; }

    [EditorVisible("Category", "Buff", "Debuff")]
    [EditorField(Label = "Cast Effect", Group = "BUFF", Order = 306, Tooltip = "Effect played on the target when the buff lands.")]
    [JsonPropertyName("castFlipbook")] public FlipbookRef? CastFlipbook { get; set; }

    // ============ SUMMON ============
    [EditorVisible("Category", "Summon")]
    [EditorField(Label = "Target Req", Group = "SUMMON", Order = 400, GroupColorR = 80, GroupColorG = 200, GroupColorB = 255, Tooltip = "What the cast must target: nothing, a corpse, a\nspecific unit type, or all corpses in an area.")]
    [EditorCombo("None", "Corpse", "UnitType", "CorpseAOE")]
    [JsonPropertyName("summonTargetReq")] public string SummonTargetReq { get; set; } = "None";

    [EditorVisible("Category", "Summon")]
    [EditorField(Label = "Mode", Group = "SUMMON", Order = 401, Tooltip = "Spawn = create a new unit. Transform = turn the\ntarget into the summon.")]
    [EditorCombo("Spawn", "Transform")]
    [JsonPropertyName("summonMode")] public string SummonMode { get; set; } = "Spawn";

    [EditorVisible("Category", "Summon")]
    [EditorField(Label = "Unit", Group = "SUMMON", Order = 402, Tooltip = "Unit to summon. Empty + corpse target = raise\nthat corpse's own zombie type.")]
    [EditorRegistryDropdown("Units")]
    [JsonPropertyName("summonUnitID")] public string SummonUnitID { get; set; } = "";

    /// <summary>Corpse-puppet raise: raise the ZOMBIE variant of the target corpse (leave
    /// <see cref="SummonUnitID"/> empty so the type resolves per-corpse), but override its AI
    /// with the CorpsePuppet archetype (walk to the nearest Corpse Pile and deposit itself),
    /// and pile as the ORIGINAL corpse type — not the zombie it wears.</summary>
    [EditorVisible("Category", "Summon")]
    [EditorField(Label = "As Corpse Puppet", Group = "SUMMON", Order = 403, Tooltip = "Raised body walks itself to the nearest Corpse Pile\ninstead of fighting.")]
    [JsonPropertyName("summonAsPuppet")] public bool SummonAsPuppet { get; set; } = false;

    [EditorVisible("Category", "Summon")]
    [EditorField(Label = "Quantity", Group = "SUMMON", Order = 403, Tooltip = "How many units appear per cast.")]
    [JsonPropertyName("summonQuantity")] public int SummonQuantity { get; set; } = 1;

    [EditorVisible("Category", "Summon")]
    [EditorField(Label = "Spawn At", Group = "SUMMON", Order = 404, Tooltip = "Where the summoned units appear.")]
    [EditorCombo("NearestTargetToMouse", "NearestTargetToCaster", "AdjacentToCaster", "AtTargetLocation")]
    [JsonPropertyName("spawnLocation")] public string SpawnLocation { get; set; } = "AdjacentToCaster";

    [EditorVisible("Category", "Summon")]
    [EditorField(Label = "Summon Effect", Group = "SUMMON", Order = 406, Tooltip = "Effect played at each spawn point.")]
    [JsonPropertyName("summonFlipbook")] public FlipbookRef? SummonFlipbook { get; set; }

    /// <summary>Which composite reanimation effect variant plays as a raised corpse rises
    /// (ReanimEffectSystem preset id, e.g. "reanim_classic"). Empty = default/none.</summary>
    [EditorVisible("Category", "Summon")]
    [EditorField(Label = "Reanim Effect ID", Group = "SUMMON", Order = 407, Tooltip = "Which composite rise effect plays as a raised\ncorpse gets up (e.g. reanim_classic).")]
    [JsonPropertyName("reanimationEffectID")] public string ReanimationEffectID { get; set; } = "";

    /// <summary>How fast the body rises after this spell raises it: scales the standup animation
    /// (gets up quicker), the spawn delay, and the outline-morph build-up so the corpse-morph stays
    /// synced to the rise. Base default 2 (the tuned reanimation feel); a per-spell override — higher
    /// = quicker. Decoupled from the smoke (see <see cref="TestFogSpeed"/>). Clamped to a floor.</summary>
    [EditorVisible("Category", "Summon")]
    [EditorField(Label = "Rise Speed (test)", Group = "SUMMON", Order = 408, Tooltip = "How fast the body stands up. Higher = quicker rise;\nthe smoke is separate (Fog Speed).")]
    [JsonPropertyName("_test_riseSpeed")] public float TestRiseSpeed { get; set; } = 2f;

    /// <summary>TEMPORARY/debug tuning knob. How fast the reanimation SMOKE (the green
    /// cloud + dust puffs) builds and dissipates, independent of <see cref="TestRiseSpeed"/>
    /// — so the body can pop up fast while the fog lingers, or vice versa. 1 = default.
    /// Clamped to a floor.</summary>
    [EditorVisible("Category", "Summon")]
    [EditorField(Label = "Fog Speed (test)", Group = "SUMMON", Order = 409, Tooltip = "How fast the reanimation smoke builds and fades,\nindependent of the rise.")]
    [JsonPropertyName("_test_fogSpeed")] public float TestFogSpeed { get; set; } = 1f;

    // ============ STRIKE ============
    [EditorVisible("Category", "Strike")]
    [EditorField(Label = "Target: Unit (Zap)", Group = "STRIKE", Order = 500, GroupColorR = 255, GroupColorG = 255, GroupColorB = 100, Tooltip = "On = zaps a unit directly. Off = strikes a ground\npoint after a telegraph.")]
    [JsonPropertyName("strikeTargetUnit")] public bool StrikeTargetUnit { get; set; }

    [EditorVisible("Category", "Strike")]
    [EditorField(Label = "Visual", Group = "STRIKE", Order = 501, Tooltip = "Bolt look: jagged lightning or a god-ray beam.")]
    [EditorCombo("Lightning", "GodRay")]
    [JsonPropertyName("strikeVisual")] public string StrikeVisualType { get; set; } = "Lightning";

    // Ground strike fields
    [EditorVisible("Category", "Strike")]
    [EditorVisible("StrikeTargetUnit", "False")]
    [EditorField(Label = "Telegraph", Group = "STRIKE", Order = 503, Step = 0.01f, Decimals = 2, Tooltip = "Warning delay before the strike lands (sec).")]
    [JsonPropertyName("telegraphDuration")] public float TelegraphDuration { get; set; } = 0.2f;

    [EditorVisible("Category", "Strike")]
    [EditorVisible("StrikeTargetUnit", "False")]
    [EditorField(Label = "Telegraph Visible", Group = "STRIKE", Order = 504, Tooltip = "Show the warning marker during the telegraph.")]
    [JsonPropertyName("telegraphVisible")] public bool TelegraphVisible { get; set; } = true;

    [EditorVisible("Category", "Strike")]
    [EditorVisible("StrikeTargetUnit", "False")]
    [EditorField(Label = "Duration", Group = "STRIKE", Order = 505, Step = 0.01f, Decimals = 2, Tooltip = "How long the strike visual lingers (sec).")]
    [JsonPropertyName("strikeDuration")] public float StrikeDuration { get; set; } = 0.2f;

    // Zap fields
    [EditorVisible("Category", "Strike")]
    [EditorVisible("StrikeTargetUnit", "True")]
    [EditorField(Label = "Zap Duration", Group = "STRIKE", Order = 505, Step = 0.01f, Decimals = 2, Tooltip = "How long the bolt stays on a zapped unit (sec).")]
    [JsonPropertyName("zapDuration")] public float ZapDuration { get; set; } = 0.12f;

    [EditorVisible("Category", "Strike")]
    [EditorVisible("StrikeTargetUnit", "True")]
    [EditorHeader("Chain Lightning", ColorR = 120, ColorG = 120, ColorB = 140)]
    [EditorField(Label = "Chain Branches", Group = "STRIKE", Order = 506, Tooltip = "Extra targets the zap forks to at each hop.\n0 = no chaining.")]
    [JsonPropertyName("strikeChainBranches")] public int StrikeChainBranches { get; set; }

    [EditorVisible("Category", "Strike")]
    [EditorVisible("StrikeTargetUnit", "True")]
    [EditorField(Label = "Chain Depth", Group = "STRIKE", Order = 507, Tooltip = "How many hops the chain can make.")]
    [JsonPropertyName("strikeChainDepth")] public int StrikeChainDepth { get; set; }

    [EditorVisible("Category", "Strike")]
    [EditorVisible("StrikeTargetUnit", "True")]
    [EditorField(Label = "Chain Range", Group = "STRIKE", Order = 508, Step = 0.1f, Decimals = 1, Tooltip = "Max distance to the next chain target.")]
    [JsonPropertyName("strikeChainRange")] public float StrikeChainRange { get; set; } = 8.0f;

    [EditorVisible("Category", "Strike")]
    [EditorVisible("StrikeTargetUnit", "True")]
    [EditorField(Label = "Chain W.Decay", Group = "STRIKE", Order = 509, Step = 0.01f, Decimals = 2, Tooltip = "Bolt width multiplier per hop; below 1 thins each fork.")]
    [JsonPropertyName("strikeChainWidthDecay")] public float StrikeChainWidthDecay { get; set; } = 0.8f;

    // Common lightning style
    [EditorVisible("Category", "Strike")]
    [EditorField(Label = "Displacement", Group = "STRIKE", Order = 510, Step = 0.01f, Decimals = 2, Tooltip = "Bolt jaggedness. Higher = wilder zigzag.")]
    [JsonPropertyName("strikeDisplacement")] public float StrikeDisplacement { get; set; } = 0.35f;

    [EditorVisible("Category", "Strike")]
    [EditorField(Label = "Branches", Group = "STRIKE", Order = 511, Tooltip = "Cosmetic side-branches drawn on the bolt.")]
    [JsonPropertyName("strikeBranches")] public int StrikeBranches { get; set; } = 3;

    [EditorVisible("Category", "Strike")]
    [EditorField(Label = "Core Width", Group = "STRIKE", Order = 512, Step = 0.1f, Decimals = 1, Tooltip = "Thickness of the bright center line (px).")]
    [JsonPropertyName("strikeCoreWidth")] public float StrikeCoreWidth { get; set; } = 2.0f;

    [EditorVisible("Category", "Strike")]
    [EditorField(Label = "Glow Width", Group = "STRIKE", Order = 513, Step = 0.1f, Decimals = 1, Tooltip = "Thickness of the soft outer glow (px).")]
    [JsonPropertyName("strikeGlowWidth")] public float StrikeGlowWidth { get; set; } = 8.0f;

    // 0 = the bolt keeps full width for its whole life (only brightness fades);
    // 1 = width shrinks with the fade (the classic collapse-to-a-thread look).
    [EditorVisible("Category", "Strike")]
    [EditorField(Label = "Width Fade", Group = "STRIKE", Order = 514, Step = 0.01f, Decimals = 2, Tooltip = "0 = width holds while fading out; 1 = bolt collapses\nto a thread as it fades.")]
    [JsonPropertyName("strikeWidthFade")] public float StrikeWidthFade { get; set; } = 1.0f;

    [EditorVisible("Category", "Strike")]
    [EditorField(Label = "Flicker Min", Group = "STRIKE", Order = 515, Step = 0.01f, Decimals = 2, Tooltip = "Brightness flicker floor (1 = steady).")]
    [JsonPropertyName("strikeFlickerMin")] public float StrikeFlickerMin { get; set; } = 1.0f;

    [EditorVisible("Category", "Strike")]
    [EditorField(Label = "Flicker Max", Group = "STRIKE", Order = 516, Step = 0.01f, Decimals = 2, Tooltip = "Brightness flicker ceiling (1 = steady).")]
    [JsonPropertyName("strikeFlickerMax")] public float StrikeFlickerMax { get; set; } = 1.0f;

    [EditorVisible("Category", "Strike")]
    [EditorField(Label = "Flicker Hz", Group = "STRIKE", Order = 517, Step = 0.1f, Decimals = 1, Tooltip = "Flickers per second. 0 = off.")]
    [JsonPropertyName("strikeFlickerHz")] public float StrikeFlickerHz { get; set; }

    [EditorVisible("Category", "Strike")]
    [EditorField(Label = "Jitter Hz", Group = "STRIKE", Order = 518, Step = 0.1f, Decimals = 1, Tooltip = "How often the bolt re-rolls its zigzag. 0 = static.")]
    [JsonPropertyName("strikeJitterHz")] public float StrikeJitterHz { get; set; }

    [EditorVisible("Category", "Strike")]
    [EditorField(Label = "Core Color", Group = "STRIKE", Order = 519, Compact = true, Tooltip = "Color of the bright center (intensity = bloom).")]
    [JsonPropertyName("strikeCoreColor")] [JsonConverter(typeof(HdrColorJsonConverter))] public HdrColor StrikeCoreColor { get; set; } = new(255, 255, 255, 255, 4.0f);

    [EditorVisible("Category", "Strike")]
    [EditorField(Label = "Glow Color", Group = "STRIKE", Order = 520, Compact = true, Tooltip = "Color of the outer glow (intensity = bloom).")]
    [JsonPropertyName("strikeGlowColor")] [JsonConverter(typeof(HdrColorJsonConverter))] public HdrColor StrikeGlowColor { get; set; } = new(140, 180, 255, 200, 2.5f);

    // God Ray sub-fields
    [EditorVisible("Category", "Strike")]
    [EditorVisible("StrikeVisualType", "GodRay")]
    [EditorHeader("GOD RAY", ColorR = 255, ColorG = 220, ColorB = 100)]
    [EditorField(Label = "Edge Softness", Group = "STRIKE", Order = 521, Step = 0.01f, Decimals = 2, Tooltip = "Edge feathering: 0 = hard edges, 1 = soft.")]
    [JsonPropertyName("godRayEdgeSoftness")] public float GodRayEdgeSoftness { get; set; } = 0.4f;

    [EditorVisible("Category", "Strike")]
    [EditorVisible("StrikeVisualType", "GodRay")]
    [EditorField(Label = "Noise Strength", Group = "STRIKE", Order = 522, Step = 0.01f, Decimals = 2, Tooltip = "How much the smoky ripple distorts the ray.")]
    [JsonPropertyName("godRayNoiseStrength")] public float GodRayNoiseStrength { get; set; } = 0.35f;

    [EditorVisible("Category", "Strike")]
    [EditorVisible("StrikeVisualType", "GodRay")]
    [EditorField(Label = "Noise Speed", Group = "STRIKE", Order = 523, Step = 0.1f, Decimals = 1, Tooltip = "How fast the ripple churns.")]
    [JsonPropertyName("godRayNoiseSpeed")] public float GodRayNoiseSpeed { get; set; } = 1.5f;

    [EditorVisible("Category", "Strike")]
    [EditorVisible("StrikeVisualType", "GodRay")]
    [EditorField(Label = "Noise Scale", Group = "STRIKE", Order = 524, Step = 0.1f, Decimals = 1, Tooltip = "Ripple detail: higher = finer wisps.")]
    [JsonPropertyName("godRayNoiseScale")] public float GodRayNoiseScale { get; set; } = 3.0f;

    // ============ BEAM ============
    // Shared hold-channel behavior (Beam + Drain): while the caster channels, stop
    // its voluntary movement (the player's WASD is ignored; facing stays locked to
    // the channel target either way). Off = the caster may walk while channeling.
    [EditorVisible("Category", "Beam", "Drain")]
    [EditorField(Label = "Stop Movement", Group = "BEAM", Order = 600, GroupColorR = 100, GroupColorG = 220, GroupColorB = 255, Tooltip = "Caster stands still while channeling\n(player movement input ignored).")]
    [JsonPropertyName("channelStopsMovement")] public bool ChannelStopsMovement { get; set; } = true;

    [EditorVisible("Category", "Beam")]
    [EditorField(Label = "Tick Rate", Group = "BEAM", Order = 601, Step = 0.01f, Decimals = 2, GroupColorR = 100, GroupColorG = 220, GroupColorB = 255, Tooltip = "Seconds between damage ticks while channeling.")]
    [JsonPropertyName("beamTickRate")] public float BeamTickRate { get; set; } = 0.25f;

    [EditorVisible("Category", "Beam")]
    [EditorField(Label = "Max Duration", Group = "BEAM", Order = 602, Step = 0.1f, Decimals = 1, Tooltip = "Channel auto-ends after this many seconds. 0 = unlimited.")]
    [JsonPropertyName("beamMaxDuration")] public float BeamMaxDuration { get; set; }

    [EditorVisible("Category", "Beam")]
    [EditorField(Label = "Retarget Radius", Group = "BEAM", Order = 603, Step = 0.1f, Decimals = 1, Tooltip = "If the target dies, the beam jumps to the nearest\nenemy within this range. 0 = the beam ends.")]
    [JsonPropertyName("beamRetargetRadius")] public float BeamRetargetRadius { get; set; } = 15.0f;

    [EditorVisible("Category", "Beam")]
    [EditorField(Label = "Displacement", Group = "BEAM", Order = 604, Step = 0.01f, Decimals = 2, Tooltip = "Beam jaggedness. Higher = wilder zigzag.")]
    [JsonPropertyName("beamDisplacement")] public float BeamDisplacement { get; set; } = 0.25f;

    [EditorVisible("Category", "Beam")]
    [EditorField(Label = "Branches", Group = "BEAM", Order = 605, Tooltip = "Cosmetic side-branches drawn on the beam.")]
    [JsonPropertyName("beamBranches")] public int BeamBranches { get; set; } = 1;

    [EditorVisible("Category", "Beam")]
    [EditorField(Label = "Core Width", Group = "BEAM", Order = 606, Step = 0.1f, Decimals = 1, Tooltip = "Thickness of the bright center line (px).")]
    [JsonPropertyName("beamCoreWidth")] public float BeamCoreWidth { get; set; } = 1.5f;

    [EditorVisible("Category", "Beam")]
    [EditorField(Label = "Glow Width", Group = "BEAM", Order = 607, Step = 0.1f, Decimals = 1, Tooltip = "Thickness of the soft outer glow (px).")]
    [JsonPropertyName("beamGlowWidth")] public float BeamGlowWidth { get; set; } = 6.0f;

    [EditorVisible("Category", "Beam")]
    [EditorField(Label = "Flicker Min", Group = "BEAM", Order = 608, Step = 0.01f, Decimals = 2, Tooltip = "Brightness flicker floor (1 = steady).")]
    [JsonPropertyName("beamFlickerMin")] public float BeamFlickerMin { get; set; } = 1.0f;

    [EditorVisible("Category", "Beam")]
    [EditorField(Label = "Flicker Max", Group = "BEAM", Order = 609, Step = 0.01f, Decimals = 2, Tooltip = "Brightness flicker ceiling (1 = steady).")]
    [JsonPropertyName("beamFlickerMax")] public float BeamFlickerMax { get; set; } = 1.0f;

    [EditorVisible("Category", "Beam")]
    [EditorField(Label = "Flicker Hz", Group = "BEAM", Order = 610, Step = 0.1f, Decimals = 1, Tooltip = "Flickers per second. 0 = off.")]
    [JsonPropertyName("beamFlickerHz")] public float BeamFlickerHz { get; set; }

    [EditorVisible("Category", "Beam")]
    [EditorField(Label = "Jitter Hz", Group = "BEAM", Order = 611, Step = 0.1f, Decimals = 1, Tooltip = "How often the beam re-rolls its zigzag. 0 = static.")]
    [JsonPropertyName("beamJitterHz")] public float BeamJitterHz { get; set; }

    [EditorVisible("Category", "Beam")]
    [EditorField(Label = "Core Color", Group = "BEAM", Order = 612, Compact = true, Tooltip = "Color of the bright center (intensity = bloom).")]
    [JsonPropertyName("beamCoreColor")] [JsonConverter(typeof(HdrColorJsonConverter))] public HdrColor BeamCoreColor { get; set; } = new(255, 255, 255, 255, 3.0f);

    [EditorVisible("Category", "Beam")]
    [EditorField(Label = "Glow Color", Group = "BEAM", Order = 613, Compact = true, Tooltip = "Color of the outer glow (intensity = bloom).")]
    [JsonPropertyName("beamGlowColor")] [JsonConverter(typeof(HdrColorJsonConverter))] public HdrColor BeamGlowColor { get; set; } = new(100, 160, 255, 180, 2.0f);

    [EditorVisible("Category", "Beam")]
    [EditorHeader("Chain Lightning", ColorR = 120, ColorG = 120, ColorB = 140)]
    [EditorField(Label = "Chain Branches", Group = "BEAM", Order = 614, Tooltip = "Extra targets the beam forks to at each hop.\n0 = no chaining.")]
    [JsonPropertyName("beamChainBranches")] public int BeamChainBranches { get; set; }

    [EditorVisible("Category", "Beam")]
    [EditorField(Label = "Chain Depth", Group = "BEAM", Order = 615, Tooltip = "How many hops the chain can make.")]
    [JsonPropertyName("beamChainDepth")] public int BeamChainDepth { get; set; }

    [EditorVisible("Category", "Beam")]
    [EditorField(Label = "Chain Range", Group = "BEAM", Order = 616, Step = 0.1f, Decimals = 1, Tooltip = "Max distance to the next chain target.")]
    [JsonPropertyName("beamChainRange")] public float BeamChainRange { get; set; } = 8.0f;

    [EditorVisible("Category", "Beam")]
    [EditorField(Label = "Chain W.Decay", Group = "BEAM", Order = 617, Step = 0.01f, Decimals = 2, Tooltip = "Beam width multiplier per hop; below 1 thins each fork.")]
    [JsonPropertyName("beamChainWidthDecay")] public float BeamChainWidthDecay { get; set; } = 0.8f;

    // ============ DRAIN ============
    [EditorVisible("Category", "Drain")]
    [EditorField(Label = "Tick Rate", Group = "DRAIN", Order = 701, Step = 0.01f, Decimals = 2, GroupColorR = 80, GroupColorG = 255, GroupColorB = 80, Tooltip = "Seconds between drain ticks.")]
    [JsonPropertyName("drainTickRate")] public float DrainTickRate { get; set; } = 0.25f;

    [EditorVisible("Category", "Drain")]
    [EditorField(Label = "Heal %", Group = "DRAIN", Order = 702, Step = 0.01f, Decimals = 2, Tooltip = "Fraction of drained HP returned to the caster\nas healing (1 = all of it).")]
    [JsonPropertyName("drainHealPercent")] public float DrainHealPercent { get; set; } = 1.0f;

    [EditorVisible("Category", "Drain")]
    [EditorField(Label = "Corpse HP", Group = "DRAIN", Order = 703, Tooltip = "HP drained per tick when draining a corpse.")]
    [JsonPropertyName("drainCorpseHP")] public int DrainCorpseHP { get; set; } = 10;

    [EditorVisible("Category", "Drain")]
    [EditorField(Label = "Max Duration", Group = "DRAIN", Order = 704, Step = 0.1f, Decimals = 1, Tooltip = "Channel auto-ends after this many seconds. 0 = unlimited.")]
    [JsonPropertyName("drainMaxDuration")] public float DrainMaxDuration { get; set; }

    [EditorVisible("Category", "Drain")]
    [EditorField(Label = "Break Range", Group = "DRAIN", Order = 705, Step = 0.1f, Decimals = 1, Tooltip = "Not wired up yet - no effect.")]
    [JsonPropertyName("drainBreakRange")] public float DrainBreakRange { get; set; }

    [EditorVisible("Category", "Drain")]
    [EditorField(Label = "Reversed", Group = "DRAIN", Order = 706, Tooltip = "Reverses the mechanic: the caster feeds HP to the\ntarget instead of draining it.")]
    [JsonPropertyName("drainReversed")] public bool DrainReversed { get; set; }

    [EditorVisible("Category", "Drain")]
    [EditorField(Label = "Visual Reversed", Group = "DRAIN", Order = 707, Tooltip = "Flips only the visual flow direction, not the mechanic.")]
    [JsonPropertyName("drainVisualReversed")] public bool DrainVisualReversed { get; set; }

    [EditorVisible("Category", "Drain")]
    [EditorHeader("Visuals", ColorR = 120, ColorG = 120, ColorB = 140)]
    [EditorField(Label = "Tendrils", Group = "DRAIN", Order = 708, Tooltip = "Number of beam strands.")]
    [JsonPropertyName("drainTendrilCount")] public int DrainTendrilCount { get; set; } = 3;

    [EditorVisible("Category", "Drain")]
    [EditorField(Label = "Arc Height", Group = "DRAIN", Order = 709, Step = 0.1f, Decimals = 1, Tooltip = "How high the strands bow upward.")]
    [JsonPropertyName("drainArcHeight")] public float DrainArcHeight { get; set; } = 40.0f;

    [EditorVisible("Category", "Drain")]
    [EditorField(Label = "Sway Amp", Group = "DRAIN", Order = 710, Step = 0.1f, Decimals = 1, Tooltip = "How far the strands wave side to side.")]
    [JsonPropertyName("drainSwayAmplitude")] public float DrainSwayAmplitude { get; set; } = 8.0f;

    [EditorVisible("Category", "Drain")]
    [EditorField(Label = "Sway Hz", Group = "DRAIN", Order = 711, Step = 0.1f, Decimals = 1, Tooltip = "How fast the strands wave.")]
    [JsonPropertyName("drainSwayHz")] public float DrainSwayHz { get; set; } = 1.5f;

    [EditorVisible("Category", "Drain")]
    [EditorField(Label = "Core Width", Group = "DRAIN", Order = 712, Step = 0.1f, Decimals = 1, Tooltip = "Thickness of each strand's bright center (px).")]
    [JsonPropertyName("drainCoreWidth")] public float DrainCoreWidth { get; set; } = 1.5f;

    [EditorVisible("Category", "Drain")]
    [EditorField(Label = "Glow Width", Group = "DRAIN", Order = 713, Step = 0.1f, Decimals = 1, Tooltip = "Thickness of each strand's outer glow (px).")]
    [JsonPropertyName("drainGlowWidth")] public float DrainGlowWidth { get; set; } = 5.0f;

    [EditorVisible("Category", "Drain")]
    [EditorField(Label = "Pulse Hz", Group = "DRAIN", Order = 714, Step = 0.1f, Decimals = 1, Tooltip = "Throb speed of the beam.")]
    [JsonPropertyName("drainPulseHz")] public float DrainPulseHz { get; set; } = 2.0f;

    [EditorVisible("Category", "Drain")]
    [EditorField(Label = "Pulse Str", Group = "DRAIN", Order = 715, Step = 0.01f, Decimals = 2, Tooltip = "Throb depth. 0 = steady beam.")]
    [JsonPropertyName("drainPulseStrength")] public float DrainPulseStrength { get; set; } = 0.4f;

    [EditorVisible("Category", "Drain")]
    [EditorField(Label = "Flicker Min", Group = "DRAIN", Order = 716, Step = 0.01f, Decimals = 2, Tooltip = "Brightness flicker floor (1 = steady).")]
    [JsonPropertyName("drainFlickerMin")] public float DrainFlickerMin { get; set; } = 0.8f;

    [EditorVisible("Category", "Drain")]
    [EditorField(Label = "Flicker Max", Group = "DRAIN", Order = 717, Step = 0.01f, Decimals = 2, Tooltip = "Brightness flicker ceiling (1 = steady).")]
    [JsonPropertyName("drainFlickerMax")] public float DrainFlickerMax { get; set; } = 1.2f;

    [EditorVisible("Category", "Drain")]
    [EditorField(Label = "Core Color", Group = "DRAIN", Order = 718, Compact = true, Tooltip = "Beam center color at the receiving end (the caster\non a normal drain). Intensity = bloom.")]
    [JsonPropertyName("drainCoreColor")] [JsonConverter(typeof(HdrColorJsonConverter))] public HdrColor DrainCoreColor { get; set; } = new(120, 255, 80, 255, 2.5f);

    [EditorVisible("Category", "Drain")]
    [EditorField(Label = "Glow Color", Group = "DRAIN", Order = 719, Compact = true, Tooltip = "Beam glow color at the receiving end (the caster\non a normal drain). Intensity = bloom.")]
    [JsonPropertyName("drainGlowColor")] [JsonConverter(typeof(HdrColorJsonConverter))] public HdrColor DrainGlowColor { get; set; } = new(40, 120, 20, 160, 1.5f);

    /// <summary>Width multiplier at the flow-source end (the target on a normal drain):
    /// 1 = uniform beam, >1 = Pugna-style funnel, narrow at the destination.</summary>
    [EditorVisible("Category", "Drain")]
    [EditorField(Label = "Source Width x", Group = "DRAIN", Order = 720, Step = 0.1f, Decimals = 1, Tooltip = "Width multiplier at the giving end: above 1 =\nfunnel shape, narrow at the receiver.")]
    [JsonPropertyName("drainSourceWidthScale")] public float DrainSourceWidthScale { get; set; } = 1.0f;

    /// <summary>Cloud puffs traveling along the beam from the flow source to the
    /// destination. 0 = off.</summary>
    [EditorVisible("Category", "Drain")]
    [EditorField(Label = "Clouds", Group = "DRAIN", Order = 721, Tooltip = "Puffs traveling along the beam. 0 = off.")]
    [JsonPropertyName("drainCloudCount")] public int DrainCloudCount { get; set; }

    [EditorVisible("Category", "Drain")]
    [EditorField(Label = "Cloud Size", Group = "DRAIN", Order = 722, Step = 0.5f, Decimals = 1, Tooltip = "Size of the traveling puffs.")]
    [JsonPropertyName("drainCloudSize")] public float DrainCloudSize { get; set; } = 10.0f;

    /// <summary>Cloud travel speed in beam-lengths per second.</summary>
    [EditorVisible("Category", "Drain")]
    [EditorField(Label = "Cloud Speed", Group = "DRAIN", Order = 723, Step = 0.05f, Decimals = 2, Tooltip = "Puff travel speed, in beam-lengths per second.")]
    [JsonPropertyName("drainCloudSpeed")] public float DrainCloudSpeed { get; set; } = 0.5f;

    [EditorVisible("Category", "Drain")]
    [EditorField(Label = "Cloud Color", Group = "DRAIN", Order = 724, Compact = true, Tooltip = "Tint of the traveling puffs.")]
    [JsonPropertyName("drainCloudColor")] [JsonConverter(typeof(HdrColorJsonConverter))] public HdrColor DrainCloudColor { get; set; } = new(200, 255, 160, 255, 3f);

    /// <summary>Beam core/glow color at the flow-source end (the target on a normal
    /// drain) — the destination end uses drainCoreColor/drainGlowColor and the beam
    /// lerps between them. Defaults match the base colors' defaults (no gradient).</summary>
    [EditorVisible("Category", "Drain")]
    [EditorField(Label = "Src Core Color", Group = "DRAIN", Order = 725, Compact = true, Tooltip = "Beam center color at the giving end; blends into\nthe Core Color along the beam.")]
    [JsonPropertyName("drainSourceCoreColor")] [JsonConverter(typeof(HdrColorJsonConverter))] public HdrColor DrainSourceCoreColor { get; set; } = new(120, 255, 80, 255, 2.5f);

    [EditorVisible("Category", "Drain")]
    [EditorField(Label = "Src Glow Color", Group = "DRAIN", Order = 726, Compact = true, Tooltip = "Beam glow color at the giving end; blends into\nthe Glow Color along the beam.")]
    [JsonPropertyName("drainSourceGlowColor")] [JsonConverter(typeof(HdrColorJsonConverter))] public HdrColor DrainSourceGlowColor { get; set; } = new(40, 120, 20, 160, 1.5f);

    /// <summary>Scrolling streak-noise overlay traveling from the flow source to
    /// the destination. Speed in px/sec (0 = off).</summary>
    [EditorVisible("Category", "Drain")]
    [EditorField(Label = "Scroll Speed", Group = "DRAIN", Order = 727, Step = 5f, Decimals = 0, Tooltip = "Streak overlay scroll speed (px/sec). 0 = off.")]
    [JsonPropertyName("drainScrollSpeed")] public float DrainScrollSpeed { get; set; }

    /// <summary>Pixels per repeat of the scroll noise texture.</summary>
    [EditorVisible("Category", "Drain")]
    [EditorField(Label = "Scroll Scale", Group = "DRAIN", Order = 728, Step = 5f, Decimals = 0, Tooltip = "Pixels per streak-noise repeat; higher = longer streaks.")]
    [JsonPropertyName("drainScrollScale")] public float DrainScrollScale { get; set; } = 90.0f;

    [EditorVisible("Category", "Drain")]
    [EditorField(Label = "Scroll Alpha", Group = "DRAIN", Order = 729, Step = 0.05f, Decimals = 2, Tooltip = "Opacity of the streak overlay.")]
    [JsonPropertyName("drainScrollAlpha")] public float DrainScrollAlpha { get; set; } = 0.5f;

    /// <summary>Flipbook puffs churning over the beam/target junction (drawn in
    /// front of the beam, tinted like the source-end core color). 0 = off.</summary>
    [EditorVisible("Category", "Drain")]
    [EditorField(Label = "Impact Puffs", Group = "DRAIN", Order = 730, Tooltip = "Puffs churning where the beam meets the target. 0 = off.")]
    [JsonPropertyName("drainImpactPuffs")] public int DrainImpactPuffs { get; set; }

    [EditorVisible("Category", "Drain")]
    [EditorField(Label = "Impact Size", Group = "DRAIN", Order = 731, Step = 0.5f, Decimals = 1, Tooltip = "Size of the impact puffs.")]
    [JsonPropertyName("drainImpactSize")] public float DrainImpactSize { get; set; } = 14.0f;

    [EditorVisible("Category", "Drain")]
    [EditorField(Label = "Impact Flipbook", Group = "DRAIN", Order = 732, Tooltip = "Flipbook id used for the impact puffs.")]
    [JsonPropertyName("drainImpactFlipbook")] public string DrainImpactFlipbook { get; set; } = "cloud03";

    /// <summary>Additive flare size multiplier at the beam endpoints (0 = off).</summary>
    [EditorVisible("Category", "Drain")]
    [EditorField(Label = "Impact Flare x", Group = "DRAIN", Order = 733, Step = 0.1f, Decimals = 1, Tooltip = "Bright flare size at the beam endpoints. 0 = off.")]
    [JsonPropertyName("drainImpactFlareScale")] public float DrainImpactFlareScale { get; set; } = 1.0f;

    [EditorVisible("Category", "Drain")]
    [EditorField(Label = "Target Effect", Group = "DRAIN", Order = 734, Tooltip = "Effect played on the drained target.")]
    [JsonPropertyName("drainTargetEffect")] public FlipbookRef? DrainTargetEffect { get; set; }

    // ============ CLOUD ============
    [EditorVisible("Category", "Cloud")]
    [EditorField(Label = "Radius", Group = "CLOUD", Order = 800, Step = 0.1f, Decimals = 1, GroupColorR = 80, GroupColorG = 200, GroupColorB = 80, Tooltip = "Cloud radius at cast time.")]
    [JsonPropertyName("cloudRadius")] public float CloudRadius { get; set; } = 5.0f;

    [EditorVisible("Category", "Cloud")]
    [EditorField(Label = "Max Radius", Group = "CLOUD", Order = 801, Step = 0.1f, Decimals = 1, Tooltip = "Largest the cloud can grow by eating corpses.")]
    [JsonPropertyName("cloudMaxRadius")] public float CloudMaxRadius { get; set; } = 10.0f;

    [EditorVisible("Category", "Cloud")]
    [EditorField(Label = "Duration", Group = "CLOUD", Order = 802, Step = 0.1f, Decimals = 1, Tooltip = "Seconds the cloud lasts (before corpse bonuses).")]
    [JsonPropertyName("cloudDuration")] public float CloudDuration { get; set; } = 12.0f;

    [EditorVisible("Category", "Cloud")]
    [EditorField(Label = "Tick Rate", Group = "CLOUD", Order = 803, Step = 0.1f, Decimals = 1, Tooltip = "Seconds between damage ticks inside the cloud.")]
    [JsonPropertyName("cloudTickRate")] public float CloudTickRate { get; set; } = 1.0f;

    [EditorVisible("Category", "Cloud")]
    [EditorField(Label = "Tick Damage", Group = "CLOUD", Order = 804, Tooltip = "Damage per tick to units inside.")]
    [JsonPropertyName("cloudTickDamage")] public int CloudTickDamage { get; set; } = 3;

    [EditorVisible("Category", "Cloud")]
    [EditorHeader("CORPSE FEEDING", ColorR = 160, ColorG = 255, ColorB = 120)]
    [EditorField(Label = "Corpse Bonus", Group = "CLOUD", Order = 810, Tooltip = "Cloud eats corpses inside it to grow bigger,\nlast longer and hit harder.")]
    [JsonPropertyName("cloudCorpseBonus")] public bool CloudCorpseBonus { get; set; } = true;

    [EditorVisible("Category", "Cloud")]
    [EditorField(Label = "Consume Rate", Group = "CLOUD", Order = 811, Step = 0.1f, Decimals = 1, Tooltip = "Seconds between corpse meals. Higher = slower eating.")]
    [JsonPropertyName("cloudCorpseConsumeRate")] public float CloudCorpseConsumeRate { get; set; } = 2.0f;

    [EditorVisible("Category", "Cloud")]
    [EditorField(Label = "Radius Bonus", Group = "CLOUD", Order = 812, Step = 0.1f, Decimals = 1, Tooltip = "Radius gained per corpse eaten.")]
    [JsonPropertyName("cloudCorpseRadiusBonus")] public float CloudCorpseRadiusBonus { get; set; } = 1.5f;

    [EditorVisible("Category", "Cloud")]
    [EditorField(Label = "Duration Bonus", Group = "CLOUD", Order = 813, Step = 0.1f, Decimals = 1, Tooltip = "Seconds gained per corpse eaten.")]
    [JsonPropertyName("cloudCorpseDurationBonus")] public float CloudCorpseDurationBonus { get; set; } = 3.0f;

    [EditorVisible("Category", "Cloud")]
    [EditorField(Label = "Potency Bonus", Group = "CLOUD", Order = 814, Step = 0.01f, Decimals = 2, Tooltip = "Damage fraction gained per corpse eaten (0.25 = +25%).")]
    [JsonPropertyName("cloudCorpsePotencyBonus")] public float CloudCorpsePotencyBonus { get; set; } = 0.25f;

    [EditorVisible("Category", "Cloud")]
    [EditorHeader("PHASES", ColorR = 180, ColorG = 140, ColorB = 255)]
    [EditorField(Label = "Eruption Dur", Group = "CLOUD", Order = 820, Step = 0.1f, Decimals = 1, Tooltip = "Opening burst phase length (sec).")]
    [JsonPropertyName("cloudEruptionDuration")] public float CloudEruptionDuration { get; set; } = 2.0f;

    [EditorVisible("Category", "Cloud")]
    [EditorField(Label = "Spread Dur", Group = "CLOUD", Order = 821, Step = 0.1f, Decimals = 1, Tooltip = "Seconds the cloud takes to expand to full size.")]
    [JsonPropertyName("cloudSpreadDuration")] public float CloudSpreadDuration { get; set; } = 7.0f;

    [EditorVisible("Category", "Cloud")]
    [EditorHeader("DEBUFFS", ColorR = 255, ColorG = 120, ColorB = 120)]
    [EditorField(Label = "Slow Buff", Group = "CLOUD", Order = 830, Tooltip = "Slow debuff applied to units inside.")]
    [EditorRegistryDropdown("Buffs")]
    [JsonPropertyName("cloudSlowBuffID")] public string CloudSlowBuffID { get; set; } = "";

    [EditorVisible("Category", "Cloud")]
    [EditorField(Label = "Plagued Buff", Group = "CLOUD", Order = 831, Tooltip = "Debuff applied after enough time inside.")]
    [EditorRegistryDropdown("Buffs")]
    [JsonPropertyName("cloudPlaguedBuffID")] public string CloudPlaguedBuffID { get; set; } = "";

    [EditorVisible("Category", "Cloud")]
    [EditorField(Label = "Plague Threshold", Group = "CLOUD", Order = 832, Step = 0.1f, Decimals = 1, Tooltip = "Seconds of exposure before the plague debuff applies.")]
    [JsonPropertyName("cloudPlagueThreshold")] public float CloudPlagueThreshold { get; set; } = 3.0f;

    [EditorVisible("Category", "Cloud")]
    [EditorField(Label = "Applies Paralysis", Group = "CLOUD", Order = 833, Tooltip = "Units inside are paralyzed.")]
    [JsonPropertyName("cloudAppliesParalysis")] public bool CloudAppliesParalysis { get; set; } = false;

    [EditorVisible("Category", "Cloud")]
    [EditorField(Label = "Cloud Color", Group = "CLOUD", Order = 835, Compact = true, Tooltip = "Main smoke tint.")]
    [JsonPropertyName("cloudColor")]
    [JsonConverter(typeof(HdrColorJsonConverter))]
    public HdrColor CloudColor { get; set; } = new(90, 180, 55, 255, 1.0f);

    [EditorVisible("Category", "Cloud")]
    [EditorField(Label = "Glow Color", Group = "CLOUD", Order = 836, Compact = true, Tooltip = "Glow tint layered over the smoke.")]
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
    [EditorField(Label = "Heal Flat", Group = "SACRIFICE", Order = 900, GroupColorR = 255, GroupColorG = 90, GroupColorB = 90, Tooltip = "Flat HP the caster heals per sacrifice.")]
    [JsonPropertyName("sacrificeHealFlat")] public int SacrificeHealFlat { get; set; } = 20;

    [EditorVisible("Category", "Sacrifice")]
    [EditorField(Label = "Heal % MaxHP", Group = "SACRIFICE", Order = 901, Step = 0.05f, Decimals = 2, Tooltip = "Extra heal as a fraction of the victim's max HP\n(0.5 = half their max HP).")]
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
    [EditorField(Label = "Blight Mode", Group = "BLIGHT", Order = 920, GroupColorR = 120, GroupColorG = 200, GroupColorB = 110, Tooltip = "Add = dump blight on the target cell.\nPurify = cleanse a 5x5 area around it.")]
    [EditorCombo("Add", "Purify")]
    [JsonPropertyName("blightMode")] public string BlightMode { get; set; } = "Add";

    [EditorVisible("Category", "Blight")]
    [EditorField(Label = "Blight Amount", Group = "BLIGHT", Order = 921, Decimals = 2, Tooltip = "How much blight is added or cleansed.")]
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
        WidthFade = StrikeWidthFade,
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
        // Mechanic direction sets the visual flow; the visual knob flips it from there.
        FlowReversed = DrainReversed ^ DrainVisualReversed,
        SourceWidthScale = DrainSourceWidthScale,
        CloudCount = DrainCloudCount,
        CloudSize = DrainCloudSize,
        CloudSpeed = DrainCloudSpeed,
        CloudColor = DrainCloudColor,
        SourceCoreColor = DrainSourceCoreColor,
        SourceGlowColor = DrainSourceGlowColor,
        ScrollSpeed = DrainScrollSpeed,
        ScrollScale = Math.Max(8f, DrainScrollScale),
        ScrollAlpha = DrainScrollAlpha,
        ImpactPuffCount = DrainImpactPuffs,
        ImpactSize = DrainImpactSize,
        ImpactFlipbookID = string.IsNullOrEmpty(DrainImpactFlipbook) ? "cloud03" : DrainImpactFlipbook,
        ImpactFlareScale = DrainImpactFlareScale,
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
