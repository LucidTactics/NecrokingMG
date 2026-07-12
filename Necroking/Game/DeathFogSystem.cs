using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Necroking.Core;
using Necroking.Lib;
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

    /// <summary>Read-only view of the active-cell index set. Renderers iterate this
    /// rather than the full Width×Height grid so per-frame work scales with fog
    /// footprint, not map size.</summary>
    public IReadOnlyCollection<int> ActiveCells => _active;

    // Tunables (publicly mutable for now — wire into settings later if needed).
    public float DiffusionRate { get; set; } = 0.18f; // k; must stay < 0.25 for stability
    public float SourceRateScale { get; set; } = 1f;
    public float SinkRateScale   { get; set; } = 1f;
    public float ZeroEpsilon { get; set; } = 0.001f;

    // Corruption tunables. Per-instance stress accumulates from absorbed fog and
    // decays at HealRate; once it exceeds CorruptionThreshold the instance flips
    // to the def's CorruptedSprite. After corrupting, the absorber rate drops to
    // CorruptedAbsorbRate so corrupted forests don't bound fog density.
    //   HealRate=4 means trees absorbing < 4 fog/sec recover indefinitely.
    //   Threshold=30 means a tree at full 6/sec absorption corrupts in ~15s.
    public float CorruptionHealRate           { get; set; } = 4f;
    public float CorruptionThreshold          { get; set; } = 30f;
    public float CorruptedAbsorbRate          { get; set; } = 0.5f;
    /// <summary>Seconds for a tree's dissolve transition once it crosses threshold.
    /// Defs with no CorruptedSprite skip the transition and flip to Corrupted instantly.</summary>
    public float CorruptionTransitionDuration { get; set; } = 10f;

    // Ground corruption: probability per second of corrupting a single vertex
    // at fog density d follows P(d) = GroundCorruptionMaxRate * d * d. Anchors:
    // P(0)=0%, P(0.5)=5%, P(1.0)=20% with a max-rate of 0.20.
    public float GroundCorruptionMaxRate { get; set; } = 0.20f;
    private float _groundCorruptionTickTimer;
    private readonly Random _groundCorrRng = new();

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

    /// <summary>Convert world coords to fog-cell coords. Clamped to grid bounds.
    /// Safe on an uninitialized grid (scenarios skip Init, leaving Width=0) —
    /// clamps to cell (0,0) instead of throwing on the inverted 0..-1 range.</summary>
    public void WorldToCell(float wx, float wy, out int cx, out int cy)
    {
        cx = Math.Clamp((int)(wx / CellSize), 0, Math.Max(0, Width  - 1));
        cy = Math.Clamp((int)(wy / CellSize), 0, Math.Max(0, Height - 1));
    }

    public float Sample(float worldX, float worldY)
    {
        if (_read.Length == 0) return 0f;
        WorldToCell(worldX, worldY, out var cx, out var cy);
        return _read[cy * Width + cx];
    }

    /// <summary>Add <paramref name="amount"/> blight (death-fog density) to the single
    /// cell containing the world point — the "blight bomb". Negative amounts subtract
    /// (clamped at 0). The cell (and its neighbors) are marked active so diffusion
    /// picks the change up next tick. No-op off-grid.</summary>
    public void AddDensity(float worldX, float worldY, float amount)
    {
        if (_read.Length == 0 || amount == 0f) return;
        WorldToCell(worldX, worldY, out int cx, out int cy);
        int idx = cy * Width + cx;
        _read[idx] = MathF.Max(0f, _read[idx] + amount);
        MarkActive(cx, cy);
    }

    /// <summary>Purifying-bomb cleanse: a 5×5 cell kernel centered on the world point
    /// removes blight, strongest at the center. <paramref name="centerReduction"/> is
    /// the amount removed from the center cell; the four orthogonal neighbors lose
    /// half, the four inner diagonals a quarter, and the outer ring (minus the 4 far
    /// corners) an eighth — so centerReduction=4 yields the 4 / 2 / 1 / 0.5 pattern.
    /// The 4 far corners are untouched, so 21 of the 25 cells are affected.
    /// <para>Low-blight scaling: a cell holding &lt; 1 blight only loses
    /// <c>reduction × (blight × 0.15 + 0.1)</c>, so faint blight is cleared gently
    /// rather than nuked disproportionately. Never drops below 0.</para></summary>
    public void PurifyArea(float worldX, float worldY, float centerReduction)
    {
        if (_read.Length == 0 || centerReduction <= 0f) return;
        WorldToCell(worldX, worldY, out int ccx, out int ccy);

        for (int dy = -2; dy <= 2; dy++)
        for (int dx = -2; dx <= 2; dx++)
        {
            float w = PurifyWeight(dx, dy);
            if (w <= 0f) continue; // far corner — untouched
            int cx = ccx + dx, cy = ccy + dy;
            if (cx < 0 || cy < 0 || cx >= Width || cy >= Height) continue;
            int idx = cy * Width + cx;
            float blight = _read[idx];
            if (blight <= 0f) continue;

            float reduction = centerReduction * w;
            // Scale the cleanse down where there's barely any blight to clear.
            if (blight < 2) reduction *= (blight * 0.4f + 0.2f);
            _read[idx] = MathF.Max(0f, blight - reduction);
            MarkActive(cx, cy);
        }
    }

    /// <summary>Kernel weight (fraction of centerReduction) for a cell offset in the
    /// 5×5 purify area. center=1, orthogonal=½, inner-diagonal=¼, outer-ring=⅛, far
    /// corners (±2,±2)=0 (excluded).</summary>
    private static float PurifyWeight(int dx, int dy)
    {
        int ax = Math.Abs(dx), ay = Math.Abs(dy);
        int cheb = Math.Max(ax, ay);
        if (cheb == 0) return 1f;                          // center
        if (cheb == 1) return (ax + ay == 1) ? 0.5f : 0.25f; // ortho vs inner-diagonal
        if (ax == 2 && ay == 2) return 0f;                 // far corner — excluded
        return 0.125f;                                     // outer ring minus corners
    }

    /// <summary>One simulation step. Reads sources/sinks from <paramref name="env"/>.
    /// Caller passes raw frame dt in seconds. Pass <paramref name="ground"/> to
    /// also tick ground-vertex corruption (once per second based on density).</summary>
    public void Update(EnvironmentSystem env, float dt, GroundSystem? ground = null)
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

        // 5. Ground corruption tick (once per second).
        if (ground != null) TickGroundCorruption(ground, dt);
    }

    /// <summary>Once-per-second ground-corruption pass over active fog cells.
    /// Each vertex inside an active cell rolls against P(d) = MaxRate * d^2 and,
    /// on success, is swapped to its def's corrupted variant (if mapped).</summary>
    private void TickGroundCorruption(GroundSystem ground, float dt)
    {
        _groundCorruptionTickTimer += dt;
        if (_groundCorruptionTickTimer < 1.0f) return;
        _groundCorruptionTickTimer = 0f;

        int vertexW = ground.VertexW;
        int vertexH = ground.VertexH;
        if (vertexW <= 0 || vertexH <= 0) return;

        foreach (int idx in _active)
        {
            float d = _read[idx];
            if (d <= 0f) continue;
            if (d > 1f) d = 1f;
            float chance = GroundCorruptionMaxRate * d * d;
            if (chance <= 0f) continue;

            int cx = idx % Width;
            int cy = idx / Width;
            int vx0 = cx * CellSize;
            int vy0 = cy * CellSize;
            int vx1 = Math.Min(vx0 + CellSize, vertexW);
            int vy1 = Math.Min(vy0 + CellSize, vertexH);

            for (int vy = vy0; vy < vy1; vy++)
            {
                for (int vx = vx0; vx < vx1; vx++)
                {
                    if (_groundCorrRng.NextSingle() < chance)
                        ground.CorruptVertex(vx, vy);
                }
            }
        }
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

            // Once a tree starts transitioning OR fully corrupts it stops fighting —
            // absorb drops to the residual rate so neighbors get hit harder (chain reaction).
            bool transitioning = rt.CorruptionTime > 0f && !rt.Corrupted;
            float baseAbsorb = (rt.Corrupted || transitioning) ? CorruptedAbsorbRate : def.FogAbsorbRate;
            float absorb = baseAbsorb * SinkRateScale;

            float take = 0f;
            if (absorb > 0f)
            {
                int cx = Math.Clamp((int)(obj.X / CellSize), 0, Width  - 1);
                int cy = Math.Clamp((int)(obj.Y / CellSize), 0, Height - 1);
                int idx = cy * Width + cx;
                take = Math.Min(_write[idx], absorb * dt);
                _write[idx] -= take;
                if (_write[idx] < 0f) _write[idx] = 0f;
                // Sink cells must stay active — neighbors will keep flowing into
                // them until depleted. Mark them so diffusion considers them.
                MarkActive(cx, cy);
            }

            if (def.IsCorruptable && !rt.Corrupted)
            {
                if (transitioning)
                {
                    // Mid-dissolve: advance timer, complete when duration reached.
                    rt.CorruptionTime += dt;
                    if (rt.CorruptionTime >= CorruptionTransitionDuration)
                    {
                        rt.Corrupted = true;
                        DebugLog.Log("startup", $"DeathFog: object {i} ({def.Id}) finished dissolve at ({obj.X:F1},{obj.Y:F1})");
                    }
                    env.SetObjectRuntime(i, rt);
                }
                else
                {
                    // Healthy: stress accumulates from absorbed fog and decays at HealRate.
                    // Trees in light fog (take < HealRate*dt) heal faster than they
                    // take damage and stay healthy indefinitely.
                    float stress = rt.CorruptionStress + take - CorruptionHealRate * dt;
                    if (stress < 0f) stress = 0f;
                    if (stress >= CorruptionThreshold)
                    {
                        rt.CorruptionStress = CorruptionThreshold;
                        // Defs with no dissolve target skip the transition entirely —
                        // a 5-second dissolve to nothing visible would just be a no-op.
                        if (string.IsNullOrEmpty(def.CorruptedSprite))
                        {
                            rt.Corrupted = true;
                            DebugLog.Log("startup", $"DeathFog: object {i} ({def.Id}) corrupted (no transition — no CorruptedSprite) at ({obj.X:F1},{obj.Y:F1})");
                        }
                        else
                        {
                            rt.CorruptionTime = 0.0001f; // signals transition started
                            DebugLog.Log("startup", $"DeathFog: object {i} ({def.Id}) starting dissolve at ({obj.X:F1},{obj.Y:F1})");
                        }
                    }
                    else
                    {
                        rt.CorruptionStress = stress;
                    }
                    env.SetObjectRuntime(i, rt);
                }
            }
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
    public void DrawDebug(Render.SpriteScope batch, Texture2D pixel, Render.Renderer renderer,
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
