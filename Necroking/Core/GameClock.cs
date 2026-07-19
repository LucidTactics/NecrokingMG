using System;

namespace Necroking.Core;

/// <summary>
/// Single authority for "what time is flowing, how fast, and what is paused".
///
/// Game1.Update drives it in two phases each frame:
///  1. <see cref="BeginFrame"/> at the top-of-frame dt derivation point (before the
///     main-menu early-returns, so menu frames still accrue VisualTime — phase parity
///     with the old <c>_gameTime</c> field).
///  2. <see cref="GateWorld"/> immediately before the sim gate, after all pause/menu
///     input for the frame has run — so pausing or entering an editor freezes the
///     world the SAME frame, matching the old inline <c>!_paused &amp;&amp; !editorActive</c>.
/// Everything else only READS.
///
/// Time domains, fastest-resetting to never-resetting:
/// <list type="bullet">
/// <item><see cref="RawDt"/> — unclamped wall-clock frame delta. FPS/perf readouts and
///   decays that must track true elapsed time across hitches.</item>
/// <item><see cref="RealDt"/> — RawDt clamped to <see cref="MaxFrameDt"/>. Real-time UI
///   (toasts, flashes) that must not jump after a hitch. Never paused, never scaled.</item>
/// <item><see cref="VisualDt"/> / <see cref="VisualTime"/> — presentation clock: 0 while
///   <see cref="Paused"/>, else RealDt × <see cref="TimeScale"/>. VisualTime is NEVER
///   reset (not even on new game): wind / ground-shader / pulse sin() drivers need phase
///   continuity, and resetting would make every phase-driven visual pop.</item>
/// <item><see cref="WorldDt"/> / <see cref="WorldRunning"/> — gameplay clock: VisualDt,
///   additionally 0 while the world is suspended (a full-screen editor is open) or before
///   GateWorld has run this frame. ALL state-mutating updates must consume WorldDt (or
///   check WorldRunning) — consuming VisualDt from gameplay code is how "corruption
///   spreads while the map editor is open" class bugs happen. The accumulated world-time
///   VALUE is canonical on <c>Simulation.GameTime</c> (per-game, reset by
///   <c>Simulation.Init</c>) — the clock owns rates and gates; the sim owns the world's
///   age.</item>
/// </list>
///
/// Deliberately OUTSIDE this clock: the multiplayer net loop (wall clock, must run while
/// paused/in menus — see Necroking/Net/README.md), dev-server drain, editor cursor
/// blinks, and the editor-mode scenario tick's fixed 1/60 step.
/// </summary>
public sealed class GameClock
{
    /// <summary>Frame-delta clamp (50 ms): a hitch or debugger break never produces a
    /// giant catch-up step in any paused-able/scaled domain.</summary>
    public const float MaxFrameDt = 1f / 20f;

    /// <summary>Player-facing speed range for the +/- keys and HUD time controls.
    /// <see cref="SetTimeScale"/> itself only sanity-clamps, so dev commands can exceed
    /// this range on purpose (matching the old unclamped dev 'speed' command).</summary>
    public const float MinUserTimeScale = 0.25f;
    public const float MaxUserTimeScale = 8f;

    /// <summary>Who is holding the game paused. Flags: several sources can hold a pause
    /// at once and each releases only its own — e.g. the press-L inspect pause survives
    /// the player also opening and closing the pause menu... unless a "force resume"
    /// path calls <see cref="ClearAllPauses"/> (menu buttons do; that matches the old
    /// unconditional <c>_paused = false</c> writes).</summary>
    [Flags]
    public enum PauseSource
    {
        None = 0,
        /// <summary>Player-initiated: ESC pause menu, Space/P toggle, HUD pause button.</summary>
        User = 1,
        /// <summary>Press-L unit inspect with the PauseOnManualInspect setting on;
        /// released when the unit info panel closes.</summary>
        Inspect = 2,
        /// <summary>Dev-server 'pause' command.</summary>
        Dev = 4,

        Mapeditor = 8,
    }

    /// <summary>Unclamped wall-clock frame delta. Never pauses, never scales.</summary>
    public float RawDt { get; private set; }

    /// <summary>Wall-clock frame delta clamped to <see cref="MaxFrameDt"/>. Never pauses,
    /// never scales. For real-time UI decays that shouldn't jump after a hitch.</summary>
    public float RealDt { get; private set; }

    /// <summary>Presentation delta: 0 while <see cref="Paused"/>, else
    /// <see cref="RealDt"/> × <see cref="TimeScale"/>. Advances in full-screen editors
    /// (they don't pause the presentation clock) — gameplay must use
    /// <see cref="WorldDt"/> instead.</summary>
    public float VisualDt { get; private set; }

    /// <summary>Accumulated <see cref="VisualDt"/>. NEVER reset — sin()-phase driver for
    /// wind, ground shader, pulses; resetting would visibly pop every phase-driven
    /// visual. Do not use as "how old is this world" — that's <c>Simulation.GameTime</c>.</summary>
    public float VisualTime { get; private set; }

    /// <summary>Gameplay delta: <see cref="VisualDt"/>, additionally 0 while the world is
    /// suspended (full-screen editor open) or on frames where <see cref="GateWorld"/>
    /// hasn't run (main-menu / early-return frames). The only delta gameplay-mutating
    /// code may consume.</summary>
    public float WorldDt { get; private set; }

    /// <summary>True only after <see cref="GateWorld"/> has run this frame with no pause
    /// and no world suspension. The single predicate for "gameplay may advance".</summary>
    public bool WorldRunning { get; private set; }

    /// <summary>Current speed multiplier (1 = normal). Set via <see cref="SetTimeScale"/>.</summary>
    public float TimeScale { get; private set; } = 1f;

    /// <summary>Active pause holders. Empty = not paused.</summary>
    public PauseSource PauseSources { get; private set; }

    /// <summary>True while any <see cref="PauseSource"/> holds the game paused. Zeroes
    /// <see cref="VisualDt"/> and <see cref="WorldDt"/>; <see cref="RawDt"/> and
    /// <see cref="RealDt"/> keep flowing.</summary>
    public bool Paused => PauseSources != PauseSource.None;

    /// <summary>Set the speed multiplier. Sanity-clamps against NaN/zero/negative and
    /// absurd values; the player-facing 0.25–8 range is enforced by the key/HUD handlers
    /// via <see cref="MinUserTimeScale"/>/<see cref="MaxUserTimeScale"/> so dev commands
    /// can exceed it deliberately.</summary>
    public void SetTimeScale(float scale)
    {
        if (float.IsNaN(scale)) return;
        TimeScale = Math.Clamp(scale, 0.01f, 100f);
    }

    public void Pause(PauseSource source) => PauseSources |= source;

    /// <summary>Release one pause holder. No-op if that source isn't holding (e.g. it was
    /// already force-cleared by <see cref="ClearAllPauses"/>).</summary>
    public void Resume(PauseSource source) => PauseSources &= ~source;

    public void TogglePause(PauseSource source)
    {
        if ((PauseSources & source) != 0) Resume(source);
        else Pause(source);
    }

    /// <summary>Force-unpause regardless of holders. The "the game must run now" paths:
    /// pause-menu Resume/editor/main-menu buttons, dev resume — all of which historically
    /// did an unconditional <c>_paused = false</c> that also stomped the inspect pause.</summary>
    public void ClearAllPauses() => PauseSources = PauseSource.None;

    public bool IsPausedBy(PauseSource source) => (PauseSources & source) != 0;

    /// <summary>Phase 1 — call once at the top of Update with the wall-clock frame delta.
    /// Computes Raw/Real/Visual and zeroes the World domain until <see cref="GateWorld"/>
    /// runs, so frames that early-return before the gate (main menu, scenario list)
    /// always present WorldDt = 0 / WorldRunning = false to the Draw pass.</summary>
    public void BeginFrame(float rawDt)
    {
        RawDt = rawDt;
        RealDt = MathF.Min(rawDt, MaxFrameDt);
        VisualDt = Paused ? 0f : RealDt * TimeScale;
        VisualTime += VisualDt;
        WorldDt = 0f;
        WorldRunning = false;
    }

    /// <summary>Phase 2 — call immediately before the sim gate, after all pause/menu
    /// input for the frame has run. <paramref name="worldSuspended"/> = a full-screen
    /// editor owns the screen (the world exists but must not advance).</summary>
    public void GateWorld(bool worldSuspended)
    {
        WorldRunning = !Paused && !worldSuspended;
        WorldDt = WorldRunning ? VisualDt : 0f;
    }

    /// <summary>New game / scenario start: clear every pause holder and restore 1×
    /// speed, so a pause or 8× speed from the previous session is never carried into a
    /// fresh world. <see cref="VisualTime"/> is deliberately NOT reset (phase
    /// continuity); world age restarts with the fresh <c>Simulation</c>.</summary>
    public void OnWorldStart()
    {
        ClearAllPauses();
        TimeScale = 1f;
    }
}
