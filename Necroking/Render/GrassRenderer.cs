using System;
using System.Runtime.InteropServices;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Necroking.Core;
using Necroking.Data.Registries;

namespace Necroking.Render;

/// <summary>
/// Renders grass blades using CPU-generated triangle strips drawn via DrawUserPrimitives.
/// Each blade is a tapered quad (2 triangles, 6 vertices) with base-to-tip color gradient
/// and wind animation. Matches the C++ instanced grass system visually.
/// </summary>
public class GrassRenderer
{
    // --- Constants matching C++ ---
    private const float MAX_BLADE_HEIGHT = 1.2f;
    private const float MIN_BLADE_HEIGHT = 0.15f;
    private const float MIN_ZOOM_FOR_GRASS = 5.0f;
    private const int MAX_VERTICES = 600000; // 100k blades * 6 verts

    // Vertex type: position + color
    [StructLayout(LayoutKind.Sequential)]
    private struct GrassVertex : IVertexType
    {
        public Vector3 Position;
        public Color Color;

        public static readonly VertexDeclaration Declaration = new(
            new VertexElement(0, VertexElementFormat.Vector3, VertexElementUsage.Position, 0),
            new VertexElement(12, VertexElementFormat.Color, VertexElementUsage.Color, 0)
        );

        VertexDeclaration IVertexType.VertexDeclaration => Declaration;
    }

    private GrassVertex[] _vertices = new GrassVertex[MAX_VERTICES];
    private int _vertexCount;
    private BasicEffect? _effect;

    public void Init(GraphicsDevice device)
    {
        _effect = new BasicEffect(device)
        {
            VertexColorEnabled = true,
            TextureEnabled = false,
            LightingEnabled = false,
            FogEnabled = false,
            World = Matrix.Identity,
            View = Matrix.Identity,
        };
    }

    /// <summary>
    /// Renders grass blades for all visible cells.
    /// Call between SpriteBatch.End() and SpriteBatch.Begin() (it manages its own draw state).
    /// </summary>
    public void Draw(
        GraphicsDevice device,
        SpriteBatch spriteBatch,
        Camera25D camera,
        int screenW, int screenH,
        byte[] grassMap, int grassW, int grassH,
        Color[] baseColors, Color[] tipColors,
        GrassSettings settings,
        float gameTime)
    {
        if (grassMap.Length == 0 || grassW == 0 || baseColors.Length == 0) return;
        if (camera.Zoom < MIN_ZOOM_FOR_GRASS) return;
        if (_effect == null) return;

        float cellSize = settings.CellSize;
        if (cellSize <= 0f) cellSize = 0.8f;

        // Generate blade vertices
        _vertexCount = 0;
        GenerateBlades(camera, screenW, screenH,
            grassMap, grassW, grassH,
            baseColors, tipColors,
            settings, cellSize, gameTime);

        if (_vertexCount < 3) return;

        // End current SpriteBatch so we can draw raw primitives
        spriteBatch.End();

        // Set up orthographic projection matching screen coordinates
        _effect.Projection = Matrix.CreateOrthographicOffCenter(
            0, screenW, screenH, 0, -1f, 1f);

        // Draw all blade triangles in one call
        device.BlendState = BlendState.AlphaBlend;
        device.DepthStencilState = DepthStencilState.None;
        device.RasterizerState = RasterizerState.CullNone;
        device.SamplerStates[0] = SamplerState.LinearClamp;

        foreach (var pass in _effect.CurrentTechnique.Passes)
        {
            pass.Apply();
            device.DrawUserPrimitives(
                PrimitiveType.TriangleList,
                _vertices, 0, _vertexCount / 3);
        }

        // Resume SpriteBatch
        spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.LinearClamp);
    }

    private void GenerateBlades(
        Camera25D camera,
        int screenW, int screenH,
        byte[] grassMap, int grassW, int grassH,
        Color[] baseColors, Color[] tipColors,
        GrassSettings settings,
        float cellSize, float gameTime)
    {
        float zoom = camera.Zoom;
        float yRatio = camera.YRatio;

        // View frustum in world space (with padding)
        float viewLeft = camera.Position.X - screenW / (2f * zoom) - cellSize * 2;
        float viewRight = camera.Position.X + screenW / (2f * zoom) + cellSize * 2;
        float viewTop = camera.Position.Y - screenH / (2f * zoom * yRatio) - cellSize * 2;
        float viewBottom = camera.Position.Y + screenH / (2f * zoom * yRatio) + cellSize * 2;

        // Grid cell range
        int cx0 = Math.Max(0, (int)MathF.Floor(viewLeft / cellSize));
        int cy0 = Math.Max(0, (int)MathF.Floor(viewTop / cellSize));
        int cx1 = Math.Min(grassW - 1, (int)MathF.Ceiling(viewRight / cellSize));
        int cy1 = Math.Min(grassH - 1, (int)MathF.Ceiling(viewBottom / cellSize));

        // LOD: reduce blade count at lower zoom
        float lodFactor = MathHelper.Clamp((zoom - MIN_ZOOM_FOR_GRASS) / 25f, 0.08f, 1f);

        float density01 = MathHelper.Clamp(settings.Density / 255f, 0f, 1f);
        float height01 = MathHelper.Clamp(settings.Height / 255f, 0f, 1f);
        int baseBladesPerCell = Math.Clamp(settings.BladesPerCell, 1, 30);
        int numBlades = Math.Max(1, (int)(baseBladesPerCell * density01 * lodFactor));

        float windSpeed = settings.WindSpeed;
        float windStrength = settings.WindStrength;

        // Edge blend zone for neighbor type mixing
        const float BLEND_ZONE = 0.5f;

        for (int cy = cy0; cy <= cy1; cy++)
        {
            for (int cx = cx0; cx <= cx1; cx++)
            {
                int idx = cy * grassW + cx;
                if (idx < 0 || idx >= grassMap.Length) continue;

                byte grassType = grassMap[idx];
                if (grassType == 0) continue; // 0 = no grass (stored as typeIndex + 1)

                int typeIdx = grassType - 1; // Convert back to 0-based type index
                if (typeIdx < 0 || typeIdx >= baseColors.Length) continue;

                Color cellBaseColor = baseColors[typeIdx];
                Color cellTipColor = tipColors[typeIdx];

                float cellWorldX = cx * cellSize;
                float cellWorldY = cy * cellSize;

                // Check neighbors for type blending
                int nLeftType = GetNeighborType(grassMap, grassW, grassH, cx - 1, cy, typeIdx, baseColors.Length);
                int nRightType = GetNeighborType(grassMap, grassW, grassH, cx + 1, cy, typeIdx, baseColors.Length);
                int nUpType = GetNeighborType(grassMap, grassW, grassH, cx, cy - 1, typeIdx, baseColors.Length);
                int nDownType = GetNeighborType(grassMap, grassW, grassH, cx, cy + 1, typeIdx, baseColors.Length);
                bool hasNeighborDiff = nLeftType >= 0 || nRightType >= 0 || nUpType >= 0 || nDownType >= 0;

                for (int b = 0; b < numBlades; b++)
                {
                    if (_vertexCount + 6 > MAX_VERTICES) return;

                    // Deterministic hash for blade placement (matches C++)
                    uint h = TileHash(cx, cy, b);
                    float fx = HashToFloat(h);
                    float fy = HashToFloat(h * 2654435761u);
                    float fh = HashToFloat(h * 340573321u);
                    float fs = HashToFloat(h * 1103515245u);

                    // Determine blade type via neighbor blending
                    Color useBase = cellBaseColor;
                    Color useTip = cellTipColor;

                    if (hasNeighborDiff)
                    {
                        float bestProb = 0f;
                        int bestNeighborType = -1;

                        if (nLeftType >= 0)
                        {
                            float p = EdgeProb(fx, BLEND_ZONE);
                            if (p > bestProb) { bestProb = p; bestNeighborType = nLeftType; }
                        }
                        if (nRightType >= 0)
                        {
                            float p = EdgeProb(1f - fx, BLEND_ZONE);
                            if (p > bestProb) { bestProb = p; bestNeighborType = nRightType; }
                        }
                        if (nUpType >= 0)
                        {
                            float p = EdgeProb(fy, BLEND_ZONE);
                            if (p > bestProb) { bestProb = p; bestNeighborType = nUpType; }
                        }
                        if (nDownType >= 0)
                        {
                            float p = EdgeProb(1f - fy, BLEND_ZONE);
                            if (p > bestProb) { bestProb = p; bestNeighborType = nDownType; }
                        }

                        float roll = HashToFloat(h * 7919u);
                        if (roll < bestProb && bestNeighborType >= 0)
                        {
                            useBase = baseColors[bestNeighborType];
                            useTip = tipColors[bestNeighborType];
                        }
                    }

                    // Per-blade hue variation (matches C++ vertex shader)
                    float hueVar = QuickHash(fs * 3.17f);
                    float varMul = 0.8f + hueVar * 0.4f;

                    Color bladeBase = new(
                        (byte)Math.Clamp((int)(useBase.R * varMul), 0, 255),
                        (byte)Math.Clamp((int)(useBase.G * varMul), 0, 255),
                        (byte)Math.Clamp((int)(useBase.B * varMul), 0, 255),
                        (byte)255
                    );
                    Color bladeTip = new(
                        (byte)Math.Clamp((int)(useTip.R * varMul), 0, 255),
                        (byte)Math.Clamp((int)(useTip.G * varMul), 0, 255),
                        (byte)Math.Clamp((int)(useTip.B * varMul), 0, 255),
                        (byte)220 // slight transparency at tip
                    );

                    // Blade world position
                    float wx = cellWorldX + fx * cellSize;
                    float wy = cellWorldY + fy * cellSize;

                    // Blade height in world units
                    float bladeHeight = MIN_BLADE_HEIGHT + (MAX_BLADE_HEIGHT - MIN_BLADE_HEIGHT)
                        * height01 * (0.5f + 0.5f * fh);

                    // Wind sway (matches C++ vertex shader)
                    float windPhase = gameTime * 1.8f * windSpeed + wx * 0.9f + wy * 0.7f;
                    float sway1 = MathF.Sin(windPhase + fs * MathF.PI * 2f) * 2.5f * windStrength;
                    float sway2 = MathF.Sin(windPhase * 0.6f + 2.1f + fs * MathF.PI) * 1.0f * windStrength;
                    float totalSway = sway1 + sway2; // in pixels

                    // Blade width in pixels
                    float bladeW = (0.12f + 0.08f * QuickHash(fs * 7.31f)) * zoom;
                    if (settings.MinBladeWidth && bladeW < 1.5f) bladeW = 1.5f;
                    float halfW = bladeW * 0.5f;

                    // Blade height in pixels
                    float bladeH = bladeHeight * zoom;

                    // Screen-space base position
                    float baseScreenX = (wx - camera.Position.X) * zoom + screenW * 0.5f;
                    float baseScreenY = (wy - camera.Position.Y) * zoom * yRatio + screenH * 0.5f;

                    // Midpoint color (lerp between base and tip)
                    Color bladeMid = Color.Lerp(bladeBase, bladeTip, 0.5f);

                    // Build a tapered blade: base (wide), mid (medium), tip (narrow point)
                    // The blade is 2 quads = 4 triangles = 12 vertices, but we simplify to
                    // 2 triangles forming a tapered shape:
                    //
                    //       tip (narrow, sway applied)
                    //      / \
                    //     /   \
                    //    /     \
                    //   base-L  base-R (wider)

                    // Base vertices (bottom of blade, wide)
                    float bx = baseScreenX;
                    float by = baseScreenY;
                    float bxL = bx - halfW;
                    float bxR = bx + halfW;

                    // Tip vertex (top of blade, narrow, with sway)
                    float tx = baseScreenX + totalSway;
                    float ty = baseScreenY - bladeH;

                    // Triangle 1: left-base, right-base, tip
                    AddVertex(bxL, by, bladeBase);
                    AddVertex(bxR, by, bladeBase);
                    AddVertex(tx, ty, bladeTip);

                    // For wider blades, add a second pair to create a quad shape
                    // Mid point for 4-triangle blade at 40% height
                    if (bladeW > 2.0f)
                    {
                        float midY = baseScreenY - bladeH * 0.4f;
                        float midSway = totalSway * 0.4f;
                        float midHalfW = halfW * 0.65f;
                        float mxL = baseScreenX + midSway - midHalfW;
                        float mxR = baseScreenX + midSway + midHalfW;

                        // Overwrite the simple triangle with a more detailed blade:
                        // Rewind and draw 4 triangles instead
                        _vertexCount -= 3; // undo the simple triangle

                        if (_vertexCount + 12 > MAX_VERTICES) return;

                        // Lower quad: base to mid (2 triangles)
                        AddVertex(bxL, by, bladeBase);
                        AddVertex(bxR, by, bladeBase);
                        AddVertex(mxL, midY, bladeMid);

                        AddVertex(bxR, by, bladeBase);
                        AddVertex(mxR, midY, bladeMid);
                        AddVertex(mxL, midY, bladeMid);

                        // Upper quad: mid to tip (2 triangles forming a triangle at top)
                        AddVertex(mxL, midY, bladeMid);
                        AddVertex(mxR, midY, bladeMid);
                        AddVertex(tx, ty, bladeTip);
                    }
                }
            }
        }
    }

    private void AddVertex(float x, float y, Color color)
    {
        _vertices[_vertexCount++] = new GrassVertex
        {
            Position = new Vector3(x, y, 0f),
            Color = color
        };
    }

    /// <summary>Returns neighbor type index (0-based), or -1 if same/none/invalid.</summary>
    private static int GetNeighborType(byte[] map, int w, int h, int cx, int cy, int currentType, int typeCount)
    {
        if (cx < 0 || cx >= w || cy < 0 || cy >= h) return -1;
        byte nt = map[cy * w + cx];
        if (nt == 0) return -1; // no grass
        int ni = nt - 1;
        if (ni == currentType || ni < 0 || ni >= typeCount) return -1;
        return ni;
    }

    private static float EdgeProb(float pos, float zone)
    {
        if (pos >= zone) return 0f;
        float t = 1f - pos / zone;
        return t * t; // quadratic falloff
    }

    // Deterministic hash matching C++ GrassSystem::tileHash
    private static uint TileHash(int tx, int ty, int idx)
    {
        uint h = (uint)tx * 374761393u
               + (uint)ty * 668265263u
               + (uint)idx * 2654435769u;
        h = (h ^ (h >> 13)) * 1274126177u;
        h = h ^ (h >> 16);
        return h;
    }

    // Matches C++ GrassSystem::hashToFloat
    private static float HashToFloat(uint h)
    {
        return (float)(h & 0x00FFFFFFu) / (float)0x01000000u;
    }

    // Matches C++ vertex shader quickHash
    private static float QuickHash(float n)
    {
        // fract(sin(n) * 43758.5453123)
        double sn = Math.Sin(n) * 43758.5453123;
        return (float)(sn - Math.Floor(sn));
    }
}
