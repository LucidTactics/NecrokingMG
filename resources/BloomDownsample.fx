#if OPENGL
#define SV_POSITION POSITION
#define VS_SHADERMODEL vs_3_0
#define PS_SHADERMODEL ps_3_0
#else
#define VS_SHADERMODEL vs_4_0
#define PS_SHADERMODEL ps_4_0
#endif

float2 TexelSize; // 1 / source (previous mip) dimensions

sampler2D TextureSampler : register(s0);

// 13-tap Jimenez downsample (SIGGRAPH 2014, CoD:AW) — replaces the plain bilinear
// blit down the mip chain. The wide overlapping footprint keeps the chain stable
// under motion (no shimmer) and produces a smoother, rounder falloff. No Karis
// weighting here: fireflies are already suppressed by the prefilter pass.
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

    // Center 2x2 group carries half the weight, the four corner groups an
    // eighth each — same distribution as the prefilter, without Karis.
    float3 color = (j + k + l + m) * 0.25 * 0.5;
    color += (a + b + d + e) * 0.25 * 0.125;
    color += (b + c + e + f) * 0.25 * 0.125;
    color += (d + e + g + h) * 0.25 * 0.125;
    color += (e + f + h + i) * 0.25 * 0.125;

    return float4(color, 1.0);
}

technique BloomDownsample
{
    pass Pass1
    {
        PixelShader = compile PS_SHADERMODEL PixelShaderFunction();
    }
}
