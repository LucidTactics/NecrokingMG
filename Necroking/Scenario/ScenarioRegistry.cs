using System;
using System.Collections.Generic;

namespace Necroking.Scenario;

public static class ScenarioRegistry
{
    private static readonly Dictionary<string, Func<ScenarioBase>> _creators = new();

    static ScenarioRegistry()
    {
        Register("anim_transitions", () => new Scenarios.AnimTransitionScenario());
        Register("horde_engaged_kiting", () => new Scenarios.HordeEngagedKitingScenario());
        Register("horde_chase_leash", () => new Scenarios.HordeChaseLeashScenario());
        Register("horde_target_teleport", () => new Scenarios.HordeTargetTeleportScenario());
        Register("zombie_deer_follow_speed", () => new Scenarios.ZombieDeerFollowSpeedScenario());
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
        Register("UISkillBook", () => new Scenarios.UISkillBookScenario());
        Register("UIGradientTest", () => new Scenarios.UIGradientTestScenario());
        Register("UIRectShadowTest", () => new Scenarios.UIRectShadowTestScenario());
        Register("UICircleTest", () => new Scenarios.UICircleTestScenario());
        Register("UIUnitInfo", () => new Scenarios.UIUnitInfoScenario());
        Register("UIResourceTipDyn", () => new Scenarios.UIResourceTipDynScenario());
        Register("UIGrimoire", () => new Scenarios.UIGrimoireScenario());
        Register("UIGrimoireClick", () => new Scenarios.UIGrimoireClickScenario());
        Register("grass_test", () => new Scenarios.GrassTestScenario());
        Register("corpse_worker", () => new Scenarios.CorpseWorkerScenario());
        Register("raid_workers", () => new Scenarios.RaidWorkersScenario());
        Register("undead_raid", () => new Scenarios.UndeadRaidScenario());
        Register("shadow_test", () => new Scenarios.ShadowTestScenario());
        Register("grass_wall_depth", () => new Scenarios.GrassWallDepthScenario());
        Register("road_rim", () => new Scenarios.RoadRimScenario());
        Register("editor_screenshots", () => new Scenarios.EditorScreenshotScenario());
        Register("ui_test", () => new Scenarios.EditorUITestScenario());
        Register("ui_skillbook_editor", () => new Scenarios.EditorSkillBookPreviewScenario());
        Register("UISkillLayout", () => new Scenarios.UISkillLayoutScenario());
        Register("nineslice_tiling", () => new Scenarios.NineSliceTilingScenario());
        Register("anim_button_test", () => new Scenarios.AnimButtonTestScenario());
        Register("bloom_test", () => new Scenarios.BloomTestScenario());
        Register("bloom_debug", () => new Scenarios.BloomDebugScenario());
        Register("spell_visual_test", () => new Scenarios.SpellVisualTestScenario());
        Register("collision_debug_test", () => new Scenarios.CollisionDebugTestScenario());
        Register("grass_blade_test", () => new Scenarios.GrassBladeTestScenario());
        Register("pathfinding_test", () => new Scenarios.PathfindingTestScenario());
        Register("wolf_hit_and_run", () => new Scenarios.WolfHitAndRunScenario());
        Register("flee_when_hit", () => new Scenarios.FleeWhenHitScenario());
        Register("deer_realert_while_calming", () => new Scenarios.DeerReAlertWhileCalmingScenario());
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
        Register("pounce_outrun", () => new Scenarios.PounceOutrunScenario());
        Register("sweep_attack", () => new Scenarios.SweepAttackScenario());
        Register("trample_attack", () => new Scenarios.TrampleAttackScenario());
        Register("trample_miss", () => new Scenarios.TrampleMissScenario());
        Register("trample_necromancer", () => new Scenarios.TrampleNecromancerScenario());
        Register("trample_greatboar", () => new Scenarios.TrampleGreatBoarScenario());
        Register("trample_horde_knockdown", () => new Scenarios.TrampleHordeKnockdownScenario());
        Register("trample_no_escape", () => new Scenarios.TrampleNoEscapeScenario());
        Register("trample_blocker", () => new Scenarios.TrampleBlockerScenario());
        Register("trample_kill", () => new Scenarios.TrampleKillScenario());
        Register("knockdown_test", () => new Scenarios.KnockdownTestScenario());
        Register("wolf_retarget", () => new Scenarios.WolfRetargetScenario());
        Register("craft_table", () => new Scenarios.CraftTableScenario());
        Register("weapon_attach_debug", () => new Scenarios.WeaponAttachDebugScenario());
        Register("cast_point_debug", () => new Scenarios.CastPointDebugScenario());
        Register("water_shed_depth", () => new Scenarios.WaterShedDepthScenario());
        Register("UISpellBar", () => new Scenarios.UISpellBarScenario());
        Register("UIStatusBars", () => new Scenarios.UIStatusBarsScenario());
        Register("corpse_carry_depth", () => new Scenarios.CorpseCarryDepthScenario());
        Register("zombie_deer_leash", () => new Scenarios.ZombieDeerLeashScenario());
        Register("zombie_deer_craft_leash", () => new Scenarios.ZombieDeerCraftLeashScenario());
        Register("combat_anim_review", () => new Scenarios.CombatAnimReviewScenario());
        Register("chase_attack_anim", () => new Scenarios.ChaseAttackAnimScenario());
        Register("log_shadow", () => new Scenarios.LogShadowScenario());
        Register("stride_debug", () => new Scenarios.StrideDebugScenario());
        Register("intrinsic_buff", () => new Scenarios.IntrinsicBuffScenario());
        Register("corpse_eater", () => new Scenarios.CorpseEaterScenario());
        Register("perf_water", () => new Scenarios.PerfWaterScenario());
        Register("deep_water_blocks", () => new Scenarios.DeepWaterBlocksScenario());
        Register("wake_color_check", () => new Scenarios.WakeColorCheckScenario());
        Register("editor_undo_lag", () => new Scenarios.EditorUndoLagScenario());
        Register("harmonize_object", () => new Scenarios.HarmonizeObjectScenario());
        Register("object_flip", () => new Scenarios.ObjectFlipScenario());
        Register("spell_kill_tally", () => new Scenarios.SpellKillTallyScenario());
        Register("path_buff", () => new Scenarios.PathBuffScenario());
        Register("seen_item", () => new Scenarios.SeenItemScenario());
        Register("corpses_eaten", () => new Scenarios.CorpsesEatenScenario());
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
