#if OPENGL
#define SV_POSITION POSITION
#define VS_SHADERMODEL vs_3_0
#define PS_SHADERMODEL ps_3_0
#else
#define VS_SHADERMODEL vs_4_0
#define PS_SHADERMODEL ps_4_0
#endif

// SpriteBatch-compatible HDR sprite shader.
//
// AlphaMode = 0 (Additive): fade baked into RGB, intensity encoded in alpha.
//   output.rgb = tex * color.rgb * (color.a * MaxIntensity),  output.a = 1.0
//
// AlphaMode = 1 (Alpha blend): intensity baked into RGB, fade in alpha.
//   output.rgb = tex * color.rgb * MaxIntensity,  output.a = tex.a * color.a

float MaxIntensity = 4.0;
float AlphaMode = 0.0; // 0 = additive, 1 = alpha blend

// SpriteBatch sets this automatically.
float4x4 MatrixTransform;

sampler TextureSampler : register(s0);

struct VSInput
{
    float4 Position : POSITION0;
    float4 Color    : COLOR0;
    float2 TexCoord : TEXCOORD0;
};

struct VSOutput
{
    float4 Position : SV_POSITION;
    float4 Color    : COLOR0;
    float2 TexCoord : TEXCOORD0;
};

VSOutput VertexShaderFunction(VSInput input)
{
    VSOutput output;
    output.Position = mul(input.Position, MatrixTransform);
    output.Color    = input.Color;
    output.TexCoord = input.TexCoord;
    return output;
}

float4 PixelShaderFunction(VSOutput input) : COLOR0
{
    float4 tex = tex2D(TextureSampler, input.TexCoord);

    // Additive path: alpha carries encoded intensity
    float addIntensity = input.Color.a * MaxIntensity;
    float4 addResult = float4(tex.rgb * input.Color.rgb * addIntensity, 1.0);

    // Alpha path: RGB carries scaled intensity, alpha is real fade
    float4 alpResult = float4(tex.rgb * input.Color.rgb * MaxIntensity, tex.a * input.Color.a);

    return lerp(addResult, alpResult, AlphaMode);
}

technique HdrSpriteTechnique
{
    pass Pass1
    {
        VertexShader = compile VS_SHADERMODEL VertexShaderFunction();
        PixelShader  = compile PS_SHADERMODEL PixelShaderFunction();
    }
}
