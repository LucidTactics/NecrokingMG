using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using Necroking.Data;
using Necroking.Data.Registries;
using Necroking.GameSystems;
using Necroking.World;

namespace Necroking.Render;

/// <summary>
/// Renders the in-game HUD: HP/mana bars, spell bars, tooltips, unit counts,
/// time controls, combat log. Extracted from Game1 to reduce its size.
/// </summary>
public class HUDRenderer
{
    // Layout constants
    private const int BarWidth = 200;
    private const int BarHeight = 16;
    private const int BarX = 10;
    private const int HpBarY = 32;
    private const int ManaBarY = 50;

    private const int PrimarySlotW = 50;
    private const int PrimarySlotH = 50;
    private const int PrimaryBarOffsetX = 110; // screenW/2 - this
    private const int PrimaryBarBottomOffset = 95; // screenH - this

    private const int SecondarySlotW = 35;
    private const int SecondarySlotH = 35;
    private const int SecondaryBarOffsetX = 80;
    private const int SecondaryBarGap = 6;
    private const int SlotSpacing = 4;
    private const int SlotBorderHeight = 2;

    private const int DropdownItemH = 20;
    private const int DropdownWidth = 164;

    private const int TcBtnW = 32;
    private const int TcBtnH = 22;
    private const int TcGap = 2;
    private const int TcCount = 6;

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
    private static readonly Color KeyLabelColor = new(180, 180, 200);
    private static readonly Color SpellNameColor = new(200, 200, 220);
    private static readonly Color CooldownOverlay = new(0, 0, 0, 150);
    private static readonly Color CooldownText = new(255, 200, 100);
    private static readonly Color LowManaOverlay = new(80, 0, 0, 80);
    private static readonly Color DropdownBg = new(20, 20, 35, 240);
    private static readonly Color DropdownNoneColor = new(150, 150, 170);
    private static readonly Color DropdownSelectedColor = new(255, 220, 100);
    private static readonly Color DropdownNormalColor = new(200, 200, 220);
    private static readonly Color TooltipBg = new(15, 15, 25, 220);
    private static readonly Color TooltipBorder = new(100, 100, 160);
    private static readonly Color TooltipText = new(220, 220, 240);
    private static readonly Color ControlHintColor = new(120, 120, 140, 200);
    private static readonly Color InventoryHintColor = new(200, 220, 180);
    private static readonly Color MaterialColor = new(200, 160, 255);
    private static readonly Color PotionActiveBorder = new(180, 200, 100, 240);
    private static readonly Color PotionQtyColor = new(255, 255, 200);
    private static readonly Color PotionEmptyColor = new(100, 100, 100, 120);

    // Dependencies (set via Init)
    private SpriteBatch _batch = null!;
    private Texture2D _pixel = null!;
    private SpriteFont? _font;
    private SpriteFont? _smallFont;

    public void Init(SpriteBatch batch, Texture2D pixel, SpriteFont? font, SpriteFont? smallFont)
    {
        _batch = batch;
        _pixel = pixel;
        _font = font;
        _smallFont = smallFont;
    }

    /// <summary>Draw the complete HUD.</summary>
    public void Draw(int screenW, int screenH, Simulation sim, GameData gameData,
        Inventory inventory, bool inventoryVisible,
        SpellBarState primaryBar, SpellBarState secondaryBar,
        int spellDropdownSlot, int secondaryDropdownSlot,
        float timeScale, int hoveredObjectIdx, EnvironmentSystem envSystem,
        Action<string, int, int> drawSpellCategoryIcon,
        string[]? potionSlots = null, int activePotionSlot = -1,
        Func<string, Texture2D?>? getItemTexture = null,
        int potionDropdownSlot = -1)
    {
        int necroIdx = FindNecromancer(sim);

        DrawStatusBars(necroIdx, sim);

        int primaryY = screenH - PrimaryBarBottomOffset;
        DrawSpellBar(screenW, primaryY, PrimarySlotW, PrimarySlotH, PrimaryBarOffsetX,
            new[] { "Q", "E", "LC", "RC" }, primaryBar, sim, gameData, drawSpellCategoryIcon, 7);
        DrawSpellDropdown(screenW, primaryY, PrimarySlotW, PrimaryBarOffsetX,
            spellDropdownSlot, primaryBar, gameData);

        int secondaryY = primaryY - SecondarySlotH - SecondaryBarGap;
        DrawSpellBar(screenW, secondaryY, SecondarySlotW, SecondarySlotH, SecondaryBarOffsetX,
            new[] { "1", "2", "3", "4" }, secondaryBar, sim, gameData, drawSpellCategoryIcon, 5);
        DrawSpellDropdown(screenW, secondaryY, SecondarySlotW, SecondaryBarOffsetX,
            secondaryDropdownSlot, secondaryBar, gameData);

        if (potionSlots != null)
            DrawPotionSlots(screenW, secondaryY, potionSlots, activePotionSlot, inventory, gameData, getItemTexture);
        if (potionSlots != null)
            DrawPotionDropdown(screenW, secondaryY, potionSlots, potionDropdownSlot, gameData);

        DrawBuildingTooltip(hoveredObjectIdx, envSystem, sim);
        DrawControlsHint(screenH);
        DrawTimeControls(screenW, screenH, timeScale, gameData);
        DrawCombatLog(screenW, screenH, sim, gameData);
    }

    // ═══════════════════════════════════════
    //  Status Bars
    // ═══════════════════════════════════════

    private void DrawStatusBars(int necroIdx, Simulation sim)
    {
        if (necroIdx >= 0)
        {
            var stats = sim.Units.Stats[necroIdx];
            float hpFrac = stats.MaxHP > 0 ? (float)stats.HP / stats.MaxHP : 0f;
            _batch.Draw(_pixel, new Rectangle(BarX, HpBarY, BarWidth, BarHeight), HpBarBg);
            _batch.Draw(_pixel, new Rectangle(BarX, HpBarY, (int)(BarWidth * hpFrac), BarHeight), HpBarFg);
            Text(_font, $"HP: {stats.HP}/{stats.MaxHP}", new Vector2(BarX + 5, HpBarY + 1), Color.White);
        }

        float manaFrac = sim.NecroState.MaxMana > 0 ? sim.NecroState.Mana / sim.NecroState.MaxMana : 0f;
        _batch.Draw(_pixel, new Rectangle(BarX, ManaBarY, BarWidth, BarHeight), ManaBarBg);
        _batch.Draw(_pixel, new Rectangle(BarX, ManaBarY, (int)(BarWidth * manaFrac), BarHeight), ManaBarFg);
        Text(_font, $"Mana: {(int)sim.NecroState.Mana}/{(int)sim.NecroState.MaxMana}", new Vector2(BarX + 5, ManaBarY + 1), Color.White);
    }


    // ═══════════════════════════════════════
    //  Spell Bars (unified for primary + secondary)
    // ═══════════════════════════════════════

    private void DrawSpellBar(int screenW, int barY, int slotW, int slotH, int centerOffset,
        string[] keys, SpellBarState bar, Simulation sim, GameData gameData,
        Action<string, int, int> drawCategoryIcon, int nameTruncLen)
    {
        bool isSecondary = slotW < PrimarySlotW;
        var filledBg = isSecondary ? SecFilledBg : SlotFilledBg;
        var emptyBg = isSecondary ? SecEmptyBg : SlotEmptyBg;
        var border = isSecondary ? SecBorder : SlotBorder;

        for (int s = 0; s < 4; s++)
        {
            int slotX = screenW / 2 - centerOffset + s * (slotW + SlotSpacing);
            bool hasSpell = s < bar.Slots.Length && !string.IsNullOrEmpty(bar.Slots[s].SpellID);

            _batch.Draw(_pixel, new Rectangle(slotX, barY, slotW, slotH), hasSpell ? filledBg : emptyBg);
            _batch.Draw(_pixel, new Rectangle(slotX, barY, slotW, SlotBorderHeight), border);

            if (_smallFont == null) continue;

            Text(_smallFont, keys[s], new Vector2(slotX + 2, barY + 2), KeyLabelColor);

            if (!hasSpell) continue;

            // Special built-in abilities
            string slotSpellId = bar.Slots[s].SpellID;
            if (slotSpellId == "melee_gather")
            {
                Text(_smallFont, "Melee", new Vector2(slotX + 3, barY + slotH - 14), SpellNameColor);
                continue;
            }

            var spell = gameData.Spells.Get(slotSpellId);
            if (spell == null) continue;

            // Name (truncated)
            string name = spell.DisplayName.Length > nameTruncLen
                ? spell.DisplayName[..nameTruncLen] : spell.DisplayName;
            Text(_smallFont, name, new Vector2(slotX + 3, barY + slotH - 14), SpellNameColor);

            // Category icon (only on primary bar)
            if (!isSecondary)
                drawCategoryIcon(spell.Category, slotX + slotW / 2, barY + slotH / 2 - 2);

            // Cooldown overlay
            float cd = sim.NecroState.GetCooldown(spell.Id);
            if (cd > 0f)
            {
                float cdFrac = MathF.Min(cd / MathF.Max(spell.Cooldown, 0.1f), 1f);
                int cdH = (int)(slotH * cdFrac);
                _batch.Draw(_pixel, new Rectangle(slotX, barY + slotH - cdH, slotW, cdH), CooldownOverlay);
                if (!isSecondary)
                    Text(_smallFont, $"{cd:F1}", new Vector2(slotX + 12, barY + 18), CooldownText);
            }

            // Low mana indicator
            if (sim.NecroState.Mana < spell.ManaCost)
                _batch.Draw(_pixel, new Rectangle(slotX, barY, slotW, slotH), LowManaOverlay);
        }
    }

    private void DrawSpellDropdown(int screenW, int barY, int slotW, int centerOffset,
        int openSlot, SpellBarState bar, GameData gameData)
    {
        if (openSlot < 0 || openSlot >= 4 || _smallFont == null) return;

        int slotX = screenW / 2 - centerOffset + openSlot * (slotW + SlotSpacing);
        var allSpells = gameData.Spells.GetIDs();
        int ddH = (allSpells.Count + 1) * DropdownItemH;
        int ddY = barY - 10;

        _batch.Draw(_pixel, new Rectangle(slotX - 2, ddY - ddH - 2, DropdownWidth, ddH + 4), DropdownBg);
        Text(_smallFont, "(None)", new Vector2(slotX + 4, ddY - DropdownItemH), DropdownNoneColor);

        for (int si = 0; si < allSpells.Count; si++)
        {
            var spDef = gameData.Spells.Get(allSpells[si]);
            int itemY = ddY - (si + 2) * DropdownItemH;
            string label = spDef != null ? $"{spDef.DisplayName} [{spDef.Category}]" : allSpells[si];
            Color labelColor = bar.Slots[openSlot].SpellID == allSpells[si]
                ? DropdownSelectedColor : DropdownNormalColor;
            Text(_smallFont, label, new Vector2(slotX + 4, itemY), labelColor);
        }
    }

    // ═══════════════════════════════════════
    //  Tooltips & Info
    // ═══════════════════════════════════════

    private void DrawBuildingTooltip(int hoveredIdx, EnvironmentSystem envSystem, Simulation sim)
    {
        if (hoveredIdx < 0 || hoveredIdx >= envSystem.ObjectCount || _smallFont == null) return;

        var obj = envSystem.GetObject(hoveredIdx);
        var def = envSystem.Defs[obj.DefIndex];
        var rt = envSystem.GetObjectRuntime(hoveredIdx);
        var proc = envSystem.GetProcessState(hoveredIdx);
        string ownerStr = rt.Owner switch { 0 => "Undead", 1 => "Neutral", _ => "Human" };
        string procStr = proc.Processing ? $"Processing ({proc.ProcessTimer:F1}s)" : "Idle";
        string[] lines = {
            def.Name.Length > 0 ? def.Name : def.Id,
            $"HP: {rt.HP}/{def.BuildingMaxHP}",
            $"Owner: {ownerStr}",
            procStr
        };

        var mouse = Mouse.GetState();
        int ttX = mouse.X + 16, ttY = mouse.Y - 70;
        int ttW = 160, ttH = lines.Length * 16 + 8;
        _batch.Draw(_pixel, new Rectangle(ttX - 4, ttY - 4, ttW + 8, ttH + 8), TooltipBg);
        _batch.Draw(_pixel, new Rectangle(ttX - 4, ttY - 4, ttW + 8, 2), TooltipBorder);
        for (int i = 0; i < lines.Length; i++)
            Text(_smallFont, lines[i], new Vector2(ttX, ttY + i * 16), TooltipText);
    }

    private void DrawControlsHint(int screenH)
    {
        Text(_smallFont, "WASD: Move | Scroll: Zoom | ESC: Menu | Space: Jump | G: Ghost | Shift: Run",
            new Vector2(10, screenH - 22), ControlHintColor);
    }

    // ═══════════════════════════════════════
    //  Time Controls
    // ═══════════════════════════════════════

    private void DrawTimeControls(int screenW, int screenH, float timeScale, GameData gameData)
    {
        if (!gameData.Settings.General.ShowTimeControls || _smallFont == null) return;

        ReadOnlySpan<float> speeds = stackalloc float[] { 0.1f, 0.25f, 0.5f, 1.0f, 1.5f, 2.0f };
        string[] labels = { "<<<", "<<", "<", "=", ">", ">>" };
        int totalW = TcCount * TcBtnW + (TcCount - 1) * TcGap;
        int baseX = screenW - totalW - 10;
        int baseY = screenH - 52;

        var mouse = Mouse.GetState();
        for (int s = 0; s < TcCount; s++)
        {
            int bx = baseX + s * (TcBtnW + TcGap);
            bool hover = mouse.X >= bx && mouse.X < bx + TcBtnW
                      && mouse.Y >= baseY && mouse.Y < baseY + TcBtnH;
            bool active = MathF.Abs(timeScale - speeds[s]) < 0.001f;
            Color bg = active ? new Color(70, 100, 160, 220)
                     : hover  ? new Color(50, 60, 80, 180)
                              : new Color(20, 20, 30, 140);
            _batch.Draw(_pixel, new Rectangle(bx, baseY, TcBtnW, TcBtnH), bg);
            var labelSize = _smallFont.MeasureString(labels[s]);
            Text(_smallFont, labels[s],
                new Vector2(bx + TcBtnW / 2f - labelSize.X / 2f, baseY + TcBtnH / 2f - labelSize.Y / 2f),
                Color.White);
        }

        Text(_smallFont, $"{timeScale:G2}x", new Vector2(baseX + totalW + 6, baseY + 5),
            new Color(180, 180, 200, 200));
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

            string logLine = e.Outcome switch
            {
                CombatLogOutcome.Hit => $"{e.AttackerName} hit {e.DefenderName} for {e.NetDamage} ({e.WeaponName})",
                CombatLogOutcome.Miss => $"{e.AttackerName} missed {e.DefenderName}",
                CombatLogOutcome.Blocked => $"{e.DefenderName} blocked {e.AttackerName}'s attack",
                _ => ""
            };

            Text(_smallFont, logLine, new Vector2(10, logBaseY - linesDrawn * 16),
                new Color((byte)200, (byte)200, (byte)200, alpha));
            linesDrawn++;
        }
    }

    // ═══════════════════════════════════════
    //  Potion Slots
    // ═══════════════════════════════════════

    private void DrawPotionSlots(int screenW, int secondaryY, string[] potionSlots,
        int activePotionSlot, Inventory inventory, GameData gameData,
        Func<string, Texture2D?>? getItemTexture)
    {
        if (_smallFont == null) return;

        // Position: to the right of the secondary bar (4 slots * (35+4) = 156, so offset = 80 + 156 + 8)
        int baseX = screenW / 2 - SecondaryBarOffsetX + 4 * (SecondarySlotW + SlotSpacing) + 8;
        int barY = secondaryY;

        for (int s = 0; s < potionSlots.Length && s < 2; s++)
        {
            int slotX = baseX + s * (SecondarySlotW + SlotSpacing);
            string potionItemId = potionSlots[s];
            bool hasPotion = !string.IsNullOrEmpty(potionItemId);
            int qty = hasPotion ? inventory.GetItemCount(potionItemId) : 0;
            bool isActive = s == activePotionSlot;

            // Background
            Color bg = hasPotion && qty > 0 ? SecFilledBg : SecEmptyBg;
            _batch.Draw(_pixel, new Rectangle(slotX, barY, SecondarySlotW, SecondarySlotH), bg);

            // Border — highlight if active
            Color border = isActive ? PotionActiveBorder : SecBorder;
            _batch.Draw(_pixel, new Rectangle(slotX, barY, SecondarySlotW, SlotBorderHeight), border);
            if (isActive)
            {
                _batch.Draw(_pixel, new Rectangle(slotX, barY + SecondarySlotH - SlotBorderHeight, SecondarySlotW, SlotBorderHeight), border);
                _batch.Draw(_pixel, new Rectangle(slotX, barY, SlotBorderHeight, SecondarySlotH), border);
                _batch.Draw(_pixel, new Rectangle(slotX + SecondarySlotW - SlotBorderHeight, barY, SlotBorderHeight, SecondarySlotH), border);
            }

            // Key label
            Text(_smallFont, (s + 5).ToString(), new Vector2(slotX + 2, barY + 2), KeyLabelColor);

            if (hasPotion && getItemTexture != null)
            {
                // Draw potion icon
                var texture = getItemTexture(potionItemId);
                if (texture != null)
                {
                    int iconSize = SecondarySlotW - 6;
                    var iconRect = new Rectangle(slotX + 3, barY + 3, iconSize, iconSize);
                    _batch.Draw(texture, iconRect, Color.White);
                }

                // Quantity in bottom-right
                if (qty > 0)
                {
                    string qtyStr = qty.ToString();
                    var qtySize = _smallFont.MeasureString(qtyStr);
                    Text(_smallFont, qtyStr,
                        new Vector2(slotX + SecondarySlotW - qtySize.X - 2, barY + SecondarySlotH - qtySize.Y - 1),
                        PotionQtyColor);
                }
                else
                {
                    // Dim overlay when out of stock
                    _batch.Draw(_pixel, new Rectangle(slotX, barY, SecondarySlotW, SecondarySlotH), PotionEmptyColor);
                }
            }
        }
    }

    private void DrawPotionDropdown(int screenW, int secondaryY, string[] potionSlots,
        int openSlot, GameData gameData)
    {
        if (openSlot < 0 || openSlot >= 2 || _smallFont == null) return;

        int baseX = screenW / 2 - SecondaryBarOffsetX + 4 * (SecondarySlotW + SlotSpacing) + 8;
        int slotX = baseX + openSlot * (SecondarySlotW + SlotSpacing);

        var allPotions = gameData.Potions.GetIDs();
        int ddItemH = DropdownItemH;
        int ddH = (allPotions.Count + 1) * ddItemH;
        int ddY = secondaryY - 10;

        _batch.Draw(_pixel, new Rectangle(slotX - 2, ddY - ddH - 2, DropdownWidth, ddH + 4), DropdownBg);
        Text(_smallFont, "(None)", new Vector2(slotX + 4, ddY - ddItemH), DropdownNoneColor);

        string currentItemId = potionSlots[openSlot];
        for (int pi = 0; pi < allPotions.Count; pi++)
        {
            var pDef = gameData.Potions.Get(allPotions[pi]);
            int itemY = ddY - (pi + 2) * ddItemH;
            string label = pDef?.DisplayName ?? allPotions[pi];
            bool isSelected = pDef != null && pDef.ItemID == currentItemId;
            Text(_smallFont, label, new Vector2(slotX + 4, itemY),
                isSelected ? DropdownSelectedColor : DropdownNormalColor);
        }
    }

    // ═══════════════════════════════════════
    //  Helpers
    // ═══════════════════════════════════════

    private void Text(SpriteFont? font, string text, Vector2 pos, Color color)
    {
        if (font != null)
            _batch.DrawString(font, text, pos, color);
    }

    private static int FindNecromancer(Simulation sim)
    {
        return sim.NecromancerIndex;
    }
}
