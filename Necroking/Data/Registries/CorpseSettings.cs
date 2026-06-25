using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Necroking.Data.Registries;

/// <summary>One entry in <see cref="CorpseSettings.Pivots"/> — the normalized
/// (0..1) pivot point for the body-bag sprite at one authored angle. The pivot
/// is what gets pinned to the carrier's hand (weapon hilt) when the bag is
/// being carried; tuning it shifts where the bag visually sits relative to the
/// grip without re-exporting art.</summary>
public class CorpseAnglePivot
{
    [JsonPropertyName("angle")] public int Angle { get; set; }
    [JsonPropertyName("x")] public float X { get; set; } = 0.5f;
    [JsonPropertyName("y")] public float Y { get; set; } = 0.15f;
}

/// <summary>Corpse-wide tuning data loaded from data/corpse.json. Today it
/// only holds per-angle pivots for the body-bag sprite; new corpse fields
/// (drop scale, table-snap offsets, etc.) belong here as they're added.</summary>
public class CorpseSettings
{
    [JsonPropertyName("pivots")] public List<CorpseAnglePivot> Pivots { get; set; } = new();

    /// <summary>Lookup the override pivot for a given authored sprite angle.
    /// Returns null when the file doesn't include this angle, signalling that
    /// the caller should fall back to whatever the spritemeta declared.</summary>
    public (float X, float Y)? GetPivot(int angle)
    {
        for (int i = 0; i < Pivots.Count; i++)
            if (Pivots[i].Angle == angle) return (Pivots[i].X, Pivots[i].Y);
        return null;
    }

    /// <summary>Set or insert the pivot for a given angle. Used by the editor.</summary>
    public void SetPivot(int angle, float x, float y)
    {
        for (int i = 0; i < Pivots.Count; i++)
        {
            if (Pivots[i].Angle == angle) { Pivots[i].X = x; Pivots[i].Y = y; return; }
        }
        Pivots.Add(new CorpseAnglePivot { Angle = angle, X = x, Y = y });
    }

    /// <summary>Push every authored pivot onto the matching BodyBag/Icon atlas
    /// frame so the renderer (which reads <see cref="Render.SpriteFrame.PivotX"/>)
    /// picks up the override. Called once after the Corpses atlas finishes
    /// loading and again whenever the editor mutates a value, so changes take
    /// effect without a restart. Frames at angles not listed in <see cref="Pivots"/>
    /// are left at their spritemeta-default — that's the documented fallback.</summary>
    public void ApplyToAtlas(Render.SpriteAtlas? corpsesAtlas)
    {
        var iconAnim = corpsesAtlas?.GetUnit("BodyBag")?.GetAnim("Icon");
        if (iconAnim == null) return;
        foreach (var (angle, kfs) in iconAnim.AngleFrames)
        {
            var ov = GetPivot(angle);
            if (!ov.HasValue) continue;
            for (int i = 0; i < kfs.Count; i++)
            {
                var kf = kfs[i];
                kf.Frame.PivotX = ov.Value.X;
                kf.Frame.PivotY = ov.Value.Y;
                kfs[i] = kf;
            }
        }
    }

    public bool Load(string path)
    {
        if (!Core.JsonFile.Load<CorpseSettings>(path, null, out var loaded)) return false;
        if (loaded == null) return false;
        Pivots = loaded.Pivots ?? new List<CorpseAnglePivot>();
        return true;
    }

    public bool Save(string path)
    {
        return Core.JsonFile.Save(path, this, Core.JsonDefaults.Indented);
    }
}
