using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Necroking.Core;
using Necroking.World;

namespace Necroking.GameSystems;

/// <summary>
/// Per-cell scalar field of "death fog" with sources, sinks, and diffusion.
///
/// Backend goals (per design discussion):
///  - Coarse grid (CellSize world-units per cell) so a 4096x4096 map fits in
///    ~1M cells at CellSize=4. Visual layer hides the grid.
///  - Sparse: most cells are zero. We track an "active set" so the per-frame
///    update only touches cells with a non-zero value or a non-zero neighbor.
///  - Sources / sinks come from EnvironmentObjectDef.FogEmitRate /
///    FogAbsorbRate. Each frame we scan _envSystem.Objects and apply.
///  - Diffusion: explicit-Euler heat equation each frame, k chosen for
///    stability (&lt; 0.25). No global decay — fog only depletes when it
///    percolates to a sink.
///  - Edges: out-of-bounds neighbors treated as 0 (fog leaks off the map).
/// </summary>
public class DeathFogSystem
{
    /// <summary>World-units per fog cell. Set at Init time; field is read-only after.</summary>
    public int CellSize { get; private set; } = 4;
    public int Width  { get; private set; }
    public int Height { get; private set; }

    /// <summary>Read-only view of the current density buffer (row-major y*W+x).</summary>
    public float[] Density => _read;

    // Tunables (publicly mutable for now — wire into settings later if needed).
    public float DiffusionRate { get; set; } = 0.18f; // k; must stay < 0.25 for stability
    public float SourceRateScale { get; set; } = 1f;
    public float SinkRateScale   { get; set; } = 1f;
    public float ZeroEpsilon { get; set; } = 0.001f;

    private float[] _read = Array.Empty<float>();
    private float[] _write = Array.Empty<float>();

    /// <summary>Indices of cells worth iterating this frame: any cell with a
    /// non-zero value OR adjacent to one. Maintained incrementally.</summary>
    private HashSet<int> _active = new();
    private HashSet<int> _nextActive = new();

    public bool DebugVisible { get; private set; }
    public void ToggleDebug() => DebugVisible = !DebugVisible;

    /// <summary>Initialize for a world of the given world-tile dimensions. Safe to
    /// call again on map reload — clears all state.</summary>
    public void Init(int worldW, int worldH, int cellSize = 4)
    {
        CellSize = Math.Max(1, cellSize);
        Width  = Math.Max(1, (worldW  + CellSize - 1) / CellSize);
        Height = Math.Max(1, (worldH + CellSize - 1) / CellSize);
        _read  = new float[Width * Height];
        _write = new float[Width * Height];
        _active.Clear();
        _nextActive.Clear();
        DebugLog.Log("startup",
            $"DeathFog: grid {Width}x{Height} (cellSize={CellSize}, world={worldW}x{worldH})");
    }

    /// <summary>Convert world coords to fog-cell coords. Clamped to grid bounds.</summary>
    public void WorldToCell(float wx, float wy, out int cx, out int cy)
    {
        cx = Math.Clamp((int)(wx / CellSize), 0, Width  - 1);
        cy = Math.Clamp((int)(wy / CellSize), 0, Height - 1);
    }

    public float Sample(float worldX, float worldY)
    {
        if (_read.Length == 0) return 0f;
        WorldToCell(worldX, worldY, out var cx, out var cy);
        return _read[cy * Width + cx];
    }

    /// <summary>One simulation step. Reads sources/sinks from <paramref name="env"/>.
    /// Caller passes raw frame dt in seconds.</summary>
    public void Update(EnvironmentSystem env, float dt)
    {
        if (_read.Length == 0 || dt <= 0f) return;

        // 1. Apply sources & queue absorbers — applied to the READ buffer so
        //    the diffusion step sees the latest emitted values immediately.
        ApplySources(env, dt);

        // 2. Diffuse over active cells -> WRITE buffer.
        DiffuseActiveCells();

        // 3. Apply sinks (after diffusion) — drain from WRITE buffer.
        ApplySinks(env, dt);

        // 4. Swap, clean small values, refresh active set.
        Swap();
        FinalizeActiveSet();
    }

    private void ApplySources(EnvironmentSystem env, float dt)
    {
        var objs = env.Objects;
        for (int i = 0; i < objs.Count; i++)
        {
            var obj = objs[i];
            // Skip dead/blueprint instances. Keep cheap by indexing once.
            var rt = env.GetObjectRuntime(i);
            if (!rt.Alive) continue;
            if (rt.BuildProgress < 1f) continue;

            var def = env.Defs[obj.DefIndex];
            float emit = def.FogEmitRate * SourceRateScale;
            if (emit <= 0f) continue;

            int cx = Math.Clamp((int)(obj.X / CellSize), 0, Width  - 1);
            int cy = Math.Clamp((int)(obj.Y / CellSize), 0, Height - 1);
            int idx = cy * Width + cx;
            _read[idx] += emit * dt;
            MarkActive(cx, cy);
        }
    }

    private void ApplySinks(EnvironmentSystem env, float dt)
    {
        var objs = env.Objects;
        for (int i = 0; i < objs.Count; i++)
        {
            var obj = objs[i];
            var rt = env.GetObjectRuntime(i);
            if (!rt.Alive) continue;
            if (rt.Collected) continue; // foragable picked up

            var def = env.Defs[obj.DefIndex];
            float absorb = def.FogAbsorbRate * SinkRateScale;
            if (absorb <= 0f) continue;

            int cx = Math.Clamp((int)(obj.X / CellSize), 0, Width  - 1);
            int cy = Math.Clamp((int)(obj.Y / CellSize), 0, Height - 1);
            int idx = cy * Width + cx;
            float take = Math.Min(_write[idx], absorb * dt);
            _write[idx] -= take;
            if (_write[idx] < 0f) _write[idx] = 0f;
            // Sink cells must stay active — neighbors will keep flowing into
            // them until depleted. Mark them so diffusion considers them.
            MarkActive(cx, cy);
        }
    }

    /// <summary>Heat-equation step over the active set. Inactive cells stay 0.</summary>
    private void DiffuseActiveCells()
    {
        // Copy read -> write first so unvisited cells start equal.
        Array.Copy(_read, _write, _read.Length);

        float k = DiffusionRate;
        foreach (int idx in _active)
        {
            int x = idx % Width;
            int y = idx / Width;

            float c = _read[idx];
            float n = (y > 0)            ? _read[idx - Width] : 0f;
            float s = (y < Height - 1)   ? _read[idx + Width] : 0f;
            float w = (x > 0)            ? _read[idx - 1]     : 0f;
            float e = (x < Width - 1)    ? _read[idx + 1]     : 0f;
            float lap = n + s + w + e - 4f * c;
            _write[idx] = c + k * lap;
        }
    }

    private void Swap()
    {
        (_read, _write) = (_write, _read);
    }

    /// <summary>Walk the previous active set (and its neighbors): drop cells that
    /// are now ~0 with no non-zero neighbor; promote any neighbor of a non-zero
    /// cell into the next active set so diffusion can spread next frame.</summary>
    private void FinalizeActiveSet()
    {
        _nextActive.Clear();
        foreach (int idx in _active)
        {
            float v = _read[idx];
            if (v < ZeroEpsilon) _read[idx] = 0f;

            // Keep this cell active if it has any density itself...
            if (_read[idx] > 0f)
            {
                _nextActive.Add(idx);
                int x = idx % Width;
                int y = idx / Width;
                // ...and propagate "active neighbor" status to its 4-neighbors so
                // the next diffusion step considers them too.
                if (y > 0)          _nextActive.Add(idx - Width);
                if (y < Height - 1) _nextActive.Add(idx + Width);
                if (x > 0)          _nextActive.Add(idx - 1);
                if (x < Width - 1)  _nextActive.Add(idx + 1);
            }
        }
        (_active, _nextActive) = (_nextActive, _active);
    }

    private void MarkActive(int cx, int cy)
    {
        int idx = cy * Width + cx;
        _active.Add(idx);
        // Also pre-seed neighbors so diffusion can spread out from a freshly
        // emitted cell on the very same frame's update.
        if (cy > 0)          _active.Add(idx - Width);
        if (cy < Height - 1) _active.Add(idx + Width);
        if (cx > 0)          _active.Add(idx - 1);
        if (cx < Width - 1)  _active.Add(idx + 1);
    }

    /// <summary>Scan all env defs and auto-tag tree-asset defs as sinks. Called once
    /// after env defs load, so we don't have to edit ~40 JSON entries by hand.</summary>
    public static void AutoTagTreesAsSinks(EnvironmentSystem env, float absorbRate)
    {
        for (int i = 0; i < env.DefCount; i++)
        {
            var def = env.GetDef(i);
            if (def.FogAbsorbRate > 0f) continue; // explicitly set already
            if (string.IsNullOrEmpty(def.TexturePath)) continue;
            if (def.TexturePath.Replace('\\', '/')
                   .Contains("/Environment/Trees/", StringComparison.OrdinalIgnoreCase))
            {
                def.FogAbsorbRate = absorbRate;
            }
        }
    }

    // ---------------- Debug rendering ----------------

    /// <summary>Draw a translucent heat-map quad per non-zero cell. Caller must
    /// have an active SpriteBatch with PointClamp/AlphaBlend (the standard pass).</summary>
    public void DrawDebug(SpriteBatch batch, Texture2D pixel, Render.Renderer renderer,
        Render.Camera25D camera)
    {
        if (!DebugVisible || _read.Length == 0) return;

        // Cell quad in world units, projected per corner.
        // Color sweep blue → cyan → green → yellow → red as density rises.
        float scale = 1.0f; // value at which color saturates

        foreach (int idx in _active)
        {
            float v = _read[idx];
            if (v <= 0f) continue;
            int x = idx % Width;
            int y = idx / Width;

            float wx0 = x * CellSize;
            float wy0 = y * CellSize;
            float wx1 = wx0 + CellSize;
            float wy1 = wy0 + CellSize;

            // Project the four corners and draw an axis-aligned screen rect that
            // contains them. Approximates the cell quad — fine for a debug view.
            var tl = renderer.WorldToScreen(new Vec2(wx0, wy0), 0f, camera);
            var br = renderer.WorldToScreen(new Vec2(wx1, wy1), 0f, camera);
            int sx = (int)Math.Floor(Math.Min(tl.X, br.X));
            int sy = (int)Math.Floor(Math.Min(tl.Y, br.Y));
            int sw = (int)Math.Ceiling(Math.Abs(br.X - tl.X));
            int sh = (int)Math.Ceiling(Math.Abs(br.Y - tl.Y));
            if (sw <= 0 || sh <= 0) continue;

            var color = HeatColor(v / scale);
            batch.Draw(pixel, new Rectangle(sx, sy, sw, sh), color);
        }
    }

    /// <summary>0..1 → blue → cyan → green → yellow → red. Alpha rises from 60..200.</summary>
    private static Color HeatColor(float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        float r, g, b;
        if (t < 0.25f)      { float u = t / 0.25f;          r = 0;            g = u;            b = 1f; }
        else if (t < 0.5f)  { float u = (t - 0.25f) / 0.25f;r = 0;            g = 1f;           b = 1f - u; }
        else if (t < 0.75f) { float u = (t - 0.5f) / 0.25f; r = u;            g = 1f;           b = 0f; }
        else                { float u = (t - 0.75f) / 0.25f;r = 1f;           g = 1f - u;       b = 0f; }
        byte a = (byte)(60 + 140 * t);
        return new Color((byte)(r * 255), (byte)(g * 255), (byte)(b * 255), a);
    }
}
