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

// Brightness
float Brightness;

// Tint
float3 TintColor;
float TintStrength;

// Vignette
float VignetteStrength;
float VignetteRadius;
float VignetteSoftness;
float2 Resolution;

// Lightning flash
float FlashIntensity;

// SpriteBatch provides the texture in s0
sampler2D TextureSampler : register(s0);

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

// --- HLSL smoothstep (some profiles already have it, but define for safety) ---
float smoothstep_custom(float edge0, float edge1, float x)
{
    float t = saturate((x - edge0) / (edge1 - edge0));
    return t * t * (3.0 - 2.0 * t);
}

float4 PixelShaderFunction(float2 texCoord : TEXCOORD0) : COLOR0
{
    // Start with transparent (we'll output fog as a blended overlay)
    float4 result = float4(0.0, 0.0, 0.0, 0.0);

    // --- Fog ---
    if (FogDensity > 0.001)
    {
        // Convert screen UV to world position
        float2 worldPos = FogWorldOrigin + texCoord * FogWorldScale;
        float2 fuv = worldPos * FogScaleU * 0.03;
        float2 scroll = float2(1.0, 0.3) * FogSpeed * Time;

        // Three noise layers at different scales and speeds
        float n1 = snoise(fuv * 3.0 + scroll * 0.3) * 0.5 + 0.5;
        float n2 = snoise(fuv * 7.0 + scroll * 0.7 + float2(100.0, 100.0)) * 0.5 + 0.5;
        float n3 = snoise(fuv * 15.0 + scroll * 1.2 + float2(200.0, 200.0)) * 0.5 + 0.5;

        float fog = n1 * 0.5 + n2 * 0.3 + n3 * 0.2;
        fog = smoothstep_custom(0.3, 0.8, fog) * FogDensity * 0.6;

        result = float4(FogColor, fog);
    }

    // --- Haze (Y-based distance fade) ---
    if (HazeStrength > 0.001)
    {
        float depth = 1.0 - texCoord.y;
        float haze = smoothstep_custom(0.2, 0.7, depth) * HazeStrength;

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
        if (Resolution.y > 0.0)
            center.x *= Resolution.x / Resolution.y;
        float dist = length(center);
        float vig = 1.0 - smoothstep_custom(VignetteRadius, VignetteRadius + VignetteSoftness, dist);
        float vigDark = (1.0 - vig) * VignetteStrength;

        // Blend vignette darkness on top
        result.rgb = result.rgb * (1.0 - vigDark);
        result.a = result.a * (1.0 - vigDark) + vigDark;
    }

    // --- Lightning flash (white additive) ---
    if (FlashIntensity > 0.001)
    {
        // Flash reduces overlay opacity and adds white
        // This brightens what's underneath
        float flashAlpha = FlashIntensity * 0.8;
        result.rgb = result.rgb * (1.0 - flashAlpha) + float3(1.0, 1.0, 1.0) * flashAlpha;
        result.a = max(result.a, flashAlpha);
    }

    // Output as premultiplied alpha (MonoGame SpriteBatch expects this)
    result.rgb *= result.a;

    return result;
}

technique WeatherFog
{
    pass Pass1
    {
        PixelShader = compile PS_SHADERMODEL PixelShaderFunction();
    }
}
