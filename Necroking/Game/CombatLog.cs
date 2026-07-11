using System.Collections.Generic;
using Necroking.Core;
using Necroking.Data;

namespace Necroking.GameSystems;

// Whiff = the swing never rolled dice: the target escaped reach between the
// attack being queued and its animation's impact frame. Distinct from Miss
// (dice rolled, defense won) so chase whiffs are visible in the log instead
// of the swing vanishing silently.
public enum CombatLogOutcome : byte { Hit, Miss, Blocked, Whiff }

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
    public int ToughnessMit;   // damage absorbed by toughness halving (post-armor)
    public int NetDamage;
    public HitLocation HitLoc;
    public string HitLocationName = "";
    /// <summary>Free-text detail line (used by Whiff: dist vs reach). Printed
    /// instead of the roll breakdown when the dice never ran.</summary>
    public string Note = "";
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

        // Whiff: the dice never ran — print the reach note, not a roll breakdown.
        if (e.Outcome == CombatLogOutcome.Whiff)
        {
            if (!string.IsNullOrEmpty(e.Note))
                DebugLog.Log(Tag, $"         {e.Note}");
            return;
        }

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
            string tough = e.ToughnessMit != 0 ? $" - Tough {e.ToughnessMit}" : "";
            DebugLog.Log(Tag, $"         Hit {e.HitLocationName}: Damage {e.DamageBase}+{e.DamageDRN}={totalDmg} - Prot {e.ProtBase}+{e.ProtDRN}={totalProt}{tough} = {e.NetDamage} net");
        }
    }
}
