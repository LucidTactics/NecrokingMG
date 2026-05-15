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
    public const byte RoutineWorkOnBush = 7;

    // Build subroutines (shared by BuildTrap and BuildGlyph)
    public const byte BuildSub_WalkToSite = 0;
    public const byte BuildSub_WorkStart = 1;
    public const byte BuildSub_WorkLoop = 2;
    public const byte BuildSub_WorkEnd = 3;
    /// <summary>Sentinel emitted at the WorkLoop → WorkEnd transition. Game1's
    /// post-AI hook observes this on the same frame, applies the bush poison
    /// + consumes the potion, then sets the subroutine back to WorkEnd so the
    /// standup animation continues normally. The unit's BushWork* fields stay
    /// populated through WorkEnd so the AI can keep facing the bush.</summary>
    public const byte BushSub_AwaitFinalize = 250;

    public const float GlyphBuildDuration = 1.5f;
    public const float TrapBuildDuration = 1.5f;
    public const float BagDuration = 2.0f;
    public const float InteractRange = 1.0f;
    public const float BushWorkDuration = 2.0f;
    /// <summary>Bushes have collision_radius=0 so InteractRange (1.0) is close enough.</summary>
    public const float BushInteractRange = 1.2f;
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
            case RoutineWorkOnBush:
                UpdateWorkOnBush(ref ctx);
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
        bool inWorkEnd = ctx.Subroutine == WorkRoutine.WorkEnd;
        var glyph = ctx.MagicGlyphs?.GetGlyph(ctx.Units[i].BuildGlyphIdx);

        // Validate only during approach/work — once we're in standup the glyph
        // is intentionally no longer Blueprint (we activated it on WorkComplete).
        if (!inWorkEnd && (glyph == null || !glyph.Alive || glyph.State != GlyphState.Blueprint))
        {
            CancelBuild(ref ctx);
            return;
        }

        Vec2 targetPos = glyph != null ? glyph.Position : ctx.MyPos;
        var phase = WorkRoutine.Update(ref ctx, targetPos, InteractRange,
            GlyphBuildDuration, out float progress01);

        if (ctx.Subroutine == WorkRoutine.WorkLoop && glyph != null)
            glyph.BuildProgress = progress01;

        if (phase == WorkRoutine.Phase.WorkComplete && glyph != null)
        {
            glyph.State = GlyphState.Dormant;
            glyph.BuildProgress = 1f;
            glyph.StateTimer = 0f;
        }
        else if (phase == WorkRoutine.Phase.Done)
        {
            ctx.Units[i].BuildGlyphIdx = -1;
            ctx.Routine = RoutineIdle;
            ctx.Subroutine = 0;
        }
    }

    // ═══════════════════════════════════════
    //  Build Trap (env object)
    // ═══════════════════════════════════════

    private void UpdateBuildTrap(ref AIContext ctx)
    {
        int i = ctx.UnitIndex;
        int objIdx = ctx.Units[i].BuildTargetIdx;
        bool inWorkEnd = ctx.Subroutine == WorkRoutine.WorkEnd;

        // Validate only during approach/work — BuildProgress hits 1f on
        // WorkComplete so we'd self-cancel into the standup otherwise.
        if (!inWorkEnd && (objIdx < 0 || ctx.EnvSystem == null
            || objIdx >= ctx.EnvSystem.ObjectCount
            || !ctx.EnvSystem.GetObjectRuntime(objIdx).Alive
            || ctx.EnvSystem.GetObjectRuntime(objIdx).BuildProgress >= 1f))
        {
            CancelBuild(ref ctx);
            return;
        }

        Vec2 targetPos = ctx.MyPos;
        if (objIdx >= 0 && ctx.EnvSystem != null && objIdx < ctx.EnvSystem.ObjectCount)
        {
            var obj = ctx.EnvSystem.GetObject(objIdx);
            targetPos = new Vec2(obj.X, obj.Y);
        }

        var phase = WorkRoutine.Update(ref ctx, targetPos, InteractRange,
            TrapBuildDuration, out float progress01);

        if (ctx.Subroutine == WorkRoutine.WorkLoop && ctx.EnvSystem != null && objIdx >= 0)
        {
            var rt = ctx.EnvSystem.GetObjectRuntime(objIdx);
            rt.BuildProgress = progress01;
            ctx.EnvSystem.SetObjectRuntime(objIdx, rt);
        }

        if (phase == WorkRoutine.Phase.WorkComplete && ctx.EnvSystem != null && objIdx >= 0)
        {
            var rt = ctx.EnvSystem.GetObjectRuntime(objIdx);
            rt.BuildProgress = 1f;
            ctx.EnvSystem.SetObjectRuntime(objIdx, rt);
        }
        else if (phase == WorkRoutine.Phase.Done)
        {
            ctx.Units[i].BuildTargetIdx = -1;
            ctx.Routine = RoutineIdle;
            ctx.Subroutine = 0;
        }
    }

    // ═══════════════════════════════════════
    //  Craft at Table
    // ═══════════════════════════════════════

    /// <summary>
    /// Routine driver for channeling a table craft. Uses <see cref="WorkRoutine"/>
    /// for walk + animation phases. The completion signal is external: the loop
    /// runs indefinitely until <c>ts.Crafting</c> flips false (driven by
    /// <c>TableCraftingSystem.Tick</c>), at which point we call EndLoopNow to
    /// transition into the standup phase.
    /// </summary>
    private void UpdateCraftAtTable(ref AIContext ctx)
    {
        int i = ctx.UnitIndex;
        int envIdx = ctx.Units[i].CraftTableIdx;

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

        // Craft completed externally? End the WorkLoop early so standup plays.
        if (!ts.Crafting && ctx.Subroutine == WorkRoutine.WorkLoop)
            WorkRoutine.EndLoopNow(ref ctx);

        // Duration is "infinite" — only EndLoopNow above transitions out of WorkLoop.
        var phase = WorkRoutine.Update(ref ctx, tablePos, CraftInteractRange,
            float.MaxValue, out _);

        if (phase == WorkRoutine.Phase.Done)
        {
            ctx.Units[i].CraftTableIdx = -1;
            ctx.Routine = RoutineIdle;
            ctx.Subroutine = 0;
        }
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
    //  Work on Berry Bush (Poison Berries ability)
    // ═══════════════════════════════════════

    /// <summary>
    /// Player channels poison/paralysis onto a berry bush. Uses the shared
    /// <see cref="WorkRoutine"/> driver. Validates the target each tick — if
    /// the bush is no longer a valid Berries-state bush (destroyed, eaten,
    /// already poisoned by someone else), cancels with no inventory change.
    /// Successful WorkLoop completion applies the buff to the bush via
    /// <see cref="World.EnvironmentSystem.PoisonBerryBush"/> and consumes one
    /// of the matching potion item. Both side-effects are driven by Game1's
    /// post-AI hook, which inspects Routine/Subroutine and the BushWork fields
    /// to dispatch — the AI handler itself never touches inventory directly.
    /// </summary>
    private void UpdateWorkOnBush(ref AIContext ctx)
    {
        int i = ctx.UnitIndex;
        int objIdx = ctx.Units[i].BushWorkObjIdx;

        // AwaitFinalize: WorkLoop just ended this same frame. Hold position
        // facing the bush while Game1's post-AI hook applies the bush mutation
        // and switches us back to WorkEnd to play the standup animation.
        if (ctx.Subroutine == BushSub_AwaitFinalize)
        {
            ctx.Units[i].PreferredVel = Vec2.Zero;
            if (objIdx >= 0 && ctx.EnvSystem != null && objIdx < ctx.EnvSystem.ObjectCount)
            {
                var aobj = ctx.EnvSystem.GetObject(objIdx);
                WorkRoutine.FaceTowards(ref ctx, new Vec2(aobj.X, aobj.Y));
            }
            return;
        }

        // Validate target only while approaching / mid-work. Once we're in
        // WorkEnd the side effects have already fired so the bush has
        // legitimately flipped to Poisoned — must not cancel standup.
        bool inPostWork = ctx.Subroutine == WorkRoutine.WorkEnd;
        if (!inPostWork)
        {
            if (objIdx < 0 || ctx.EnvSystem == null
                || objIdx >= ctx.EnvSystem.ObjectCount
                || !ctx.EnvSystem.GetObjectRuntime(objIdx).Alive)
            {
                CancelBushWork(ref ctx);
                return;
            }
            var def = ctx.EnvSystem.GetDef(ctx.EnvSystem.GetObject(objIdx).DefIndex);
            if (!def.IsBerryBush
                || ctx.EnvSystem.GetObjectRuntime(objIdx).BerryState != World.BerryState.Berries)
            {
                CancelBushWork(ref ctx);
                return;
            }
        }

        Vec2 targetPos = Vec2.Zero;
        if (objIdx >= 0 && ctx.EnvSystem != null && objIdx < ctx.EnvSystem.ObjectCount)
        {
            var obj = ctx.EnvSystem.GetObject(objIdx);
            targetPos = new Vec2(obj.X, obj.Y);
        }

        var phase = WorkRoutine.Update(ref ctx, targetPos, BushInteractRange,
            BushWorkDuration, out _);
        if (phase == WorkRoutine.Phase.WorkComplete)
        {
            // One-shot transition: WorkLoop just finished. Game1's hook will
            // apply the bush + inventory change this same frame and flip us
            // back to WorkEnd so the standup animation plays out.
            ctx.Subroutine = BushSub_AwaitFinalize;
        }
        else if (phase == WorkRoutine.Phase.Done)
        {
            CancelBushWork(ref ctx);
        }
    }

    public static void CancelBushWork(ref AIContext ctx)
    {
        ctx.Units[ctx.UnitIndex].BushWorkObjIdx = -1;
        ctx.Units[ctx.UnitIndex].BushWorkBuffID = "";
        ctx.Units[ctx.UnitIndex].BushWorkItemID = "";
        WorkRoutine.Reset(ref ctx);
        ctx.Routine = RoutineIdle;
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
