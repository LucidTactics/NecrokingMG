using Necroking.Core;
using Necroking.GameSystems;

namespace Necroking.Scenario.Scenarios;

/// <summary>
/// Comprehensive UI test scenario that screenshots every editor state:
/// Unit Editor (selected, weapon sub-editor), Spell Editor (selected, buff manager),
/// Map Editor (all 7 tabs), UI Editor (all 3 tabs).
/// Produces 14 screenshots for visual verification of layout, overflow, and accessibility.
/// </summary>
public class EditorUITestScenario : UIScenarioBase
{
    public override string Name => "ui_test";
    public override bool WantsGround => true;

    private bool _complete;
    private int _stepIdx;
    private int _tickCount;
    private int _phase; // 0=open editor, 1=select first, 2=set tab/popup, 3=wait, 4=screenshot
    private bool _screenshotPending;

    // Each step: editor to open, screenshot name, optional tab/popup action
    private static readonly Step[] Steps = new Step[]
    {
        // --- Unit Editor ---
        new("UnitEditor", "ui_unit_selected", null, StepAction.None),
        new("UnitEditor", "ui_unit_weapon_sub", null, StepAction.OpenWeaponSub),

        // --- Spell Editor ---
        new("SpellEditor", "ui_spell_selected", null, StepAction.None),
        new("SpellEditor", "ui_spell_buff_mgr", null, StepAction.OpenBuffManager),

        // --- Map Editor tabs ---
        new("MapEditor", "ui_map_ground", "Ground", StepAction.MapTab),
        new("MapEditor", "ui_map_grass", "Grass", StepAction.MapTab),
        new("MapEditor", "ui_map_objects", "Objects", StepAction.MapTab),
        new("MapEditor", "ui_map_walls", "Walls", StepAction.MapTab),
        new("MapEditor", "ui_map_roads", "Roads", StepAction.MapTab),
        new("MapEditor", "ui_map_regions", "Regions", StepAction.MapTab),
        new("MapEditor", "ui_map_triggers", "Triggers", StepAction.MapTab),

        // --- UI Editor tabs ---
        new("UIEditor", "ui_editor_nineslice", "NineSlices", StepAction.UITab),
        new("UIEditor", "ui_editor_elements", "Elements", StepAction.UITab),
        new("UIEditor", "ui_editor_widgets", "Widgets", StepAction.UITab),
    };

    private string? _lastEditor; // track which editor is currently open to avoid re-opening

    public override void OnInit(Simulation sim)
    {
        DebugLog.Clear(ScenarioLog);
        DebugLog.Log(ScenarioLog, "=== Editor UI Test Scenario ===");
        DebugLog.Log(ScenarioLog, $"Total steps: {Steps.Length}");
    }

    public override void OnTick(Simulation sim, float dt)
    {
        if (_complete) return;

        // Wait for screenshot to be consumed
        if (_screenshotPending)
        {
            if (DeferredScreenshot == null)
            {
                DebugLog.Log(ScenarioLog, $"Screenshot captured: {Steps[_stepIdx].Filename}");
                _screenshotPending = false;
                _stepIdx++;
                _phase = 0;
                _tickCount = 0;
            }
            return;
        }

        _tickCount++;

        // All done?
        if (_stepIdx >= Steps.Length)
        {
            RequestedMenuState = "None";
            _complete = true;
            return;
        }

        var step = Steps[_stepIdx];

        switch (_phase)
        {
            case 0: // Open the editor (skip if already open)
                if (_lastEditor == step.Editor)
                {
                    _phase = 1;
                    _tickCount = 0;
                    goto case 1;
                }
                DebugLog.Log(ScenarioLog, $"[{_stepIdx}] Opening {step.Editor}");
                RequestedMenuState = step.Editor;
                _lastEditor = step.Editor;
                _phase = 1;
                _tickCount = 0;
                break;

            case 1: // Wait 3 ticks, then select first item
                if (_tickCount < 3) return;
                // Only select first for Unit and Spell editors, and UI editor
                if (step.Editor is "UnitEditor" or "SpellEditor" or "UIEditor")
                {
                    DebugLog.Log(ScenarioLog, $"[{_stepIdx}] Selecting first item");
                    RequestSelectFirst = true;
                }
                _phase = 2;
                _tickCount = 0;
                break;

            case 2: // Apply tab switch or popup action
                if (_tickCount < 3) return;
                switch (step.Action)
                {
                    case StepAction.MapTab:
                        DebugLog.Log(ScenarioLog, $"[{_stepIdx}] Switching map tab to {step.TabName}");
                        RequestedMapTab = step.TabName;
                        break;
                    case StepAction.UITab:
                        DebugLog.Log(ScenarioLog, $"[{_stepIdx}] Switching UI tab to {step.TabName}");
                        RequestedUITab = step.TabName;
                        // Also select first item after tab switch
                        RequestSelectFirst = true;
                        break;
                    case StepAction.OpenWeaponSub:
                        DebugLog.Log(ScenarioLog, $"[{_stepIdx}] Opening weapon sub-editor");
                        RequestOpenWeaponSub = true;
                        break;
                    case StepAction.OpenBuffManager:
                        DebugLog.Log(ScenarioLog, $"[{_stepIdx}] Opening buff manager");
                        RequestOpenBuffManager = true;
                        break;
                    case StepAction.None:
                        DebugLog.Log(ScenarioLog, $"[{_stepIdx}] No additional action");
                        break;
                }
                _phase = 3;
                _tickCount = 0;
                break;

            case 3: // Wait for rendering to settle
                if (_tickCount < 10) return;
                _phase = 4;
                break;

            case 4: // Take screenshot
                DebugLog.Log(ScenarioLog, $"[{_stepIdx}] Taking screenshot: {step.Filename}");
                DeferredScreenshot = step.Filename;
                _screenshotPending = true;
                break;
        }
    }

    public override bool IsComplete => _complete;

    public override int OnComplete(Simulation sim)
    {
        DebugLog.Log(ScenarioLog, $"Editor UI test complete — {_stepIdx} screenshots taken");
        return 0;
    }

    // --- Step definition ---

    private enum StepAction { None, MapTab, UITab, OpenWeaponSub, OpenBuffManager }

    private readonly struct Step
    {
        public readonly string Editor;
        public readonly string Filename;
        public readonly string? TabName;
        public readonly StepAction Action;

        public Step(string editor, string filename, string? tabName, StepAction action)
        {
            Editor = editor;
            Filename = filename;
            TabName = tabName;
            Action = action;
        }
    }
}
