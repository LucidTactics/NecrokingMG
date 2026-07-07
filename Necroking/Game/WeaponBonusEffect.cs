namespace Necroking.GameSystems;

/// <summary>
/// What kind of effect a WeaponBonusEffect represents.
/// BonusDamage  — extra damage of a chosen type/flags applied per attack hit.
/// ZombieOnDeath — chance roll per hit; on success, sets defender's ZombieOnDeath flag.
/// </summary>
public enum BonusEffectKind : byte
{
    BonusDamage,
    ZombieOnDeath,
}

/// <summary>
/// One effect that fires when an attacker lands a melee hit. Attached to a unit's
/// BonusEffects list (lazy-allocated). Each entry is rolled / applied independently
/// inside the melee resolution path — extra damage runs through DamageSystem.Apply
/// using the configured DmgType + Flags, which intentionally does NOT re-trigger
/// the BonusEffects iteration (that lives in the attack-resolution code, not in
/// DamageSystem.Apply), so effects don't recurse on themselves.
///
/// Permanent=true entries never expire. Permanent=false entries decrement
/// ExpiryTimer each tick (via WeaponBonusEffectSystem.Tick) and are removed at 0.
/// </summary>
public struct WeaponBonusEffect
{
    public BonusEffectKind Kind;
    public DamageType DmgType;     // BonusDamage: Physical / Poison / Fatigue
    public DamageFlags DmgFlags;   // BonusDamage: ArmorNegating, etc.
    public int Amount;              // BonusDamage: raw damage before armor
    public byte ChancePct;          // 0-100; 0 or 100 = always (treat 0 as "always" for convenience)
    public bool Permanent;          // true = no expiry
    public float ExpiryTimer;       // seconds remaining when Permanent=false

    public static WeaponBonusEffect Damage(DamageType type, int amount, DamageFlags flags = DamageFlags.None)
        => new() { Kind = BonusEffectKind.BonusDamage, DmgType = type, DmgFlags = flags, Amount = amount, ChancePct = 100, Permanent = true };

    public static WeaponBonusEffect ZombieOnDeath(int chancePct)
        => new() { Kind = BonusEffectKind.ZombieOnDeath, ChancePct = (byte)chancePct, Permanent = true };
}

/// <summary>
/// Lifecycle owner for per-unit WeaponBonusEffect lists (Unit.BonusEffects).
/// Add merges/refreshes timed entries; Tick expires them. This is the single
/// home for "this unit's melee hits carry a timed extra effect" — potion weapon
/// coats (poison / zombie) are expressed as timed entries here, not as ad-hoc
/// unit fields.
/// </summary>
public static class WeaponBonusEffectSystem
{
    /// <summary>
    /// Add a bonus effect to a unit's BonusEffects list (lazy-allocated).
    /// Timed entries (Permanent=false) merge with an existing timed entry of the
    /// same Kind+DmgType — re-applying (e.g. re-drinking a coat potion) refreshes
    /// the timer/amount instead of stacking a duplicate. Permanent entries
    /// (table-crafted) always append and are never merge targets.
    /// </summary>
    public static void Add(Necroking.Movement.UnitArrays units, int unitIdx, WeaponBonusEffect effect)
    {
        if (unitIdx < 0 || unitIdx >= units.Count) return;
        var u = units[unitIdx];
        u.BonusEffects ??= new System.Collections.Generic.List<WeaponBonusEffect>();
        if (!effect.Permanent)
        {
            for (int i = 0; i < u.BonusEffects.Count; i++)
            {
                var e = u.BonusEffects[i];
                if (!e.Permanent && e.Kind == effect.Kind && e.DmgType == effect.DmgType)
                {
                    u.BonusEffects[i] = effect; // refresh timer + amount
                    return;
                }
            }
        }
        u.BonusEffects.Add(effect);
    }

    /// <summary>
    /// Decrement ExpiryTimer on every non-Permanent bonus effect and remove entries
    /// that reach 0. Called once per world tick from Simulation.Tick.
    /// </summary>
    public static void Tick(Necroking.Movement.UnitArrays units, float dt)
    {
        for (int i = 0; i < units.Count; i++)
        {
            var list = units[i].BonusEffects;
            if (list == null || list.Count == 0) continue;
            for (int j = list.Count - 1; j >= 0; j--)
            {
                var e = list[j];
                if (e.Permanent) continue;
                e.ExpiryTimer -= dt;
                if (e.ExpiryTimer <= 0f) list.RemoveAt(j);
                else list[j] = e;
            }
        }
    }
}
