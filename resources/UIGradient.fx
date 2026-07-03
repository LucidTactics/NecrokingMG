#if OPENGL
#define SV_POSITION POSITION
#define VS_SHADERMODEL vs_3_0
#define PS_SHADERMODEL ps_3_0
#else
#define VS_SHADERMODEL vs_4_0
#define PS_SHADERMODEL ps_4_0
#endif

// UI gradient shader.
// Mode selects the gradient type:
//   0 = vertical linear (ColorA top -> ColorB bottom)
//   1 = horizontal linear (ColorA left -> ColorB right)
//   2 = vertical 3-stop  (A -> B at MidStop -> C)
//   3 = radial (ColorA at Center -> ColorB at Center+Radius, clamped beyond)
//   4 = radial 3-stop (A at Center -> B at MidStop*Radius -> C at Radius)
//
// Draw by stretching the 1x1 pixel texture across the target rect with
// SpriteBatch; texCoord varies 0..1 across the rect so the math is local
// to the rect and independent of pixel coordinates.
//
// Output is premultiplied RGBA for SpriteBatch BlendState.AlphaBlend.
float Mode;

float4 ColorA;
float4 ColorB;
float4 ColorC;
float MidStop;
float2 Center;
float Radius;

// s0 is reserved by SpriteBatch for the drawn texture; this shader is purely
// procedural and intentionally never samples it.
sampler2D TextureSampler : register(s0);

float4 PixelShaderFunction(float2 texCoord : TEXCOORD0) : COLOR0
{
    // Premultiply the stops BEFORE interpolating: lerping straight-alpha colors
    // toward a transparent stop drags RGB toward that stop's (invisible) RGB,
    // producing dark fringes mid-fade. Lerping premultiplied colors is
    // fringe-free, and for fully opaque stops the result is identical.
    float4 pa = float4(ColorA.rgb * ColorA.a, ColorA.a);
    float4 pb = float4(ColorB.rgb * ColorB.a, ColorB.a);
    float4 pc = float4(ColorC.rgb * ColorC.a, ColorC.a);

    float4 c;
    if (Mode < 0.5)
    {
        // vertical linear: top -> bottom
        c = lerp(pa, pb, saturate(texCoord.y));
    }
    else if (Mode < 1.5)
    {
        // horizontal linear: left -> right
        c = lerp(pa, pb, saturate(texCoord.x));
    }
    else if (Mode < 2.5)
    {
        // vertical 3-stop: A -> B at MidStop -> C
        float t = saturate(texCoord.y);
        float mid = clamp(MidStop, 0.001, 0.999);
        if (t < mid)
            c = lerp(pa, pb, t / mid);
        else
            c = lerp(pb, pc, (t - mid) / (1.0 - mid));
    }
    else if (Mode < 3.5)
    {
        // radial 2-stop: A at center, B at Radius
        float d = length(texCoord - Center) / max(Radius, 0.001);
        c = lerp(pa, pb, saturate(d));
    }
    else
    {
        // radial 3-stop: A at center, B at MidStop*Radius, C at Radius
        float d = length(texCoord - Center) / max(Radius, 0.001);
        float t = saturate(d);
        float mid = clamp(MidStop, 0.001, 0.999);
        if (t < mid)
            c = lerp(pa, pb, t / mid);
        else
            c = lerp(pb, pc, (t - mid) / (1.0 - mid));
    }
    // Already premultiplied (stops converted above).
    return c;
}

technique UIGradient
{
    pass Pass1
    {
        PixelShader = compile PS_SHADERMODEL PixelShaderFunction();
    }
}
