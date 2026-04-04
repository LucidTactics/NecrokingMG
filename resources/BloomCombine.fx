#if OPENGL
#define SV_POSITION POSITION
#define VS_SHADERMODEL vs_3_0
#define PS_SHADERMODEL ps_3_0
#else
#define VS_SHADERMODEL vs_4_0
#define PS_SHADERMODEL ps_4_0
#endif

// Matches C++ bloom_composite.fs: simple additive scene + bloom * intensity
float BloomIntensity;
float BaseIntensity;
float BloomSaturation;
float BaseSaturation;

sampler2D TextureSampler : register(s0);
sampler2D BloomSampler : register(s1);

float4 PixelShaderFunction(float2 texCoord : TEXCOORD0) : COLOR0
{
    float4 bloom = tex2D(BloomSampler, texCoord);
    float4 base = tex2D(TextureSampler, texCoord);

    return float4(base.rgb + bloom.rgb * BloomIntensity, 1.0);
}

technique BloomCombine
{
    pass Pass1
    {
        PixelShader = compile PS_SHADERMODEL PixelShaderFunction();
    }
}
