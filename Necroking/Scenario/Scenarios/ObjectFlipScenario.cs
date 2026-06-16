using Necroking.Core;
using Necroking.GameSystems;
using Necroking.World;

namespace Necroking.Scenario.Scenarios;

/// <summary>
/// Verifies random horizontal flip for placed objects:
///  - a RandomFlip=true tree def produces a mix of flipped/unflipped instances
///    (deterministic per seed, roughly 50/50),
///  - a RandomFlip=false def never flips,
///  - category defaults resolve sensibly.
/// Screenshots a row of trees so the flip variety (and that shadows still fall
/// the same direction) can be eyeballed.
/// </summary>
public class ObjectFlipScenario : ScenarioBase
{
    public override string Name => "object_flip";

    private float _elapsed;
    private bool _shot;
    private bool _done;
    private int _treeIdx = -1;
    private int _bldgIdx = -1;
    private const int Count = 16;

    private const string TreeSprite = "assets/Environment/Trees/OakExports/SwampTree4Alive.png";

    public override void OnInit(Simulation sim)
    {
        DebugLog.Clear(ScenarioLog);
        DebugLog.Log(ScenarioLog, "=== Object Random-Flip Test ===");
        sim.GameData.Settings.Weather.Enabled = false;
        BackgroundColor = new Microsoft.Xna.Framework.Color(45, 40, 55);

        var env = sim.EnvironmentSystem;

        _treeIdx = env.AddDef(new EnvironmentObjectDef
        {
            Id = "flip_tree", Name = "Flip Tree", Category = "Tree",
            TexturePath = TreeSprite, SpriteWorldHeight = 6f, Scale = 1f,
            PivotX = 0.5f, PivotY = 1f, ShadowType = 0,
            RandomFlip = true,
        });
        _bldgIdx = env.AddDef(new EnvironmentObjectDef
        {
            Id = "flip_bldg", Name = "Flip Bldg", Category = "Building",
            TexturePath = TreeSprite, SpriteWorldHeight = 6f, Scale = 1f,
            PivotX = 0.5f, PivotY = 1f, ShadowType = 0,
            RandomFlip = false,
        });

        // Deterministic spread of seeds so the run is repeatable.
        for (int i = 0; i < Count; i++)
        {
            float seed = (i + 0.5f) / Count;
            env.AddObject((ushort)_treeIdx, 5f + i * 1.6f, 12f, 1f, seed);
            env.AddObject((ushort)_bldgIdx, 5f + i * 1.6f, 18f, 1f, seed);
        }

        ZoomOnLocation(17f, 14f, 14f);
        DebugLog.Log(ScenarioLog, $"Placed {Count} flip trees + {Count} no-flip buildings");
    }

    public override void OnTick(Simulation sim, float dt)
    {
        _elapsed += dt;
        if (!_shot && _elapsed >= 0.4f) { DeferredScreenshot = "object_flip"; _shot = true; }
        if (_shot && DeferredScreenshot == null && _elapsed > 0.6f) _done = true;
    }

    public override bool IsComplete => _done;

    public override int OnComplete(Simulation sim)
    {
        var env = sim.EnvironmentSystem;

        int treeFlips = 0, bldgFlips = 0;
        for (int i = 0; i < env.ObjectCount; i++)
        {
            bool flipped = env.ShouldFlipObject(i);
            int defIdx = env.Objects[i].DefIndex;
            if (defIdx == _treeIdx && flipped) treeFlips++;
            if (defIdx == _bldgIdx && flipped) bldgFlips++;
        }
        DebugLog.Log(ScenarioLog, $"tree flips = {treeFlips}/{Count}  (expect a mix)");
        DebugLog.Log(ScenarioLog, $"building flips = {bldgFlips}/{Count}  (expect 0)");

        // Category defaults
        bool dTree = EnvironmentObjectDef.DefaultRandomFlipForCategory("Tree");
        bool dBush = EnvironmentObjectDef.DefaultRandomFlipForCategory("Bush");
        bool dBldg = EnvironmentObjectDef.DefaultRandomFlipForCategory("Building");
        bool dWall = EnvironmentObjectDef.DefaultRandomFlipForCategory("Wall");
        DebugLog.Log(ScenarioLog, $"category defaults: Tree={dTree} Bush={dBush} Building={dBldg} Wall={dWall}");

        if (bldgFlips != 0)
        {
            DebugLog.Log(ScenarioLog, "[FAIL] RandomFlip=false def was flipped");
            return 1;
        }
        if (treeFlips == 0 || treeFlips == Count)
        {
            DebugLog.Log(ScenarioLog, "[FAIL] flip not varied (expected a mix of flipped + unflipped)");
            return 1;
        }
        if (!dTree || !dBush || dBldg || dWall)
        {
            DebugLog.Log(ScenarioLog, "[FAIL] category defaults wrong");
            return 1;
        }

        DebugLog.Log(ScenarioLog, "[PASS] random flip varied, disabled defs never flip, defaults sane");
        return 0;
    }
}
