#if OPENGL
#define SV_POSITION POSITION
#define VS_SHADERMODEL vs_3_0
#define PS_SHADERMODEL ps_3_0
#else
#define VS_SHADERMODEL vs_4_0
#define PS_SHADERMODEL ps_4_0
#endif

// Rectangle shadow (drop + inset). Works in pixel space so corners stay
// circular regardless of rect aspect ratio.
//
// Draw by stretching a 1x1 pixel across the *outer* rect (fill + padding
// for the shadow). The shader computes a signed distance to the inner
// rect and uses it to shade outside (drop) or inside-near-edge (inset).
//
// Mode:
//   0 = drop shadow (fill inside, shadow fades outward to Softness)
//   1 = inset shadow (shadow at edges, fades inward to Softness)
//
// All sizes/offsets are in PIXELS (RectSize is the drawn quad size).
float Mode;
float2 RectSize;     // size of the drawn quad, px
float2 InnerOffset;  // top-left of inner rect, relative to drawn quad, px
float2 InnerSize;    // inner rect size, px
float Softness;      // shadow distance, px
float4 FillColor;    // inside fill (RGBA, non-premultiplied). Can be transparent.
float4 ShadowColor;  // shadow color (RGBA, non-premultiplied)

sampler2D TextureSampler : register(s0);

float4 PixelShaderFunction(float2 texCoord : TEXCOORD0) : COLOR0
{
    float2 px = texCoord * RectSize;
    float2 innerCenter = InnerOffset + InnerSize * 0.5;
    float2 d = abs(px - innerCenter) - InnerSize * 0.5;
    // Standard 2D axis-aligned rect SDF.
    float outside = length(max(d, 0));
    float inside  = min(max(d.x, d.y), 0);
    float sdf = outside + inside; // >0 outside, <0 inside, 0 on edge

    float soft = max(Softness, 0.001);

    float4 c;
    if (Mode < 0.5)
    {
        // Drop shadow: fill inside, shadow fades outside.
        if (sdf <= 0)
        {
            c = FillColor;
        }
        else
        {
            float t = 1.0 - saturate(sdf / soft);
            c = ShadowColor;
            c.a *= t * t;
        }
    }
    else
    {
        // Inset shadow: transparent outside, shadow near inside edges
        // fading inward to FillColor.
        if (sdf >= 0)
        {
            c = float4(0, 0, 0, 0);
        }
        else
        {
            float innerDist = -sdf;
            float t = saturate(innerDist / soft); // 0 at edge, 1 deep inside
            t = t * t;
            c = lerp(ShadowColor, FillColor, t);
        }
    }
    // MonoGame SpriteBatch expects premultiplied alpha
    c.rgb *= c.a;
    return c;
}

technique UIRectShadow
{
    pass Pass1
    {
        PixelShader = compile PS_SHADERMODEL PixelShaderFunction();
    }
}
