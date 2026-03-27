using Necroking.Core;
using Necroking.GameSystems;

namespace Necroking.Scenario.Scenarios;

/// <summary>
/// Opens each editor in sequence, selects the first item, and takes screenshots
/// for comparison with C++ editors.
/// Produces: editor_unit.png, editor_spell.png, editor_map.png, editor_ui.png
/// </summary>
public class EditorScreenshotScenario : UIScenarioBase
{
    public override string Name => "editor_screenshots";
    public override bool WantsGround => true;

    private bool _complete;
    private int _editorIdx;
    private int _tickCount;
    private bool _opened;
    private bool _selected;
    private bool _screenshotRequested;

    private static readonly (string menuState, string filename)[] Editors = new[]
    {
        ("UnitEditor", "editor_unit"),
        ("SpellEditor", "editor_spell"),
        ("MapEditor", "editor_map"),
        ("UIEditor", "editor_ui"),
    };

    public override void OnInit(Simulation sim)
    {
        DebugLog.Clear(ScenarioLog);
        DebugLog.Log(ScenarioLog, "=== Editor Screenshot Scenario ===");
    }

    public override void OnTick(Simulation sim, float dt)
    {
        if (_complete) return;
        _tickCount++;

        // Wait for screenshot to be consumed by Draw
        if (_screenshotRequested)
        {
            if (DeferredScreenshot == null)
            {
                DebugLog.Log(ScenarioLog, $"Screenshot captured: {Editors[_editorIdx].filename}");
                _screenshotRequested = false;
                _editorIdx++;
                _opened = false;
                _selected = false;
                _tickCount = 0;
            }
            return;
        }

        // All done?
        if (_editorIdx >= Editors.Length)
        {
            RequestedMenuState = "None";
            _complete = true;
            return;
        }

        var (menuState, filename) = Editors[_editorIdx];

        // Step 1: Open the editor (once)
        if (!_opened)
        {
            DebugLog.Log(ScenarioLog, $"Opening {menuState}");
            RequestedMenuState = menuState;
            _opened = true;
            _tickCount = 0;
            return;
        }

        // Step 2: After 3 ticks, select the first item
        if (!_selected && _tickCount >= 3)
        {
            DebugLog.Log(ScenarioLog, $"Selecting first item in {menuState}");
            RequestSelectFirst = true;
            _selected = true;
            _tickCount = 0;
            return;
        }

        // Step 3: Wait 10 more ticks for the detail panel to render with selection
        if (_selected && _tickCount < 10)
            return;

        if (!_selected)
            return; // still waiting for step 2

        // Step 4: Request screenshot
        DebugLog.Log(ScenarioLog, $"Taking screenshot: {filename}");
        DeferredScreenshot = filename;
        _screenshotRequested = true;
    }

    public override bool IsComplete => _complete;

    public override int OnComplete(Simulation sim)
    {
        DebugLog.Log(ScenarioLog, $"Editor screenshots complete — {_editorIdx} screenshots taken");
        return 0;
    }
}
