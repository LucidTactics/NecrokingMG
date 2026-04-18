#if OPENGL
#define SV_POSITION POSITION
#define VS_SHADERMODEL vs_3_0
#define PS_SHADERMODEL ps_3_0
#else
#define VS_SHADERMODEL vs_4_0
#define PS_SHADERMODEL ps_4_0
#endif

// Anti-aliased filled circle with optional outer glow.
//
// The filled area can be a top-to-bottom vertical gradient (bone-ring style)
// by using different FillTopColor and FillBottomColor. For a solid fill,
// pass the same color for both.
//
// Outer glow fades from GlowColor at the circle boundary to transparent at
// GlowRadius. Set GlowColor alpha to 0 (or GlowRadius == Radius) to disable.
//
// Sizes are in PIXELS relative to the drawn quad; draw by stretching a 1x1
// pixel across the quad that contains the full circle + glow.
float2 RectSize;       // drawn quad size, px
float2 Center;         // circle center inside quad, px
float Radius;          // fill radius, px
float GlowRadius;      // outer extent of glow (>= Radius), px
float EdgeAA;          // anti-aliasing band width, px
float4 FillTopColor;
float4 FillBottomColor;
float4 GlowColor;

sampler2D TextureSampler : register(s0);

float4 PixelShaderFunction(float2 texCoord : TEXCOORD0) : COLOR0
{
    float2 px = texCoord * RectSize;
    float2 d = px - Center;
    float dist = length(d);

    float aa = max(EdgeAA, 0.5);

    // Fill mask: 1 inside, 0 outside, AA band straddling Radius.
    float fillMask = saturate((Radius - dist) / aa + 0.5);

    // Glow mask: 0 inside circle (except AA band), ramps to 1 just outside
    // Radius, fades to 0 at GlowRadius. Quadratic falloff.
    float glowSpan = max(GlowRadius - Radius, 0.001);
    float glowT = saturate((dist - Radius) / glowSpan);
    float glowMask = (1.0 - glowT) * (1.0 - glowT);
    // Glow only where fill is not present:
    glowMask *= (1.0 - fillMask);

    // Fill color (vertical gradient based on y within the circle bounding box)
    float yt = saturate((px.y - (Center.y - Radius)) / (2.0 * Radius));
    float4 fill = lerp(FillTopColor, FillBottomColor, yt);
    fill.a *= fillMask;

    float4 glow = GlowColor;
    glow.a *= glowMask;

    // Combine: both masks mutually exclusive, add their premultiplied forms.
    float4 c;
    c.rgb = fill.rgb * fill.a + glow.rgb * glow.a;
    c.a   = fill.a + glow.a;
    return c;
}

technique UICircleEffect
{
    pass Pass1
    {
        PixelShader = compile PS_SHADERMODEL PixelShaderFunction();
    }
}
