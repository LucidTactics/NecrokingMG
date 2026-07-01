using Necroking.Data;

namespace Necroking.AI;

/// <summary>
/// Shared helper for village AI: villages fear the undead specifically, not ambient
/// wildlife. The awareness system flags any cross-faction unit (so a Human would treat
/// a deer as an "enemy"), which is fine for a soldier but wrong for the village alarm —
/// a wandering deer must not make peasants flee or dogs sound the alarm. These helpers
/// scan for the undead threat only.
/// </summary>
internal static class VillageThreat
{
    /// <summary>Nearest living Undead within <paramref name="range"/>, or -1. Linear scan —
    /// only a handful of villagers/dogs call this per frame.</summary>
    public static int FindNearestUndead(ref AIContext ctx, float range)
    {
        float best = range * range;
        int bestIdx = -1;
        var me = ctx.MyPos;
        for (int j = 0; j < ctx.Units.Count; j++)
        {
            if (!ctx.Units[j].Alive) continue;
            if (ctx.Units[j].Faction != Faction.Undead) continue;
            float d = (ctx.Units[j].Position - me).LengthSq();
            if (d < best) { best = d; bestIdx = j; }
        }
        return bestIdx;
    }
}
