using Necroking.Core;
using Necroking.Data;
using Necroking.GameSystems;
using Necroking.World;

namespace Necroking.Scenario.Scenarios;

/// <summary>
/// Tests shadow pivot alignment for units, trees, and buildings.
/// Each object is placed alone with bright background so shadows are clearly visible.
/// Screenshots at high zoom verify shadow connects to object base (feet/trunk).
/// </summary>
public class ShadowPivotScenario : ScenarioBase
{
    public override string Name => "shadow_pivot";

    private bool _complete;
    private int _frame;
    private int _step;

    private const float CX = 15f, CY = 15f;

    public override void OnInit(Simulation sim)
    {
        DebugLog.Clear(ScenarioLog);
        DebugLog.Log(ScenarioLog, $"=== Scenario: {Name} ===");

        // Single skeleton — isolated so shadow is clearly visible
        int skel = sim.UnitsMut.AddUnit(new Vec2(CX, CY), UnitType.Skeleton);
        sim.UnitsMut[skel].AI = AIBehavior.IdleAtPoint;

        // Single soldier on second row
        int sold = sim.UnitsMut.AddUnit(new Vec2(CX + 8f, CY), UnitType.Soldier);
        sim.UnitsMut[sold].AI = AIBehavior.IdleAtPoint;

        // Tree
        var env = sim.EnvironmentSystem;
        if (env != null)
        {
            var treeDef = new EnvironmentObjectDef
            {
                Id = "shadow_pivot_tree", Name = "Test Tree",
                TexturePath = GamePaths.Resolve("assets/Environment/Trees/BranchlessTree1.png"),
                SpriteWorldHeight = 6f, PivotX = 0.5f, PivotY = 1f, Scale = 1f
            };
            int treeIdx = env.AddDef(treeDef);
            env.AddObject((ushort)treeIdx, CX, CY + 8f);

            // Use Cottage1 (ThatchedCottage) with actual def values from env_defs.json
            var cottageDef = new EnvironmentObjectDef
            {
                Id = "shadow_pivot_cottage", Name = "Cottage1",
                TexturePath = GamePaths.Resolve("assets/Environment/Buildings/ThatchedCottage.png"),
                SpriteWorldHeight = 8f, PivotX = 0.5f, PivotY = 1f,
                IsBuilding = true, Scale = 1.0492146f
            };
            int cottageIdx = env.AddDef(cottageDef);
            env.AddObject((ushort)cottageIdx, CX + 8f, CY + 8f);
        }

        DebugLog.Log(ScenarioLog, "Placed: skeleton, soldier, tree, house");
        ZoomOnLocation(CX, CY, 60f);
    }

    public override void OnTick(Simulation sim, float dt)
    {
        if (_complete || DeferredScreenshot != null) return;

        // Wait a couple frames for rendering to settle
        if (_frame < 3) { _frame++; return; }

        switch (_step)
        {
            case 0: // Skeleton close-up
                ZoomOnLocation(CX, CY, 100f);
                _step++; break;
            case 1:
                DeferredScreenshot = "shadow_skeleton";
                _step++; break;
            case 2: // Soldier close-up
                ZoomOnLocation(CX + 8f, CY, 100f);
                _step++; break;
            case 3:
                DeferredScreenshot = "shadow_soldier";
                _step++; break;
            case 4: // Tree close-up
                ZoomOnLocation(CX, CY + 8f, 80f);
                _step++; break;
            case 5:
                DeferredScreenshot = "shadow_tree";
                _step++; break;
            case 6: // House close-up
                ZoomOnLocation(CX + 8f, CY + 8f, 50f);
                _step++; break;
            case 7:
                DeferredScreenshot = "shadow_house";
                _step++; break;
            case 8: // Overview of all four
                ZoomOnLocation(CX + 4f, CY + 4f, 30f);
                _step++; break;
            case 9:
                DeferredScreenshot = "shadow_all";
                _step++; break;
            default:
                _complete = true; break;
        }
        _frame++;
    }

    public override bool IsComplete => _complete && DeferredScreenshot == null;

    public override int OnComplete(Simulation sim)
    {
        DebugLog.Log(ScenarioLog, $"=== shadow_pivot complete: {_step} screenshots ===");
        return 0;
    }
}
