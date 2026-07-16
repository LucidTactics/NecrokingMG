#if OPENGL
#define SV_POSITION POSITION
#define VS_SHADERMODEL vs_3_0
#define PS_SHADERMODEL ps_3_0
#else
#define VS_SHADERMODEL vs_4_0
#define PS_SHADERMODEL ps_4_0
#endif

// ScatterGlow: world-space light-scatter halos around VFX emitters, drawn into
// the HDR scene RT BEFORE bloom (bloom models the eye; this models the AIR).
// Geometry comes from ScatterGlowSystem as screen-space quads; the VS derives
// each pixel's WORLD position so all halos sample ONE world-anchored scrolling
// mist field — overlapping emitters light the same wisp of air.
//
// Zoom policy (docs/vfx-zoom-audit.md): every constant here is WORLD-space
// (mist feature size in world units, wind in world units/sec). Screen coupling
// happens only through WorldOrigin/WorldPerPixel, set per frame from the camera.
//
// MGFX-on-GL zeroes uniform initializers — ScatterGlowSystem sets EVERY uniform
// each frame (memory/mgfx_shader_gotchas.md).

float4x4 WorldViewProjection;
float Time;             // seconds, wrapped CPU-side
float Density;          // global medium density: perf setting x weather fog
float MistStrength;     // 0 = uniform glow, 1 = fully patchy mist
float2 WorldOrigin;     // world position at screen (0,0)
float2 WorldPerPixel;   // (1/zoom, 1/(zoom*yRatio))
float SolidIntensity;   // HDR multiplier for the Solid (test-shape emission) technique

// ─── Mist tuning (world-space) ───
static const float2 MistWindDir      = float2(1.0, 0.35);  // scroll direction (world)
static const float  MistWindSpeed    = 0.55;               // world units / sec
static const float2 MistOctaveScale  = float2(0.13, 0.42); // cycles per world unit (features ~8 / ~2.4 wu)
static const float2 MistOctaveWeight = float2(0.62, 0.38);
static const float  MistCoverLo      = 0.18;               // noise below = thin air
static const float  MistCoverHi      = 0.85;               // noise above = dense air

// --- 2D Simplex noise (same Ashima/Gustavson implementation as WeatherFog.fx) ---
float3 mod289_3(float3 x) { return x - floor(x * (1.0 / 289.0)) * 289.0; }
float2 mod289_2(float2 x) { return x - floor(x * (1.0 / 289.0)) * 289.0; }
float3 permute(float3 x) { return mod289_3(((x * 34.0) + 1.0) * x); }

float snoise(float2 v)
{
    float C_x = 0.211324865405187;
    float C_y = 0.366025403784439;
    float C_z = -0.577350269189626;
    float C_w = 0.024390243902439;

    float2 i = floor(v + dot(v, float2(C_y, C_y)));
    float2 x0 = v - i + dot(i, float2(C_x, C_x));

    float2 i1;
    i1 = (x0.x > x0.y) ? float2(1.0, 0.0) : float2(0.0, 1.0);
    float4 x12 = float4(x0.x + C_x, x0.y + C_x, x0.x + C_z, x0.y + C_z);
    x12.xy -= i1;

    i = mod289_2(i);
    float3 p = permute(permute(i.y + float3(0.0, i1.y, 1.0)) + i.x + float3(0.0, i1.x, 1.0));

    float3 m = max(0.5 - float3(dot(x0, x0), dot(x12.xy, x12.xy), dot(x12.zw, x12.zw)), 0.0);
    m = m * m;
    m = m * m;

    float3 x_ = 2.0 * frac(p * C_w) - 1.0;
    float3 h = abs(x_) - 0.5;
    float3 ox = floor(x_ + 0.5);
    float3 a0 = x_ - ox;

    m *= 1.79284291400159 - 0.85373472095314 * (a0 * a0 + h * h);

    float3 g;
    g.x = a0.x * x0.x + h.x * x0.y;
    g.yz = a0.yz * x12.xz + h.yz * x12.yw;
    return 130.0 * dot(m, g);
}

struct VSInput
{
    float4 Position : POSITION0;   // screen px; z = FogDepthForY depth
    float4 Color : COLOR0;         // rgb = scatter color (straight), a = strength
    float2 TexCoord : TEXCOORD0;   // quad-local: halo corners at +-1; solid rect all 0
};

struct VSOutput
{
    float4 Position : SV_POSITION;
    float4 Color : COLOR0;
    float2 TexCoord : TEXCOORD0;
    float2 WorldPos : TEXCOORD1;
};

VSOutput VertexShaderFunction(VSInput input)
{
    VSOutput output;
    output.Position = mul(input.Position, WorldViewProjection);
    output.Color = input.Color;
    output.TexCoord = input.TexCoord;
    output.WorldPos = WorldOrigin + input.Position.xy * WorldPerPixel;
    return output;
}

// Shared mist field: 2 octaves of world-anchored scrolling simplex, shaped into
// a modulation factor around 1.0. Multiplication distributes over the additive
// blend, so per-halo modulate-then-add == accumulate-then-modulate — every halo
// visibly sits in the SAME air.
float MistAt(float2 world)
{
    float2 scroll = MistWindDir * (MistWindSpeed * Time);
    float n1 = snoise((world + scroll) * MistOctaveScale.x) * 0.5 + 0.5;
    float n2 = snoise((world + scroll * 1.7 + float2(137.0, 71.0)) * MistOctaveScale.y) * 0.5 + 0.5;
    float n = n1 * MistOctaveWeight.x + n2 * MistOctaveWeight.y;
    n = smoothstep(MistCoverLo, MistCoverHi, n);
    // Range at MistStrength 1: x0.25 in thin air .. x1.35 in a dense wisp.
    return lerp(1.0 - 0.75 * MistStrength, 1.0 + 0.35 * MistStrength, n);
}

// Halo: soft radial falloff x mist x density. Output stays LDR-ish (below the
// bloom-extract knee) so the halo adds light IN the scene instead of being
// re-amplified by bloom. Additive One/One blend; alpha out = 0 keeps RT alpha clean.
float4 PixelHalo(VSOutput input) : COLOR0
{
    float d = length(input.TexCoord);
    float baseFall = saturate(1.0 - d);
    // Quadratic body + a gently hotter heart near the source (kept mild so
    // polyline splats blend into one tube instead of beading).
    float falloff = baseFall * baseFall * (1.0 + 0.5 * baseFall * baseFall * baseFall);
    float mist = MistAt(input.WorldPos);
    float3 rgb = input.Color.rgb * (input.Color.a * falloff * mist * Density);
    return float4(rgb, 0.0);
}

// Solid: bright emission primitives for the ScatterGlow test spells (glow line /
// glow circle). Rect quads author TexCoord = 0 (fully inside); circles author
// corners at +-1 and get a soft-stepped disc edge. Vertex alpha = fade.
float4 PixelSolid(VSOutput input) : COLOR0
{
    float d = length(input.TexCoord);
    float a = (1.0 - smoothstep(0.9, 1.0, d)) * input.Color.a;
    return float4(input.Color.rgb * (SolidIntensity * a), 0.0);
}

technique ScatterHalo
{
    pass Pass1
    {
        VertexShader = compile VS_SHADERMODEL VertexShaderFunction();
        PixelShader = compile PS_SHADERMODEL PixelHalo();
    }
}

technique ScatterSolid
{
    pass Pass1
    {
        VertexShader = compile VS_SHADERMODEL VertexShaderFunction();
        PixelShader = compile PS_SHADERMODEL PixelSolid();
    }
}
