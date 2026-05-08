#if OPENGL
#define SV_POSITION POSITION
#define VS_SHADERMODEL vs_3_0
#define PS_SHADERMODEL ps_3_0
#else
#define VS_SHADERMODEL vs_4_0
#define PS_SHADERMODEL ps_4_0
#endif

// Dissolve shader for the tree corruption transition.
//
// The dead-sprite texture is bound as the SpriteBatch's drawn texture (s0).
// The live spritesheet is bound via Effect.Parameters; the renderer also
// supplies the UV bounds of frame 0 so the shader pulls the correct slice
// out of the larger sheet.
//
// At each pixel: sample low-frequency noise; if noise < Threshold the pixel
// has dissolved away (show dead sprite), otherwise show the live frame.
// A small smoothstep band gives a soft edge; per-instance Seed offsets the
// noise so neighboring trees don't dissolve in identical patterns.

sampler2D DeadSampler : register(s0);
sampler2D LiveSampler : register(s1);

// xy = uv top-left of frame 0 in the live spritesheet
// zw = uv bottom-right of frame 0
float4 LiveFrameUV;
float Threshold;       // 0 = fully live, 1 = fully dissolved
float Seed;            // 0..1 per-instance offset
float NoiseScale = 6;  // higher = smaller blotches
float EdgeSoftness = 0.06;
float DebugMode = 0;   // when > 0.5: output diagnostic colors instead of dissolve

float hash21(float2 p)
{
    p = frac(p * float2(123.34, 456.21));
    p += dot(p, p + 45.32);
    return frac(p.x * p.y);
}

float valueNoise(float2 p)
{
    float2 i = floor(p);
    float2 f = frac(p);
    float2 u = f * f * (3.0 - 2.0 * f);
    float a = hash21(i);
    float b = hash21(i + float2(1.0, 0.0));
    float c = hash21(i + float2(0.0, 1.0));
    float d = hash21(i + float2(1.0, 1.0));
    return lerp(lerp(a, b, u.x), lerp(c, d, u.x), u.y);
}

float4 PixelShaderFunction(float4 color : COLOR0, float2 texCoord : TEXCOORD0) : COLOR0
{
    float4 deadCol = tex2D(DeadSampler, texCoord);
    float2 liveUv  = lerp(LiveFrameUV.xy, LiveFrameUV.zw, texCoord);
    float4 liveCol = tex2D(LiveSampler, liveUv);

    if (DebugMode > 0.5)
    {
        // Diagnostic: red = dead alpha, green = live alpha, blue = threshold.
        // If green never appears, LiveSampler is not bound; if blue doesn't rise
        // over 5s, the threshold uniform isn't animating.
        return float4(deadCol.a, liveCol.a, Threshold, 1.0);
    }

    // SIMPLE CROSSFADE (debug build): linear lerp(live -> dead) by Threshold.
    // If this fades smoothly across 5s, the noise math is the issue.
    // If it still flips suddenly, the live texture isn't sampling correctly.
    float4 result = lerp(liveCol, deadCol, Threshold);
    return result * color;
}

technique DissolveTree
{
    pass Pass1
    {
        PixelShader = compile PS_SHADERMODEL PixelShaderFunction();
    }
}
