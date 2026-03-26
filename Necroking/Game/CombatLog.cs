using System.Collections.Generic;
using Necroking.Data;

namespace Necroking.GameSystems;

public enum CombatLogOutcome : byte { Hit, Miss, Blocked }

public class CombatLogEntry
{
    public float Timestamp;
    public string AttackerName = "";
    public string DefenderName = "";
    public char AttackerFaction = 'A';
    public char DefenderFaction = 'B';
    public CombatLogOutcome Outcome = CombatLogOutcome.Hit;
    public string WeaponName = "";
    public int AttackBase, AttackDRN;
    public int DefenseBase, DefenseDRN;
    public int HarassmentPenalty;
    public int DamageBase, DamageDRN;
    public int ProtBase, ProtDRN;
    public int NetDamage;
    public HitLocation HitLoc;
    public string HitLocationName = "";
}

public class CombatLog
{
    private const int MaxEntries = 200;
    private readonly List<CombatLogEntry> _entries = new();

    public void AddEntry(CombatLogEntry entry)
    {
        _entries.Add(entry);
        if (_entries.Count > MaxEntries)
            _entries.RemoveAt(0);
    }

    public void Clear() => _entries.Clear();
    public IReadOnlyList<CombatLogEntry> Entries => _entries;
}
