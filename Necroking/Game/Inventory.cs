using System.Collections.Generic;

namespace Necroking.GameSystems;

/// <summary>
/// Simple resource inventory — tracks count of each foragable type collected.
/// </summary>
public class Inventory
{
    private readonly Dictionary<string, int> _resources = new();

    public void Add(string resourceType, int amount = 1)
    {
        if (string.IsNullOrEmpty(resourceType)) return;
        if (_resources.ContainsKey(resourceType))
            _resources[resourceType] += amount;
        else
            _resources[resourceType] = amount;
    }

    public bool Spend(string resourceType, int amount)
    {
        if (!_resources.TryGetValue(resourceType, out int current) || current < amount)
            return false;
        _resources[resourceType] -= amount;
        return true;
    }

    public int GetCount(string resourceType)
    {
        return _resources.TryGetValue(resourceType, out int count) ? count : 0;
    }

    public IReadOnlyDictionary<string, int> All => _resources;
}
