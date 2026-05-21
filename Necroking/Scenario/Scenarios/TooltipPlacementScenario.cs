using Necroking.Core;
using Necroking.GameSystems;

namespace Necroking.Scenario.Scenarios;

/// <summary>
/// Visual test for SkillBookPanel tooltip placement. Hovers each node we care
/// about in the Monstrology tab one by one and screenshots, so a reviewer can
/// confirm the tooltip never overlaps the hovered node's own footer (the
/// "0/5 monstrology pts" line).
///
/// Tooltip positioning policy:
///   1. Prefer below the node.
///   2. Fall back to above / right / left as the panel edge crowds it.
///   3. Final clamp keeps the tooltip inside the panel even if the chosen
///      side doesn't perfectly center.
/// </summary>
public class TooltipPlacementScenario : UIScenarioBase
{
    public override string Name => "UITooltipPlacement";

    private int _phase;
    private float _phaseT;
    private bool _waitingForScreenshot;
    private bool _complete;

    // Sample the corners + interior of the monstrology tree so we exercise
    // every fallback branch: roots near the top edge (above fails → below),
    // left-edge nodes (left fails → below/right), bottom-row (below fails →
    // above), wide internal nodes (any side works).
    private static readonly string[] HoverNodes =
    {
        "monster_summoner",         // top center
        "boar_charge",              // left-edge, deep
        "wolf_lunge",               // right-edge, mid
        "monsterous_legion",        // bottom center
        "improved_corpse_eating",   // bottom-most, may force above
    };

    public override void OnInit(Simulation sim)
    {
        DebugLog.Clear(ScenarioLog);
        DebugLog.Log(ScenarioLog, "=== UI Tooltip Placement Scenario ===");
        ZoomOnLocation(10f, 10f, 32f);
        RequestOpenSkillBook = true;
    }

    public override void OnTick(Simulation sim, float dt)
    {
        if (_complete) return;
        if (SkillBookPanel == null) { _complete = true; return; }
        if (_waitingForScreenshot)
        {
            if (DeferredScreenshot == null) { _waitingForScreenshot = false; _phaseT = 0; }
            return;
        }
        _phaseT += dt;

        // Phase 0 — wait for panel to open, then switch to Monstrology (tab 1).
        if (_phase == 0)
        {
            if (!SkillBookPanel.IsVisible) return;
            if (_phaseT < 0.4f) return;
            SkillBookPanel.SetActiveTab(1); // Monstrology
            _phase = 1;
            _phaseT = 0;
            return;
        }

        int hoverIdx = _phase - 1;
        if (hoverIdx >= HoverNodes.Length) { _complete = true; return; }
        if (_phaseT < 0.25f) return;

        var nodeId = HoverNodes[hoverIdx];
        SkillBookPanel.DebugSetHoverSkill(nodeId);
        DeferredScreenshot = $"ui_tooltip_{nodeId}";
        _waitingForScreenshot = true;
        DebugLog.Log(ScenarioLog, $"Phase {_phase}: hover '{nodeId}' → screenshot");
        _phase++;
    }

    public override bool IsComplete => _complete;
    public override int OnComplete(Simulation sim) => 0;
}
