#if OPENGL
#define SV_POSITION POSITION
#define VS_SHADERMODEL vs_3_0
#define PS_SHADERMODEL ps_3_0
#else
#define VS_SHADERMODEL vs_4_0
#define PS_SHADERMODEL ps_4_0
#endif

// Temporal smoothing pass for fog of war.
// Given a source "hard visibility" RT and a destination "smoothed visibility"
// RT already loaded, this shader rewrites the source color so that when drawn
// with premultiplied AlphaBlend, the result is:
//   newDst = lerp(oldDst, src, Rate)
//
// Works identically on DesktopGL and WindowsDX (unlike Blend.BlendFactor,
// which is not reliably supported in the OpenGL backend).

sampler2D SrcSampler : register(s0);
float Rate; // 0..1 blend strength this frame (= dt / FadeTime)

float4 PixelShaderFunction(float2 texCoord : TEXCOORD0) : COLOR0
{
    float4 c = tex2D(SrcSampler, texCoord);
    // Premultiply: output = (c.rgb * Rate, Rate). With BlendState.AlphaBlend
    // (One / InvSrcAlpha) this evaluates to newDst.rgb = c.rgb * Rate + oldDst.rgb * (1 - Rate).
    return float4(c.rgb * Rate, Rate);
}

technique FogSmooth
{
    pass Pass1
    {
        PixelShader = compile PS_SHADERMODEL PixelShaderFunction();
    }
}
