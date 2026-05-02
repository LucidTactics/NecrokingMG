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
