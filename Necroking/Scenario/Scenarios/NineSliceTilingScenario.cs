using Microsoft.Xna.Framework;
using Necroking.Core;
using Necroking.GameSystems;

namespace Necroking.Scenario.Scenarios;

/// <summary>
/// Visual proof that nine-slice edge tiling works. Draws the grim_cloth_frame
/// nine-slice (tileEdges=true, source ClothUpgradeFrame2 = 1028x1028, borders 51,
/// middle strip 926px) at several sizes:
///   - small 300x300       — corners fixed, edges show a native-res crop
///   - wide  1180x160      — wider than the 926 middle, so top/bottom edges REPEAT
///   - tall  160x460       — left/right edges (taller cases would repeat)
/// and, for contrast, the baked Grim_WindowBorder image stretched to the same wide
/// box — which scales the whole pattern (what the skill book currently does).
/// </summary>
public class NineSliceTilingScenario : ScenarioBase
{
    public override string Name => "nineslice_tiling";
    public override bool WantsWidgetRenderer => true;

    private float _t;
    private int _phase;
    private bool _complete;

    public override void OnInit(Simulation sim)
    {
        DebugLog.Clear(ScenarioLog);
        DebugLog.Log(ScenarioLog, "=== Nine-Slice Tiling Scenario ===");
        BackgroundColor = new Color(28, 30, 38);
        BloomOverride = new Data.Registries.BloomSettings { Enabled = false };

        CustomUIDraw = (batch, sw, sh) =>
        {
            var r = WidgetRenderer;
            if (r == null) return;

            // Nine-slice (tiles its edges).
            r.DrawNineSlice("grim_cloth_frame", new Rectangle(30, 30, 300, 300));
            r.DrawNineSlice("grim_cloth_frame", new Rectangle(30, 360, 1180, 160));
            r.DrawNineSlice("grim_cloth_frame", new Rectangle(30, 540, 160, 460));

            // Baked image stretched to the same wide box (corners scale too) — what
            // the skill book's Grim_WindowBorder element does today.
            r.DrawIcon("assets/UI/Imported/baked_ClothUpgradeFrame2-DarkBorder1_706x1080.png",
                230, 540, 980, 160);
        };
    }

    public override void OnTick(Simulation sim, float dt)
    {
        _t += dt;
        if (_phase == 0 && _t > 0.5f)
        {
            DebugLog.Log(ScenarioLog, "Screenshot: nineslice_tiling (top=ns sizes, bottom-right=baked stretched)");
            DeferredScreenshot = "nineslice_tiling";
            _phase = 1;
        }
        else if (_phase == 1 && DeferredScreenshot == null)
        {
            _complete = true;
        }
    }

    public override bool IsComplete => _complete;
    public override int OnComplete(Simulation sim) => 0;
}
