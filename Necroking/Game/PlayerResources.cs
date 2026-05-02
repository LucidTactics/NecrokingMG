namespace Necroking.Game;

/// <summary>
/// Global per-player resources that aren't items in the slot inventory — currently
/// just Essence (consumed by table-crafted zombies). Wood/Stone/Gold from
/// EnvironmentObjectDef.CostWood/Stone/Gold can migrate here later when those
/// costs become real instead of legacy fields.
///
/// Storage shape kept deliberately narrow (no events, no accessors) — every
/// caller mutates Essence directly the same way. If we later need cap-clamping
/// or change notifications, add a TryAdd/TrySpend pair and gate writes through it.
/// </summary>
public class PlayerResources
{
    public int Essence;
    public int MaxEssence = 100;

    /// <summary>True if the player has at least `cost` essence available.</summary>
    public bool CanAffordEssence(int cost) => Essence >= cost;

    /// <summary>Subtract `cost` from Essence if affordable. Returns true on success.</summary>
    public bool SpendEssence(int cost)
    {
        if (cost <= 0) return true;
        if (Essence < cost) return false;
        Essence -= cost;
        return true;
    }

    /// <summary>Add essence, clamped to MaxEssence.</summary>
    public void AddEssence(int amount)
    {
        if (amount <= 0) return;
        Essence = System.Math.Min(MaxEssence, Essence + amount);
    }
}
