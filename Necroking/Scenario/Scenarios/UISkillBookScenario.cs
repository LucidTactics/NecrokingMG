using Necroking.Core;
using Necroking.GameSystems;

namespace Necroking.Scenario.Scenarios;

/// <summary>
/// Visual smoke test for the new tabbed SkillBookOverlay:
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
    public override bool WantsWidgetRenderer => true; // grimoire chrome (frame/ribbon/tiles)

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
                if (SkillBookOverlay?.IsVisible == true && _phaseT > 0.4f)
                {
                    DebugLog.Log(ScenarioLog, "Phase 0: panel visible, default tab");
                    SkillBookOverlay.SetActiveTab(0); // Potions
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
                    SkillBookOverlay!.SetActiveTab(1);
                    DeferredScreenshot = "ui_skillbook_necromancy";
                    _waitingForScreenshot = true;
                    _phase = 2;
                }
                break;

            case 2:
                if (_phaseT > 0.3f)
                {
                    DebugLog.Log(ScenarioLog, "Phase 2: try to re-learn raise_skeleton (no-op)");
                    SkillBookOverlay!.TryLearnById("raise_skeleton");
                    DebugLog.Log(ScenarioLog, "Phase 2: learn bone_knight (cost: 4 MagicMushroom)");
                    bool ok = SkillBookOverlay.TryLearnById("bone_knight");
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
                    SkillBookOverlay!.SetActiveTab(2);
                    DeferredScreenshot = "ui_skillbook_magic";
                    _waitingForScreenshot = true;
                    _phase = 4;
                }
                break;

            case 4:
                if (_phaseT > 0.3f)
                {
                    DebugLog.Log(ScenarioLog, "Phase 4: switch to Metamorphosis tab");
                    SkillBookOverlay!.SetActiveTab(3);
                    DeferredScreenshot = "ui_skillbook_metamorphosis";
                    _waitingForScreenshot = true;
                    _phase = 5;
                }
                break;

            case 5:
                if (_phaseT > 0.3f)
                {
                    DebugLog.Log(ScenarioLog, "Phase 5: tally 2 monster kills, hover monster_summoner -> met (2/1)");
                    SkillBookOverlay!.SetActiveTab(1);
                    SkillBookOverlay.TallyEventForTest("monster_kill", 2);
                    SkillBookOverlay.SetHoverForTest("monster_summoner");
                    DeferredScreenshot = "ui_skillbook_tooltip";
                    _waitingForScreenshot = true;
                    _phase = 6;
                }
                break;

            case 6:
                if (_phaseT > 0.3f)
                {
                    DebugLog.Log(ScenarioLog, "Phase 6: hover improved_monstrology -> skillpoints ticker cost");
                    SkillBookOverlay!.SetHoverForTest("improved_monstrology");
                    DeferredScreenshot = "ui_skillbook_tooltip_locked";
                    _waitingForScreenshot = true;
                    _phase = 7;
                }
                break;

            case 7:
                if (_phaseT > 0.3f)
                {
                    DebugLog.Log(ScenarioLog, "Phase 7: learn monster_summoner, hover it -> effects show on a LEARNED node");
                    bool learned = SkillBookOverlay!.TryLearnById("monster_summoner");
                    DebugLog.Log(ScenarioLog, $"  monster_summoner learned={learned}");
                    SkillBookOverlay.SetHoverForTest("monster_summoner");
                    DeferredScreenshot = "ui_skillbook_tooltip_learned";
                    _waitingForScreenshot = true;
                    _phase = 8;
                }
                break;

            case 8:
                if (_phaseT > 0.3f)
                {
                    SkillBookOverlay!.SetHoverForTest(null);
                    SkillBookOverlay.SetActiveTab(0); // Potions
                    _metaIdx = FindTabIndex("metamorphosis");
                    bool lockedNow = !SkillBookOverlay.IsTabUnlockedForTest(_metaIdx);
                    DebugLog.Log(ScenarioLog, $"Phase 8: metamorphosis(idx={_metaIdx}) hidden before seeing potion = {lockedNow}");
                    if (!lockedNow) { DebugLog.Log(ScenarioLog, "FAIL: metamorphosis tab visible too early"); _failCode = 20; }
                    DeferredScreenshot = "ui_skillbook_tabs_locked";
                    _waitingForScreenshot = true;
                    _phase = 9;
                }
                break;

            case 9:
                if (_phaseT > 0.3f)
                {
                    DebugLog.Log(ScenarioLog, "Phase 9: see potion_death_evolution -> metamorphosis tab unlocks");
                    Inventory?.AddItem("potion_death_evolution", 1);
                    bool unlockedNow = SkillBookOverlay!.IsTabUnlockedForTest(_metaIdx);
                    DebugLog.Log(ScenarioLog, $"  metamorphosis unlocked after seeing potion = {unlockedNow}");
                    if (!unlockedNow) { DebugLog.Log(ScenarioLog, "FAIL: metamorphosis tab still hidden after seeing potion"); _failCode = 21; }
                    DeferredScreenshot = "ui_skillbook_tabs_unlocked";
                    _waitingForScreenshot = true;
                    _phase = 10;
                }
                break;

            case 10:
                if (_phaseT > 0.3f)
                {
                    _lichIdx = FindTabIndex("lich");
                    DebugLog.Log(ScenarioLog, $"Phase 10: lich tab dynamically loaded at idx={_lichIdx} (total tabs={Necroking.Data.SkillBookDefs.Tabs.Count})");
                    if (_lichIdx < 0) { DebugLog.Log(ScenarioLog, "FAIL: lich.json not discovered dynamically"); _failCode = 22; }
                    else if (SkillBookOverlay!.IsTabUnlockedForTest(_lichIdx))
                    { DebugLog.Log(ScenarioLog, "FAIL: lich tab visible before morphing"); _failCode = 23; }
                    DeferredScreenshot = "ui_skillbook_lich_locked";
                    _waitingForScreenshot = true;
                    _phase = 11;
                }
                break;

            case 11:
                if (_phaseT > 0.3f)
                {
                    DebugLog.Log(ScenarioLog, "Phase 11: morph into lich -> lich tab unlocks");
                    SkillBookOverlay!.SetPassiveForTest("morphed:lich");
                    bool unlocked = SkillBookOverlay.IsTabUnlockedForTest(_lichIdx);
                    DebugLog.Log(ScenarioLog, $"  lich unlocked after morph = {unlocked}");
                    if (!unlocked) { DebugLog.Log(ScenarioLog, "FAIL: lich tab still hidden after morph"); _failCode = 24; }
                    DeferredScreenshot = "ui_skillbook_lich_unlocked";
                    _waitingForScreenshot = true;
                    _phase = 12;
                }
                break;

            case 12:
                if (_phaseT > 0.3f)
                {
                    DebugLog.Log(ScenarioLog, "All phases complete");
                    _complete = true;
                }
                break;
        }
    }

    private int _metaIdx = -1;
    private int _lichIdx = -1;

    private static int FindTabIndex(string id)
    {
        var tabs = Necroking.Data.SkillBookDefs.Tabs;
        for (int i = 0; i < tabs.Count; i++) if (tabs[i].Id == id) return i;
        return -1;
    }

    public override bool IsComplete => _complete;

    public override int OnComplete(Simulation sim)
    {
        DebugLog.Log(ScenarioLog, $"Final result: {(_failCode == 0 ? "PASS" : $"FAIL ({_failCode})")}");
        return _failCode;
    }
}
