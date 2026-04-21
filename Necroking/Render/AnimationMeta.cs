using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Necroking.Render;

public class AnimYawMeta
{
    public float EffectSpawnX;
    public float EffectSpawnY = 0.6f;
    public float EffectSpawnZ;
    public List<int> FrameDurationsMs = new();
    public List<int> FrameTicks = new();
}

public class AnimationMeta
{
    public int EffectTimeMs;
    public int LoopStartIndex;
    public int LoopEndIndex;
    public Dictionary<int, AnimYawMeta> YawData = new();

    public int TotalDurationMs()
    {
        foreach (var (_, ym) in YawData)
        {
            int total = 0;
            foreach (var ms in ym.FrameDurationsMs) total += ms;
            if (total > 0) return total;
        }
        return 0;
    }

    public void GetEffectSpawnPos(int yaw, out float x, out float y, out float z)
    {
        if (YawData.TryGetValue(yaw, out var ym))
        {
            x = ym.EffectSpawnX; y = ym.EffectSpawnY; z = ym.EffectSpawnZ;
            return;
        }
        x = 0f; y = 0.6f; z = 0f;
    }
}

public class AnimTimingOverride
{
    public List<int> FrameDurationsMs = new();
    public int EffectTimeMs = -1;
}

// Map from "UnitName.Category" → AnimationMeta
public static class AnimMetaLoader
{
    public static string MetaKey(string unitName, string category) => $"{unitName}.{category}";

    // Categories that encode a "moment" event (hit connects, projectile spawns, jump
    // takeoff fires). Missing/zero effect_time on these causes silent timing bugs:
    // hits resolve at 50%-through fallback, pounces wait out the full safety timeout.
    // We log on load so regressions are caught immediately instead of in-game.
    private static readonly HashSet<string> CategoriesRequiringEffectTime = new()
    {
        "Attack1", "Attack2", "Attack3", "AttackBite", "AttackBody", "AttackKick",
        "Ranged1", "Spell1", "Special1",
        "JumpTakeoff", "JumpLand", "JumpAttackHit",
    };

    public static bool Load(string path, Dictionary<string, AnimationMeta> output)
    {
        if (!File.Exists(path)) return false;

        foreach (var line in File.ReadLines(path))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || !trimmed.StartsWith('{')) continue;

            try
            {
                using var doc = JsonDocument.Parse(trimmed);
                var root = doc.RootElement;

                string unit = root.GetProperty("unit").GetString() ?? "";
                string category = root.GetProperty("category").GetString() ?? "";
                // Use GetDouble→int cast for all numeric fields to handle both int and float JSON values
                int yaw = root.TryGetProperty("yaw", out var y) ? (int)Math.Round(y.GetDouble()) : 0;
                string key = MetaKey(unit, category);

                if (!output.TryGetValue(key, out var meta))
                {
                    meta = new AnimationMeta();
                    output[key] = meta;
                }

                if (root.TryGetProperty("effect_time_ms", out var et))
                    meta.EffectTimeMs = (int)Math.Round(et.GetDouble());
                if (root.TryGetProperty("loop_start", out var ls))
                    meta.LoopStartIndex = (int)Math.Round(ls.GetDouble());
                if (root.TryGetProperty("loop_end", out var le))
                    meta.LoopEndIndex = (int)Math.Round(le.GetDouble());

                var ym = new AnimYawMeta();
                if (root.TryGetProperty("effect_spawn_x", out var esx)) ym.EffectSpawnX = esx.GetSingle();
                if (root.TryGetProperty("effect_spawn_y", out var esy)) ym.EffectSpawnY = esy.GetSingle();
                if (root.TryGetProperty("effect_spawn_z", out var esz)) ym.EffectSpawnZ = esz.GetSingle();

                if (root.TryGetProperty("time_ms", out var tms) && tms.ValueKind == JsonValueKind.Array)
                    foreach (var v in tms.EnumerateArray()) ym.FrameDurationsMs.Add((int)Math.Round(v.GetDouble()));

                if (root.TryGetProperty("frame_ticks", out var ft) && ft.ValueKind == JsonValueKind.Array)
                    foreach (var v in ft.EnumerateArray()) ym.FrameTicks.Add((int)Math.Round(v.GetDouble()));

                meta.YawData[yaw] = ym;
            }
            catch (Exception ex) { Core.DebugLog.Log("error", $"Failed to parse animation meta line in {path}: {ex.Message}"); }
        }

        // Post-load validation: warn about categories where effect_time is critical but missing.
        foreach (var (key, meta) in output)
        {
            int dotIdx = key.IndexOf('.');
            if (dotIdx < 0) continue;
            string category = key.Substring(dotIdx + 1);
            if (!CategoriesRequiringEffectTime.Contains(category)) continue;
            if (meta.EffectTimeMs <= 0)
            {
                Core.DebugLog.Log("asset",
                    $"[AnimMeta] {key} has no effect_time_ms — hits/spawns/jump transitions " +
                    $"will fall back to 50%-of-duration heuristic. Author should add effect_time_ms.");
            }
        }

        return true;
    }
}
