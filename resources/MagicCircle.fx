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

sampler2D TextureSampler : register(s0);

static const float PI = 3.14159265;
static const float TAU = 6.28318530;

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

struct VSOutput
{
    float4 Position : SV_POSITION;
    float4 Color : COLOR0;
    float2 TexCoord : TEXCOORD0;
};

float4 MainPS(VSOutput input) : COLOR0
{
    float2 uv = input.TexCoord * 2.0 - 1.0;
    float dist = length(uv);
    float angle = atan2(uv.y, uv.x);
    if (angle < 0.0) angle += TAU;

    float alpha = 0.0;
    float3 color = GlyphColor;

    float rot = Rotation;
    float slowRot = Time * 0.2;

    // ─── Outer circle (thick, glowing) ───
    float outer = Ring(dist, 0.90, 0.038);
    alpha += outer * 0.9;

    // ─── Outer decorative ring ───
    float outerThin = Ring(dist, 0.95, 0.018);
    alpha += outerThin * 0.5;

    // ─── Rune marks between outer rings ───
    for (int i = 0; i < 5; i++)
    {
        float markAngle = (float)i * TAU / 5.0 + slowRot;
        float2 markPos = float2(cos(markAngle), sin(markAngle)) * 0.925;
        float mark = RuneMark(uv, markPos, 0.06, markAngle + PI * 0.25);
        alpha += mark * 0.6;
        color = lerp(color, GlyphColor2, mark * 0.4);
    }

    // ─── Inner circle ───
    float inner = Ring(dist, 0.70, 0.027);
    alpha += inner * 0.8;

    // ─── Pentagram (connected to inner circle) ───
    float penta = Pentagram(uv, 0.70, rot + slowRot, 0.027);
    alpha += penta * 0.85;
    color = lerp(color, GlyphColor2, penta * 0.3);

    // ─── Small inner circle at center ───
    float centerRing = Ring(dist, 0.18, 0.018);
    alpha += centerRing * 0.7;

    // ─── Center dot ───
    float dot = smoothstep(0.09, 0.03, dist);
    alpha += dot * 0.8;
    color = lerp(color, GlyphColor2, dot * 0.5);

    // ─── Vertex dots on pentagram points ───
    for (int v = 0; v < 5; v++)
    {
        float va = rot + slowRot + (float)v * TAU / 5.0 - PI / 2.0;
        float2 vp = float2(cos(va), sin(va)) * 0.70;
        float vdot = smoothstep(0.06, 0.022, length(uv - vp));
        alpha += vdot * 0.7;
        color = lerp(color, GlyphColor2, vdot * 0.5);
    }

    // ─── Pulse ───
    float pulse = 0.75 + 0.25 * sin(Time * PulseSpeed);
    alpha *= pulse;

    // ─── Activation glow ───
    float actGlow = Activation * smoothstep(0.95, 0.0, dist) * 0.4;
    alpha += actGlow;
    // Activation brightens toward secondary color
    color = lerp(color, GlyphColor2, Activation * 0.4);

    // ─── Activation: energy lines radiating from pentagram vertices ───
    if (Activation > 0.1)
    {
        for (int e = 0; e < 5; e++)
        {
            float ea = rot + slowRot + (float)e * TAU / 5.0 - PI / 2.0;
            float2 ep = float2(cos(ea), sin(ea)) * 0.70;
            float2 toCenter = -normalize(ep);

            // Radial energy line from vertex outward
            float2 outDir = normalize(ep);
            float2 lineEnd = ep + outDir * 0.25;
            float ld = LineSDF(uv, ep, lineEnd);
            float energyLine = smoothstep(0.022, 0.005, ld) * Activation;

            // Animated shimmer along the line
            float shimmer = 0.5 + 0.5 * sin(Time * 8.0 + (float)e * 1.3);
            alpha += energyLine * 0.6 * shimmer;
            color = lerp(color, GlyphColor2, energyLine * 0.5);
        }
    }

    // ─── Edge fade ───
    alpha *= smoothstep(1.0, 0.92, dist);

    // ─── Dormant dimming ───
    alpha *= lerp(0.5, 1.0, Activation);

    // ─── Apply intensity ───
    alpha *= Intensity;
    alpha = saturate(alpha);

    return float4(color * alpha, alpha);
}

technique MagicCircleTechnique
{
    pass P0
    {
        PixelShader = compile PS_SHADERMODEL MainPS();
    }
};
