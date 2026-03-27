#if OPENGL
#define SV_POSITION POSITION
#define VS_SHADERMODEL vs_3_0
#define PS_SHADERMODEL ps_3_0
#else
#define VS_SHADERMODEL vs_4_0
#define PS_SHADERMODEL ps_4_0
#endif

float BloomIntensity;
float BaseIntensity;
float BloomSaturation;
float BaseSaturation;

sampler2D TextureSampler : register(s0);
sampler2D BloomSampler : register(s1);

float4 AdjustSaturation(float4 color, float saturation)
{
    float grey = dot(color.rgb, float3(0.2126, 0.7152, 0.0722));
    return float4(lerp(grey.xxx, color.rgb, saturation), color.a);
}

float4 PixelShaderFunction(float2 texCoord : TEXCOORD0) : COLOR0
{
    float4 bloom = tex2D(BloomSampler, texCoord);
    float4 base = tex2D(TextureSampler, texCoord);

    bloom = AdjustSaturation(bloom, BloomSaturation) * BloomIntensity;
    base = AdjustSaturation(base, BaseSaturation) * BaseIntensity;
    base *= (1 - saturate(bloom));

    return base + bloom;
}

technique BloomCombine
{
    pass Pass1
    {
        PixelShader = compile PS_SHADERMODEL PixelShaderFunction();
    }
}
