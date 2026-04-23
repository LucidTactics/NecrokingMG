using Necroking.Core;
using Necroking.Data;

namespace Necroking.Movement;

/// <summary>
/// Central turn-rate-limited facing helper. Every voluntary facing change on
/// an AI unit should go through <see cref="TurnToward"/> or
/// <see cref="TurnTowardPosition"/> so the angular-velocity cap
/// (<c>UnitDef.TurnSpeed</c> or <c>GameSettings.Combat.TurnSpeed</c>) is
/// respected. Before this helper existed, handlers wrote
/// <c>unit.FacingAngle = ...</c> directly and snapped instantly, bypassing the
/// rate cap that <see cref="Necroking.GameSystems.Simulation.UpdateFacingAngles"/>
/// was supposed to enforce.
///
/// PlayerControlled units are exempt — the necromancer's facing is driven by
/// the mouse and must be instantaneous. Incap-locked and airborne units
/// (JumpPhase ≥ 2) also skip the rotation because their facing is frozen by
/// other systems.
///
/// No turn acceleration is modeled — turn speed is a flat deg/s. Units always
/// rotate at the same rate when they do rotate; they don't ramp up/down.
/// </summary>
public static class FacingUtil
{
    public const float DefaultTurnSpeed = 360f;

    /// <summary>Signed short-way angle from <paramref name="current"/> to
    /// <paramref name="target"/>, in the range (-180, 180] degrees.</summary>
    public static float AngleDiff(float target, float current)
    {
        float diff = target - current;
        while (diff > 180f) diff -= 360f;
        while (diff < -180f) diff += 360f;
        return diff;
    }

    /// <summary>
    /// Rotate <paramref name="unit"/>'s facing toward <paramref name="targetAngle"/>
    /// (degrees), clamped by the unit's turn speed × <paramref name="dt"/>.
    /// PlayerControlled units snap instantly; incap'd / airborne units don't rotate.
    /// </summary>
    public static void TurnToward(Unit unit, float targetAngle, float dt, GameData? gameData)
    {
        // Necromancer / player-controlled: mouse owns facing, snap.
        if (unit.AI == AIBehavior.PlayerControlled)
        {
            unit.FacingAngle = targetAngle;
            return;
        }
        // Can't rotate while knocked down / airborne.
        if (unit.Incap.IsLocked) return;
        if (unit.JumpPhase >= 2) return;

        float turnSpeed = ResolveTurnSpeed(unit, gameData);
        float diff = AngleDiff(targetAngle, unit.FacingAngle);
        float maxTurn = turnSpeed * dt;
        unit.FacingAngle += MathUtil.Clamp(diff, -maxTurn, maxTurn);
    }

    /// <summary>Rotate toward the angle pointing from <paramref name="unit"/>
    /// toward <paramref name="worldTarget"/>. No-op if target is on top of
    /// the unit.</summary>
    public static void TurnTowardPosition(Unit unit, Vec2 worldTarget, float dt, GameData? gameData)
    {
        var dir = worldTarget - unit.Position;
        if (dir.LengthSq() < 0.01f) return;
        float angle = System.MathF.Atan2(dir.Y, dir.X) * (180f / System.MathF.PI);
        TurnToward(unit, angle, dt, gameData);
    }

    /// <summary>Per-unit TurnSpeed with fallback to the global default.</summary>
    public static float ResolveTurnSpeed(Unit unit, GameData? gameData)
    {
        float global = gameData?.Settings.Combat.TurnSpeed ?? DefaultTurnSpeed;
        var def = gameData?.Units.Get(unit.UnitDefID);
        if (def?.TurnSpeed.HasValue == true) return def.TurnSpeed.Value;
        return global;
    }
}
