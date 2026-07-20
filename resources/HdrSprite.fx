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
// LinearTexture = 1: the bound texture holds LINEAR half-float HDR data
//   (EXR flipbooks, premultiplied in linear space) instead of the usual
//   sRGB-encoded LDR. The sample is converted to scene units first:
//   sRGB-encode the [0,1] part (so it matches its LDR .tga twin exactly)
//   and pass overbright (>1) values through linearly (the same way vertex
//   intensity scales LDR sprites) so per-texel energy reaches bloom.
//   The encode runs on UN-premultiplied color (enc(p/a)*a), matching how
//   LDR sheets are encoded before premultiplication at load.
//
// IMPORTANT: MaxIntensity, MaxAlphaIntensity, AlphaMode, and LinearTexture
// must be set explicitly from C# — MGFX on OpenGL does not honor default
// uniform values.

float MaxIntensity;
float MaxAlphaIntensity;
float AlphaMode;
float LinearTexture;

sampler2D TextureSampler : register(s0);

float3 EncodeSceneUnits(float3 v)
{
    v = max(v, 0.0);
    float3 srgb = min(v, 1.0) <= 0.0031308
        ? min(v, 1.0) * 12.92
        : 1.055 * pow(min(v, 1.0), 1.0 / 2.4) - 0.055;
    // Overbright: keep the linear value (srgb(1)=1, so this is continuous).
    return v > 1.0 ? v : srgb;
}

float4 PixelShaderFunction(float4 color : COLOR0, float2 texCoord : TEXCOORD0) : COLOR0
{
    float4 tex = tex2D(TextureSampler, texCoord);

    // Linear HDR texture -> scene units (un-premultiply, encode, re-premultiply)
    float linA = max(tex.a, 1e-5);
    float4 linTex = float4(EncodeSceneUnits(tex.rgb / linA) * linA, tex.a);
    tex = lerp(tex, linTex, LinearTexture);

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
