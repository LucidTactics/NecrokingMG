using System.Collections.Generic;

namespace Necroking.Game;

/// <summary>
/// Cumulative counter for "milestone" events used as skill prerequisites
/// (e.g. "raise 5 corpses"). Counts are not consumed when a skill is learned —
/// once a milestone is hit, every skill that requires it stays satisfied.
///
/// Hook gameplay systems by calling <see cref="Tally"/> when the relevant event
/// happens (e.g. on corpse-raise success, kill confirmation, spell cast).
/// Per-game only — no persistence yet (cleared on Reset / new game).
/// </summary>
public class SkillEventTracker
{
    private readonly Dictionary<string, int> _counts = new();

    public void Reset() => _counts.Clear();

    public void Tally(string eventKey, int n = 1)
    {
        if (string.IsNullOrEmpty(eventKey) || n == 0) return;
        _counts.TryGetValue(eventKey, out var cur);
        _counts[eventKey] = cur + n;
    }

    public int Get(string eventKey) => _counts.GetValueOrDefault(eventKey);
}
