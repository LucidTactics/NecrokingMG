using System.Collections.Generic;
using Necroking.Core;
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
    private const string Tag = "combat";
    private readonly List<CombatLogEntry> _entries = new();

    public void AddEntry(CombatLogEntry entry)
    {
        _entries.Add(entry);
        if (_entries.Count > MaxEntries)
            _entries.RemoveAt(0);

        // Write to file
        WriteEntryToFile(entry);
    }

    public void Clear()
    {
        _entries.Clear();
        DebugLog.Clear(Tag);
    }

    public IReadOnlyList<CombatLogEntry> Entries => _entries;

    private static string FormatTime(float t)
    {
        int totalSec = (int)t;
        return $"{totalSec / 60:D2}:{totalSec % 60:D2}";
    }

    private static void WriteEntryToFile(CombatLogEntry e)
    {
        string time = FormatTime(e.Timestamp);
        string outcome = e.Outcome.ToString();

        // Header: attack roll summary
        DebugLog.Log(Tag, $"{time}  {e.AttackerName}({e.AttackerFaction}) attacks {e.DefenderName}({e.DefenderFaction}) with {e.WeaponName} -> {outcome}");

        // Detail lines (indented to align past "MM:SS  ")
        int totalAtk = e.AttackBase + e.AttackDRN;
        int totalDef = e.DefenseBase + e.DefenseDRN;
        DebugLog.Log(Tag, $"         Attack: {e.AttackBase}+{e.AttackDRN}={totalAtk} vs Defense: {e.DefenseBase}+{e.DefenseDRN}={totalDef}");

        if (e.HarassmentPenalty != 0)
            DebugLog.Log(Tag, $"         Harassment penalty: {e.HarassmentPenalty}");

        if (e.Outcome == CombatLogOutcome.Hit)
        {
            int totalDmg = e.DamageBase + e.DamageDRN;
            int totalProt = e.ProtBase + e.ProtDRN;
            DebugLog.Log(Tag, $"         Hit {e.HitLocationName}: Damage {e.DamageBase}+{e.DamageDRN}={totalDmg} - Prot {e.ProtBase}+{e.ProtDRN}={totalProt} = {e.NetDamage} net");
        }
    }
}
