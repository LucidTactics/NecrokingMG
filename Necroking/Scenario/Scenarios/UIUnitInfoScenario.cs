using Microsoft.Xna.Framework;
using Necroking.Core;
using Necroking.Data;
using Necroking.GameSystems;
using Necroking.Movement;

namespace Necroking.Scenario.Scenarios;

/// <summary>
/// [UI test] Renders the UnitInfoPanel (imported Unit Tooltip2 sheet wired to
/// live unit data) for a soldier (full loadout: weapons/armor/shield rows) and
/// then a skeleton (sparse loadout: unused rows must hide cleanly).
/// Screenshots: ui_unitinfo_soldier.png, ui_unitinfo_skeleton.png.
/// </summary>
public class UIUnitInfoScenario : ScenarioBase
{
    public override string Name => "UIUnitInfo";
    public override bool WantsWidgetRenderer => true;

    private float _elapsed;
    private int _phase;
    private bool _complete;
    private int _soldierIdx = -1, _skeletonIdx = -1, _necroIdx = -1;
    private UI.UnitInfoPanel? _panel;

    public override void OnInit(Simulation sim)
    {
        DebugLog.Clear(ScenarioLog);
        DebugLog.Log(ScenarioLog, "=== UI Unit Info Panel Scenario ===");
        ZoomOnLocation(10f, 10f, 32f);
        BackgroundColor = new Color(58, 62, 72);
        BloomOverride = new Data.Registries.BloomSettings { Enabled = false };

        // SpawnUnitByID (not raw AddUnit) so stats/weapons/armor come from the def.
        // Idle AI so nobody moves/fights — a death would swap-pop unit indices
        // and invalidate the stored _necroIdx etc.
        _soldierIdx = sim.SpawnUnitByID("soldier", new Vec2(8f, 10f));
        _skeletonIdx = sim.SpawnUnitByID("skeleton", new Vec2(60f, 60f));
        sim.UnitsMut[_soldierIdx].AI = AIBehavior.IdleAtPoint;
        sim.UnitsMut[_skeletonIdx].AI = AIBehavior.IdleAtPoint;
        var s = sim.Units[_soldierIdx].Stats;
        DebugLog.Log(ScenarioLog,
            $"soldier: melee={s.MeleeWeapons.Count} ranged={s.RangedWeapons.Count} " +
            $"shieldProt={s.ShieldProtection} bodyProt={s.Armor.BodyProtection} headProt={s.Armor.HeadProtection}");

        // Apply a spread of stat buffs to the soldier so the panel exercises
        // buff-modified values (green/red) + the tooltip itemisation.
        void Buff(string id)
        {
            var bd = sim.GameData.Buffs.Get(id);
            if (bd != null) BuffSystem.ApplyBuff(sim.UnitsMut, _soldierIdx, bd);
            else DebugLog.Log(ScenarioLog, $"WARN buff '{id}' not found");
        }
        Buff("buff_god_mode");   // God Mode (icon 0): 6 effects + Permanent (worst-case overlap test)
        Buff("buff_frenzy");     // Frenzy (icon 1): Mult Attack x1.2, Mult Speed x1.5 (multi-effect)
        Buff("strength_buff");   // Add Strength +5
        Buff("buff_3");          // Lucky: Add Defense +4
        Buff("iron_skin");       // Set NaturalProt 15
        Buff("iron_skin_copy");  // Quickness: Multiply CombatSpeed x2
        Buff("buff_miasma_slow");// Miasma: Multiply CombatSpeed x0.6 (mixed w/ above)
        DebugLog.Log(ScenarioLog, $"soldier buffs applied: {sim.Units[_soldierIdx].ActiveBuffs.Count}");

        // Necromancer for the magic-path display (native Death 3). God Mode's
        // AllPaths=9 floor raises Death to 9, exercising the buff-bonus tooltip.
        _necroIdx = sim.SpawnUnitByID("necromancer", new Vec2(40f, 40f));
        if (_necroIdx >= 0)
        {
            sim.UnitsMut[_necroIdx].AI = AIBehavior.IdleAtPoint;
            void NBuff(string id)
            {
                var bd = sim.GameData.Buffs.Get(id);
                if (bd != null) BuffSystem.ApplyBuff(sim.UnitsMut, _necroIdx, bd);
            }
            NBuff("buff_god_mode");
            NBuff("strength_buff");
            int nativeDeath = sim.GameData.Units.Get("necromancer")
                ?.GetPathLevel(Necroking.Data.Registries.MagicPath.Death) ?? -1;
            DebugLog.Log(ScenarioLog, $"necromancer #{_necroIdx} native Death={nativeDeath}");
        }

        CustomUIDraw = (batch, screenW, screenH) =>
        {
            if (WidgetRenderer == null) return;
            if (_panel == null)
            {
                _panel = new UI.UnitInfoPanel();
                _panel.Init(WidgetRenderer, sim.GameData);
                _panel.DrawUnitIconCallback = DrawUnitSprite == null ? null
                    : (defId, rect) => DrawUnitSprite(defId, rect);
                _panel.ShowForUnit(sim.Units[_soldierIdx].Id);
                DebugLog.Log(ScenarioLog, "panel created, showing soldier");
            }
            _panel.Draw(screenW, screenH, sim);
        };
    }

    public override void OnTick(Simulation sim, float dt)
    {
        _elapsed += dt;
        if (_phase == 0 && _elapsed > 0.5f && _panel != null)
        {
            DeferredScreenshot = "ui_unitinfo_soldier";
            _phase = 1;
        }
        else if (_phase == 1 && DeferredScreenshot == null)
        {
            DebugLog.Log(ScenarioLog, "soldier shot taken, switching to skeleton");
            _panel!.ShowForUnit(sim.Units[_skeletonIdx].Id);
            DeferredScreenshot = "ui_unitinfo_skeleton";
            _phase = 2;
        }
        else if (_phase == 2 && DeferredScreenshot == null)
        {
            // Back to soldier; settle one beat so its panel lays out before we
            // probe cell rects (DebugCellCenter reads the last drawn layout).
            _panel!.ShowForUnit(sim.Units[_soldierIdx].Id);
            _elapsed = 0f;
            _phase = 3;
        }
        else if (_phase == 3 && _elapsed > 0.2f)
        {
            // Hover the Strength LABEL (simple tooltip). Cell centre resolved
            // from the live layout so it's resolution-independent.
            _panel!.DebugMouseOverride = _panel!.DebugCellCenter("st_r2c0", value: false);
            _elapsed = 0f;
            _phase = 4;
        }
        else if (_phase == 4 && _elapsed > 1.5f)
        {
            DeferredScreenshot = "ui_stattip_label";
            _phase = 5;
        }
        else if (_phase == 5 && DeferredScreenshot == null)
        {
            _panel!.DebugMouseOverride = _panel!.DebugCellCenter("st_r2c0", value: true); // Strength VALUE
            _elapsed = 0f;
            _phase = 6;
        }
        else if (_phase == 6 && _elapsed > 1.5f)
        {
            DeferredScreenshot = "ui_stattip_value";
            _phase = 7;
        }
        else if (_phase == 7 && DeferredScreenshot == null)
        {
            // Hover the first buff icon (Frenzy — multi-effect) for its tooltip.
            _panel!.DebugMouseOverride = _panel!.DebugBuffIconCenter(0);
            DebugLog.Log(ScenarioLog, $"buff icon 0 center = {_panel!.DebugMouseOverride}");
            _elapsed = 0f;
            _phase = 8;
        }
        else if (_phase == 8 && _elapsed > 1.5f)
        {
            DeferredScreenshot = "ui_buff_tooltip";
            _phase = 9;
        }
        else if (_phase == 9 && DeferredScreenshot == null)
        {
            // Switch to the necromancer to show magic paths; settle a beat.
            _panel!.ShowForUnit(sim.Units[_necroIdx].Id);
            _panel!.DebugMouseOverride = null;
            _elapsed = 0f;
            _phase = 10;
        }
        else if (_phase == 10 && _elapsed > 0.2f)
        {
            DeferredScreenshot = "ui_unitinfo_necro";
            _phase = 11;
        }
        else if (_phase == 11 && DeferredScreenshot == null)
        {
            // Hover the first magic-path entry (Death) for its breakdown tooltip.
            _panel!.DebugMouseOverride = _panel!.DebugPathEntryCenter(0);
            DebugLog.Log(ScenarioLog, $"path entry 0 center = {_panel!.DebugMouseOverride}");
            _elapsed = 0f;
            _phase = 12;
        }
        else if (_phase == 12 && _elapsed > 1.5f)
        {
            DeferredScreenshot = "ui_path_tooltip";
            _phase = 13;
        }
        else if (_phase == 13 && DeferredScreenshot == null)
        {
            if (LaunchArgs.Headless)
            {
                _panel?.Hide();
                DebugLog.Log(ScenarioLog, "all screenshots taken, complete");
                _complete = true;
            }
        }
    }

    public override bool IsComplete => _complete;

    public override int OnComplete(Simulation sim)
    {
        DebugLog.Log(ScenarioLog, "Result: PASS");
        return 0;
    }
}
