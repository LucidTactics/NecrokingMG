using Microsoft.Xna.Framework.Graphics;
using Necroking.Core;
using Necroking.Editor;
using Necroking.GameSystems;
using Necroking.Render;
using Necroking.World;

namespace Necroking.Scenario.Scenarios;

/// <summary>
/// Verifies per-pixel object harmonize: two copies of the same tree sprite are
/// placed side by side — left raw, right with a Harmonize recipe shifting it
/// toward orange. Confirms (numerically) that the baked texture is recolored vs
/// the raw one, and screenshots both for visual inspection. No duplicate asset
/// files are involved — the right tree's texture is generated in memory at load.
/// </summary>
public class HarmonizeObjectScenario : ScenarioBase
{
    public override string Name => "harmonize_object";

    private float _elapsed;
    private bool _shot;
    private bool _done;
    private int _rawIdx = -1;
    private int _harmIdx = -1;

    private const string TreeSprite = "assets/Environment/Trees/OakExports/SwampTree4Alive.png";

    public override void OnInit(Simulation sim)
    {
        DebugLog.Clear(ScenarioLog);
        DebugLog.Log(ScenarioLog, "=== Object Harmonize Test ===");
        sim.GameData.Settings.Weather.Enabled = false;
        BackgroundColor = new Microsoft.Xna.Framework.Color(40, 30, 55); // purple → contrasts both green & orange

        var rawDef = new EnvironmentObjectDef
        {
            Id = "harm_raw", Name = "Raw Tree", Category = "Tree",
            TexturePath = TreeSprite, SpriteWorldHeight = 6f, Scale = 1f, PivotX = 0.5f, PivotY = 1f,
        };
        _rawIdx = sim.EnvironmentSystem.AddDef(rawDef);

        var harmDef = new EnvironmentObjectDef
        {
            Id = "harm_orange", Name = "Harmonized Tree", Category = "Tree",
            TexturePath = TreeSprite, SpriteWorldHeight = 6f, Scale = 1f, PivotX = 0.5f, PivotY = 1f,
            Harmonize = new HarmonizeSettings
            {
                TargetColor = new byte[] { 235, 120, 40, 255 }, // orange
                HueStrength = 1.0f, SatStrength = 0.8f, ValStrength = 0.25f, UseHcl = false,
            },
        };
        _harmIdx = sim.EnvironmentSystem.AddDef(harmDef);

        sim.EnvironmentSystem.AddObject((ushort)_rawIdx, 9f, 13f);
        sim.EnvironmentSystem.AddObject((ushort)_harmIdx, 19f, 13f);

        ZoomOnLocation(14f, 12f, 16f);
        DebugLog.Log(ScenarioLog, $"Placed raw tree (left) + harmonized→orange tree (right), sprite={TreeSprite}");
    }

    public override void OnTick(Simulation sim, float dt)
    {
        _elapsed += dt;
        if (!_shot && _elapsed >= 0.4f)
        {
            ZoomOnLocation(14f, 12f, 16f);
            DeferredScreenshot = "harmonize_object";
            _shot = true;
        }
        if (_shot && DeferredScreenshot == null && _elapsed > 0.6f)
            _done = true;
    }

    public override bool IsComplete => _done;

    public override int OnComplete(Simulation sim)
    {
        var env = sim.EnvironmentSystem;
        var rawTex = env.GetDefTexture(_rawIdx);
        var harmTex = env.GetDefTexture(_harmIdx);
        if (rawTex == null || harmTex == null)
        {
            DebugLog.Log(ScenarioLog, "[FAIL] missing textures");
            return 1;
        }
        if (env.IsUsingPlaceholder(_rawIdx) || env.IsUsingPlaceholder(_harmIdx))
        {
            DebugLog.Log(ScenarioLog, $"[FAIL] sprite failed to load (placeholder in use) — check {TreeSprite}");
            return 1;
        }

        var (rawR, rawG, rawB) = AverageOpaque(rawTex);
        var (harmR, harmG, harmB) = AverageOpaque(harmTex);
        DebugLog.Log(ScenarioLog, $"raw  avg = ({rawR:F0},{rawG:F0},{rawB:F0})");
        DebugLog.Log(ScenarioLog, $"harm avg = ({harmR:F0},{harmG:F0},{harmB:F0})");

        // Orange shift: red-vs-green balance should increase noticeably vs raw.
        float rawRG = rawR - rawG;
        float harmRG = harmR - harmG;
        DebugLog.Log(ScenarioLog, $"R-G balance: raw={rawRG:F1}  harm={harmRG:F1}  (delta={harmRG - rawRG:F1})");

        if (harmRG <= rawRG + 10f)
        {
            DebugLog.Log(ScenarioLog, "[FAIL] harmonized texture did not shift toward orange");
            return 1;
        }

        DebugLog.Log(ScenarioLog, "[PASS] harmonize baked a recolored texture (no duplicate asset files)");
        return 0;
    }

    private static (float r, float g, float b) AverageOpaque(Texture2D tex)
    {
        var px = new Microsoft.Xna.Framework.Color[tex.Width * tex.Height];
        tex.GetData(px);
        double r = 0, g = 0, b = 0; long n = 0;
        foreach (var c in px)
        {
            if (c.A < 32) continue;
            r += c.R; g += c.G; b += c.B; n++;
        }
        if (n == 0) return (0, 0, 0);
        return ((float)(r / n), (float)(g / n), (float)(b / n));
    }
}
