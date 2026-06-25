using Necroking.Data;
using Necroking.Movement;

namespace Necroking.GameSystems.Combat;

/// <summary>
/// Single source for the melee engage/attack range — "am I close enough to melee this
/// target". Used by the combat sim and the AI handlers so the formula and its
/// null-GameData fallback can't drift: SubroutineSteps.GetMeleeRange previously fell
/// back to 1.5f while the sim used 0.8f, a kiting/engage-range drift that surfaced in
/// null-GameData tests (live play always supplies Settings.Combat.MeleeRange).
/// </summary>
public static class MeleeRangeUtil
{
    /// <summary>Fallback base when GameData is unavailable (null-GameData tests only).</summary>
    public const float MeleeRangeBase = 0.8f;

    /// <summary>Engage/attack range = base + attacker reach (Length*0.15) + both radii.</summary>
    public static float Compute(UnitArrays units, int attackerIdx, int targetIdx, GameData? gd)
    {
        float baseRange = gd?.Settings.Combat.MeleeRange ?? MeleeRangeBase;
        return baseRange + units[attackerIdx].Stats.Length * 0.15f
            + units[attackerIdx].Radius + units[targetIdx].Radius;
    }
}
