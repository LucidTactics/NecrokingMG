using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using Necroking.Core;
using Necroking.Data;
using Necroking.Data.Registries;
using Necroking.GameSystems;
using Necroking.World;

namespace Necroking.Render;

/// <summary>
/// Renders the in-game HUD: HP/mana bars, spell bars, tooltips, unit counts,
/// time controls, combat log. Extracted from Game1 to reduce its size.
/// </summary>
public partial class HUDRenderer
{

    // Layout constants
    private const int BarWidth = 200;
    private const int BarHeight = 16;
    private const int BarX = 10;
    private const int HpBarY = 32;
    private const int ManaBarY = 50;

    // Two rows of grimoire-style frame slots, sized so both rows span the same
    // total width (bottom: 4 boxes; top: 6 = 4 secondary spells + 2 potions),
    // centred and aligned. Bottom box fits the 96px spell icon at an exact 2:1
    // downscale (48px) — pixel-perfect; top boxes are smaller with scaled icons.
    // 4*60 + 3*6 = 258 == 6*38 + 5*6, so both offsets are 258/2 = 129.
    private const int PrimarySlotW = 60;
    private const int PrimarySlotH = 60;
    private const int PrimaryBarOffsetX = 129; // screenW/2 - this
    // Sits the primary row's bottom edge ~2px above the screen bottom
    // (slot height 60 + 1px gap; the frame's own margin makes ~2 visible).
    // The secondary row stacks above it.
    private const int PrimaryBarBottomOffset = 61; // screenH - this

    private const int SecondarySlotW = 38;
    private const int SecondarySlotH = 38;
    private const int SecondaryBarOffsetX = 129;
    private const int SecondaryBarGap = 6;
    private const int SlotSpacing = 6;
    private const int SlotBorderHeight = 2;

    // The slot chrome is the SpellSlot widget (Background = fancy_inner parchment,
    // Frame = spider_frame border, both harmonized) — editable in the UI editor.
    // The HUD draws its background + frame at each slot's size around the icon.
    private const string SpellSlotWidget = "SpellSlot";
    private const float SlotIconRatio = 0.80f; // icon size as a fraction of the box

    private const int DropdownItemH = 20;
    private const int DropdownWidth = 164;

    private const int TcBtnW = 32;
    private const int TcBtnH = 22;
    private const int TcGap = 2;
    private const int TcCount = 6;
    private const int TcSpeedTextReserve = 56; // room on the right for the "Paused"/"1.5x" readout
    private const int TcRightMargin = 8;

    /// <summary>Time-control button block layout, shared by DrawTimeControls and
    /// HitTestTimeControls so they never desync. The block is right-aligned with
    /// room reserved for the speed-text readout (so it can't crop off-screen), and
    /// bottom-aligned like the spell bar (~2px gap).</summary>
    private static void TimeControlLayout(int screenW, int screenH, out int baseX, out int baseY)
    {
        int buttonsW = TcBtnW + TcGap + TcCount * TcBtnW + (TcCount - 1) * TcGap;
        baseX = screenW - (buttonsW + TcSpeedTextReserve + TcRightMargin);
        baseY = screenH - TcBtnH - 2;
    }

    // Colors
    private static readonly Color HpBarBg = new(60, 20, 20);
    private static readonly Color HpBarFg = new(200, 40, 40);
    private static readonly Color ManaBarBg = new(40, 40, 80);
    private static readonly Color ManaBarFg = new(80, 80, 220);
    private static readonly Color SlotFilledBg = new(50, 50, 70, 200);
    private static readonly Color SlotEmptyBg = new(30, 30, 40, 150);
    private static readonly Color SlotBorder = new(100, 100, 130, 200);
    private static readonly Color SecFilledBg = new(45, 50, 65, 180);
    private static readonly Color SecEmptyBg = new(25, 25, 35, 120);
    private static readonly Color SecBorder = new(90, 90, 120, 180);
    private static readonly Color KeyLabelColor = new(0x3e, 0x31, 0x11); // dark brown, reads on the parchment slot
    private static readonly Color SpellNameColor = new(200, 200, 220);
    private static readonly Color CooldownOverlay = new(0, 0, 0, 150);
    private static readonly Color CooldownText = new(255, 200, 100);
    private static readonly Color LowManaOverlay = new(80, 0, 0, 80);
    private static readonly Color DropdownBg = new(20, 20, 35, 100);
    private static readonly Color DropdownNoneColor = new(150, 150, 170);
    private static readonly Color DropdownSelectedColor = new(255, 220, 100);
    private static readonly Color DropdownNormalColor = new(200, 200, 220);
    private static readonly Color DropdownHoverBg = new(80, 80, 120, 120);
    private static readonly Color TooltipBg = new(15, 15, 25, 220);
    private static readonly Color TooltipBorder = new(100, 100, 160);
    private static readonly Color TooltipText = new(220, 220, 240);
    private static readonly Color ControlHintColor = new(120, 120, 140, 200);
    private static readonly Color InventoryHintColor = new(200, 220, 180);
    private static readonly Color MaterialColor = new(200, 160, 255);
    private static readonly Color PotionQtyColor = new(255, 255, 200);
    private static readonly Color PotionEmptyColor = new(100, 100, 100, 120);

    // Dependencies (set via Init)
    private SpriteBatch _batch = null!;
    private Texture2D _pixel = null!;
    private SpriteFont? _font;
    private SpriteFont? _smallFont;
    // Shares Game1's SpriteBatch, so its element/icon draws land in the same
    // pass. Used to reuse the grimoire's frame + parchment backing for slots.
    private UI.RuntimeWidgetRenderer? _widgets;
    private InputState _input = new();

    public void Init(SpriteBatch batch, Texture2D pixel, SpriteFont? font, SpriteFont? smallFont,
        UI.RuntimeWidgetRenderer? widgets = null)
    {
        _batch = batch;
        _pixel = pixel;
        _font = font;
        _smallFont = smallFont;
        _widgets = widgets;
    }

    /// <summary>Set the input state reference for hover detection in draw calls.</summary>
    public void SetInput(InputState input) => _input = input;

    /// <summary>Draw the complete HUD.</summary>
    public void Draw(int screenW, int screenH, Simulation sim, GameData gameData,
        Inventory inventory, bool inventoryVisible,
        SpellBarState primaryBar, SpellBarState secondaryBar,
        int spellDropdownSlot, int secondaryDropdownSlot,
        float timeScale, int hoveredObjectIdx, EnvironmentSystem envSystem,
        Action<string, int, int> drawSpellCategoryIcon, bool paused = false)
    {
        int necroIdx = FindNecromancer(sim);

        DrawStatusBars(necroIdx, sim);

        int primaryY = screenH - PrimaryBarBottomOffset;
        DrawSpellBar(screenW, primaryY, PrimarySlotW, PrimarySlotH, PrimaryBarOffsetX,
            new[] { "Q", "E", "LC", "RC" }, primaryBar, sim, gameData, inventory, drawSpellCategoryIcon);

        int secondaryY = primaryY - SecondarySlotH - SecondaryBarGap;
        DrawSpellBar(screenW, secondaryY, SecondarySlotW, SecondarySlotH, SecondaryBarOffsetX,
            new[] { "1", "2", "3", "4", "5", "6" }, secondaryBar, sim, gameData, inventory, drawSpellCategoryIcon);

        // Draw all dropdowns after all bars so they render on top
        DrawSpellDropdown(screenW, primaryY, PrimarySlotW, PrimaryBarOffsetX,
            spellDropdownSlot, primaryBar, gameData);
        DrawSpellDropdown(screenW, secondaryY, SecondarySlotW, SecondaryBarOffsetX,
            secondaryDropdownSlot, secondaryBar, gameData);

        DrawObjectTooltip(hoveredObjectIdx, envSystem, sim, gameData, screenW, screenH);
        // Controls hint intentionally omitted — overlapped the FPS/zoom bottom-
        // left readout. Re-enable if we add a menu page for it.
        DrawTimeControls(screenW, screenH, timeScale, gameData, paused);
        DrawHordeCaps(screenW, sim, gameData);
        DrawCombatLog(screenW, screenH, sim, gameData);
    }

    // ═══════════════════════════════════════
    //  Horde Caps (top-right)
    // ═══════════════════════════════════════

    /// <summary>Top-right readouts: "Monsters used/cap", "Humans used/cap".
    /// Each line only renders when its cap > 0 (i.e. the player has unlocked
    /// that category via the skill tree). Colour goes red when at-or-over cap
    /// so the player notices the gate.</summary>
    private void DrawHordeCaps(int screenW, Simulation sim, GameData gameData)
    {
        if (_smallFont == null) return;
        var necro = sim.NecroState;
        int necroIdx = FindNecromancer(sim);

        int humanCap = HordeCapTracker.GetCap(sim.Units, necroIdx, necro, UndeadCategory.Human);
        int monsterCap = HordeCapTracker.GetCap(sim.Units, necroIdx, necro, UndeadCategory.Monster);
        if (monsterCap <= 0 && humanCap <= 0) return;

        int x = screenW - 110;
        int y = 10;
        const int lineH = 16;

        if (humanCap > 0)
        {
            int used = HordeCapTracker.CountUsed(sim.Units, gameData, UndeadCategory.Human);
            Color col = used >= humanCap
                ? new Color(255, 90, 90)
                : new Color(220, 200, 180);
            Text(_smallFont, $"Humans  {used}/{humanCap}", new Vector2(x, y), col);
            y += lineH;
        }
        if (monsterCap > 0)
        {
            int used = HordeCapTracker.CountUsed(sim.Units, gameData, UndeadCategory.Monster);
            Color col = used >= monsterCap
                ? new Color(255, 90, 90)
                : new Color(200, 220, 180);
            Text(_smallFont, $"Monsters {used}/{monsterCap}", new Vector2(x, y), col);
        }
    }

    // ═══════════════════════════════════════
    //  Status Bars
    // ═══════════════════════════════════════

    private void DrawStatusBars(int necroIdx, Simulation sim)
    {
        // Gather HP/Mana values + fractions, then hand off to the selected skin.
        int hp = 0, maxHp = 0;
        float hpFrac = 0f;
        if (necroIdx >= 0)
        {
            var stats = sim.Units[necroIdx].Stats;
            hp = stats.HP; maxHp = stats.MaxHP;
            hpFrac = maxHp > 0 ? (float)hp / maxHp : 0f;
        }
        float maxManaEff = sim.NecroState.MaxMana
            + (necroIdx >= 0 ? BuffSystem.SumExtraAdd(sim.Units, necroIdx, "MaxMana") : 0f);
        float manaFrac = maxManaEff > 0 ? sim.NecroState.Mana / maxManaEff : 0f;
        int mana = (int)sim.NecroState.Mana, maxMana = (int)maxManaEff;

        // Taller bars than the original 16px so thin nine-slice frames render
        // without crushing their corners. Colour implies HP vs Mana, so the text
        // is just the value (no "HP:"/"Mana:" prefix).
        const int barH = 24;
        var hpRect = new Rectangle(BarX, 30, BarWidth, barH);
        var manaRect = new Rectangle(BarX, 30 + barH + 4, BarWidth, barH);
        string hpLabel = necroIdx >= 0 ? $"{hp}/{maxHp}" : "";
        string manaLabel = $"{mana}/{maxMana}";

        if (necroIdx >= 0) DrawStatBar(hpRect, hpFrac, HpFillA, HpFillB, hpLabel);
        DrawStatBar(manaRect, manaFrac, ManaFillA, ManaFillB, manaLabel);
    }


    // ═══════════════════════════════════════
    //  Spell Bars (unified for primary + secondary)
    // ═══════════════════════════════════════

    private void DrawSpellBar(int screenW, int barY, int slotW, int slotH, int centerOffset,
        string[] keys, SpellBarState bar, Simulation sim, GameData gameData,
        Inventory inventory, Action<string, int, int> drawCategoryIcon)
    {
        bool isSecondary = slotW < PrimarySlotW;

        for (int s = 0; s < keys.Length; s++)
        {
            int slotX = screenW / 2 - centerOffset + s * (slotW + SlotSpacing);
            var slot = new Rectangle(slotX, barY, slotW, slotH);
            var inner = SlotInterior(slot);
            bool hasSpell = s < bar.Slots.Length && !string.IsNullOrEmpty(bar.Slots[s].SpellID);

            int mx = (int)_input.MousePos.X, my = (int)_input.MousePos.Y;
            bool hovered = slot.Contains(mx, my);

            string slotSpellId = hasSpell ? bar.Slots[s].SpellID : "";
            SpellDef? spell = hasSpell && slotSpellId != "melee_gather"
                ? gameData.Spells.Get(slotSpellId) : null;

            // Frame box: parchment backing + icon + frame (grimoire chrome).
            DrawFramedSlot(slot, innerRect =>
            {
                if (spell != null && !string.IsNullOrEmpty(spell.Icon))
                    _widgets?.DrawIcon(spell.Icon, innerRect.X, innerRect.Y, innerRect.Width, innerRect.Height);
                else if (spell != null && !isSecondary)
                    drawCategoryIcon(spell.Category, innerRect.Center.X, innerRect.Center.Y);
            });

            if (hovered)
                _batch.Draw(_pixel, inner, Color.White * 0.12f);

            // Cooldown sweep (over the icon interior).
            if (spell != null)
            {
                float cd = sim.NecroState.GetCooldown(spell.Id);
                if (cd > 0f)
                {
                    float cdFrac = MathF.Min(cd / MathF.Max(spell.Cooldown, 0.1f), 1f);
                    int cdH = (int)(inner.Height * cdFrac);
                    _batch.Draw(_pixel, new Rectangle(inner.X, inner.Bottom - cdH, inner.Width, cdH), CooldownOverlay);
                    if (!isSecondary && _smallFont != null)
                        Text(_smallFont, $"{cd:F1}", new Vector2(inner.Center.X - 10, inner.Center.Y - 6), CooldownText);
                }
                if (sim.NecroState.Mana < spell.ManaCost)
                    _batch.Draw(_pixel, inner, LowManaOverlay);

                // Consumable charges (potion-spells): show the inventory count,
                // grey out at 0.
                if (!string.IsNullOrEmpty(spell.ConsumesItem))
                {
                    int qty = inventory.GetItemCount(spell.ConsumesItem);
                    if (qty <= 0) _batch.Draw(_pixel, inner, PotionEmptyColor);
                    if (_smallFont != null)
                    {
                        string q = qty.ToString();
                        var qs = _smallFont.MeasureString(q);
                        Text(_smallFont, q, new Vector2(slot.Right - qs.X - 3, slot.Bottom - qs.Y - 2), PotionQtyColor);
                    }
                }
            }

            if (_smallFont == null) continue;
            // melee_gather has no icon — label it.
            if (slotSpellId == "melee_gather")
                Text(_smallFont, "Melee", new Vector2(inner.X + 1, inner.Center.Y - 6), SpellNameColor);
            // Hotkey label, just inside the frame at the parchment's top-left.
            Text(_smallFont, keys[s], new Vector2(inner.X + 3, inner.Y), KeyLabelColor);
        }
    }

    /// <summary>The icon area inside a slot frame (centered, SlotIconRatio of the
    /// box) — matches the grimoire frame's transparent interior.</summary>
    private static Rectangle SlotInterior(Rectangle slot)
    {
        int icon = (int)MathF.Round(slot.Width * SlotIconRatio);
        int off = (slot.Width - icon) / 2;
        return new Rectangle(slot.X + off, slot.Y + off, icon, icon);
    }

    /// <summary>Draw a slot using the SpellSlot widget chrome: its background
    /// (parchment) at the slot size, the interior content (icon, drawn by the
    /// callback) between, then its frame on top. Falls back to a plain box if the
    /// widget renderer isn't available.</summary>
    private void DrawFramedSlot(Rectangle slot, Action<Rectangle> drawInterior)
    {
        var inner = SlotInterior(slot);
        if (_widgets == null)
        {
            _batch.Draw(_pixel, slot, SlotEmptyBg);
            drawInterior(inner);
            return;
        }
        _widgets.DrawWidgetBackground(SpellSlotWidget, slot);
        drawInterior(inner);
        _widgets.DrawWidgetFrameLayer(SpellSlotWidget, slot);
    }

    private void DrawSpellDropdown(int screenW, int barY, int slotW, int centerOffset,
        int openSlot, SpellBarState bar, GameData gameData)
    {
        if (openSlot < 0 || openSlot >= 4 || _smallFont == null) return;

        int slotX = screenW / 2 - centerOffset + openSlot * (slotW + SlotSpacing);
        var allSpells = gameData.Spells.GetIDs();
        int ddH = (allSpells.Count + 1) * DropdownItemH;
        int ddY = barY - 10;

        int ddLeft = slotX - 2;
        _batch.Draw(_pixel, new Rectangle(ddLeft, ddY - ddH - 2, DropdownWidth, ddH + 4), DropdownBg);

        int mx = (int)_input.MousePos.X, my = (int)_input.MousePos.Y;
        int hoverIdx = -1;
        if (mx >= ddLeft && mx < ddLeft + DropdownWidth && my >= ddY - ddH && my < ddY)
            hoverIdx = (ddY - my) / DropdownItemH;

        // (None) item — index 0
        if (hoverIdx == 0)
            _batch.Draw(_pixel, new Rectangle(ddLeft, ddY - DropdownItemH, DropdownWidth, DropdownItemH), DropdownHoverBg);
        Text(_smallFont, "(None)", new Vector2(slotX + 4, ddY - DropdownItemH), DropdownNoneColor);

        for (int si = 0; si < allSpells.Count; si++)
        {
            var spDef = gameData.Spells.Get(allSpells[si]);
            int itemY = ddY - (si + 2) * DropdownItemH;
            if (hoverIdx == si + 1)
                _batch.Draw(_pixel, new Rectangle(ddLeft, itemY, DropdownWidth, DropdownItemH), DropdownHoverBg);
            string label = spDef != null ? $"{spDef.DisplayName} [{spDef.Category}]" : allSpells[si];
            Color labelColor = bar.Slots[openSlot].SpellID == allSpells[si]
                ? DropdownSelectedColor : DropdownNormalColor;
            Text(_smallFont, label, new Vector2(slotX + 4, itemY), labelColor);
        }
    }

    // ═══════════════════════════════════════
    //  Hit Testing (shared layout with draw)
    // ═══════════════════════════════════════

    /// <summary>
    /// Get the Y position and layout params for the primary spell bar.
    /// Returns (barY, slotW, slotH, centerOffset).
    /// </summary>
    public (int barY, int slotW, int slotH, int centerOffset) GetPrimaryBarLayout(int screenH)
    {
        int barY = screenH - PrimaryBarBottomOffset;
        return (barY, PrimarySlotW, PrimarySlotH, PrimaryBarOffsetX);
    }

    /// <summary>
    /// Get the Y position and layout params for the secondary spell bar.
    /// Returns (barY, slotW, slotH, centerOffset).
    /// </summary>
    public (int barY, int slotW, int slotH, int centerOffset) GetSecondaryBarLayout(int screenH)
    {
        int primaryY = screenH - PrimaryBarBottomOffset;
        int barY = primaryY - SecondarySlotH - SecondaryBarGap;
        return (barY, SecondarySlotW, SecondarySlotH, SecondaryBarOffsetX);
    }

    /// <summary>
    /// Hit-test a spell bar slot. Returns slot index 0-3, or -1 if not hit.
    /// Uses the same layout constants as drawing.
    /// </summary>
    public int HitTestBarSlot(int screenW, int barY, int slotW, int slotH, int centerOffset,
        int mouseX, int mouseY, int slotCount = 4)
    {
        if (mouseY < barY || mouseY >= barY + slotH) return -1;
        for (int s = 0; s < slotCount; s++)
        {
            int slotX = screenW / 2 - centerOffset + s * (slotW + SlotSpacing);
            if (mouseX >= slotX && mouseX < slotX + slotW)
                return s;
        }
        return -1;
    }

    /// <summary>
    /// Hit-test a spell dropdown. Returns item index (0 = None, 1+ = spell index),
    /// or -1 if click is outside the dropdown.
    /// Uses the exact same layout as DrawSpellDropdown.
    /// </summary>
    public int HitTestSpellDropdown(int screenW, int barY, int slotW, int centerOffset,
        int openSlot, int totalSpells, int mouseX, int mouseY)
    {
        if (openSlot < 0 || openSlot >= 4) return -1;

        int slotX = screenW / 2 - centerOffset + openSlot * (slotW + SlotSpacing);
        int ddH = (totalSpells + 1) * DropdownItemH;
        int ddY = barY - 10; // Same offset as DrawSpellDropdown
        int ddLeft = slotX - 2;

        if (mouseX < ddLeft || mouseX >= ddLeft + DropdownWidth) return -1;
        if (mouseY < ddY - ddH || mouseY >= ddY) return -1;

        return (ddY - mouseY) / DropdownItemH;
    }

    /// <summary>
    /// Hit-test time control buttons. Returns:
    /// -1 = not hit, -2 = pause button, 0-5 = speed preset index.
    /// Uses the same layout constants as DrawTimeControls.
    /// </summary>
    public int HitTestTimeControls(int screenW, int screenH, int mouseX, int mouseY)
    {
        TimeControlLayout(screenW, screenH, out int tcBaseX, out int tcBaseY);

        if (mouseY < tcBaseY || mouseY >= tcBaseY + TcBtnH) return -1;

        // Pause button (leftmost)
        if (mouseX >= tcBaseX && mouseX < tcBaseX + TcBtnW)
            return -2;

        // Speed buttons (right of pause)
        int speedBaseX = tcBaseX + TcBtnW + TcGap;
        for (int s = 0; s < TcCount; s++)
        {
            int bx = speedBaseX + s * (TcBtnW + TcGap);
            if (mouseX >= bx && mouseX < bx + TcBtnW)
                return s;
        }
        return -1;
    }

    /// <summary>Speed presets matching time control button indices.</summary>
    public static readonly float[] TimeControlSpeeds = { 0.1f, 0.25f, 0.5f, 1.0f, 1.5f, 2.0f };

    // ═══════════════════════════════════════
    //  Tooltips & Info
    // ═══════════════════════════════════════

    /// <summary>Floating cursor tooltip for the hovered ground object. Branches
    /// on the object kind: buildings show name/HP/owner/processing, foragable
    /// items show name/category/description. Which kinds are hoverable at all is
    /// gated upstream in Game1 (Tooltips settings) — this just renders whatever
    /// index it's handed.</summary>
    private void DrawObjectTooltip(int hoveredIdx, EnvironmentSystem envSystem, Simulation sim,
        GameData gameData, int screenW, int screenH)
    {
        if (hoveredIdx < 0 || hoveredIdx >= envSystem.ObjectCount || _smallFont == null) return;

        var obj = envSystem.GetObject(hoveredIdx);
        var def = envSystem.Defs[obj.DefIndex];

        string[] lines;
        if (def.IsBuilding)
        {
            var rt = envSystem.GetObjectRuntime(hoveredIdx);
            var proc = envSystem.GetProcessState(hoveredIdx);
            string ownerStr = rt.Owner switch { 0 => "Undead", 1 => "Neutral", _ => "Human" };
            string procStr = proc.Processing ? $"Processing ({proc.ProcessTimer:F1}s)" : "Idle";
            lines = new[] {
                def.Name.Length > 0 ? def.Name : def.Id,
                $"HP: {rt.HP}/{def.BuildingMaxHP}",
                $"Owner: {ownerStr}",
                procStr
            };
        }
        else if (def.IsBerryBush)
        {
            var rt = envSystem.GetObjectRuntime(hoveredIdx);
            string stateStr = rt.BerryState switch
            {
                BerryState.Berries  => "Has berries",
                BerryState.Poisoned => "Poisoned",
                _                   => "Picked (no berries)",
            };
            lines = new[] {
                def.Name.Length > 0 ? def.Name : def.Id,
                stateStr
            };
        }
        else if (def.IsForagable)
        {
            var itemDef = !string.IsNullOrEmpty(def.ForagableType) ? gameData.Items.Get(def.ForagableType) : null;
            string title = itemDef != null && itemDef.DisplayName.Length > 0 ? itemDef.DisplayName
                         : def.Name.Length > 0 ? def.Name : def.ForagableType;
            var ls = new List<string> { title };
            if (itemDef != null && !string.IsNullOrEmpty(itemDef.Category))
                ls.Add(itemDef.Category);
            if (itemDef != null && !string.IsNullOrEmpty(itemDef.Description))
                ls.Add(itemDef.Description);
            lines = ls.ToArray();
        }
        else return;

        DrawCursorTooltip(lines, screenW, screenH);
    }

    /// <summary>Draw a small text-box tooltip anchored to the cursor, auto-sized to
    /// the widest line and flipped/clamped to stay on screen.</summary>
    private void DrawCursorTooltip(string[] lines, int screenW, int screenH)
    {
        if (_smallFont == null || lines.Length == 0) return;
        const int lineH = 16;
        float maxW = 0f;
        foreach (var l in lines)
        {
            float w = _smallFont.MeasureString(l).X;
            if (w > maxW) maxW = w;
        }
        int ttW = (int)maxW, ttH = lines.Length * lineH;
        int mx = (int)_input.MousePos.X, my = (int)_input.MousePos.Y;
        // Default: above-right of the cursor; flip when it would clip the edge.
        int ttX = mx + 16, ttY = my - ttH - 12;
        if (ttX + ttW + 4 > screenW) ttX = mx - ttW - 12;
        if (ttX < 4) ttX = 4;
        if (ttY < 4) ttY = my + 20;

        _batch.Draw(_pixel, new Rectangle(ttX - 4, ttY - 4, ttW + 8, ttH + 8), TooltipBg);
        _batch.Draw(_pixel, new Rectangle(ttX - 4, ttY - 4, ttW + 8, 2), TooltipBorder);
        for (int i = 0; i < lines.Length; i++)
            Text(_smallFont, lines[i], new Vector2(ttX, ttY + i * lineH), TooltipText);
    }

    private void DrawControlsHint(int screenH)
    {
        Text(_smallFont, "WASD: Move | Scroll: Zoom | ESC: Menu | Space: Jump | G: Ghost | Shift: Run",
            new Vector2(10, screenH - 22), ControlHintColor);
    }

    // ═══════════════════════════════════════
    //  Time Controls
    // ═══════════════════════════════════════

    private void DrawTimeControls(int screenW, int screenH, float timeScale, GameData gameData, bool paused)
    {
        if (!gameData.Settings.General.ShowTimeControls || _smallFont == null) return;

        ReadOnlySpan<float> speeds = stackalloc float[] { 0.1f, 0.25f, 0.5f, 1.0f, 1.5f, 2.0f };
        string[] labels = { "<<<", "<<", "<", "=", ">", ">>" };
        int pauseW = TcBtnW;
        TimeControlLayout(screenW, screenH, out int baseX, out int baseY);

        int mx = (int)_input.MousePos.X, my = (int)_input.MousePos.Y;

        // Pause button (left of speed buttons)
        {
            bool hover = mx >= baseX && mx < baseX + pauseW
                      && my >= baseY && my < baseY + TcBtnH;
            Color bg = paused ? new Color(160, 70, 70, 220)
                     : hover  ? new Color(50, 60, 80, 180)
                              : new Color(20, 20, 30, 140);
            _batch.Draw(_pixel, new Rectangle(baseX, baseY, pauseW, TcBtnH), bg);
            string pauseLabel = paused ? ">" : "||";
            var labelSize = _smallFont.MeasureString(pauseLabel);
            Text(_smallFont, pauseLabel,
                new Vector2(baseX + pauseW / 2f - labelSize.X / 2f, baseY + TcBtnH / 2f - labelSize.Y / 2f),
                Color.White);
        }

        int speedBaseX = baseX + pauseW + TcGap;
        for (int s = 0; s < TcCount; s++)
        {
            int bx = speedBaseX + s * (TcBtnW + TcGap);
            bool hover = mx >= bx && mx < bx + TcBtnW
                      && my >= baseY && my < baseY + TcBtnH;
            bool active = !paused && MathF.Abs(timeScale - speeds[s]) < 0.001f;
            Color bg = active ? new Color(70, 100, 160, 220)
                     : hover  ? new Color(50, 60, 80, 180)
                              : new Color(20, 20, 30, 140);
            _batch.Draw(_pixel, new Rectangle(bx, baseY, TcBtnW, TcBtnH), bg);
            var labelSize = _smallFont.MeasureString(labels[s]);
            Text(_smallFont, labels[s],
                new Vector2(bx + TcBtnW / 2f - labelSize.X / 2f, baseY + TcBtnH / 2f - labelSize.Y / 2f),
                Color.White);
        }

        string speedText = paused ? "Paused" : $"{timeScale:G2}x";
        Text(_smallFont, speedText, new Vector2(speedBaseX + TcCount * TcBtnW + (TcCount - 1) * TcGap + 6, baseY + 5),
            paused ? new Color(200, 100, 100, 220) : new Color(180, 180, 200, 200));
    }

    // ═══════════════════════════════════════
    //  Combat Log
    // ═══════════════════════════════════════

    private void DrawCombatLog(int screenW, int screenH, Simulation sim, GameData gameData)
    {
        if (!gameData.Settings.General.CombatLogEnabled) return;

        var entries = sim.CombatLog.Entries;
        int maxLines = gameData.Settings.General.CombatLogLines;
        float fadeTime = gameData.Settings.General.CombatLogFadeTime;
        int logBaseY = screenH - 40;
        int linesDrawn = 0;

        for (int li = entries.Count - 1; li >= 0 && linesDrawn < maxLines; li--)
        {
            var e = entries[li];
            float age = sim.GameTime - e.Timestamp;
            if (age > fadeTime * 3f) continue;
            float fade = age < fadeTime ? 1f : MathF.Max(0f, 1f - (age - fadeTime) / fadeTime);
            byte alpha = (byte)(fade * 200);

            // Weapon name shown on every outcome (Hit / Miss / Blocked) so the
            // player can tell which attack each line came from — important when a
            // unit has multiple attacks (e.g. wolf's Pounce vs Bite). Previously
            // Miss hid the weapon name, making a missed Pounce indistinguishable
            // from a missed Bite.
            string weap = string.IsNullOrEmpty(e.WeaponName) ? "" : $" ({e.WeaponName})";
            string logLine = e.Outcome switch
            {
                CombatLogOutcome.Hit => $"{e.AttackerName} hit {e.DefenderName} for {e.NetDamage}{weap}",
                CombatLogOutcome.Miss => $"{e.AttackerName} missed {e.DefenderName}{weap}",
                CombatLogOutcome.Blocked => $"{e.DefenderName} blocked {e.AttackerName}'s attack{weap}",
                _ => ""
            };

            Text(_smallFont, logLine, new Vector2(10, logBaseY - linesDrawn * 16),
                new Color((byte)200, (byte)200, (byte)200, alpha));
            linesDrawn++;
        }
    }

    // ═══════════════════════════════════════
    //  Helpers
    // ═══════════════════════════════════════

    private void Text(SpriteFont? font, string text, Vector2 pos, Color color)
    {
        if (font != null)
            _batch.DrawString(font, text, new Vector2((int)pos.X, (int)pos.Y), color);
    }

    private static int FindNecromancer(Simulation sim)
    {
        return sim.NecromancerIndex;
    }
}
