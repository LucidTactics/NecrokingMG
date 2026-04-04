using Necroking.GameSystems;

namespace Necroking.Scenario.Scenarios;

/// <summary>
/// Opens the spell editor, selects "Lucky" (a Buff spell), and takes a screenshot
/// to verify the reflection-based property rendering looks correct.
/// </summary>
public class SpellEditorScenario : UIScenarioBase
{
    public override string Name => "spell_editor";
    public override bool IsComplete => _complete;
    private bool _complete;
    private int _tick;

    public override void OnInit(Simulation sim)
    {
        RequestedMenuState = "SpellEditor";
    }

    public override void OnTick(Simulation sim, float dt)
    {
        _tick++;
        if (_tick == 3) RequestSelectSpellByName = "Lucky";
        if (_tick == 10) DeferredScreenshot = "spell_editor_lucky";
        if (_tick > 12) _complete = true;
    }

    public override int OnComplete(Simulation sim) => 0;
}
