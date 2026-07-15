using System;
using Necroking.Data;
using Necroking.Lib;
using Necroking.Movement;

namespace Necroking.GameSystems;

/// <summary>
/// Spirit Walk (spell "spirit_walk" / potion_spirit_walk): the necromancer's body
/// drops lifeless where it stands and the player pilots a detached spirit for a
/// fixed duration (default 30s), then snaps back to the body and wakes up.
///
/// The body is held prone by the Incapacitating <see cref="SleepBuffId"/> buff —
/// BuffSystem owns the knockdown hold and the standup release (same mechanism as
/// the paralysis stun; a bare Incap hold with no owning buff gets auto-released by
/// TickBuffs) — and is parked on AI=IdleAtPoint / Archetype=0 so neither player
/// input nor an archetype brain moves it. The spirit is a GhostMode clone of the
/// body's def: damage-immune, no collision, ghost tint + hover anim (all free with
/// GhostMode), a flat <see cref="SpiritSpeed"/> cap (Unit.GhostSpeedOverride,
/// consumed by Locomotion), double sight (Unit.DetectionRange feeds
/// FogOfWarSystem), and invisible to enemy aggro (GhostMode skips in
/// WorldQuery / SubroutineSteps / VillageThreat). The horde stays anchored to the
/// sleeping body via Simulation.HordeAnchorUnitId.
///
/// Same pattern as JumpSystem / TickPendingReanimRises: a plain timer system,
/// ticked from Game1.Update on WorldDt (a buff can't mutate the world on expiry).
/// State is per-game — Game1.ResetWorldState calls <see cref="Reset"/>. Stores
/// Unit.Ids, never indices (swap-pop safety).
/// </summary>
public static class SpiritWalkSystem
{
    public const float DefaultDuration = 30f;
    /// <summary>Flat spirit fly speed (wu/s) — read by Locomotion's GhostMode branch.</summary>
    public const float SpiritSpeed = 30f;
    public const float SightMultiplier = 2f;
    public const string SleepBuffId = "buff_spirit_sleep";
    /// <summary>Seconds before snap-back at which the spirit gets a "Returning..." warning.</summary>
    private const float ReturnWarnTime = 5f;

    private static uint _bodyId;
    private static uint _spiritId;
    private static float _timer;
    private static bool _warned;
    private static AIBehavior _bodyAI;
    private static byte _bodyArchetype;

    public static bool Active => _spiritId != 0;

    /// <summary>Per-game reset — called from Game1.ResetWorldState.</summary>
    public static void Reset()
    {
        _bodyId = 0;
        _spiritId = 0;
        _timer = 0f;
        _warned = false;
    }

    /// <summary>Leave the body: spawn the possessed spirit and drop the body prone.
    /// Possession is player-only — <paramref name="bodyIdx"/> must be the live
    /// necromancer. No-op while a walk is already active.</summary>
    public static void Begin(Game1 game, int bodyIdx, float duration = DefaultDuration)
    {
        if (Active) return;
        var sim = game._sim;
        var units = sim.UnitsMut;
        if (bodyIdx < 0 || bodyIdx >= units.Count || !units[bodyIdx].Alive) return;
        if (bodyIdx != sim.NecromancerIndex) return;

        var body = units[bodyIdx];

        // Spawn the spirit first — if the def can't spawn, the body never drops.
        int spiritIdx = game.SpawnUnit(body.UnitDefID, body.Position);
        if (spiritIdx < 0) return;
        var spirit = units[spiritIdx];

        _bodyId = body.Id;
        _spiritId = spirit.Id;
        _timer = duration > 0f ? duration : DefaultDuration;
        _warned = false;

        spirit.GhostMode = true;
        spirit.GhostSpeedOverride = SpiritSpeed;
        float baseSight = body.DetectionRange > 0f ? body.DetectionRange : spirit.DetectionRange;
        spirit.DetectionRange = baseSight * SightMultiplier;
        spirit.FacingAngle = body.FacingAngle;
        sim.Horde.RemoveUnit(spirit.Id);

        // Possess: player input, camera follow, and spell casts all key off
        // NecromancerIndex. (SpawnUnit already repointed it because the def is
        // PlayerControlled — this just makes the handoff explicit.)
        sim.SetNecromancerIndex(spiritIdx);

        // The body drops lifeless. The sleep buff outlives the walk by a second so
        // End()'s RemoveBuff is what triggers the standup, not the buff clock.
        _bodyAI = body.AI;
        _bodyArchetype = body.Archetype;
        body.AI = AIBehavior.IdleAtPoint;
        body.Archetype = 0;
        body.PreferredVel = Vec2.Zero;
        var sleepDef = game._gameData.Buffs.Get(SleepBuffId);
        if (sleepDef != null)
            BuffSystem.ApplyBuffWithDuration(units, bodyIdx, sleepDef, _timer + 1f, game._gameData);

        sim.HordeAnchorUnitId = _bodyId;
    }

    /// <summary>Snap back: despawn the spirit, wake the body, restore control.
    /// Safe to call when inactive (no-op) or with a dead/removed body.</summary>
    public static void End(Game1 game)
    {
        if (!Active) return;
        var sim = game._sim;
        var units = sim.UnitsMut;

        sim.HordeAnchorUnitId = 0;

        if (units.TryGetIndex(_spiritId, out int spiritIdx))
            sim.RemoveUnitTracked(spiritIdx);

        // Re-resolve after the swap-pop removal above.
        if (units.TryGetIndex(_bodyId, out int bodyIdx))
        {
            var body = units[bodyIdx];
            body.AI = _bodyAI;
            body.Archetype = _bodyArchetype;
            // Wake up: dropping the sleep buff makes BuffSystem start the standup
            // recovery on its next tick; the input lock releases when it finishes.
            BuffSystem.RemoveBuff(units, bodyIdx, SleepBuffId);
            sim.SetNecromancerIndex(body.Alive ? bodyIdx : units.FindAliveNecromancerIndex());
        }
        else
        {
            sim.SetNecromancerIndex(units.FindAliveNecromancerIndex());
        }

        _bodyId = 0;
        _spiritId = 0;
        _timer = 0f;
    }

    /// <summary>Tick the walk timer. Called from Game1.Update on WorldDt, so pause
    /// and editors freeze the countdown.</summary>
    public static void Update(Game1 game, float dt)
    {
        if (!Active) return;
        var sim = game._sim;

        // Body killed/removed mid-walk, or spirit lost → snap back now (a dead
        // body ends the walk; the normal death flow takes over from there).
        if (!sim.Units.TryGetIndex(_bodyId, out int bodyIdx) || !sim.Units[bodyIdx].Alive
            || !sim.Units.TryGetIndex(_spiritId, out int spiritIdx))
        {
            End(game);
            return;
        }

        _timer -= dt;
        if (!_warned && _timer <= ReturnWarnTime)
        {
            _warned = true;
            game.SpawnCastFailText(spiritIdx, "Returning...");
        }
        if (_timer <= 0f)
            End(game);
    }
}
