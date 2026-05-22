using System;
using System.Diagnostics;
using Necroking.Core;
using Necroking.GameSystems;
using Necroking.World;

namespace Necroking.Scenario.Scenarios;

/// <summary>
/// Benchmark scenario: stresses the ground+water shader to measure its real
/// per-pixel cost despite OS/driver-forced vsync.
///
/// Why stress: at 60Hz vsync we can't observe ms differences between cheap
/// and expensive shaders — they all finish under the 16.67ms budget. We
/// instead ask Game1 to run DrawGroundShader N extra times per frame (writing
/// the same pixels to the same backbuffer N times) and watch how frame time
/// climbs once GPU work exceeds the vsync slot. The slope of frame time vs N
/// gives us per-draw GPU cost in milliseconds.
///
/// Phases pair (camera) × (extra-draws). Each phase samples 2s after a 0.5s
/// warm-up. Phases with the camera over water sample the dual-tap + foam path;
/// phases over grass sample the cheap path; the delta IS the water cost.
/// </summary>
public class PerfWaterScenario : ScenarioBase
{
    public override string Name => "perf_water";
    public override bool WantsGround => true;
    public override bool BenchmarkMode => true;
    public override int GridSize => 64;

    private const float WarmupSec = 0.5f;
    private const float SampleSec = 2.0f;
    private const float PhaseTotal = WarmupSec + SampleSec;

    // (cameraX, cameraY, zoom, label, extraDraws)
    private readonly (float x, float y, float zoom, string label, int extra)[] _phases = new[]
    {
        ( 12f, 12f, 40f, "grass_x1",   0),
        ( 44f, 44f, 40f, "water_x1",   0),
        ( 36f, 36f, 96f, "shore_x1",   0),
        ( 12f, 12f, 40f, "grass_x100", 99),
        ( 44f, 44f, 40f, "water_x100", 99),
        ( 36f, 36f, 96f, "shore_x100", 99),
    };

    private readonly long[] _frameCount;
    private readonly double[] _frameMsSum;
    private readonly double[] _drawMsSum;
    private readonly double[] _groundMsSum;

    private int _phaseIdx;
    private float _phaseTimer;
    private bool _complete;
    private readonly Stopwatch _wallClock = new();
    private readonly Stopwatch _realFrameSw = new();
    private double _realFrameMs;

    public PerfWaterScenario()
    {
        _frameCount = new long[_phases.Length];
        _frameMsSum = new double[_phases.Length];
        _drawMsSum  = new double[_phases.Length];
        _groundMsSum = new double[_phases.Length];
    }

    public double LastDrawMs { get; set; }
    public double LastGroundMs { get; set; }
    public double LastFrameMs { get; set; }

    public override void OnInit(Simulation sim)
    {
        DebugLog.Clear(ScenarioLog);
        DebugLog.Log(ScenarioLog, "=== Perf Water Scenario ===");
        DebugLog.Log(ScenarioLog, $"GridSize={GridSize}, vsync={VsyncEnabled}, fts={FixedTimeStepEnabled}");

        if (GroundSystem == null) throw new InvalidOperationException("PerfWaterScenario requires WantsGround");
        int grassIdx   = GroundSystem.FindType("grass");
        int shallowIdx = GroundSystem.FindType("shallow_water");
        int deepIdx    = GroundSystem.FindType("deep_water");
        DebugLog.Log(ScenarioLog, $"Type indices: grass={grassIdx} shallow={shallowIdx} deep={deepIdx}");

        // Pond at (36, 36), radius 12 shallow with a deep core (r=7).
        int vw = GroundSystem.VertexW;
        int vh = GroundSystem.VertexH;
        float cx = 36f, cy = 36f;
        float rShallow = 12f, rDeep = 7f;
        for (int vy = 0; vy < vh; vy++)
        for (int vx = 0; vx < vw; vx++)
        {
            float dx = vx - cx, dy = vy - cy;
            float d = MathF.Sqrt(dx * dx + dy * dy);
            int type;
            if (d <= rDeep)         type = deepIdx;
            else if (d <= rShallow) type = shallowIdx;
            else                    type = grassIdx;
            GroundSystem.SetVertex(vx, vy, (byte)type);
        }

        _phaseIdx = 0;
        _phaseTimer = 0f;
        ApplyPhaseCamera();
        _wallClock.Restart();
        DebugLog.Log(ScenarioLog, $"Phase 0 ({_phases[0].label}): camera=({_phases[0].x},{_phases[0].y}) zoom={_phases[0].zoom} extraDraws={_phases[0].extra}");
    }

    private void ApplyPhaseCamera()
    {
        var p = _phases[_phaseIdx];
        ZoomOnLocation(p.x, p.y, p.zoom);
        ExtraGroundDrawsPerFrame = p.extra;
    }

    public override void OnTick(Simulation sim, float dt)
    {
        if (_complete) return;
        _phaseTimer += dt;

        if (_realFrameSw.IsRunning)
        {
            _realFrameSw.Stop();
            _realFrameMs = _realFrameSw.Elapsed.TotalMilliseconds;
        }
        _realFrameSw.Restart();

        if (_phaseTimer >= WarmupSec && _phaseTimer < PhaseTotal)
        {
            _frameCount[_phaseIdx]++;
            _frameMsSum[_phaseIdx]   += _realFrameMs;
            _drawMsSum[_phaseIdx]    += LastDrawMs;
            _groundMsSum[_phaseIdx]  += LastGroundMs;
        }

        if (_phaseTimer >= PhaseTotal)
        {
            long n = Math.Max(_frameCount[_phaseIdx], 1);
            double frameMs = _frameMsSum[_phaseIdx] / n;
            double drawMs = _drawMsSum[_phaseIdx] / n;
            double groundMs = _groundMsSum[_phaseIdx] / n;
            DebugLog.Log(ScenarioLog,
                $"Phase {_phaseIdx} ({_phases[_phaseIdx].label}) DONE: n={n} frame={frameMs:F3}ms ({(frameMs > 0 ? 1000.0 / frameMs : 0):F1} FPS) draw={drawMs:F3} ground={groundMs:F3}");

            _phaseIdx++;
            _phaseTimer = 0f;
            if (_phaseIdx >= _phases.Length)
            {
                _complete = true;
                return;
            }
            ApplyPhaseCamera();
            DebugLog.Log(ScenarioLog,
                $"Phase {_phaseIdx} ({_phases[_phaseIdx].label}): camera=({_phases[_phaseIdx].x},{_phases[_phaseIdx].y}) zoom={_phases[_phaseIdx].zoom} extraDraws={_phases[_phaseIdx].extra}");
        }
    }

    public override bool IsComplete => _complete;

    public override int OnComplete(Simulation sim)
    {
        _wallClock.Stop();
        DebugLog.Log(ScenarioLog, "=== Phase summary ===");
        for (int i = 0; i < _phases.Length; i++)
        {
            long n = Math.Max(_frameCount[i], 1);
            double frame = _frameMsSum[i] / n;
            double draw  = _drawMsSum[i] / n;
            double ground = _groundMsSum[i] / n;
            DebugLog.Log(ScenarioLog,
                $"  [{_phases[i].label,-10}] n={_frameCount[i],4}  frame={frame:F3}ms  fps={(frame > 0 ? 1000.0 / frame : 0):F1}  draw={draw:F3}  ground={ground:F3}  extraDraws={_phases[i].extra}");
        }

        // Per-extra-draw cost estimates (water_x100 - water_x1) / 99.
        double f_g1 = _frameMsSum[0] / Math.Max(_frameCount[0], 1);
        double f_w1 = _frameMsSum[1] / Math.Max(_frameCount[1], 1);
        double f_s1 = _frameMsSum[2] / Math.Max(_frameCount[2], 1);
        double f_g100 = _frameMsSum[3] / Math.Max(_frameCount[3], 1);
        double f_w100 = _frameMsSum[4] / Math.Max(_frameCount[4], 1);
        double f_s100 = _frameMsSum[5] / Math.Max(_frameCount[5], 1);

        DebugLog.Log(ScenarioLog, "=== Cost analysis ===");
        DebugLog.Log(ScenarioLog, $"  baseline frame (grass_x1, water_x1, shore_x1): {f_g1:F3} / {f_w1:F3} / {f_s1:F3} ms");
        DebugLog.Log(ScenarioLog, $"  stressed frame (grass_x100, water_x100, shore_x100): {f_g100:F3} / {f_w100:F3} / {f_s100:F3} ms");
        DebugLog.Log(ScenarioLog, $"  per-draw cost (frame_x100 - frame_x1) / 99:");
        DebugLog.Log(ScenarioLog, $"    grass: {(f_g100 - f_g1) / 99.0:F4} ms/draw  ({(f_g100 - f_g1) * 1000.0 / 99.0:F1} us)");
        DebugLog.Log(ScenarioLog, $"    water: {(f_w100 - f_w1) / 99.0:F4} ms/draw  ({(f_w100 - f_w1) * 1000.0 / 99.0:F1} us)");
        DebugLog.Log(ScenarioLog, $"    shore: {(f_s100 - f_s1) / 99.0:F4} ms/draw  ({(f_s100 - f_s1) * 1000.0 / 99.0:F1} us)");
        DebugLog.Log(ScenarioLog, $"  water cost vs grass: {(((f_w100 - f_w1) - (f_g100 - f_g1)) * 1000.0 / 99.0):F1} us/draw extra over grass");
        DebugLog.Log(ScenarioLog, $"  shore cost vs grass: {(((f_s100 - f_s1) - (f_g100 - f_g1)) * 1000.0 / 99.0):F1} us/draw extra over grass");

        DebugLog.Log(ScenarioLog, $"Wall-clock: {_wallClock.Elapsed.TotalSeconds:F2}s");
        return 0;
    }
}
