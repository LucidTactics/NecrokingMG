using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using Necroking.Core;

namespace Necroking.Data.Registries;

public class BuffEffect
{
    [JsonPropertyName("type")] public string Type { get; set; } = "Add";
    [JsonPropertyName("stat")] public string Stat { get; set; } = "Strength";
    [JsonPropertyName("value")] public float Value { get; set; }

    // Parsed view (identity-cached, never serialized). Stat stays a string by
    // design — it's an open vocabulary (BuffStat names + MaxMana/AllPaths/Path*/
    // ... resource keys); Type is the closed Add/Multiply/Set set the modifier
    // loops switch on, where a typo used to be silently ignored.
    private CachedEnum<BuffEffectType> _typeCache;
    [JsonIgnore] public BuffEffectType TypeEnum => _typeCache.Get(Type, BuffEffectType.Unknown);
}

public class OrbitalVisual
{
    [JsonPropertyName("flipbookID")] public string FlipbookID { get; set; } = "";
    [JsonPropertyName("orbScale")] public float OrbScale { get; set; } = 1.0f;
    [JsonPropertyName("orbColor")] [JsonConverter(typeof(HdrColorJsonConverter))] public HdrColor OrbColor { get; set; } = new();
    [JsonPropertyName("sunOrbitRadius")] public float SunOrbitRadius { get; set; } = 1.2f;
    [JsonPropertyName("sunOrbitSpeed")] public float SunOrbitSpeed { get; set; }
    [JsonPropertyName("moonOrbitRadius")] public float MoonOrbitRadius { get; set; } = 0.15f;
    [JsonPropertyName("moonOrbitSpeed")] public float MoonOrbitSpeed { get; set; } = 3.0f;
    [JsonPropertyName("orbCount")] public int OrbCount { get; set; } = 1;
    [JsonPropertyName("orbCountMatchesStacks")] public bool OrbCountMatchesStacks { get; set; } = true;
}

public class GroundAuraVisual
{
    [JsonPropertyName("flipbookID")] public string FlipbookID { get; set; } = "";
    [JsonPropertyName("scale")] public float Scale { get; set; } = 2.0f;
    [JsonPropertyName("color")] [JsonConverter(typeof(HdrColorJsonConverter))] public HdrColor Color { get; set; } = new();
    [JsonPropertyName("blendMode")] public int BlendMode { get; set; } = 1;
    [JsonPropertyName("pulseSpeed")] public float PulseSpeed { get; set; }
    [JsonPropertyName("pulseAmount")] public float PulseAmount { get; set; }
}

public class UprightEffectVisual
{
    [JsonPropertyName("flipbookID")] public string FlipbookID { get; set; } = "";
    [JsonPropertyName("scale")] public float Scale { get; set; } = 2.0f;
    [JsonPropertyName("color")] [JsonConverter(typeof(HdrColorJsonConverter))] public HdrColor Color { get; set; } = new();
    [JsonPropertyName("blendMode")] public int BlendMode { get; set; } = 1;
    [JsonPropertyName("yOffset")] public float YOffset { get; set; }
    [JsonPropertyName("pinToEffectSpawn")] public bool PinToEffectSpawn { get; set; }
}

public class LightningAuraVisual
{
    [JsonPropertyName("arcCount")] public int ArcCount { get; set; } = 3;
    [JsonPropertyName("arcRadius")] public float ArcRadius { get; set; } = 1.0f;
    [JsonPropertyName("coreColor")] [JsonConverter(typeof(HdrColorJsonConverter))] public HdrColor CoreColor { get; set; } = new(255, 255, 255, 255, 4.0f);
    [JsonPropertyName("glowColor")] [JsonConverter(typeof(HdrColorJsonConverter))] public HdrColor GlowColor { get; set; } = new(140, 180, 255, 200, 2.5f);
    [JsonPropertyName("coreWidth")] public float CoreWidth { get; set; } = 2.0f;
    [JsonPropertyName("glowWidth")] public float GlowWidth { get; set; } = 6.0f;
    [JsonPropertyName("flickerHz")] public float FlickerHz { get; set; } = 8.0f;
    [JsonPropertyName("jitterHz")] public float JitterHz { get; set; } = 12.0f;
}

public class ImageBehindVisual
{
    [JsonPropertyName("color")] [JsonConverter(typeof(HdrColorJsonConverter))] public HdrColor Color { get; set; } = new(255, 200, 100, 150, 2.0f);
    [JsonPropertyName("scale")] public float Scale { get; set; } = 1.08f;
    [JsonPropertyName("pulseSpeed")] public float PulseSpeed { get; set; } = 2.0f;
    [JsonPropertyName("pulseAmount")] public float PulseAmount { get; set; } = 0.04f;
    [JsonPropertyName("blendMode")] public int BlendMode { get; set; } = 1;
}

public class PulsingOutlineVisual
{
    [JsonPropertyName("color")] [JsonConverter(typeof(HdrColorJsonConverter))] public HdrColor Color { get; set; } = new(100, 200, 255, 255, 2.0f);
    [JsonPropertyName("pulseColor")] [JsonConverter(typeof(HdrColorJsonConverter))] public HdrColor PulseColor { get; set; } = new(255, 255, 255, 255, 4.0f);
    [JsonPropertyName("outlineWidth")] public float OutlineWidth { get; set; } = 2.0f;
    [JsonPropertyName("pulseWidth")] public float PulseWidth { get; set; } = 4.0f;
    [JsonPropertyName("pulseSpeed")] public float PulseSpeed { get; set; } = 2.0f;
    [JsonPropertyName("blendMode")] public int BlendMode { get; set; } = 1;
}

public class WeaponParticleVisual
{
    [JsonPropertyName("flipbookID")] public string FlipbookID { get; set; } = "";
    [JsonPropertyName("fps")] public float FPS { get; set; } = 15.0f;
    [JsonPropertyName("color")] [JsonConverter(typeof(HdrColorJsonConverter))] public HdrColor Color { get; set; } = new(255, 200, 100, 255, 2.0f);
    [JsonPropertyName("spawnRate")] public float SpawnRate { get; set; } = 10.0f;
    [JsonPropertyName("rangeMin")] public float RangeMin { get; set; }
    [JsonPropertyName("rangeMax")] public float RangeMax { get; set; } = 1.0f;
    [JsonPropertyName("particleLifetime")] public float ParticleLifetime { get; set; } = 0.5f;
    [JsonPropertyName("particleScale")] public float ParticleScale { get; set; } = 0.5f;
    [JsonPropertyName("moveSpeed")] public float MoveSpeed { get; set; } = 1.0f;
    [JsonPropertyName("moveDirX")] public float MoveDirX { get; set; }
    [JsonPropertyName("moveDirY")] public float MoveDirY { get; set; }
    [JsonPropertyName("moveDirZ")] public float MoveDirZ { get; set; } = 1.0f;
    [JsonPropertyName("blendMode")] public int BlendMode { get; set; } = 1;
    [JsonPropertyName("renderBehind")] public bool RenderBehind { get; set; }
    /// <summary>Single persistent looping flame pinned to the weapon attach at
    /// RangeMax (1 = tip/hand) instead of spawned drifting particles — no world
    /// trail. SpawnRate/Lifetime/MoveSpeed/MoveDir are ignored in this mode.</summary>
    [JsonPropertyName("attachedFlame")] public bool AttachedFlame { get; set; }
    /// <summary>ScatterGlow halo at the weapon tip. Defaults match the values
    /// previously hardcoded in DrawSingleUnit. Radius is world units.</summary>
    [JsonPropertyName("scatterRadius")] public float ScatterRadius { get; set; } = 1.1f;
    [JsonPropertyName("scatterStrength")] public float ScatterStrength { get; set; } = 0.45f;
}

public class ColorJson
{
    [JsonPropertyName("r")] public int R { get; set; }
    [JsonPropertyName("g")] public int G { get; set; }
    [JsonPropertyName("b")] public int B { get; set; }
    [JsonPropertyName("a")] public int A { get; set; }
}

public class BuffDef : INamedDef
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string DisplayName { get; set; } = "";
    /// <summary>Icon shown in the unit sheet's Abilities &amp; Buffs row and the
    /// buff tooltip header (e.g. assets/UI/Icons/Buffs/iron_skin.png). When
    /// empty or the file is missing, the UI falls back to the buff's primary
    /// stat-effect icon. Generate art via tools/gen_buff_icons.py.</summary>
    [JsonPropertyName("icon")] public string Icon { get; set; } = "";
    [JsonPropertyName("duration")] public float Duration { get; set; } = 10.0f;
    /// <summary>Visual fade-out window in seconds. A non-permanent buff's
    /// visuals (attached flame etc.) draw at RemainingDuration/FadeOutSeconds
    /// alpha over its final seconds instead of vanishing. For casting buffs
    /// with duration -1 ("lasts while channeling"), the channel-end removal
    /// converts the buff to a FadeOutSeconds countdown instead of deleting it.
    /// 0 = instant vanish (previous behavior).</summary>
    [JsonPropertyName("fadeOutSeconds")] public float FadeOutSeconds { get; set; }
    [JsonPropertyName("effects")] public List<BuffEffect> Effects { get; set; } = new();
    [JsonPropertyName("maxStacks")] public int MaxStacks { get; set; } = 1;

    /// <summary>Weapon IDs this buff appends to the bearer's effective weapon list
    /// while active. Resolved against WeaponRegistry at buff-apply time and pushed
    /// into Stats.MeleeWeapons / Stats.RangedWeapons; removed on buff expiry.
    /// Used by skill-tree intrinsic buffs (Wolf Lunge → buff_wolf_pounce →
    /// grants weapon_wolf_pounce) to layer attacks onto a unit without mutating
    /// its UnitDef.</summary>
    [JsonPropertyName("grantedWeapons")] public List<string> GrantedWeapons { get; set; } = new();

    /// <summary>True for buffs that come from skill-tree intrinsic effects
    /// (permanent, no countdown, no UI icon). Hides them from the buff bar
    /// display and skips most "buff applied to X" combat-log lines so the log
    /// isn't spammed with every wolf gaining buff_wolf_pounce at spawn.</summary>
    [JsonPropertyName("intrinsic")] public bool Intrinsic { get; set; }

    // Incapacitation: buff prevents movement/AI/combat while active
    [JsonPropertyName("incapacitating")] public bool Incapacitating { get; set; }
    [JsonPropertyName("incapHoldAnim")] public string IncapHoldAnim { get; set; } = "";     // e.g. "Knockdown", "Stunned"
    [JsonPropertyName("incapRecoverAnim")] public string IncapRecoverAnim { get; set; } = ""; // e.g. "Standup"

    // Parsed views (identity-cached, never serialized). Empty/unknown falls back
    // to Idle — the fallback BuffSystem always used; bad values are reported at
    // load by BuffRegistry.ValidateDef.
    private CachedEnum<Necroking.Render.AnimState> _incapHoldCache;
    private CachedEnum<Necroking.Render.AnimState> _incapRecoverCache;
    [JsonIgnore] public Necroking.Render.AnimState IncapHoldAnimEnum
        => _incapHoldCache.Get(IncapHoldAnim, Necroking.Render.AnimState.Idle);
    [JsonIgnore] public Necroking.Render.AnimState IncapRecoverAnimEnum
        => _incapRecoverCache.Get(IncapRecoverAnim, Necroking.Render.AnimState.Idle);
    [JsonPropertyName("incapRecoverTime")] public float IncapRecoverTime { get; set; } = 0.8f;
    [JsonPropertyName("incapHoldAtEnd")] public bool IncapHoldAtEnd { get; set; }            // Snap to last frame of hold anim

    [JsonPropertyName("hasOrbital")] public bool HasOrbital { get; set; }
    [JsonPropertyName("orbital")] public OrbitalVisual? Orbital { get; set; }

    [JsonPropertyName("hasGroundAura")] public bool HasGroundAura { get; set; }
    [JsonPropertyName("groundAura")] public GroundAuraVisual? GroundAura { get; set; }

    [JsonPropertyName("hasBehindEffect")] public bool HasBehindEffect { get; set; }
    [JsonPropertyName("behindEffect")] public UprightEffectVisual? BehindEffect { get; set; }

    [JsonPropertyName("hasFrontEffect")] public bool HasFrontEffect { get; set; }
    [JsonPropertyName("frontEffect")] public UprightEffectVisual? FrontEffect { get; set; }

    [JsonPropertyName("hasLightningAura")] public bool HasLightningAura { get; set; }
    [JsonPropertyName("lightningAura")] public LightningAuraVisual? LightningAura { get; set; }

    [JsonPropertyName("hasImageBehind")] public bool HasImageBehind { get; set; }
    [JsonPropertyName("imageBehind")] public ImageBehindVisual? ImageBehind { get; set; }

    [JsonPropertyName("hasPulsingOutline")] public bool HasPulsingOutline { get; set; }
    [JsonPropertyName("pulsingOutline")] public PulsingOutlineVisual? PulsingOutline { get; set; }

    [JsonPropertyName("hasWeaponParticle")] public bool HasWeaponParticle { get; set; }
    [JsonPropertyName("weaponParticle")] public WeaponParticleVisual? WeaponParticle { get; set; }

    [JsonPropertyName("unitTint")] public ColorJson? UnitTint { get; set; }
}

public class BuffRegistry : RegistryBase<BuffDef>
{
    protected override string RootKey => "buffs";

    protected override void ValidateDef(BuffDef def, Action<string> report)
    {
        // Effect Stat is deliberately unchecked: it's an open vocabulary
        // (BuffStat names + resource keys like MaxMana/AllPaths/PathShock/...).
        foreach (var eff in def.Effects)
            EnumJson.Check<BuffEffectType>(eff.Type, "effects.type", report, allowEmpty: false);
        EnumJson.Check<Necroking.Render.AnimState>(def.IncapHoldAnim, "incapHoldAnim", report);
        EnumJson.Check<Necroking.Render.AnimState>(def.IncapRecoverAnim, "incapRecoverAnim", report);
    }
}
