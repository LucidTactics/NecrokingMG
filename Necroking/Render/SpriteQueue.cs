using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using XnaEffect = Microsoft.Xna.Framework.Graphics.Effect;

namespace Necroking.Render;

/// <summary>
/// Coarse draw-order bands inside a sprite-queue pass — the data replacement
/// for "which block of the old Draw() am I in". Values are spaced so new
/// layers (fog bands, poke-through) can slot between existing ones.
/// Within a layer, items order by depth (world Y) then submission order.
/// </summary>
public enum WorldLayer : byte
{
    Roads = 10,
    Traps = 20,
    Glyphs = 30,
    Walls = 40,
    Shadows = 50,
    HoverMarkers = 60,
    Corpses = 70,
    FogBack = 75,       // ground-fog back blanket — behind all Y-sorted bodies
    YSort = 80,         // the depth-sorted world: units, env objects, particles
    Projectiles = 90,
    Rope = 100,
    Rain = 110,
    FogWisps = 115,          // ground-fog wisps, depth-tested vs unit stamps
    EffectsHdrAlpha = 120,   // HDR clouds/smoke (alpha-blended sub-pass)
    EffectsHdrAdditive = 130,// HDR glows/fireballs/reanim/lightning (additive)
    AdditiveShapes = 140,    // plain additive shapes (energy columns, debug)
    Foragables = 200,
    DamageNumbers = 210,
}

/// <summary>Sets per-draw uniforms on a material's effect just before its batch
/// opens (tier 3 of the param model — forces a batch break; see design doc §4.2).</summary>
public delegate void MaterialParamSetter(XnaEffect effect);

/// <summary>A composite draw occupying one sortable slot: draws its sprites
/// in call order into the open batch via the scope. <paramref name="a"/>/<paramref name="b"/>
/// carry payload indices (unit index, puff sub-index…) so callback instances can
/// be cached once instead of closing over per-item state.
/// The scope is passed by value — it is a cheap handle; the active material
/// lives in <see cref="Materials.Open"/>, not in the scope.</summary>
public delegate void SpriteDrawCallback(SpriteScope scope, int a, int b);

/// <summary>
/// THE draw surface: wraps the open batch and knows which <see cref="Material"/>
/// is active, so every color funnels through <see cref="Material.Tint"/> in one
/// place. Draw/DrawString take STRAIGHT-alpha colors — a plain
/// <c>new Color(r, g, b, 128)</c> is correct by default; never hand-premultiply
/// (that was the recurring washed-out-hue / double-dim bug class). Colors that
/// are already a material-native encoding (HDR vertex pack, the additive-via-A=0
/// trick) go through <see cref="Batch"/> instead, which bypasses conversion.
///
/// The ONLY sanctioned way to deviate from the pass material is
/// PushMaterial/PopMaterial — the resume state is carried in the scope, computed
/// by the executor, never guessed by the call site (this retires the
/// EffectBatch.BeginEffect pattern and its wrong-restore bug class).
/// </summary>
public readonly struct SpriteScope
{
    private readonly SpriteBatch _batch;
    private readonly Material _resume;

    public SpriteScope(SpriteBatch batch, Material resume)
    {
        _batch = batch;
        _resume = resume;
    }

    /// <summary>Any batch can be viewed as a draw surface: encoding follows the
    /// globally tracked open material either way, so passing a raw SpriteBatch
    /// where a scope is expected is always color-correct. (The reverse — getting
    /// the raw batch out of a scope — stays explicit via <see cref="Batch"/>.)</summary>
    public static implicit operator SpriteScope(SpriteBatch batch)
        => new(batch, Materials.Open ?? Materials.Hud);

    /// <summary>The raw batch — the explicit escape hatch. Colors passed here
    /// bypass the straight-alpha convention and must already be in the active
    /// material's native encoding. Also for Immediate-sort per-draw uniform sets.</summary>
    public SpriteBatch Batch => _batch;

    public GraphicsDevice GraphicsDevice => _batch.GraphicsDevice;

    /// <summary>The material whose batch is currently open (tracked globally by
    /// <see cref="Material.Begin"/>, so Push/Pop/ad-hoc opens can never desync it).</summary>
    public Material? CurrentMaterial => Materials.Open;

    /// <summary>Encode a straight-alpha color for the open material — identity
    /// when no material is tracked (raw batch) or the material takes straight tints.</summary>
    private static Color Encode(Color straight)
        => Materials.Open is { } m ? m.Tint(straight) : straight;

    /// <summary>Public form of the tint encoding, for draws that must go through
    /// third-party extension methods on the raw <see cref="Batch"/> (FontStashSharp
    /// text) but still author straight-alpha colors.</summary>
    public Color EncodeTint(Color straight) => Encode(straight);

    /// <summary>Open <paramref name="m"/>'s batch and return a scope over it —
    /// the immediate-mode entry point (HUD/editor passes). Pair with <see cref="End"/>.</summary>
    public static SpriteScope Begin(SpriteBatch batch, Material m)
    {
        m.Begin(batch);
        return new SpriteScope(batch, m);
    }

    /// <summary>End the open batch (immediate-mode counterpart of <see cref="Begin"/>).</summary>
    public void End() => _batch.End();

    /// <summary>End the open batch and open <paramref name="m"/> (optionally
    /// setting its per-draw uniforms first). Pair with <see cref="PopMaterial"/>.
    /// No nesting — one push at a time.</summary>
    public void PushMaterial(Material m, MaterialParamSetter? setParams = null)
    {
        _batch.End();
        if (setParams != null && m.Effect != null) setParams(m.Effect);
        m.Begin(_batch);
    }

    /// <summary>End the pushed material's batch and resume the pass material.</summary>
    public void PopMaterial()
    {
        _batch.End();
        _resume.Begin(_batch);
    }

    /// <summary>End the open batch so external code can run its own batches /
    /// raw device draws. Pair with <see cref="Resume"/>. Prefer
    /// Push/PopMaterial when the deviation is just a different material.</summary>
    public void Suspend() => _batch.End();

    /// <summary>Reopen the pass material after a <see cref="Suspend"/>.</summary>
    public void Resume()
    {
        _resume.Begin(_batch);
    }

    // --- Converting draw surface: mirrors SpriteBatch.Draw/DrawString so call
    // sites are drop-in, but colors are STRAIGHT alpha and get encoded by the
    // active material. ---

    public void Draw(Texture2D texture, Vector2 position, Color color)
        => _batch.Draw(texture, position, Encode(color));

    public void Draw(Texture2D texture, Vector2 position, Rectangle? sourceRectangle, Color color)
        => _batch.Draw(texture, position, sourceRectangle, Encode(color));

    public void Draw(Texture2D texture, Vector2 position, Rectangle? sourceRectangle, Color color,
        float rotation, Vector2 origin, float scale, SpriteEffects effects, float layerDepth)
        => _batch.Draw(texture, position, sourceRectangle, Encode(color),
            rotation, origin, scale, effects, layerDepth);

    public void Draw(Texture2D texture, Vector2 position, Rectangle? sourceRectangle, Color color,
        float rotation, Vector2 origin, Vector2 scale, SpriteEffects effects, float layerDepth)
        => _batch.Draw(texture, position, sourceRectangle, Encode(color),
            rotation, origin, scale, effects, layerDepth);

    public void Draw(Texture2D texture, Rectangle destinationRectangle, Color color)
        => _batch.Draw(texture, destinationRectangle, Encode(color));

    public void Draw(Texture2D texture, Rectangle destinationRectangle, Rectangle? sourceRectangle, Color color)
        => _batch.Draw(texture, destinationRectangle, sourceRectangle, Encode(color));

    public void Draw(Texture2D texture, Rectangle destinationRectangle, Rectangle? sourceRectangle, Color color,
        float rotation, Vector2 origin, SpriteEffects effects, float layerDepth)
        => _batch.Draw(texture, destinationRectangle, sourceRectangle, Encode(color),
            rotation, origin, effects, layerDepth);

    public void DrawString(SpriteFont spriteFont, string text, Vector2 position, Color color)
        => _batch.DrawString(spriteFont, text, position, Encode(color));

    public void DrawString(SpriteFont spriteFont, System.Text.StringBuilder text, Vector2 position, Color color)
        => _batch.DrawString(spriteFont, text, position, Encode(color));

    public void DrawString(SpriteFont spriteFont, string text, Vector2 position, Color color,
        float rotation, Vector2 origin, float scale, SpriteEffects effects, float layerDepth)
        => _batch.DrawString(spriteFont, text, position, Encode(color),
            rotation, origin, scale, effects, layerDepth);

    public void DrawString(SpriteFont spriteFont, string text, Vector2 position, Color color,
        float rotation, Vector2 origin, Vector2 scale, SpriteEffects effects, float layerDepth)
        => _batch.DrawString(spriteFont, text, position, Encode(color),
            rotation, origin, scale, effects, layerDepth);
}

/// <summary>One submitted draw: a sprite (SpriteBatch.Draw args, screen space)
/// or a callback slot, plus its material and packed sort key.</summary>
public struct RenderItem : IComparable<RenderItem>
{
    // Packed sort key — see SortKey. Plain ulong compare = full draw order.
    public ulong Key;
    public Material Material;

    // Sprite payload (ignored when Callback != null):
    public Texture2D? Texture;
    public Rectangle? Source;
    public Vector2 Position;      // screen space, projected at submit time
    public Vector2 Origin;
    public float Scale;
    public float Rotation;
    public Color Color;
    public SpriteEffects Flip;

    // GPU depth for materials with a depth-testing DepthStencilState
    // (FogDepthForY mapping); 0 for everything else.
    public float LayerDepth;

    // Composite payload:
    public SpriteDrawCallback? Callback;
    public int CbA, CbB;

    // Tier-3 per-draw uniforms — forces this item into its own batch.
    public MaterialParamSetter? SetParams;

    public int CompareTo(RenderItem other) => Key.CompareTo(other.Key);
}

/// <summary>
/// Sort-key packing: [63..56] layer | [55..32] depth (24b) | [31..16] material
/// id | [15..0] sequence. Layer replaces block order; depth is camera-relative
/// quantized world Y (camera-relative for the same reason as FogDepthForY — an
/// absolute mapping saturates across a 4096-unit map); material groups
/// same-state items at equal depth; sequence bakes submission order in so the
/// unstable List.Sort can never flicker equal-depth items.
/// </summary>
public static class SortKey
{
    public static ulong Pack(byte layer, uint depth24, ushort materialId, ushort seq)
        => ((ulong)layer << 56)
         | ((ulong)(depth24 & 0xFFFFFF) << 32)
         | ((ulong)materialId << 16)
         | seq;

    /// <summary>±2048 world units around the camera → 0..2^24-1 (1/4096-unit
    /// precision — far finer than any visible sort distinction).</summary>
    public static uint DepthFromWorldY(float worldY, float cameraY)
    {
        float t = (worldY - cameraY + 2048f) * (16777215f / 4096f);
        if (t < 0f) t = 0f;
        else if (t > 16777215f) t = 16777215f;
        return (uint)t;
    }
}

/// <summary>
/// The workhorse pass: collects submissions, sorts by key, walks the sorted
/// list and opens a SpriteBatch only when the material changes (reference
/// compare — the Nez flush rule). Batch count is an output, not an invariant:
/// LastItemCount/LastBatchCount feed the perf readout.
/// </summary>
public sealed class SpriteQueuePass : RenderPass
{
    private readonly List<RenderItem> _items;
    private readonly Material _defaultMaterial;
    private readonly Func<float> _cameraY;
    private ushort _seq;
    private float _frameCameraY;

    /// <summary>Fills the queue at Execute time, before sorting — game systems
    /// submit here so collection order (and thus the sequence tiebreaker)
    /// matches the old imperative order.</summary>
    public Action<RenderContext>? Collect;

    public int LastItemCount;
    public int LastBatchCount;

    public SpriteQueuePass(string name, Material defaultMaterial, Func<float> cameraY,
        int capacity = 512) : base(name)
    {
        _defaultMaterial = defaultMaterial;
        _cameraY = cameraY;
        _items = new List<RenderItem>(capacity);
    }

    // --- Submission API (screen-space positions, projected by the caller) ---

    public void SubmitCallback(WorldLayer layer, float worldY, SpriteDrawCallback cb,
        int a, int b, Material? material = null)
    {
        var mat = material ?? _defaultMaterial;
        _items.Add(new RenderItem
        {
            Key = SortKey.Pack((byte)layer, SortKey.DepthFromWorldY(worldY, _frameCameraY), mat.Id, NextSeq()),
            Material = mat,
            Callback = cb,
            CbA = a,
            CbB = b,
        });
    }

    /// <summary>Layer-ordered callback slot (no depth sort within the layer —
    /// submission order rules via the sequence bits). For whole-block items.</summary>
    public void SubmitCallback(WorldLayer layer, SpriteDrawCallback cb, int a, int b,
        Material? material = null)
    {
        var mat = material ?? _defaultMaterial;
        _items.Add(new RenderItem
        {
            Key = SortKey.Pack((byte)layer, 0, mat.Id, NextSeq()),
            Material = mat,
            Callback = cb,
            CbA = a,
            CbB = b,
        });
    }

    public void SubmitSprite(WorldLayer layer, float worldY, Texture2D texture, Vector2 position,
        Rectangle? source, Color color, float rotation, Vector2 origin, float scale,
        SpriteEffects flip = SpriteEffects.None, Material? material = null,
        MaterialParamSetter? setParams = null, float layerDepth = 0f)
    {
        var mat = material ?? _defaultMaterial;
        _items.Add(new RenderItem
        {
            Key = SortKey.Pack((byte)layer, SortKey.DepthFromWorldY(worldY, _frameCameraY), mat.Id, NextSeq()),
            Material = mat,
            Texture = texture,
            Source = source,
            Position = position,
            Origin = origin,
            Scale = scale,
            Rotation = rotation,
            Color = color,
            Flip = flip,
            SetParams = setParams,
            LayerDepth = layerDepth,
        });
    }

    private ushort NextSeq() => _seq == ushort.MaxValue ? _seq : _seq++;

    // --- Execution: sort + group-consecutive-materials + flush ---

    public override void Execute(RenderContext ctx)
    {
        _items.Clear();
        _seq = 0;
        _frameCameraY = _cameraY();
        Collect?.Invoke(ctx);

        var items = _items;
        LastItemCount = items.Count;
        LastBatchCount = 0;
        if (items.Count == 0) return;

        items.Sort();

        var batch = ctx.Batch;
        Material? open = null;
        bool forceBreak = false;

        for (int i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var mat = item.Material;
            bool needOwn = item.SetParams != null || mat.RequiresPerDrawParams;

            if (open == null || !ReferenceEquals(mat, open) || needOwn || forceBreak)
            {
                if (open != null) batch.End();
                if (item.SetParams != null && mat.Effect != null) item.SetParams(mat.Effect);
                mat.Begin(batch);
                LastBatchCount++;
                open = mat;
                forceBreak = needOwn;
            }

            if (item.Callback != null)
            {
                // Callbacks may Push/PopMaterial — Pop resumes `open`, so the
                // batch state after the callback matches what the loop tracks.
                item.Callback(new SpriteScope(batch, open), item.CbA, item.CbB);
            }
            else if (item.Texture != null)
            {
                // Submitted colors are straight alpha; the material encodes them
                // here — the queue-side twin of SpriteScope.Draw's conversion.
                batch.Draw(item.Texture, item.Position, item.Source, mat.Tint(item.Color),
                    item.Rotation, item.Origin, item.Scale, item.Flip, item.LayerDepth);
            }
        }

        if (open != null) batch.End();
    }
}
