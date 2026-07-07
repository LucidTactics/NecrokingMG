using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Necroking.Core;
using Necroking.GameSystems;

namespace Necroking.Scenario.Scenarios;

/// <summary>
/// Verifies AtomicFile.CreateStream — the stream-based atomic write used by the
/// map editor's SaveMap for the ~55 MB map JSON (write .tmp, rename on committed
/// dispose). Checks the exact usage pattern SaveMap uses (Utf8JsonWriter over the
/// stream), plus the crash-safety contract: disposing without Commit must leave
/// the pre-existing target untouched and clean up the temp file.
/// </summary>
public class AtomicStreamScenario : ScenarioBase
{
    public override string Name => "atomic_stream";

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
        string dir = Path.Combine("log", "atomic_stream");
        Directory.CreateDirectory(dir);
        string target = Path.Combine(dir, "target.json");
        string tmp = target + ".tmp";
        if (File.Exists(target)) File.Delete(target);
        if (File.Exists(tmp)) File.Delete(tmp);

        // 1. Fresh write with Commit — the SaveMap pattern (Utf8JsonWriter over the stream).
        using (var stream = AtomicFile.CreateStream(target))
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteString("version", "one");
            writer.WriteEndObject();
            writer.Flush();
            stream.Commit();
        }
        Check(File.Exists(target) && File.ReadAllText(target).Contains("\"one\""),
            "committed write creates the target");
        Check(!File.Exists(tmp), "temp file cleaned up after commit");

        // 2. Overwrite WITHOUT Commit (simulated crash mid-save) — target must survive.
        using (var stream = AtomicFile.CreateStream(target))
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes("{\"version\":\"partial\"");
            stream.Write(bytes, 0, bytes.Length);
            // no Commit — dispose must discard
        }
        Check(File.ReadAllText(target).Contains("\"one\""),
            "uncommitted write leaves the original target untouched");
        Check(!File.Exists(tmp), "temp file cleaned up after abandoned write");

        // 3. Overwrite WITH Commit — target replaced.
        using (var stream = AtomicFile.CreateStream(target))
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("version", "two");
            writer.WriteEndObject();
            writer.Flush();
            stream.Commit();
        }
        Check(File.ReadAllText(target).Contains("\"two\""), "committed overwrite replaces the target");
        Check(!File.Exists(tmp), "temp file cleaned up after overwrite");

        _done = true;
    }

    public override void OnTick(Simulation sim, float dt) { }

    public override bool IsComplete => _done;

    public override int OnComplete(Simulation sim)
    {
        if (_failures.Count == 0)
        {
            DebugLog.Log(ScenarioLog, "All atomic stream checks passed");
            return 0;
        }
        DebugLog.Log(ScenarioLog, $"{_failures.Count} atomic stream check(s) FAILED");
        return _failures.Count;
    }
}
