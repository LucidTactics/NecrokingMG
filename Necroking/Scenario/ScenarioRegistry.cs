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
        Register("order_attack", () => new Scenarios.OrderAttackScenario());
        Register("god_ray", () => new Scenarios.GodRayScenario());
        Register("priest_battle", () => new Scenarios.PriestBattleScenario());
        Register("patrol_encounter", () => new Scenarios.PatrolEncounterScenario());
        Register("wall_test", () => new Scenarios.WallTestScenario());
        Register("wall_trap", () => new Scenarios.WallTrapScenario());
        Register("wall_gate", () => new Scenarios.WallGateScenario());
        Register("UIEmpty", () => new Scenarios.UIEmptyScenario());
        Register("UISkillTree", () => new Scenarios.UISkillTreeScenario());
        Register("UIGradientTest", () => new Scenarios.UIGradientTestScenario());
        Register("UIRectShadowTest", () => new Scenarios.UIRectShadowTestScenario());
        Register("UICircleTest", () => new Scenarios.UICircleTestScenario());
        Register("grass_test", () => new Scenarios.GrassTestScenario());
        Register("corpse_worker", () => new Scenarios.CorpseWorkerScenario());
        Register("raid_workers", () => new Scenarios.RaidWorkersScenario());
        Register("undead_raid", () => new Scenarios.UndeadRaidScenario());
        Register("shadow_test", () => new Scenarios.ShadowTestScenario());
        Register("grass_wall_depth", () => new Scenarios.GrassWallDepthScenario());
        Register("road_rim", () => new Scenarios.RoadRimScenario());
        Register("editor_screenshots", () => new Scenarios.EditorScreenshotScenario());
        Register("ui_test", () => new Scenarios.EditorUITestScenario());
        Register("anim_button_test", () => new Scenarios.AnimButtonTestScenario());
        Register("bloom_test", () => new Scenarios.BloomTestScenario());
        Register("bloom_debug", () => new Scenarios.BloomDebugScenario());
        Register("spell_visual_test", () => new Scenarios.SpellVisualTestScenario());
        Register("collision_debug_test", () => new Scenarios.CollisionDebugTestScenario());
        Register("grass_blade_test", () => new Scenarios.GrassBladeTestScenario());
        Register("pathfinding_test", () => new Scenarios.PathfindingTestScenario());
        Register("wolf_hit_and_run", () => new Scenarios.WolfHitAndRunScenario());
        Register("flee_when_hit", () => new Scenarios.FleeWhenHitScenario());
        Register("neutral_fight_back", () => new Scenarios.NeutralFightBackScenario());
        Register("move_to_point", () => new Scenarios.MoveToPointScenario());
        Register("retarget", () => new Scenarios.RetargetScenario());
        Register("horde_follow", () => new Scenarios.HordeFollowScenario());
        Register("sprite_scale", () => new Scenarios.SpriteScaleScenario());
        Register("shadow_pivot", () => new Scenarios.ShadowPivotScenario());
        Register("inventory_ui", () => new Scenarios.InventoryUIScenario());
        Register("weather_test", () => new Scenarios.WeatherTestScenario());
        Register("spell_editor", () => new Scenarios.SpellEditorScenario());
        Register("blend_test", () => new Scenarios.BlendTestScenario());
        Register("godray_render_test", () => new Scenarios.GodRayRenderTestScenario());
        Register("poison_cloud", () => new Scenarios.PoisonCloudScenario());
        Register("magic_glyph", () => new Scenarios.MagicGlyphScenario());
        Register("poison_burst", () => new Scenarios.PoisonBurstScenario());
        Register("poison_flee", () => new Scenarios.PoisonFleeScenario());
        Register("physics_single", () => new Scenarios.PhysicsSingleScenario());
        Register("physics_multi", () => new Scenarios.PhysicsMultiScenario());
        Register("physics_chain", () => new Scenarios.PhysicsChainScenario());
        Register("knockback_corpse", () => new Scenarios.KnockbackCorpseScenario());
        Register("animated_object", () => new Scenarios.AnimatedObjectScenario());
        Register("fog_of_war", () => new Scenarios.FogOfWarScenario());
        Register("grass_tufts", () => new Scenarios.GrassTuftTestScenario());
        Register("grass_depth", () => new Scenarios.GrassDepthScenario());
        Register("angle_sweep", () => new Scenarios.AngleSweepScenario());
        Register("poison_drain", () => new Scenarios.PoisonDrainScenario());
        Register("paralyze_burst_test", () => new Scenarios.ParalyzeBurstScenario());
        Register("paralyze_trap_test", () => new Scenarios.ParalyzeTrapScenario());
        Register("horde_idle_anim", () => new Scenarios.HordeIdleAnimScenario());
        Register("summon_lag", () => new Scenarios.SummonLagScenario());
        Register("pounce_test", () => new Scenarios.PounceTestScenario());
        Register("knockdown_test", () => new Scenarios.KnockdownTestScenario());
        Register("wolf_retarget", () => new Scenarios.WolfRetargetScenario());
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
