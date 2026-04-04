#if OPENGL
#define SV_POSITION POSITION
#define VS_SHADERMODEL vs_3_0
#define PS_SHADERMODEL ps_3_0
#else
#define VS_SHADERMODEL vs_4_0
#define PS_SHADERMODEL ps_4_0
#endif

// Matches C++ hdr_output.fs exactly: multiply vertex color RGB by Intensity.
// No capping — bloom pipeline handles the overbright values.
float Intensity;
float4x4 WorldViewProjection;

struct VSInput
{
    float4 Position : POSITION0;
    float4 Color : COLOR0;
};

struct VSOutput
{
    float4 Position : SV_POSITION;
    float4 Color : COLOR0;
};

VSOutput VertexShaderFunction(VSInput input)
{
    VSOutput output;
    output.Position = mul(input.Position, WorldViewProjection);
    output.Color = input.Color;
    return output;
}

float4 PixelShaderFunction(VSOutput input) : COLOR0
{
    float4 c = input.Color;
    return float4(c.rgb * Intensity, c.a);
}

technique HdrIntensityTechnique
{
    pass Pass1
    {
        VertexShader = compile VS_SHADERMODEL VertexShaderFunction();
        PixelShader = compile PS_SHADERMODEL PixelShaderFunction();
    }
}
