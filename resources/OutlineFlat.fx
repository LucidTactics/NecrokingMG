#if OPENGL
#define SV_POSITION POSITION
#define VS_SHADERMODEL vs_3_0
#define PS_SHADERMODEL ps_3_0
#else
#define VS_SHADERMODEL vs_4_0
#define PS_SHADERMODEL ps_4_0
#endif

// Flat-color outline shader: dilates the sprite silhouette by RadiusUv and
// outputs OutlineColor over the union mask. Single-pass replacement for the
// old "redraw the silhouette 8x at directional offsets" approach — the union
// (max) happens in-shader, so the layers can never additively stack with
// themselves. Two techniques, chosen by the caller from the effect's MAX
// screen radius (pulse peak) so the pattern never pops mid-pulse: Outline8
// (cheap ring, radii <= ~3px) and Outline24 (16+8 rings, keeps the rim round
// instead of faceted at large radii). Draw on a quad EXPANDED by the radius
// (the rim extends past the frame rect); FrameUvMin/Max clamp sampling to
// the source frame so atlas neighbors can't leak in.
//
// ALPHA CONVENTION: outputs STRAIGHT (non-premultiplied) alpha — the deliberate
// exception to the codebase's premultiplied-output convention. Draw only in
// NonPremultiplied or Additive batches; in a premultiplied AlphaBlend batch the
// soft edges render as a bright halo.
//
// All uniforms are set per draw from C# (MGFX on GL ignores .fx initializers).
float4 OutlineColor;
float2 FrameUvMin;   // source frame rect min, atlas UV
float2 FrameUvMax;   // source frame rect max, atlas UV
float2 RadiusUv;     // outline radius in UV units per axis

sampler2D TextureSampler : register(s0);

float SampleMask(float2 uv)
{
    // Outside the source frame rect = transparent. The expanded quad samples
    // beyond the rect by design; without this, tightly-packed atlas neighbors
    // would bleed into the rim.
    float inside = step(FrameUvMin.x, uv.x) * step(uv.x, FrameUvMax.x)
                 * step(FrameUvMin.y, uv.y) * step(uv.y, FrameUvMax.y);
    return tex2D(TextureSampler, uv).a * inside;
}

// Cheap variant: center + one 8-direction ring. Enough while the radius
// (including the pulse peak) stays small — the facet gaps are sub-pixel.
float4 PS_Outline8(float2 texCoord : TEXCOORD0) : COLOR0
{
    float m = SampleMask(texCoord);
    [unroll] for (int i = 0; i < 8; i++)
    {
        float ang = i * 0.7853981634; // 2*pi/8
        float2 d = float2(cos(ang), sin(ang));
        m = max(m, SampleMask(texCoord + d * RadiusUv));
    }
    return float4(OutlineColor.rgb, OutlineColor.a * m);
}

// Wide variant: center + 16 directions at full radius + 8 at half radius.
// Keeps the rim round instead of faceted once the radius exceeds a few px.
// cos/sin of the constant angles fold at compile time under [unroll].
float4 PS_Outline24(float2 texCoord : TEXCOORD0) : COLOR0
{
    float m = SampleMask(texCoord);
    [unroll] for (int i = 0; i < 16; i++)
    {
        float ang = i * 0.3926990817; // 2*pi/16
        float2 d = float2(cos(ang), sin(ang));
        m = max(m, SampleMask(texCoord + d * RadiusUv));
    }
    [unroll] for (int j = 0; j < 8; j++)
    {
        float ang2 = j * 0.7853981634; // 2*pi/8
        float2 d2 = float2(cos(ang2), sin(ang2));
        m = max(m, SampleMask(texCoord + d2 * RadiusUv * 0.5));
    }
    return float4(OutlineColor.rgb, OutlineColor.a * m);
}

technique Outline8
{
    pass Pass1
    {
        PixelShader = compile PS_SHADERMODEL PS_Outline8();
    }
}

technique Outline24
{
    pass Pass1
    {
        PixelShader = compile PS_SHADERMODEL PS_Outline24();
    }
}
