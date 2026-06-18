using System.Collections.Generic;

namespace Necroking.Game;

/// <summary>
/// Central per-game tally of notable things that happen to the player — kills,
/// spell casts, corpses eaten, items crafted, and so on. A flat string-keyed
/// counter: gameplay systems call <see cref="Tally"/> when an event fires, and
/// consumers (skill prerequisites, the stats screen, future achievements /
/// quests) read cumulative totals with <see cref="Get"/>.
///
/// Counts are cumulative and never consumed — once a milestone is hit it stays
/// hit, so every skill that requires it stays satisfied. Per-game only; no
/// persistence yet (cleared on <see cref="Reset"/> / new game).
///
/// The canonical live instance hangs off <c>Simulation.PlayerEvents</c>. That is
/// where future "record an event about the player" hooks belong — call
/// <c>sim.PlayerEvents.Tally(...)</c> rather than reaching through any one
/// menu/state object. Well-known keys live in <see cref="Keys"/>; ad-hoc keys
/// work too, but prefer adding a constant so the vocabulary stays discoverable.
/// </summary>
public class PlayerEventTracker
{
    private readonly Dictionary<string, int> _counts = new();

    /// <summary>Canonical event keys. Add new player-event kinds here so every
    /// system that records or reads them shares one vocabulary.</summary>
    public static class Keys
    {
        public const string MonsterKill  = "monster_kill";
        public const string HumanKill    = "human_kill";
        public const string CastSpell    = "cast_spell";
        public const string CorpsesEaten = "corpses_eaten";
    }

    public void Reset() => _counts.Clear();

    public void Tally(string eventKey, int n = 1)
    {
        if (string.IsNullOrEmpty(eventKey) || n == 0) return;
        _counts.TryGetValue(eventKey, out var cur);
        _counts[eventKey] = cur + n;
    }

    public int Get(string eventKey) => _counts.GetValueOrDefault(eventKey);
}
