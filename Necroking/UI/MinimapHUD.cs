using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Necroking.Data;
using Necroking.World;

namespace Necroking.UI;

/// <summary>
/// Top-right minimap, sitting under the core-menu/editor button rows. Shows a
/// player-centered window of the world (not the whole map — on the 4096-tile
/// default map a whole-map view collapses every lake and forest into a pixel).
/// Terrain (ground colors + darkened natural obstacles) is baked into a small
/// texture and refreshed when the player wanders or every few seconds —
/// corruption spread and chopped forests show up on the next bake — while
/// unit/building markers draw live every frame:
///   player   = outlined white dot
///   animals  = bright green dots
///   humans   = yellow dots,  human buildings = larger outlined yellow dots
///   undead   = gray dots,   undead buildings = larger outlined gray squares
/// </summary>
public class MinimapHUD
{
    public const int MapSize = 192;
    // Just below the editor-launcher row (HUDRenderer: EditorBtnTop 36 + MenuBtnH 24 + 6 gap).
    public const int Top = 66;
    public const int RightMargin = 8;
    /// <summary>Bottom edge — HUD elements that used to sit under the button
    /// rows (horde caps) now sit under this.</summary>
    public const int Bottom = Top + MapSize;

    // World units the window spans (2 units/px at MapSize 192): one tree ≈ one
    // darkened pixel. Shrinks to the map size on maps smaller than this.
    private const int ViewRange = 384;
    // How far (world units) the player may drift from the baked window's center
    // before a rebake re-centers it.
    private const float RecenterDistance = 8f;
    // Terrain rebake cadence in drawn frames (~2.5 s at 60 fps). Session changes
    // and player movement rebake sooner; this only bounds how stale corruption
    // spread and chopped/grown obstacles can look.
    private const int RebakeFrames = 150;
    private const float ObstacleDarken = 0.75f;

    private static readonly Color BorderColor = new(20, 20, 30, 220);
    private static readonly Color MarkerOutline = new(10, 10, 12);
    private static readonly Color PlayerColor = Color.White;
    private static readonly Color AnimalColor = new(90, 240, 70);
    private static readonly Color HumanColor = new(255, 215, 60);
    private static readonly Color UndeadColor = new(180, 180, 190);

    private GraphicsDevice _device = null!;
    private SpriteBatch _batch = null!;
    private Texture2D _pixel = null!;

    private Texture2D? _terrainTex;
    private Color[] _bakeBuffer = Array.Empty<Color>();
    private bool[] _obstacleMask = Array.Empty<bool>();
    private Color[] _typeColors = Array.Empty<Color>();
    private GroundSystem? _bakedGround; // session identity — rebake + recolor when it changes
    private int _framesSinceBake;
    // The world window the baked texture covers (origin + span, world units).
    // Markers use the SAME window so they stay registered with the terrain.
    private float _winX, _winY, _winW, _winH;

    public void Init(GraphicsDevice device, SpriteBatch batch, Texture2D pixel)
    {
        _device = device;
        _batch = batch;
        _pixel = pixel;
    }

    public static Rectangle Bounds(int screenW)
        => new(screenW - RightMargin - MapSize, Top, MapSize, MapSize);

    public void Draw(int screenW, int screenH)
    {
        var g = Game1.Instance;
        var ground = g._groundSystem;
        if (ground == null || ground.WorldW <= 0 || ground.WorldH <= 0) return;

        var (wantX, wantY, wantW, wantH) = DesiredWindow(g, ground);
        bool drifted = MathF.Abs(wantX + wantW * 0.5f - (_winX + _winW * 0.5f)) > RecenterDistance
                    || MathF.Abs(wantY + wantH * 0.5f - (_winY + _winH * 0.5f)) > RecenterDistance;
        if (_terrainTex == null || _bakedGround != ground || drifted
            || ++_framesSinceBake >= RebakeFrames)
            Bake(g, ground, wantX, wantY, wantW, wantH);
        if (_terrainTex == null) return;

        var rect = Bounds(screenW);
        FillRect(new Rectangle(rect.X - 2, rect.Y - 2, rect.Width + 4, rect.Height + 4), BorderColor);
        _batch.Draw(_terrainTex, rect, Color.White);

        float sx = rect.Width / _winW;
        float sy = rect.Height / _winH;
        DrawBuildingMarkers(g, rect, sx, sy);
        DrawUnitMarkers(g, rect, sx, sy);
    }

    /// <summary>The window of the world the minimap should show: ViewRange
    /// world units centered on the player (map center if none), clamped to the
    /// map — whole map when it's smaller than ViewRange.</summary>
    private (float x, float y, float w, float h) DesiredWindow(Game1 g, GroundSystem ground)
    {
        float w = Math.Min(ViewRange, ground.WorldW);
        float h = Math.Min(ViewRange, ground.WorldH);
        float cx = ground.WorldW * 0.5f, cy = ground.WorldH * 0.5f;
        int necroIdx = g.FindNecromancer();
        if (necroIdx >= 0)
        {
            var p = g._sim.Units[necroIdx].Position;
            cx = p.X; cy = p.Y;
        }
        float x = Math.Clamp(cx - w * 0.5f, 0, ground.WorldW - w);
        float y = Math.Clamp(cy - h * 0.5f, 0, ground.WorldH - h);
        return (x, y, w, h);
    }

    // ═══════════════════════════════════════
    //  Terrain bake
    // ═══════════════════════════════════════

    private void Bake(Game1 g, GroundSystem ground, float winX, float winY, float winW, float winH)
    {
        _framesSinceBake = 0;
        if (_bakedGround != ground)
        {
            _typeColors = new Color[ground.TypeCount];
            for (int i = 0; i < _typeColors.Length; i++)
                _typeColors[i] = TypeColor(ground, i);
            _bakedGround = ground;
        }
        _winX = winX; _winY = winY; _winW = winW; _winH = winH;

        int n = MapSize * MapSize;
        if (_bakeBuffer.Length != n)
        {
            _bakeBuffer = new Color[n];
            _obstacleMask = new bool[n];
        }

        var vmap = ground.GetVertexMap();
        int vw = ground.VertexW, vh = ground.VertexH;
        var fallback = FallbackTerrainColor(TerrainType.Open);
        for (int py = 0; py < MapSize; py++)
        {
            int vy = Math.Clamp((int)(winY + (py + 0.5f) * winH / MapSize), 0, vh - 1);
            int vrow = vy * vw;
            int row = py * MapSize;
            for (int px = 0; px < MapSize; px++)
            {
                int vx = Math.Clamp((int)(winX + (px + 0.5f) * winW / MapSize), 0, vw - 1);
                byte t = vmap[vrow + vx];
                _bakeBuffer[row + px] = t < _typeColors.Length ? _typeColors[t] : fallback;
            }
        }

        // Natural blocking obstacles (trees, rocks — collision, not buildings)
        // darken their terrain texel so forests read as texture. Mask keeps
        // dense clusters from darkening the same texel repeatedly.
        Array.Clear(_obstacleMask, 0, _obstacleMask.Length);
        var env = g._envSystem;
        var objects = env.Objects;
        var defs = env.Defs;
        for (int i = 0; i < env.ObjectCount; i++)
        {
            var def = defs[objects[i].DefIndex];
            if (def.IsBuilding || def.CollisionRadius <= 0) continue;
            int px = (int)((objects[i].X - winX) * MapSize / winW);
            int py = (int)((objects[i].Y - winY) * MapSize / winH);
            if (px < 0 || px >= MapSize || py < 0 || py >= MapSize) continue;
            if (!env.IsObjectVisible(i)) continue;
            int idx = py * MapSize + px;
            if (_obstacleMask[idx]) continue;
            _obstacleMask[idx] = true;
            var c = _bakeBuffer[idx];
            _bakeBuffer[idx] = new Color(
                (byte)(c.R * ObstacleDarken), (byte)(c.G * ObstacleDarken),
                (byte)(c.B * ObstacleDarken), c.A);
        }

        if (_terrainTex == null || _terrainTex.IsDisposed)
            _terrainTex = new Texture2D(_device, MapSize, MapSize);
        _terrainTex.SetData(_bakeBuffer);
    }

    /// <summary>Representative minimap color for a ground type: the average of
    /// its texture (the smallest CPU-baked mip level — see
    /// TextureUtil.CreateTextureFromPixels) times its tint, falling back to a
    /// MovementTerrain palette when no texture/mips are available.</summary>
    private static Color TypeColor(GroundSystem ground, int idx)
    {
        var def = ground.GetTypeDef(idx);
        Color col = FallbackTerrainColor(def.MovementTerrain);
        var tex = ground.GetTexture(idx);
        if (tex != null && !tex.IsDisposed && tex.LevelCount > 1)
        {
            int level = tex.LevelCount - 1;
            int lw = Math.Max(1, tex.Width >> level);
            int lh = Math.Max(1, tex.Height >> level);
            var data = new Color[lw * lh];
            tex.GetData(level, null, data, 0, data.Length);
            long r = 0, gr = 0, b = 0;
            for (int i = 0; i < data.Length; i++) { r += data[i].R; gr += data[i].G; b += data[i].B; }
            col = new Color((int)(r / data.Length), (int)(gr / data.Length), (int)(b / data.Length));
        }
        var tint = def.TintColor;
        if (tint != Color.White)
            col = new Color(col.R * tint.R / 255, col.G * tint.G / 255, col.B * tint.B / 255);
        return new Color(col.R, col.G, col.B, (byte)255);
    }

    private static Color FallbackTerrainColor(TerrainType t) => t switch
    {
        TerrainType.Rough => new Color(120, 105, 75),
        TerrainType.ShallowWater => new Color(70, 115, 150),
        TerrainType.DeepWater => new Color(40, 65, 110),
        TerrainType.Wall => new Color(75, 75, 80),
        _ => new Color(95, 140, 75),
    };

    // ═══════════════════════════════════════
    //  Live markers
    // ═══════════════════════════════════════

    private void DrawBuildingMarkers(Game1 g, Rectangle rect, float sx, float sy)
    {
        var env = g._envSystem;
        var objects = env.Objects;
        var defs = env.Defs;
        for (int i = 0; i < env.ObjectCount; i++)
        {
            var def = defs[objects[i].DefIndex];
            if (!def.IsBuilding) continue;
            if (!env.IsObjectVisible(i)) continue;
            int cx = rect.X + (int)((objects[i].X - _winX) * sx);
            int cy = rect.Y + (int)((objects[i].Y - _winY) * sy);
            // Owner convention (see EnvironmentSystem trap dispatch): 0 = Undead, 1 = Human.
            if (env.GetObjectRuntime(i).Owner == 0)
                OutlinedSquare(cx, cy, 3, UndeadColor, rect);
            else
                OutlinedDisc(cx, cy, 3, HumanColor, rect);
        }
    }

    private void DrawUnitMarkers(Game1 g, Rectangle rect, float sx, float sy)
    {
        var units = g._sim.Units;
        int necroIdx = g.FindNecromancer();
        for (int i = 0; i < units.Count; i++)
        {
            var u = units[i];
            if (!u.Alive || i == necroIdx) continue;
            Color col = u.Faction switch
            {
                Faction.Animal => AnimalColor,
                Faction.Human => HumanColor,
                _ => UndeadColor,
            };
            ClippedRect(rect.X + (int)((u.Position.X - _winX) * sx) - 1,
                        rect.Y + (int)((u.Position.Y - _winY) * sy) - 1, 2, 2, col, rect);
        }
        // Player drawn last so it always sits on top of the crowd.
        if (necroIdx >= 0)
        {
            var p = units[necroIdx].Position;
            OutlinedDisc(rect.X + (int)((p.X - _winX) * sx),
                         rect.Y + (int)((p.Y - _winY) * sy), 2, PlayerColor, rect);
        }
    }

    // ═══════════════════════════════════════
    //  Pixel-primitive helpers (clipped to the map rect)
    // ═══════════════════════════════════════

    private void FillRect(Rectangle r, Color c) => _batch.Draw(_pixel, r, c);

    private void ClippedRect(int x, int y, int w, int h, Color c, Rectangle clip)
    {
        var r = Rectangle.Intersect(new Rectangle(x, y, w, h), clip);
        if (r.Width > 0 && r.Height > 0) FillRect(r, c);
    }

    /// <summary>Filled circle from horizontal pixel strips (r=2 → 5px rounded
    /// dot, r=3 → 7px octagon).</summary>
    private void Disc(int cx, int cy, int r, Color c, Rectangle clip)
    {
        float rr = (r + 0.5f) * (r + 0.5f);
        for (int dy = -r; dy <= r; dy++)
        {
            int halfW = (int)MathF.Sqrt(rr - dy * dy);
            ClippedRect(cx - halfW, cy + dy, halfW * 2 + 1, 1, c, clip);
        }
    }

    private void OutlinedDisc(int cx, int cy, int r, Color fill, Rectangle clip)
    {
        Disc(cx, cy, r + 1, MarkerOutline, clip);
        Disc(cx, cy, r, fill, clip);
    }

    private void OutlinedSquare(int cx, int cy, int r, Color fill, Rectangle clip)
    {
        ClippedRect(cx - r - 1, cy - r - 1, 2 * r + 3, 2 * r + 3, MarkerOutline, clip);
        ClippedRect(cx - r, cy - r, 2 * r + 1, 2 * r + 1, fill, clip);
    }
}
