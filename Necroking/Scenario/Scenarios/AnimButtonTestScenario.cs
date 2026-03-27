using Necroking.Core;
using Necroking.GameSystems;

namespace Necroking.Scenario.Scenarios;

/// <summary>
/// Tests the animation preview buttons in the Unit Editor:
/// - Opens Unit Editor, selects first unit
/// - Logs initial animation state (frame, playing, looping)
/// - Simulates toggle loop, step-forward, step-back, play/pause
/// - Logs state after each action and verifies expected changes
/// - Returns PASS (0) or FAIL (1) based on results
/// </summary>
public class AnimButtonTestScenario : UIScenarioBase
{
    public override string Name => "anim_button_test";
    public override bool WantsGround => true;

    // Game1 sets this after editor is ready so we can call methods on the unit editor
    public UnitEditorAccessor? UnitEditor;

    private bool _complete;
    private int _phase;
    private int _tickCount;
    private int _failures;

    // Captured state snapshots
    private bool _initialPlaying;
    private bool _initialLooping;
    private float _initialAnimTime;

    public override void OnInit(Simulation sim)
    {
        DebugLog.Clear(ScenarioLog);
        DebugLog.Log(ScenarioLog, "=== Animation Button Test Scenario ===");
    }

    public override void OnTick(Simulation sim, float dt)
    {
        if (_complete) return;
        _tickCount++;

        switch (_phase)
        {
            case 0: // Open Unit Editor
                DebugLog.Log(ScenarioLog, "Phase 0: Opening Unit Editor");
                RequestedMenuState = "UnitEditor";
                _phase = 1;
                _tickCount = 0;
                break;

            case 1: // Wait, then select first unit
                if (_tickCount < 5) return;
                DebugLog.Log(ScenarioLog, "Phase 1: Selecting first unit");
                RequestSelectFirst = true;
                _phase = 2;
                _tickCount = 0;
                break;

            case 2: // Wait for selection to render, then log initial state
                if (_tickCount < 10) return;
                if (UnitEditor == null)
                {
                    DebugLog.Log(ScenarioLog, "FAIL: UnitEditor accessor not set by Game1");
                    _failures++;
                    _complete = true;
                    return;
                }
                if (!UnitEditor.HasSelection)
                {
                    DebugLog.Log(ScenarioLog, "FAIL: No unit selected after SelectFirst");
                    _failures++;
                    _complete = true;
                    return;
                }

                _initialPlaying = UnitEditor.PreviewPlaying;
                _initialLooping = UnitEditor.PreviewLooping;
                _initialAnimTime = UnitEditor.PreviewAnimTime;

                DebugLog.Log(ScenarioLog, $"Initial state — Playing: {_initialPlaying}, Looping: {_initialLooping}, AnimTime: {_initialAnimTime:F2}");
                _phase = 3;
                _tickCount = 0;
                break;

            // Phases 3-8: UnitEditor is guaranteed non-null (phase 2 exits on null)
            case 3: RunToggleLoopTest(); break;
            case 4: RunTogglePlayPauseTest(); break;
            case 5: RunStepForwardTest(); break;
            case 6: RunStepBackTest(); break;
            case 7: RunResumePlaybackTest(); break;

            case 8: // Done
                RequestedMenuState = "None";
                _complete = true;
                break;
        }
    }

    private void RunToggleLoopTest()
    {
        if (_tickCount < 3) return;
        var ue = UnitEditor!;
        DebugLog.Log(ScenarioLog, "Test: Toggle Loop");
        ue.ToggleLoop();
        bool expected = !_initialLooping;
        bool actual = ue.PreviewLooping;
        DebugLog.Log(ScenarioLog, $"  After ToggleLoop — Looping: {actual} (expected: {expected})");
        if (actual != expected)
        {
            DebugLog.Log(ScenarioLog, "  FAIL: Loop toggle did not change state");
            _failures++;
        }
        else
        {
            DebugLog.Log(ScenarioLog, "  PASS");
        }
        // Toggle back to original
        ue.ToggleLoop();
        _phase = 4;
        _tickCount = 0;
    }

    private void RunTogglePlayPauseTest()
    {
        if (_tickCount < 3) return;
        var ue = UnitEditor!;
        bool beforePlay = ue.PreviewPlaying;
        DebugLog.Log(ScenarioLog, $"Test: Toggle Play/Pause (before: {beforePlay})");
        ue.TogglePlayPause();
        bool afterPlay = ue.PreviewPlaying;
        DebugLog.Log(ScenarioLog, $"  After TogglePlayPause — Playing: {afterPlay} (expected: {!beforePlay})");
        if (afterPlay != !beforePlay)
        {
            DebugLog.Log(ScenarioLog, "  FAIL: Play/Pause toggle did not change state");
            _failures++;
        }
        else
        {
            DebugLog.Log(ScenarioLog, "  PASS");
        }
        _phase = 5;
        _tickCount = 0;
    }

    private void RunStepForwardTest()
    {
        if (_tickCount < 3) return;
        var ue = UnitEditor!;
        float beforeTime = ue.PreviewAnimTime;
        DebugLog.Log(ScenarioLog, $"Test: Step Forward (before time: {beforeTime:F2})");
        ue.StepForward();
        float afterTime = ue.PreviewAnimTime;
        bool playing = ue.PreviewPlaying;
        DebugLog.Log(ScenarioLog, $"  After StepForward — AnimTime: {afterTime:F2}, Playing: {playing}");
        // StepForward should pause and advance time
        if (playing)
        {
            DebugLog.Log(ScenarioLog, "  FAIL: StepForward should pause playback");
            _failures++;
        }
        else if (afterTime <= beforeTime && beforeTime > 0)
        {
            // Allow time to wrap around (e.g., if at end of animation)
            DebugLog.Log(ScenarioLog, "  WARN: AnimTime did not advance (may have wrapped)");
        }
        else
        {
            DebugLog.Log(ScenarioLog, "  PASS");
        }
        _phase = 6;
        _tickCount = 0;
    }

    private void RunStepBackTest()
    {
        if (_tickCount < 3) return;
        var ue = UnitEditor!;
        // First step forward to ensure we have somewhere to step back from
        ue.StepForward();
        float beforeTime = ue.PreviewAnimTime;
        DebugLog.Log(ScenarioLog, $"Test: Step Back (before time: {beforeTime:F2})");
        ue.StepBack();
        float afterTime = ue.PreviewAnimTime;
        bool playing = ue.PreviewPlaying;
        DebugLog.Log(ScenarioLog, $"  After StepBack — AnimTime: {afterTime:F2}, Playing: {playing}");
        if (playing)
        {
            DebugLog.Log(ScenarioLog, "  FAIL: StepBack should pause playback");
            _failures++;
        }
        else if (afterTime > beforeTime)
        {
            DebugLog.Log(ScenarioLog, "  FAIL: StepBack should not advance time");
            _failures++;
        }
        else
        {
            DebugLog.Log(ScenarioLog, "  PASS");
        }
        _phase = 7;
        _tickCount = 0;
    }

    private void RunResumePlaybackTest()
    {
        if (_tickCount < 3) return;
        var ue = UnitEditor!;
        // Ensure we can resume playback after stepping
        bool beforePlay = ue.PreviewPlaying;
        DebugLog.Log(ScenarioLog, $"Test: Resume playback (before: {beforePlay})");
        if (!beforePlay)
            ue.TogglePlayPause();
        bool afterPlay = ue.PreviewPlaying;
        DebugLog.Log(ScenarioLog, $"  After resume — Playing: {afterPlay}");
        if (!afterPlay)
        {
            DebugLog.Log(ScenarioLog, "  FAIL: Could not resume playback");
            _failures++;
        }
        else
        {
            DebugLog.Log(ScenarioLog, "  PASS");
        }
        _phase = 8;
        _tickCount = 0;
    }

    public override bool IsComplete => _complete;

    public override int OnComplete(Simulation sim)
    {
        string result = _failures == 0 ? "ALL PASSED" : $"{_failures} FAILURE(S)";
        DebugLog.Log(ScenarioLog, $"Animation Button Test complete — {result}");
        return _failures == 0 ? 0 : 1;
    }
}

/// <summary>
/// Thin accessor wrapper so the scenario can call UnitEditorWindow methods
/// without directly depending on the editor's internal state.
/// Game1 creates this and assigns it to the scenario.
/// </summary>
public class UnitEditorAccessor
{
    private readonly Necroking.Editor.UnitEditorWindow _editor;

    public UnitEditorAccessor(Necroking.Editor.UnitEditorWindow editor)
    {
        _editor = editor;
    }

    public bool PreviewPlaying => _editor.PreviewPlaying;
    public bool PreviewLooping => _editor.PreviewLooping;
    public float PreviewAnimTime => _editor.PreviewAnimTime;
    public bool HasSelection => _editor.HasSelection;
    public void TogglePlayPause() => _editor.TogglePlayPause();
    public void ToggleLoop() => _editor.ToggleLoop();
    public void StepForward() => _editor.StepForward();
    public void StepBack() => _editor.StepBack();
}
