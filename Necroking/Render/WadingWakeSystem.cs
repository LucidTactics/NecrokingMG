using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Necroking.Core;

namespace Necroking.Render;

/// <summary>What asset + animation this particle draws as. Selects which
/// texture and frame-selection rule the draw pass uses.</summary>
public enum WakeParticleKind : byte
{
    /// <summary>Soft procedural circle texture. Used for the bow wave,
    /// which needs a small continuous foam look that the splash-shaped
    /// MiniSplash frames don't quite fit.</summary>
    SoftCircle = 0,

    /// <summary>FX_TX_MiniSplash 2×6 flipbook — small splash that
    /// dissipates over its frames. Plays once across the particle's
    /// lifetime. Used for the trailing-behind wake.</summary>
    MiniSplash = 1,

    /// <summary>FX_TX_BubbleMagic 5×5 flipbook — bubble forming then
    /// breaking. Used for the AIRBORNE splash droplets that arc up and
    /// down. Lifetime is set to predicted airborne time so the animation
    /// completes right as the droplet lands.</summary>
    BubbleMagic = 2,

    /// <summary>FX_TX_RainSplash 3×3 flipbook — crown of water rising
    /// then ripple dispersing. Used when a BubbleMagic droplet LANDS:
    /// the airborne particle transforms in-place to this kind, and the
    /// rain-splash animation plays through at the impact point.</summary>
    RainSplash = 3,
}

/// <summary>One foam particle in a wading wake. Position is on the
/// ground/water plane (X, Y world units); WorldHeight lifts the rendered
/// screen position so the particle aligns with the unit's visible
/// waterline cut rather than the unit's feet pivot.
///
/// For trail / bow wave: WorldHeight is constant (set at spawn to the
/// unit's waterline-lift height) — they "float" at the waterline.
/// HasGravity=false. Death by Lifetime.
///
/// For entry splash (BubbleMagic): HasGravity=true, VertVel is the
/// upward velocity, WorldHeight animates each frame. When WorldHeight
/// drops to LandHeight, the AgeParticles landing branch transforms
/// the particle in place to a RainSplash (Kind reassigned, Age reset,
/// new Lifetime set) — so the rain-splash animation plays at the
/// impact point, replacing the bubble.
///
/// LandHeight is the height the droplet should LAND at (not where it
/// spawned). For an entry splash that's the water surface (droplet
/// arcs up and falls back to it). For an EXIT splash — water dripping
/// off a unit that just stepped onto dry land — that's 0 (ground),
/// so drips spawn at the body's waterline height and fall all the way
/// to the ground.
///
/// IsFront controls draw layering: false = drawn before the sprite (gets
/// covered if it drifts into the unit's silhouette — right for the trail),
/// true = drawn after the sprite (always visible — right for bow wave and
/// splash, which spawn ahead of the unit and overlap the body for N-facing
/// motion where "ahead" is screen-up).</summary>
public struct WakeParticle
{
    public Vec2 Pos;
    public Vec2 Vel;
    public float VertVel;
    public float WorldHeight;
    public float LandHeight;
    public float Age;
    public float Lifetime;
    public float LandedAge;
    public float Size;
    public float Rotation;
    public WakeParticleKind Kind;
    public bool IsFront;
    public bool HasGravity;
    /// <summary>Index into <see cref="WadingWakeSystem"/>'s per-water-tint
    /// pre-baked texture arrays. 0 = default (untinted) variant; > 0 = a
    /// variant tinted to match the water type at the spawn position. The
    /// particle keeps its spawn-time tint for its whole lifetime — a real
    /// water droplet doesn't change colour mid-air just because the unit
    /// drifted across a shoreline.</summary>
    public byte VariantIdx;
}

/// <summary>Per-unit emitter state. Separate spawn accumulators for trail
/// and bow wave (they spawn at independent rates and shouldn't share the
/// fractional carry-over). WasWading tracks the wading-state edge so an
/// entry splash only fires on the false→true transition, not every frame
/// the unit is in water.
///
/// Splash session fields (SplashRemainingDuration, SplashRemainingCount,
/// SplashSpawnAccum) represent an active "trickle" — after the initial
/// burst on entry, the splash continues to spawn particles over the next
/// EntrySplashSessionDurationSec, tracking the unit's current position
/// each frame. RemainingDuration > 0 means a session is active.</summary>
public class WakeEmitterState
{
    public List<WakeParticle> Particles = new();
    public float TrailSpawnAccum;
    public float BowWaveSpawnAccum;
    public bool WasWading;

    /// <summary>Captured each frame the unit is wading. Used by the
    /// exit-splash trigger because the false-wading frames don't carry
    /// a waterline height through the call signature.</summary>
    public float LastWaterlineHeight;

    /// <summary>Maximum waterline height the unit reached during the
    /// CURRENT wading session. Resets when a new entry edge fires.
    /// Higher value = unit went deeper, water touched higher up the
    /// body. Used as the spawn ceiling for exit drips so a unit that
    /// briefly stepped ankle-deep gets a small drip from low on the
    /// body, while a unit that went chest-deep drips from up high.</summary>
    public float MaxWaterlineHeightThisSession;

    /// <summary>Cached unit velocity at the moment of exit. Each
    /// trickled exit-splash droplet inherits this rather than the
    /// current unit velocity, so the drip motion stays coherent even
    /// if the unit suddenly stops or turns after exiting the water.</summary>
    public Vec2 ExitVelocity;

    // --- Entry splash session ---
    public float SplashRemainingDuration;
    public int SplashRemainingCount;
    public float SplashSpawnAccum;
    public float SplashEntrySpeed; // captured at session start, used for size scaling
    /// <summary>Body half-vector captured at entry-splash session start.
    /// Trickle spawns reuse this so the spawn axis stays coherent even if
    /// the unit turns mid-session.</summary>
    public Vec2 BodyHalfAtStart;

    // --- Exit splash session (mirrors entry layout but for drips
    // falling FROM the waterline TO the ground after the unit leaves
    // water). ExitRemainingDuration > 0 means an exit drip session
    // is in flight, releasing more droplets each frame.
    public float ExitRemainingDuration;
    public int ExitRemainingCount;
    public float ExitSpawnAccum;
    public float ExitSpeedAtStart; // captured at exit, for size scaling
    public float ExitWaterlineHeight; // captured at exit, for drip spawn height
    /// <summary>Body half-vector captured at exit moment. Drip trickle
    /// spawns use this so all drips spread along the same body axis the
    /// unit had when exiting, even if the unit turns on dry land.</summary>
    public Vec2 ExitBodyHalf;

    /// <summary>Wake-particle variant index from the last frame the unit
    /// was actually in water. Used to tint exit-splash drips with the
    /// colour of the water they just left rather than re-sampling at the
    /// dry-land spawn position (which would always return 0 = untinted).
    /// Refreshed every wading frame and frozen at the wading-true→false
    /// edge until the next session.</summary>
    public byte LastWaterVariantIdx;
}

/// <summary>
/// Trailing-foam wake for units wading through water.
///
/// Spawning: while a unit's wading state is active AND it's moving above a
/// minimum speed, particles are emitted from the unit's footprint at a rate
/// proportional to speed, with initial velocity biased rearward (opposite
/// the unit's movement direction). The bias produces a V-shaped trailing
/// wake rather than a symmetric ring — which is what made the previous
/// flat-ellipse foam ring read as a "ring under the unit" instead of as
/// motion through water.
///
/// Rendering: particles live on the ground plane (drift in world X/Y)
/// but draw at a constant <see cref="WakeParticle.WorldHeight"/> above
/// ground so they appear aligned with the sprite's visual waterline cut
/// (which is well above the unit's pivot for waist-deep wading).
///
/// Scaling: per-unit emitter state, no global update pass — the draw loop
/// calls <see cref="UpdateAndDraw"/> once per visible unit. Particles cap
/// at <see cref="MaxParticlesPerUnit"/> to bound worst-case cost.
/// </summary>
public class WadingWakeSystem
{
    // --- Tunables (Subtle wisps preset — dial up if too sparse) ---
    /// <summary>Particles emitted per world-unit of motion. Bumped from 4.0
    /// → 5.2 (+30%) — denser trailing wake without losing the "subtle"
    /// quality.</summary>
    public const float ParticlesPerWorldUnitOfMotion = 5.2f;

    /// <summary>Per-particle lifetime range. Wider spread (0.4-1.0) makes
    /// the wake feel less uniform — some dots blip out quickly while others
    /// linger, which reads more naturally than synchronized fade.</summary>
    public const float MinLifetimeSec = 0.40f;
    public const float MaxLifetimeSec = 1.00f;

    /// <summary>Particle diameter in world units. Cumulative bumps from the
    /// first pass: +30%, then +20% more = 1.56× original.</summary>
    public const float MinSizeWorld = 0.0624f;
    public const float MaxSizeWorld = 0.1560f;

    /// <summary>Fraction of lifetime spent shrinking from full size to zero.
    /// 0.20 → for the final 20% of a particle's life it shrinks linearly,
    /// which combined with the alpha fade reads as the dot dissipating
    /// rather than just disappearing.</summary>
    public const float ShrinkFraction = 0.20f;

    /// <summary>Fraction of the unit's velocity inherited by particles at
    /// spawn (in the rearward direction with spread). Lower = particles
    /// drift less, sit closer to the unit. Higher = longer trailing wake.</summary>
    public const float DriftFactor = 0.35f;

    /// <summary>Half-cone of random spread around the rear direction
    /// (radians). ~57° here = particles fan out behind the unit rather
    /// than all going dead-rearward.</summary>
    public const float SpreadHalfConeRad = 1.0f;

    /// <summary>Per-second exponential drag on particle velocity. 1.8 →
    /// velocity halves in ~0.4s, particle has effectively stopped before
    /// it dies.</summary>
    public const float DragPerSec = 1.8f;

    /// <summary>Below this unit speed (wu/s), no spawning. Stops the
    /// wake from emitting when the unit is essentially stationary.</summary>
    public const float MinSpeedToEmit = 0.05f;

    /// <summary>Hard cap so a unit teleporting or in a feedback loop
    /// can't accumulate thousands of particles. Raised to 48 to make room
    /// for the bow wave + occasional entry splash burst.</summary>
    public const int MaxParticlesPerUnit = 48;

    /// <summary>Default body length (world units) used for quadrupeds when
    /// the unit def's <c>BodyLengthWorld</c> is 0 (unset). The wolf-like
    /// reference body — head-to-tail extent on a typical quadruped sprite
    /// — sits around 0.9 wu. Particles spawned by the wake system spread
    /// across this length along the unit's facing axis so the trail
    /// emerges from the rear, the bow wave from the front, and splashes
    /// distribute across the body silhouette instead of clustering at the
    /// single pivot point. Per-unit overrides via UnitDef.BodyLengthWorld
    /// for unusual proportions (horses, snakes, badgers).</summary>
    public const float QuadrupedDefaultBodyLength = 0.9f;

    /// <summary>Default maximum sink offset in world units at full wading
    /// (waterness = 1). Positive Y is south on screen — the sprite shifts
    /// downward so the body appears to descend into the water. Combined
    /// with the existing waterline-cut-on-body effect (which covers more
    /// of the upper body as waterness rises), this creates the impression
    /// of the unit walking into deeper water. Per-unit overrides via
    /// UnitDef.WadingSinkWorld (or set to a negative value to disable).
    /// 0.5 wu is roughly belly-deep for a typical humanoid at full wading.</summary>
    public const float DefaultMaxSinkWorld = 0.5f;

    /// <summary>Resolve the per-frame sink offset for a unit at the given
    /// waterness. Centralised so the pre-draw pass and any future
    /// callers (potential debug overlay) compute it the same way.</summary>
    public static float ComputeSinkOffset(float waterness, float maxSinkWorld)
    {
        if (maxSinkWorld <= 0f) return 0f;
        return MathHelper.Clamp(waterness, 0f, 1f) * maxSinkWorld;
    }

    // --- Bow wave (front foam streak) ---
    // Reference: Sea of Thieves, Witcher 3, RIME — small crescent of foam
    // riding just ahead of the wading character, oriented along motion.
    // Implemented as a denser-but-tighter version of the trail emitted
    // forward of the unit instead of behind. Same particle material so the
    // bow wave reads as part of the same wake system.

    /// <summary>Density of bow-wave particles per world-unit of motion.
    /// Tighter spread + ahead-of-unit position means fewer particles read
    /// as a denser feature than the same rate would behind the unit.</summary>
    public const float BowWaveParticlesPerWorldUnitOfMotion = 6.0f;

    /// <summary>How far in front of the unit (along velocity direction)
    /// the bow wave particles spawn. Min/max gives a small range so they
    /// don't all stack at one point.</summary>
    public const float BowWaveForwardOffsetMin = 0.30f;
    public const float BowWaveForwardOffsetMax = 0.50f;

    /// <summary>Lateral half-spread of the bow wave (world units to
    /// either side of the motion line). Narrow → the wave reads as a
    /// crescent, not a cloud.</summary>
    public const float BowWaveLateralSpread = 0.22f;

    /// <summary>Outward + slightly-back drift speed of bow-wave particles,
    /// as a fraction of unit speed. They slip past the unit as they age.</summary>
    public const float BowWaveDriftFactor = 0.25f;

    /// <summary>Minimum unit speed for the bow wave to emit. Higher than
    /// the trail's threshold — a stationary or shuffling unit shouldn't
    /// kick up a front wave.</summary>
    public const float BowWaveMinSpeedToEmit = 0.25f;

    /// <summary>Shorter than trail life — bow wave should stay tight to
    /// the front, not linger.</summary>
    public const float BowWaveMinLifetimeSec = 0.25f;
    public const float BowWaveMaxLifetimeSec = 0.50f;

    /// <summary>Bow wave particle size range (world units). Bow wave uses
    /// the MiniSplash flipbook (same as trail) but rendered slightly
    /// smaller so the densely-emitted front fan doesn't look like one
    /// big foam mass — each splash reads as its own beat.</summary>
    public const float BowWaveMinSizeWorld = 0.22f;
    public const float BowWaveMaxSizeWorld = 0.42f;

    // --- Entry splash (one-shot on shore→water transition) ---
    // Reference: Diablo IV, RIME, Trifox — a brief burst of foam droplets
    // when a character first steps into water. Triggered by the wading-
    // state edge, not by per-frame conditions.
    //
    // Speed-scaled: a stationary entry produces a baseline "plop", a
    // walking entry produces a forward fan, a sprinting entry throws
    // water hard forward + up. Droplets inherit a fraction of the unit's
    // forward velocity (per user spec) so the splash reads as energy
    // imparted by the unit's motion, not a canned animation.
    //
    // Physics (when HasGravity=true on a particle):
    //  - Horizontal velocity decays with strong drag (water spreads + slows)
    //  - Vertical: VertVel adds to WorldHeight; gravity decelerates VertVel
    //    Droplets arc up then fall back down — read as water flung in air.

    /// <summary>Vertical acceleration on splash droplets (wu/s²). At 20
    /// the arc reads as snappy water rather than floating debris — droplets
    /// peak in ~0.1-0.4s and fall back as quickly. The earlier value (8)
    /// made droplets hang in air. Note: in the iso projection
    /// (Camera25D.WorldToScreen) world-height is foreshortened by YRatio
    /// (~0.6), so visual fall speed already reads slightly slower than
    /// the numerical gravity; we want the numerical value clearly higher
    /// than real-world ~9.8 to compensate.</summary>
    public const float EntrySplashGravity = 20.0f;

    /// <summary>Per-second drag on splash droplet horizontal velocity.
    /// Stronger than trail drag (1.8) — droplet horizontal speed halves
    /// every ~0.14s. Matches the spec "decay very quickly as water tends
    /// to spread out and slow down."</summary>
    public const float EntrySplashDragPerSec = 5.0f;

    /// <summary>Baseline particle count for a stationary entry. A unit
    /// teleported onto a water tile still shows a small "plop."</summary>
    public const int EntrySplashBaseCount = 8;

    /// <summary>Additional particles per wu/s of entry speed. Walk
    /// (~1 wu/s) → ~12-13, jog (~3 wu/s) → ~22, hits the cap at sprint.</summary>
    public const float EntrySplashCountPerSpeed = 4.5f;

    /// <summary>Hard cap on splash particle count regardless of speed —
    /// keeps the burst from eating the whole MaxParticlesPerUnit budget
    /// and starving the trail/bow for the next half-second of wading.</summary>
    public const int EntrySplashMaxCount = 30;

    /// <summary>Omnidirectional outward speed every droplet gets at spawn
    /// even when the unit is stationary. The "baseline plop" that gives
    /// stationary entries some visible energy.</summary>
    public const float EntrySplashOmniBaseSpeed = 0.8f;

    /// <summary>Per-droplet forward velocity inheritance: each droplet's
    /// initial forward velocity ≈ unit speed × this × random(0.5..1.5).
    /// 1.0 means "droplets fly forward at roughly the unit's running
    /// speed" (per user spec) — running unit → fast forward-flying droplets,
    /// walking unit → slow forward-flying droplets.</summary>
    public const float EntrySplashForwardInheritFactor = 1.0f;

    /// <summary>Baseline upward velocity every droplet gets at spawn,
    /// even for a stationary entry.</summary>
    public const float EntrySplashUpBaseSpeed = 2.0f;

    /// <summary>Additional upward velocity per wu/s of unit speed,
    /// applied at full rate up to <see cref="EntrySplashVertSpeedKnee"/>
    /// and at the reduced <see cref="EntrySplashVertSpeedRunFactor"/>
    /// rate beyond it.</summary>
    public const float EntrySplashUpPerSpeed = 1.2f;

    /// <summary>Speed (wu/s) below which the upward-velocity contribution
    /// scales linearly with unit speed; above it the slope reduces.
    /// 1.5 sits at the top of walking range — walking unit gets the full
    /// vertical pop, running unit's vertical contribution tapers.</summary>
    public const float EntrySplashVertSpeedKnee = 1.5f;

    /// <summary>Multiplier on the per-speed vertical contribution above
    /// the knee. 0.4 = vert contribution above walk speed is dialed
    /// to ~40% — net effect at sprint (~5 wu/s) is roughly 70% of what
    /// pure-linear scaling would produce. Reads as "running splashes
    /// more energetic forward, but doesn't fountain water." Walking
    /// speeds are unaffected.</summary>
    public const float EntrySplashVertSpeedRunFactor = 0.4f;

    /// <summary>Base size range for splash droplets at zero speed. Speed
    /// then multiplies this (see <see cref="EntrySplashSizeSpeedFactor"/>).</summary>
    public const float EntrySplashMinSizeBase = 0.12f;
    public const float EntrySplashMaxSizeBase = 0.22f;

    /// <summary>Per-wu/s additional size multiplier. At zero speed
    /// droplets are at base size; at sprint (~5 wu/s) they're up to
    /// 1.75× base — running splashes throw bigger chunks of water.</summary>
    public const float EntrySplashSizeSpeedFactor = 0.15f;
    public const float EntrySplashMaxSizeMultiplier = 1.8f;

    /// <summary>Splash airborne-lifetime failsafe. Splash droplets normally
    /// die after landing (via <see cref="LandedFadeDurationSec"/>) — this
    /// range is just an upper bound in case the arc somehow never lands
    /// (numerical drift, gravity 0, etc). 1.5s comfortably covers the
    /// worst-case airborne time (sprint vert≈3 wu/s, lands at ~0.75s)
    /// plus the post-land fade.</summary>
    public const float EntrySplashMinLifetimeSec = 1.20f;
    public const float EntrySplashMaxLifetimeSec = 1.60f;

    /// <summary>Time from a splash droplet landing back on the water
    /// surface to its visual fade-out + shrink completing. Reads as the
    /// foam dot dissipating where it landed, instead of the droplet
    /// popping out mid-arc.</summary>
    public const float LandedFadeDurationSec = 0.35f;

    /// <summary>Horizontal drag applied while a splash droplet is sitting
    /// on the water (post-landing). Heavier than the airborne drag so
    /// the foam dot doesn't continue drifting — it settles in place and
    /// fades.</summary>
    public const float EntrySplashLandedDragPerSec = 8.0f;

    /// <summary>Half-cone (radians) of the splash's forward spread when
    /// the entry has clear forward motion. ~86° = fans across the unit's
    /// forward hemisphere. Below the speed threshold the splash goes
    /// fully omnidirectional (π).</summary>
    public const float EntrySplashForwardHalfConeRad = 1.5f;

    /// <summary>Above this speed at the moment of entry, splash fans
    /// forward; below, omnidirectional. ~0.2 wu/s separates "running in"
    /// from "stepping in slowly / spawned in place."</summary>
    public const float EntrySplashForwardSpeedThreshold = 0.20f;

    /// <summary>Fraction of the splash's total particle count released
    /// as an immediate burst at the moment of entry. The remaining
    /// (1 - this) is released over <see cref="EntrySplashSessionDurationSec"/>
    /// and tracks the unit's current position so droplets spawn at
    /// where the unit IS, not just where it entered.</summary>
    public const float EntrySplashInitialBurstFraction = 0.20f;

    /// <summary>How long the post-burst trickle continues after entry.
    /// During this window the splash session re-spawns particles each
    /// frame at the unit's current position — so a running unit lays
    /// down a short trail of splash bursts as it advances into the water,
    /// not just one stationary explosion at the shoreline crossing.</summary>
    public const float EntrySplashSessionDurationSec = 0.5f;

    // --- Exit splash (water dripping off the unit after they leave water) ---
    // "Anti-splash" — droplets spawn at the body's waterline height and
    // FALL to the ground. Same flipbook chain as entry (BubbleMagic ↔
    // RainSplash via the landing transform), same gravity + drag.
    // Differences from entry:
    //  • No upward velocity — VertVel starts at zero, gravity pulls down
    //  • Spawn height = the body's waterline; landing target = ground (0)
    //  • Inherits unit velocity directly (no omni-burst component)
    //  • Smaller count + sizes — drips, not a fountain

    /// <summary>How long the drip session continues after the unit
    /// leaves water (seconds). Plus per-droplet airtime (~0.4-0.6s for
    /// typical waterline height) gives a total visual duration around
    /// the user-spec'd ~1 second.</summary>
    public const float ExitSplashSessionDurationSec = 0.55f;

    /// <summary>Initial fraction of total particle count released
    /// immediately on the exit edge. Small (10%) — exit drips are
    /// gradual, no burst beat like the entry "step in."</summary>
    public const float ExitSplashInitialBurstFraction = 0.10f;

    /// <summary>Baseline particle count for an exit at zero speed.
    /// Small because a stationary "step out" doesn't have much to
    /// shake off.</summary>
    public const int ExitSplashBaseCount = 4;

    /// <summary>Additional drips per wu/s of exit speed. Sprinting out
    /// of water generates more drips than a slow walk-out.</summary>
    public const float ExitSplashCountPerSpeed = 3.0f;

    /// <summary>Hard cap on total exit-splash particles.</summary>
    public const int ExitSplashMaxCount = 22;

    /// <summary>Slight initial downward kick on exit drips (wu/s).
    /// Zero would also work (gravity does the rest) but a small push
    /// gives them an initial direction so they don't appear to
    /// "hesitate" at the waterline for the first frame.</summary>
    public const float ExitSplashInitialDownSpeed = 0.4f;

    /// <summary>Fraction of unit velocity inherited as horizontal
    /// motion by each drip. The droplet travels along with the unit
    /// briefly before drag slows it — natural "water carried forward
    /// off the body."</summary>
    public const float ExitSplashVelocityInheritFactor = 0.85f;

    /// <summary>Lateral fan added to the inherited velocity (wu/s).
    /// Spreads the drips slightly to the sides instead of all
    /// following the unit in a tight column.</summary>
    public const float ExitSplashLateralSpread = 0.6f;

    /// <summary>Base size range for exit drip droplets. Smaller than
    /// entry splash — these are drips, not a splash.</summary>
    public const float ExitSplashMinSizeBase = 0.10f;
    public const float ExitSplashMaxSizeBase = 0.18f;

    /// <summary>Per-wu/s additional size multiplier for exit drips.</summary>
    public const float ExitSplashSizeSpeedFactor = 0.10f;
    public const float ExitSplashMaxSizeMultiplier = 1.5f;

    /// <summary>Per-droplet airborne-lifetime failsafe. Drips normally
    /// die via the landing transform when WorldHeight reaches ground;
    /// this is the failsafe upper bound. 1.5s comfortably covers a
    /// fall from any realistic waterline height under our gravity.</summary>
    public const float ExitSplashLifetimeSec = 1.5f;

    /// <summary>Random spawn-position jitter radius around the unit's
    /// footprint (world units). Avoids a perfect point source.</summary>
    public const float SpawnJitterRadius = 0.15f;

    /// <summary>Minimum fraction of MaxWaterlineHeight to use as spawn
    /// height for exit drips. 0.10 → drips never spawn lower than 10%
    /// up from the feet (waist-deep wading produces drips from
    /// ankle-and-above, not from below-the-feet). Avoids drips that
    /// appear to come from inside the ground.</summary>
    public const float ExitSplashMinSpawnHeightFraction = 0.10f;

    /// <summary>Exponent on the random height fraction for exit drips.
    /// Less than 1 biases samples toward the high end (closer to max
    /// waterline). 0.4 puts ~75% of drips above the body midline, which
    /// reads as "most water drips from where the body was wettest" but
    /// still gives some lower-body drips for visual variation.</summary>
    public const float ExitSplashHeightBiasExponent = 0.4f;

    /// <summary>Peak alpha multiplier applied on top of the per-particle
    /// alpha curve. 1.0 = full opacity at peak; the brightest moment of
    /// each particle renders the baked texture's color verbatim. Was 200/255
    /// earlier, which capped visible brightness ~22% below the baked target
    /// — pushing it back to 1.0 brings highlights up to the shoreline color
    /// they were meant to match. (Was originally lowered so particles would
    /// blend more gently with the water; the per-particle curves now do
    /// that more selectively.)</summary>
    public const float PeakAlpha = 1.0f;

    /// <summary>Perceived shoreline foam color, used as the REFERENCE for
    /// the gradient's bright endpoint — but not the endpoint itself.
    /// The flipbook source pixels never reach pure white (max grey is
    /// ~171/255), so they carry built-in shading that already darkens
    /// the visible result. If we mapped max-grey source pixels directly
    /// to this color, the flipbook's natural shading would push the
    /// "average visible color" below it; particles would render darker
    /// than the shoreline foam. The actual bright endpoint
    /// (FoamColorBright) is this hue+saturation scaled to MAX brightness,
    /// leaving headroom for the spritesheet's built-in shading to land
    /// the displayed color back near this reference.</summary>
    public static readonly Color FoamColor = new((byte)159, (byte)180, (byte)176);

    /// <summary>Bright end of the particle color gradient — FoamColor's
    /// hue and saturation scaled to maximum brightness. The brightest
    /// flipbook source pixels (which are NOT pure white) map to this
    /// color; the flipbook's natural shading then produces the visible
    /// darker shades. Net effect: visible "highlight" pixels of a
    /// rendered particle land near the user-perceived FoamColor, while
    /// the brightest individual pixels approach white-with-teal-tint.
    /// Computed as FoamColor × (255 / max(FoamColor.R, .G, .B)) = roughly
    /// (225, 255, 249) — hue 169° preserved, saturation ~0.12 preserved,
    /// value bumped from 0.71 to 1.0.</summary>
    public static readonly Color FoamColorBright = ComputeMaxBrightnessSameHue(FoamColor);

    private static Color ComputeMaxBrightnessSameHue(Color c)
    {
        int maxCh = Math.Max(c.R, Math.Max(c.G, c.B));
        if (maxCh == 0) return Color.White;
        float scale = 255f / maxCh;
        byte r = (byte)Math.Min(255, (int)(c.R * scale));
        byte g = (byte)Math.Min(255, (int)(c.G * scale));
        byte b = (byte)Math.Min(255, (int)(c.B * scale));
        return new Color(r, g, b);
    }

    /// <summary>Dark end of the particle color gradient. The darkest
    /// source-grey pixels in the flipbook textures map to this color in
    /// the baked output. Tuned darker than the water surface so the
    /// "interior shadow" parts of each splash sink into the water hue
    /// rather than reading as a neutral grey blob. Lowered from
    /// (80, 110, 115) by 20% per user request — gives stronger contrast
    /// between the foam highlights and the splash interior.</summary>
    public static readonly Color ParticleShadowColor = new((byte)64, (byte)88, (byte)92);

    /// <summary>Gamma curve applied to the normalized grey value before
    /// looking it up in the shadow→foam gradient. 1.0 = linear lerp
    /// (recommended now that the gradient endpoint is FoamColorBright
    /// rather than FoamColor — the brightness headroom already pushes
    /// visible colors near the perceived foam color). Values &lt; 1 bias
    /// the gradient toward the bright end (mid-grey source pixels land
    /// closer to bright); values &gt; 1 bias toward shadow.</summary>
    public const float ParticleGradientGamma = 1.0f;

    /// <summary>How strongly the source water's tint pushes the wake
    /// gradient endpoints away from the default shoreline colours.
    /// 0 = ignore water tint (every variant looks like default shallow
    /// water); 1 = full multiply (swamp shallow water → swamp-green
    /// wake). The default of 1.0 matches how the in-shader shoreline
    /// foam in GroundShader.fx already tints its band; lower it if the
    /// per-water tint reads as too muddy in dark variants.</summary>
    public const float WaterTintInfluence = 1.0f;

    /// <summary>Soft-edged round particle texture, generated once on first
    /// draw. Used for the bow-wave (SoftCircle kind) — the trail and
    /// splash kinds use the flipbook textures instead.</summary>
    private const int ParticleTexSize = 32;
    private Texture2D? _particleTex;

    // Flipbook references — populated via Init() from Game1 after the
    // flipbooks dictionary loads. Lazily-null-safe in the draw path so
    // a missing asset doesn't crash; we just skip those particles.
    // We only consult these for frame layout (cols / rows / GetFrameRect),
    // never for the texture pixels — the actual draw uses the gradient-
    // baked textures below.
    private Flipbook? _fbMiniSplash;
    private Flipbook? _fbBubbleMagic;
    private Flipbook? _fbRainSplash;

    // Gradient-colored textures baked at Init from the grayscale source
    // flipbooks. One entry per water-tint variant: index 0 = default
    // (shadow → FoamColorBright), index N = same gradient but with both
    // endpoints multiplied by the Nth unique water tint discovered in
    // GroundSystem. WakeParticle.VariantIdx selects which to draw with.
    // Drawn with a white tint + alpha-modulated color so the gradient
    // reproduces verbatim, no shader needed.
    private Texture2D?[] _miniSplashByVariant = System.Array.Empty<Texture2D?>();
    private Texture2D?[] _bubbleMagicByVariant = System.Array.Empty<Texture2D?>();
    private Texture2D?[] _rainSplashByVariant = System.Array.Empty<Texture2D?>();
    /// <summary>One entry per baked variant. Index 0 is Color.White (the
    /// untinted default). Subsequent entries are unique tints harvested
    /// from <see cref="GroundSystem"/> at InitWaterVariants time.
    /// LookupVariantForPos compares against these to pick a particle's
    /// variant at spawn.</summary>
    private readonly List<Color> _variantTints = new();
    /// <summary>GroundSystem captured at InitWaterVariants. Re-sampled at
    /// every particle spawn to pick the right variant. Null until
    /// InitWaterVariants is called; in that null state every particle
    /// uses variant 0 (untinted default).</summary>
    private Necroking.World.GroundSystem? _groundRef;

    /// <summary>World-size range for trail particles (MiniSplash). Bumped
    /// up substantially from the previous soft-circle sizing because the
    /// MiniSplash frames have visible internal detail (foam shapes) that
    /// would be lost if downsampled to a couple of pixels. At zoom 40,
    /// 0.30 wu ≈ 12px, 0.55 wu ≈ 22px — small but with readable splash
    /// shape.</summary>
    public const float TrailMiniSplashMinSizeWorld = 0.30f;
    public const float TrailMiniSplashMaxSizeWorld = 0.55f;

    /// <summary>World-size range for the AIRBORNE splash bubble. Smaller
    /// than the rain-splash crown that replaces it on landing — the
    /// bubble is one droplet in the air, the rain splash is the impact
    /// crown.</summary>
    public const float SplashBubbleMinSizeWorld = 0.18f;
    public const float SplashBubbleMaxSizeWorld = 0.32f;

    /// <summary>World-size of the RainSplash crown that spawns where a
    /// BubbleMagic droplet lands. Sized to read as the impact splash —
    /// noticeably bigger than the bubble that preceded it.</summary>
    public const float SplashRainMinSizeWorld = 0.35f;
    public const float SplashRainMaxSizeWorld = 0.60f;

    /// <summary>Lifetime of the RainSplash crown animation after a droplet
    /// lands. At default 30 FPS the 9-frame RainSplash plays through in
    /// 0.3s; 0.4s gives a small buffer so the final frame doesn't snap
    /// off-screen the moment it appears.</summary>
    public const float RainSplashLifetimeSec = 0.40f;

    private static readonly Random _rand = new();

    private readonly List<WakeEmitterState?> _perUnit = new();

    /// <summary>Wire up flipbook references after the flipbooks dictionary
    /// has been populated in Game1.LoadGame. Bakes the default (untinted)
    /// gradient variant only — call <see cref="InitWaterVariants"/>
    /// afterwards (once the GroundSystem has been populated) to also
    /// bake variants for each tinted water type. Safe to call multiple
    /// times — the previous baked textures get disposed, then re-baked
    /// from the new sources. Missing flipbooks stay null and their
    /// particle kinds render as no-ops in the draw path.</summary>
    public void Init(Dictionary<string, Flipbook> flipbooks)
    {
        flipbooks.TryGetValue("mini_splash", out _fbMiniSplash);
        flipbooks.TryGetValue("bubble_magic", out _fbBubbleMagic);
        flipbooks.TryGetValue("rain_splash", out _fbRainSplash);

        DisposeAllVariants();
        _variantTints.Clear();
        _variantTints.Add(Color.White);
        BakeAllVariants();
        // Drop the ground reference — InitWaterVariants will set it again
        // if there are tinted water types this session.
        _groundRef = null;
    }

    /// <summary>Discover the unique water tints in <paramref name="ground"/>
    /// and bake one extra variant of each colored flipbook per tint, so
    /// particles spawned over swamp shallow water can render with a
    /// swamp-green wake gradient while plain shallow water keeps the
    /// default shoreline colours. Idempotent — calling again disposes
    /// the previous variant set and rebuilds from the current ground.
    /// Must be called AFTER <see cref="Init"/>; the GroundSystem
    /// reference is stashed so <see cref="LookupVariantForPos"/> can
    /// resolve a particle's variant at spawn time.</summary>
    public void InitWaterVariants(Necroking.World.GroundSystem ground)
    {
        _groundRef = ground;

        // Re-collect the unique water tints (Color.White is always idx 0).
        _variantTints.Clear();
        _variantTints.Add(Color.White);
        for (int i = 0; i < ground.TypeCount; i++)
        {
            var def = ground.GetTypeDef(i);
            bool isWater = def.MovementTerrain == Necroking.World.TerrainType.ShallowWater
                        || def.MovementTerrain == Necroking.World.TerrainType.DeepWater;
            if (!isWater) continue;
            // Skip exact dupes (including white) — keeps variant 0 as the
            // canonical untinted fallback.
            var t = def.TintColor;
            bool dup = false;
            for (int j = 0; j < _variantTints.Count; j++)
                if (_variantTints[j].PackedValue == t.PackedValue) { dup = true; break; }
            if (!dup) _variantTints.Add(t);
        }

        DisposeAllVariants();
        BakeAllVariants();
    }

    private void DisposeAllVariants()
    {
        for (int i = 0; i < _miniSplashByVariant.Length; i++) _miniSplashByVariant[i]?.Dispose();
        for (int i = 0; i < _bubbleMagicByVariant.Length; i++) _bubbleMagicByVariant[i]?.Dispose();
        for (int i = 0; i < _rainSplashByVariant.Length; i++) _rainSplashByVariant[i]?.Dispose();
        _miniSplashByVariant = System.Array.Empty<Texture2D?>();
        _bubbleMagicByVariant = System.Array.Empty<Texture2D?>();
        _rainSplashByVariant = System.Array.Empty<Texture2D?>();
    }

    private void BakeAllVariants()
    {
        int n = _variantTints.Count;
        _miniSplashByVariant = new Texture2D?[n];
        _bubbleMagicByVariant = new Texture2D?[n];
        _rainSplashByVariant = new Texture2D?[n];
        for (int v = 0; v < n; v++)
        {
            var tint = _variantTints[v];
            var shadow = ApplyWaterTint(ParticleShadowColor, tint, WaterTintInfluence);
            var foam = ApplyWaterTint(FoamColorBright, tint, WaterTintInfluence);
            _miniSplashByVariant[v] = BakeGradientTexture(_fbMiniSplash, shadow, foam);
            _bubbleMagicByVariant[v] = BakeGradientTexture(_fbBubbleMagic, shadow, foam);
            _rainSplashByVariant[v] = BakeGradientTexture(_fbRainSplash, shadow, foam);
        }
    }

    /// <summary>Multiply <paramref name="baseColor"/> by <paramref name="tint"/>,
    /// blended toward identity by <paramref name="influence"/>. influence=1
    /// is a full multiply (swamp shallow water tint = (120,165,100)/255
    /// pulls the foam endpoint from (225,255,249) to ~(106,165,97));
    /// influence=0 returns baseColor unchanged.</summary>
    private static Color ApplyWaterTint(Color baseColor, Color tint, float influence)
    {
        float tr = MathHelper.Lerp(1f, tint.R / 255f, influence);
        float tg = MathHelper.Lerp(1f, tint.G / 255f, influence);
        float tb = MathHelper.Lerp(1f, tint.B / 255f, influence);
        return new Color(
            (byte)Math.Clamp((int)(baseColor.R * tr), 0, 255),
            (byte)Math.Clamp((int)(baseColor.G * tg), 0, 255),
            (byte)Math.Clamp((int)(baseColor.B * tb), 0, 255));
    }

    /// <summary>Pick the variant index whose tint matches the water at
    /// <paramref name="pos"/>. Returns 0 (untinted default) when there's
    /// no ground reference, the position is over a non-water vertex, or
    /// the sampled tint doesn't match any registered variant.</summary>
    private byte LookupVariantForPos(Vec2 pos)
    {
        if (_groundRef == null || _variantTints.Count <= 1) return 0;
        var tint = _groundRef.SampleNearestWaterTint(pos);
        // Linear scan — _variantTints is tiny (one per unique water tint),
        // so the cost is negligible vs the dict overhead.
        for (int i = 1; i < _variantTints.Count; i++)
            if (_variantTints[i].PackedValue == tint.PackedValue) return (byte)i;
        return 0;
    }

    /// <summary>Bake a gradient-colored copy of a grayscale source.
    ///
    /// PASS 1: scans the source for the maximum effective grey value
    /// across all opaque-ish pixels. This is the calibration constant —
    /// without it, source pixels that top out at e.g. grey ~171 (the
    /// MiniSplash brightest pixels) would land mid-gradient even with
    /// grey=1.0 in the lerp, leaving the visible "highlights" darker
    /// than the user-specified FoamColor target.
    ///
    /// PASS 2: maps each pixel's grey by `clamp(grey / maxGrey, 0, 1)`
    /// then lerps between <see cref="ParticleShadowColor"/> (at 0) and
    /// <see cref="FoamColor"/> (at 1). The brightest source pixels now
    /// land at exactly FoamColor; mid-greys fill the gradient between;
    /// the darkest source pixels become ParticleShadowColor. Result is
    /// re-premultiplied by source alpha so the texture stays compatible
    /// with the AlphaBlend pipeline (drawn with white tint).
    ///
    /// Per-texture calibration handles each sprite's particular grey
    /// distribution automatically — MiniSplash (max grey ~171),
    /// BubbleMagic (~215), and RainSplash (~163) each get their own
    /// max-grey, so all three render with the same visible foam color
    /// in their brightest spots.</summary>
    private static Texture2D? BakeGradientTexture(Flipbook? fb) =>
        BakeGradientTexture(fb, ParticleShadowColor, FoamColorBright);

    private static Texture2D? BakeGradientTexture(Flipbook? fb, Color shadowColor, Color foamColor)
    {
        if (fb == null || fb.Texture == null) return null;
        var src = fb.Texture;
        var pixels = new Color[src.Width * src.Height];
        src.GetData(pixels);

        // Pass 1 — find max effective grey. Skip fully transparent pixels.
        // Floor at 0.5 so a very faint sprite doesn't end up calibrated
        // such that all its "darker" pixels miss the gradient — we want
        // the gradient to span a meaningful range.
        float maxGrey = 0.5f;
        for (int i = 0; i < pixels.Length; i++)
        {
            var p = pixels[i];
            if (p.A == 0) continue;
            float a01 = p.A / 255f;
            float grey01 = MathHelper.Clamp((p.R / 255f) / a01, 0f, 1f);
            if (grey01 > maxGrey) maxGrey = grey01;
        }

        // Pass 2 — gradient-bake using the calibrated max grey.
        for (int i = 0; i < pixels.Length; i++)
        {
            var p = pixels[i];
            if (p.A == 0) continue;
            float a01 = p.A / 255f;
            // Recover original grey from premultiplied source. Grayscale
            // assets have R=G=B; we sample R, divide out alpha to undo
            // premultiplication.
            float grey01 = MathHelper.Clamp((p.R / 255f) / a01, 0f, 1f);
            // Normalize against the texture's max grey, then optionally
            // apply a gamma curve. The brightest source pixels lerp all
            // the way to FoamColorBright (max-brightness, same hue/sat as
            // FoamColor) — leaving headroom for the spritesheet's built-in
            // shading to land the rendered "highlight" near FoamColor.
            float tLin = MathHelper.Clamp(grey01 / maxGrey, 0f, 1f);
            float t = ParticleGradientGamma == 1f ? tLin : MathF.Pow(tLin, ParticleGradientGamma);
            // Lerp colors then re-premultiply by alpha.
            float r = MathHelper.Lerp(shadowColor.R, foamColor.R, t) * a01;
            float g = MathHelper.Lerp(shadowColor.G, foamColor.G, t) * a01;
            float b = MathHelper.Lerp(shadowColor.B, foamColor.B, t) * a01;
            pixels[i] = new Color((byte)r, (byte)g, (byte)b, p.A);
        }
        var baked = new Texture2D(src.GraphicsDevice, src.Width, src.Height);
        baked.SetData(pixels);
        return baked;
    }

    public void Clear() => _perUnit.Clear();

    /// <summary>Update the unit's wake (age existing particles, spawn new
    /// trail / bow wave / entry splash) and draw the BACK layer (IsFront=
    /// false — the trail). Call this BEFORE drawing the unit's sprite so
    /// trail particles that drift into the body's silhouette are correctly
    /// covered. Cheap when the unit isn't wading and has no live particles.
    /// The <paramref name="pixel"/> parameter is unused — the wake uses
    /// its own soft circle texture generated lazily on first draw.</summary>
    public void UpdateAndDrawBack(
        int unitIdx, float dt,
        Vec2 unitPos, Vec2 unitVel,
        float facingDeg, float bodyLengthWorld,
        float waterlineWorldHeight,
        bool wadingActive,
        SpriteBatch sb, Texture2D pixel,
        Renderer renderer, Camera25D camera)
    {
        EnsureCapacity(unitIdx);
        var state = _perUnit[unitIdx];

        // Fast exit: unit never had wake state AND isn't wading now →
        // there's nothing to set up, age, or draw.
        if (!wadingActive && state == null)
            return;

        if (state == null)
        {
            state = new WakeEmitterState();
            _perUnit[unitIdx] = state;
        }

        // Body-axis half-vector: pointing from the unit's center toward
        // the FRONT (along facing) at half the body length. For
        // humanoid (bodyLength=0) this is a zero vector, all spawn
        // positions stay at the unit's pivot. For quadrupeds it spreads
        // spawn positions across the body silhouette.
        float facingRad = facingDeg * (MathF.PI / 180f);
        var bodyHalf = new Vec2(
            MathF.Cos(facingRad) * bodyLengthWorld * 0.5f,
            MathF.Sin(facingRad) * bodyLengthWorld * 0.5f);

        AgeParticles(state, dt);

        // Pre-resolve the water-tint variant for this unit's position
        // once per frame — every particle that spawns this tick lives in
        // the same body of water and gets the same gradient colours.
        // Exit-splash drips are a special case: by the time they spawn,
        // the unit has left the water (unitPos sits on dry land) and a
        // fresh lookup would return variant 0 (untinted default). Use
        // state.LastWaterVariantIdx instead — that's the variant from
        // the last frame the unit was actually wading.
        byte variantIdx = LookupVariantForPos(unitPos);
        if (wadingActive)
            state.LastWaterVariantIdx = variantIdx;
        byte exitVariantIdx = state.LastWaterVariantIdx;

        // Entry-splash edge — wading false → true. Also resets the
        // max-depth tracker + caches the body half-vector for the
        // session so trickle spawns stay coherent if the unit turns.
        if (wadingActive && !state.WasWading)
        {
            state.MaxWaterlineHeightThisSession = waterlineWorldHeight;
            StartEntrySplash(state, unitPos, unitVel, waterlineWorldHeight, bodyHalf, variantIdx);
        }

        // Exit-splash edge — wading true → false. We use the MAX waterline
        // height the unit reached during the session, not the last frame's,
        // so drips fall from the deepest point the body got wet. A unit
        // that went chest-deep then came back to ankle-deep before exiting
        // still has drips from chest-height.
        if (!wadingActive && state.WasWading)
            StartExitSplash(state, unitPos, unitVel, state.MaxWaterlineHeightThisSession, bodyHalf, exitVariantIdx);

        // Entry-splash session trickle — releases the remaining 80%
        // over EntrySplashSessionDurationSec at the unit's current
        // position. Uses the CACHED body-half (from session start) so
        // the spread axis stays coherent through the session.
        if (state.SplashRemainingDuration > 0f)
            TrickleEntrySplash(state, dt, unitPos, unitVel, waterlineWorldHeight, state.BodyHalfAtStart, variantIdx);

        // Exit-splash session trickle — drips spawn from the body's
        // sunken silhouette on dry land, but their colour should match
        // the water the unit just left (exitVariantIdx), not the dry
        // ground underneath.
        if (state.ExitRemainingDuration > 0f)
            TrickleExitSplash(state, dt, unitPos, state.ExitVelocity, state.ExitWaterlineHeight, state.ExitBodyHalf, exitVariantIdx);

        if (wadingActive)
        {
            SpawnTrail(state, dt, unitPos, unitVel, waterlineWorldHeight, bodyHalf, variantIdx);
            SpawnBowWave(state, dt, unitPos, unitVel, waterlineWorldHeight, bodyHalf, variantIdx);
            // Cache the waterline so we can use it on the next frame's
            // exit edge (which won't carry a fresh waterline height).
            state.LastWaterlineHeight = waterlineWorldHeight;
            // Track the deepest point the unit reached this session.
            if (waterlineWorldHeight > state.MaxWaterlineHeightThisSession)
                state.MaxWaterlineHeightThisSession = waterlineWorldHeight;
        }
        else
        {
            state.TrailSpawnAccum = 0f;
            state.BowWaveSpawnAccum = 0f;
        }

        state.WasWading = wadingActive;

        if (state.Particles.Count == 0) return;

        _particleTex ??= CreateSoftCircleTexture(sb.GraphicsDevice, ParticleTexSize);
        DrawParticles(state, sb, _particleTex, renderer, camera, drawFront: false);
    }

    /// <summary>Draw the FRONT layer (IsFront=true — bow wave + entry
    /// splash) for the unit. Call AFTER drawing the unit's sprite so the
    /// front foam crescent stays visible even when the unit is moving N
    /// (where "ahead of unit" overlaps the body's screen footprint). No
    /// update step — UpdateAndDrawBack already did that this frame.</summary>
    public void DrawFront(
        int unitIdx,
        SpriteBatch sb, Renderer renderer, Camera25D camera)
    {
        if (unitIdx < 0 || unitIdx >= _perUnit.Count) return;
        var state = _perUnit[unitIdx];
        if (state == null || state.Particles.Count == 0) return;
        if (_particleTex == null) return; // back pass didn't run; nothing to draw
        DrawParticles(state, sb, _particleTex, renderer, camera, drawFront: true);
    }

    /// <summary>Soft white circle with a gaussian-ish alpha falloff. Used as
    /// the particle billboard so foam dots have feathered edges instead of
    /// the hard squares you get from a 1-pixel texture stretched to N pixels.</summary>
    private static Texture2D CreateSoftCircleTexture(GraphicsDevice device, int size)
    {
        var tex = new Texture2D(device, size, size);
        var pixels = new Color[size * size];
        float center = size * 0.5f;
        float radius = center;
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float dx = x + 0.5f - center;
                float dy = y + 0.5f - center;
                float distNorm = MathF.Sqrt(dx * dx + dy * dy) / radius;
                // Smoothstep-based falloff: solid in the inner ~25%, then
                // a long soft tail to zero at the edge. (1-x)^2 reads
                // softer than linear and avoids the wedge look.
                float a;
                if (distNorm >= 1f) a = 0f;
                else
                {
                    float t = MathF.Max(0f, (distNorm - 0.25f) / 0.75f);
                    a = (1f - t) * (1f - t);
                }
                byte ab = (byte)MathHelper.Clamp(a * 255f, 0f, 255f);
                pixels[y * size + x] = new Color(ab, ab, ab, ab);
            }
        }
        tex.SetData(pixels);
        return tex;
    }

    private void EnsureCapacity(int unitIdx)
    {
        while (_perUnit.Count <= unitIdx)
            _perUnit.Add(null);
    }

    private static void AgeParticles(WakeEmitterState state, float dt)
    {
        var ps = state.Particles;
        for (int i = ps.Count - 1; i >= 0; i--)
        {
            var p = ps[i];
            p.Age += dt;
            p.Pos += p.Vel * dt;

            if (p.HasGravity)
            {
                // Airborne BubbleMagic droplet. Ballistic arc + horizontal
                // drag; on landing, transform in-place into a RainSplash
                // (kind reassigned, age reset, lifetime swapped) so the
                // rain-splash animation plays through at the impact point.
                p.Vel *= MathF.Max(0f, 1f - dt * EntrySplashDragPerSec);
                p.WorldHeight += p.VertVel * dt;
                p.VertVel -= EntrySplashGravity * dt;

                if (p.WorldHeight <= p.LandHeight && p.VertVel <= 0f)
                {
                    // LAND: transform → RainSplash. The droplet stops in
                    // place; the rain-splash crown plays the full 9-frame
                    // animation, then the particle dies at end of Lifetime.
                    p.WorldHeight = p.LandHeight;
                    p.VertVel = 0f;
                    p.Vel = new Vec2(0f, 0f);
                    p.HasGravity = false;
                    p.Kind = WakeParticleKind.RainSplash;
                    p.Age = 0f;
                    p.Lifetime = RainSplashLifetimeSec;
                    // Bigger world size — the crown is a more substantial
                    // impact effect than the bubble that just landed.
                    p.Size = SplashRainMinSizeWorld
                        + (float)_rand.NextDouble() * (SplashRainMaxSizeWorld - SplashRainMinSizeWorld);
                    // Rain-splash crown reads better straight up than
                    // tilted, so we don't carry over the bubble's rotation.
                    p.Rotation = 0f;
                    ps[i] = p;
                    continue;
                }

                // Airborne failsafe — kill the droplet if its arc somehow
                // never lands (numerical edge case).
                if (p.Age >= p.Lifetime)
                {
                    ps.RemoveAt(i);
                    continue;
                }
            }
            else
            {
                // Non-gravity particle — trail, bow wave, or a landed
                // RainSplash. All three age toward Lifetime; horizontal
                // drag applies to the trail and bow wave, but for a
                // RainSplash (Vel was zeroed at landing) it's a no-op.
                p.Vel *= MathF.Max(0f, 1f - dt * DragPerSec);
                if (p.Age >= p.Lifetime)
                {
                    ps.RemoveAt(i);
                    continue;
                }
            }

            ps[i] = p;
        }
    }

    private static void SpawnTrail(
        WakeEmitterState state, float dt,
        Vec2 unitPos, Vec2 unitVel,
        float waterlineWorldHeight,
        Vec2 bodyHalf,
        byte variantIdx)
    {
        float speed = unitVel.Length();
        if (speed < MinSpeedToEmit)
        {
            state.TrailSpawnAccum = 0f;
            return;
        }

        state.TrailSpawnAccum += speed * ParticlesPerWorldUnitOfMotion * dt;
        int toSpawn = (int)state.TrailSpawnAccum;
        if (toSpawn <= 0) return;
        state.TrailSpawnAccum -= toSpawn;

        int allowed = MaxParticlesPerUnit - state.Particles.Count;
        if (allowed <= 0) return;
        if (toSpawn > allowed) toSpawn = allowed;

        float velAngle = MathF.Atan2(unitVel.Y, unitVel.X);
        float rearAngle = velAngle + MathF.PI;

        // Trail emits from the REAR end of the body — for a quadruped,
        // that's at the tail. For a humanoid (bodyHalf=0,0) this is
        // just the unit pivot.
        var rearAnchor = new Vec2(unitPos.X - bodyHalf.X, unitPos.Y - bodyHalf.Y);

        for (int n = 0; n < toSpawn; n++)
        {
            float spread = ((float)_rand.NextDouble() * 2f - 1f) * SpreadHalfConeRad;
            float a = rearAngle + spread;
            float drift = speed * DriftFactor * (0.6f + (float)_rand.NextDouble() * 0.4f);
            var pVel = new Vec2(MathF.Cos(a) * drift, MathF.Sin(a) * drift);

            float jitterR = (float)_rand.NextDouble() * SpawnJitterRadius;
            float jitterA = (float)(_rand.NextDouble() * 2.0 * Math.PI);
            var spawnPos = new Vec2(
                rearAnchor.X + MathF.Cos(jitterA) * jitterR,
                rearAnchor.Y + MathF.Sin(jitterA) * jitterR);

            float life = MinLifetimeSec + (float)_rand.NextDouble() * (MaxLifetimeSec - MinLifetimeSec);
            float size = TrailMiniSplashMinSizeWorld
                + (float)_rand.NextDouble() * (TrailMiniSplashMaxSizeWorld - TrailMiniSplashMinSizeWorld);
            // Random per-particle rotation so adjacent trail particles
            // don't look like xerox copies — each MiniSplash plays at a
            // different orientation.
            float rotation = (float)(_rand.NextDouble() * 2.0 * Math.PI);

            state.Particles.Add(new WakeParticle
            {
                Pos = spawnPos,
                Vel = pVel,
                WorldHeight = waterlineWorldHeight,
                Age = 0f,
                Lifetime = life,
                Size = size,
                Rotation = rotation,
                Kind = WakeParticleKind.MiniSplash,
                IsFront = false,
                VariantIdx = variantIdx
            });
        }
    }

    private static void SpawnBowWave(
        WakeEmitterState state, float dt,
        Vec2 unitPos, Vec2 unitVel,
        float waterlineWorldHeight,
        Vec2 bodyHalf,
        byte variantIdx)
    {
        float speed = unitVel.Length();
        if (speed < BowWaveMinSpeedToEmit)
        {
            state.BowWaveSpawnAccum = 0f;
            return;
        }

        state.BowWaveSpawnAccum += speed * BowWaveParticlesPerWorldUnitOfMotion * dt;
        int toSpawn = (int)state.BowWaveSpawnAccum;
        if (toSpawn <= 0) return;
        state.BowWaveSpawnAccum -= toSpawn;

        int allowed = MaxParticlesPerUnit - state.Particles.Count;
        if (allowed <= 0) return;
        if (toSpawn > allowed) toSpawn = allowed;

        // Unit velocity direction + a perpendicular for the lateral spread.
        float velAngle = MathF.Atan2(unitVel.Y, unitVel.X);
        float fwdX = MathF.Cos(velAngle);
        float fwdY = MathF.Sin(velAngle);
        float perpX = -fwdY;
        float perpY = fwdX;

        // Bow wave emits from the FRONT end of the body (head). For a
        // quadruped this places the foam crescent at the wolf's chest
        // rather than at its middle. Humanoids (bodyHalf=0,0) get the
        // existing pivot-based behavior.
        var frontAnchor = new Vec2(unitPos.X + bodyHalf.X, unitPos.Y + bodyHalf.Y);

        for (int n = 0; n < toSpawn; n++)
        {
            // Spawn ahead of the unit center with a small lateral fan —
            // wider across the motion axis than along it gives the
            // crescent shape that game references show for character
            // bow waves (Sea of Thieves, RIME, Witcher 3).
            float forwardDist = BowWaveForwardOffsetMin
                + (float)_rand.NextDouble() * (BowWaveForwardOffsetMax - BowWaveForwardOffsetMin);
            float lateralFrac = (float)_rand.NextDouble() * 2f - 1f; // [-1, +1]
            float lateral = lateralFrac * BowWaveLateralSpread;
            var spawnPos = new Vec2(
                frontAnchor.X + fwdX * forwardDist + perpX * lateral,
                frontAnchor.Y + fwdY * forwardDist + perpY * lateral);

            // Drift: outward (in the lateral direction this particle was
            // offset to) + slightly backward (slipping past the unit as
            // the unit advances). Magnitude scales with unit speed so
            // a slow walker has a slow-moving wave and a sprinter throws
            // water harder.
            float outwardSign = lateralFrac >= 0f ? 1f : -1f;
            float outwardMag = speed * BowWaveDriftFactor * (0.6f + (float)_rand.NextDouble() * 0.4f);
            float backMag = speed * BowWaveDriftFactor * 0.4f;
            var pVel = new Vec2(
                perpX * outwardMag * outwardSign - fwdX * backMag,
                perpY * outwardMag * outwardSign - fwdY * backMag);

            float life = BowWaveMinLifetimeSec
                + (float)_rand.NextDouble() * (BowWaveMaxLifetimeSec - BowWaveMinLifetimeSec);
            float size = BowWaveMinSizeWorld
                + (float)_rand.NextDouble() * (BowWaveMaxSizeWorld - BowWaveMinSizeWorld);
            // Random per-particle rotation — same reason as the trail:
            // overlapping front-fan particles otherwise read as xeroxed.
            float rotation = (float)(_rand.NextDouble() * 2.0 * Math.PI);

            state.Particles.Add(new WakeParticle
            {
                Pos = spawnPos,
                Vel = pVel,
                WorldHeight = waterlineWorldHeight,
                Age = 0f,
                Lifetime = life,
                Size = size,
                Rotation = rotation,
                Kind = WakeParticleKind.MiniSplash,
                IsFront = true,
                VariantIdx = variantIdx
            });
        }
    }

    /// <summary>Fired on the wading false→true edge. Computes the total
    /// splash size from entry speed, releases the initial burst fraction
    /// immediately, and sets up the session state so the rest trickles
    /// out over the next EntrySplashSessionDurationSec.</summary>
    private static void StartEntrySplash(
        WakeEmitterState state,
        Vec2 unitPos, Vec2 unitVel,
        float waterlineWorldHeight,
        Vec2 bodyHalf,
        byte variantIdx)
    {
        float speed = unitVel.Length();
        int totalRequested = EntrySplashBaseCount + (int)(speed * EntrySplashCountPerSpeed);
        totalRequested = Math.Min(totalRequested, EntrySplashMaxCount);

        int initialBurst = Math.Max(1, (int)(totalRequested * EntrySplashInitialBurstFraction));
        int trickle = totalRequested - initialBurst;

        // Cache entry speed so the trickle spawns use the SAME speed-scaled
        // size as the burst (current velocity might differ as the unit
        // decelerates entering the water — visual consistency wins).
        state.SplashEntrySpeed = speed;
        state.SplashRemainingCount = trickle;
        state.SplashRemainingDuration = EntrySplashSessionDurationSec;
        state.SplashSpawnAccum = 0f;
        state.BodyHalfAtStart = bodyHalf;

        EmitSplashParticles(state, initialBurst, speed, unitPos, unitVel, waterlineWorldHeight, bodyHalf, variantIdx);
    }

    /// <summary>Per-frame trickle spawn during an active splash session.
    /// Spawns particles at the unit's CURRENT position with the CURRENT
    /// velocity direction (so the splash follows the unit), but uses the
    /// cached entry-speed for size scaling so the splash droplet size
    /// stays consistent through the session.</summary>
    private static void TrickleEntrySplash(
        WakeEmitterState state, float dt,
        Vec2 unitPos, Vec2 unitVel,
        float waterlineWorldHeight,
        Vec2 bodyHalf,
        byte variantIdx)
    {
        // End-of-session housekeeping. If duration expired we drop any
        // un-spawned remainder (rather than dumping a final burst), which
        // matches the spec: the trickle ends after 0.5s regardless.
        if (state.SplashRemainingDuration <= 0f || state.SplashRemainingCount <= 0)
        {
            state.SplashRemainingDuration = 0f;
            state.SplashRemainingCount = 0;
            state.SplashSpawnAccum = 0f;
            return;
        }

        // Rate = particles still to spawn / time still to spawn them.
        // Stays approximately constant as the session progresses — if
        // accumulator rounding leaves the remaining count slightly above
        // the linear schedule, rate ticks up to catch up.
        float rate = state.SplashRemainingCount / state.SplashRemainingDuration;
        state.SplashSpawnAccum += rate * dt;
        int toSpawn = (int)state.SplashSpawnAccum;
        state.SplashSpawnAccum -= toSpawn;
        toSpawn = Math.Min(toSpawn, state.SplashRemainingCount);

        if (toSpawn > 0)
        {
            EmitSplashParticles(state, toSpawn, state.SplashEntrySpeed,
                                unitPos, unitVel, waterlineWorldHeight, bodyHalf, variantIdx);
            state.SplashRemainingCount -= toSpawn;
        }

        state.SplashRemainingDuration -= dt;
    }

    /// <summary>Spawn N splash droplets at the given world position with
    /// the given unit velocity (used for forward direction + boost) and
    /// the given <paramref name="entrySpeed"/> used for the speed-scaled
    /// size multiplier (so initial burst + trickle droplets are sized
    /// consistently regardless of how the unit's velocity changes during
    /// the session).</summary>
    private static void EmitSplashParticles(
        WakeEmitterState state, int n,
        float entrySpeed,
        Vec2 unitPos, Vec2 unitVel,
        float waterlineWorldHeight,
        Vec2 bodyHalf,
        byte variantIdx)
    {
        int allowed = MaxParticlesPerUnit - state.Particles.Count;
        if (allowed <= 0) return;
        if (n > allowed) n = allowed;

        // Direction uses CURRENT unit velocity — splash droplets fan
        // forward of where the unit is heading right now.
        float currentSpeed = unitVel.Length();
        bool hasForward = currentSpeed >= EntrySplashForwardSpeedThreshold;
        float centerAngle = hasForward ? MathF.Atan2(unitVel.Y, unitVel.X) : 0f;
        float halfCone = hasForward ? EntrySplashForwardHalfConeRad : MathF.PI;
        float fwdX = hasForward ? MathF.Cos(centerAngle) : 0f;
        float fwdY = hasForward ? MathF.Sin(centerAngle) : 0f;

        // Size scaling uses the ENTRY speed — keeps droplet size constant
        // across the session, so a unit that decelerates into the water
        // doesn't shrink its splash mid-burst.
        float sizeMul = MathF.Min(
            1f + entrySpeed * EntrySplashSizeSpeedFactor,
            EntrySplashMaxSizeMultiplier);

        for (int i = 0; i < n; i++)
        {
            // Horizontal velocity = baseline omnidirectional outward + (if
            // hasForward) a forward-velocity inheritance from the unit. The
            // forward boost is varied 0.5-1.5× per droplet so they don't
            // all fly in identical formation — gives the fan some life.
            float a = centerAngle + ((float)_rand.NextDouble() * 2f - 1f) * halfCone;
            float omniSpeed = EntrySplashOmniBaseSpeed * (0.7f + (float)_rand.NextDouble() * 0.6f);
            float vx = MathF.Cos(a) * omniSpeed;
            float vy = MathF.Sin(a) * omniSpeed;
            if (hasForward)
            {
                // Velocity inheritance uses CURRENT speed (not entry speed)
                // so droplets spawned mid-session match the unit's
                // current motion — if the unit slows after entry, later
                // splashes have less forward energy. Natural decay.
                float fwdBoost = currentSpeed * EntrySplashForwardInheritFactor
                    * (0.5f + (float)_rand.NextDouble() * 1.0f);
                vx += fwdX * fwdBoost;
                vy += fwdY * fwdBoost;
            }
            var pVel = new Vec2(vx, vy);

            // Upward velocity: baseline + per-CURRENT-speed contribution
            // (soft-knee'd so running doesn't fountain water — see
            // EntrySplashVertSpeedKnee). At walk speeds the contribution
            // scales linearly; above the knee it tapers to 40%, so a
            // sprint splash is forward-energetic but doesn't shoot
            // disproportionately higher than the walk splash.
            float vertEffectiveSpeed = currentSpeed <= EntrySplashVertSpeedKnee
                ? currentSpeed
                : EntrySplashVertSpeedKnee
                  + (currentSpeed - EntrySplashVertSpeedKnee) * EntrySplashVertSpeedRunFactor;
            float vertVel = (EntrySplashUpBaseSpeed + vertEffectiveSpeed * EntrySplashUpPerSpeed)
                * (0.5f + (float)_rand.NextDouble() * 0.5f);

            // Spawn position spreads along the body axis: pick a random
            // offset in [-1, +1] along bodyHalf so droplets distribute
            // across the unit's silhouette (head to tail for quadrupeds).
            // For humanoids (bodyHalf=0,0) this collapses to the existing
            // point-source pattern. Small jitter on top so the spawn
            // doesn't read as a straight line.
            float bodyOffset = (float)_rand.NextDouble() * 2f - 1f; // [-1, +1]
            float jR = (float)_rand.NextDouble() * 0.10f;
            float jA = (float)(_rand.NextDouble() * 2.0 * Math.PI);
            var pos = new Vec2(
                unitPos.X + bodyHalf.X * bodyOffset + MathF.Cos(jA) * jR,
                unitPos.Y + bodyHalf.Y * bodyOffset + MathF.Sin(jA) * jR);

            // Lifetime = predicted airborne arc time, with a 10% buffer so
            // numerical drift in the ballistic integration doesn't trip the
            // failsafe kill before landing detection fires. The BubbleMagic
            // animation plays once across Lifetime (via the normalized-time
            // frame selector), so by the time the droplet lands the bubble
            // animation has just about completed and the in-place
            // transform to RainSplash takes over.
            float predictedAirTime = 2f * vertVel / EntrySplashGravity;
            float life = predictedAirTime * 1.10f;
            float baseSize = SplashBubbleMinSizeWorld
                + (float)_rand.NextDouble() * (SplashBubbleMaxSizeWorld - SplashBubbleMinSizeWorld);
            float rotation = (float)(_rand.NextDouble() * 2.0 * Math.PI);

            state.Particles.Add(new WakeParticle
            {
                Pos = pos,
                Vel = pVel,
                VertVel = vertVel,
                WorldHeight = waterlineWorldHeight,
                // LandHeight captures the waterline the droplet launched
                // from — landing-detection compares WorldHeight against
                // this. Per-particle (rather than global) so droplets
                // spawned at slightly different waterlines (e.g., during
                // a trickle as the unit moves to deeper water) each have
                // a correct landing reference.
                LandHeight = waterlineWorldHeight,
                Age = 0f,
                Lifetime = life,
                // LandedAge unused in the new model — landing transforms
                // the particle to RainSplash and resets Age instead.
                LandedAge = 0f,
                Size = baseSize * sizeMul,
                Rotation = rotation,
                Kind = WakeParticleKind.BubbleMagic,
                IsFront = true,
                HasGravity = true,
                VariantIdx = variantIdx
            });
        }
    }

    /// <summary>Fired on the wading true→false edge. Computes the total
    /// drip count from exit speed, releases the small initial fraction,
    /// and sets up the session state so the rest drip out over
    /// <see cref="ExitSplashSessionDurationSec"/>. Each droplet spawns
    /// at the body's last-known waterline height and falls to the
    /// ground (LandHeight=0).</summary>
    private static void StartExitSplash(
        WakeEmitterState state,
        Vec2 unitPos, Vec2 unitVel,
        float waterlineHeight,
        Vec2 bodyHalf,
        byte variantIdx)
    {
        float speed = unitVel.Length();
        int totalRequested = ExitSplashBaseCount + (int)(speed * ExitSplashCountPerSpeed);
        totalRequested = Math.Min(totalRequested, ExitSplashMaxCount);

        int initialBurst = Math.Max(1, (int)(totalRequested * ExitSplashInitialBurstFraction));
        int trickle = totalRequested - initialBurst;

        // Cache state for the session. ExitVelocity preserves the
        // motion the unit had at the moment of exit; we use this for
        // forward direction rather than the unit's current velocity so
        // the drip motion stays coherent even if the unit stops or
        // turns immediately after leaving the water.
        state.ExitSpeedAtStart = speed;
        state.ExitVelocity = unitVel;
        state.ExitWaterlineHeight = waterlineHeight;
        state.ExitRemainingCount = trickle;
        state.ExitRemainingDuration = ExitSplashSessionDurationSec;
        state.ExitSpawnAccum = 0f;
        state.ExitBodyHalf = bodyHalf;

        EmitExitSplashParticles(state, initialBurst, speed, unitPos, unitVel, waterlineHeight, bodyHalf, variantIdx);
    }

    /// <summary>Per-frame trickle release of exit drips over an active
    /// session. Uses the unit's CURRENT XY position (drips spawn from
    /// where the unit is now, tracking it as it moves on dry land) but
    /// the CACHED velocity and waterline height (so the drip motion
    /// + spawn elevation remain coherent through the session).</summary>
    private static void TrickleExitSplash(
        WakeEmitterState state, float dt,
        Vec2 unitPos, Vec2 unitVel, float waterlineHeight,
        Vec2 bodyHalf,
        byte variantIdx)
    {
        if (state.ExitRemainingDuration <= 0f || state.ExitRemainingCount <= 0)
        {
            state.ExitRemainingDuration = 0f;
            state.ExitRemainingCount = 0;
            state.ExitSpawnAccum = 0f;
            return;
        }

        float rate = state.ExitRemainingCount / state.ExitRemainingDuration;
        state.ExitSpawnAccum += rate * dt;
        int toSpawn = (int)state.ExitSpawnAccum;
        state.ExitSpawnAccum -= toSpawn;
        toSpawn = Math.Min(toSpawn, state.ExitRemainingCount);

        if (toSpawn > 0)
        {
            EmitExitSplashParticles(state, toSpawn, state.ExitSpeedAtStart,
                                    unitPos, unitVel, waterlineHeight, bodyHalf, variantIdx);
            state.ExitRemainingCount -= toSpawn;
        }

        state.ExitRemainingDuration -= dt;
    }

    /// <summary>Spawn N drip droplets. Each starts at the body's last
    /// waterline height, inherits a fraction of the cached exit velocity
    /// for horizontal motion, gets a small downward kick + gravity, and
    /// uses LandHeight=0 so the landing branch transforms it to a
    /// RainSplash when it reaches the ground.</summary>
    private static void EmitExitSplashParticles(
        WakeEmitterState state, int n,
        float speedAtStart,
        Vec2 unitPos, Vec2 unitVel, float waterlineHeight,
        Vec2 bodyHalf,
        byte variantIdx)
    {
        int allowed = MaxParticlesPerUnit - state.Particles.Count;
        if (allowed <= 0) return;
        if (n > allowed) n = allowed;

        // Forward + perpendicular from the cached exit velocity. If
        // exit velocity is near-zero the unit was barely moving at exit;
        // forward direction becomes arbitrary, so we fall back to no
        // forward bias (lateral spread alone).
        float velSpeed = unitVel.Length();
        float fwdX, fwdY, perpX, perpY;
        if (velSpeed > 0.01f)
        {
            fwdX = unitVel.X / velSpeed;
            fwdY = unitVel.Y / velSpeed;
            perpX = -fwdY;
            perpY = fwdX;
        }
        else
        {
            fwdX = 1f; fwdY = 0f; perpX = 0f; perpY = 1f;
        }

        // Size scaling matches the same speed-curve as entry, just using
        // the exit-splash specific factors so drips can be tuned
        // separately from the entry burst.
        float sizeMul = MathF.Min(
            1f + speedAtStart * ExitSplashSizeSpeedFactor,
            ExitSplashMaxSizeMultiplier);

        for (int i = 0; i < n; i++)
        {
            // Horizontal velocity: inherit a fraction of unit velocity
            // (varied per-droplet) plus a lateral spread. Drag slows
            // these down quickly so drips don't fly far from the unit.
            float fwdMul = ExitSplashVelocityInheritFactor
                * (0.8f + (float)_rand.NextDouble() * 0.4f);
            float lateral = ((float)_rand.NextDouble() * 2f - 1f) * ExitSplashLateralSpread;
            float vx = fwdX * velSpeed * fwdMul + perpX * lateral;
            float vy = fwdY * velSpeed * fwdMul + perpY * lateral;
            var pVel = new Vec2(vx, vy);

            // Small downward kick at spawn so droplets don't appear
            // momentarily stationary at the waterline before gravity
            // takes over. Negative VertVel = moving down (toward ground).
            float vertVel = -ExitSplashInitialDownSpeed
                * (0.5f + (float)_rand.NextDouble() * 1.0f);

            // Spawn position: distribute along the body axis (head to
            // tail for a quadruped) plus small jitter, plus a randomized
            // height between low body and the MAX waterline height. The
            // body-axis spread means drips fall from across the wet
            // silhouette, not just from the pivot point.
            float bodyOffset = (float)_rand.NextDouble() * 2f - 1f; // [-1, +1]
            float jR = (float)_rand.NextDouble() * 0.12f;
            float jA = (float)(_rand.NextDouble() * 2.0 * Math.PI);
            var pos = new Vec2(
                unitPos.X + bodyHalf.X * bodyOffset + MathF.Cos(jA) * jR,
                unitPos.Y + bodyHalf.Y * bodyOffset + MathF.Sin(jA) * jR);

            float heightFraction = MathF.Pow(
                (float)_rand.NextDouble(), ExitSplashHeightBiasExponent);
            heightFraction = ExitSplashMinSpawnHeightFraction
                + (1f - ExitSplashMinSpawnHeightFraction) * heightFraction;
            float spawnHeight = waterlineHeight * heightFraction;

            float baseSize = ExitSplashMinSizeBase
                + (float)_rand.NextDouble() * (ExitSplashMaxSizeBase - ExitSplashMinSizeBase);
            float rotation = (float)(_rand.NextDouble() * 2.0 * Math.PI);

            state.Particles.Add(new WakeParticle
            {
                Pos = pos,
                Vel = pVel,
                VertVel = vertVel,
                WorldHeight = spawnHeight,
                LandHeight = 0f,
                Age = 0f,
                Lifetime = ExitSplashLifetimeSec,
                LandedAge = 0f,
                Size = baseSize * sizeMul,
                Rotation = rotation,
                Kind = WakeParticleKind.BubbleMagic,
                IsFront = true,
                HasGravity = true,
                VariantIdx = variantIdx
            });
        }
    }

    private void DrawParticles(
        WakeEmitterState state,
        SpriteBatch sb, Texture2D softCircleTex,
        Renderer renderer, Camera25D camera,
        bool drawFront)
    {
        foreach (var p in state.Particles)
        {
            if (p.IsFront != drawFront) continue;

            // Per-kind alpha + size scaling. Trail/bow rely on age-based
            // fade because their texture doesn't dissipate on its own;
            // BubbleMagic stays full-alpha during the arc (the flipbook
            // does the visual work) and RainSplash stays full-alpha
            // during its crown animation. Slight fade-out tails added to
            // gravity kinds to avoid hard-edge pop at end of animation.
            float alpha;
            float sizeMul;
            float t = p.Lifetime > 0f ? p.Age / p.Lifetime : 0f;
            switch (p.Kind)
            {
                case WakeParticleKind.BubbleMagic:
                    // Airborne bubble — brief fade-in to avoid spawn pop,
                    // then full visibility through the animation.
                    alpha = MathF.Min(p.Age / 0.06f, 1f);
                    sizeMul = 1f;
                    break;
                case WakeParticleKind.RainSplash:
                    // Crown animation plays through; quick alpha taper in
                    // the final 20% so the last frame doesn't snap off.
                    alpha = t < 0.80f ? 1f : (1f - t) / 0.20f;
                    sizeMul = 1f;
                    break;
                case WakeParticleKind.MiniSplash:
                    // Trail: the flipbook animation itself dissipates the
                    // splash shape across frames, so we only need a tiny
                    // fade-in to avoid pop-in and a gentle tail fade in
                    // the last quarter. Holding at full alpha for most of
                    // life lets the brightest source frames render at the
                    // baked foam color (matching shoreline).
                    alpha = t < 0.08f
                        ? t / 0.08f
                        : t < 0.75f
                            ? 1f
                            : 1f - (t - 0.75f) / 0.25f;
                    sizeMul = 1f;
                    break;
                case WakeParticleKind.SoftCircle:
                default:
                    // Soft circle (bow wave) — age-based fade + shrink.
                    alpha = t < 0.15f
                        ? t / 0.15f
                        : 1f - (t - 0.15f) / 0.85f;
                    sizeMul = t < 1f - ShrinkFraction
                        ? 1f
                        : (1f - t) / ShrinkFraction;
                    break;
            }

            byte a = (byte)(MathHelper.Clamp(alpha, 0f, 1f) * PeakAlpha * 255f);
            if (a == 0) continue;
            if (sizeMul <= 0f) continue;

            // Choose texture, source rect, tint per kind. Flipbook kinds
            // use the BAKED gradient textures (color already mapped from
            // source grey to ParticleShadowColor↔FoamColor at Init), so
            // we draw with a white tint and only the alpha channel of
            // the tint matters (for the per-particle fade). Flipbook
            // frame layout still comes from the Flipbook object —
            // GetFrameRect against the baked texture works because the
            // baked texture has the same dimensions as the source.
            Texture2D? tex;
            Rectangle src;
            // VariantIdx selects which of the pre-baked tint variants to
            // sample. Clamp defensively so an out-of-range index (e.g. a
            // unit re-init mid-game) falls back to the default rather
            // than crashing.
            int variantIdx = p.VariantIdx;
            switch (p.Kind)
            {
                case WakeParticleKind.MiniSplash:
                    if (_fbMiniSplash == null || _miniSplashByVariant.Length == 0) continue;
                    if (variantIdx >= _miniSplashByVariant.Length) variantIdx = 0;
                    tex = _miniSplashByVariant[variantIdx];
                    if (tex == null) continue;
                    src = _fbMiniSplash.GetFrameRect(_fbMiniSplash.GetFrameAtNormalizedTime(t));
                    break;
                case WakeParticleKind.BubbleMagic:
                    if (_fbBubbleMagic == null || _bubbleMagicByVariant.Length == 0) continue;
                    if (variantIdx >= _bubbleMagicByVariant.Length) variantIdx = 0;
                    tex = _bubbleMagicByVariant[variantIdx];
                    if (tex == null) continue;
                    src = _fbBubbleMagic.GetFrameRect(_fbBubbleMagic.GetFrameAtNormalizedTime(t));
                    break;
                case WakeParticleKind.RainSplash:
                    if (_fbRainSplash == null || _rainSplashByVariant.Length == 0) continue;
                    if (variantIdx >= _rainSplashByVariant.Length) variantIdx = 0;
                    tex = _rainSplashByVariant[variantIdx];
                    if (tex == null) continue;
                    src = _fbRainSplash.GetFrameRect(_fbRainSplash.GetFrameAtNormalizedTime(t));
                    break;
                case WakeParticleKind.SoftCircle:
                default:
                    tex = softCircleTex;
                    src = new Rectangle(0, 0, softCircleTex.Width, softCircleTex.Height);
                    break;
            }
            // White tint with the per-particle alpha for fade. With
            // premultiplied alpha blend, Color.White * a/255 gives
            // (a, a, a, a) which preserves the baked texture's color
            // and scales it by the particle's fade.
            var tint = new Color(a, a, a, a);

            var sp = renderer.WorldToScreen(p.Pos, p.WorldHeight, camera);
            // Scale: target pixel size on screen, divided by source frame
            // width. Flipbook frames are large (~400-500px); particles draw
            // at a small fraction of source dimensions.
            float sizePx = p.Size * sizeMul * camera.Zoom;
            float scale = sizePx / Math.Max(1, src.Width);
            if (scale <= 0f) continue;

            var origin = new Vector2(src.Width * 0.5f, src.Height * 0.5f);
            sb.Draw(tex, sp, src, tint, p.Rotation, origin, scale, SpriteEffects.None, 0f);
        }
    }
}
