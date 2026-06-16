using Necroking.Core;
using Necroking.GameSystems;

namespace Necroking.Scenario.Scenarios;

/// <summary>
/// Editor-side verification for the skill-book tabs: opens the UI Editor, selects
/// the SkillBookWindow widget, and screenshots its preview. Confirms the per-tab
/// labels (TabBar child overrides → SkillBookTab "Name") render in the editor's own
/// renderer (DrawWidgetChild), not just at runtime.
/// </summary>
public class EditorSkillBookPreviewScenario : UIScenarioBase
{
    public override string Name => "ui_skillbook_editor";

    private bool _complete;
    private int _phase;
    private int _ticks;
    private bool _shotPending;

    public override void OnInit(Simulation sim)
    {
        DebugLog.Clear(ScenarioLog);
        DebugLog.Log(ScenarioLog, "=== Editor SkillBook Preview Scenario ===");
    }

    public override void OnTick(Simulation sim, float dt)
    {
        if (_complete) return;
        if (_shotPending)
        {
            if (DeferredScreenshot == null) { _shotPending = false; _ticks = 0; }
            return;
        }
        if (_phase >= 5) { _complete = true; RequestedMenuState = "None"; return; }
        _ticks++;
        switch (_phase)
        {
            case 0: // open the UI editor
                DebugLog.Log(ScenarioLog, "Opening UIEditor");
                RequestedMenuState = "UIEditor";
                _phase = 1; _ticks = 0;
                break;
            case 1: // select the SkillBookWindow widget
                if (_ticks < 4) return;
                DebugLog.Log(ScenarioLog, "Selecting SkillBookWindow");
                RequestSelectUIWidgetById = "SkillBookWindow";
                _phase = 2; _ticks = 0;
                break;
            case 2: // let it settle, then screenshot the window
                if (_ticks < 10) return;
                DebugLog.Log(ScenarioLog, "Screenshot: ui_skillbook_editor_preview");
                DeferredScreenshot = "ui_skillbook_editor_preview";
                _shotPending = true;
                _phase = 3; _ticks = 0;
                break;
            case 3: // select the SkillTile widget
                if (_ticks < 4) return;
                DebugLog.Log(ScenarioLog, "Selecting SkillTile");
                RequestSelectUIWidgetById = "SkillTile";
                _phase = 4; _ticks = 0;
                break;
            case 4: // screenshot the tile
                if (_ticks < 10) return;
                DebugLog.Log(ScenarioLog, "Screenshot: ui_skilltile_editor_preview");
                DeferredScreenshot = "ui_skilltile_editor_preview";
                _shotPending = true;
                _phase = 5;
                break;
        }
    }

    public override bool IsComplete => _complete;

    public override int OnComplete(Simulation sim)
    {
        DebugLog.Log(ScenarioLog, "Editor SkillBook preview complete");
        return 0;
    }
}
