using System;
using System.Collections.Generic;
using System.IO;
using Necroking.Core;
using Necroking.Editor;
using Necroking.GameSystems;

namespace Necroking.Scenario.Scenarios;

/// <summary>
/// Regression test for the shared UI-defs I/O (UIDefsIO): the current
/// data/ui/{nine_slices,elements,widgets}.json must load, re-save, reload and
/// re-save to byte-identical output (save-load-save fixpoint — everything the
/// writer emits is read back losslessly). Also asserts the nine-slice
/// harmonize field survives a round-trip: historically the runtime read it
/// but the editor's saver silently stripped it.
/// Pure data test — completes on the first tick.
/// </summary>
public class UIDefsRoundtripScenario : ScenarioBase
{
    public override string Name => "ui_defs_roundtrip";

    private readonly List<string> _failures = new();
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
        string outDir = Path.Combine("log", "ui_defs_roundtrip");
        Directory.CreateDirectory(outDir);
        string srcDir = GamePaths.Resolve("data/ui");

        // ── Real files: load → save → load → save, fixpoint per file ──
        RoundtripNineSlices(Path.Combine(srcDir, "nine_slices.json"), outDir);
        RoundtripElements(Path.Combine(srcDir, "elements.json"), outDir);
        RoundtripWidgets(Path.Combine(srcDir, "widgets.json"), outDir);

        // ── Synthetic: nine-slice harmonize must survive (the historical drift) ──
        var withHarm = new List<UIEditorNineSliceDef>
        {
            new()
            {
                Id = "harm_test", Texture = "assets/UI/test.png",
                BorderLeft = 4, BorderRight = 5, BorderTop = 6, BorderBottom = 7, TileEdges = true,
                Harmonize = new HarmonizeSettings
                {
                    TargetColor = new byte[] { 120, 60, 200, 255 },
                    HueStrength = 0.8f, SatStrength = 0.5f, ValStrength = 0.25f, UseHcl = true,
                },
            },
        };
        string hp = Path.Combine(outDir, "syn_nineslices.json");
        Check(UIDefsIO.SaveNineSlices(hp, withHarm), "synthetic nine-slice with harmonize saved");
        var reloaded = new List<UIEditorNineSliceDef>();
        Check(UIDefsIO.LoadNineSlices(hp, reloaded) == UIDefsLoadResult.Ok, "synthetic nine-slice reloads");
        var h = reloaded.Count == 1 ? reloaded[0].Harmonize : null;
        Check(h != null && h.HasEffect
            && h.TargetColor[0] == 120 && h.TargetColor[2] == 200
            && Math.Abs(h.HueStrength - 0.8f) < 1e-5f && h.UseHcl,
            "nine-slice harmonize survives save/load (was editor-stripped before)");

        _done = true;
    }

    private void RoundtripNineSlices(string realPath, string outDir)
        => Roundtrip("nine_slices", realPath, outDir,
            load: path => { var l = new List<UIEditorNineSliceDef>(); return (UIDefsIO.LoadNineSlices(path, l), l.Count, (object)l); },
            save: (path, l) => UIDefsIO.SaveNineSlices(path, (List<UIEditorNineSliceDef>)l));

    private void RoundtripElements(string realPath, string outDir)
        => Roundtrip("elements", realPath, outDir,
            load: path => { var l = new List<UIEditorElementDef>(); return (UIDefsIO.LoadElements(path, l), l.Count, (object)l); },
            save: (path, l) => UIDefsIO.SaveElements(path, (List<UIEditorElementDef>)l));

    private void RoundtripWidgets(string realPath, string outDir)
        => Roundtrip("widgets", realPath, outDir,
            load: path => { var l = new List<UIEditorWidgetDef>(); return (UIDefsIO.LoadWidgets(path, l), l.Count, (object)l); },
            save: (path, l) => UIDefsIO.SaveWidgets(path, (List<UIEditorWidgetDef>)l));

    private void Roundtrip(string label, string realPath, string outDir,
        Func<string, (UIDefsLoadResult Result, int Count, object List)> load,
        Func<string, object, bool> save)
    {
        if (!File.Exists(realPath))
        {
            DebugLog.Log(ScenarioLog, $"  {label}: no file on disk, skipped");
            return;
        }

        var (res, count, list) = load(realPath);
        Check(res == UIDefsLoadResult.Ok, $"{label}: real file loads");
        if (res != UIDefsLoadResult.Ok) return;
        DebugLog.Log(ScenarioLog, $"  {label}: {count} defs loaded from real file");

        string copy1 = Path.Combine(outDir, label + "_1.json");
        string copy2 = Path.Combine(outDir, label + "_2.json");
        Check(save(copy1, list), $"{label}: re-saved copy");

        var (res2, count2, list2) = load(copy1);
        Check(res2 == UIDefsLoadResult.Ok && count2 == count, $"{label}: copy reloads with same def count ({count2}/{count})");
        if (res2 != UIDefsLoadResult.Ok) return;

        Check(save(copy2, list2), $"{label}: second save");
        Check(File.ReadAllText(copy1) == File.ReadAllText(copy2), $"{label}: save-load-save fixpoint");
    }

    public override void OnTick(Simulation sim, float dt) { }

    public override bool IsComplete => _done;

    public override int OnComplete(Simulation sim)
    {
        if (_failures.Count == 0)
        {
            DebugLog.Log(ScenarioLog, "All UI-defs round-trip checks passed");
            return 0;
        }
        DebugLog.Log(ScenarioLog, $"{_failures.Count} UI-defs round-trip check(s) FAILED");
        return _failures.Count;
    }
}
