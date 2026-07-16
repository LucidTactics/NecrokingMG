#if OPENGL
#define SV_POSITION POSITION
#define VS_SHADERMODEL vs_3_0
#define PS_SHADERMODEL ps_3_0
#else
#define VS_SHADERMODEL vs_4_0
#define PS_SHADERMODEL ps_4_0
#endif

// Fog uniforms
float FogDensity;
float3 FogColor;
float FogSpeed;
float FogScaleU;   // user-facing fogScale multiplier
float Time;
float2 FogWorldOrigin;  // world position at screen top-left
float2 FogWorldScale;   // world units covered by screen (width, height)

// Haze uniforms
float HazeStrength;
float3 HazeColor;
// World-anchored haze ramp: anchor (bottom of the ramp, world Y) and inverse
// depth (1 / ramp length in world units). Authored so zoom 32 @ 720p matches
// the old screen-Y ramp exactly; at other zooms the haze band is world-locked
// (realism model — see docs/vfx-zoom-audit.md).
float HazeAnchorY;
float HazeInvWorldDepth;

// Brightness — NOTE: WeatherRenderer.cs currently always passes 1.0 (darkening
// is applied pre-bloom as ambient light on sprites); block kept for the weather
// editor to re-enable.
float Brightness;

// Tint — NOTE: WeatherRenderer.cs currently always passes white/0 (see Brightness).
float3 TintColor;
float TintStrength;

// Vignette
float VignetteStrength;
float VignetteRadius;
float VignetteSoftness;
float2 ScreenSize;

// Lightning flash
float FlashIntensity;

// SpriteBatch provides the texture in s0
sampler2D TextureSampler : register(s0);

// ─── Fog/haze tuning ───
static const float2 FogWindDir       = float2(1.0, 0.3);         // scroll direction (unnormalized)
static const float  FogUvScale       = 0.03;                     // world units → noise UV
static const float3 FogOctaveScales  = float3(3.0, 7.0, 15.0);   // coarse / mid / fine layer
static const float3 FogOctaveSpeeds  = float3(0.3, 0.7, 1.2);    // per-layer scroll multiplier
static const float3 FogOctaveWeights = float3(0.5, 0.3, 0.2);    // per-layer blend (sums to 1)
static const float  FogCoverLo       = 0.3;                      // noise below this = clear
static const float  FogCoverHi       = 0.8;                      // noise above this = max fog
static const float  FogMaxOpacity    = 0.6;                      // overlay alpha cap at density 1
static const float  HazeRampLo       = 0.2;                      // haze fades in from this screen depth...
static const float  HazeRampHi       = 0.7;                      // ...to full by this (0 = bottom, 1 = top)
static const float  FlashMaxOpacity  = 0.8;                      // lightning flash white-layer cap

// --- 2D Simplex noise ---
// Based on the classic Ashima/Stefan Gustavson implementation

float3 mod289_3(float3 x) { return x - floor(x * (1.0 / 289.0)) * 289.0; }
float2 mod289_2(float2 x) { return x - floor(x * (1.0 / 289.0)) * 289.0; }
float3 permute(float3 x) { return mod289_3(((x * 34.0) + 1.0) * x); }

float snoise(float2 v)
{
    // Skew constants for 2D simplex
    float C_x = 0.211324865405187;  // (3.0 - sqrt(3.0)) / 6.0
    float C_y = 0.366025403784439;  // 0.5 * (sqrt(3.0) - 1.0)
    float C_z = -0.577350269189626; // -1.0 + 2.0 * C.x
    float C_w = 0.024390243902439;  // 1.0 / 41.0

    // First corner
    float2 i = floor(v + dot(v, float2(C_y, C_y)));
    float2 x0 = v - i + dot(i, float2(C_x, C_x));

    // Other corners
    float2 i1;
    i1 = (x0.x > x0.y) ? float2(1.0, 0.0) : float2(0.0, 1.0);
    float4 x12 = float4(x0.x + C_x, x0.y + C_x, x0.x + C_z, x0.y + C_z);
    x12.xy -= i1;

    // Permutations
    i = mod289_2(i);
    float3 p = permute(permute(i.y + float3(0.0, i1.y, 1.0)) + i.x + float3(0.0, i1.x, 1.0));

    float3 m = max(0.5 - float3(dot(x0, x0), dot(x12.xy, x12.xy), dot(x12.zw, x12.zw)), 0.0);
    m = m * m;
    m = m * m;

    // Gradients
    float3 x_ = 2.0 * frac(p * C_w) - 1.0;
    float3 h = abs(x_) - 0.5;
    float3 ox = floor(x_ + 0.5);
    float3 a0 = x_ - ox;

    // Normalise gradients implicitly by scaling m
    m *= 1.79284291400159 - 0.85373472095314 * (a0 * a0 + h * h);

    // Compute final noise value at P
    float3 g;
    g.x = a0.x * x0.x + h.x * x0.y;
    g.yz = a0.yz * x12.xz + h.yz * x12.yw;
    return 130.0 * dot(m, g);
}

float4 PixelShaderFunction(float2 texCoord : TEXCOORD0) : COLOR0
{
    // The whole overlay is composited in PREMULTIPLIED space: every layer is
    // "over"-blended as (rgb*(1-a) + layerColor*layerA, a*(1-a) + layerA), and
    // the final value is drawn with premultiplied AlphaBlend. Do NOT multiply
    // rgb by alpha at the end — each layer's rgb is already alpha-weighted.
    float4 result = float4(0.0, 0.0, 0.0, 0.0);

    // --- Fog ---
    if (FogDensity > 0.001)
    {
        // Convert screen UV to world position
        float2 worldPos = FogWorldOrigin + texCoord * FogWorldScale;
        float2 fuv = worldPos * FogScaleU * FogUvScale;
        float2 scroll = FogWindDir * FogSpeed * Time;

        // Three noise layers at different scales and speeds (offsets decorrelate them)
        float n1 = snoise(fuv * FogOctaveScales.x + scroll * FogOctaveSpeeds.x) * 0.5 + 0.5;
        float n2 = snoise(fuv * FogOctaveScales.y + scroll * FogOctaveSpeeds.y + float2(100.0, 100.0)) * 0.5 + 0.5;
        float n3 = snoise(fuv * FogOctaveScales.z + scroll * FogOctaveSpeeds.z + float2(200.0, 200.0)) * 0.5 + 0.5;

        float fog = n1 * FogOctaveWeights.x + n2 * FogOctaveWeights.y + n3 * FogOctaveWeights.z;
        fog = smoothstep(FogCoverLo, FogCoverHi, fog) * FogDensity * FogMaxOpacity;

        result = float4(FogColor * fog, fog);
    }

    // --- Haze (world-anchored distance fade: north = farther) ---
    if (HazeStrength > 0.001)
    {
        float hazeWorldY = FogWorldOrigin.y + texCoord.y * FogWorldScale.y;
        float depth = saturate((HazeAnchorY - hazeWorldY) * HazeInvWorldDepth);
        float haze = smoothstep(HazeRampLo, HazeRampHi, depth) * HazeStrength;

        // Blend haze on top of fog using standard alpha compositing
        float3 hazeRgb = HazeColor * haze;
        result.rgb = result.rgb * (1.0 - haze) + hazeRgb;
        result.a = result.a * (1.0 - haze) + haze;
    }

    // --- Brightness darkening ---
    if (Brightness < 0.95)
    {
        float darkAmount = 1.0 - Brightness;
        float darkAlpha = darkAmount * 0.706; // ~180/255

        // Blend darkness on top (black with alpha)
        result.rgb = result.rgb * (1.0 - darkAlpha);
        result.a = result.a * (1.0 - darkAlpha) + darkAlpha;
    }

    // --- Tint (multiplicative color grading) ---
    if (TintStrength > 0.001)
    {
        // Lerp toward multiplicative tint: at full strength, result *= TintColor
        float3 tinted = result.rgb * TintColor;
        result.rgb = lerp(result.rgb, tinted, TintStrength);
    }

    // --- Vignette (radial darkening) ---
    if (VignetteStrength > 0.001)
    {
        float2 center = texCoord - 0.5;
        // Aspect ratio correction
        if (ScreenSize.y > 0.0)
            center.x *= ScreenSize.x / ScreenSize.y;
        float dist = length(center);
        // Softness floor: the editor allows 0, and smoothstep with equal edges
        // divides by zero (NaN on some drivers).
        float vig = 1.0 - smoothstep(VignetteRadius, VignetteRadius + max(VignetteSoftness, 1e-4), dist);
        float vigDark = (1.0 - vig) * VignetteStrength;

        // Blend vignette darkness on top
        result.rgb = result.rgb * (1.0 - vigDark);
        result.a = result.a * (1.0 - vigDark) + vigDark;
    }

    // --- Lightning flash (white layer over the overlay) ---
    if (FlashIntensity > 0.001)
    {
        float flashAlpha = FlashIntensity * FlashMaxOpacity;
        result.rgb = result.rgb * (1.0 - flashAlpha) + float3(1.0, 1.0, 1.0) * flashAlpha;
        result.a = result.a * (1.0 - flashAlpha) + flashAlpha;
    }

    // Already premultiplied (see top of function) — output as-is.
    return result;
}

technique WeatherFog
{
    pass Pass1
    {
        PixelShader = compile PS_SHADERMODEL PixelShaderFunction();
    }
}
