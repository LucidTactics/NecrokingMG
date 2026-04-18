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

sampler2D TextureSampler : register(s0);

float4 PixelShaderFunction(float2 texCoord : TEXCOORD0) : COLOR0
{
    float4 c;
    if (Mode < 0.5)
    {
        // vertical linear: top -> bottom
        c = lerp(ColorA, ColorB, saturate(texCoord.y));
    }
    else if (Mode < 1.5)
    {
        // horizontal linear: left -> right
        c = lerp(ColorA, ColorB, saturate(texCoord.x));
    }
    else if (Mode < 2.5)
    {
        // vertical 3-stop: A -> B at MidStop -> C
        float t = saturate(texCoord.y);
        float mid = clamp(MidStop, 0.001, 0.999);
        if (t < mid)
            c = lerp(ColorA, ColorB, t / mid);
        else
            c = lerp(ColorB, ColorC, (t - mid) / (1.0 - mid));
    }
    else if (Mode < 3.5)
    {
        // radial 2-stop: A at center, B at Radius
        float d = length(texCoord - Center) / max(Radius, 0.001);
        c = lerp(ColorA, ColorB, saturate(d));
    }
    else
    {
        // radial 3-stop: A at center, B at MidStop*Radius, C at Radius
        float d = length(texCoord - Center) / max(Radius, 0.001);
        float t = saturate(d);
        float mid = clamp(MidStop, 0.001, 0.999);
        if (t < mid)
            c = lerp(ColorA, ColorB, t / mid);
        else
            c = lerp(ColorB, ColorC, (t - mid) / (1.0 - mid));
    }
    // MonoGame SpriteBatch expects premultiplied alpha
    c.rgb *= c.a;
    return c;
}

technique UIGradient
{
    pass Pass1
    {
        PixelShader = compile PS_SHADERMODEL PixelShaderFunction();
    }
}
