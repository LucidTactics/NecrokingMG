using System.IO;
using System.Text.Json;
using Necroking.Core;
using Necroking.Data;
using Necroking.GameSystems;
using Necroking.World;

namespace Necroking.Scenario.Scenarios;

/// <summary>
/// Regression test for the env-def serializer (MapData.EnvDefJson): the real
/// data/env_defs.json must load, re-save, reload and re-save byte-identically
/// (save-load-save fixpoint), and the category-dependent randomFlip default
/// must apply only when the field is absent (legacy embedded defs). Guards the
/// same drift class the old split ParseEnvDef/WriteJson pair was prone to.
/// Pure data test — completes on the first tick.
/// </summary>
public class EnvDefsRoundtripScenario : ScenarioBase
{
    public override string Name => "env_defs_roundtrip";

    private readonly System.Collections.Generic.List<string> _failures = new();
    private bool _done;

    private void Check(bool cond, string what)
    {
        if (cond)
            DebugLog.Log(ScenarioLog, $"  ok: {what}");
        else
        {
            _failures.Add(what);
            DebugLog.Log(ScenarioLog, $"  FAIL: {what}");
        }
    }

    public override void OnInit(Simulation sim)
    {
        string outDir = Path.Combine("log", "env_defs_roundtrip");
        Directory.CreateDirectory(outDir);
        string real = GamePaths.Resolve("data/env_defs.json");

        // ── Real file: load → save → load → save fixpoint ──
        var envA = new EnvironmentSystem();
        Check(MapData.LoadEnvDefs(real, envA), "real env_defs.json loads");
        DebugLog.Log(ScenarioLog, $"  {envA.DefCount} env defs loaded");

        string c1 = Path.Combine(outDir, "env_defs_1.json");
        string c2 = Path.Combine(outDir, "env_defs_2.json");
        Check(MapData.SaveEnvDefs(c1, envA), "re-saved copy");

        var envB = new EnvironmentSystem();
        Check(MapData.LoadEnvDefs(c1, envB) && envB.DefCount == envA.DefCount,
            $"copy reloads with same def count ({envB.DefCount}/{envA.DefCount})");
        Check(MapData.SaveEnvDefs(c2, envB), "second save");
        Check(File.ReadAllText(c1) == File.ReadAllText(c2), "save-load-save fixpoint");

        // ── randomFlip category default: only when the field is ABSENT ──
        var noFlipField = JsonSerializer.Deserialize<EnvironmentObjectDef>(
            "{\"id\":\"t1\",\"category\":\"Tree\"}", MapData.EnvDefJson);
        var noFlipBuilding = JsonSerializer.Deserialize<EnvironmentObjectDef>(
            "{\"id\":\"b1\",\"category\":\"Building\"}", MapData.EnvDefJson);
        var explicitFalseTree = JsonSerializer.Deserialize<EnvironmentObjectDef>(
            "{\"id\":\"t2\",\"category\":\"Tree\",\"randomFlip\":false}", MapData.EnvDefJson);
        var explicitTrueBuilding = JsonSerializer.Deserialize<EnvironmentObjectDef>(
            "{\"id\":\"b2\",\"category\":\"Building\",\"randomFlip\":true}", MapData.EnvDefJson);
        Check(noFlipField != null && noFlipField.RandomFlip, "absent randomFlip on Tree defaults true");
        Check(noFlipBuilding != null && !noFlipBuilding.RandomFlip, "absent randomFlip on Building defaults false");
        Check(explicitFalseTree != null && !explicitFalseTree.RandomFlip, "explicit randomFlip=false on Tree honored");
        Check(explicitTrueBuilding != null && explicitTrueBuilding.RandomFlip, "explicit randomFlip=true on Building honored");

        // ── Harmonize + HdrColor survive a synthetic round-trip ──
        var syn = new EnvironmentObjectDef
        {
            Id = "syn", Category = "Misc",
            TintColor = new HdrColor(10, 20, 30, 200, 2.5f),
            Harmonize = new Necroking.Editor.HarmonizeSettings
            {
                TargetColor = new byte[] { 90, 120, 60, 255 },
                HueStrength = 0.7f, SatStrength = 0.4f, ValStrength = 0.1f, UseHcl = true,
            },
        };
        string json = JsonSerializer.Serialize(syn, MapData.EnvDefJson);
        var synBack = JsonSerializer.Deserialize<EnvironmentObjectDef>(json, MapData.EnvDefJson);
        Check(synBack != null
            && synBack.TintColor.R == 10 && synBack.TintColor.A == 200 && synBack.TintColor.Intensity == 2.5f,
            "HdrColor tint round-trips");
        Check(synBack?.Harmonize != null && synBack.Harmonize.HasEffect
            && synBack.Harmonize.TargetColor[1] == 120 && synBack.Harmonize.UseHcl,
            "harmonize settings round-trip");

        _done = true;
    }

    public override void OnTick(Simulation sim, float dt) { }

    public override bool IsComplete => _done;

    public override int OnComplete(Simulation sim)
    {
        if (_failures.Count == 0)
        {
            DebugLog.Log(ScenarioLog, "All env-defs round-trip checks passed");
            return 0;
        }
        DebugLog.Log(ScenarioLog, $"{_failures.Count} env-defs round-trip check(s) FAILED");
        return _failures.Count;
    }
}
