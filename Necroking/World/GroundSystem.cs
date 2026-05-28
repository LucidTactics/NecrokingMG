using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Necroking.Core;

namespace Necroking.World;

public class GroundTypeDef
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string TexturePath { get; set; } = "";
    public Color TintColor { get; set; } = Color.White;
    /// <summary>Id of the ground type to swap in when this type is corrupted by death fog.
    /// Empty = no corrupted variant; rolls do nothing on this type.</summary>
    public string CorruptedTypeId { get; set; } = "";
    /// <summary>Movement terrain class this visual ground type implies. Drives the
    /// pathfinding cost field via <see cref="GroundSystem.StampTerrainOnto"/>
    /// (worst-of-4-corners per tile). Defaults to Open — paint a water-textured
    /// ground type and set this to ShallowWater/DeepWater to make pathfinding
    /// respect it.</summary>
    public TerrainType MovementTerrain { get; set; } = TerrainType.Open;
}

public class GroundSystem
{
    private int _worldW, _worldH;
    private readonly List<GroundTypeDef> _types = new();
    private readonly List<Texture2D?> _textures = new();
    private byte[] _vertexMap = Array.Empty<byte>();

    // Sparse runtime corruption: vertex index → original (pre-corruption) byte.
    // _vertexMap holds the live (possibly corrupted) state; this dict is only
    // ever populated by the corruption tick, never by editor paint, so save can
    // strip out gameplay-driven corruption while preserving dev paint.
    private readonly Dictionary<int, byte> _corruptionEdits = new();

    // Per-vertex visual fade progress for newly corrupted vertices: 0 = just
    // started (renders as original type), 1 = fully transitioned (renders as
    // corrupted type). Vertices not in this dict are stable (no fade in flight).
    // Encoded into the B channel of the vertex map texture; the GroundShader
    // lerps between original (G channel) and current (R channel) by this value.
    private readonly Dictionary<int, float> _corruptionFadeProgress = new();
    public float CorruptionFadeDuration { get; set; } = 5f;
    private float _fadeRebuildTimer;
    public float FadeRebuildInterval { get; set; } = 0.125f; // ~8 Hz GPU re-uploads while fading

    /// <summary>True when the vertex map has changed since the last GPU rebuild.
    /// Cleared by callers after they re-upload the vertex map texture.</summary>
    public bool CorruptionDirty { get; private set; }
    public int CorruptedVertexCount => _corruptionEdits.Count;
    public int FadingVertexCount   => _corruptionFadeProgress.Count;

    /// <summary>Fired when a vertex newly corrupts (CorruptVertex returns true).
    /// Used by Game1 to start grass-tuft fades in the affected world region.
    /// Not fired for editor paint, map load, or ClearAllCorruption.</summary>
    public Action<int, int>? OnVertexCorrupted;

    // Bounding box of vertex changes since the last GPU upload. UploadDirtyRect
    // re-encodes only this rect and pushes it into the existing vertex map
    // texture via Texture2D.SetData(rect, ...). Avoids both the 67 MB full
    // upload and the per-tick texture allocation.
    private int _dirtyMinX = int.MaxValue, _dirtyMinY = int.MaxValue;
    private int _dirtyMaxX = int.MinValue, _dirtyMaxY = int.MinValue;
    public bool HasDirtyRect => _dirtyMaxX >= _dirtyMinX;
    private Microsoft.Xna.Framework.Color[] _uploadBuffer = Array.Empty<Microsoft.Xna.Framework.Color>();

    private void MarkDirty(int vx, int vy)
    {
        if (vx < _dirtyMinX) _dirtyMinX = vx;
        if (vy < _dirtyMinY) _dirtyMinY = vy;
        if (vx > _dirtyMaxX) _dirtyMaxX = vx;
        if (vy > _dirtyMaxY) _dirtyMaxY = vy;
    }
    private void ResetDirtyRect()
    {
        _dirtyMinX = int.MaxValue; _dirtyMinY = int.MaxValue;
        _dirtyMaxX = int.MinValue; _dirtyMaxY = int.MinValue;
    }

    public float TypeWarpStrength = 1.8f;
    public float UvWarpAmp = 0.4f;
    public float UvWarpFreq = 0.15f;
    public int DebugMode = 0;

    public int WorldW => _worldW;
    public int WorldH => _worldH;
    public int VertexW => _worldW + 1;
    public int VertexH => _worldH + 1;

    public void Init(int worldW, int worldH)
    {
        _worldW = worldW;
        _worldH = worldH;
        _vertexMap = new byte[VertexW * VertexH];
    }

    /// <summary>Bool-per-type-index, true if that ground type's MovementTerrain
    /// is a water variant. Refreshed by <see cref="RebuildIsWaterCache"/>
    /// whenever types are added/removed/cleared. Hot path: SampleWaterness
    /// does 4 of these lookups per call, and SampleWaternessSmoothed does
    /// 5×4 per call — caching this saves a property + enum compare per lookup.</summary>
    private bool[] _isWaterByTypeIdx = Array.Empty<bool>();

    // Texture-slot deduplication. Multiple ground types can share the same
    // PNG (e.g. shallow_water + swamp_shallow_water both use ShallowWater.png
    // but differ in tint). The shader cascade is keyed by texture slot, not
    // ground-type index, so that growing the ground-type count doesn't
    // expand the cascade and blow the PS_3_0 temp-register budget.
    private int[] _textureSlotByTypeIdx = Array.Empty<int>();
    private List<Texture2D?> _uniqueTextures = new();
    public int UniqueTextureCount => _uniqueTextures.Count;
    public int GetTextureSlot(int typeIdx) =>
        (typeIdx >= 0 && typeIdx < _textureSlotByTypeIdx.Length) ? _textureSlotByTypeIdx[typeIdx] : 0;
    public Texture2D? GetUniqueTexture(int slot) =>
        (slot >= 0 && slot < _uniqueTextures.Count) ? _uniqueTextures[slot] : null;

    /// <summary>Pack (textureSlot, groundType) into a single byte for the tilemap
    /// R/G channels. Top 3 bits = texture slot (0..7), bottom 5 bits = ground
    /// type id (0..31). The shader decodes both to (a) sample the texture
    /// cascade by slot — no dynamic-index uniform array read inside the
    /// cascade, which is what keeps PS_3_0's temp-register budget — and
    /// (b) look up per-type tint and iswater flag separately.</summary>
    public byte PackSlotType(byte typeIdx)
    {
        int slot = (typeIdx < _textureSlotByTypeIdx.Length) ? _textureSlotByTypeIdx[typeIdx] : 0;
        slot = Math.Clamp(slot, 0, 7);
        int t = Math.Clamp((int)typeIdx, 0, 31);
        return (byte)((slot << 5) | t);
    }

    public int AddGroundType(GroundTypeDef def)
    {
        _types.Add(def);
        _textures.Add(null);
        RebuildIsWaterCache();
        RebuildTextureSlotCache();
        return _types.Count - 1;
    }

    private void RebuildIsWaterCache()
    {
        if (_isWaterByTypeIdx.Length != _types.Count)
            _isWaterByTypeIdx = new bool[_types.Count];
        for (int i = 0; i < _types.Count; i++)
        {
            var t = _types[i].MovementTerrain;
            _isWaterByTypeIdx[i] = (t == TerrainType.ShallowWater || t == TerrainType.DeepWater);
        }
    }

    // Build slot mapping by deduplicating ground types' texture paths. Types
    // with empty TexturePath share slot 0 (the fallback). Called after types
    // are added/removed and after LoadTextures so slot references stay in sync.
    private void RebuildTextureSlotCache()
    {
        if (_textureSlotByTypeIdx.Length != _types.Count)
            _textureSlotByTypeIdx = new int[_types.Count];
        var pathToSlot = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        _uniqueTextures.Clear();
        for (int i = 0; i < _types.Count; i++)
        {
            string path = _types[i].TexturePath ?? "";
            if (string.IsNullOrEmpty(path)) { _textureSlotByTypeIdx[i] = 0; continue; }
            if (!pathToSlot.TryGetValue(path, out int slot))
            {
                slot = _uniqueTextures.Count;
                pathToSlot[path] = slot;
                _uniqueTextures.Add(i < _textures.Count ? _textures[i] : null);
            }
            _textureSlotByTypeIdx[i] = slot;
        }
    }

    public int TypeCount => _types.Count;
    public GroundTypeDef GetTypeDef(int idx) => _types[idx];

    public void SetVertex(int vx, int vy, byte typeIndex)
    {
        if (vx >= 0 && vx < VertexW && vy >= 0 && vy < VertexH)
        {
            int vi = vy * VertexW + vx;
            _vertexMap[vi] = typeIndex;
            // Editor paint always wins as authoritative — drop any prior corruption
            // record AND any in-flight fade so save preserves the painted value
            // and the GPU shows the new type without a transition.
            if (_corruptionEdits.Remove(vi)) CorruptionDirty = true;
            if (_corruptionFadeProgress.Remove(vi)) CorruptionDirty = true;
        }
    }

    public byte GetVertex(int vx, int vy) =>
        vx >= 0 && vx < VertexW && vy >= 0 && vy < VertexH
            ? _vertexMap[vy * VertexW + vx] : (byte)0;

    public void FillAll(byte typeIndex) { Array.Fill(_vertexMap, typeIndex); _corruptionEdits.Clear(); _corruptionFadeProgress.Clear(); }

    public byte[] GetVertexMap() => _vertexMap;
    public void SetVertexMap(byte[] map) { if (map.Length == _vertexMap.Length) Array.Copy(map, _vertexMap, map.Length); _corruptionEdits.Clear(); _corruptionFadeProgress.Clear(); }

    /// <summary>Returns the vertex map with runtime corruption reverted, for serialization.
    /// Allocates a copy only when corruption is present; otherwise returns the live array.</summary>
    public byte[] GetVertexMapForSave()
    {
        if (_corruptionEdits.Count == 0) return _vertexMap;
        var temp = new byte[_vertexMap.Length];
        Array.Copy(_vertexMap, temp, _vertexMap.Length);
        foreach (var kv in _corruptionEdits)
            temp[kv.Key] = kv.Value;
        return temp;
    }

    /// <summary>Resolve a type Id to its index, or -1 if missing.</summary>
    public int FindType(string id)
    {
        if (string.IsNullOrEmpty(id)) return -1;
        for (int i = 0; i < _types.Count; i++) if (_types[i].Id == id) return i;
        return -1;
    }

    /// <summary>Resolve the corrupted-variant type index for a given type, or -1 if it has no mapping.</summary>
    public int GetCorruptedIndex(int typeIdx)
    {
        if (typeIdx < 0 || typeIdx >= _types.Count) return -1;
        return FindType(_types[typeIdx].CorruptedTypeId);
    }

    /// <summary>Attempt to corrupt the vertex at (vx, vy). Returns true if the vertex
    /// was changed (had a corrupted variant and wasn't already corrupted).</summary>
    public bool CorruptVertex(int vx, int vy)
    {
        if (vx < 0 || vx >= VertexW || vy < 0 || vy >= VertexH) return false;
        int vi = vy * VertexW + vx;
        if (_corruptionEdits.ContainsKey(vi)) return false; // already corrupted by tick
        byte cur = _vertexMap[vi];
        int corrIdx = GetCorruptedIndex(cur);
        if (corrIdx < 0) return false; // no mapping (e.g. cobblestone, or already a corrupted variant)
        _corruptionEdits[vi] = cur;
        _vertexMap[vi] = (byte)corrIdx;
        // Start fade-in: shader will lerp from original (cur) to current (corrIdx) over
        // CorruptionFadeDuration. Without this the swap would render as a hard flip.
        _corruptionFadeProgress[vi] = 0f;
        CorruptionDirty = true;
        MarkDirty(vx, vy);
        OnVertexCorrupted?.Invoke(vx, vy);
        return true;
    }

    /// <summary>Advance per-vertex fade progress. Called every gameplay frame so fades
    /// animate smoothly between texture rebuilds. Returns true if any fade level
    /// changed enough to warrant a GPU rebuild this frame (rate-limited internally).</summary>
    public bool AdvanceCorruptionFades(float dt)
    {
        if (_corruptionFadeProgress.Count == 0) return false;

        float fadeRate = 1f / MathF.Max(CorruptionFadeDuration, 0.01f);
        // Iterate via key snapshot since we may remove entries mid-iteration.
        if (_fadingKeyBuffer.Length < _corruptionFadeProgress.Count)
            _fadingKeyBuffer = new int[Math.Max(_corruptionFadeProgress.Count, 64)];
        int n = 0;
        foreach (var k in _corruptionFadeProgress.Keys) _fadingKeyBuffer[n++] = k;
        for (int i = 0; i < n; i++)
        {
            int vi = _fadingKeyBuffer[i];
            float t = _corruptionFadeProgress[vi] + dt * fadeRate;
            int vx = vi % VertexW;
            int vy = vi / VertexW;
            // Every fading vertex's encoded fade byte changes each frame, so we
            // need its texel re-uploaded at the next tick. Vertices that just
            // *finished* fading also need one final re-upload to set fade=255.
            MarkDirty(vx, vy);
            if (t >= 1f) _corruptionFadeProgress.Remove(vi);
            else         _corruptionFadeProgress[vi] = t;
        }

        // Rate-limit the GPU re-upload — even partial uploads are cheap, but
        // ~8 Hz is plenty smooth and avoids per-frame driver overhead.
        _fadeRebuildTimer += dt;
        if (_fadeRebuildTimer >= FadeRebuildInterval)
        {
            _fadeRebuildTimer = 0f;
            CorruptionDirty = true;
            return true;
        }
        return false;
    }
    private int[] _fadingKeyBuffer = Array.Empty<int>();

    /// <summary>Revert all runtime corruption. Useful for debugging / mid-session reset.</summary>
    public void ClearAllCorruption()
    {
        if (_corruptionEdits.Count == 0) return;
        foreach (var kv in _corruptionEdits)
        {
            _vertexMap[kv.Key] = kv.Value;
            int vx = kv.Key % VertexW;
            int vy = kv.Key / VertexW;
            MarkDirty(vx, vy);
        }
        _corruptionEdits.Clear();
        _corruptionFadeProgress.Clear();
        CorruptionDirty = true;
    }

    /// <summary>Caller signals it has rebuilt the GPU vertex map texture from the latest map.</summary>
    public void ClearCorruptionDirty() => CorruptionDirty = false;

    public void RemoveType(int index)
    {
        if (index < 0 || index >= _types.Count) return;
        _types.RemoveAt(index);
        _textures.RemoveAt(index);
        RebuildIsWaterCache();
        RebuildTextureSlotCache();
        // Remap vertex map
        for (int i = 0; i < _vertexMap.Length; i++)
        {
            if (_vertexMap[i] == index) _vertexMap[i] = 0;
            else if (_vertexMap[i] > index) _vertexMap[i]--;
        }
    }

    public void ClearTypes() { _types.Clear(); _textures.Clear(); RebuildIsWaterCache(); RebuildTextureSlotCache(); }

    public void LoadTextures(GraphicsDevice device)
    {
        while (_textures.Count < _types.Count) _textures.Add(null);
        // Cache-by-path so types sharing a texture path (e.g. shallow_water and
        // swamp_shallow_water) reference the same Texture2D rather than loading
        // it twice.
        var byPath = new Dictionary<string, Texture2D>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < _types.Count; i++)
        {
            if (_textures[i] != null) continue;
            string path = _types[i].TexturePath;
            if (string.IsNullOrEmpty(path)) continue;
            if (byPath.TryGetValue(path, out var cached)) { _textures[i] = cached; continue; }
            string resolved = Core.GamePaths.Resolve(path);
            if (!System.IO.File.Exists(resolved)) continue;
            try
            {
                var loaded = Necroking.Render.TextureUtil.LoadPremultiplied(device, path);
                _textures[i] = loaded;
                if (loaded != null) byPath[path] = loaded;
            }
            catch (Exception ex) { DebugLog.Log("error", $"Failed to load ground texture '{path}': {ex.Message}"); }
        }
        RebuildTextureSlotCache();
    }

    public Texture2D? GetTexture(int typeIdx)
    {
        if (typeIdx < 0 || typeIdx >= _textures.Count) return null;
        return _textures[typeIdx];
    }

    /// <summary>Push only the dirty rect's pixels into the existing vertex map
    /// texture via Texture2D.SetData(rect, ...). Avoids re-allocating a 67 MB
    /// texture and re-uploading 16 M pixels each fade tick — typical fade
    /// footprints upload a few KB. Returns false if there's no rect to push or
    /// the supplied texture's dimensions don't match the vertex grid (caller
    /// should fall back to CreateVertexMapTexture).</summary>
    public bool UploadDirtyRect(Texture2D tex)
    {
        if (!HasDirtyRect) { CorruptionDirty = false; return true; }
        if (tex == null || tex.Width != VertexW || tex.Height != VertexH) return false;

        int minX = Math.Max(0, _dirtyMinX);
        int minY = Math.Max(0, _dirtyMinY);
        int maxX = Math.Min(VertexW - 1, _dirtyMaxX);
        int maxY = Math.Min(VertexH - 1, _dirtyMaxY);
        int w = maxX - minX + 1;
        int h = maxY - minY + 1;
        if (w <= 0 || h <= 0) { ResetDirtyRect(); CorruptionDirty = false; return true; }

        int needed = w * h;
        if (_uploadBuffer.Length < needed)
            _uploadBuffer = new Microsoft.Xna.Framework.Color[Math.Max(needed, 1024)];

        bool anyFading = _corruptionFadeProgress.Count > 0;
        for (int y = 0; y < h; y++)
        {
            int srcY = minY + y;
            int srcRow = srcY * VertexW;
            int dstRow = y * w;
            for (int x = 0; x < w; x++)
            {
                int vi = srcRow + minX + x;
                byte cur = _vertexMap[vi];
                byte orig = cur;
                byte fade = 255;
                if (anyFading
                    && _corruptionFadeProgress.TryGetValue(vi, out float t01)
                    && _corruptionEdits.TryGetValue(vi, out byte origByte))
                {
                    orig = origByte;
                    fade = (byte)Math.Clamp((int)(t01 * 255f + 0.5f), 0, 255);
                }
                _uploadBuffer[dstRow + x] = new Microsoft.Xna.Framework.Color(
                    PackSlotType(cur), PackSlotType(orig), fade, (byte)255);
            }
        }

        var rect = new Rectangle(minX, minY, w, h);
        tex.SetData(0, rect, _uploadBuffer, 0, needed);

        ResetDirtyRect();
        CorruptionDirty = false;
        return true;
    }

    /// <summary>Bilinear sample of how "in water" a world position is, 0..1.
    /// Each of the 4 surrounding vertices contributes 0 (not water) or 1
    /// (water — type's MovementTerrain is ShallowWater or DeepWater); the
    /// returned value is the bilerp at the fractional position. Used to
    /// smoothly transition wading depth as a unit crosses the shoreline so
    /// the waterline rises on the body rather than snapping to full depth.
    ///
    /// Returns 0 if the position is outside the map.</summary>
    public float SampleWaterness(Vec2 worldPos)
    {
        if (_worldW <= 0 || _worldH <= 0 || _types.Count == 0) return 0f;
        float u = worldPos.X;
        float v = worldPos.Y;
        int x0 = (int)MathF.Floor(u);
        int y0 = (int)MathF.Floor(v);
        if (x0 < 0 || x0 >= _worldW || y0 < 0 || y0 >= _worldH) return 0f;
        float fx = u - x0;
        float fy = v - y0;

        int vw = VertexW;
        // Read all 4 vertex types and convert to 1/0 by water-ness.
        float w00 = IsWaterTypeIdx(_vertexMap[y0 * vw + x0]) ? 1f : 0f;
        float w10 = IsWaterTypeIdx(_vertexMap[y0 * vw + (x0 + 1)]) ? 1f : 0f;
        float w01 = IsWaterTypeIdx(_vertexMap[(y0 + 1) * vw + x0]) ? 1f : 0f;
        float w11 = IsWaterTypeIdx(_vertexMap[(y0 + 1) * vw + (x0 + 1)]) ? 1f : 0f;

        float wTop = w00 * (1f - fx) + w10 * fx;
        float wBot = w01 * (1f - fx) + w11 * fx;
        return wTop * (1f - fy) + wBot * fy;
    }

    private bool IsWaterTypeIdx(byte typeIdx) =>
        typeIdx < _isWaterByTypeIdx.Length && _isWaterByTypeIdx[typeIdx];

    /// <summary>Sample the tint of the nearest water vertex around a world
    /// position. Used by the wake system to colour spawned particles to
    /// match the water they're in: pre-baked particle texture variants are
    /// keyed on this tint, and the variant lookup just compares it to the
    /// known set. Returns <see cref="Color.White"/> when none of the
    /// surrounding vertices are water (or the position is outside the
    /// map), so the caller falls back to the default (untinted) variant
    /// cleanly.
    ///
    /// Examines all 4 corner vertices of the tile the position sits in
    /// and picks the *nearest water vertex by distance* — using just the
    /// rounded-nearest vertex (the obvious one-tap version) misses the
    /// shoreline case: a unit standing in water right next to a grass
    /// vertex can round to that grass vertex and return White even
    /// though three other corners are water. Wading edge events (entry
    /// splash, first-step trail) fire exactly at that shoreline, which
    /// is where this would have shown up as "no tint" in swamp water.</summary>
    public Color SampleNearestWaterTint(Vec2 worldPos)
    {
        if (_worldW <= 0 || _worldH <= 0 || _types.Count == 0) return Color.White;
        int x0 = (int)MathF.Floor(worldPos.X);
        int y0 = (int)MathF.Floor(worldPos.Y);

        float bestDistSq = float.MaxValue;
        Color bestTint = Color.White;
        for (int dy = 0; dy <= 1; dy++)
        for (int dx = 0; dx <= 1; dx++)
        {
            int vx = x0 + dx;
            int vy = y0 + dy;
            if (vx < 0 || vx >= VertexW || vy < 0 || vy >= VertexH) continue;
            byte typeIdx = _vertexMap[vy * VertexW + vx];
            if (!IsWaterTypeIdx(typeIdx)) continue;
            float ddx = worldPos.X - vx;
            float ddy = worldPos.Y - vy;
            float distSq = ddx * ddx + ddy * ddy;
            if (distSq < bestDistSq)
            {
                bestDistSq = distSq;
                bestTint = _types[typeIdx].TintColor;
            }
        }
        return bestTint;
    }

    /// <summary>Like <see cref="SampleWaterness"/> but averages over a small
    /// kernel (centre + 4 cardinal offsets at <paramref name="kernelRadius"/>
    /// world units). Spreads the 0→1 transition across roughly
    /// 2 × kernelRadius world units instead of the ~0.5-unit span you get
    /// from a single bilinear sample — so a unit walking onto a shore feels
    /// like it's wading deeper gradually over multiple tiles, not snapping
    /// to full depth as soon as it clears the shoreline.</summary>
    public float SampleWaternessSmoothed(Vec2 worldPos, float kernelRadius = 1f)
    {
        float w0 = SampleWaterness(worldPos);
        float wE = SampleWaterness(new Vec2(worldPos.X + kernelRadius, worldPos.Y));
        float wW = SampleWaterness(new Vec2(worldPos.X - kernelRadius, worldPos.Y));
        float wS = SampleWaterness(new Vec2(worldPos.X, worldPos.Y + kernelRadius));
        float wN = SampleWaterness(new Vec2(worldPos.X, worldPos.Y - kernelRadius));
        return (w0 + wE + wW + wS + wN) * 0.2f;
    }

    /// <summary>Resolve each tile's pathfinding TerrainType from the 4 corner
    /// vertex ground types: tile gets the highest-cost terrain of its 4 corners
    /// (worst-of-4). Walls already stamped into the grid are left alone — the
    /// caller is expected to BakeWalls afterwards so walls win cleanly.
    /// Safe to call repeatedly (overwrites all non-wall tiles).</summary>
    public void StampTerrainOnto(TileGrid grid)
    {
        if (_worldW <= 0 || _worldH <= 0 || _types.Count == 0)
        {
            DebugLog.Log("startup", $"  StampTerrainOnto: skipped (worldW={_worldW} worldH={_worldH} types={_types.Count})");
            return;
        }
        if (grid.Width != _worldW || grid.Height != _worldH)
        {
            DebugLog.Log("startup", $"  StampTerrainOnto: skipped (grid {grid.Width}x{grid.Height} != ground {_worldW}x{_worldH})");
            return;
        }

        // Cache type-index → TerrainType (and its cost) so we don't switch per vertex.
        int n = _types.Count;
        var terrainPerType = new TerrainType[n];
        var costPerType = new float[n];
        var typeNameTerrain = new System.Text.StringBuilder();
        for (int i = 0; i < n; i++)
        {
            terrainPerType[i] = _types[i].MovementTerrain;
            costPerType[i] = TerrainCosts.GetCost(terrainPerType[i]);
            if (typeNameTerrain.Length > 0) typeNameTerrain.Append(", ");
            typeNameTerrain.Append($"{i}={_types[i].Id}->{terrainPerType[i]}");
        }
        DebugLog.Log("startup", $"  StampTerrainOnto type map: {typeNameTerrain}");

        int countOpen = 0, countRough = 0, countShallow = 0, countDeep = 0, countWall = 0;
        int vw = VertexW;
        for (int ty = 0; ty < _worldH; ty++)
        {
            int row0 = ty * vw;
            int row1 = (ty + 1) * vw;
            for (int tx = 0; tx < _worldW; tx++)
            {
                // Don't clobber walls written by WallSystem.
                if (grid.GetTerrain(tx, ty) == TerrainType.Wall) { countWall++; continue; }

                byte v00 = _vertexMap[row0 + tx];
                byte v10 = _vertexMap[row0 + tx + 1];
                byte v01 = _vertexMap[row1 + tx];
                byte v11 = _vertexMap[row1 + tx + 1];

                TerrainType worst = TerrainType.Open;
                float worstCost = -1f;
                for (int c = 0; c < 4; c++)
                {
                    byte v = c == 0 ? v00 : c == 1 ? v10 : c == 2 ? v01 : v11;
                    if (v >= n) continue;
                    float cc = costPerType[v];
                    if (cc > worstCost) { worstCost = cc; worst = terrainPerType[v]; }
                }
                grid.SetTerrain(tx, ty, worst);
                switch (worst)
                {
                    case TerrainType.Open: countOpen++; break;
                    case TerrainType.Rough: countRough++; break;
                    case TerrainType.ShallowWater: countShallow++; break;
                    case TerrainType.DeepWater: countDeep++; break;
                }
            }
        }
        DebugLog.Log("startup", $"  StampTerrainOnto wrote: Open={countOpen} Rough={countRough} ShallowWater={countShallow} DeepWater={countDeep} (Wall left alone={countWall})");
    }

    public Texture2D? CreateVertexMapTexture(GraphicsDevice device)
    {
        if (_vertexMap.Length == 0) return null;
        // Channel layout (read by GroundShader.fx):
        //   R = current PackSlotType byte: top 3 bits = texture slot (0..7),
        //       bottom 5 bits = ground type id (0..31). Shader cascade reads
        //       the slot directly without an array indirection (which keeps
        //       PS_3_0 temp-register pressure inside the 32-register limit)
        //       and indexes per-type tint / iswater via the type bits.
        //   G = original PackSlotType byte (same encoding; equals R for
        //       stable vertices).
        //   B = fade progress 0..255 (255 = stable, < 255 = mid-fade).
        //   A = 255 (so the channel isn't dropped by premultiplied-alpha paths).
        var tex = new Texture2D(device, VertexW, VertexH, false, SurfaceFormat.Color);
        var colorData = new Microsoft.Xna.Framework.Color[_vertexMap.Length];
        // Fast path when nothing is fading — avoid per-vertex dict lookup for 16M entries.
        bool anyFading = _corruptionFadeProgress.Count > 0;
        if (!anyFading)
        {
            for (int i = 0; i < _vertexMap.Length; i++)
            {
                byte packed = PackSlotType(_vertexMap[i]);
                colorData[i] = new Microsoft.Xna.Framework.Color(packed, packed, (byte)255, (byte)255);
            }
        }
        else
        {
            for (int i = 0; i < _vertexMap.Length; i++)
            {
                byte cur = _vertexMap[i];
                byte orig = cur;
                byte fade = 255;
                if (_corruptionFadeProgress.TryGetValue(i, out float t01)
                    && _corruptionEdits.TryGetValue(i, out byte origByte))
                {
                    orig = origByte;
                    fade = (byte)Math.Clamp((int)(t01 * 255f + 0.5f), 0, 255);
                }
                colorData[i] = new Microsoft.Xna.Framework.Color(
                    PackSlotType(cur), PackSlotType(orig), fade, (byte)255);
            }
        }
        tex.SetData(colorData);
        // Any rebuild reflects the latest map state — clear the dirty flag so
        // the per-frame "rebuild if dirty" check in Game1 doesn't fire spuriously.
        CorruptionDirty = false;
        ResetDirtyRect();
        return tex;
    }
}
