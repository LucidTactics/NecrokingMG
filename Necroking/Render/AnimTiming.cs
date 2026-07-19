using System;

namespace Necroking.Render;

/// <summary>
/// The "make the animation fit the gameplay clock" half of the timing-vs-animation
/// pattern (its sibling is <see cref="Necroking.Game.ScheduledTasks"/>, the "fire a
/// gameplay event later" half).
///
/// The anti-pattern this exists to kill: gameplay reads <c>AnimController.IsAnimFinished</c>
/// to decide *when* something happens (corpse gets consumed, essence is spent, craft
/// starts). That makes frame-timing and art clip lengths load-bearing for the rules.
///
/// The fix: a gameplay system owns the duration (a designer value like a table's
/// ProcessTime, or the natural length of a clip picked once). The animation then merely
/// *reflects* that duration — its PlaybackSpeed is stretched/compressed to land on the
/// same clock. The animation never advances gameplay; gameplay never waits on the
/// animation. These helpers compute that PlaybackSpeed.
///
/// All methods are pure and allocation-free — safe to call every frame (they recompute
/// cheaply so live edits to the target duration track immediately).
/// </summary>
public static class AnimTiming
{
    /// <summary>Natural (PlaybackSpeed-1) wall-clock length of one play of
    /// <paramref name="state"/>, in seconds. 0 if the clip has no timing metadata.</summary>
    public static float NaturalSeconds(AnimController ctrl, AnimState state)
        => ctrl.AnimDurationMsFor(state) / 1000f;

    /// <summary>PlaybackSpeed that makes ONE play of <paramref name="state"/> last exactly
    /// <paramref name="targetSeconds"/>. A longer target slows the clip (&lt;1); a shorter
    /// one speeds it up (&gt;1). Returns 1 when the target is non-positive or the clip has no
    /// timing metadata (nothing to fit to). Apply the result to
    /// <see cref="AnimController.PlaybackSpeed"/> right before the per-frame Update.</summary>
    public static float FitOneShot(AnimController ctrl, AnimState state, float targetSeconds)
    {
        float natural = NaturalSeconds(ctrl, state);
        if (natural <= 0.0001f || targetSeconds <= 0.0001f) return 1f;
        return natural / targetSeconds;
    }

    /// <summary>Fit a channeled Start → Loop → (Finish) triple into
    /// <paramref name="targetSeconds"/>. Two regimes, matching how a channel should feel:
    ///  - target LONGER than one natural Start+Loop+Finish cycle → play at speed 1 and let
    ///    the LOOP repeat to fill the slack (a longer channel is *more loops*, not a
    ///    slow-motion clip). <paramref name="loopBudget"/> comes back &gt; one loop cycle.
    ///  - target TOO SHORT for even one cycle → time-stretch the whole channel (speed &gt; 1,
    ///    frame-rate accelerated) so it still fits, keeping exactly one scaled loop;
    ///    <paramref name="loopBudget"/> is that one scaled cycle (lC / speed).
    /// The caller spends <paramref name="loopBudget"/> wall-clock in the Loop phase, then
    /// fires the payoff and advances to Finish. This is the shared math behind the channeled
    /// reanimation cast and the necro-bench imbue-table craft loop.</summary>
    public static float FitChannel(AnimController ctrl, AnimState start, AnimState loop,
        AnimState? finish, float targetSeconds, out float loopBudget)
    {
        float sD = NaturalSeconds(ctrl, start);
        float lC = MathF.Max(0.05f, NaturalSeconds(ctrl, loop));
        float fD = finish.HasValue ? NaturalSeconds(ctrl, finish.Value) : 0f;
        float baseTotal = sD + lC + fD;
        bool tooShort = targetSeconds > 0.01f && baseTotal > targetSeconds;
        float spd = tooShort ? baseTotal / targetSeconds : 1f;
        loopBudget = tooShort ? lC / spd : MathF.Max(lC, targetSeconds - sD - fD);
        return spd;
    }
}
