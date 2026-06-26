#if OPENGL
#define SV_POSITION POSITION
#define VS_SHADERMODEL vs_3_0
#define PS_SHADERMODEL ps_3_0
#else
#define VS_SHADERMODEL vs_4_0
#define PS_SHADERMODEL ps_4_0
#endif

// SDF morph shader for the reanimation "amoeba" body morph.
//
// Instead of cross-dissolving (ghosting two poses), this interpolates the SIGNED DISTANCE
// FIELDS of the death pose (ColorA) and the standup-start pose (ColorB) — packed in SdfMap.r/.g
// — and re-thresholds, so the silhouette continuously deforms: pixels GROW where the target
// sticks out and SHED where the source sticks out. A mid-morph Bulge swells the shape through
// the transition (the amoeba look). The morphed shape is filled with the crossfaded body color,
// green energy in the bulge "bridge" gaps, and a pulsing green outline traces the morphed edge.
//
// All three textures share one pivot-aligned canvas, so UVs map 1:1. Colors are premultiplied
// (from the atlas); the quad is drawn in the standard premultiplied AlphaBlend batch.

sampler2D ColorA : register(s0);   // death-pose color (premultiplied)
sampler2D ColorB : register(s1);   // standup-start color (premultiplied)
sampler2D SdfMap : register(s2);   // r = death SDF, g = standup SDF, encoded 0.5 + d/(2*MaxDist)

float MorphT;          // 0 = death, 1 = standup
float MaxDist;         // SDF decode scale (canvas px)
float EdgeSoftness;    // anti-alias band half-width (canvas px)
float Bulge;           // mid-morph inflation (canvas px) — the amoeba swell
float3 GreenFill;      // energy color filling the morph bulge "bridge" gaps (straight color)
float3 OutlineColor;   // pulsing edge outline color (straight color)
float OutlineWidth;    // outline band half-width (canvas px)
float OutlinePulse;    // 0..1 outline strength (fades in + pulses, from CPU)

float decodeSdf(float e) { return (e - 0.5) * 2.0 * MaxDist; }

float4 PixelShaderFunction(float4 color : COLOR0, float2 uv : TEXCOORD0) : COLOR0
{
    float4 cA = tex2D(ColorA, uv);
    float4 cB = tex2D(ColorB, uv);
    float2 enc = tex2D(SdfMap, uv).rg;
    float dA = decodeSdf(enc.r);
    float dB = decodeSdf(enc.g);

    // Interpolate the distance fields, with a mid-morph bulge so the shape swells through the
    // transition instead of just sliding A->B (the amoeba gaining/shedding pixels).
    float dM = lerp(dA, dB, MorphT) - Bulge * 4.0 * MorphT * (1.0 - MorphT);

    // Morphed silhouette coverage (1 inside, 0 outside, AA edge). d<0 is inside.
    float cover = smoothstep(EdgeSoftness, -EdgeSoftness, dM);

    // Body fill: crossfade the two premultiplied colors -> straight color; green energy where
    // the morphed shape bulges past either body (the bridge gap).
    float aA = cA.a, aB = cB.a;
    float3 bodyPM = cA.rgb * (1.0 - MorphT) + cB.rgb * MorphT;
    float  aBody  = aA * (1.0 - MorphT) + aB * MorphT;
    float3 bodyStraight = aBody > 0.001 ? bodyPM / aBody : float3(0, 0, 0);
    float bodyFrac = saturate(aBody / max(cover, 0.001));   // 1 where body fills the shape, 0 in the bridge
    float3 fillStraight = lerp(GreenFill, bodyStraight, bodyFrac);

    float3 outRGB = fillStraight * cover;   // premultiply by coverage
    float outA = cover;

    // Pulsing green outline tracing the morphed edge (a band around d=0).
    float band = (1.0 - smoothstep(OutlineWidth, OutlineWidth + EdgeSoftness, abs(dM))) * OutlinePulse;
    outRGB += OutlineColor * band;
    outA = max(outA, band);

    return float4(outRGB, outA) * color;
}

technique MorphSDF
{
    pass Pass1
    {
        PixelShader = compile PS_SHADERMODEL PixelShaderFunction();
    }
}
