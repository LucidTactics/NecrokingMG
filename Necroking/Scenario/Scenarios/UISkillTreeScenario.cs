using Necroking.Core;
using Necroking.GameSystems;

namespace Necroking.Scenario.Scenarios;

/// <summary>
/// Verifies the skill tree panel:
///  1. Opens via scenario request
///  2. Renders without crashing (sealed nodes, locked icons)
///  3. Allocating a root node doesn't crash and updates state
///  4. Clicking a sealed node fires the warning toast without crashing
///     (this was the original repro path: em-dash glyph in the Warn message)
/// </summary>
public class UISkillTreeScenario : UIScenarioBase
{
    public override string Name => "UISkillTree";

    private int _phase;
    private float _phaseT;
    private bool _waitingForScreenshot;
    private bool _complete;
    private int _failCode;

    public override void OnInit(Simulation sim)
    {
        DebugLog.Clear(ScenarioLog);
        DebugLog.Log(ScenarioLog, "=== UI Skill Tree Scenario ===");
        ZoomOnLocation(10f, 10f, 32f);
        RequestOpenSkillTree = true;
    }

    public override void OnTick(Simulation sim, float dt)
    {
        if (_complete) return;

        if (_waitingForScreenshot)
        {
            if (DeferredScreenshot == null) { _waitingForScreenshot = false; _phaseT = 0; }
            return;
        }

        _phaseT += dt;

        switch (_phase)
        {
            // Phase 0: wait for panel to actually be visible, then snapshot.
            case 0:
                if (SkillTreePanel?.IsVisible == true && _phaseT > 0.4f)
                {
                    DebugLog.Log(ScenarioLog, "Phase 0: panel visible -- screenshot empty state");
                    DeferredScreenshot = "ui_skill_tree_empty";
                    _waitingForScreenshot = true;
                    _phase = 1;
                }
                else if (_phaseT > 4f)
                {
                    DebugLog.Log(ScenarioLog, "FAIL: panel never opened");
                    _failCode = 10; _complete = true;
                }
                break;

            // Phase 1: click a sealed (locked) node -- triggers Warn path.
            case 1:
                DebugLog.Log(ScenarioLog, "Phase 1: simulate clicking a locked node");
                SkillTreePanel!.TryClickLocked();
                _phase = 2;
                break;

            // Phase 2: allocate root nodes for each school
            case 2:
                if (_phaseT > 0.3f)
                {
                    DebugLog.Log(ScenarioLog, "Phase 2: allocate b_rattle, s_siphon, h_veil");
                    bool a = SkillTreePanel!.TryAllocate("b_rattle");
                    bool b = SkillTreePanel.TryAllocate("s_siphon");
                    bool c = SkillTreePanel.TryAllocate("h_veil");
                    DebugLog.Log(ScenarioLog, $"  TryAllocate results: bone={a} soul={b} shadow={c}");
                    if (!(a && b && c)) { _failCode = 11; _complete = true; return; }
                    _phase = 3;
                }
                break;

            // Phase 3: allocate tier-1 nodes (now unlocked) and screenshot
            case 3:
                if (_phaseT > 0.3f)
                {
                    SkillTreePanel!.TryAllocate("b_shard");
                    SkillTreePanel.TryAllocate("b_brittle");
                    SkillTreePanel.TryAllocate("h_blight");
                    SkillTreePanel.TryAllocate("h_ember");
                    DebugLog.Log(ScenarioLog, "Phase 3: tier-1 allocations made -- screenshot");
                    DeferredScreenshot = "ui_skill_tree_allocated";
                    _waitingForScreenshot = true;
                    _phase = 4;
                }
                break;

            case 4:
                if (_phaseT > 0.3f)
                {
                    DebugLog.Log(ScenarioLog, "All phases complete");
                    _complete = true;
                }
                break;
        }
    }

    public override bool IsComplete => _complete;

    public override int OnComplete(Simulation sim)
    {
        DebugLog.Log(ScenarioLog, $"Final result: {(_failCode == 0 ? "PASS" : $"FAIL ({_failCode})")}");
        return _failCode;
    }
}
