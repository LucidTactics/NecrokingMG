using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Necroking.Core;

namespace Necroking;

/// <summary>
/// The Game1 draw pipeline, extracted out of the former <c>Game1.Render*.cs</c>
/// partial files into its own class (2026-06-30). All shared game state is reached
/// through the <see cref="_g"/> back-reference to the owning <see cref="Game1"/>;
/// the <c>Game1.Draw</c> MonoGame lifecycle override forwards into <see cref="Draw"/>.
/// Split across GameRenderer.Draw/World/Units/Hud/Corpses.cs by render concern.
/// </summary>
internal sealed partial class GameRenderer
{
    private readonly Game1 _g;
    public GameRenderer(Game1 g) { _g = g; }

    // --- Render-only constants/statics, moved here out of Game1 ---

    // Carried body bag offset and scale (pixel offsets on top of hilt position)
    private const float CarryOffsetX = 4.5f;
    private const float CarryOffsetY = 8.5f;
    private const float CarryBagScale = 3.4f;
    // Raw-corpse carry: nudge the centroid-pegged corpse vertically off the hands
    // (negative = up, so the body rests slightly on top of the hands). Tunable.
    private const float CarriedCorpseHandOffsetY = -2f;

    // Hover-marker ground geometry, shared by the renderer (DrawHoverGroundMarkers)
    // and the building hit-test (CursorInObjectMarker) so the pickable area matches
    // the drawn shape exactly.
    private const float HoverMarkerRadiusMul = 1.5f;   // visual marker radius over the collision footprint
    private const float HoverMarkerFlatten   = 0.42f;  // vertical squash for the ground-plane (RTS) look

    private const float ForagableWiggleRange = 3f;

    // 8-direction offsets: N, NE, E, SE, S, SW, W, NW
    private static readonly float[][] _outlineDirs =
    {
        new[] { 0f, -1f }, new[] { 1f, -1f }, new[] { 1f, 0f }, new[] { 1f, 1f },
        new[] { 0f,  1f }, new[] {-1f,  1f }, new[] {-1f, 0f }, new[] {-1f,-1f }
    };

    // Hardcoded ghost outline params matching C++
    private static readonly HdrColor _ghostColor1 = new(140, 200, 255, 45, 1.0f);
    private static readonly HdrColor _ghostColor2 = new(170, 215, 255, 60, 1.1f);

    // Aggression level names + one-line descriptions, indexed 0 (least) .. 4 (most).
    private static readonly string[] AggroNames =
        { "Defensive", "Cautious", "Balanced", "Aggressive", "Bloodthirsty" };
    private static readonly string[] AggroDescs =
    {
        "Hold tight - strike only enemies at the formation's edge.",
        "Engage threats just beyond the formation.",
        "Balanced engagement and leash (default).",
        "Press forward - wider engagement, longer leash.",
        "Engagement and leash doubled - chase enemies far.",
    };
}
