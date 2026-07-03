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
// Set every frame from C# (DrawGroundShader) — MGFX on OpenGL does not honor
// default uniform initializers, so no defaults are declared here.
float3 AmbientColor;

// Time in seconds since startup. Drives water scroll animation and the
// shore-foam pulse. Set every frame from C# (DrawGroundShader).
float Time;

// Per-ground-type config. Indexed by ground-type id 0..15.
// TintColors: multiplied into the bilerped per-pixel color in main (NOT
// inside sampleGroundType), so multiple ground types that share a texture
// slot (e.g. shallow_water and swamp_shallow_water both reference
// ShallowWater.png) can still display distinct tints. Applying tint after
// the cascade keeps the inlined sampleGroundType's temp-register footprint
// minimal, which is what fits PS_3_0's 32-register budget across the up-to-
// 8 inline call sites per pixel.
// IsWaterType: 1.0 if this type should participate in shore-foam, 0.0
// otherwise. Read only in the main shader's foam pass, not inside the
// inlined cascade.
float4 TintColors[16];
float IsWaterType[16];

// ─── Shore-foam tuning ───
// The foam band peaks just inside the water side of the shoreline (waterness ≈
// 0.75-0.95) and pulses gently; see the foam pass in main for the full why.
static const float  FoamBandInLo      = 0.02;  // band rises as nonWaterness passes this...
static const float  FoamBandInHi      = 0.18;  // ...peaking here (just inside the water)
static const float  FoamBandOutLo     = 0.30;  // band falls off toward land from here...
static const float  FoamBandOutHi     = 0.55;  // ...gone by here
static const float  FoamWaterGateLo   = 0.45;  // only paint over mostly-water pixels
static const float  FoamWaterGateHi   = 0.70;
static const float  FoamPulseNoiseScale = 0.6; // world-space scale of the phase noise
static const float  FoamPulseSpeed    = 0.6;   // pulse rate, rad/s
static const float  FoamPulseBase     = 0.78;  // brightness pulses Base ± Amp (surf, not flicker)
static const float  FoamPulseAmp      = 0.22;
static const float  FoamStrength      = 0.55;  // overall foam blend into the ground color
static const float3 FoamBaseColor     = float3(0.86, 0.92, 0.95); // near-white cyan, tinted per water type

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
    MipFilter = Linear;
    AddressU = Wrap;
    AddressV = Wrap;
};

texture GroundTexture1;
sampler2D GroundTex1 = sampler_state
{
    Texture = <GroundTexture1>;
    MinFilter = Linear;
    MagFilter = Linear;
    MipFilter = Linear;
    AddressU = Wrap;
    AddressV = Wrap;
};

texture GroundTexture2;
sampler2D GroundTex2 = sampler_state
{
    Texture = <GroundTexture2>;
    MinFilter = Linear;
    MagFilter = Linear;
    MipFilter = Linear;
    AddressU = Wrap;
    AddressV = Wrap;
};

texture GroundTexture3;
sampler2D GroundTex3 = sampler_state
{
    Texture = <GroundTexture3>;
    MinFilter = Linear;
    MagFilter = Linear;
    MipFilter = Linear;
    AddressU = Wrap;
    AddressV = Wrap;
};

texture GroundTexture4;
sampler2D GroundTex4 = sampler_state
{
    Texture = <GroundTexture4>;
    MinFilter = Linear;
    MagFilter = Linear;
    MipFilter = Linear;
    AddressU = Wrap;
    AddressV = Wrap;
};

texture GroundTexture5;
sampler2D GroundTex5 = sampler_state
{
    Texture = <GroundTexture5>;
    MinFilter = Linear;
    MagFilter = Linear;
    MipFilter = Linear;
    AddressU = Wrap;
    AddressV = Wrap;
};

texture GroundTexture6;
sampler2D GroundTex6 = sampler_state
{
    Texture = <GroundTexture6>;
    MinFilter = Linear;
    MagFilter = Linear;
    MipFilter = Linear;
    AddressU = Wrap;
    AddressV = Wrap;
};

texture GroundTexture7;
sampler2D GroundTex7 = sampler_state
{
    Texture = <GroundTexture7>;
    MinFilter = Linear;
    MagFilter = Linear;
    MipFilter = Linear;
    AddressU = Wrap;
    AddressV = Wrap;
};

// --- Noise functions ---
// Duplicated in DissolveTree.fx — keep in sync (no #include; each .fx builds standalone).
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

// --- Sample ground texture by packed (slot, type) byte ---
// Decodes the tilemap byte into texture-slot (0..7) and ground-type id
// (0..31), runs the texture cascade by slot, and applies the per-type tint /
// water-scroll. Keeping the decoding + tint/iswater lookups inside the
// function (rather than hoisting them to main) means every inlined call
// site's locals are scope-local and the compiler can reuse the same temp
// registers across the 4..8 call sites — which keeps PS_3_0 temp-register
// pressure inside its 32-register budget. Function is inlined up to 8x per
// pixel: 4 corners × 2 for the corruption fade lerp.
float4 sampleGroundType(int packedByte, float2 worldPos)
{
    int texSlot = packedByte / 32;

    float texScale = 0.125;
    float2 uvWarp = smoothNoise2(worldPos * UvWarpFreq) * UvWarpAmp;
    float2 baseUv = worldPos * texScale + uvWarp;

    // Subtle brightness variation
    float brightness = 0.9875 + 0.025 * valueNoise(worldPos * 0.37 + float2(13.7, 29.3));

    // First (and only) tap. The pre-bit-pack version of this shader did a
    // second tex2D at a Time-scrolled UV for water types and averaged the
    // two to hide tile repetition — but adding the bit-pack decoding +
    // larger ground-type tilemap encoding pushed the second cascade over
    // PS_3_0's temp-register budget. Tile repetition for water is now
    // hidden by the typeWarp displacement applied during sampling plus the
    // shore-foam pulse in the main shader.
    float4 color;
    if (texSlot == 1)       color = tex2D(GroundTex1, baseUv);
    else if (texSlot == 2)  color = tex2D(GroundTex2, baseUv);
    else if (texSlot == 3)  color = tex2D(GroundTex3, baseUv);
    else if (texSlot == 4)  color = tex2D(GroundTex4, baseUv);
    else if (texSlot == 5)  color = tex2D(GroundTex5, baseUv);
    else if (texSlot == 6)  color = tex2D(GroundTex6, baseUv);
    else if (texSlot == 7)  color = tex2D(GroundTex7, baseUv);
    else                    color = tex2D(GroundTex0, baseUv);

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

    // Tilemap R/G bytes encode (slot << 5) | type — sampleGroundType decodes
    // both fields internally so per-call temps stay scope-local. We still
    // pull the type out here so the shore-foam pass below can read
    // IsWaterType[type??] without re-decoding (foam-band only needs the
    // *current* type's water flag, not the original).
    int  packCur00 = (int)(m00.r * 255.0 + 0.5);
    int  packCur10 = (int)(m10.r * 255.0 + 0.5);
    int  packCur01 = (int)(m01.r * 255.0 + 0.5);
    int  packCur11 = (int)(m11.r * 255.0 + 0.5);
    int  packOrig00 = (int)(m00.g * 255.0 + 0.5);
    int  packOrig10 = (int)(m10.g * 255.0 + 0.5);
    int  packOrig01 = (int)(m01.g * 255.0 + 0.5);
    int  packOrig11 = (int)(m11.g * 255.0 + 0.5);
    // The packed byte carries a 5-bit type field (0..31) but TintColors /
    // IsWaterType are sized 16 (matching MaxGroundTypes on the C# side) —
    // clamp so a future 17th+ ground type can't index past the arrays.
    int  type00  = min(packCur00 - (packCur00 / 32) * 32, 15);
    int  type10  = min(packCur10 - (packCur10 / 32) * 32, 15);
    int  type01  = min(packCur01 - (packCur01 / 32) * 32, 15);
    int  type11  = min(packCur11 - (packCur11 / 32) * 32, 15);
    int  typeO00 = min(packOrig00 - (packOrig00 / 32) * 32, 15);
    int  typeO10 = min(packOrig10 - (packOrig10 / 32) * 32, 15);
    int  typeO01 = min(packOrig01 - (packOrig01 / 32) * 32, 15);
    int  typeO11 = min(packOrig11 - (packOrig11 / 32) * 32, 15);
    float fade00 = m00.b;
    float fade10 = m10.b;
    float fade01 = m01.b;
    float fade11 = m11.b;

    // Per-corner fade between original and current type. When fade == 1 the
    // current type wins, so stable vertices (B = 255) skip the second sample.
    float4 c00 = sampleGroundType(packCur00, worldPos);
    float4 c10 = sampleGroundType(packCur10, worldPos);
    float4 c01 = sampleGroundType(packCur01, worldPos);
    float4 c11 = sampleGroundType(packCur11, worldPos);
    if (fade00 < 0.999) c00 = lerp(sampleGroundType(packOrig00, worldPos), c00, fade00);
    if (fade10 < 0.999) c10 = lerp(sampleGroundType(packOrig10, worldPos), c10, fade10);
    if (fade01 < 0.999) c01 = lerp(sampleGroundType(packOrig01, worldPos), c01, fade01);
    if (fade11 < 0.999) c11 = lerp(sampleGroundType(packOrig11, worldPos), c11, fade11);

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

    // Apply per-ground-type tint after the bilerp. Per-corner tints are
    // bilerped the same way as the textures so transitions between two
    // types with different tints (e.g. shallow_water → swamp_shallow_water)
    // fade smoothly. Doing the tint here instead of inside sampleGroundType
    // means the inlined cascade doesn't carry a dynamic-indexed uniform
    // read — which is what keeps PS_3_0 temp-register pressure inside its
    // 32-register budget when sampleGroundType is inlined 4..8x per pixel.
    // Each corner's tint is also lerped original→current by its fade — for
    // type pairs that share a texture slot and differ only by tint, the
    // texture lerp is identity, so without this the tint (the whole visible
    // difference) would pop instead of fade.
    float3 tint00 = lerp(TintColors[typeO00].rgb, TintColors[type00].rgb, fade00);
    float3 tint10 = lerp(TintColors[typeO10].rgb, TintColors[type10].rgb, fade10);
    float3 tint01 = lerp(TintColors[typeO01].rgb, TintColors[type01].rgb, fade01);
    float3 tint11 = lerp(TintColors[typeO11].rgb, TintColors[type11].rgb, fade11);
    float3 tintTop = lerp(tint00, tint10, s.x);
    float3 tintBot = lerp(tint01, tint11, s.x);
    result.rgb *= lerp(tintTop, tintBot, s.y);

    // --- Shore foam ---
    // Gate the whole block on "any corner is water" so warps that are entirely
    // grass/dirt/etc skip the smoothsteps + sin. The branch is uniform across
    // large screen regions, so most non-shoreline pixels pay nothing here.
    // Water flags fade original→current alongside the tint so shore foam
    // ramps out during a water→land corruption fade instead of vanishing
    // the instant the vertex's current type flips.
    float iw00 = lerp(IsWaterType[typeO00], IsWaterType[type00], fade00);
    float iw10 = lerp(IsWaterType[typeO10], IsWaterType[type10], fade10);
    float iw01 = lerp(IsWaterType[typeO01], IsWaterType[type01], fade01);
    float iw11 = lerp(IsWaterType[typeO11], IsWaterType[type11], fade11);
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
        float foamBand = smoothstep(FoamBandInLo, FoamBandInHi, nonWaterness)
                       * (1.0 - smoothstep(FoamBandOutLo, FoamBandOutHi, nonWaterness));
        foamBand *= smoothstep(FoamWaterGateLo, FoamWaterGateHi, waterness);

        // Gentle pulse along the shoreline. Earlier version used a single
        // sin(time + worldPos.x + worldPos.y) which moves as one diagonal
        // wave — across a circular pond it reads as rotation. Here each
        // pixel takes its phase from a 2D noise sample of its position, so
        // neighbouring pixels brighten/dim at slightly different times and
        // there's no coherent sweep direction. Amplitude is halved from
        // the previous (0.55 ± 0.45 → 0.78 ± 0.22) so bright/dim contrast
        // reads as surf rather than flickering.
        float pulsePhase = valueNoise(worldPos * FoamPulseNoiseScale) * 6.28318;
        float foamPulse = FoamPulseBase + FoamPulseAmp * sin(Time * FoamPulseSpeed + pulsePhase);
        float foam = foamBand * foamPulse * FoamStrength;

        // Modulate foam color by the water vertices' tint so tinted water
        // variants (e.g. swamp_shallow_water with a green tint) get a foam
        // that reads as their own colour rather than the bright cyan-white
        // used for unmodified shallow water. Weighted by (iw_n * w_n) so
        // only water corners contribute — grass corners don't pollute the
        // foam tint. Normalising by `waterness` keeps the foam at full
        // brightness in pure-water cells, falling off naturally near land.
        float3 waterTint = (tint00 * (iw00 * w00)
                          + tint10 * (iw10 * w10)
                          + tint01 * (iw01 * w01)
                          + tint11 * (iw11 * w11)) / max(waterness, 0.001);
        float3 foamColor = FoamBaseColor * waterTint;
        result.rgb = lerp(result.rgb, foamColor, foam);
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
