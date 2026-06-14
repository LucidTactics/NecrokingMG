using Microsoft.Xna.Framework;
using Necroking.Core;
using Necroking.GameSystems;

namespace Necroking.Scenario.Scenarios;

/// <summary>
/// [UI test] Opens the skill book and screenshots each skin (0 = original flat,
/// 1..10 reuse grimoire / unit-sheet / tooltip chrome) for design review.
/// Screenshots ui_skillbook_skin_00.png .. _10.png. Cycle in-game with Shift+B.
/// </summary>
public class UISkillBookSkinsScenario : UIScenarioBase
{
    public override string Name => "UISkillBookSkins";
    public override bool WantsWidgetRenderer => true; // skins use harmonized chrome

    private const int Count = 5; // 5 tab variants of the chosen base design
    private int _skin;
    private int _phase;
    private float _t;
    private bool _complete;

    public override void OnInit(Simulation sim)
    {
        DebugLog.Clear(ScenarioLog);
        DebugLog.Log(ScenarioLog, "=== UI Skill Book skin sweep ===");
        ZoomOnLocation(10f, 10f, 32f);
        BackgroundColor = new Color(40, 46, 56);
        RequestOpenSkillBook = true;
        // Stock inventory so node affordability colours are interesting.
        Inventory?.AddItem("MagicMushroom", 8);
        Inventory?.AddItem("Mushroom", 5);
        Inventory?.AddItem("Ghostcap", 4);
    }

    public override void OnTick(Simulation sim, float dt)
    {
        if (_complete) return;
        if (SkillBookPanel?.IsVisible != true) return; // wait for the panel to open
        _t += dt;
        if (_phase == 0 && _t > 0.4f)
        {
            SkillBookPanel!.SetSkin(_skin);
            DeferredScreenshot = $"ui_skillbook_skin_{_skin:D2}";
            DebugLog.Log(ScenarioLog, $"shot skin {_skin}");
            _phase = 1;
        }
        else if (_phase == 1 && DeferredScreenshot == null)
        {
            _skin++;
            if (_skin >= Count) _complete = true;
            else { _phase = 0; _t = 0f; }
        }
    }

    public override bool IsComplete => _complete;
    public override int OnComplete(Simulation sim) => 0;
}
