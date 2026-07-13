using System;
using Microsoft.Xna.Framework;
using Necroking.Core;
using Necroking.Data.Registries;
using Necroking.Lib;
using Necroking.World;

namespace Necroking.Render;

/// <summary>Tunable constants for the wading visual system. Centralized here
/// so a tweak doesn't need to be applied in three different files.</summary>
public static class WadingConfig
{
    /// <summary>Raw bilinear waterness value (0..1, sampled from the ground
    /// vertex map) that corresponds to the visible shoreline (where the
    /// ground shader transitions from grass to water). Used to remap the
    /// raw value so the visible shore = 0 depth and full water = 1 depth.</summary>
    public const float ShorelineMidpoint = 0.5f;

    /// <summary>Radius of the 5-sample waterness kernel in world units. The
    /// shore→fully-wading transition spans roughly 2×KernelRadius world units.</summary>
    public const float KernelRadius = 1.0f;

    /// <summary>Minimum effective waterness for the wading effect to activate.
    /// Below this threshold the unit renders as if on dry land — no shader
    /// switch, no foam ring, no shadow change. Keeps a tiny floating-point
    /// drift from triggering the wading branch on every grass tile.</summary>
    public const float MinWaternessForWading = 0.001f;

    /// <summary>|cos(facing)| threshold below which the diagonal cut slope is
    /// forced to 0 (horizontal). N/S facings have body axis vertical in screen
    /// — physically the cut would be vertical too, but artistic convention is
    /// horizontal.</summary>
    public const float CosThresholdForHorizontal = 0.05f;

    /// <summary>Absolute clamp on the diagonal cut slope. Caps absurd values
    /// from facings near the N/S transition.</summary>
    public const float MaxBodySlope = 1.5f;

    /// <summary>Unit height (world units, <see cref="Necroking.Movement.Unit.Z"/>)
    /// at which the wading waterline has fully faded out — a unit flying this high
    /// over the water is drawn un-submerged. Between 0 and this the waterline
    /// recedes linearly as the unit rises, so a jump lifts cleanly out of the water
    /// instead of dragging a submerged bottom across the arc.</summary>
    public const float AirborneFadeHeight = 0.5f;
}

/// <summary>Default per-direction wading fractions. Applied when a unit def
/// leaves the per-direction fields null — so every new quadruped added to
/// units.json gets a sensible waterline without any JSON tweaking, and
/// per-unit overrides are only needed when the body proportions differ from
/// the typical low-slung quadruped (wolf, deer, boar, bear, …).
///
/// Values were tuned on the wolf sprite and tend to read well on any
/// quadruped whose body fills the lower ~60% of the sprite frame:
///  • Bottom: deepest from the rear (N) where the body sits visually lower
///    in the frame, shallowest head-on (S) where the chest is high.
///  • Top: only the head-on (S) view trims the back/rump because that's
///    where the back protrudes above the waterline for the camera.
///
/// Bipeds fall back to the scalar <see cref="UnitDef.WadingWaterlineFraction"/>
/// (uniform across facings) — that matched the original hardcoded behavior.
///
/// <see cref="DirectionalFractions"/> now stores all 8 sectors (cardinals +
/// intercardinals) so each can be tuned independently. The defaults below
/// are the loaded-at-startup values from <c>data/wading_defaults.json</c>
/// — when that file is missing, the embedded literals here apply.
///
/// Editable from the unit editor's wading sub-tool: the "Save as default"
/// button writes the in-editor values back to the JSON file, replacing
/// these on the next game launch.</summary>
public static class WadingDefaults
{
    public static readonly DirectionalFractions QuadrupedBottom = new()
    {
        N = 0.35f, NE = 0.45f, E = 0.55f, SE = 0.40f,
        S = 0.25f, SW = 0.40f, W = 0.55f, NW = 0.45f
    };

    public static readonly DirectionalFractions QuadrupedTop = new()
    {
        N = 0.0f, NE = 0.0f, E = 0.0f, SE = 0.05f,
        S = 0.10f, SW = 0.05f, W = 0.0f, NW = 0.0f
    };

    /// <summary>Replace the static defaults with values loaded from
    /// data/wading_defaults.json. Called at startup by GameData.Load
    /// after the JSON is read. If any field is missing in the JSON the
    /// embedded literal value above is kept for that field.</summary>
    public static void Apply(DirectionalFractions? bottom, DirectionalFractions? top)
    {
        if (bottom != null) QuadrupedBottom.CopyFrom(bottom);
        if (top != null) QuadrupedTop.CopyFrom(top);
    }
}

/// <summary>Snapshot of a unit's wading state for one frame. Computed once
/// per (unit, frame) via <see cref="Compute"/> and consumed by both the
/// sprite-render path (Game1.DrawSingleUnit) and the shadow path
/// (ShadowRenderer) — without this, the same ~25 lines of waterness +
/// fraction-lookup math live in both files and drift apart as features
/// are added.</summary>
public readonly struct WadingState
{
    /// <summary>Effective waterness 0..1. 0 at (or before) the visible
    /// shoreline; 1 fully in water. This is the SCALING factor for the
    /// wading depth — multiply per-direction fractions by this.</summary>
    public readonly float Waterness;

    /// <summary>Local frame V (0=top, 1=bottom) where the bottom wading cut
    /// sits. Equal to bodyBottomV when Waterness=0 (no cut); rises toward
    /// bodyTopV as Waterness→1.</summary>
    public readonly float WaterlineV;

    /// <summary>Local frame V of the top cut (back-submerged / swimming pose).
    /// -1 disables the cut (the value the wading shader uses as a sentinel).</summary>
    public readonly float TopWaterlineV;

    /// <summary>Bottom-cut slope dV/dU in local frame UV. Non-zero only for
    /// quadrupeds in 3/4 sprite buckets (cardinals get slope=0). Tilts to
    /// follow the body axis projected to screen so the waterline runs along
    /// the body's footprint rather than horizontally chopping it.</summary>
    public readonly float Slope;

    /// <summary>Resolved (quantized) sprite angle in Necroking convention
    /// (0=E, 90=S). Same value the sprite is rendered with — exposed so
    /// callers needing it (e.g. for foam-ring layout) don't have to call
    /// AnimController.ResolveAngle a second time.</summary>
    public readonly int SpriteAngle;

    /// <summary>True if the wading effect should apply this frame.</summary>
    public bool Active => Waterness > WadingConfig.MinWaternessForWading;

    private WadingState(float waterness, float waterlineV, float topWaterlineV,
                        float slope, int spriteAngle)
    {
        Waterness = waterness;
        WaterlineV = waterlineV;
        TopWaterlineV = topWaterlineV;
        Slope = slope;
        SpriteAngle = spriteAngle;
    }

    /// <summary>Compute the wading state for a unit at <paramref name="worldPos"/>
    /// with the given facing. Reads the body bbox from <paramref name="frame"/>
    /// and the per-direction wading fractions from <paramref name="unitDef"/>.</summary>
    public static WadingState Compute(
        Vec2 worldPos,
        float unitZ,
        float facingAngle,
        in SpriteFrame frame,
        UnitDef unitDef,
        AnimController animCtrl,
        GroundSystem? groundSystem,
        float cameraYRatio)
    {
        // Sample waterness (kernel-averaged for smooth shoreline transition),
        // then remap so the visible shoreline (~0.5 raw) = 0 effective.
        float waternessRaw = groundSystem != null
            ? groundSystem.SampleWaternessSmoothed(worldPos, WadingConfig.KernelRadius)
            : 0f;
        float waterness = MathHelper.Clamp(
            (waternessRaw - WadingConfig.ShorelineMidpoint) * 2f, 0f, 1f);

        // Airborne units (a rogue/engine jump, a knockback) fly OVER the water, so
        // the waterline must not submerge them. Fade it out as they lift off; above
        // AirborneFadeHeight there is no wading at all. Waterness=0 makes the whole
        // effect inactive below (no cut, no foam, full shadow).
        if (unitZ > 0f)
            waterness *= MathHelper.Clamp(1f - unitZ / WadingConfig.AirborneFadeHeight, 0f, 1f);

        int spriteAngle = animCtrl.ResolveAngle(facingAngle, out _);

        // Per-orientation reference body bbox — averaged across the idle
        // animation's frames at this angle. We use this instead of the
        // current frame's pixel-derived bbox so the waterline stays in
        // the same screen position as the unit cycles through animation
        // frames. Without this, each frame's bbox shifts (e.g. as a
        // quadruped's legs extend/tuck) and the waterline appears to
        // float up and down on the body. Fallback to the current frame's
        // values when the sprite data has no reference (test scenarios,
        // missing Idle anim, etc).
        var (bodyTopV, bodyBottomV) = animCtrl.GetReferenceBodyBbox(
            spriteAngle, frame.BodyTopV, frame.BodyBottomV);
        float waterlineV = bodyBottomV;
        float topWaterlineV = -1f;
        float slope = 0f;

        if (waterness > WadingConfig.MinWaternessForWading)
        {
            // Resolve bottom waterline fraction with this priority:
            //   1. Explicit per-direction override on the unit def (artist tuned this unit)
            //   2. Quadruped default profile (every quadruped gets this for free)
            //   3. Scalar fallback (bipeds — uniform across facings)
            // Same priority chain for the top cut, except bipeds default to "no cut".
            float bottomFracBase;
            if (unitDef.WadingFractionByDirection != null)
                bottomFracBase = unitDef.WadingFractionByDirection.Sample(spriteAngle);
            else if (unitDef.IsQuadruped)
                bottomFracBase = WadingDefaults.QuadrupedBottom.Sample(spriteAngle);
            else
                bottomFracBase = unitDef.WadingWaterlineFraction;
            float bottomFrac = MathHelper.Clamp(bottomFracBase, 0f, 1f) * waterness;
            waterlineV = bodyBottomV - bottomFrac * (bodyBottomV - bodyTopV);

            float topFracBase;
            if (unitDef.WadingTopFractionByDirection != null)
                topFracBase = MathHelper.Clamp(unitDef.WadingTopFractionByDirection.Sample(spriteAngle), 0f, 1f);
            else if (unitDef.IsQuadruped)
                topFracBase = MathHelper.Clamp(WadingDefaults.QuadrupedTop.Sample(spriteAngle), 0f, 1f);
            else
                topFracBase = 0f;
            float topFrac = topFracBase * waterness;
            topWaterlineV = topFrac > 0f
                ? bodyTopV + topFrac * (bodyBottomV - bodyTopV)
                : -1f;

            // Diagonal bottom slope only for quadrupeds in 3/4 facings — it
            // tilts to follow the body axis projected to screen so legs
            // disappear along the body's length, not via a horizontal chop.
            //
            // Top cut is disabled on diagonals: every variant we tried
            // (parallel to bottom, perpendicular to bottom) read poorly on
            // SE/SW because the visible body silhouette is too irregular for
            // a clean line. Cardinals (S in particular) still get a top cut
            // — that's the case where "back submerged" makes visual sense.
            if (unitDef.IsQuadruped)
            {
                float spriteAngleRad = spriteAngle * MathF.PI / 180f;
                float cosA = MathF.Cos(spriteAngleRad);
                float sinA = MathF.Sin(spriteAngleRad);
                if (MathF.Abs(cosA) >= WadingConfig.CosThresholdForHorizontal)
                {
                    slope = sinA * cameraYRatio / cosA;
                    slope = MathHelper.Clamp(slope, -WadingConfig.MaxBodySlope, WadingConfig.MaxBodySlope);
                    if (MathF.Abs(slope) > 0.001f)
                        topWaterlineV = -1f;
                }
            }
        }

        return new WadingState(waterness, waterlineV, topWaterlineV, slope, spriteAngle);
    }

    /// <summary>Visible body V range after applying the wading cuts — what
    /// the shadow projection should sample/extend. When Waterness=0 this is
    /// just the body bbox.</summary>
    public (float topV, float bottomV) GetVisibleBodyRange(in SpriteFrame frame)
    {
        float visibleTopV = frame.BodyTopV;
        float visibleBottomV = frame.BodyBottomV;
        if (Active)
        {
            // Top cut clamps the visible top down (TopWaterlineV >= bodyTopV
            // when active; -1 sentinel falls below bodyTopV so max() picks bodyTopV).
            visibleTopV = MathF.Max(frame.BodyTopV, TopWaterlineV);
            visibleBottomV = MathF.Min(frame.BodyBottomV, WaterlineV);
        }
        return (visibleTopV, MathF.Max(visibleTopV + 0.001f, visibleBottomV));
    }
}
