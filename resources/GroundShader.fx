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
float4 sampleGroundType(int typeIdx, float2 worldPos)
{
    float texScale = 0.125;
    float2 uvWarp = smoothNoise2(worldPos * UvWarpFreq) * UvWarpAmp;
    float2 uv = worldPos * texScale + uvWarp;

    // Subtle brightness variation
    float brightness = 0.9875 + 0.025 * valueNoise(worldPos * 0.37 + float2(13.7, 29.3));

    float4 color;
    if (typeIdx == 1)
        color = tex2D(GroundTex1, uv);
    else if (typeIdx == 2)
        color = tex2D(GroundTex2, uv);
    else
        color = tex2D(GroundTex0, uv);

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

    // Sample tilemap at 4 corners to get ground type indices
    // Color format: type index packed into R channel
    float type00f = tex2D(TilemapSampler, (bpos + float2(0.5, 0.5)) * invWorld).r;
    float type10f = tex2D(TilemapSampler, (bpos + float2(1.5, 0.5)) * invWorld).r;
    float type01f = tex2D(TilemapSampler, (bpos + float2(0.5, 1.5)) * invWorld).r;
    float type11f = tex2D(TilemapSampler, (bpos + float2(1.5, 1.5)) * invWorld).r;

    int type00 = (int)(type00f * 255.0 + 0.5);
    int type10 = (int)(type10f * 255.0 + 0.5);
    int type01 = (int)(type01f * 255.0 + 0.5);
    int type11 = (int)(type11f * 255.0 + 0.5);

    // Sample ground textures for all 4 corners
    float4 c00 = sampleGroundType(type00, worldPos);
    float4 c10 = sampleGroundType(type10, worldPos);
    float4 c01 = sampleGroundType(type01, worldPos);
    float4 c11 = sampleGroundType(type11, worldPos);

    // Blend between types using smoothstep
    float4 result;
    if (type00 == type10 && type00 == type01 && type00 == type11)
    {
        result = c00;
    }
    else
    {
        float4 top = lerp(c00, c10, s.x);
        float4 bot = lerp(c01, c11, s.x);
        result = lerp(top, bot, s.y);
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
