using System;
using Necroking.Core;
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
/// Pressing Q mid-walk ROOTS the spirit where it hovers (<see cref="RootSpirit"/>):
/// the player wakes up in the body right away, and the spirit stays behind as a
/// stationary scrying eye — sight halved, expiring after <see cref="EyeDuration"/>.
///
/// Same pattern as JumpSystem: a plain timer system, ticked from Game1.Update on
/// WorldDt (a buff can't mutate the world on expiry).
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
    /// <summary>Seconds a rooted spirit ("eye") keeps scrying before it fades.</summary>
    public const float EyeDuration = 60f;
    /// <summary>Rooting halves the spirit's sight (2x normal → normal).</summary>
    public const float EyeSightMultiplier = 0.5f;
    /// <summary>Seconds before snap-back at which the spirit gets a "Returning..." warning.</summary>
    private const float ReturnWarnTime = 5f;

    private static uint _bodyId;
    private static uint _spiritId;
    private static float _timer;
    private static bool _warned;
    private static AIBehavior _bodyAI;
    private static byte _bodyArchetype;

    // Rooted eye — outlives the walk itself; one at a time.
    private static uint _eyeId;
    private static float _eyeTimer;

    public static bool Active => _spiritId != 0;

    /// <summary>Per-game reset — called from Game1.ResetWorldState.</summary>
    public static void Reset()
    {
        _bodyId = 0;
        _spiritId = 0;
        _timer = 0f;
        _warned = false;
        _eyeId = 0;
        _eyeTimer = 0f;
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

        if (game._sim.UnitsMut.TryGetIndex(_spiritId, out int spiritIdx))
            game._sim.RemoveUnitTracked(spiritIdx);

        WakeBody(game);
    }

    /// <summary>Q mid-walk: root the spirit where it hovers and wake up at once.
    /// The rooted spirit becomes a stationary scrying eye — no control, half the
    /// spirit's sight — and keeps revealing fog for <see cref="EyeDuration"/>.
    /// Replaces any previous eye (one scrying spot at a time).</summary>
    public static void RootSpirit(Game1 game)
    {
        if (!Active) return;
        var sim = game._sim;
        var units = sim.UnitsMut;

        if (!units.TryGetIndex(_spiritId, out int spiritIdx))
        {
            End(game); // spirit lost — degrade to a normal snap-back
            return;
        }

        // One eye at a time: the new root replaces any previous one.
        if (_eyeId != 0 && units.TryGetIndex(_eyeId, out int oldEyeIdx))
        {
            sim.RemoveUnitTracked(oldEyeIdx);
            units.TryGetIndex(_spiritId, out spiritIdx); // re-resolve after swap-pop
        }

        // Convert the spirit into the eye: parked (never PlayerControlled, so
        // necromancer-index repairs can't pick it), pinned in place the way net
        // ghosts are, sight halved. GhostMode stays — untargetable + ghost look.
        var eye = units[spiritIdx];
        eye.AI = AIBehavior.IdleAtPoint;
        eye.Archetype = 0;
        eye.PreferredVel = Vec2.Zero;
        eye.Velocity = Vec2.Zero;
        eye.MoveTarget = eye.Position;
        eye.SpawnPosition = eye.Position;
        eye.Target = CombatTarget.None;
        eye.EngagedTarget = CombatTarget.None;
        eye.PendingAttack = CombatTarget.None;
        eye.DetectionRange *= EyeSightMultiplier;
        _eyeId = eye.Id;
        _eyeTimer = EyeDuration;

        _spiritId = 0; // the walk is over; the eye is no longer "the spirit"
        WakeBody(game);
    }

    /// <summary>Shared end-of-walk teardown: restore the body's AI/archetype, drop
    /// the sleep buff (BuffSystem then plays the standup), hand NecromancerIndex
    /// back, and clear the walk state. Handles a dead/removed body gracefully.</summary>
    private static void WakeBody(Game1 game)
    {
        var sim = game._sim;
        var units = sim.UnitsMut;

        sim.HordeAnchorUnitId = 0;

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

    /// <summary>Tick the walk + eye timers. Called from Game1.Update on WorldDt,
    /// so pause and editors freeze the countdowns.</summary>
    public static void Update(Game1 game, float dt)
    {
        // The eye ticks independently — it outlives the walk that planted it.
        if (_eyeId != 0)
        {
            _eyeTimer -= dt;
            if (!game._sim.Units.TryGetIndex(_eyeId, out int eyeIdx))
            {
                _eyeId = 0;
            }
            else if (_eyeTimer <= 0f)
            {
                game._sim.RemoveUnitTracked(eyeIdx);
                _eyeId = 0;
            }
        }

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
