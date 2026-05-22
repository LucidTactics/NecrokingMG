#if OPENGL
#define SV_POSITION POSITION
#define VS_SHADERMODEL vs_3_0
#define PS_SHADERMODEL ps_3_0
#else
#define VS_SHADERMODEL vs_4_0
#define PS_SHADERMODEL ps_4_0
#endif

// Camera/world uniforms
float2 CameraPos;
float Zoom;
float YRatio;
float2 ScreenSize;
float2 WorldSize;   // vertex grid size (worldW+1, worldH+1)
float TypeWarpStrength;
float UvWarpAmp;
float UvWarpFreq;
float3 AmbientColor = float3(1, 1, 1);

// Time in seconds since startup. Drives water scroll animation and the
// shore-foam pulse. Set every frame from C# (DrawGroundShader).
float Time;

// Per-type config. Index = ground-type index (0..7). Set from C# each frame.
// TintColors: multiplied into the sampled texture so palette can be tuned
// from JSON without re-exporting PNGs. Defaults to (1,1,1,1) for non-tinted
// types.
// IsWaterType: 1.0 if this type should animate as water (dual-scroll +
// participate in shore-foam), 0.0 otherwise.
float4 TintColors[8];
float IsWaterType[8];

// Hardcoded water-scroll velocities in world-UV units per second. Two layers
// in different directions so they interfere and hide tiling. Tuned for slow
// "pond" motion; bump up for streams/rivers later.
static const float2 WaterScrollA = float2( 0.045,  0.028);
static const float2 WaterScrollB = float2(-0.032,  0.054);

// Textures as named parameters (bound via Effect.Parameters in C#)
// s0 is used by SpriteBatch for the drawn texture (tilemap/vertex map)
sampler2D TilemapSampler : register(s0);

// Ground type textures - declared as Texture2D + sampler for reliable binding
texture GroundTexture0;
sampler2D GroundTex0 = sampler_state
{
    Texture = <GroundTexture0>;
    MinFilter = Linear;
    MagFilter = Linear;
    AddressU = Wrap;
    AddressV = Wrap;
};

texture GroundTexture1;
sampler2D GroundTex1 = sampler_state
{
    Texture = <GroundTexture1>;
    MinFilter = Linear;
    MagFilter = Linear;
    AddressU = Wrap;
    AddressV = Wrap;
};

texture GroundTexture2;
sampler2D GroundTex2 = sampler_state
{
    Texture = <GroundTexture2>;
    MinFilter = Linear;
    MagFilter = Linear;
    AddressU = Wrap;
    AddressV = Wrap;
};

texture GroundTexture3;
sampler2D GroundTex3 = sampler_state
{
    Texture = <GroundTexture3>;
    MinFilter = Linear;
    MagFilter = Linear;
    AddressU = Wrap;
    AddressV = Wrap;
};

texture GroundTexture4;
sampler2D GroundTex4 = sampler_state
{
    Texture = <GroundTexture4>;
    MinFilter = Linear;
    MagFilter = Linear;
    AddressU = Wrap;
    AddressV = Wrap;
};

texture GroundTexture5;
sampler2D GroundTex5 = sampler_state
{
    Texture = <GroundTexture5>;
    MinFilter = Linear;
    MagFilter = Linear;
    AddressU = Wrap;
    AddressV = Wrap;
};

texture GroundTexture6;
sampler2D GroundTex6 = sampler_state
{
    Texture = <GroundTexture6>;
    MinFilter = Linear;
    MagFilter = Linear;
    AddressU = Wrap;
    AddressV = Wrap;
};

texture GroundTexture7;
sampler2D GroundTex7 = sampler_state
{
    Texture = <GroundTexture7>;
    MinFilter = Linear;
    MagFilter = Linear;
    AddressU = Wrap;
    AddressV = Wrap;
};

// --- Noise functions ---
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

float2 smoothNoise2(float2 p)
{
    return float2(
        valueNoise(p),
        valueNoise(p + float2(47.3, 91.7))
    );
}

// --- Sample ground texture by type index ---
// Non-water types take a single tap at baseUv (same cost as the pre-water
// shader). Water types take a second tap at a scrolled offset and average
// the two — the static layer and the scrolling layer drift relative to each
// other, which hides the texture's tiling. Each branch contains a single
// tex2D cascade, keeping temp-register pressure inside the ps_3_0 budget.
// The branch is uniform across most warps (typeIdx is coherent over screen
// regions), so only water pixels pay the extra-sample cost.
float4 sampleGroundType(int typeIdx, float2 worldPos)
{
    float texScale = 0.125;
    float2 uvWarp = smoothNoise2(worldPos * UvWarpFreq) * UvWarpAmp;
    float2 baseUv = worldPos * texScale + uvWarp;

    // Subtle brightness variation
    float brightness = 0.9875 + 0.025 * valueNoise(worldPos * 0.37 + float2(13.7, 29.3));

    // First (and for non-water, only) tap.
    float4 colorA;
    if (typeIdx == 1)      colorA = tex2D(GroundTex1, baseUv);
    else if (typeIdx == 2) colorA = tex2D(GroundTex2, baseUv);
    else if (typeIdx == 3) colorA = tex2D(GroundTex3, baseUv);
    else if (typeIdx == 4) colorA = tex2D(GroundTex4, baseUv);
    else if (typeIdx == 5) colorA = tex2D(GroundTex5, baseUv);
    else if (typeIdx == 6) colorA = tex2D(GroundTex6, baseUv);
    else if (typeIdx == 7) colorA = tex2D(GroundTex7, baseUv);
    else                   colorA = tex2D(GroundTex0, baseUv);

    float4 color = colorA;

    // Water: one extra tap at a scrolled UV. The static colorA + scrolling
    // colorB interfere as they drift apart, hiding the tiling. No [branch]
    // hint because tex2D inside needs pixel-quad derivatives; the compiler
    // picks predication vs dynamic flow.
    if (IsWaterType[typeIdx] > 0.5)
    {
        float2 uvScroll = baseUv + WaterScrollA * Time;

        float4 colorB;
        if (typeIdx == 1)      colorB = tex2D(GroundTex1, uvScroll);
        else if (typeIdx == 2) colorB = tex2D(GroundTex2, uvScroll);
        else if (typeIdx == 3) colorB = tex2D(GroundTex3, uvScroll);
        else if (typeIdx == 4) colorB = tex2D(GroundTex4, uvScroll);
        else if (typeIdx == 5) colorB = tex2D(GroundTex5, uvScroll);
        else if (typeIdx == 6) colorB = tex2D(GroundTex6, uvScroll);
        else if (typeIdx == 7) colorB = tex2D(GroundTex7, uvScroll);
        else                   colorB = tex2D(GroundTex0, uvScroll);

        color = (colorA + colorB) * 0.5;
    }

    color.rgb *= TintColors[typeIdx].rgb;
    color.rgb *= brightness;
    return color;
}

// --- Pixel shader (uses SpriteBatch's built-in vertex shader for vertex transform) ---
float4 PixelShaderFunction(float2 texCoord : TEXCOORD0) : COLOR0
{
    float2 screenUV = texCoord;
    float2 screenPos = screenUV * ScreenSize;

    // Screen to world coordinate conversion
    // Snap screen position to integer pixel to eliminate subpixel shimmer
    float2 snappedScreen = floor(screenPos) + 0.5;
    float worldX = (snappedScreen.x - ScreenSize.x * 0.5) / Zoom + CameraPos.x;
    float worldY = (snappedScreen.y - ScreenSize.y * 0.5) / (Zoom * YRatio) + CameraPos.y;
    float2 worldPos = float2(worldX, worldY);

    // Type boundary warping with noise
    float2 typeWarp = (smoothNoise2(worldPos * 0.4 + float2(5.3, 11.7)) - 0.5) * TypeWarpStrength;
    float2 warpedPos = worldPos + typeWarp;

    float2 bpos = floor(warpedPos);
    float2 t = warpedPos - bpos;
    float2 s = smoothstep(0.0, 1.0, t);

    float2 invWorld = 1.0 / WorldSize;

    // Sample tilemap at 4 corners. Channel layout written by GroundSystem:
    //   R = current type, G = original type, B = fade progress (255 = stable),
    // so for fading vertices we lerp between the original and current type.
    float4 m00 = tex2D(TilemapSampler, (bpos + float2(0.5, 0.5)) * invWorld);
    float4 m10 = tex2D(TilemapSampler, (bpos + float2(1.5, 0.5)) * invWorld);
    float4 m01 = tex2D(TilemapSampler, (bpos + float2(0.5, 1.5)) * invWorld);
    float4 m11 = tex2D(TilemapSampler, (bpos + float2(1.5, 1.5)) * invWorld);

    int  type00  = (int)(m00.r * 255.0 + 0.5);
    int  type10  = (int)(m10.r * 255.0 + 0.5);
    int  type01  = (int)(m01.r * 255.0 + 0.5);
    int  type11  = (int)(m11.r * 255.0 + 0.5);
    int  orig00  = (int)(m00.g * 255.0 + 0.5);
    int  orig10  = (int)(m10.g * 255.0 + 0.5);
    int  orig01  = (int)(m01.g * 255.0 + 0.5);
    int  orig11  = (int)(m11.g * 255.0 + 0.5);
    float fade00 = m00.b;
    float fade10 = m10.b;
    float fade01 = m01.b;
    float fade11 = m11.b;

    // Per-corner fade between original and current type. When fade == 1 the
    // current type wins, so stable vertices (B = 255) skip the second sample.
    float4 c00 = sampleGroundType(type00, worldPos);
    float4 c10 = sampleGroundType(type10, worldPos);
    float4 c01 = sampleGroundType(type01, worldPos);
    float4 c11 = sampleGroundType(type11, worldPos);
    if (fade00 < 0.999) c00 = lerp(sampleGroundType(orig00, worldPos), c00, fade00);
    if (fade10 < 0.999) c10 = lerp(sampleGroundType(orig10, worldPos), c10, fade10);
    if (fade01 < 0.999) c01 = lerp(sampleGroundType(orig01, worldPos), c01, fade01);
    if (fade11 < 0.999) c11 = lerp(sampleGroundType(orig11, worldPos), c11, fade11);

    // Blend between types using smoothstep across the cell.
    float4 result;
    if (type00 == type10 && type00 == type01 && type00 == type11
        && fade00 > 0.999 && fade10 > 0.999 && fade01 > 0.999 && fade11 > 0.999)
    {
        result = c00;
    }
    else
    {
        float4 top = lerp(c00, c10, s.x);
        float4 bot = lerp(c01, c11, s.x);
        result = lerp(top, bot, s.y);
    }

    // --- Shore foam ---
    // Gate the whole block on "any corner is water" so warps that are entirely
    // grass/dirt/etc skip the smoothsteps + sin. The branch is uniform across
    // large screen regions, so most non-shoreline pixels pay nothing here.
    float iw00 = IsWaterType[type00];
    float iw10 = IsWaterType[type10];
    float iw01 = IsWaterType[type01];
    float iw11 = IsWaterType[type11];
    float anyWater = max(max(iw00, iw10), max(iw01, iw11));
    if (anyWater > 0.0)
    {
        // Bilerp the 4 corners' is-water flags. waterness is 1 in deep water,
        // 0 on land, varies smoothly across the shoreline.
        float w00 = (1.0 - s.x) * (1.0 - s.y);
        float w10 =         s.x * (1.0 - s.y);
        float w01 = (1.0 - s.x) *         s.y;
        float w11 =         s.x *         s.y;
        float waterness    = iw00 * w00 + iw10 * w10 + iw01 * w01 + iw11 * w11;
        float nonWaterness = 1.0 - waterness;

        // Peak around nonWaterness ~ 0.10–0.25 — i.e. just inside the water side.
        // Gate by waterness so the band only paints over water samples.
        float foamBand = smoothstep(0.02, 0.18, nonWaterness)
                       * (1.0 - smoothstep(0.30, 0.55, nonWaterness));
        foamBand *= smoothstep(0.45, 0.70, waterness);

        // Gentle pulse along the shoreline. Earlier version used a single
        // sin(time + worldPos.x + worldPos.y) which moves as one diagonal
        // wave — across a circular pond it reads as rotation. Here each
        // pixel takes its phase from a 2D noise sample of its position, so
        // neighbouring pixels brighten/dim at slightly different times and
        // there's no coherent sweep direction. Amplitude is halved from
        // the previous (0.55 ± 0.45 → 0.78 ± 0.22) so bright/dim contrast
        // reads as surf rather than flickering.
        float pulsePhase = valueNoise(worldPos * 0.6) * 6.28318;
        float foamPulse = 0.78 + 0.22 * sin(Time * 0.6 + pulsePhase);
        float foam = foamBand * foamPulse * 0.55;

        result.rgb = lerp(result.rgb, float3(0.86, 0.92, 0.95), foam);
    }

    result.rgb *= AmbientColor;
    result.a = 1.0;
    return result;
}

technique GroundShader
{
    pass Pass1
    {
        PixelShader = compile PS_SHADERMODEL PixelShaderFunction();
    }
}
