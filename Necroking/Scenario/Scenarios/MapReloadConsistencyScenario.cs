using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Necroking.Core;
using Necroking.GameSystems;

namespace Necroking.Scenario.Scenarios;

/// <summary>
/// Regression test for the load → exit-to-main-menu → load-again lifecycle.
/// Starts the real map twice with a simulated main-menu round trip in between
/// and fails if any captured state differs between the two loads: env defs,
/// buildable defs, env-object and unit histograms, corpses, necromancer,
/// resources, starting inventory, and — the player-facing check — what the
/// build menu actually lists when opened. The two loads must be byte-identical:
/// any diff means per-session state leaked across the round trip or a UI kept
/// a reference into the disposed previous GameSession (the
/// empty-build-menu-on-reenter bug this was written for). Run this instead of
/// manually driving the game whenever a "works first time, breaks on re-enter"
/// symptom shows up.
/// </summary>
public class MapReloadConsistencyScenario : ScenarioBase
{
    public override string Name => "map_reload_consistency";

    /// <summary>The real playable map — "empty_test" synthesizes a world with
    /// no env defs at all, so only a JSON map exercises the full load path.</summary>
    private const string MapName = "default";

    /// <summary>World runs this long between the loads so first-session
    /// activity (sim ticks, lazy inits) gets a chance to leak state.</summary>
    private const int TicksBetweenLoads = 30;

    private enum Phase { FirstLoad, RunWorld, ReloadAndCompare, Done }
    private Phase _phase = Phase.FirstLoad;
    private int _runTicks;
    private Dictionary<string, string>? _first, _second;
    private readonly List<string> _problems = new();

    public override void OnInit(Simulation sim) { }

    public override bool IsComplete => _phase == Phase.Done;

    public override void OnTick(Simulation sim, float dt)
    {
        // The sim param goes stale the moment LoadMapAsPlayer swaps the
        // GameSession — everything below reads live through Game1 instead.
        var g = Game1.Instance;
        switch (_phase)
        {
            case Phase.FirstLoad:
                if (!LoadMapAsPlayer(g)) return;
                _first = CaptureSnapshot(g, "first");
                _phase = Phase.RunWorld;
                break;

            case Phase.RunWorld:
                if (++_runTicks < TicksBetweenLoads) break;
                _phase = Phase.ReloadAndCompare;
                break;

            case Phase.ReloadAndCompare:
                // Exit to the main menu exactly like PauseMenuScreen's Main Menu
                // button, then immediately Play again. Both must happen inside
                // this one tick: Game1.Update returns before the scenario tick
                // while the real main menu is showing, so a scenario that leaves
                // the world unloaded across frames never ticks again.
                g._menuState = MenuState.MainMenu;
                g._clock.ClearAllPauses();
                g._gameWorldLoaded = false;
                if (!LoadMapAsPlayer(g)) return;
                _second = CaptureSnapshot(g, "second");
                Compare();
                _phase = Phase.Done;
                break;
        }
    }

    /// <summary>StartGame like the main menu's Play button. ResetWorldState
    /// inside detaches the running scenario (by design — see its comment), so
    /// re-install this one before returning: Game1's scenario block dereferences
    /// _activeScenario right after OnTick.</summary>
    private bool LoadMapAsPlayer(Game1 g)
    {
        if (!File.Exists(GamePaths.Resolve($"{GamePaths.MapsDir}/{MapName}.json")))
        {
            _problems.Add($"map file missing: assets/maps/{MapName}.json (Drive-synced — run /sync-assets)");
            _phase = Phase.Done;
            return false;
        }
        DebugLog.Log(ScenarioLog, $"[reload] StartGame(\"{MapName}\")");
        g.StartGame(MapName);
        g._activeScenario = this;
        return true;
    }

    private Dictionary<string, string> CaptureSnapshot(Game1 g, string label)
    {
        var snap = new Dictionary<string, string>();
        var env = g._envSystem;
        var s = g._sim;

        snap["env.defCount"] = env.DefCount.ToString();
        var buildable = new List<string>();
        for (int di = 0; di < env.DefCount; di++)
            if (env.Defs[di].PlayerBuildable) buildable.Add(env.Defs[di].Id);
        buildable.Sort(StringComparer.Ordinal);
        snap["env.buildableDefs"] = string.Join(",", buildable);

        snap["env.objectCount"] = env.ObjectCount.ToString();
        var objCounts = new SortedDictionary<string, int>(StringComparer.Ordinal);
        for (int oi = 0; oi < env.ObjectCount; oi++)
        {
            string id = env.Defs[env.GetObject(oi).DefIndex].Id;
            objCounts[id] = objCounts.GetValueOrDefault(id) + 1;
        }
        foreach (var kv in objCounts) snap[$"env.obj[{kv.Key}]"] = kv.Value.ToString();

        snap["units.count"] = s.Units.Count.ToString();
        var unitCounts = new SortedDictionary<string, int>(StringComparer.Ordinal);
        for (int ui = 0; ui < s.Units.Count; ui++)
        {
            var u = s.Units[ui];
            if (!u.Alive) continue;
            string key = $"{u.UnitDefID}|{u.Faction}";
            unitCounts[key] = unitCounts.GetValueOrDefault(key) + 1;
        }
        foreach (var kv in unitCounts) snap[$"units[{kv.Key}]"] = kv.Value.ToString();
        snap["corpses.count"] = s.Corpses.Count.ToString();

        int necroIdx = s.NecromancerIndex;
        snap["necro"] = necroIdx >= 0
            ? $"{s.Units[necroIdx].UnitDefID}@{s.Units[necroIdx].Position.X:F2},{s.Units[necroIdx].Position.Y:F2}"
            : "NONE";

        snap["resources"] = $"{s.PlayerResources.Essence}/{s.PlayerResources.MaxEssence}";

        var inv = new List<string>();
        for (int si = 0; si < g._inventory.SlotCount; si++)
        {
            var slot = g._inventory.GetSlot(si);
            if (!slot.IsEmpty) inv.Add($"{slot.ItemId}x{slot.Quantity}");
        }
        inv.Sort(StringComparer.Ordinal);
        snap["inventory"] = string.Join(",", inv);

        // The player-facing check: open the build menu the way pressing B would
        // and record what it actually lists. A menu holding a stale session ref
        // shows nothing here even when the env system above reloaded fine.
        // The menu is gated on construction-skill unlocks (empty for a fresh
        // book), so unlock every buildable first — the subject here is session
        // wiring, not the skill gate.
        for (int di = 0; di < env.DefCount; di++)
            if (env.Defs[di].PlayerBuildable) g._skillBookState.UnlockBuilding(env.Defs[di].Id);
        g.EnsureInventoryUIsInitialized();
        var menu = g._buildingMenuUI;
        menu.Open(g.GraphicsDevice.Viewport.Width, g.GraphicsDevice.Viewport.Height);
        var menuIds = menu.BuildableDefIndices
            .Select(di => env.Defs[di].Id)
            .OrderBy(id => id, StringComparer.Ordinal);
        snap["buildMenu.items"] = string.Join(",", menuIds);
        menu.Close();

        DebugLog.Log(ScenarioLog,
            $"[reload] snapshot {label}: defs={snap["env.defCount"]} objects={snap["env.objectCount"]} " +
            $"units={snap["units.count"]} corpses={snap["corpses.count"]} necro={snap["necro"]} " +
            $"buildMenu=[{snap["buildMenu.items"]}]");
        return snap;
    }

    private void Compare()
    {
        // Empty-world sanity first — two identically-empty loads must not pass.
        if (_first!["env.defCount"] == "0") _problems.Add("first load has 0 env defs — map didn't really load");
        if (_first["buildMenu.items"].Length == 0) _problems.Add("first load: build menu lists nothing");
        if (_first["units.count"] == "0") _problems.Add("first load has 0 units");
        if (_first["necro"] == "NONE") _problems.Add("first load has no necromancer");

        foreach (var key in _first.Keys.Union(_second!.Keys).OrderBy(k => k, StringComparer.Ordinal))
        {
            _first.TryGetValue(key, out var a);
            _second.TryGetValue(key, out var b);
            if (a != b)
                _problems.Add($"{key}: first='{a ?? "<missing>"}' second='{b ?? "<missing>"}'");
        }
    }

    public override int OnComplete(Simulation sim)
    {
        foreach (var p in _problems)
            DebugLog.Log(ScenarioLog, $"[reload] PROBLEM: {p}");
        DebugLog.Log(ScenarioLog, _problems.Count == 0
            ? $"[reload] PASS: {_first?.Count ?? 0} snapshot keys identical across the main-menu round trip"
            : $"[reload] FAIL: {_problems.Count} problem(s) — state differs between first and second load");
        // Problems also go to stderr so a headless run shows the diff without
        // opening log/scenario.log.
        foreach (var p in _problems)
            Console.Error.WriteLine($"  reload diff: {p}");
        return _problems.Count == 0 ? 0 : 1;
    }
}
