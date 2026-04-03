#if OPENGL
#define SV_POSITION POSITION
#define VS_SHADERMODEL vs_3_0
#define PS_SHADERMODEL ps_3_0
#else
#define VS_SHADERMODEL vs_4_0
#define PS_SHADERMODEL ps_4_0
#endif

#define SAMPLE_COUNT 15

float2 SampleOffsets[SAMPLE_COUNT];
float SampleWeights[SAMPLE_COUNT];

sampler2D TextureSampler : register(s0);

float4 PixelShaderFunction(float2 texCoord : TEXCOORD0) : COLOR0
{
    float4 color = 0;
    for (int i = 0; i < SAMPLE_COUNT; i++)
    {
        color += tex2D(TextureSampler, texCoord + SampleOffsets[i]) * SampleWeights[i];
    }
    return color;
}

technique GaussianBlur
{
    pass Pass1
    {
        PixelShader = compile PS_SHADERMODEL PixelShaderFunction();
    }
}
