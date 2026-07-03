using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework.Graphics;
using Necroking.Core;
// The project has its own Necroking.Render.Effect (game effects), so the shader
// type must be aliased explicitly.
using XnaEffect = Microsoft.Xna.Framework.Graphics.Effect;

namespace Necroking.Render;

/// <summary>
/// Complete SpriteBatch state for one Begin(): shader + blend + sampler +
/// depth-stencil (+ sampler states for extra texture slots). Immutable, created
/// once at load, shared by reference — the pipeline batches by ReferenceEquals,
/// so two structurally identical materials are still two separate batches.
/// Create via <see cref="Materials.Register"/> so every material gets a stable
/// Id for sort-key packing.
/// </summary>
public sealed class Material
{
    public readonly string Name;
    public readonly ushort Id;
    public readonly XnaEffect? Effect;          // null → SpriteBatch default shader
    public readonly BlendState Blend;
    public readonly SamplerState Sampler;
    public readonly DepthStencilState DepthStencil;
    public readonly RasterizerState? Rasterizer; // null → SpriteBatch default (CullCounterClockwise)

    /// <summary>Usually Deferred (ordering is the sort key's job). Immediate is
    /// the sanctioned exception for a RUN of same-effect draws with per-draw
    /// uniforms (magic glyphs): params set between Draw calls apply per draw,
    /// one batch instead of a Begin/End pair per element.</summary>
    public readonly SpriteSortMode SortMode;

    /// <summary>Sampler states for texture slots >= 1, applied at every batch
    /// open. Slots >= 1 otherwise inherit whatever the last pass left in the
    /// device — the recurring bug class that dissolve/bloom hand-fixed with
    /// ad-hoc <c>device.SamplerStates[1] = ...</c> lines.</summary>
    public readonly (int Slot, SamplerState Sampler)[] ExtraSamplerSlots;

    /// <summary>True for materials whose effect needs uniforms set per draw
    /// (wading, dissolve): the executor opens a dedicated batch per item and
    /// never merges consecutive items of this material.</summary>
    public readonly bool RequiresPerDrawParams;

    internal Material(string name, ushort id, XnaEffect? effect, BlendState blend,
        SamplerState sampler, DepthStencilState depthStencil, RasterizerState? rasterizer,
        SpriteSortMode sortMode, (int, SamplerState)[]? extraSamplerSlots,
        bool requiresPerDrawParams)
    {
        Name = name;
        Id = id;
        Effect = effect;
        Blend = blend;
        Sampler = sampler;
        DepthStencil = depthStencil;
        Rasterizer = rasterizer;
        SortMode = sortMode;
        ExtraSamplerSlots = extraSamplerSlots ?? Array.Empty<(int, SamplerState)>();
        RequiresPerDrawParams = requiresPerDrawParams;
    }

    /// <summary>Open a SpriteBatch with this material's full state.</summary>
    public void Begin(SpriteBatch batch)
    {
        batch.Begin(SortMode, Blend, Sampler, DepthStencil, Rasterizer, Effect);
        foreach (var (slot, sampler) in ExtraSamplerSlots)
            batch.GraphicsDevice.SamplerStates[slot] = sampler;
    }
}

/// <summary>
/// The canonical material registry — the single place pass state is defined
/// (grown out of EffectBatch's SceneBlend/HudBlend constants, which now
/// delegate here). Parameterless materials are created eagerly; effect-backed
/// ones are created in <see cref="InitEffectMaterials"/> once shaders load and
/// stay null when their shader failed to load (call sites fall back exactly
/// like the raw effect fields do today).
/// </summary>
public static class Materials
{
    private static readonly List<Material> All = new();

    public static Material Register(string name, XnaEffect? effect, BlendState blend,
        SamplerState sampler, DepthStencilState? depthStencil = null,
        RasterizerState? rasterizer = null, SpriteSortMode sortMode = SpriteSortMode.Deferred,
        (int, SamplerState)[]? extraSamplerSlots = null, bool perDrawParams = false)
    {
        var m = new Material(name, (ushort)All.Count, effect, blend, sampler,
            depthStencil ?? DepthStencilState.None, rasterizer, sortMode,
            extraSamplerSlots, perDrawParams);
        All.Add(m);
        return m;
    }

    public static Material Get(ushort id) => All[id];
    public static int Count => All.Count;

    // --- Canonical pass states (no shader needed, created eagerly) ---

    /// <summary>Scene pass: premultiplied-alpha sprites, linear filtering.</summary>
    public static readonly Material Scene =
        Register("Scene", null, BlendState.AlphaBlend, SamplerState.LinearClamp);

    /// <summary>HUD pass: premultiplied-alpha UI, point filtering (crisp pixel UI).</summary>
    public static readonly Material Hud =
        Register("Hud", null, BlendState.AlphaBlend, SamplerState.PointClamp);

    /// <summary>Plain additive shapes (energy columns, debug shapes) — no shader.</summary>
    public static readonly Material AdditiveShapes =
        Register("AdditiveShapes", null, BlendState.Additive, SamplerState.LinearClamp);

    /// <summary>Ground-fog wisps: alpha-blended, DEPTH-TESTED (read-only)
    /// against the unit silhouettes stamped by the fog occluder pass — a wisp
    /// passes in front of feet, fails behind bodies, per pixel. Items carry
    /// LayerDepth from GameRenderer.FogDepthForY.</summary>
    public static readonly Material FogWisp =
        Register("FogWisp", null, BlendState.AlphaBlend, SamplerState.LinearClamp,
            DepthStencilState.DepthRead, RasterizerState.CullNone);

    // --- Effect-backed materials (null until InitEffectMaterials; stay null if
    //     their shader failed to load) ---

    /// <summary>Waterline/fog-line sprite cut (Wading.fx). Per-draw uniforms.</summary>
    public static Material? Wading { get; private set; }

    /// <summary>Corruption dissolve for env objects (DissolveTree.fx). Per-draw
    /// uniforms + live texture on slot 1.</summary>
    public static Material? DissolveTree { get; private set; }

    /// <summary>HDR sprites, alpha-blended sub-pass (HdrSprite.fx clone, AlphaMode=1).</summary>
    public static Material? HdrAlpha { get; private set; }

    /// <summary>HDR sprites, additive sub-pass (HdrSprite.fx clone, AlphaMode=0).</summary>
    public static Material? HdrAdditive { get; private set; }

    /// <summary>Depth-only unit silhouette stamp (DepthCutout.fx): color write
    /// off, depth write on — the occluder pass for depth-tested fog.</summary>
    public static Material? DepthStamp { get; private set; }

    /// <summary>SDF reanimation morph (MorphSDF.fx). Per-draw uniforms + pose
    /// texture on slot 1, SDF map on slot 2.</summary>
    public static Material? MorphSdf { get; private set; }

    /// <summary>Flat-color sprite outline (OutlineFlat.fx) — straight-alpha
    /// output, NonPremultiplied blend variant. Per-draw color uniform.</summary>
    public static Material? OutlineAlpha { get; private set; }

    /// <summary>Flat-color sprite outline, additive blend variant.</summary>
    public static Material? OutlineAdditive { get; private set; }

    // Write depth only, no color — for the depth-sorted-fog occluder stamp.
    private static readonly BlendState DepthOnlyBlend = new()
    {
        ColorWriteChannels = ColorWriteChannels.None
    };

    /// <summary>Create the effect-backed materials. Call once from LoadContent
    /// after shader loading; any null effect leaves its material null.</summary>
    public static void InitEffectMaterials(XnaEffect? wading, XnaEffect? dissolveTree,
        XnaEffect? hdrSprite, XnaEffect? depthCutout, XnaEffect? morphSdf,
        XnaEffect? outlineFlat)
    {
        if (morphSdf != null)
            MorphSdf = Register("MorphSdf", morphSdf, BlendState.AlphaBlend,
                SamplerState.LinearClamp,
                extraSamplerSlots: new[] { (1, SamplerState.LinearClamp), (2, SamplerState.LinearClamp) },
                perDrawParams: true);

        if (outlineFlat != null)
        {
            // OutlineFlat outputs STRAIGHT alpha — NonPremultiplied/Additive only
            // (see the .fx header). One shared effect instance, two blend variants;
            // the per-call OutlineColor uniform makes both per-draw materials.
            OutlineAlpha = Register("OutlineAlpha", outlineFlat, BlendState.NonPremultiplied,
                SamplerState.LinearClamp, perDrawParams: true);
            OutlineAdditive = Register("OutlineAdditive", outlineFlat, BlendState.Additive,
                SamplerState.LinearClamp, perDrawParams: true);
        }
        if (wading != null)
            Wading = Register("Wading", wading, BlendState.AlphaBlend,
                SamplerState.LinearClamp, perDrawParams: true);

        if (dissolveTree != null)
            DissolveTree = Register("DissolveTree", dissolveTree, BlendState.AlphaBlend,
                SamplerState.LinearClamp,
                extraSamplerSlots: new[] { (1, SamplerState.LinearClamp) },
                perDrawParams: true);

        if (hdrSprite != null)
        {
            // A uniform that selects a sub-pass IS a material distinction: two
            // clones with AlphaMode baked at load replace the per-frame
            // Parameters["AlphaMode"] flip between the alpha and additive passes.
            var alphaFx = hdrSprite.Clone();
            alphaFx.Parameters["MaxIntensity"]?.SetValue(HdrColor.MaxHdrIntensity);
            alphaFx.Parameters["AlphaMode"]?.SetValue(1f);
            HdrAlpha = Register("HdrAlpha", alphaFx, BlendState.AlphaBlend, SamplerState.LinearClamp);

            var addFx = hdrSprite.Clone();
            addFx.Parameters["MaxIntensity"]?.SetValue(HdrColor.MaxHdrIntensity);
            addFx.Parameters["AlphaMode"]?.SetValue(0f);
            HdrAdditive = Register("HdrAdditive", addFx, BlendState.Additive, SamplerState.LinearClamp);
        }

        if (depthCutout != null)
            DepthStamp = Register("DepthStamp", depthCutout, DepthOnlyBlend,
                SamplerState.LinearClamp, DepthStencilState.Default, RasterizerState.CullNone);
    }
}
