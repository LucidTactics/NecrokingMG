using Necroking.Core;
using Necroking.Data;
using Necroking.GameSystems;
using Necroking.Movement;

namespace Necroking.Game;

/// <summary>
/// Manages glyph trap placement: T key toggle, click to place new,
/// click existing blueprint to assign necromancer to build it.
/// Extracted from Game1.Update to reduce main loop complexity.
/// </summary>
public class TrapPlacementManager
{
    public bool IsActive;

    private const float GlyphRadius = 1.125f;

    public void Update(InputState input, Simulation sim, int necroIdx, Vec2 mouseWorld)
    {
        // Toggle placement mode
        if (input.WasKeyPressed(Microsoft.Xna.Framework.Input.Keys.T))
            IsActive = !IsActive;
        if (IsActive && (input.WasKeyPressed(Microsoft.Xna.Framework.Input.Keys.Escape) || input.RightPressed))
            IsActive = false;

        if (necroIdx < 0) return;

        // Place new glyph blueprint
        if (IsActive && input.LeftPressed)
        {
            var mw = new Vec2(mouseWorld.X, mouseWorld.Y);
            if (sim.MagicGlyphs.CanPlace(mw, GlyphRadius))
            {
                var glyph = sim.MagicGlyphs.SpawnBlueprint(mw, GlyphRadius, Faction.Undead);
                glyph.Color = new HdrColor(50, 160, 40, 255, 1.5f);
                glyph.Color2 = new HdrColor(120, 230, 80, 255, 2.0f);
                glyph.TriggerDuration = 0.5f;
                glyph.ActiveDuration = 1.5f;
                glyph.Damage = 0;
                glyph.TriggerSpellID = "poison_burst";

                int glyphIdx = sim.MagicGlyphs.IndexOf(glyph);
                IsActive = false;
                AssignBuild(sim, necroIdx, glyphIdx, mw);
            }
            return; // Don't fall through to build-existing check
        }

        // Click existing blueprint glyph to build it
        if (!IsActive && input.LeftPressed
            && sim.Units[necroIdx].Routine == 0
            && sim.Units[necroIdx].CorpseInteractPhase == 0)
        {
            var mw = new Vec2(mouseWorld.X, mouseWorld.Y);
            float bestDist = 2f * 2f;
            int bestGlyphIdx = -1;
            for (int gi = 0; gi < sim.MagicGlyphs.Glyphs.Count; gi++)
            {
                var g = sim.MagicGlyphs.Glyphs[gi];
                if (!g.Alive || g.State != GlyphState.Blueprint) continue;
                float d = (g.Position - mw).LengthSq();
                if (d < bestDist) { bestDist = d; bestGlyphIdx = gi; }
            }
            if (bestGlyphIdx >= 0)
                AssignBuild(sim, necroIdx, bestGlyphIdx, sim.MagicGlyphs.Glyphs[bestGlyphIdx].Position);
        }
    }

    /// <summary>Assign necromancer to build a glyph (walk if far, immediate if close).</summary>
    private static void AssignBuild(Simulation sim, int necroIdx, int glyphIdx, Vec2 glyphPos)
    {
        var np = sim.Units[necroIdx].Position;
        float dist = (glyphPos - np).Length();
        sim.UnitsMut[necroIdx].BuildGlyphIdx = glyphIdx;
        sim.UnitsMut[necroIdx].BuildTimer = 0f;

        if (dist <= AI.PlayerControlledHandler.InteractRange)
        {
            sim.UnitsMut[necroIdx].Routine = AI.PlayerControlledHandler.RoutineBuildGlyph;
            sim.UnitsMut[necroIdx].Subroutine = AI.PlayerControlledHandler.BuildSub_WorkStart;
            sim.UnitsMut[necroIdx].CorpseInteractPhase = 1;
            sim.UnitsMut[necroIdx].PreferredVel = Vec2.Zero;
        }
        else
        {
            sim.UnitsMut[necroIdx].Routine = AI.PlayerControlledHandler.RoutineBuildGlyph;
            sim.UnitsMut[necroIdx].Subroutine = AI.PlayerControlledHandler.BuildSub_WalkToSite;
        }
    }
}
