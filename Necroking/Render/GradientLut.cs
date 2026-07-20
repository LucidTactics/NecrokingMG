using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Necroking.Data.Registries;

namespace Necroking.Render;

/// <summary>
/// Bakes TemperatureRamp recipes into 256x1 chroma LUT textures for
/// TempGradient.fx (sampler slot 1): stop-list gradient (built-in fire base
/// unless the ramp overrides it) run through the color harmonizer. Memoized by
/// the full recipe; LUTs are tiny (1KB) so the cache lives for the process —
/// no disposal churn on flipbook reloads, an edited recipe just mints a new
/// key. Design: todos/temperature-ramp-design.md.
/// </summary>
public static class GradientLut
{
    private const int Width = 256;
    private static readonly Dictionary<string, Texture2D> _cache = new();

    /// <summary>The built-in base ramp: classic fire chroma (deep red → orange
    /// → yellow → white-hot). Luminance comes from the heat texel, so stops
    /// stay saturated/bright rather than fading to black at the cool end.</summary>
    public static readonly IReadOnlyList<GradientStop> FireStops = new List<GradientStop>
    {
        new() { T = 0.00f, R = 170, G = 40,  B = 20  },
        new() { T = 0.25f, R = 230, G = 90,  B = 30  },
        new() { T = 0.50f, R = 255, G = 140, B = 40  },
        new() { T = 0.75f, R = 255, G = 210, B = 90  },
        new() { T = 1.00f, R = 255, G = 245, B = 225 },
    };

    // Per-ramp-instance fast path: the string-key lookup below allocates, and
    // draw sites call Get every frame per ramped effect. A recipe hash checked
    // against the instance's last result makes the steady-state path
    // allocation-free; edits (hash change) fall through to the exact cache.
    private sealed class InstanceEntry { public int Hash; public Texture2D? Lut; }
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<TemperatureRamp, InstanceEntry> _byInstance = new();

    /// <summary>Get (or bake) the LUT for a ramp recipe. Null when ramp is null
    /// or no device is available.</summary>
    public static Texture2D? Get(GraphicsDevice? device, TemperatureRamp? ramp)
    {
        if (ramp == null || device == null) return null;
        int hash = RecipeHash(ramp.Stops, ramp.Harmonize);
        var entry = _byInstance.GetOrCreateValue(ramp);
        if (entry.Lut is { IsDisposed: false } && entry.Hash == hash) return entry.Lut;
        entry.Lut = Get(device, ramp.Stops, ramp.Harmonize);
        entry.Hash = hash;
        return entry.Lut;
    }

    private static int RecipeHash(IReadOnlyList<GradientStop>? stops, Necroking.Editor.HarmonizeSettings? hz)
    {
        var h = new HashCode();
        if (stops != null)
            foreach (var s in stops) { h.Add(s.T); h.Add(s.R); h.Add(s.G); h.Add(s.B); }
        if (hz != null && hz.HasEffect)
        {
            foreach (var b in hz.TargetColor) h.Add(b);
            h.Add(hz.HueStrength); h.Add(hz.SatStrength); h.Add(hz.ValStrength); h.Add(hz.UseHcl);
        }
        return h.ToHashCode();
    }

    /// <summary>Explicit-parts variant (the editor uses it to preview the
    /// un-harmonized base next to the result).</summary>
    public static Texture2D? Get(GraphicsDevice? device, IReadOnlyList<GradientStop>? stops,
        Necroking.Editor.HarmonizeSettings? harmonize)
    {
        if (device == null) return null;
        var effective = stops is { Count: >= 2 } ? stops : FireStops;
        bool harmonized = harmonize != null && harmonize.HasEffect;

        // Invariant culture — default float ToString uses the current culture,
        // and a comma decimal separator would collide with the ',' delimiters.
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var key = new StringBuilder(64);
        foreach (var s in effective) key.Append(s.T.ToString(inv)).Append(',').Append(s.R).Append(',').Append(s.G).Append(',').Append(s.B).Append(';');
        if (harmonized)
        {
            var h = harmonize!;
            key.Append('H').Append(h.TargetColor[0]).Append(',').Append(h.TargetColor[1]).Append(',')
               .Append(h.TargetColor[2]).Append(',').Append(h.TargetColor[3]).Append(';')
               .Append(h.HueStrength.ToString(inv)).Append(',').Append(h.SatStrength.ToString(inv)).Append(',')
               .Append(h.ValStrength.ToString(inv)).Append(',').Append(h.UseHcl ? 1 : 0);
        }
        string k = key.ToString();
        if (_cache.TryGetValue(k, out var cached)) return cached;

        // Sort a copy by T (defs are shared registry objects — never reorder them)
        var sorted = new List<GradientStop>(effective);
        sorted.Sort((x, y) => x.T.CompareTo(y.T));

        var pixels = new Color[Width];
        for (int i = 0; i < Width; i++)
        {
            float t = i / (float)(Width - 1);
            int seg = 0;
            while (seg < sorted.Count - 2 && t > sorted[seg + 1].T) seg++;
            var lo = sorted[seg];
            var hi = sorted[seg + 1];
            float span = hi.T - lo.T;
            // Clamp covers t outside the authored range (end-stop color)
            float f = span > 1e-5f ? Math.Clamp((t - lo.T) / span, 0f, 1f) : (t < lo.T ? 0f : 1f);
            pixels[i] = new Color(
                (byte)(lo.R + (hi.R - lo.R) * f),
                (byte)(lo.G + (hi.G - lo.G) * f),
                (byte)(lo.B + (hi.B - lo.B) * f));
        }

        // Recolor the ramp itself — 256 px, effectively free, and LDR-safe:
        // the HDR range lives in the heat texels, never in the LUT.
        if (harmonized)
            Necroking.Editor.ColorHarmonizer.TransformPixels(pixels, Width, 1, harmonize!);

        var tex = new Texture2D(device, Width, 1);
        tex.SetData(pixels);
        _cache[k] = tex;
        return tex;
    }
}
