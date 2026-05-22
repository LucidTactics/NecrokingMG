#if OPENGL
#define SV_POSITION POSITION
#define VS_SHADERMODEL vs_3_0
#define PS_SHADERMODEL ps_3_0
#else
#define VS_SHADERMODEL vs_4_0
#define PS_SHADERMODEL ps_4_0
#endif

// Unit-sprite shader for wading / partially submerged characters.
//
// The waterline is a LINE in local frame UV (not a row). Each cut has a
// centre V (V where the line crosses U=0.5) and a slope (dV/dU). For
// horizontal cuts (cardinal facings), slope=0 — same as before. For 3/4
// facings, the slope follows the body axis projected to screen so the cut
// runs parallel to the body (legs disappear across the whole length,
// instead of horizontal-chopping the rear half).
//
// Pixels with V below the bottom line OR above the top line are faded to
// UnderwaterAlpha. A foam smear sits straddling each line.
//
// Frame U/V uniforms convert atlas UV (what SpriteBatch passes the shader)
// into local 0..1 frame UV, so cut parameters are sprite-relative and don't
// depend on where the frame sits in the atlas.

float FrameTopV;
float FrameBottomV;
float FrameLeftU;
float FrameRightU;

float WaterlineCenterV;     // bottom waterline V at U=0.5
float WaterlineSlope;       // dV/dU in local frame UV (0 = horizontal)
float TopWaterlineCenterV;  // top waterline V at U=0.5; <0 disables the cut
float TopWaterlineSlope;    // dV/dU for the top cut

float FoamHalfWidth;
float TopFoamHalfWidth;
float UnderwaterAlpha;
float3 FoamColor;

sampler2D TextureSampler : register(s0);

float4 PixelShaderFunction(float4 vColor : COLOR0, float2 texCoord : TEXCOORD0) : COLOR0
{
    float4 p = tex2D(TextureSampler, texCoord);
    if (p.a < 0.001) return float4(0, 0, 0, 0);
    float3 rgbStraight = p.rgb / p.a;
    rgbStraight *= vColor.rgb;
    float alpha = p.a * vColor.a;

    // Normalize atlas UV into local frame UV (0..1 across the sprite frame).
    float localU = (texCoord.x - FrameLeftU) / max(FrameRightU - FrameLeftU, 0.00001);
    float localV = (texCoord.y - FrameTopV) / max(FrameBottomV - FrameTopV, 0.00001);

    // Expected V of each waterline at this pixel's local U.
    float expectedBottomV = WaterlineCenterV + WaterlineSlope * (localU - 0.5);
    float expectedTopV    = TopWaterlineCenterV + TopWaterlineSlope * (localU - 0.5);

    // Signed depth: > 0 means hidden (below bottom or above top).
    float bottomDepth = localV - expectedBottomV;     // > 0 → below bottom waterline
    float topDepth    = expectedTopV - localV;        // > 0 → above top waterline

    float alphaMul = 1.0;
    float3 mixedRGB = rgbStraight;

    // --- Bottom waterline ---
    if (bottomDepth >= FoamHalfWidth)
    {
        alphaMul = min(alphaMul, UnderwaterAlpha);
    }
    else if (bottomDepth > -FoamHalfWidth)
    {
        float t = (bottomDepth + FoamHalfWidth) / (2.0 * FoamHalfWidth);
        alphaMul = min(alphaMul, lerp(1.0, UnderwaterAlpha, t));
        float foamMix = 1.0 - abs(2.0 * t - 1.0);
        mixedRGB = lerp(mixedRGB, FoamColor, foamMix * 0.65);
    }

    // --- Top waterline (back submerged). TopWaterlineCenterV ≤ 0 effectively
    // disables this branch because no body pixel has localV < that value. ---
    if (topDepth >= TopFoamHalfWidth)
    {
        alphaMul = min(alphaMul, UnderwaterAlpha);
    }
    else if (topDepth > -TopFoamHalfWidth)
    {
        float t = (topDepth + TopFoamHalfWidth) / (2.0 * TopFoamHalfWidth);
        alphaMul = min(alphaMul, lerp(1.0, UnderwaterAlpha, t));
        float foamMix = 1.0 - abs(2.0 * t - 1.0);
        mixedRGB = lerp(mixedRGB, FoamColor, foamMix * 0.65);
    }

    alpha *= alphaMul;
    return float4(mixedRGB * alpha, alpha);
}

technique Wading
{
    pass Pass1
    {
        PixelShader = compile PS_SHADERMODEL PixelShaderFunction();
    }
}
