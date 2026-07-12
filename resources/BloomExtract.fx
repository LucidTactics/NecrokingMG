#if OPENGL
#define SV_POSITION POSITION
#define VS_SHADERMODEL vs_3_0
#define PS_SHADERMODEL ps_3_0
#else
#define VS_SHADERMODEL vs_4_0
#define PS_SHADERMODEL ps_4_0
#endif

float BloomThreshold;
float SoftKnee;
float2 TexelSize; // 1 / source (full-res scene) dimensions

sampler2D TextureSampler : register(s0);

float Luma(float3 c)
{
    return dot(c, float3(0.2126, 0.7152, 0.0722));
}

// Prefilter = 13-tap Jimenez downsample (SIGGRAPH 2014, CoD:AW) + Karis-weighted
// group averages + soft-knee threshold. The Karis weights (1/(1+luma)) suppress
// single ultra-bright pixels ("fireflies") that would otherwise shimmer/pulse as
// they move across the half-res grid.
float4 PixelShaderFunction(float2 uv : TEXCOORD0) : COLOR0
{
    float2 t = TexelSize;
    float3 a = tex2D(TextureSampler, uv + float2(-2.0, -2.0) * t).rgb;
    float3 b = tex2D(TextureSampler, uv + float2( 0.0, -2.0) * t).rgb;
    float3 c = tex2D(TextureSampler, uv + float2( 2.0, -2.0) * t).rgb;
    float3 d = tex2D(TextureSampler, uv + float2(-2.0,  0.0) * t).rgb;
    float3 e = tex2D(TextureSampler, uv).rgb;
    float3 f = tex2D(TextureSampler, uv + float2( 2.0,  0.0) * t).rgb;
    float3 g = tex2D(TextureSampler, uv + float2(-2.0,  2.0) * t).rgb;
    float3 h = tex2D(TextureSampler, uv + float2( 0.0,  2.0) * t).rgb;
    float3 i = tex2D(TextureSampler, uv + float2( 2.0,  2.0) * t).rgb;
    float3 j = tex2D(TextureSampler, uv + float2(-1.0, -1.0) * t).rgb;
    float3 k = tex2D(TextureSampler, uv + float2( 1.0, -1.0) * t).rgb;
    float3 l = tex2D(TextureSampler, uv + float2(-1.0,  1.0) * t).rgb;
    float3 m = tex2D(TextureSampler, uv + float2( 1.0,  1.0) * t).rgb;

    // Five overlapping 2x2 groups (center weighted 0.5, corners 0.125 each)
    float3 g0 = (j + k + l + m) * 0.25;
    float3 g1 = (a + b + d + e) * 0.25;
    float3 g2 = (b + c + e + f) * 0.25;
    float3 g3 = (d + e + g + h) * 0.25;
    float3 g4 = (e + f + h + i) * 0.25;
    float w0 = 0.5   / (1.0 + Luma(g0));
    float w1 = 0.125 / (1.0 + Luma(g1));
    float w2 = 0.125 / (1.0 + Luma(g2));
    float w3 = 0.125 / (1.0 + Luma(g3));
    float w4 = 0.125 / (1.0 + Luma(g4));
    float3 color = (g0 * w0 + g1 * w1 + g2 * w2 + g3 * w3 + g4 * w4)
                 / (w0 + w1 + w2 + w3 + w4);

    // Max-channel brightness (matches C++ bloom_prefilter)
    float brightness = max(color.r, max(color.g, color.b));

    // Soft knee: smooth quadratic ramp around threshold (matches C++)
    // Prevents the hard cutoff that causes sudden bloom explosion
    float knee = BloomThreshold * SoftKnee;
    float soft = brightness - BloomThreshold + knee;
    soft = clamp(soft, 0.0, 2.0 * knee);
    soft = soft * soft / (4.0 * knee + 0.00001);

    float contribution = max(soft, brightness - BloomThreshold) / max(brightness, 0.00001);
    contribution = max(contribution, 0.0);

    return float4(color * contribution, 1.0);
}

technique BloomExtract
{
    pass Pass1
    {
        PixelShader = compile PS_SHADERMODEL PixelShaderFunction();
    }
}
