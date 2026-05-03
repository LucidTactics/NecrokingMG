using Necroking.Core;
using Necroking.GameSystems;

namespace Necroking.Scenario.Scenarios;

/// <summary>
/// Visual smoke test for the new tabbed SkillBookPanel:
///  1. Open it, screenshot the default tab (Potions).
///  2. Switch to Necromancy, screenshot — root "Raise Skeleton" should already be
///     learned (StartLearned=true) and one of its children (Bone Knight) should be
///     learnable thanks to the starting MagicMushrooms in the inventory.
///  3. Drop a bunch of items into the inventory and tally a few raise_corpse events
///     so a clear mix of affordable / unaffordable / locked nodes is visible.
///  4. Try to learn "raise_skeleton" again (already learned — should toast & no-op).
///  5. Learn "bone_knight" via TryLearnById, screenshot the resulting Learned state.
/// </summary>
public class UISkillBookScenario : UIScenarioBase
{
    public override string Name => "UISkillBook";

    private int _phase;
    private float _phaseT;
    private bool _waitingForScreenshot;
    private bool _complete;
    private int _failCode;

    public override void OnInit(Simulation sim)
    {
        DebugLog.Clear(ScenarioLog);
        DebugLog.Log(ScenarioLog, "=== UI Skill Book Scenario ===");
        ZoomOnLocation(10f, 10f, 32f);
        RequestOpenSkillBook = true;

        // Stock the inventory so the cost colors are interesting in the screenshots.
        Inventory?.AddItem("Mushroom", 5);
        Inventory?.AddItem("MagicMushroom", 8);
        Inventory?.AddItem("PoisonMushroom", 3);
        Inventory?.AddItem("Rotgill", 2);
        Inventory?.AddItem("Ghostcap", 4);
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
            case 0:
                if (SkillBookPanel?.IsVisible == true && _phaseT > 0.4f)
                {
                    DebugLog.Log(ScenarioLog, "Phase 0: panel visible, default tab");
                    SkillBookPanel.SetActiveTab(0); // Potions
                    DeferredScreenshot = "ui_skillbook_potions";
                    _waitingForScreenshot = true;
                    _phase = 1;
                }
                else if (_phaseT > 4f)
                {
                    DebugLog.Log(ScenarioLog, "FAIL: panel never opened");
                    _failCode = 10; _complete = true;
                }
                break;

            case 1:
                if (_phaseT > 0.3f)
                {
                    DebugLog.Log(ScenarioLog, "Phase 1: switch to Necromancy tab");
                    SkillBookPanel!.SetActiveTab(1);
                    DeferredScreenshot = "ui_skillbook_necromancy";
                    _waitingForScreenshot = true;
                    _phase = 2;
                }
                break;

            case 2:
                if (_phaseT > 0.3f)
                {
                    DebugLog.Log(ScenarioLog, "Phase 2: try to re-learn raise_skeleton (no-op)");
                    SkillBookPanel!.TryLearnById("raise_skeleton");
                    DebugLog.Log(ScenarioLog, "Phase 2: learn bone_knight (cost: 4 MagicMushroom)");
                    bool ok = SkillBookPanel.TryLearnById("bone_knight");
                    DebugLog.Log(ScenarioLog, $"  bone_knight learned={ok}");
                    DeferredScreenshot = "ui_skillbook_learned";
                    _waitingForScreenshot = true;
                    _phase = 3;
                }
                break;

            case 3:
                if (_phaseT > 0.3f)
                {
                    DebugLog.Log(ScenarioLog, "Phase 3: switch to Magic tab");
                    SkillBookPanel!.SetActiveTab(2);
                    DeferredScreenshot = "ui_skillbook_magic";
                    _waitingForScreenshot = true;
                    _phase = 4;
                }
                break;

            case 4:
                if (_phaseT > 0.3f)
                {
                    DebugLog.Log(ScenarioLog, "Phase 4: switch to Metamorphosis tab");
                    SkillBookPanel!.SetActiveTab(3);
                    DeferredScreenshot = "ui_skillbook_metamorphosis";
                    _waitingForScreenshot = true;
                    _phase = 5;
                }
                break;

            case 5:
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
