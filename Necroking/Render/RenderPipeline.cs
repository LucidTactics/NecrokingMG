using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Necroking.Render;

/// <summary>
/// Per-frame state handed to every render pass. One instance owned by
/// GameRenderer, refreshed at the top of Draw — passes must not cache values
/// across frames.
/// </summary>
public sealed class RenderContext
{
    public GraphicsDevice Device = null!;
    public SpriteBatch Batch = null!;
    public GameTime GameTime = null!;
    public int ScreenW;
    public int ScreenH;
}

/// <summary>
/// One named unit of draw work inside a <see cref="RenderPhase"/>. Enabled
/// gates execution (dev/scenario toggles); LastMs is refreshed every executed
/// frame for perf inspection.
/// </summary>
public abstract class RenderPass
{
    public readonly string Name;
    public bool Enabled = true;
    public double LastMs;

    protected RenderPass(string name) => Name = name;
    public abstract void Execute(RenderContext ctx);
}

/// <summary>
/// Imperative pass: runs an arbitrary draw action. The step-0 wrapper for the
/// legacy Draw() blocks, and permanently the right tool for call-order work
/// (ground shader, HUD/editor UI, bloom internals).
/// </summary>
public sealed class CustomPass : RenderPass
{
    private readonly Action<RenderContext> _draw;
    public CustomPass(string name, Action<RenderContext> draw) : base(name) => _draw = draw;
    public override void Execute(RenderContext ctx) => _draw(ctx);
}

/// <summary>
/// A group of passes sharing one render-target scope. OnBegin/OnEnd own target
/// binds and phase-wide state (bloom capture bracket, HUD batch); passes only
/// draw. Render targets may only change at phase boundaries or inside a
/// self-contained pass body (fog-of-war prep, bloom's internal mip chain).
/// </summary>
public sealed class RenderPhase
{
    public readonly string Name;
    public readonly List<RenderPass> Passes = new();
    public Action<RenderContext>? OnBegin;
    public Action<RenderContext>? OnEnd;

    public RenderPhase(string name) => Name = name;
    public RenderPass Add(RenderPass pass) { Passes.Add(pass); return pass; }
}

/// <summary>
/// The frame as data: an ordered list of phases executed top to bottom. Built
/// once at load (GameRenderer.BuildPipeline); reordering the frame is a data
/// edit, not a code-flow change.
/// </summary>
public sealed class RenderPipeline
{
    public readonly List<RenderPhase> Phases = new();
    private readonly System.Diagnostics.Stopwatch _sw = new();

    public RenderPhase AddPhase(RenderPhase phase) { Phases.Add(phase); return phase; }

    public void Execute(RenderContext ctx)
    {
        foreach (var phase in Phases)
        {
            phase.OnBegin?.Invoke(ctx);
            foreach (var pass in phase.Passes)
            {
                if (!pass.Enabled) continue;
                _sw.Restart();
                pass.Execute(ctx);
                _sw.Stop();
                pass.LastMs = _sw.Elapsed.TotalMilliseconds;
            }
            phase.OnEnd?.Invoke(ctx);
        }
    }

    /// <summary>Find a pass by name (case-insensitive) across all phases —
    /// used by dev commands to toggle passes.</summary>
    public RenderPass? FindPass(string name)
    {
        foreach (var phase in Phases)
            foreach (var pass in phase.Passes)
                if (string.Equals(pass.Name, name, StringComparison.OrdinalIgnoreCase))
                    return pass;
        return null;
    }
}
