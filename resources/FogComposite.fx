#if OPENGL
#define SV_POSITION POSITION
#define VS_SHADERMODEL vs_3_0
#define PS_SHADERMODEL ps_3_0
#else
#define VS_SHADERMODEL vs_4_0
#define PS_SHADERMODEL ps_4_0
#endif

// Fog of war composite shader.
// Combines visibility (current sight) and explored (ever-seen) render targets
// into a fog overlay with three states:
//   - Visible (vis > 0): fully transparent
//   - Explored but not visible (exp > 0, vis == 0): semi-transparent (foggedAlpha)
//   - Unexplored (both 0): fully opaque (unexploredAlpha)

float UnexploredAlpha; // 0-1, typically 1.0
float FoggedAlpha;     // 0-1, typically 0.7

// Single combined texture:
//   R channel = explored (persistent, ever-seen)
//   G channel = visible  (current-frame vision)
// Merged into one RT by the C# side so we only need one sampler — MonoGame's
// SpriteBatch can't reliably keep a second texture bound to slot 1 across a
// flush, so we avoid the problem entirely by using a single slot-0 sampler.
sampler2D FogSampler : register(s0);

float4 PixelShaderFunction(float2 texCoord : TEXCOORD0) : COLOR0
{
    float2 sample = tex2D(FogSampler, texCoord).rg;
    float explored = sample.r;
    float visible  = sample.g;

    // Wide smoothstep stretches the circle texture's feathered perimeter into a
    // diffuse transition between visible/fogged/unexplored — softer than a hard
    // cutoff but still clearly oval-shaped.
    float visAA = smoothstep(0.05, 0.95, visible);
    float expAA = smoothstep(0.05, 0.95, explored);

    float baseAlpha = lerp(UnexploredAlpha, FoggedAlpha, expAA);
    float fogAlpha = lerp(baseAlpha, 0.0, visAA);

    return float4(0.0, 0.0, 0.0, fogAlpha);
}

technique FogComposite
{
    pass Pass1
    {
        PixelShader = compile PS_SHADERMODEL PixelShaderFunction();
    }
}
