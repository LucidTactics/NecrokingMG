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

// Textured variant (drain-beam scroll layers): tiling noise sampled with
// wrap so the arc-length U coordinate can scroll unbounded.
texture ScrollTexture;
sampler2D ScrollSampler = sampler_state
{
    Texture = <ScrollTexture>;
    MinFilter = Linear;
    MagFilter = Linear;
    MipFilter = None;
    AddressU = Wrap;
    AddressV = Wrap;
};

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

struct VSInputTex
{
    float4 Position : POSITION0;
    float4 Color : COLOR0;
    float2 TexCoord : TEXCOORD0;
};

struct VSOutputTex
{
    float4 Position : SV_POSITION;
    float4 Color : COLOR0;
    float2 TexCoord : TEXCOORD0;
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
    // max() guards negative Intensity from unvalidated spell JSON — a negative
    // value here writes negative color into the HalfVector4 scene RT, which the
    // bloom chain then spreads as a darkening halo.
    return float4(c.rgb * max(Intensity, 0.0), c.a);
}

VSOutputTex VertexShaderTextured(VSInputTex input)
{
    VSOutputTex output;
    output.Position = mul(input.Position, WorldViewProjection);
    output.Color = input.Color;
    output.TexCoord = input.TexCoord;
    return output;
}

float4 PixelShaderTextured(VSOutputTex input) : COLOR0
{
    float4 t = tex2D(ScrollSampler, input.TexCoord);
    float4 c = input.Color;
    return float4(c.rgb * t.rgb * max(Intensity, 0.0), c.a * t.a);
}

technique HdrIntensityTechnique
{
    pass Pass1
    {
        VertexShader = compile VS_SHADERMODEL VertexShaderFunction();
        PixelShader = compile PS_SHADERMODEL PixelShaderFunction();
    }
}

technique HdrIntensityTexturedTechnique
{
    pass Pass1
    {
        VertexShader = compile VS_SHADERMODEL VertexShaderTextured();
        PixelShader = compile PS_SHADERMODEL PixelShaderTextured();
    }
}
