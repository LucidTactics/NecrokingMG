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
// AlphaMode = 0 (Additive): fade baked into RGB, intensity sqrt-encoded in alpha
//   (HdrColor.ToHdrVertex stores sqrt(I/MaxIntensity); squaring here recovers I —
//   precision concentrates in the everyday low range while the cap reaches 16).
//   output.rgb = tex * color.rgb * (color.a² * MaxIntensity),  output.a = 1.0
//
// AlphaMode = 1 (Alpha blend): intensity baked LINEARLY into RGB (own, lower
//   ceiling — MaxAlphaIntensity), fade in alpha.
//   output.rgb = tex * color.rgb * MaxAlphaIntensity * color.a,  output.a = tex.a * color.a
//   RGB must be scaled by color.a too: output is premultiplied for AlphaBlend
//   (One/InvSrcAlpha), where source RGB is added at full strength regardless of
//   alpha — without the multiply, fading effects keep 100% brightness and turn
//   into a lingering additive glow instead of dimming out.
//
// IMPORTANT: MaxIntensity, MaxAlphaIntensity, and AlphaMode must be set
// explicitly from C# — MGFX on OpenGL does not honor default uniform values.

float MaxIntensity;
float MaxAlphaIntensity;
float AlphaMode;

sampler2D TextureSampler : register(s0);

float4 PixelShaderFunction(float4 color : COLOR0, float2 texCoord : TEXCOORD0) : COLOR0
{
    float4 tex = tex2D(TextureSampler, texCoord);

    // Additive path: alpha carries sqrt-encoded intensity — square to decode.
    float addIntensity = color.a * color.a * MaxIntensity;
    float4 addResult = float4(tex.rgb * color.rgb * addIntensity, 1.0);

    // Alpha path: RGB carries linearly scaled intensity, alpha is real fade.
    // color.a scales RGB as well — premultiplied output for AlphaBlend.
    float4 alpResult = float4(tex.rgb * color.rgb * (MaxAlphaIntensity * color.a), tex.a * color.a);

    return lerp(addResult, alpResult, AlphaMode);
}

technique HdrSpriteTechnique
{
    pass Pass1
    {
        PixelShader = compile PS_SHADERMODEL PixelShaderFunction();
    }
}
