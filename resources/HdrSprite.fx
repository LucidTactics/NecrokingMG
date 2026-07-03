#if OPENGL
#define SV_POSITION POSITION
#define VS_SHADERMODEL vs_3_0
#define PS_SHADERMODEL ps_3_0
#else
#define VS_SHADERMODEL vs_4_0
#define PS_SHADERMODEL ps_4_0
#endif

// SpriteBatch-compatible HDR pixel shader (pixel-shader-only, no custom vertex
// shader — SpriteBatch's built-in vertex shader handles projection).
//
// AlphaMode = 0 (Additive): fade baked into RGB, intensity encoded in alpha.
//   output.rgb = tex * color.rgb * (color.a * MaxIntensity),  output.a = 1.0
//
// AlphaMode = 1 (Alpha blend): intensity baked into RGB, fade in alpha.
//   output.rgb = tex * color.rgb * MaxIntensity * color.a,  output.a = tex.a * color.a
//   RGB must be scaled by color.a too: output is premultiplied for AlphaBlend
//   (One/InvSrcAlpha), where source RGB is added at full strength regardless of
//   alpha — without the multiply, fading effects keep 100% brightness and turn
//   into a lingering additive glow instead of dimming out.
//
// IMPORTANT: MaxIntensity and AlphaMode must be set explicitly from C# —
// MGFX on OpenGL does not honor default uniform values.

float MaxIntensity;
float AlphaMode;

sampler2D TextureSampler : register(s0);

float4 PixelShaderFunction(float4 color : COLOR0, float2 texCoord : TEXCOORD0) : COLOR0
{
    float4 tex = tex2D(TextureSampler, texCoord);

    // Additive path: alpha carries encoded intensity
    float addIntensity = color.a * MaxIntensity;
    float4 addResult = float4(tex.rgb * color.rgb * addIntensity, 1.0);

    // Alpha path: RGB carries scaled intensity, alpha is real fade.
    // color.a scales RGB as well — premultiplied output for AlphaBlend.
    float4 alpResult = float4(tex.rgb * color.rgb * (MaxIntensity * color.a), tex.a * color.a);

    return lerp(addResult, alpResult, AlphaMode);
}

technique HdrSpriteTechnique
{
    pass Pass1
    {
        PixelShader = compile PS_SHADERMODEL PixelShaderFunction();
    }
}
