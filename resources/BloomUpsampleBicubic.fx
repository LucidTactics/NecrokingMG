#if OPENGL
#define SV_POSITION POSITION
#define VS_SHADERMODEL vs_3_0
#define PS_SHADERMODEL ps_3_0
#else
#define VS_SHADERMODEL vs_4_0
#define PS_SHADERMODEL ps_4_0
#endif

// Matches C++ bloom_upsample_bicubic.fs: Catmull-Rom bicubic upsampling
// using 4 bilinear taps instead of 16 point samples.
float2 TexelSize; // 1.0 / source texture size

sampler2D TextureSampler : register(s0);

// Catmull-Rom spline weights
float4 cubic(float x)
{
    float x2 = x * x;
    float x3 = x2 * x;
    float4 w;
    w.x = -x3 + 2.0 * x2 - x;            // weight at t-1
    w.y =  3.0 * x3 - 5.0 * x2 + 2.0;    // weight at t
    w.z = -3.0 * x3 + 4.0 * x2 + x;       // weight at t+1
    w.w =  x3 - x2;                        // weight at t+2
    return w * 0.5;
}

float4 PixelShaderFunction(float4 color : COLOR0, float2 texCoord : TEXCOORD0) : COLOR0
{
    float2 texSize = 1.0 / TexelSize;
    float2 coord = texCoord * texSize - 0.5;
    float2 f = frac(coord);
    coord -= f;

    float4 xw = cubic(f.x);
    float4 yw = cubic(f.y);

    // Combine adjacent pairs of weights for bilinear trick
    float4 s = float4(xw.x + xw.y, xw.z + xw.w, yw.x + yw.y, yw.z + yw.w);

    // Offset within each pair
    float4 offset = coord.xxyy + float4(-0.5, 1.5, -0.5, 1.5) + float4(xw.y, xw.w, yw.y, yw.w) / s;
    offset *= TexelSize.xxyy;

    // 4 bilinear samples covering the 4x4 grid
    float4 s0 = tex2D(TextureSampler, offset.xz);
    float4 s1 = tex2D(TextureSampler, offset.yz);
    float4 s2 = tex2D(TextureSampler, offset.xw);
    float4 s3 = tex2D(TextureSampler, offset.yw);

    float sx = s.x / (s.x + s.y);
    float sy = s.z / (s.z + s.w);

    return lerp(lerp(s3, s2, sx), lerp(s1, s0, sx), sy) * color;
}

technique BloomUpsampleBicubic
{
    pass Pass1
    {
        PixelShader = compile PS_SHADERMODEL PixelShaderFunction();
    }
}
