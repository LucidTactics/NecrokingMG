#if OPENGL
#define SV_POSITION POSITION
#define VS_SHADERMODEL vs_3_0
#define PS_SHADERMODEL ps_3_0
#else
#define VS_SHADERMODEL vs_4_0
#define PS_SHADERMODEL ps_4_0
#endif

// Glyph parameters
float Time;
float Activation;       // 0 = dormant, 0..1 = triggering, 1 = fully active
float Intensity;        // Overall brightness multiplier
float3 GlyphColor;      // Base color (RGB, 0-1)
float3 GlyphColor2;     // Secondary color for inner elements
float Rotation;         // Base rotation in radians
float PulseSpeed;       // Pulse frequency

// s0 is reserved by SpriteBatch for the drawn texture; this shader is purely
// procedural and intentionally never samples it.
sampler2D TextureSampler : register(s0);

static const float PI = 3.14159265;
static const float TAU = 6.28318530;

// ─── Design constants ───
// All geometry is in normalized quad UV: the quad spans -1..1, so radii and
// widths scale with the drawn quad (a known limitation — lines soften on big
// quads and alias on small ones). Alpha weights are per-feature contributions;
// they intentionally sum past 1 where features overlap and saturate() clips.
static const float SlowRotRate     = 0.2;    // idle rotation rad/s — matches MagicGlyphRenderer's ribbons
static const float OuterRingR      = 0.90;
static const float OuterRingThick  = 0.076;
static const float OuterRingAlpha  = 0.9;
static const float TrimRingR       = 0.95;   // thin decorative ring outside the main one
static const float TrimRingThick   = 0.036;
static const float TrimRingAlpha   = 0.5;
static const float RuneOrbitR      = 0.925;  // rune marks orbit between the two outer rings
static const float RuneSize        = 0.06;
static const float RuneAlpha       = 0.6;
static const float RuneColorMix    = 0.4;
static const float InnerRingR      = 0.70;   // shared: inner circle, pentagram, vertex dots, energy lines
static const float InnerRingThick  = 0.054;
static const float InnerRingAlpha  = 0.8;
static const float PentaLineWidth  = 0.054;
static const float PentaAlpha      = 0.85;
static const float PentaColorMix   = 0.3;
static const float CenterRingR     = 0.18;
static const float CenterRingThick = 0.036;
static const float CenterRingAlpha = 0.7;
static const float CenterDotOuter  = 0.09;   // dot fades in from here...
static const float CenterDotInner  = 0.03;   // ...to full by here
static const float CenterDotAlpha  = 0.8;
static const float CenterDotColorMix = 0.5;
static const float VertexDotOuter  = 0.06;
static const float VertexDotInner  = 0.022;
static const float VertexDotAlpha  = 0.7;
static const float VertexDotColorMix = 0.5;
static const float PulseBase       = 0.75;   // brightness pulses PulseBase ± PulseAmp
static const float PulseAmp        = 0.25;
static const float ActGlowAlpha    = 0.4;    // activation: soft center-glow strength
static const float ActColorMix     = 0.4;    // activation shift toward secondary color
static const float EnergyLineLen   = 0.25;   // radial line length past each pentagram vertex
static const float EnergyLineOuter = 0.022;  // line fades in from this SDF distance...
static const float EnergyLineInner = 0.005;  // ...to full by this
static const float EnergyLineAlpha = 0.6;
static const float EnergyLineColorMix = 0.5;
static const float ShimmerFreq     = 8.0;    // energy-line flicker rate
static const float ShimmerPhase    = 1.3;    // per-line phase offset
static const float EdgeFadeStart   = 0.92;   // everything fades out between here...
static const float EdgeFadeEnd     = 1.0;    // ...and the quad edge
static const float DormantDim      = 0.85;   // brightness floor at Activation 0

// ─── Utility ───

float Ring(float dist, float r, float thick)
{
    return smoothstep(thick, 0.0, abs(dist - r));
}

// Line segment SDF: distance from point p to segment a→b
float LineSDF(float2 p, float2 a, float2 b)
{
    float2 ab = b - a;
    float t = saturate(dot(p - a, ab) / dot(ab, ab));
    return length(p - a - ab * t);
}

// ─── Pentagram ───

// Returns the outline intensity of a pentagram (5-pointed star drawn with lines)
float Pentagram(float2 uv, float radius, float rot, float lineWidth)
{
    float result = 0.0;

    // 5 vertices of the pentagram
    float2 verts[5];
    for (int i = 0; i < 5; i++)
    {
        float a = rot + (float)i * TAU / 5.0 - PI / 2.0; // Start from top
        verts[i] = float2(cos(a), sin(a)) * radius;
    }

    // Connect every other vertex (skip 1) to form the star
    // 0→2, 2→4, 4→1, 1→3, 3→0
    int connections[10] = { 0,2, 2,4, 4,1, 1,3, 3,0 };
    for (int j = 0; j < 5; j++)
    {
        float d = LineSDF(uv, verts[connections[j*2]], verts[connections[j*2+1]]);
        result = max(result, smoothstep(lineWidth, lineWidth * 0.3, d));
    }

    return result;
}

// Small rune mark: rotated cross/diamond at a position
float RuneMark(float2 uv, float2 center, float size, float rot)
{
    float2 d = uv - center;
    float c = cos(rot); float s = sin(rot);
    float2 rd = float2(d.x*c + d.y*s, -d.x*s + d.y*c);

    // Diamond shape
    float diamond = (abs(rd.x) + abs(rd.y)) / size;
    float shape = smoothstep(1.0, 0.7, diamond);

    // Inner cross cutout
    float cross = smoothstep(size * 0.2, 0.0, min(abs(rd.x), abs(rd.y)));
    return max(shape * 0.6, cross * smoothstep(1.3, 0.5, diamond));
}

// ─── Main pixel shader ───

float4 PixelShaderFunction(float2 texCoord : TEXCOORD0) : COLOR0
{
    float2 uv = texCoord * 2.0 - 1.0;
    float dist = length(uv);

    float alpha = 0.0;
    float3 color = GlyphColor;

    float rot = Rotation;
    float slowRot = Time * SlowRotRate;

    // ─── Outer circle (thick, glowing) ───
    float outer = Ring(dist, OuterRingR, OuterRingThick);
    alpha += outer * OuterRingAlpha;

    // ─── Outer decorative ring ───
    float outerThin = Ring(dist, TrimRingR, TrimRingThick);
    alpha += outerThin * TrimRingAlpha;

    // ─── Rune marks between outer rings ───
    for (int i = 0; i < 5; i++)
    {
        float markAngle = (float)i * TAU / 5.0 + slowRot;
        float2 markPos = float2(cos(markAngle), sin(markAngle)) * RuneOrbitR;
        float mark = RuneMark(uv, markPos, RuneSize, markAngle + PI * 0.25);
        alpha += mark * RuneAlpha;
        color = lerp(color, GlyphColor2, mark * RuneColorMix);
    }

    // ─── Inner circle ───
    float inner = Ring(dist, InnerRingR, InnerRingThick);
    alpha += inner * InnerRingAlpha;

    // ─── Pentagram (connected to inner circle) ───
    float penta = Pentagram(uv, InnerRingR, rot + slowRot, PentaLineWidth);
    alpha += penta * PentaAlpha;
    color = lerp(color, GlyphColor2, penta * PentaColorMix);

    // ─── Small inner circle at center ───
    float centerRing = Ring(dist, CenterRingR, CenterRingThick);
    alpha += centerRing * CenterRingAlpha;

    // ─── Center dot ───
    // (named centerDot — a local called "dot" would shadow the intrinsic)
    float centerDot = smoothstep(CenterDotOuter, CenterDotInner, dist);
    alpha += centerDot * CenterDotAlpha;
    color = lerp(color, GlyphColor2, centerDot * CenterDotColorMix);

    // ─── Vertex dots on pentagram points ───
    for (int v = 0; v < 5; v++)
    {
        float va = rot + slowRot + (float)v * TAU / 5.0 - PI / 2.0;
        float2 vp = float2(cos(va), sin(va)) * InnerRingR;
        float vdot = smoothstep(VertexDotOuter, VertexDotInner, length(uv - vp));
        alpha += vdot * VertexDotAlpha;
        color = lerp(color, GlyphColor2, vdot * VertexDotColorMix);
    }

    // ─── Pulse ───
    float pulse = PulseBase + PulseAmp * sin(Time * PulseSpeed);
    alpha *= pulse;

    // ─── Activation glow ───
    float actGlow = Activation * smoothstep(TrimRingR, 0.0, dist) * ActGlowAlpha;
    alpha += actGlow;
    // Activation brightens toward secondary color
    color = lerp(color, GlyphColor2, Activation * ActColorMix);

    // ─── Activation: energy lines radiating from pentagram vertices ───
    if (Activation > 0.1)
    {
        for (int e = 0; e < 5; e++)
        {
            float ea = rot + slowRot + (float)e * TAU / 5.0 - PI / 2.0;
            float2 ep = float2(cos(ea), sin(ea)) * InnerRingR;

            // Radial energy line from vertex outward
            float2 outDir = normalize(ep);
            float2 lineEnd = ep + outDir * EnergyLineLen;
            float ld = LineSDF(uv, ep, lineEnd);
            float energyLine = smoothstep(EnergyLineOuter, EnergyLineInner, ld) * Activation;

            // Animated shimmer along the line
            float shimmer = 0.5 + 0.5 * sin(Time * ShimmerFreq + (float)e * ShimmerPhase);
            alpha += energyLine * EnergyLineAlpha * shimmer;
            color = lerp(color, GlyphColor2, energyLine * EnergyLineColorMix);
        }
    }

    // ─── Edge fade ───
    alpha *= smoothstep(EdgeFadeEnd, EdgeFadeStart, dist);

    // ─── Dormant dimming (subtle) ───
    alpha *= lerp(DormantDim, 1.0, Activation);

    // ─── Apply intensity ───
    alpha *= Intensity;
    alpha = saturate(alpha);

    return float4(color * alpha, alpha);
}

technique MagicCircleTechnique
{
    pass Pass1
    {
        PixelShader = compile PS_SHADERMODEL PixelShaderFunction();
    }
}
