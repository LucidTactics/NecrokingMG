using System;
using Necroking.Core;
using Necroking.Data;
using Necroking.GameSystems;
using Necroking.Movement;

namespace Necroking.Game;

/// <summary>
/// Handles F-key corpse interaction: context-sensitive putdown/pickup/bag.
/// Priority: carrying → putdown, else → pickup nearest bagged, else → bag nearest unbagged.
/// Extracted from Game1.Update to reduce main loop complexity.
/// </summary>
public static class CorpseInteractionManager
{
    public static void TryInteract(Simulation sim, int necroIdx)
    {
        if (necroIdx < 0) return;

        var nu = sim.Units[necroIdx];

        // Currently carrying → start PutDown
        if (nu.CarryingCorpseID >= 0 && nu.CorpseInteractPhase == 0)
        {
            var cc = sim.FindCorpseByID(nu.CarryingCorpseID);
            if (cc != null)
            {
                float fRad = nu.FacingAngle * MathF.PI / 180f;
                cc.LerpStartPos = nu.Position + new Vec2(MathF.Cos(fRad), MathF.Sin(fRad)) * 0.8f;
            }
            sim.UnitsMut[necroIdx].CorpseInteractPhase = 5; // PutDown
            return;
        }

        // Currently bagging → committed, ignore
        if (nu.BaggingCorpseID >= 0) return;

        // Idle → search for nearby corpses
        if (nu.CorpseInteractPhase != 0) return;

        var np = nu.Position;
        float searchRange = 3f * 3f;

        // First: nearest bagged corpse to pickup
        int bestBaggedIdx = -1;
        float bestDist = searchRange;
        for (int ci = 0; ci < sim.Corpses.Count; ci++)
        {
            var c = sim.Corpses[ci];
            if (!c.Bagged || c.Dissolving || c.ConsumedBySummon) continue;
            if (c.DraggedByUnitID != GameConstants.InvalidUnit) continue;
            float d = (c.Position - np).LengthSq();
            if (d < bestDist) { bestDist = d; bestBaggedIdx = ci; }
        }

        if (bestBaggedIdx >= 0)
        {
            var c = sim.CorpsesMut[bestBaggedIdx];
            c.LerpStartPos = c.Position;
            sim.UnitsMut[necroIdx].CarryingCorpseID = c.CorpseID;
            sim.UnitsMut[necroIdx].CorpseInteractPhase = 4; // Pickup
            c.DraggedByUnitID = nu.Id;
            return;
        }

        // Second: nearest un-bagged corpse to start bagging
        bestDist = searchRange;
        int bestUnbaggedIdx = -1;
        for (int ci = 0; ci < sim.Corpses.Count; ci++)
        {
            var c = sim.Corpses[ci];
            if (c.Bagged || c.Dissolving || c.ConsumedBySummon) continue;
            if (c.DraggedByUnitID != GameConstants.InvalidUnit) continue;
            if (c.BaggedByUnitID != GameConstants.InvalidUnit) continue;
            float d = (c.Position - np).LengthSq();
            if (d < bestDist) { bestDist = d; bestUnbaggedIdx = ci; }
        }

        if (bestUnbaggedIdx >= 0)
        {
            var c = sim.CorpsesMut[bestUnbaggedIdx];
            sim.UnitsMut[necroIdx].BaggingCorpseID = c.CorpseID;
            sim.UnitsMut[necroIdx].BaggingTimer = 0f;
            sim.UnitsMut[necroIdx].CorpseInteractPhase = 1; // WorkStart
            c.BaggedByUnitID = nu.Id;
        }
    }
}
