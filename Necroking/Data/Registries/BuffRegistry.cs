using System.Collections.Generic;
using System.Text.Json.Serialization;
using Necroking.Core;

namespace Necroking.Data.Registries;

public class BuffEffect
{
    [JsonPropertyName("type")] public string Type { get; set; } = "Add";
    [JsonPropertyName("stat")] public string Stat { get; set; } = "Strength";
    [JsonPropertyName("value")] public float Value { get; set; }
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
}

public class ColorJson
{
    [JsonPropertyName("r")] public int R { get; set; }
    [JsonPropertyName("g")] public int G { get; set; }
    [JsonPropertyName("b")] public int B { get; set; }
    [JsonPropertyName("a")] public int A { get; set; }
}

public class BuffDef : IHasId
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string DisplayName { get; set; } = "";
    [JsonPropertyName("duration")] public float Duration { get; set; } = 10.0f;
    [JsonPropertyName("effects")] public List<BuffEffect> Effects { get; set; } = new();
    [JsonPropertyName("maxStacks")] public int MaxStacks { get; set; } = 1;

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
}
