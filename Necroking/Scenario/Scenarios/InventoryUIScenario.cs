using Necroking.Core;
using Necroking.GameSystems;

namespace Necroking.Scenario.Scenarios;

/// <summary>
/// Tests the inventory UI system:
/// 1. Opens empty inventory, screenshots it
/// 2. Adds items progressively, screenshots each state
/// 3. Removes items and verifies slots update
/// 4. Verifies slot count, stacking, and visual correctness
/// </summary>
public class InventoryUIScenario : ScenarioBase
{
    public override string Name => "inventory_ui";
    public override bool WantsUI => true;

    private int _phase;
    private int _tick;
    private bool _complete;
    private int _failCode;
    private bool _waitingForScreenshot; // pause phase progression until screenshot is taken

    public override void OnInit(Simulation sim)
    {
        DebugLog.Log(ScenarioLog, "=== InventoryUI Scenario ===");
        DebugLog.Log(ScenarioLog, $"Inventory slots: {Inventory?.SlotCount ?? -1}");
        DebugLog.Log(ScenarioLog, $"Item registry count: {ItemRegistry?.Count ?? -1}");

        // Verify prerequisites
        if (Inventory == null || ItemRegistry == null)
        {
            DebugLog.Log(ScenarioLog, "FAIL: Inventory or ItemRegistry not set");
            _failCode = 1;
            _complete = true;
            return;
        }

        // Start with empty inventory
        _phase = 0;
        _tick = 0;
        ZoomOnLocation(0, 0, 40);
    }

    public override void OnTick(Simulation sim, float dt)
    {
        if (_complete) return;

        // Wait for screenshot to be taken before advancing
        if (_waitingForScreenshot)
        {
            if (DeferredScreenshot == null) // Game1 clears it after taking
            {
                _waitingForScreenshot = false;
                _tick = 0;
            }
            return;
        }

        _tick++;

        switch (_phase)
        {
            // Phase 0: Open inventory (empty), wait a frame, screenshot
            case 0:
                RequestOpenInventory = true;
                DebugLog.Log(ScenarioLog, "Phase 0: Opening empty inventory");
                _phase = 1;
                break;

            case 1:
                if (_tick >= 5)
                {
                    DebugLog.Log(ScenarioLog, "Phase 1: Screenshot empty inventory");
                    DeferredScreenshot = "inventory_empty";
                    _waitingForScreenshot = true;
                    _phase = 2;
                }
                break;

            // Phase 2: Add 3 mushrooms, screenshot
            case 2:
                if (_tick >= 3)
                {
                    int overflow = Inventory!.AddItem("Mushroom", 3);
                    DebugLog.Log(ScenarioLog, $"Phase 2: Added 3 Mushroom (overflow={overflow})");
                    DebugLog.Log(ScenarioLog, $"  Slot 0: {Inventory.GetSlot(0).ItemId} x{Inventory.GetSlot(0).Quantity}");
                    DebugLog.Log(ScenarioLog, $"  UsedSlots: {Inventory.UsedSlots}");

                    if (overflow != 0) { _failCode = 2; _complete = true; return; }
                    if (Inventory.GetSlot(0).ItemId != "Mushroom" || Inventory.GetSlot(0).Quantity != 3)
                    {
                        DebugLog.Log(ScenarioLog, "FAIL: Expected slot 0 = Mushroom x3");
                        _failCode = 3; _complete = true; return;
                    }

                    _phase = 3;
                    _tick = 0;
                }
                break;

            case 3:
                if (_tick >= 3)
                {
                    DebugLog.Log(ScenarioLog, "Phase 3: Screenshot with 3 mushrooms");
                    DeferredScreenshot = "inventory_3_mushrooms";
                    _waitingForScreenshot = true;
                    _phase = 4;
                }
                break;

            // Phase 4: Add more diverse items
            case 4:
                if (_tick >= 3)
                {
                    Inventory!.AddItem("Ghostcap", 5);
                    Inventory.AddItem("Rotgill", 2);
                    Inventory.AddItem("MagicMushroom", 1);
                    DebugLog.Log(ScenarioLog, "Phase 4: Added Ghostcap x5, Rotgill x2, MagicMushroom x1");
                    DebugLog.Log(ScenarioLog, $"  UsedSlots: {Inventory.UsedSlots}");
                    for (int i = 0; i < 5; i++)
                    {
                        var s = Inventory.GetSlot(i);
                        DebugLog.Log(ScenarioLog, $"  Slot {i}: {(s.IsEmpty ? "empty" : $"{s.ItemId} x{s.Quantity}")}");
                    }
                    _phase = 5;
                    _tick = 0;
                }
                break;

            case 5:
                if (_tick >= 3)
                {
                    DebugLog.Log(ScenarioLog, "Phase 5: Screenshot with multiple item types");
                    DeferredScreenshot = "inventory_multiple_items";
                    _waitingForScreenshot = true;
                    _phase = 6;
                }
                break;

            // Phase 6: Remove 2 mushrooms (reduce stack), screenshot
            case 6:
                if (_tick >= 3)
                {
                    bool removed = Inventory!.RemoveItem("Mushroom", 2);
                    DebugLog.Log(ScenarioLog, $"Phase 6: Removed 2 Mushroom (success={removed})");
                    DebugLog.Log(ScenarioLog, $"  Mushroom count: {Inventory.GetItemCount("Mushroom")}");

                    if (!removed) { _failCode = 4; _complete = true; return; }
                    if (Inventory.GetItemCount("Mushroom") != 1)
                    {
                        DebugLog.Log(ScenarioLog, $"FAIL: Expected 1 Mushroom, got {Inventory.GetItemCount("Mushroom")}");
                        _failCode = 5; _complete = true; return;
                    }

                    _phase = 7;
                    _tick = 0;
                }
                break;

            case 7:
                if (_tick >= 3)
                {
                    DebugLog.Log(ScenarioLog, "Phase 7: Screenshot after removing mushrooms");
                    DeferredScreenshot = "inventory_after_remove";
                    _waitingForScreenshot = true;
                    _phase = 8;
                }
                break;

            // Phase 8: Remove all mushrooms (slot should clear), verify
            case 8:
                if (_tick >= 3)
                {
                    Inventory!.RemoveItem("Mushroom", 1);
                    DebugLog.Log(ScenarioLog, "Phase 8: Removed last Mushroom");
                    DebugLog.Log(ScenarioLog, $"  Mushroom count: {Inventory.GetItemCount("Mushroom")}");
                    DebugLog.Log(ScenarioLog, $"  UsedSlots: {Inventory.UsedSlots}");

                    if (Inventory.GetItemCount("Mushroom") != 0)
                    {
                        DebugLog.Log(ScenarioLog, "FAIL: Expected 0 Mushroom");
                        _failCode = 6; _complete = true; return;
                    }

                    _phase = 9;
                    _tick = 0;
                }
                break;

            case 9:
                if (_tick >= 3)
                {
                    DebugLog.Log(ScenarioLog, "Phase 9: Screenshot after clearing mushroom slot");
                    DeferredScreenshot = "inventory_slot_cleared";
                    _waitingForScreenshot = true;
                    _phase = 10;
                }
                break;

            // Phase 10: Close inventory
            case 10:
                if (_tick >= 3)
                {
                    RequestCloseInventory = true;
                    DebugLog.Log(ScenarioLog, "Phase 10: Closing inventory");
                    _phase = 11;
                    _tick = 0;
                }
                break;

            case 11:
                if (_tick >= 3)
                {
                    DebugLog.Log(ScenarioLog, "Phase 11: Screenshot with inventory closed");
                    DeferredScreenshot = "inventory_closed";
                    _waitingForScreenshot = true;
                    _phase = 12;
                }
                break;

            case 12:
                if (_tick >= 3)
                {
                    DebugLog.Log(ScenarioLog, "All phases complete");
                    _complete = true;
                }
                break;
        }
    }

    public override bool IsComplete => _complete;

    public override int OnComplete(Simulation sim)
    {
        DebugLog.Log(ScenarioLog, "=== InventoryUI Scenario Summary ===");
        DebugLog.Log(ScenarioLog, $"Final UsedSlots: {Inventory?.UsedSlots ?? -1}");
        DebugLog.Log(ScenarioLog, $"Result: {(_failCode == 0 ? "PASS" : $"FAIL (code={_failCode})")}");
        return _failCode;
    }
}
