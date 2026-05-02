using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using Necroking.Core;
using Necroking.Data.Registries;
using Necroking.GameSystems;
using Necroking.Render;
using Necroking.UI;
using Necroking.World;

namespace Necroking.Game;

/// <summary>
/// Small floating menu that opens above a craft-table. Shows N corpse slots, M
/// item slots, an essence display, a progress bar, a Start button, and a close
/// X. Auto-sizes its width to slot count so 1 / 2 / 3 corpse-or-item slots all
/// lay out cleanly with consistent spacing.
///
/// UI policy:
///   - Auto-opens on corpse load (Game1 hook).
///   - Click the table to reopen if closed.
///   - X button or Escape closes.
///   - Click an item in player inventory while menu is open → moves to first
///     empty table item slot (decrements inventory by 1).
///   - Right-click a filled table item slot → returns the item to inventory.
///   - Start button → invokes Game1's craft-start callback (Phase E).
///
/// Rendering: outer panel chrome via DrawWidget("TableCraftMenu") for the skin;
/// slot boxes / progress bar / button / X via primitives for tight pixel control
/// (slot widget "Item Slot" is sized for the equipment grid, too large here).
/// </summary>
public class TableCraftMenuUI
{
    private const string PanelWidgetId = "TableCraftMenu";

    // BASE layout constants — pixel sizes at the reference zoom (BaseZoom).
    // The actual rendered sizes flow through Scaled(...) which multiplies by
    // _uiScale (= camera.Zoom / BaseZoom). At default zoom they match these
    // base values exactly; zooming in/out scales the menu like an in-world
    // object so it feels anchored to the table instead of floating fixed-pixel.
    private const int SlotSize = 56;
    private const int SlotSpacing = 8;
    private const int PadX = 14;
    private const int PadTop = 28;          // leaves room for close X
    private const int PadBottom = 12;
    private const int LabelHeight = 16;     // band between slot row and action row for "Corpse" / "Slot 1" / "Essence"
    private const int RowGap = 8;           // gap between label band and action row
    private const int ActionRowHeight = 28;
    private const int CloseBtnSize = 18;
    private const int StartButtonWidth = 50;
    private const int LabelOffsetY = 2;     // gap between slot bottom and its label
    private const int CloseBtnOffsetY = 6;  // CloseBtn top inset from panel top
    private const int CloseBtnInset = 4;    // X-glyph inset inside the button rect

    /// <summary>Reference zoom level — at this zoom × MenuSizeFactor, _uiScale = 1.0
    /// and the menu draws at its raw base sizes. Matches Camera25D's default Zoom of 32.</summary>
    private const float BaseZoom = 32f;

    /// <summary>Multiplier on top of the zoom-derived scale. Tune this to make the
    /// whole menu uniformly bigger or smaller without touching individual layout
    /// constants. 1.0 = base sizes at default zoom; &lt;1 = shrink, &gt;1 = grow.</summary>
    private const float MenuSizeFactor = 0.6f;
    /// <summary>World-space height (above table center) to anchor the menu's bottom edge.
    /// Same convention DamageEvent uses (default ~1.5 = humanoid torso). Tuned higher
    /// here so the menu sits clearly above the table sprite, not on top of it.</summary>
    private const float MenuAnchorWorldHeight = 3.0f;

    private RuntimeWidgetRenderer _renderer = null!;
    private EnvironmentSystem _envSystem = null!;
    private Inventory _inventory = null!;
    private ItemRegistry _items = null!;
    private PlayerResources _resources = null!;
    private SpriteBatch _batch = null!;
    private Texture2D _pixel = null!;
    private SpriteFont? _font;

    private bool _visible;
    private int _envIdx = -1;
    private int _screenX, _screenY;
    private int _widgetW, _widgetH;
    private int _screenW, _screenH;
    private Camera25D? _camera;
    private Renderer? _worldRenderer;
    private InputState? _lastInput;
    private float _uiScale = 1f; // recomputed each frame from camera.Zoom

    /// <summary>Scale a base pixel value by the current UI scale (camera-zoom-relative).
    /// All layout numbers go through this so the menu behaves like an in-world
    /// object — bigger when zoomed in, smaller when zoomed out.</summary>
    private int Scaled(int v) => (int)(v * _uiScale);

    /// <summary>Invoked when the player clicks the Start button. Caller is responsible
    /// for kicking off the craft routine + assigning a channeler. Returns true on
    /// success (UI shows progress), false on failure (UI no-ops).</summary>
    public Func<int, bool>? StartCraftCallback;

    /// <summary>Optional callback to draw a unit's idle sprite into a screen rect.
    /// Set by Game1; lets the menu show the loaded corpse's source unit (deer, wolf,
    /// etc.) inside the corpse slot without TableCraftMenuUI needing atlas knowledge.</summary>
    public Action<string, Rectangle>? DrawUnitIconCallback;

    public bool IsVisible => _visible;
    public int EnvIdx => _envIdx;

    public void Init(RuntimeWidgetRenderer renderer, EnvironmentSystem envSystem,
        Inventory inventory, ItemRegistry items, PlayerResources resources,
        SpriteBatch batch, Texture2D pixel, SpriteFont? font)
    {
        _renderer = renderer;
        _envSystem = envSystem;
        _inventory = inventory;
        _items = items;
        _resources = resources;
        _batch = batch;
        _pixel = pixel;
        _font = font;
    }

    public void OpenForTable(int envIdx, int screenW, int screenH, Camera25D camera, Renderer worldRenderer)
    {
        if (envIdx < 0 || envIdx >= _envSystem.ObjectCount) return;
        var def = _envSystem.Defs[_envSystem.GetObject(envIdx).DefIndex];
        if (!Game.TableSystem.IsTable(def)) return;

        _envIdx = envIdx;
        _visible = true;
        _screenW = screenW;
        _screenH = screenH;
        _camera = camera;
        _worldRenderer = worldRenderer;

        ResizeToDef(def);
        // Initial position; Update() re-anchors every frame so the menu tracks
        // the table as the camera pans.
        RepositionAboveTable();
    }

    public void Close()
    {
        _visible = false;
        _envIdx = -1;
    }

    /// <summary>Auto-size the panel def to fit the table's slot counts at current UI scale.</summary>
    private void ResizeToDef(EnvironmentObjectDef def)
    {
        int slotsTotal = def.CorpseSlots + def.ItemSlots + 1; // +1 for essence display
        int contentW = slotsTotal * Scaled(SlotSize) + (slotsTotal - 1) * Scaled(SlotSpacing);
        _widgetW = Scaled(PadX) * 2 + contentW;
        _widgetH = Scaled(PadTop) + Scaled(SlotSize) + Scaled(LabelHeight) + Scaled(RowGap) + Scaled(ActionRowHeight) + Scaled(PadBottom);

        var panelDef = _renderer.GetWidgetDef(PanelWidgetId);
        if (panelDef != null)
        {
            panelDef.Width = _widgetW;
            panelDef.Height = _widgetH;
        }
    }

    /// <summary>
    /// Re-anchor the menu above the table's world position (uses the same
    /// _renderer.WorldToScreen helper that DamageNumbers / floating UI use).
    /// Called every frame from Update so the menu tracks camera pans + zoom.
    /// Recomputes _uiScale and the panel size before placing — at higher zoom
    /// the menu is bigger and lifted further above the table; at lower zoom
    /// it shrinks proportionally, like an in-world object.
    /// </summary>
    private void RepositionAboveTable()
    {
        if (_envIdx < 0 || _envIdx >= _envSystem.ObjectCount) return;
        if (_camera == null || _worldRenderer == null) return;

        // Scale the whole menu off the camera zoom so it feels world-anchored.
        // MenuSizeFactor is a global shrink/grow multiplier on top of zoom.
        _uiScale = (_camera.Zoom / BaseZoom) * MenuSizeFactor;
        var def = _envSystem.Defs[_envSystem.GetObject(_envIdx).DefIndex];
        ResizeToDef(def);

        var obj = _envSystem.GetObject(_envIdx);
        var anchor = _worldRenderer.WorldToScreen(new Vec2(obj.X, obj.Y), MenuAnchorWorldHeight, _camera);

        int desiredX = (int)anchor.X - _widgetW / 2;
        int desiredY = (int)anchor.Y - _widgetH;

        _screenX = Math.Clamp(desiredX, 8, _screenW - _widgetW - 8);
        _screenY = Math.Clamp(desiredY, 8, _screenH - _widgetH - 8);
    }

    public void Update(InputState input)
    {
        _lastInput = input;
        if (!_visible || _envIdx < 0) return;

        // Validate the table still exists and is alive.
        if (_envIdx >= _envSystem.ObjectCount || !_envSystem.GetObjectRuntime(_envIdx).Alive)
        {
            Close();
            return;
        }

        // Track the table in screen space — same idea as DamageNumber rendering
        // (both use Renderer.WorldToScreen). Anchor recomputes each frame so the
        // menu doesn't drift away from the table when the camera pans/zooms.
        RepositionAboveTable();

        // Escape closes
        if (input.WasKeyPressed(Keys.Escape))
        {
            Close();
            input.ConsumeMouse();
            return;
        }

        var def = _envSystem.Defs[_envSystem.GetObject(_envIdx).DefIndex];
        var ts = _envSystem.GetTableState(_envIdx);
        ts.EnsureSized(def.CorpseSlots, def.ItemSlots);

        int mx = (int)input.MousePos.X, my = (int)input.MousePos.Y;

        // Close X button hit-test
        var closeRect = GetCloseButtonRect();
        if (input.LeftPressed && closeRect.Contains(mx, my))
        {
            Close();
            input.ConsumeMouse();
            return;
        }

        // Start button hit-test
        var startRect = GetStartButtonRect();
        if (input.LeftPressed && startRect.Contains(mx, my))
        {
            if (CanStartCraft(def, ts) && StartCraftCallback != null)
            {
                if (StartCraftCallback(_envIdx))
                    input.ConsumeMouse();
            }
            else
            {
                input.ConsumeMouse(); // swallow even if can't start, to avoid misclicks pass-through
            }
            return;
        }

        // Right-click on table item slot returns the item to inventory.
        if (input.RightPressed)
        {
            for (int i = 0; i < def.ItemSlots; i++)
            {
                var r = GetItemSlotRect(def, i);
                if (r.Contains(mx, my) && !ts.ItemSlots[i].IsEmpty)
                {
                    string id = ts.ItemSlots[i].ItemID;
                    _inventory.AddItem(id, 1);
                    ts.ItemSlots[i] = default;
                    input.ConsumeMouse(); // shared L/R consumption flag
                    return;
                }
            }
        }

        // Left-click on a panel slot consumes the click (so the world doesn't get it).
        if (input.LeftPressed && ContainsMouse(mx, my))
            input.ConsumeMouse();
    }

    /// <summary>
    /// Try to move an item id from the player inventory into the table's first
    /// empty item slot. Decrements inventory by 1 on success. Returns true if
    /// a slot was filled. Game1 calls this when the player clicks an inventory
    /// item while the table menu is open.
    /// </summary>
    public bool TryDepositItem(string itemId)
    {
        if (!_visible || _envIdx < 0 || string.IsNullOrEmpty(itemId)) return false;
        if (_inventory.GetItemCount(itemId) <= 0) return false;
        int slot = Game.TableSystem.LoadItemIntoTable(_envSystem, _envIdx, itemId);
        if (slot < 0) return false;
        _inventory.RemoveItem(itemId, 1);
        return true;
    }

    /// <summary>
    /// Eject *all* items in this table's item slots back to the player inventory.
    /// Called when the table is destroyed or the table state is otherwise wiped.
    /// </summary>
    public void EjectItemsBack()
    {
        if (_envIdx < 0) return;
        var ts = _envSystem.GetTableState(_envIdx);
        for (int i = 0; i < ts.ItemSlots.Length; i++)
        {
            if (ts.ItemSlots[i].IsEmpty) continue;
            _inventory.AddItem(ts.ItemSlots[i].ItemID, 1);
            ts.ItemSlots[i] = default;
        }
    }

    public void Draw()
    {
        if (!_visible || _envIdx < 0) return;
        if (_envIdx >= _envSystem.ObjectCount) return;

        var def = _envSystem.Defs[_envSystem.GetObject(_envIdx).DefIndex];
        var ts = _envSystem.GetTableState(_envIdx);
        ts.EnsureSized(def.CorpseSlots, def.ItemSlots);

        // Outer panel chrome (background + frame from the widget skin).
        _renderer.DrawWidget(PanelWidgetId, _screenX, _screenY, "tablemenu");

        // Slot row
        DrawSlotRow(def, ts);

        // Action row (progress bar + Start button)
        DrawActionRow(def, ts);

        // Close X (top-right)
        DrawCloseButton();
    }

    // ─────────────────────────────────────────
    //  Layout helpers (single source of slot rects)
    // ─────────────────────────────────────────

    /// <summary>Anchor X for the start of the slot row (inside the panel padding).</summary>
    private int SlotRowX => _screenX + Scaled(PadX);
    /// <summary>Anchor Y for the slot row (just below the title bar / close X).</summary>
    private int SlotRowY => _screenY + Scaled(PadTop);

    private Rectangle GetCorpseSlotRect(int i)
    {
        int s = Scaled(SlotSize);
        return new(SlotRowX + i * (s + Scaled(SlotSpacing)), SlotRowY, s, s);
    }

    private Rectangle GetItemSlotRect(EnvironmentObjectDef def, int i)
    {
        int idx = def.CorpseSlots + i; // item slots come after corpse slots
        int s = Scaled(SlotSize);
        return new(SlotRowX + idx * (s + Scaled(SlotSpacing)), SlotRowY, s, s);
    }

    private Rectangle GetEssenceSlotRect(EnvironmentObjectDef def)
    {
        int idx = def.CorpseSlots + def.ItemSlots;
        int s = Scaled(SlotSize);
        return new(SlotRowX + idx * (s + Scaled(SlotSpacing)), SlotRowY, s, s);
    }

    /// <summary>Y coord of the action row (progress bar + Start button), below the slot label band.</summary>
    private int ActionRowY => _screenY + Scaled(PadTop) + Scaled(SlotSize) + Scaled(LabelHeight) + Scaled(RowGap);

    private Rectangle GetStartButtonRect()
    {
        // Start button is a small box at the right end of the action row. The
        // progress bar fills the rest of the action row width to the left of it.
        int btnW = Scaled(StartButtonWidth);
        int actionRight = _screenX + _widgetW - Scaled(PadX);
        return new(actionRight - btnW, ActionRowY, btnW, Scaled(ActionRowHeight));
    }

    private Rectangle GetProgressBarRect()
    {
        int actionLeft = _screenX + Scaled(PadX);
        int barRight = GetStartButtonRect().X - Scaled(8);
        return new(actionLeft, ActionRowY, barRight - actionLeft, Scaled(ActionRowHeight));
    }

    private Rectangle GetCloseButtonRect()
    {
        int sz = Scaled(CloseBtnSize);
        return new(_screenX + _widgetW - Scaled(PadX) - sz, _screenY + Scaled(CloseBtnOffsetY), sz, sz);
    }

    // ─────────────────────────────────────────
    //  Drawing
    // ─────────────────────────────────────────

    private void DrawSlotRow(EnvironmentObjectDef def, TableCraftState ts)
    {
        // Corpse slots
        for (int i = 0; i < def.CorpseSlots; i++)
        {
            var r = GetCorpseSlotRect(i);
            DrawSlotBackground(r, ts.CorpseSlots[i].IsEmpty);
            DrawSlotLabel(r, "Corpse");
            if (!ts.CorpseSlots[i].IsEmpty)
            {
                // Always lay down a brown "filled" base layer so the slot reads as
                // occupied even if the icon callback / sprite resolution fails. The
                // icon (if present) draws on top. Inset scales with menu size.
                int inset = Scaled(4);
                var inner = new Rectangle(r.X + inset, r.Y + inset, r.Width - inset * 2, r.Height - inset * 2);
                _batch.Draw(_pixel, inner, new Color(120, 90, 60, 200));

                // Defer to Game1 for the actual unit sprite — keeps the menu free
                // of atlas / sprite-frame plumbing.
                DrawUnitIconCallback?.Invoke(ts.CorpseSlots[i].SourceUnitDefID, r);
            }
        }
        // Item slots
        for (int i = 0; i < def.ItemSlots; i++)
        {
            var r = GetItemSlotRect(def, i);
            DrawSlotBackground(r, ts.ItemSlots[i].IsEmpty);
            DrawSlotLabel(r, $"Slot {i + 1}");
            if (!ts.ItemSlots[i].IsEmpty)
            {
                var item = _items.Get(ts.ItemSlots[i].ItemID);
                if (item != null && !string.IsNullOrEmpty(item.Icon))
                    DrawIconAt(r, item.Icon);
            }
        }
        // Essence
        var er = GetEssenceSlotRect(def);
        DrawSlotBackground(er, false);
        DrawSlotLabel(er, "Essence");
        DrawSlotCenterText(er, $"{_resources.Essence}/{_resources.MaxEssence}");
    }

    private void DrawActionRow(EnvironmentObjectDef def, TableCraftState ts)
    {
        // Progress bar
        var prog = GetProgressBarRect();
        _batch.Draw(_pixel, prog, new Color(20, 20, 25, 220));
        DrawBorder(prog, new Color(160, 160, 160, 180), 1);

        float progress = (def.ProcessTime > 0f && ts.Crafting)
            ? Math.Clamp(ts.CraftTimer / def.ProcessTime, 0f, 1f)
            : 0f;
        if (progress > 0f)
        {
            int fillW = (int)(prog.Width * progress);
            _batch.Draw(_pixel, new Rectangle(prog.X, prog.Y, fillW, prog.Height),
                new Color(120, 200, 100, 230));
        }

        // Start button
        var start = GetStartButtonRect();
        bool canStart = CanStartCraft(def, ts);
        Color btnFill = ts.Crafting
            ? new Color(60, 60, 70, 230)
            : canStart ? new Color(80, 140, 80, 230) : new Color(70, 50, 50, 220);
        _batch.Draw(_pixel, start, btnFill);
        DrawBorder(start, new Color(220, 220, 220, 220), 1);
        DrawTextCentered(start, ts.Crafting ? "..." : "Start");
    }

    private void DrawCloseButton()
    {
        var r = GetCloseButtonRect();
        bool hover = _lastInput != null && r.Contains((int)_lastInput.MousePos.X, (int)_lastInput.MousePos.Y);
        _batch.Draw(_pixel, r, hover ? new Color(160, 60, 60, 230) : new Color(80, 30, 30, 200));
        DrawBorder(r, new Color(220, 220, 220, 220), 1);

        // Draw the X as two diagonal lines instead of font text. Reasons:
        //  - Pixel-art style match: the rest of the UI uses 1-px borders / flat
        //    fills; an anti-aliased font glyph at ~18 px reads as soft/fuzzy and
        //    visually clashes.
        //  - Predictable centering: glyph-metric centering via MeasureString is
        //    slightly off because side-bearing differs from visible-bounds.
        //  - Easy to scale / restyle: inset, color, thickness all live in one place.
        int inset = Scaled(CloseBtnInset);
        var xColor = hover ? Color.White : new Color(220, 220, 220, 230);
        var tl = new Vector2(r.X + inset, r.Y + inset);
        var tr = new Vector2(r.X + r.Width - inset, r.Y + inset);
        var bl = new Vector2(r.X + inset, r.Y + r.Height - inset);
        var br = new Vector2(r.X + r.Width - inset, r.Y + r.Height - inset);
        // Stroke thickness scales with UI scale (clamped ≥1) so the X stays
        // visually proportional to the button at any zoom.
        int thick = Math.Max(1, Scaled(2));
        for (int t = 0; t < thick; t++)
        {
            DrawUtils.DrawLine(_batch, _pixel, new Vector2(tl.X + t, tl.Y), new Vector2(br.X + t, br.Y), xColor);
            DrawUtils.DrawLine(_batch, _pixel, new Vector2(tr.X - t, tr.Y), new Vector2(bl.X - t, bl.Y), xColor);
        }
    }

    /// <summary>
    /// Gates for the Start button:
    ///   - No corpse → can't start
    ///   - Fresh craft (ts.Crafting=false) → must have enough essence to start
    ///   - Resuming (ts.Crafting=true) → essence already spent, always allowed
    /// Hides the actively-channeling state so the user doesn't double-click and
    /// reset the WalkToSite walk; that gating lives in StartTableCraft itself.
    /// </summary>
    private bool CanStartCraft(EnvironmentObjectDef def, TableCraftState ts)
    {
        if (!ts.HasAnyCorpse()) return false;
        if (ts.Crafting) return true; // resume — already paid for
        return _resources.Essence >= def.EssenceCost;
    }

    private void DrawSlotBackground(Rectangle r, bool isEmpty)
    {
        var fill = isEmpty ? new Color(35, 35, 40, 220) : new Color(55, 55, 60, 230);
        _batch.Draw(_pixel, r, fill);
        DrawBorder(r, new Color(180, 180, 180, 200), 1);
    }

    private void DrawSlotLabel(Rectangle r, string text)
    {
        if (_font == null) return;
        // Text scales with _uiScale so labels grow/shrink with the menu instead
        // of staying fixed-pixel. Clamp prevents overflow when the box is narrow.
        var size = _font.MeasureString(text);
        float scale = MathF.Min(_uiScale, (r.Width - 4) / size.X);
        var pos = new Vector2((int)(r.X + (r.Width - size.X * scale) / 2f),
                              (int)(r.Y + r.Height + Scaled(LabelOffsetY)));
        _batch.DrawString(_font, text, pos, new Color(220, 220, 220, 230),
            0f, Vector2.Zero, scale, SpriteEffects.None, 0f);
    }

    private void DrawSlotCenterText(Rectangle r, string text)
    {
        if (_font == null) return;
        var size = _font.MeasureString(text);
        float scale = MathF.Min(_uiScale, (r.Width - 6) / size.X);
        var pos = new Vector2((int)(r.X + (r.Width - size.X * scale) / 2f),
                              (int)(r.Y + (r.Height - size.Y * scale) / 2f));
        _batch.DrawString(_font, text, pos, Color.White,
            0f, Vector2.Zero, scale, SpriteEffects.None, 0f);
    }

    private void DrawTextCentered(Rectangle r, string text)
    {
        if (_font == null) return;
        var size = _font.MeasureString(text);
        float scale = MathF.Min(_uiScale, (r.Width - 4) / Math.Max(1f, size.X));
        var pos = new Vector2((int)(r.X + (r.Width - size.X * scale) / 2f),
                              (int)(r.Y + (r.Height - size.Y * scale) / 2f));
        _batch.DrawString(_font, text, pos, Color.White,
            0f, Vector2.Zero, scale, SpriteEffects.None, 0f);
    }

    private void DrawIconAt(Rectangle r, string iconPath)
    {
        // The widget renderer caches icon textures — reuse via SetImage path.
        // Pulled into the slot via a synthesized sub-instance so multiple table
        // openings don't conflict. Inset scales with the menu so icons keep
        // proportional padding inside their slot at any zoom.
        int inset = Scaled(4);
        _renderer.DrawIcon(iconPath, r.X + inset, r.Y + inset, r.Width - inset * 2, r.Height - inset * 2);
    }

    private void DrawBorder(Rectangle r, Color c, int t = 1)
    {
        _batch.Draw(_pixel, new Rectangle(r.X, r.Y, r.Width, t), c);
        _batch.Draw(_pixel, new Rectangle(r.X, r.Y + r.Height - t, r.Width, t), c);
        _batch.Draw(_pixel, new Rectangle(r.X, r.Y + t, t, r.Height - t * 2), c);
        _batch.Draw(_pixel, new Rectangle(r.X + r.Width - t, r.Y + t, t, r.Height - t * 2), c);
    }

    /// <summary>Mouse-over-menu test (callers use to suppress world clicks).</summary>
    public bool ContainsMouse(int mouseX, int mouseY)
    {
        if (!_visible) return false;
        return mouseX >= _screenX && mouseX < _screenX + _widgetW &&
               mouseY >= _screenY && mouseY < _screenY + _widgetH;
    }
}
