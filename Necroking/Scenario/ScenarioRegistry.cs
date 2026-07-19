using System;
using System.Collections.Generic;

namespace Necroking.Scenario;

public static class ScenarioRegistry
{
    private static readonly Dictionary<string, Func<ScenarioBase>> _creators = new();
    private static readonly List<string> name_list = new();
    // Category display: order in which categories first appear, plus the
    // scenarios registered under each (in registration order). The category is
    // purely cosmetic — it changes how the scenario menu groups/labels entries
    // and has no effect on headless `--scenario <name>` lookups (those go
    // through Create by name).
    private static readonly List<string> category_order = new();
    private static readonly Dictionary<string, List<string>> by_category = new();

    static ScenarioRegistry()
    {
        // --- Combat ---
        Register("aggression_radius", "Combat", () => new Scenarios.AggressionRadiusScenario());
        Register("combat_test", "Combat", () => new Scenarios.CombatTestScenario());
        Register("skirmish", "Combat", () => new Scenarios.SkirmishScenario());
        Register("spell_test", "Combat", () => new Scenarios.SpellTestScenario());
        Register("combat_log", "Combat", () => new Scenarios.CombatLogScenario());
        Register("priest_battle", "Combat", () => new Scenarios.PriestBattleScenario());
        Register("poison_drain", "Combat", () => new Scenarios.PoisonDrainScenario());
        Register("paralyze_burst_test", "Combat", () => new Scenarios.ParalyzeBurstScenario());
        Register("paralyze_trap_test", "Combat", () => new Scenarios.ParalyzeTrapScenario());
        Register("summon_lag", "Combat", () => new Scenarios.SummonLagScenario());
        Register("pounce_test", "Combat", () => new Scenarios.PounceTestScenario());
        Register("pounce_outrun", "Combat", () => new Scenarios.PounceOutrunScenario());
        Register("sweep_attack", "Combat", () => new Scenarios.SweepAttackScenario());
        Register("trample_attack", "Combat", () => new Scenarios.TrampleAttackScenario());
        Register("trample_miss", "Combat", () => new Scenarios.TrampleMissScenario());
        Register("trample_necromancer", "Combat", () => new Scenarios.TrampleNecromancerScenario());
        Register("trample_greatboar", "Combat", () => new Scenarios.TrampleGreatBoarScenario());
        Register("trample_horde_knockdown", "Combat", () => new Scenarios.TrampleHordeKnockdownScenario());
        Register("trample_no_escape", "Combat", () => new Scenarios.TrampleNoEscapeScenario());
        Register("trample_blocker", "Combat", () => new Scenarios.TrampleBlockerScenario());
        Register("trample_kill", "Combat", () => new Scenarios.TrampleKillScenario());
        Register("archer_vs_deer", "Combat", () => new Scenarios.ArcherVsDeerScenario());
        Register("knockdown_test", "Combat", () => new Scenarios.KnockdownTestScenario());
        Register("weapon_attach_debug", "Combat", () => new Scenarios.WeaponAttachDebugScenario());
        Register("cast_point_debug", "Combat", () => new Scenarios.CastPointDebugScenario());
        Register("whiff_on_escape", "Combat", () => new Scenarios.WhiffOnEscapeScenario());
        Register("balance_matrix", "Combat", () => new Scenarios.BalanceMatrixScenario());
        Register("power_progression", "Combat", () => new Scenarios.PowerProgressionScenario());

        // --- AI & Movement ---
        Register("horde_engaged_kiting", "AI & Movement", () => new Scenarios.HordeEngagedKitingScenario());
        Register("horde_chase_leash", "AI & Movement", () => new Scenarios.HordeChaseLeashScenario());
        Register("horde_target_teleport", "AI & Movement", () => new Scenarios.HordeTargetTeleportScenario());
        Register("zombie_deer_follow_speed", "AI & Movement", () => new Scenarios.ZombieDeerFollowSpeedScenario());
        Register("patrol_encounter", "AI & Movement", () => new Scenarios.PatrolEncounterScenario());
        Register("collision_debug_test", "AI & Movement", () => new Scenarios.CollisionDebugTestScenario());
        Register("pathfinding_test", "AI & Movement", () => new Scenarios.PathfindingTestScenario());
        Register("portal_route_scale", "AI & Movement", () => new Scenarios.PortalRouteScaleScenario());
        Register("wolf_hit_and_run", "AI & Movement", () => new Scenarios.WolfHitAndRunScenario());
        Register("deer_realert_while_calming", "AI & Movement", () => new Scenarios.DeerReAlertWhileCalmingScenario());
        Register("neutral_fight_back", "AI & Movement", () => new Scenarios.NeutralFightBackScenario());
        Register("move_to_point", "AI & Movement", () => new Scenarios.MoveToPointScenario());
        Register("horde_follow", "AI & Movement", () => new Scenarios.HordeFollowScenario());
        Register("poison_flee", "AI & Movement", () => new Scenarios.PoisonFleeScenario());
        Register("wolf_retarget", "AI & Movement", () => new Scenarios.WolfRetargetScenario());
        Register("zombie_deer_leash", "AI & Movement", () => new Scenarios.ZombieDeerLeashScenario());
        Register("zombie_deer_craft_leash", "AI & Movement", () => new Scenarios.ZombieDeerCraftLeashScenario());
        Register("deer_flee_no_slide", "AI & Movement", () => new Scenarios.DeerFleeNoSlideScenario());

        // --- Animation ---
        Register("anim_transitions", "Animation", () => new Scenarios.AnimTransitionScenario());
        Register("slow_walk_anim", "Animation", () => new Scenarios.SlowWalkAnimScenario());
        Register("animated_object", "Animation", () => new Scenarios.AnimatedObjectScenario());
        Register("horde_idle_anim", "Animation", () => new Scenarios.HordeIdleAnimScenario());
        Register("combat_anim_review", "Animation", () => new Scenarios.CombatAnimReviewScenario());
        Register("chase_attack_anim", "Animation", () => new Scenarios.ChaseAttackAnimScenario());
        Register("stride_debug", "Animation", () => new Scenarios.StrideDebugScenario());

        // --- Rendering & VFX ---
        Register("ground_test", "Rendering & VFX", () => new Scenarios.GroundTestScenario());
        Register("god_ray", "Rendering & VFX", () => new Scenarios.GodRayScenario());
        Register("grass_test", "Rendering & VFX", () => new Scenarios.GrassTestScenario());
        Register("shadow_test", "Rendering & VFX", () => new Scenarios.ShadowTestScenario());
        Register("grass_wall_depth", "Rendering & VFX", () => new Scenarios.GrassWallDepthScenario());
        Register("road_rim", "Rendering & VFX", () => new Scenarios.RoadRimScenario());
        Register("nineslice_tiling", "Rendering & VFX", () => new Scenarios.NineSliceTilingScenario());
        Register("bloom_test", "Rendering & VFX", () => new Scenarios.BloomTestScenario());
        Register("bloom_debug", "Rendering & VFX", () => new Scenarios.BloomDebugScenario());
        Register("spell_visual_test", "Rendering & VFX", () => new Scenarios.SpellVisualTestScenario());
        Register("grass_blade_test", "Rendering & VFX", () => new Scenarios.GrassBladeTestScenario());
        Register("sprite_scale", "Rendering & VFX", () => new Scenarios.SpriteScaleScenario());
        Register("shadow_pivot", "Rendering & VFX", () => new Scenarios.ShadowPivotScenario());
        Register("blend_test", "Rendering & VFX", () => new Scenarios.BlendTestScenario());
        Register("godray_render_test", "Rendering & VFX", () => new Scenarios.GodRayRenderTestScenario());
        Register("poison_cloud", "Rendering & VFX", () => new Scenarios.PoisonCloudScenario());
        Register("magic_glyph", "Rendering & VFX", () => new Scenarios.MagicGlyphScenario());
        Register("ground_fog", "Rendering & VFX", () => new Scenarios.GroundFogScenario());
        Register("occlusion_fade", "Rendering & VFX", () => new Scenarios.OcclusionFadeScenario());
        Register("poison_burst", "Rendering & VFX", () => new Scenarios.PoisonBurstScenario());
        Register("fog_of_war", "Rendering & VFX", () => new Scenarios.FogOfWarScenario());
        Register("grass_tufts", "Rendering & VFX", () => new Scenarios.GrassTuftTestScenario());
        Register("grass_depth", "Rendering & VFX", () => new Scenarios.GrassDepthScenario());
        Register("angle_sweep", "Rendering & VFX", () => new Scenarios.AngleSweepScenario());
        Register("water_shed_depth", "Rendering & VFX", () => new Scenarios.WaterShedDepthScenario());
        Register("corpse_carry_depth", "Rendering & VFX", () => new Scenarios.CorpseCarryDepthScenario());
        Register("log_shadow", "Rendering & VFX", () => new Scenarios.LogShadowScenario());
        Register("perf_water", "Rendering & VFX", () => new Scenarios.PerfWaterScenario());
        Register("wake_color_check", "Rendering & VFX", () => new Scenarios.WakeColorCheckScenario());

        // --- UI / HUD ---
        Register("UIEmpty", "UI / HUD", () => new Scenarios.UIEmptyScenario());
        Register("UISkillBook", "UI / HUD", () => new Scenarios.UISkillBookScenario());
        Register("UIGradientTest", "UI / HUD", () => new Scenarios.UIGradientTestScenario());
        Register("UIRectShadowTest", "UI / HUD", () => new Scenarios.UIRectShadowTestScenario());
        Register("UICircleTest", "UI / HUD", () => new Scenarios.UICircleTestScenario());
        Register("UIUnitInfo", "UI / HUD", () => new Scenarios.UIUnitInfoScenario());
        Register("UIResourceTipDyn", "UI / HUD", () => new Scenarios.UIResourceTipDynScenario());
        Register("UIGrimoire", "UI / HUD", () => new Scenarios.UIGrimoireScenario());
        Register("UIGrimoireClick", "UI / HUD", () => new Scenarios.UIGrimoireClickScenario());
        Register("UISkillLayout", "UI / HUD", () => new Scenarios.UISkillLayoutScenario());
        Register("anim_button_test", "UI / HUD", () => new Scenarios.AnimButtonTestScenario());
        Register("inventory_ui", "UI / HUD", () => new Scenarios.InventoryUIScenario());
        Register("UISpellBar", "UI / HUD", () => new Scenarios.UISpellBarScenario());
        Register("UIStatusBars", "UI / HUD", () => new Scenarios.UIStatusBarsScenario());

        // --- Editor ---
        Register("building_placement", "Editor", () => new Scenarios.BuildingPlacementScenario());
        Register("editor_screenshots", "Editor", () => new Scenarios.EditorScreenshotScenario());
        Register("ui_test", "Editor", () => new Scenarios.EditorUITestScenario());
        Register("ui_skillbook_editor", "Editor", () => new Scenarios.EditorSkillBookPreviewScenario());
        Register("spell_editor", "Editor", () => new Scenarios.SpellEditorScenario());
        Register("editor_undo_lag", "Editor", () => new Scenarios.EditorUndoLagScenario());
        Register("harmonize_object", "Editor", () => new Scenarios.HarmonizeObjectScenario());
        Register("object_flip", "Editor", () => new Scenarios.ObjectFlipScenario());

        // --- Physics ---
        Register("physics_single", "Physics", () => new Scenarios.PhysicsSingleScenario());
        Register("physics_multi", "Physics", () => new Scenarios.PhysicsMultiScenario());
        Register("physics_chain", "Physics", () => new Scenarios.PhysicsChainScenario());
        Register("knockback_corpse", "Physics", () => new Scenarios.KnockbackCorpseScenario());

        // --- World & Environment ---
        Register("empty_map", "World & Environment", () => new Scenarios.EmptyMapScenario());
        Register("wall_test", "World & Environment", () => new Scenarios.WallTestScenario());
        Register("wall_trap", "World & Environment", () => new Scenarios.WallTrapScenario());
        Register("wall_gate", "World & Environment", () => new Scenarios.WallGateScenario());
        Register("weather_test", "World & Environment", () => new Scenarios.WeatherTestScenario());
        Register("deep_water_blocks", "World & Environment", () => new Scenarios.DeepWaterBlocksScenario());
        Register("map_reload_consistency", "World & Environment", () => new Scenarios.MapReloadConsistencyScenario());

        // --- Game Systems ---
        Register("placed_corpse", "Game Systems", () => new Scenarios.PlacedCorpseScenario());
        Register("item_consume", "Game Systems", () => new Scenarios.ItemConsumeScenario());
        Register("corpse_worker", "Game Systems", () => new Scenarios.CorpseWorkerScenario());
        Register("craft_table", "Game Systems", () => new Scenarios.CraftTableScenario());
        Register("intrinsic_buff", "Game Systems", () => new Scenarios.IntrinsicBuffScenario());
        Register("corpse_eater", "Game Systems", () => new Scenarios.CorpseEaterScenario());
        Register("spell_kill_tally", "Game Systems", () => new Scenarios.SpellKillTallyScenario());
        Register("path_buff", "Game Systems", () => new Scenarios.PathBuffScenario());
        Register("seen_item", "Game Systems", () => new Scenarios.SeenItemScenario());
        Register("corpses_eaten", "Game Systems", () => new Scenarios.CorpsesEatenScenario());

        // --- Data & Serialization ---
        Register("sidecar_roundtrip", "Data & Serialization", () => new Scenarios.SidecarRoundtripScenario());
        Register("atomic_stream", "Data & Serialization", () => new Scenarios.AtomicStreamScenario());
        Register("ui_defs_roundtrip", "Data & Serialization", () => new Scenarios.UIDefsRoundtripScenario());
        Register("env_defs_roundtrip", "Data & Serialization", () => new Scenarios.EnvDefsRoundtripScenario());
    }

    public static void Register(string name, string category, Func<ScenarioBase> creator)
    {
        _creators[name] = creator;
        name_list.Add(name);

        if (!by_category.TryGetValue(category, out var list))
        {
            list = new List<string>();
            by_category[category] = list;
            category_order.Add(category);
        }
        list.Add(name);
    }

    public static ScenarioBase? Create(string name)
    {
        return _creators.TryGetValue(name, out var creator) ? creator() : null;
    }

    public static IEnumerable<string> GetNames() => name_list;

    /// <summary>Categories in the order they were first registered (menu display order).</summary>
    public static IReadOnlyList<string> GetCategories() => category_order;

    /// <summary>Scenario names in the given category, in registration order.</summary>
    public static IReadOnlyList<string> GetNamesInCategory(string category)
        => by_category.TryGetValue(category, out var list) ? list : Array.Empty<string>();
}
