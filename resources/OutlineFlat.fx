#if OPENGL
#define SV_POSITION POSITION
#define VS_SHADERMODEL vs_3_0
#define PS_SHADERMODEL ps_3_0
#else
#define VS_SHADERMODEL vs_4_0
#define PS_SHADERMODEL ps_4_0
#endif

// Flat-color outline shader: outputs OutlineColor wherever the sprite has alpha.
// Used by pulsing outline buff visual — draws sprite silhouette in a solid color.
float4 OutlineColor;

sampler2D TextureSampler : register(s0);

float4 PixelShaderFunction(float2 texCoord : TEXCOORD0) : COLOR0
{
    float a = tex2D(TextureSampler, texCoord).a;
    return float4(OutlineColor.rgb * a, OutlineColor.a * a);
}

technique OutlineFlat
{
    pass Pass1
    {
        PixelShader = compile PS_SHADERMODEL PixelShaderFunction();
    }
}
