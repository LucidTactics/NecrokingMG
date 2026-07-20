#if OPENGL
#define SV_POSITION POSITION
#define VS_SHADERMODEL vs_3_0
#define PS_SHADERMODEL ps_3_0
#else
#define VS_SHADERMODEL vs_4_0
#define PS_SHADERMODEL ps_4_0
#endif

// SpriteBatch-compatible temperature-gradient flame shader (pixel-shader-only).
//
// Input texture (s0): a "-temperature" HDR flipbook — LINEAR premultiplied
// half-float, grayscale heat scalar (values run well above 1.0, e.g. 2-37).
// Gradient LUT (s1): a baked 1D chroma ramp (256x1). The heat value picks the
// hue (u = heat / TempMax) and ALSO drives luminance, so the flame keeps its
// per-texel HDR energy for bloom while the LUT decides the palette — this is
// what lets one temperature sheet render as orange fire, necro-green fire, or
// anything else purely from data.
//
// Luminance uses the same scene-unit encode as HdrSprite.fx LinearTexture mode:
// sRGB for [0,1] (matches LDR look), linear passthrough above 1.0 (feeds bloom).
//
// Vertex color: HdrColor.ToHdrVertex additive encoding (rgb = tint*fade,
// a = sqrt(I/MaxIntensity)) — same convention as Materials.HdrAdditive.
// Additive-only: output alpha is 1, RGB premultiplied by the source alpha.
//
// IMPORTANT: MaxIntensity and TempMax must be set explicitly from C# — MGFX on
// OpenGL does not honor default uniform values.

float MaxIntensity;
float TempMax;

sampler2D TextureSampler : register(s0);
sampler2D GradientSampler : register(s1);

float EncodeSceneUnits(float v)
{
    v = max(v, 0.0);
    float lo = min(v, 1.0);
    float srgb = lo <= 0.0031308 ? lo * 12.92 : 1.055 * pow(lo, 1.0 / 2.4) - 0.055;
    return v > 1.0 ? v : srgb;
}

float4 PixelShaderFunction(float4 color : COLOR0, float2 texCoord : TEXCOORD0) : COLOR0
{
    float4 tex = tex2D(TextureSampler, texCoord);

    // Un-premultiply to recover the true heat scalar at soft edges.
    float a = max(tex.a, 1e-5);
    float heat = max(tex.r, max(tex.g, tex.b)) / a;

    float3 chroma = tex2D(GradientSampler, float2(saturate(heat / TempMax), 0.5)).rgb;
    float lum = EncodeSceneUnits(heat);

    // Additive path: alpha carries sqrt-encoded intensity — square to decode.
    float intensity = color.a * color.a * MaxIntensity;
    return float4(chroma * color.rgb * (lum * intensity * tex.a), 1.0);
}

technique TempGradientTechnique
{
    pass Pass1
    {
        PixelShader = compile PS_SHADERMODEL PixelShaderFunction();
    }
}
