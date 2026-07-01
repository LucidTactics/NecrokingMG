#if OPENGL
#define SV_POSITION POSITION
#define PS_SHADERMODEL ps_3_0
#else
#define PS_SHADERMODEL ps_4_0
#endif

// Depth-only "cutout" shader for the depth-sorted-fog occluder pass.
//
// SpriteBatch's default vertex shader turns each sprite's layerDepth into the vertex Z, so with a
// depth-writing DepthStencilState the sprite stamps its layerDepth into the depth buffer. This pixel
// shader just DISCARDS the near-transparent pixels (clip) so only the solid silhouette writes depth
// — otherwise a sprite's transparent margins would write a rectangular depth "halo" that wrongly
// occludes the fog. Drawn with ColorWriteChannels.None, so nothing is written to color; only depth.

sampler2D SpriteTex : register(s0);

float4 PS(float4 color : COLOR0, float2 uv : TEXCOORD0) : COLOR0
{
    // Textures are premultiplied; .a is the sprite coverage. Cut at 0.5 = the solid body only.
    float a = tex2D(SpriteTex, uv).a;
    clip(a - 0.5);
    return float4(0, 0, 0, 0);   // color discarded (ColorWriteChannels.None) — depth is the output
}

technique DepthCutout
{
    pass Pass1
    {
        PixelShader = compile PS_SHADERMODEL PS();
    }
}
