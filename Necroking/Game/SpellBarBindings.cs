using Microsoft.Xna.Framework.Input;
using Necroking.Core;

namespace Necroking;

public struct SpellBarSlot { public string SpellID; }
public struct SpellBarState { public SpellBarSlot[] Slots; }

/// <summary>The single source of truth for spell-bar slot→key bindings.
/// One bar, ten slots: Q, E, then the number row 1-8. Mouse buttons are
/// deliberately NOT bindable — LMB/RMB are reserved for world interaction
/// (see Game1.WorldClicks.cs). The cast loop, the channel-hold release
/// check, and the HUD key labels all read this table; never re-encode the
/// mapping at a call site.</summary>
public static class SpellBarBindings
{
    public const int SlotCount = 10;

    public static readonly Keys[] SlotKeys =
    {
        Keys.Q, Keys.E, Keys.D1, Keys.D2, Keys.D3,
        Keys.D4, Keys.D5, Keys.D6, Keys.D7, Keys.D8,
    };

    public static readonly string[] SlotLabels =
    {
        "Q", "E", "1", "2", "3", "4", "5", "6", "7", "8",
    };

    public static bool WasSlotPressed(InputState input, int slot)
        => (uint)slot < SlotCount && input.WasKeyPressed(SlotKeys[slot]);

    public static bool IsSlotHeld(InputState input, int slot)
        => (uint)slot < SlotCount && input.IsKeyDown(SlotKeys[slot]);
}
