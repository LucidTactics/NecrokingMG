using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Necroking.Data;
using Necroking.Data.Registries;
using Necroking.Lib;
using Necroking.Render;
using Necroking.World;

namespace Necroking.UI;

/// <summary>
/// Top-right minimap, sitting under the core-menu/editor button rows (in the
/// map editor it docks left of the editor panel instead and follows the free
/// camera; clicking it there jumps the camera — see MinimapLayer). Shows a
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
    public const int BaseSize = 192;
    // The map editor docks a bigger minimap (1.5× each side, 2.25× the world
    // area at the same units/px) — an overview map is worth more screen there.
    public const int EditorSize = BaseSize * 3 / 2;
    // Just below the editor-launcher row (HUDRenderer: EditorBtnTop 36 + MenuBtnH 24 + 6 gap).
    public const int Top = 66;
    public const int RightMargin = 8;
    /// <summary>Bottom edge in normal play — HUD elements that used to sit
    /// under the button rows (horde caps) now sit under this. Deliberately
    /// base-size: its only consumer never shows in the map editor.</summary>
    public const int Bottom = Top + BaseSize;
    // Gap between the minimap and the map editor's right-side panel when the
    // editor is open (the minimap docks left of the panel so it doesn't cover it).
    private const int EditorPanelGap = 8;

    /// <summary>Rendered pixel size (and baked texture resolution) for the
    /// current mode. Everything — bounds, bake, window span — scales off this,
    /// so per-mode sizes only need a new case here.</summary>
    public static int CurrentSize
        => Game1.Instance._menuState == MenuState.MapEditor ? EditorSize : BaseSize;

    // World units each minimap pixel spans: one tree ≈ one darkened pixel. The
    // window covers CurrentSize * this (384 at the base 192px size), shrinking
    // to the map size on maps smaller than that.
    private const int WorldUnitsPerPixel = 2;
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
    private static readonly Color ViewportColor = Color.White * 0.5f; // premultiplied 50% white
    // Fog overlay (premultiplied). Unexplored is opaque — nothing shows through —
    // but deliberately dark gray, not pure black (too stark next to the terrain).
    private static readonly Color FogUnexplored = new(26, 26, 32, 255);
    private static readonly Color FogExplored = new Color(0, 0, 0, 90); // ~35% dim: terrain readable, clearly fogged
    // Canonical faction palette lives in Render.FactionColors (shared with the
    // map editor's placed-unit labels). These aliases keep the local call sites
    // terse — change a color there, not here.
    private static readonly Color PlayerColor = FactionColors.Player;
    private static readonly Color AnimalColor = FactionColors.Animal;
    private static readonly Color HumanColor = FactionColors.Human;
    private static readonly Color UndeadColor = FactionColors.Undead;

    private GraphicsDevice _device = null!;
    private SpriteBatch _batch = null!;
    private Texture2D _pixel = null!;

    // Fog overlay refreshes much faster than the terrain bake (the bright circle
    // follows moving units) but not every frame — the CPU fog grid itself only
    // updates every 2nd frame (FogOfWarSystem.UpdateInterval).
    private const int FogRefreshFrames = 3;

    private Texture2D? _terrainTex;
    private Texture2D? _fogTex;
    private Color[] _bakeBuffer = Array.Empty<Color>();
    private Color[] _fogBuffer = Array.Empty<Color>();
    private int _framesSinceFog = 999;
    private bool[] _obstacleMask = Array.Empty<bool>();
    private Color[] _typeColors = Array.Empty<Color>();
    private GroundSystem? _bakedGround; // session identity — rebake + recolor when it changes
    private int _framesSinceBake;
    // The world window the baked texture covers (origin + span, world units).
    // Markers use the SAME window so they stay registered with the terrain.
    private float _winX, _winY, _winW, _winH;
    // Pixel size the terrain texture was baked at (CurrentSize can change on a
    // mode switch mid-frame-cycle; the fog overlay must match the bake).
    private int _texSize = BaseSize;

    public void Init(GraphicsDevice device, SpriteBatch batch, Texture2D pixel)
    {
        _device = device;
        _batch = batch;
        _pixel = pixel;
    }

    /// <summary>Screen rect of the minimap. Top-right normally; in the map
    /// editor it moves left of the editor's right-side panel so it doesn't
    /// cover it. The single placement source — draw, hit rect, and click
    /// mapping all derive from it.</summary>
    public static Rectangle Bounds(int screenW)
    {
        int size = CurrentSize;
        int x = Game1.Instance._menuState == MenuState.MapEditor
            ? Editor.MapEditorWindow.PanelLeftX(screenW) - EditorPanelGap - size
            : screenW - RightMargin - size;
        return new(x, Top, size, size);
    }

    public void Draw(int screenW, int screenH)
    {
        var g = Game1.Instance;
        var ground = g._groundSystem;
        if (ground == null || ground.WorldW <= 0 || ground.WorldH <= 0) return;

        var (wantX, wantY, wantW, wantH) = DesiredWindow(g, ground);
        bool drifted = MathF.Abs(wantX + wantW * 0.5f - (_winX + _winW * 0.5f)) > RecenterDistance
                    || MathF.Abs(wantY + wantH * 0.5f - (_winY + _winH * 0.5f)) > RecenterDistance;
        // Width check: a mode switch (editor <-> play) changes CurrentSize
        // without necessarily drifting the center — the texture must follow.
        if (_terrainTex == null || _terrainTex.Width != CurrentSize
            || _bakedGround != ground || drifted
            || ++_framesSinceBake >= RebakeFrames)
            Bake(g, ground, wantX, wantY, wantW, wantH);
        if (_terrainTex == null) return;

        var rect = Bounds(screenW);
        // This is premultiplied for some reason!!
        FillRect(new Rectangle(rect.X - 2, rect.Y - 2, rect.Width + 4, rect.Height + 4), new(80, 80, 70, 80));
        FillRect(new Rectangle(rect.X - 1, rect.Y - 1, rect.Width + 2, rect.Height + 2), BorderColor);
        _batch.Draw(_terrainTex, rect, Color.White);

        // Fog over terrain, markers over fog: buildings stay full-bright inside
        // the dimmed explored band, and gating (below) keeps markers out of the
        // opaque unexplored area. Fog-free in the map editor, mirroring the
        // world's FogOfWarOverlay pass — the editor sees everything.
        var fog = g._fogOfWar;
        bool fogOn = fog.Mode != FogOfWarMode.Off && g._menuState != MenuState.MapEditor;
        if (fogOn)
        {
            if (++_framesSinceFog >= FogRefreshFrames) RefreshFogTexture(fog);
            if (_fogTex != null) _batch.Draw(_fogTex, rect, Color.White);
        }

        float sx = rect.Width / _winW;
        float sy = rect.Height / _winH;
        DrawBuildingMarkers(g, rect, sx, sy, fogOn ? fog : null);
        DrawUnitMarkers(g, rect, sx, sy, fogOn ? fog : null);
        DrawCameraViewport(g, rect, sx, sy, screenW, screenH);
    }

    /// <summary>Map a screen pixel inside the minimap to the world point it
    /// shows (inverse of the marker mapping, against the baked window). False
    /// when the pixel is outside the minimap or nothing has been baked yet.</summary>
    public bool TryScreenToWorld(int mx, int my, int screenW, out Vec2 world)
    {
        world = Vec2.Zero;
        if (_terrainTex == null || _winW <= 0 || _winH <= 0) return false;
        var rect = Bounds(screenW);
        if (!rect.Contains(mx, my)) return false;
        world = new Vec2(_winX + (mx - rect.X) * _winW / rect.Width,
                         _winY + (my - rect.Y) * _winH / rect.Height);
        return true;
    }
    public bool TryScreenToWorldNoBoundsCheck(int mx, int my, int screenW, out Vec2 world)
    {
        world = Vec2.Zero;
        if (_terrainTex == null || _winW <= 0 || _winH <= 0) return false;
        var rect = Bounds(screenW);
        world = new Vec2(_winX + (mx - rect.X) * _winW / rect.Width,
            _winY + (my - rect.Y) * _winH / rect.Height);
        return true;
    }

    /// <summary>Rebuild the fog overlay for the current window: opaque dark on
    /// unexplored, translucent dim on explored-but-not-visible, clear where seen.</summary>
    private void RefreshFogTexture(FogOfWarSystem fog)
    {
        _framesSinceFog = 0;
        int size = _texSize; // must match the baked terrain texture, not CurrentSize
        int n = size * size;
        if (_fogBuffer.Length != n) _fogBuffer = new Color[n];
        for (int py = 0; py < size; py++)
        {
            int ty = (int)(_winY + (py + 0.5f) * _winH / size);
            int row = py * size;
            for (int px = 0; px < size; px++)
            {
                int tx = (int)(_winX + (px + 0.5f) * _winW / size);
                _fogBuffer[row + px] = fog.GetTileState(tx, ty) switch
                {
                    FogTileState.Unexplored => FogUnexplored,
                    FogTileState.Explored => FogExplored,
                    _ => Color.Transparent,
                };
            }
        }
        if (_fogTex == null || _fogTex.IsDisposed || _fogTex.Width != size)
        {
            _fogTex?.Dispose();
            _fogTex = new Texture2D(_device, size, size);
        }
        _fogTex.SetData(_fogBuffer);
    }

    /// <summary>Outline of the world area the camera currently renders.</summary>
    private void DrawCameraViewport(Game1 g, Rectangle rect, float sx, float sy, int screenW, int screenH)
    {
        var tl = g._camera.ScreenToWorld(Vector2.Zero, screenW, screenH);
        var br = g._camera.ScreenToWorld(new Vector2(screenW, screenH), screenW, screenH);
        int x0 = rect.X + (int)((tl.X - _winX) * sx);
        int y0 = rect.Y + (int)((tl.Y - _winY) * sy);
        int x1 = rect.X + (int)((br.X - _winX) * sx);
        int y1 = rect.Y + (int)((br.Y - _winY) * sy);
        // Sides skip the corners the top/bottom strips already covered — at 50%
        // alpha a double-drawn corner pixel would pop brighter.
        ClippedRect(x0, y0, x1 - x0 + 1, 1, ViewportColor, rect);
        ClippedRect(x0, y1, x1 - x0 + 1, 1, ViewportColor, rect);
        ClippedRect(x0, y0 + 1, 1, y1 - y0 - 1, ViewportColor, rect);
        ClippedRect(x1, y0 + 1, 1, y1 - y0 - 1, ViewportColor, rect);
    }

    public Vec2 map_center;

    public Vec2 baked_map_center => new Vec2(_winX + _winW * 0.5f, _winY + _winH * 0.5f);

    /// <summary>The window of the world the minimap should show: CurrentSize *
    /// WorldUnitsPerPixel world units centered on the player (map center if
    /// none), clamped to the map — whole map when it's smaller than that. In
    /// the map editor it centers on the free camera instead, so the minimap
    /// follows where you're working rather than staying pinned to the
    /// necromancer.</summary>
    private (float x, float y, float w, float h) DesiredWindow(Game1 g, GroundSystem ground)
    {
        int viewRange = CurrentSize * WorldUnitsPerPixel;
        float w = Math.Min(viewRange, ground.WorldW);
        float h = Math.Min(viewRange, ground.WorldH);
        float cx = ground.WorldW * 0.5f, cy = ground.WorldH * 0.5f;
        if (g._menuState == MenuState.MapEditor)
        {
            cx = map_center.X;
            cy = map_center.Y;
        }
        else
        {
            int necroIdx = g.FindNecromancer();
            if (necroIdx >= 0)
            {
                var p = g._sim.Units[necroIdx].Position;
                cx = p.X; cy = p.Y;
            }
        }

        map_center = new(cx, cy);
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
        _framesSinceFog = 999; // window moved — fog overlay must follow this frame

        int size = _texSize = CurrentSize;
        int n = size * size;
        if (_bakeBuffer.Length != n)
        {
            _bakeBuffer = new Color[n];
            _obstacleMask = new bool[n];
        }

        var vmap = ground.GetVertexMap();
        int vw = ground.VertexW, vh = ground.VertexH;
        var fallback = FallbackTerrainColor(TerrainType.Open);
        for (int py = 0; py < size; py++)
        {
            int vy = Math.Clamp((int)(winY + (py + 0.5f) * winH / size), 0, vh - 1);
            int vrow = vy * vw;
            int row = py * size;
            for (int px = 0; px < size; px++)
            {
                int vx = Math.Clamp((int)(winX + (px + 0.5f) * winW / size), 0, vw - 1);
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
            int px = (int)((objects[i].X - winX) * size / winW);
            int py = (int)((objects[i].Y - winY) * size / winH);
            if (px < 0 || px >= size || py < 0 || py >= size) continue;
            if (!env.IsObjectVisible(i)) continue;
            int idx = py * size + px;
            if (_obstacleMask[idx]) continue;
            _obstacleMask[idx] = true;
            var c = _bakeBuffer[idx];
            _bakeBuffer[idx] = new Color(
                (byte)(c.R * ObstacleDarken), (byte)(c.G * ObstacleDarken),
                (byte)(c.B * ObstacleDarken), c.A);
        }

        if (_terrainTex == null || _terrainTex.IsDisposed || _terrainTex.Width != size)
        {
            _terrainTex?.Dispose();
            _terrainTex = new Texture2D(_device, size, size);
        }
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

    private void DrawBuildingMarkers(Game1 g, Rectangle rect, float sx, float sy, FogOfWarSystem? fog)
    {
        var env = g._envSystem;
        var objects = env.Objects;
        var defs = env.Defs;
        for (int i = 0; i < env.ObjectCount; i++)
        {
            var def = defs[objects[i].DefIndex];
            if (!def.IsBuilding) continue;
            if (!env.IsObjectVisible(i)) continue;
            // Owner convention (see EnvironmentSystem trap dispatch): 0 = Undead, 1 = Human.
            bool undeadOwned = env.GetObjectRuntime(i).Owner == 0;
            // Own buildings always show; others need the tile explored at least
            // once (buildings, unlike units, stay marked in the fogged band).
            if (!undeadOwned && fog != null
                && fog.GetTileState((int)objects[i].X, (int)objects[i].Y) == FogTileState.Unexplored)
                continue;
            int cx = rect.X + (int)((objects[i].X - _winX) * sx);
            int cy = rect.Y + (int)((objects[i].Y - _winY) * sy);
            if (undeadOwned)
                OutlinedSquare(cx, cy, 3, UndeadColor, rect);
            else
                OutlinedDisc(cx, cy, 3, HumanColor, rect);
        }
    }

    private void DrawUnitMarkers(Game1 g, Rectangle rect, float sx, float sy, FogOfWarSystem? fog)
    {
        var units = g._sim.Units;
        int necroIdx = g.FindNecromancer();
        for (int i = 0; i < units.Count; i++)
        {
            var u = units[i];
            if (!u.Alive || i == necroIdx) continue;
            // Same culling as the world renderer (GameRenderer.Units): own undead
            // always show, everyone else only inside current vision. The extra
            // Unexplored check only bites in Explored mode, where IsVisible is
            // unconditionally true but the dark shroud still hides unseen tiles.
            if (u.Faction != Faction.Undead && fog != null
                && (!fog.IsVisible(u.Position)
                    || fog.GetTileState((int)u.Position.X, (int)u.Position.Y) == FogTileState.Unexplored))
                continue;
            Color col = FactionColors.For(u.Faction);
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
