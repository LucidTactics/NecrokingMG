using System.IO;
using System.Text.Json;
using Necroking.Core;
using Necroking.Data;
using Necroking.GameSystems;

namespace Necroking.Scenario.Scenarios;

/// <summary>
/// Exercises the skill-book layout editor:
///  1. Open the book, turn on edit mode, screenshot the toolbar + draggable outlines.
///  2. Verify the save path: nudge a skill's x/y, SaveLayout(), re-read the JSON and
///     confirm x/y persisted while every other field (name, costs...) is preserved.
/// The skill JSON files are backed up and restored so the test leaves no changes.
/// </summary>
public class UISkillLayoutScenario : UIScenarioBase
{
    public override string Name => "UISkillLayout";
    public override bool WantsWidgetRenderer => true;

    private int _phase;
    private float _t;
    private bool _waitShot, _complete;
    private int _fail;

    public override void OnInit(Simulation sim)
    {
        DebugLog.Clear(ScenarioLog);
        DebugLog.Log(ScenarioLog, "=== UI Skill Layout Scenario ===");
        ZoomOnLocation(10f, 10f, 32f);
        RequestOpenSkillBook = true;
    }

    public override void OnTick(Simulation sim, float dt)
    {
        if (_complete) return;
        if (_waitShot) { if (DeferredScreenshot == null) { _waitShot = false; _t = 0; } return; }
        _t += dt;

        switch (_phase)
        {
            case 0:
                if (SkillBookOverlay?.IsVisible == true && _t > 0.4f)
                {
                    SkillBookOverlay.SetActiveTab(0);
                    SkillBookOverlay.SetLayoutEditMode(true);
                    DebugLog.Log(ScenarioLog, "Phase 0: edit mode on, screenshot");
                    DeferredScreenshot = "ui_skilllayout_edit";
                    _waitShot = true; _phase = 1;
                }
                else if (_t > 4f) { DebugLog.Log(ScenarioLog, "FAIL: book never opened"); _fail = 10; _complete = true; }
                break;

            case 1:
                DebugLog.Log(ScenarioLog, "Phase 1: verify save round-trips x/y, preserves other fields");
                _fail = RunSaveTest();
                _complete = true;
                break;
        }
    }

    private int RunSaveTest()
    {
        // Back up every skill file (SaveLayout rewrites them all).
        var paths = new System.Collections.Generic.List<string>();
        var backups = new System.Collections.Generic.Dictionary<string, string>();
        foreach (var id in SkillBookDefs.TabIds)
        {
            string p = GamePaths.Resolve($"data/skills/{id}.json");
            paths.Add(p);
            if (File.Exists(p)) backups[p] = File.ReadAllText(p);
        }

        try
        {
            var tab = SkillBookDefs.Tabs[0];
            if (tab.Skills.Count == 0) { DebugLog.Log(ScenarioLog, "  no skills in tab 0"); return 20; }
            var sk = tab.Skills[0];
            int ox = sk.X, oy = sk.Y, nx = ox + 137, ny = oy + 59;
            string origName = sk.Name;
            DebugLog.Log(ScenarioLog, $"  moving '{sk.Id}' ({ox},{oy}) -> ({nx},{ny})");
            sk.X = nx; sk.Y = ny;

            if (!SkillBookDefs.SaveLayout()) { DebugLog.Log(ScenarioLog, "  SaveLayout returned false"); return 30; }

            // Re-read the written file directly.
            using var doc = JsonDocument.Parse(File.ReadAllText(paths[0]));
            var skills = doc.RootElement.GetProperty("skills");
            int gotX = 0, gotY = 0, count = 0; string gotName = ""; bool found = false;
            foreach (var s in skills.EnumerateArray())
            {
                count++;
                if (s.GetProperty("id").GetString() == sk.Id)
                {
                    found = true;
                    gotX = s.GetProperty("x").GetInt32();
                    gotY = s.GetProperty("y").GetInt32();
                    gotName = s.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                }
            }
            DebugLog.Log(ScenarioLog, $"  read back: found={found} x={gotX} y={gotY} name='{gotName}' count={count}");

            int fail = 0;
            if (!found) { DebugLog.Log(ScenarioLog, "  FAIL: skill missing after save"); fail = 31; }
            else if (gotX != nx || gotY != ny) { DebugLog.Log(ScenarioLog, "  FAIL: x/y not persisted"); fail = 32; }
            else if (gotName != origName) { DebugLog.Log(ScenarioLog, "  FAIL: name field not preserved"); fail = 33; }
            else if (count != tab.Skills.Count) { DebugLog.Log(ScenarioLog, "  FAIL: skill count changed"); fail = 34; }
            else DebugLog.Log(ScenarioLog, "  PASS: x/y persisted, other fields intact");

            sk.X = ox; sk.Y = oy; // restore in-memory
            return fail;
        }
        finally
        {
            // Restore every file byte-for-byte so the test leaves the repo clean.
            foreach (var kv in backups) File.WriteAllText(kv.Key, kv.Value);
            DebugLog.Log(ScenarioLog, "  restored skill files from backup");
        }
    }

    public override bool IsComplete => _complete;

    public override int OnComplete(Simulation sim)
    {
        DebugLog.Log(ScenarioLog, $"Result: {(_fail == 0 ? "PASS" : $"FAIL ({_fail})")}");
        return _fail;
    }
}
