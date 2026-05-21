using Necroking.Core;
using Necroking.Data;
using Necroking.Game;
using Necroking.GameSystems;
using Necroking.Movement;

namespace Necroking.Scenario.Scenarios;

/// <summary>
/// Validates the new "skill-tree intrinsic buff" plumbing:
///   1. A bare ZombieWolf spawns with no pounce weapon (default loadout).
///   2. After we wire SkillBook + register buff_wolf_pounce against
///      [wolf, zombie] and apply it to live units, the existing wolf gains the
///      pounce weapon AND a freshly-spawned wolf gets it at spawn time.
///   3. A ZombieBoar (different tag) stays untouched.
///
/// Pass conditions:
///   - The pre-skill wolf has no weapon_wolf_pounce in MeleeWeapons.
///   - After applying the intrinsic buff, both the original and a newly-spawned
///     ZombieWolf have weapon_wolf_pounce.
///   - The newly-spawned ZombieBoar has weapon_boar_tusk but neither
///     weapon_boar_charge (stripped from def) nor weapon_wolf_pounce.
/// </summary>
public class IntrinsicBuffScenario : ScenarioBase
{
    public override string Name => "intrinsic_buff";
    public override int GridSize => 32;

    private float _t;
    private bool _phase1Pass, _phase2Pass, _phase3Pass;
    private int _wolfIdx0 = -1;
    private int _wolfIdx1 = -1;
    private int _boarIdx = -1;
    private SkillBookState _bookState = new();

    public override void OnInit(Simulation sim)
    {
        DebugLog.Clear(ScenarioLog);
        DebugLog.Log(ScenarioLog, "=== intrinsic_buff scenario start ===");

        sim.SetSkillBook(_bookState);

        // Phase 1 setup: spawn a ZombieWolf with no skill applied. Expect no
        // pounce weapon present in its melee list.
        _wolfIdx0 = sim.SpawnUnitByID("ZombieWolf", new Vec2(10, 10));
        _boarIdx  = sim.SpawnUnitByID("ZombieBoar", new Vec2(12, 10));

        if (_wolfIdx0 < 0) { DebugLog.Log(ScenarioLog, "FAIL: ZombieWolf spawn failed"); return; }
        if (_boarIdx < 0)  { DebugLog.Log(ScenarioLog, "FAIL: ZombieBoar spawn failed"); return; }

        bool wolfHasPouncePre = HasWeapon(sim, _wolfIdx0, "wolf_pounce");
        bool boarHasChargePre = HasWeapon(sim, _boarIdx, "weapon_boar_charge");
        DebugLog.Log(ScenarioLog, $"Pre-skill wolf has pounce: {wolfHasPouncePre} (expect False)");
        DebugLog.Log(ScenarioLog, $"Pre-skill boar has charge: {boarHasChargePre} (expect False — stripped from def)");
        _phase1Pass = !wolfHasPouncePre && !boarHasChargePre;
    }

    public override void OnTick(Simulation sim, float dt)
    {
        _t += dt;

        // Apply the Wolf Lunge intrinsic buff at t≈0.5s — give the sim a frame
        // or two to settle so the test ordering is unambiguous.
        if (_t > 0.5f && _wolfIdx1 < 0)
        {
            ApplyWolfLungeSkill(sim);

            // Spawn a fresh wolf AFTER the skill is learned so the spawn-path
            // hook gets to fire on it.
            _wolfIdx1 = sim.SpawnUnitByID("ZombieWolf", new Vec2(14, 10));
            DebugLog.Log(ScenarioLog, $"Spawned post-skill wolf at idx {_wolfIdx1}");

            bool wolf0HasPouncePost = HasWeapon(sim, _wolfIdx0, "wolf_pounce");
            bool wolf1HasPouncePost = HasWeapon(sim, _wolfIdx1, "wolf_pounce");
            bool boarHasPouncePost  = HasWeapon(sim, _boarIdx, "wolf_pounce");
            DebugLog.Log(ScenarioLog, $"Post-skill wolf0 has pounce: {wolf0HasPouncePost} (expect True — retroactive)");
            DebugLog.Log(ScenarioLog, $"Post-skill wolf1 has pounce: {wolf1HasPouncePost} (expect True — spawn hook)");
            DebugLog.Log(ScenarioLog, $"Post-skill boar  has pounce: {boarHasPouncePost} (expect False — wrong tag)");
            _phase2Pass = wolf0HasPouncePost && wolf1HasPouncePost && !boarHasPouncePost;
            _phase3Pass = HasWeapon(sim, _boarIdx, "weapon_boar_tusk");
        }
    }

    private void ApplyWolfLungeSkill(Simulation sim)
    {
        // Skip the full TryLearn path (skill defs may not be loaded in test
        // environment); call the SkillEffect directly with a hand-built context.
        var ctx = new Game.SkillEffects.SkillEffectContext
        {
            Inventory = new Inventory(20, sim.GameData!.Items),
            GameData = sim.GameData!,
            BookState = _bookState,
            Sim = sim,
        };
        bool ok = Game.SkillEffects.SkillEffectRegistry.Apply("grant_intrinsic_buff", ctx,
            "buff_wolf_pounce:wolf,zombie");
        DebugLog.Log(ScenarioLog, $"grant_intrinsic_buff returned {ok}");
    }

    public override bool IsComplete => _t > 2.5f;

    public override int OnComplete(Simulation sim)
    {
        DebugLog.Log(ScenarioLog, $"phase1 (pre-skill, no special weapons): {_phase1Pass}");
        DebugLog.Log(ScenarioLog, $"phase2 (post-skill wolves have pounce, boar doesn't): {_phase2Pass}");
        DebugLog.Log(ScenarioLog, $"phase3 (boar retains tusk): {_phase3Pass}");
        return (_phase1Pass && _phase2Pass && _phase3Pass) ? 0 : 1;
    }

    private static bool HasWeapon(Simulation sim, int unitIdx, string weaponId)
    {
        if (unitIdx < 0 || unitIdx >= sim.Units.Count) return false;
        var melee = sim.Units[unitIdx].Stats.MeleeWeapons;
        for (int i = 0; i < melee.Count; i++)
        {
            // Match by weapon name OR by archetype-specific marker, since the
            // resolved WeaponStats only keeps DisplayName, not the source ID.
            // Fall back: look up the def via the weapon registry by name.
            var w = melee[i];
            if (string.IsNullOrEmpty(w.Name)) continue;
            // The fastest reliable signal is matching the source-buff-or-id by
            // looking up the registry for the weapon whose DisplayName matches.
            var def = sim.GameData!.Weapons.Get(weaponId);
            if (def != null && def.DisplayName == w.Name) return true;
        }
        return false;
    }
}
