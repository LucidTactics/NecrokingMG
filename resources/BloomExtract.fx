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

sampler2D TextureSampler : register(s0);

float4 PixelShaderFunction(float2 texCoord : TEXCOORD0) : COLOR0
{
    float4 color = tex2D(TextureSampler, texCoord);

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

    return float4(color.rgb * contribution, 1.0);
}

technique BloomExtract
{
    pass Pass1
    {
        PixelShader = compile PS_SHADERMODEL PixelShaderFunction();
    }
}
