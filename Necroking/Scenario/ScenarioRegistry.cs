using System;
using System.Collections.Generic;

namespace Necroking.Scenario;

public static class ScenarioRegistry
{
    private static readonly Dictionary<string, Func<ScenarioBase>> _creators = new();

    static ScenarioRegistry()
    {
        Register("combat_test", () => new Scenarios.CombatTestScenario());
        Register("skirmish", () => new Scenarios.SkirmishScenario());
        Register("empty_map", () => new Scenarios.EmptyMapScenario());
        Register("spell_test", () => new Scenarios.SpellTestScenario());
        Register("combat_log", () => new Scenarios.CombatLogScenario());
        Register("ai_behavior", () => new Scenarios.AIBehaviorScenario());
        Register("building_placement", () => new Scenarios.BuildingPlacementScenario());
        Register("ground_test", () => new Scenarios.GroundTestScenario());
    }

    public static void Register(string name, Func<ScenarioBase> creator)
    {
        _creators[name] = creator;
    }

    public static ScenarioBase? Create(string name)
    {
        return _creators.TryGetValue(name, out var creator) ? creator() : null;
    }

    public static IEnumerable<string> GetNames() => _creators.Keys;
}
