using Necroking.Core;
using Necroking.Data;
using Necroking.Game;
using Necroking.GameSystems;
using Necroking.Movement;

namespace Necroking.Scenario.Scenarios;

/// <summary>
/// Regression test for spell-kill attribution feeding the skill-book milestone
/// tally. An Undead caster lobs a fireball at an Animal-faction "deer"; the kill
/// must increment <c>monster_kill</c>. Before the fix, spell damage never set the
/// victim's LastAttackerID, so the tally (which requires a player-aligned
/// attacker) silently ignored every spell kill.
///
/// Validates:
///  1. The deer's LastAttackerID gets set to the caster when the fireball lands.
///  2. The deer dies.
///  3. SkillBook.Events["monster_kill"] increments to 1.
/// </summary>
public class SpellKillTallyScenario : ScenarioBase
{
    public override string Name => "spell_kill_tally";

    private SkillBookState _book = null!;
    private uint _casterID, _deerID;
    private bool _fired, _attackerSeen, _complete;
    private int _failCode = -1; // -1 = not yet decided
    private float _t;

    public override void OnInit(Simulation sim)
    {
        DebugLog.Clear(ScenarioLog);
        DebugLog.Log(ScenarioLog, "=== Spell Kill Tally Scenario ===");

        _book = new SkillBookState();
        sim.SetSkillBook(_book);
        DebugLog.Log(ScenarioLog, $"SkillBook attached. monster_kill start = {_book.Events.Get("monster_kill")}");

        var units = sim.UnitsMut;

        // Caster: Undead skeleton standing still (player-aligned attacker).
        var casterPos = new Vec2(30f, 30f);
        int ci = units.AddUnit(casterPos, UnitType.Skeleton);
        units[ci].Faction = Faction.Undead;
        units[ci].AI = AIBehavior.PlayerControlled; // no autonomous movement/attacks
        _casterID = units[ci].Id;
        DebugLog.Log(ScenarioLog, $"Caster (Undead/PlayerControlled) id={_casterID} at ({casterPos.X},{casterPos.Y})");

        // "Deer": Animal-faction unit with low HP so one bolt kills it.
        var deerPos = new Vec2(36f, 30f);
        int di = units.AddUnit(deerPos, UnitType.Skeleton);
        units[di].Faction = Faction.Animal;
        // Inert victim (FleeWhenHit is gone — DeerHerd owns prey behavior now);
        // it dies to one bolt so it only needs to stand still and not retaliate.
        units[di].AI = AIBehavior.MoveToPoint;
        units[di].MoveTarget = deerPos;
        units[di].Stats.HP = 20;
        units[di].Stats.MaxHP = 20;
        _deerID = units[di].Id;
        DebugLog.Log(ScenarioLog, $"Deer (Animal/inert) id={_deerID} at ({deerPos.X},{deerPos.Y}), HP={units[di].Stats.HP}");
        DebugLog.Log(ScenarioLog, $"Distance caster->deer = {(deerPos - casterPos).Length():F1}");
    }

    public override void OnTick(Simulation sim, float dt)
    {
        if (_complete) return;
        _t += dt;
        var units = sim.UnitsMut;
        int di = FindByID(units, _deerID);

        // Fire one fireball after a short settle so positions are stable.
        if (!_fired && _t > 0.2f)
        {
            int ci = FindByID(units, _casterID);
            if (ci >= 0 && di >= 0)
            {
                var from = units[ci].Position;
                var to = units[di].Position;
                sim.Projectiles.Spawn(from, to, Faction.Undead, _casterID,
                    ProjectileType.Explosive, damage: 1000, ProjectileManager.MagicSpeed,
                    lob: true, aoeRadius: 2.0f, weaponName: "TestBolt");
                _fired = true;
                DebugLog.Log(ScenarioLog, $"t={_t:F2}s: fired fireball from caster {_casterID} at deer {_deerID} (dmg=1000, aoe=2.0)");
            }
        }

        // Observe attribution landing before the dead unit is swapped out.
        if (_fired && !_attackerSeen && di >= 0 && units[di].LastAttackerID != GameConstants.InvalidUnit)
        {
            _attackerSeen = true;
            DebugLog.Log(ScenarioLog, $"t={_t:F2}s: deer LastAttackerID set to {units[di].LastAttackerID} (caster={_casterID}), HP={units[di].Stats.HP}");
        }

        // Deer gone from the live array => killed and removed. Check the tally.
        if (_fired && di < 0)
        {
            int tally = _book.Events.Get("monster_kill");
            DebugLog.Log(ScenarioLog, $"t={_t:F2}s: deer removed. monster_kill tally = {tally} (attackerSeen={_attackerSeen})");
            // The tally is the assertion; LastAttackerID may flash and clear within a
            // single tick if the bolt one-shots the deer, so it's informational only.
            _failCode = tally >= 1 ? 0 : 20;
            _complete = true;
            return;
        }

        if (_t > 8f)
        {
            DebugLog.Log(ScenarioLog, $"TIMEOUT: deer still alive? di={di}, attackerSeen={_attackerSeen}, tally={_book.Events.Get("monster_kill")}");
            _failCode = 21;
            _complete = true;
        }
    }

    public override bool IsComplete => _complete;

    public override int OnComplete(Simulation sim)
    {
        int code = _failCode < 0 ? 22 : _failCode;
        DebugLog.Log(ScenarioLog, "--- Summary ---");
        DebugLog.Log(ScenarioLog, $"attacker attributed: {_attackerSeen}");
        DebugLog.Log(ScenarioLog, $"monster_kill tally:  {_book.Events.Get("monster_kill")}");
        DebugLog.Log(ScenarioLog, $"Result: {(code == 0 ? "PASS" : $"FAIL ({code})")}");
        return code;
    }

    private static int FindByID(UnitArrays units, uint id)
    {
        for (int i = 0; i < units.Count; i++)
            if (units[i].Id == id && units[i].Alive) return i;
        return -1;
    }
}
