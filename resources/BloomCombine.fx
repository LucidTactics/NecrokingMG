#if OPENGL
#define SV_POSITION POSITION
#define VS_SHADERMODEL vs_3_0
#define PS_SHADERMODEL ps_3_0
#else
#define VS_SHADERMODEL vs_4_0
#define PS_SHADERMODEL ps_4_0
#endif

// Additive scene + bloom * intensity, then an optional shoulder-only tonemap.
//
// Without the tonemap, everything above 1.0 in the HDR scene hard-clips to
// white at the back-buffer write — stacked additive spell layers turn into
// flat white discs. The shoulder curve leaves pixels below TonemapShoulder
// untouched (scene look is preserved) and rolls everything above it off
// smoothly, reaching pure white only at TonemapWhitePoint. Per-channel, so
// hot cores desaturate toward white gradually (film-like) instead of clipping.
float BloomIntensity;
float TonemapEnabled;     // 0 = legacy hard clip, 1 = shoulder tonemap
float TonemapShoulder;    // rolloff start, in [0,1) — below this, identity
float TonemapWhitePoint;  // HDR value that maps to 1.0 (must be > 1)
float TonemapDesaturate;  // 0 = hue-preserving (glow keeps its color even when
                          // very bright), 1 = per-channel (hot cores bleach to
                          // white like film). Blend between the two.

sampler2D TextureSampler : register(s0);
sampler2D BloomSampler : register(s1);

// Extended Reinhard on the over-shoulder part, rescaled so the slope is
// continuous (=1) at the shoulder and output hits 1.0 exactly at WhitePoint.
float3 ShoulderCurve(float3 c, float s, float range, float W)
{
    float3 x = max(c - s, 0.0) / range;
    float3 r = x * (1.0 + x / (W * W)) / (1.0 + x);
    return min(c, s) + range * min(r, 1.0);
}

float4 PixelShaderFunction(float2 texCoord : TEXCOORD0) : COLOR0
{
    float4 bloom = tex2D(BloomSampler, texCoord);
    float4 base = tex2D(TextureSampler, texCoord);

    float3 color = base.rgb + bloom.rgb * BloomIntensity;

    if (TonemapEnabled > 0.5)
    {
        float s = clamp(TonemapShoulder, 0.0, 0.95);
        float range = 1.0 - s;
        float W = max((TonemapWhitePoint - s) / range, 1.001);

        // Per-channel: channels compress independently, so a bright colored
        // glow drifts toward white (all channels pile up near 1).
        float3 perChannel = ShoulderCurve(color, s, range, W);

        // Hue-preserving: compress only the max channel and rescale RGB by the
        // same factor — channel ratios (the hue) survive any brightness.
        float m = max(color.r, max(color.g, color.b));
        float compressedM = ShoulderCurve(float3(m, m, m), s, range, W).r;
        float3 huePreserved = color * (compressedM / max(m, 0.0001));

        color = lerp(huePreserved, perChannel, clamp(TonemapDesaturate, 0.0, 1.0));
    }

    return float4(color, 1.0);
}

technique BloomCombine
{
    pass Pass1
    {
        PixelShader = compile PS_SHADERMODEL PixelShaderFunction();
    }
}
