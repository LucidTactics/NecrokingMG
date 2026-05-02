using System;
using Necroking.Core;
using Necroking.GameSystems;
using Necroking.World;

namespace Necroking.AI;

/// <summary>
/// Archetype handler for player-controlled units (necromancer).
/// Player input (WASD, mouse) is handled in Game1 and overrides movement.
/// This handler manages structured activities triggered by player commands:
///   - BuildGlyph: walk to magic glyph blueprint, WorkStart/WorkLoop/WorkEnd
///   - BuildTrap: walk to env object blueprint, WorkStart/WorkLoop/WorkEnd
///   - BaggingCorpse, PickupCorpse, PutDownCorpse: corpse interaction
///
/// Routine 0 (Idle) means normal player control — handler does nothing.
/// Other routines lock movement until complete or cancelled by WASD.
/// </summary>
public class PlayerControlledHandler : IArchetypeHandler
{
    // Routine IDs
    public const byte RoutineIdle = 0;
    public const byte RoutineBuildTrap = 1;
    public const byte RoutineBagCorpse = 2;
    public const byte RoutinePickupCorpse = 3;
    public const byte RoutinePutDownCorpse = 4;
    public const byte RoutineBuildGlyph = 5;
    public const byte RoutineCraftAtTable = 6;

    // Build subroutines (shared by BuildTrap and BuildGlyph)
    public const byte BuildSub_WalkToSite = 0;
    public const byte BuildSub_WorkStart = 1;
    public const byte BuildSub_WorkLoop = 2;
    public const byte BuildSub_WorkEnd = 3;

    public const float GlyphBuildDuration = 1.5f;
    public const float TrapBuildDuration = 1.5f;
    public const float BagDuration = 2.0f;
    public const float InteractRange = 1.0f;
    /// <summary>"Arrived at table" range — larger than InteractRange because tables
    /// have a collision radius (~0.5) plus the unit's own radius, so the closest
    /// the necromancer can stand center-to-center is ~1.0. 1.6 gives ORCA tolerance.</summary>
    public const float CraftInteractRange = 1.6f;

    public void OnSpawn(ref AIContext ctx)
    {
        ctx.Routine = RoutineIdle;
        ctx.Subroutine = 0;
    }

    public void Update(ref AIContext ctx)
    {
        switch (ctx.Routine)
        {
            case RoutineIdle:
                break;
            case RoutineBuildTrap:
                UpdateBuildTrap(ref ctx);
                break;
            case RoutineBuildGlyph:
                UpdateBuildGlyph(ref ctx);
                break;
            case RoutineCraftAtTable:
                UpdateCraftAtTable(ref ctx);
                break;
            case RoutineBagCorpse:
            case RoutinePickupCorpse:
            case RoutinePutDownCorpse:
                ctx.Units[ctx.UnitIndex].PreferredVel = Vec2.Zero;
                break;
        }
    }

    // ═══════════════════════════════════════
    //  Build Glyph
    // ═══════════════════════════════════════

    private void UpdateBuildGlyph(ref AIContext ctx)
    {
        int i = ctx.UnitIndex;
        int glyphIdx = ctx.Units[i].BuildGlyphIdx;

        var glyph = ctx.MagicGlyphs?.GetGlyph(glyphIdx);
        if (glyph == null || !glyph.Alive || glyph.State != GlyphState.Blueprint)
        {
            CancelBuild(ref ctx);
            return;
        }

        switch (ctx.Subroutine)
        {
            case BuildSub_WalkToSite:
            {
                ctx.Units[i].MoveTarget = glyph.Position;
                SubroutineSteps.MoveToPosition(ref ctx, ctx.MySpeed);

                var dir = glyph.Position - ctx.MyPos;
                if (dir.LengthSq() > 0.01f)
                    ctx.Units[i].FacingAngle = MathF.Atan2(dir.Y, dir.X) * 180f / MathF.PI;

                if (SubroutineSteps.MoveToPosition_Arrived(ref ctx, InteractRange))
                {
                    ctx.Subroutine = BuildSub_WorkStart;
                    ctx.Units[i].PreferredVel = Vec2.Zero;
                    ctx.Units[i].CorpseInteractPhase = 1;
                }
                break;
            }

            case BuildSub_WorkStart:
                ctx.Units[i].PreferredVel = Vec2.Zero;
                if (ctx.Units[i].CorpseInteractPhase == 2)
                {
                    ctx.Subroutine = BuildSub_WorkLoop;
                    ctx.Units[i].BuildTimer = 0f;
                }
                break;

            case BuildSub_WorkLoop:
                ctx.Units[i].PreferredVel = Vec2.Zero;
                ctx.Units[i].BuildTimer += ctx.Dt;
                glyph.BuildProgress = Math.Min(1f, ctx.Units[i].BuildTimer / GlyphBuildDuration);
                if (ctx.Units[i].BuildTimer >= GlyphBuildDuration)
                {
                    // Activate glyph immediately when progress completes
                    glyph.State = GlyphState.Dormant;
                    glyph.BuildProgress = 1f;
                    glyph.StateTimer = 0f;
                    ctx.Units[i].BuildGlyphIdx = -1;
                    // Transition to WorkEnd standup animation (cosmetic only)
                    ctx.Subroutine = BuildSub_WorkEnd;
                    ctx.Units[i].CorpseInteractPhase = 3;
                }
                break;

            case BuildSub_WorkEnd:
                ctx.Units[i].PreferredVel = Vec2.Zero;
                if (ctx.Units[i].CorpseInteractPhase == 0)
                {
                    // Standup animation finished — return to idle
                    ctx.Routine = RoutineIdle;
                    ctx.Subroutine = 0;
                }
                break;
        }
    }

    // ═══════════════════════════════════════
    //  Build Trap (env object)
    // ═══════════════════════════════════════

    private void UpdateBuildTrap(ref AIContext ctx)
    {
        int i = ctx.UnitIndex;
        int objIdx = ctx.Units[i].BuildTargetIdx;

        if (objIdx < 0 || ctx.EnvSystem == null
            || objIdx >= ctx.EnvSystem.ObjectCount
            || !ctx.EnvSystem.GetObjectRuntime(objIdx).Alive
            || ctx.EnvSystem.GetObjectRuntime(objIdx).BuildProgress >= 1f)
        {
            CancelBuild(ref ctx);
            return;
        }

        switch (ctx.Subroutine)
        {
            case BuildSub_WalkToSite:
            {
                var obj = ctx.EnvSystem.GetObject(objIdx);
                var targetPos = new Vec2(obj.X, obj.Y);
                ctx.Units[i].MoveTarget = targetPos;
                SubroutineSteps.MoveToPosition(ref ctx, ctx.MySpeed);

                var dir = targetPos - ctx.MyPos;
                if (dir.LengthSq() > 0.01f)
                    ctx.Units[i].FacingAngle = MathF.Atan2(dir.Y, dir.X) * 180f / MathF.PI;

                if (SubroutineSteps.MoveToPosition_Arrived(ref ctx, InteractRange))
                {
                    ctx.Subroutine = BuildSub_WorkStart;
                    ctx.Units[i].PreferredVel = Vec2.Zero;
                    ctx.Units[i].CorpseInteractPhase = 1;
                }
                break;
            }

            case BuildSub_WorkStart:
                ctx.Units[i].PreferredVel = Vec2.Zero;
                if (ctx.Units[i].CorpseInteractPhase == 2)
                {
                    ctx.Subroutine = BuildSub_WorkLoop;
                    ctx.Units[i].BuildTimer = 0f;
                }
                break;

            case BuildSub_WorkLoop:
                ctx.Units[i].PreferredVel = Vec2.Zero;
                ctx.Units[i].BuildTimer += ctx.Dt;
                {
                    float progress = Math.Min(1f, ctx.Units[i].BuildTimer / TrapBuildDuration);
                    var rt = ctx.EnvSystem.GetObjectRuntime(objIdx);
                    rt.BuildProgress = progress;
                    ctx.EnvSystem.SetObjectRuntime(objIdx, rt);
                }
                if (ctx.Units[i].BuildTimer >= TrapBuildDuration)
                {
                    ctx.Subroutine = BuildSub_WorkEnd;
                    ctx.Units[i].CorpseInteractPhase = 3;
                }
                break;

            case BuildSub_WorkEnd:
                ctx.Units[i].PreferredVel = Vec2.Zero;
                if (ctx.Units[i].CorpseInteractPhase == 0)
                {
                    var rt = ctx.EnvSystem.GetObjectRuntime(objIdx);
                    rt.BuildProgress = 1f;
                    ctx.EnvSystem.SetObjectRuntime(objIdx, rt);
                    ctx.Units[i].BuildTargetIdx = -1;
                    ctx.Routine = RoutineIdle;
                    ctx.Subroutine = 0;
                }
                break;
        }
    }

    // ═══════════════════════════════════════
    //  Craft at Table
    // ═══════════════════════════════════════

    /// <summary>
    /// Routine driver for channeling a table craft. Walks to the table, plays
    /// WorkStart → WorkLoop → WorkEnd. The actual craft timer + completion live
    /// in TableCraftingSystem.Tick (called from Simulation) so this handler is
    /// pure animation control — when ts.Crafting flips false (TableCraftingSystem
    /// completed the craft), we transition to WorkEnd; when WorkEnd's anim ends
    /// we drop back to Idle.
    /// </summary>
    private void UpdateCraftAtTable(ref AIContext ctx)
    {
        int i = ctx.UnitIndex;
        int envIdx = ctx.Units[i].CraftTableIdx;

        // Validate target table.
        if (envIdx < 0 || ctx.EnvSystem == null
            || envIdx >= ctx.EnvSystem.ObjectCount
            || !ctx.EnvSystem.GetObjectRuntime(envIdx).Alive)
        {
            CancelCraftAtTable(ref ctx);
            return;
        }

        var ts = ctx.EnvSystem.GetTableState(envIdx);
        var obj = ctx.EnvSystem.GetObject(envIdx);
        var tablePos = new Vec2(obj.X, obj.Y);

        // Craft completed externally? Transition to WorkEnd from any active phase.
        if (!ts.Crafting && ctx.Subroutine == BuildSub_WorkLoop)
        {
            ctx.Subroutine = BuildSub_WorkEnd;
            ctx.Units[i].CorpseInteractPhase = 3;
        }

        switch (ctx.Subroutine)
        {
            case BuildSub_WalkToSite:
            {
                ctx.Units[i].MoveTarget = tablePos;
                SubroutineSteps.MoveToPosition(ref ctx, ctx.MySpeed);

                var dir = tablePos - ctx.MyPos;
                if (dir.LengthSq() > 0.01f)
                    ctx.Units[i].FacingAngle = MathF.Atan2(dir.Y, dir.X) * 180f / MathF.PI;

                if (SubroutineSteps.MoveToPosition_Arrived(ref ctx, CraftInteractRange))
                {
                    ctx.Subroutine = BuildSub_WorkStart;
                    ctx.Units[i].PreferredVel = Vec2.Zero;
                    ctx.Units[i].CorpseInteractPhase = 1; // WorkStart anim
                }
                break;
            }
            case BuildSub_WorkStart:
                ctx.Units[i].PreferredVel = Vec2.Zero;
                FaceTowards(ref ctx, tablePos);
                if (ctx.Units[i].CorpseInteractPhase == 2)
                    ctx.Subroutine = BuildSub_WorkLoop;
                break;

            case BuildSub_WorkLoop:
                ctx.Units[i].PreferredVel = Vec2.Zero;
                FaceTowards(ref ctx, tablePos);
                // Craft timer is advanced by TableCraftingSystem; we sit in WorkLoop
                // (animation is Loop play mode) until ts.Crafting flips false above.
                break;

            case BuildSub_WorkEnd:
                ctx.Units[i].PreferredVel = Vec2.Zero;
                if (ctx.Units[i].CorpseInteractPhase == 0)
                {
                    ctx.Units[i].CraftTableIdx = -1;
                    ctx.Routine = RoutineIdle;
                    ctx.Subroutine = 0;
                }
                break;
        }
    }

    private static void FaceTowards(ref AIContext ctx, Vec2 target)
    {
        var dir = target - ctx.MyPos;
        if (dir.LengthSq() > 0.01f)
            ctx.Units[ctx.UnitIndex].FacingAngle = MathF.Atan2(dir.Y, dir.X) * 180f / MathF.PI;
    }

    /// <summary>Reset a unit out of the craft routine. Does NOT touch table state
    /// (ts.Crafting / ts.CraftTimer) — the player can re-engage by clicking Start
    /// again, which re-attaches the channeler and resumes from CraftTimer.</summary>
    public static void CancelCraftAtTable(ref AIContext ctx)
    {
        ctx.Units[ctx.UnitIndex].CraftTableIdx = -1;
        ctx.Units[ctx.UnitIndex].CorpseInteractPhase = 0;
        ctx.Routine = RoutineIdle;
        ctx.Subroutine = 0;
    }

    // ═══════════════════════════════════════
    //  Helpers
    // ═══════════════════════════════════════

    public static void CancelBuild(ref AIContext ctx)
    {
        ctx.Units[ctx.UnitIndex].BuildTargetIdx = -1;
        ctx.Units[ctx.UnitIndex].BuildGlyphIdx = -1;
        ctx.Units[ctx.UnitIndex].CorpseInteractPhase = 0;
        ctx.Units[ctx.UnitIndex].BuildTimer = 0f;
        ctx.Routine = RoutineIdle;
        ctx.Subroutine = 0;
    }

    public string GetRoutineName(byte routine) => routine switch
    {
        RoutineIdle => "Idle",
        RoutineBuildTrap => "BuildTrap",
        RoutineBuildGlyph => "BuildGlyph",
        RoutineCraftAtTable => "CraftAtTable",
        RoutineBagCorpse => "BagCorpse",
        RoutinePickupCorpse => "PickupCorpse",
        RoutinePutDownCorpse => "PutDownCorpse",
        _ => $"R{routine}"
    };

    public string GetSubroutineName(byte routine, byte subroutine) => routine switch
    {
        RoutineBuildTrap or RoutineBuildGlyph or RoutineCraftAtTable => subroutine switch
        {
            BuildSub_WalkToSite => "WalkToSite",
            BuildSub_WorkStart => "WorkStart",
            BuildSub_WorkLoop => "WorkLoop",
            BuildSub_WorkEnd => "WorkEnd",
            _ => $"S{subroutine}"
        },
        _ => $"S{subroutine}"
    };
}
