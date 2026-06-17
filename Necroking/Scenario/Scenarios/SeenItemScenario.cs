using Necroking.Core;
using Necroking.Data.Registries;
using Necroking.GameSystems;

namespace Necroking.Scenario.Scenarios;

/// <summary>
/// Verifies the "ever seen" inventory registry and the rule that a potion
/// throw-spell (SpellDef.ConsumesItem) is only visible once its item has been
/// seen. Mirrors the grimoire's visibility predicate in Game1.
/// </summary>
public class SeenItemScenario : ScenarioBase
{
    public override string Name => "seen_item";

    private bool _complete;
    private int _fail; // 0 = pass

    // Same gate the grimoire's _canShow uses for potion throw-spells.
    private static bool Visible(SpellDef spell, Inventory inv)
        => string.IsNullOrEmpty(spell.ConsumesItem) || inv.HasEverSeen(spell.ConsumesItem);

    public override void OnInit(Simulation sim)
    {
        DebugLog.Clear(ScenarioLog);
        DebugLog.Log(ScenarioLog, "=== Seen-Item / potion-spell visibility ===");
        var gd = sim.GameData;
        if (gd == null) { Fail(1, "no GameData"); return; }

        // Find a generated potion throw-spell (has a ConsumesItem).
        SpellDef? potionSpell = null;
        foreach (var id in gd.Spells.GetIDs())
        {
            var s = gd.Spells.Get(id);
            if (s != null && !string.IsNullOrEmpty(s.ConsumesItem)) { potionSpell = s; break; }
        }
        if (potionSpell == null) { Fail(2, "no potion throw-spell (ConsumesItem) found"); return; }
        string item = potionSpell.ConsumesItem;
        DebugLog.Log(ScenarioLog, $"Using potion spell '{potionSpell.Id}' -> item '{item}'");

        var inv = new Inventory(20, gd.Items);

        // Unseen -> hidden.
        DebugLog.Log(ScenarioLog, $"before: HasEverSeen({item})={inv.HasEverSeen(item)} visible={Visible(potionSpell, inv)}");
        if (inv.HasEverSeen(item)) { Fail(3, "item seen before ever added"); return; }
        if (Visible(potionSpell, inv)) { Fail(4, "potion spell visible before item seen"); return; }

        // See it -> visible.
        inv.AddItem(item, 2);
        DebugLog.Log(ScenarioLog, $"after add: HasEverSeen={inv.HasEverSeen(item)} visible={Visible(potionSpell, inv)} count={inv.GetItemCount(item)}");
        if (!inv.HasEverSeen(item)) { Fail(5, "item not seen after add"); return; }
        if (!Visible(potionSpell, inv)) { Fail(6, "potion spell hidden after item seen"); return; }

        // Use it all up -> still seen (stays visible).
        inv.RemoveItem(item, 2);
        DebugLog.Log(ScenarioLog, $"after remove all: count={inv.GetItemCount(item)} HasEverSeen={inv.HasEverSeen(item)} visible={Visible(potionSpell, inv)}");
        if (!inv.HasEverSeen(item)) { Fail(7, "item forgotten after being used up"); return; }
        if (!Visible(potionSpell, inv)) { Fail(8, "potion spell hidden after item used up"); return; }

        // A non-potion spell (no ConsumesItem) is always visible.
        SpellDef? plain = null;
        foreach (var id in gd.Spells.GetIDs())
        {
            var s = gd.Spells.Get(id);
            if (s != null && string.IsNullOrEmpty(s.ConsumesItem)) { plain = s; break; }
        }
        if (plain != null && !Visible(plain, inv)) { Fail(9, $"non-potion spell '{plain.Id}' incorrectly hidden"); return; }
        DebugLog.Log(ScenarioLog, $"non-potion spell always visible: {(plain != null ? plain.Id : "(none)")}");

        DebugLog.Log(ScenarioLog, "All checks passed.");
    }

    private void Fail(int code, string why)
    {
        _fail = code;
        DebugLog.Log(ScenarioLog, $"FAIL ({code}): {why}");
    }

    public override void OnTick(Simulation sim, float dt) => _complete = true;
    public override bool IsComplete => _complete;

    public override int OnComplete(Simulation sim)
    {
        DebugLog.Log(ScenarioLog, $"Result: {(_fail == 0 ? "PASS" : $"FAIL ({_fail})")}");
        return _fail;
    }
}
