using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using Necroking.Core;
using Necroking.Data;
using Necroking.Data.Registries;
using Necroking.GameSystems;
using Necroking.Render;
using Necroking.World;

namespace Necroking.UI;

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

    // Grimoire-style frame slots (see SpellBarBindings for the slot count +
    // key labels) arranged like the physical keyboard: the number row (1-8)
    // centred on the screen, Q and E on a row below it — Q under the 1/2 seam,
    // E under the 3/4 seam (a one-key gap where W sits). Each box fits the
    // 96px spell icon at an exact 2:1 downscale (48px) — pixel-perfect.
    private const int PrimarySlotW = 60;
    private const int PrimarySlotH = 60;
    // Sits the Q/E row's bottom edge ~2px above the screen bottom (slot height
    // 60 + 1px gap; the frame's own margin makes ~2 visible).
    private const int PrimaryBarBottomOffset = 61; // screenH - this
    private const int SlotSpacing = 6;
    private const int SlotPitch = PrimarySlotW + SlotSpacing;
    private const int RowGap = 6; // vertical gap between the two rows
    private const int NumberSlotCount = SpellBarBindings.SlotCount - 2; // slots 2..9 = keys 1-8
    // Half the number row's total width (screenW/2 - this = row's left edge).
    private const int NumberRowCenterOffset = (NumberSlotCount * SlotPitch - SlotSpacing) / 2;
    private const int SlotBorderHeight = 2;

    // The slot chrome is the SpellSlot widget (Background = fancy_inner parchment,
    // Frame = spider_frame border, both harmonized) — editable in the UI editor.
    // The HUD draws its background + frame at each slot's size around the icon.
    private const string SpellSlotWidget = "SpellSlot";
    private const float SlotIconRatio = 0.80f; // icon size as a fraction of the box

    private const int TcBtnW = 32;
    private const int TcBtnH = 22;
    private const int TcGap = 2;
    private const int TcCount = 6;
    private const int TcSpeedTextReserve = 56; // room on the right for the "Paused"/"1.5x" readout
    private const int TcRightMargin = 8;

    // ── Top-right core-menu buttons ──
    // Button index contract, shared with Game1 (mask bit i = menu i open, and
    // HitTestMenuButtons returns these indices). Keep order in sync with
    // MenuButtonLabels and Game1.ToggleCoreMenu.
    public const int MenuInventory = 0, MenuCrafting = 1, MenuBuilding = 2,
                     MenuGrimoire = 3, MenuSkills = 4, MenuCharacter = 5, MenuLog = 6;
    public const int MenuBtnCount = 7;
    public static readonly string[] MenuButtonLabels =
        { "Inventory (I)", "Crafting (C)", "Building (B)", "Grimoire (J)", "Skills (K)", "Character (Tab)", "Log" };
    private const int MenuBtnH = 24;
    private const int MenuBtnTop = 8;
    private const int MenuBtnPadX = 8;   // text padding per side
    private const int MenuBtnGap = 3;
    private const int MenuBtnRightMargin = 8;
    private readonly Rectangle[] _menuBtnRects = new Rectangle[MenuBtnCount];

    // ── Editor-launcher row (below the core-menu row) ──
    // Click mirror of the F9-F12 editor-toggle shortcuts, and Game1.ToggleEditorButton.
    public const int EditorUnit = 0, EditorSpell = 1, EditorMap = 2, EditorUi = 3, EditorDebug = 4;
    public const int EditorBtnCount = 5;
    public static readonly string[] EditorButtonLabels =
        { "Units (F9)", "Spells (F10)", "Map (F11)", "UI (F12)", "Debug" };
    private const int EditorBtnTop = MenuBtnTop + MenuBtnH + 4;
    private readonly Rectangle[] _editorBtnRects = new Rectangle[EditorBtnCount];

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
    private static readonly Color KeyLabelColor = new(0xff, 0xea, 0xbe); // bright parchment-cream, pops on spell icons
    private static readonly Color KeyLabelCantUse = new(0x83, 0x54, 0x54); // spell uncastable (mana/path/charges — not cooldown)
    private static readonly Color KeyLabelOutline = new(0x2a, 0x1f, 0x0c); // dark rim so the label reads on any art
    private static readonly Color SpellNameColor = new(200, 200, 220);
    private static readonly Color CooldownOverlay = new(0, 0, 0, 150);
    private static readonly Color CooldownText = new(255, 200, 100);
    private static readonly Color LowManaOverlay = new(80, 0, 0, 80);
    // (Tooltip box colors moved to TooltipSystem — the one canonical style.)
    private static readonly Color ControlHintColor = new(120, 120, 140, 200);
    private static readonly Color InventoryHintColor = new(200, 220, 180);
    private static readonly Color MaterialColor = new(200, 160, 255);
    private static readonly Color PotionQtyColor = new(255, 255, 200);
    private static readonly Color PotionEmptyColor = new(100, 100, 100, 120);
    // Spell-slot activation flash (warm gold) — interior wash + bright frame edges,
    // scaled by the remaining flash fraction so it fades out smoothly. Public so the
    // cast dispatch sets the timer with the SAME duration the draw normalizes by —
    // one source of truth, otherwise the fade math drifts and it reads "stuck on".
    public const float SlotFlashDuration = 0.20f;
    private static readonly Color SlotFlashColor = new(255, 225, 130, 150);
    private static readonly Color SlotFlashEdge = new(255, 240, 180, 230);

    // Dependencies (set via Init)
    private SpriteBatch _batch = null!;
    private Render.SpriteScope Scope => _batch;  // straight-alpha draw surface (implicit conversion)
    private Texture2D _pixel = null!;
    private SpriteFont _font;
    private SpriteFont _smallFont;
    // Shares Game1's SpriteBatch, so its element/icon draws land in the same
    // pass. Used to reuse the grimoire's frame + parchment backing for slots.
    private UI.RuntimeWidgetRenderer? _widgets;
    private InputState _input = new();

    // Spell-bar hover tooltip: the slot under the cursor this frame, captured during
    // DrawSpellBar and rendered after the bars so it layers on top. _hoverSlotSpell
    // is the hovered ability (null = none); _hoverSlotMelee flags the icon-less
    // melee_gather slot (which has no SpellDef).
    private SpellDef? _hoverSlotSpell;
    private bool _hoverSlotMelee;

    /// <summary>When set, the top-left player stat bars are replaced by a
    /// programmer-style debug readout (projectile/potion/arrow counts by enum
    /// category). Toggled by the same G key that toggles ghost/god mode
    /// (see Game1's Ghost-mode toggle). Runtime-only, not persisted.</summary>
    public bool ShowDebugPanel;

    public void Init(SpriteBatch batch, Texture2D pixel, SpriteFont font, SpriteFont smallFont,
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

    /// <summary>Draw the HUD. <paramref name="drawTopRows"/> excludes the
    /// top-right core-menu/editor-launcher rows — those are drawn by their own
    /// router layers (HudTop band, ABOVE panels and blocking overlays), so
    /// their draw position matches their input position.</summary>
    public void Draw(int screenW, int screenH, Simulation sim, GameData gameData,
        Inventory inventory, bool inventoryVisible,
        SpellBarState bar,
        float timeScale,
        Action<string, int, int> drawSpellCategoryIcon, int menuOpenMask = 0, bool paused = false,
        float[]? slotFlash = null,
        int editorOpenMask = 0, bool drawTopRows = true)
    {
        int necroIdx = FindNecromancer(sim);

        if (ShowDebugPanel)
            DrawDebugPanel(sim);
        else
            DrawStatusBars(necroIdx, sim);

        // Reset hover-tooltip capture; the spell bar sets it if a filled slot is hovered.
        // The tooltip itself draws later via DrawCursorTooltips (Tooltip band, topmost).
        _hoverSlotSpell = null;
        _hoverSlotMelee = false;

        DrawSpellBar(screenW, screenH,
            SpellBarBindings.SlotLabels, bar, sim, gameData, inventory, drawSpellCategoryIcon, necroIdx, slotFlash);

        // Controls hint intentionally omitted — overlapped the FPS/zoom bottom-
        // left readout. Re-enable if we add a menu page for it.
        DrawTimeControls(screenW, screenH, timeScale, gameData, paused);
        if (drawTopRows)
        {
            DrawMenuButtons(screenW, menuOpenMask, (int)_input.MousePos.X, (int)_input.MousePos.Y);
            DrawEditorButtons(screenW, editorOpenMask, (int)_input.MousePos.X, (int)_input.MousePos.Y);
        }
        DrawHordeCaps(screenW, sim, gameData);
        DrawCombatLog(screenW, screenH, sim, gameData);
    }

    /// <summary>Cursor-anchored hover tooltips (spell-bar slot, world object,
    /// belly, corpse, unit). Drawn from the Tooltip-band CursorTooltipLayer —
    /// a separate topmost pass — so tooltips layer above every Hud-band widget
    /// (e.g. the aggression bar). Reads the hover state <see cref="Draw"/>
    /// captured earlier this frame; bands draw bottom-up, so it's fresh.</summary>
    public void DrawCursorTooltips(int screenW, int screenH, Simulation sim, GameData gameData,
        Inventory inventory, int hoveredObjectIdx, EnvironmentSystem envSystem,
        uint hoveredBellyUnitId, int hoveredCorpseIdx, int hoveredUnitIdx, bool editorInspect)
    {
        DrawSpellSlotTooltip(gameData, inventory, screenW, screenH);
        DrawObjectTooltip(hoveredObjectIdx, envSystem, sim, gameData, screenW, screenH);
        DrawBellyTooltip(hoveredBellyUnitId, sim, gameData, screenW, screenH);
        DrawCorpseTooltip(hoveredCorpseIdx, sim, gameData, screenW, screenH);
        DrawUnitTooltip(hoveredUnitIdx, sim, gameData, screenW, screenH, editorInspect);
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
        int y = EditorBtnTop + MenuBtnH + 6; // sit below the core-menu + editor-launcher rows
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
    //  Core-menu buttons (top-right)
    // ═══════════════════════════════════════

    // The core-menu row and the editor-launcher row are the same right-aligned,
    // auto-sized button strip with different labels/top-Y/rect array and a
    // different open/hover/idle palette. One ButtonRow helper owns the mechanics
    // (Layout + HitTest + Draw); the public wrappers below stay 1-liners so the
    // Game1/router call sites (HitTest*/Draw*) are untouched.
    private readonly struct ButtonRowStyle
    {
        public readonly Color OpenBg, HoverBg, IdleBg, AccentOpen, AccentIdle, LabelOpen, LabelIdle;
        public ButtonRowStyle(Color openBg, Color hoverBg, Color idleBg,
            Color accentOpen, Color accentIdle, Color labelOpen, Color labelIdle)
        {
            OpenBg = openBg; HoverBg = hoverBg; IdleBg = idleBg;
            AccentOpen = accentOpen; AccentIdle = accentIdle;
            LabelOpen = labelOpen; LabelIdle = labelIdle;
        }
    }

    private static readonly ButtonRowStyle MenuRowStyle = new(
        new Color(70, 100, 160, 220), new Color(50, 60, 80, 200), new Color(20, 20, 30, 160),
        new Color(140, 180, 255), new Color(90, 90, 120, 180),
        Color.White, new Color(210, 210, 230));

    private static readonly ButtonRowStyle EditorRowStyle = new(
        new Color(160, 100, 70, 220), new Color(80, 60, 50, 200), new Color(30, 20, 20, 160),
        new Color(255, 180, 140), new Color(120, 90, 90, 180),
        Color.White, new Color(230, 210, 210));

    /// <summary>Right-aligned, auto-sized rects for a top-right button row,
    /// computed into <paramref name="rects"/>. Shared by draw + hit-test so they
    /// never desync. Returns false when no font is available (can't measure).</summary>
    private bool LayoutButtonRow(string[] labels, int top, int screenW, Rectangle[] rects)
    {
        if (_smallFont == null) return false;
        int n = labels.Length;
        Span<int> ws = stackalloc int[n];
        int totalW = MenuBtnGap * (n - 1);
        for (int i = 0; i < n; i++)
        {
            ws[i] = (int)_smallFont.MeasureString(labels[i]).X + MenuBtnPadX * 2;
            totalW += ws[i];
        }
        int x = screenW - MenuBtnRightMargin - totalW;
        for (int i = 0; i < n; i++)
        {
            rects[i] = new Rectangle(x, top, ws[i], MenuBtnH);
            x += ws[i] + MenuBtnGap;
        }
        return true;
    }

    /// <summary>Index of the row button under the cursor, or -1.</summary>
    private static int HitTestButtonRow(Rectangle[] rects, int mouseX, int mouseY)
    {
        for (int i = 0; i < rects.Length; i++)
            if (rects[i].Contains(mouseX, mouseY)) return i;
        return -1;
    }

    /// <summary>Draw one button row. <paramref name="openMask"/> bit i set =
    /// button i currently open (highlighted).</summary>
    private void DrawButtonRow(string[] labels, Rectangle[] rects, int openMask,
        int mx, int my, in ButtonRowStyle style)
    {
        for (int i = 0; i < labels.Length; i++)
        {
            var r = rects[i];
            bool open = (openMask & (1 << i)) != 0;
            bool hover = r.Contains(mx, my);
            Color bg = open ? style.OpenBg : hover ? style.HoverBg : style.IdleBg;
            Scope.Draw(_pixel, r, bg);
            // Top accent line — brighter when open.
            Scope.Draw(_pixel, new Rectangle(r.X, r.Y, r.Width, 2),
                open ? style.AccentOpen : style.AccentIdle);
            string label = labels[i];
            var ls = _smallFont!.MeasureString(label);
            Text(_smallFont, label,
                new Vector2(r.X + r.Width / 2f - ls.X / 2f, r.Y + r.Height / 2f - ls.Y / 2f),
                open ? style.LabelOpen : style.LabelIdle);
        }
    }

    /// <summary>Returns the menu-button index under the cursor, or -1.</summary>
    public int HitTestMenuButtons(int screenW, int mouseX, int mouseY)
        => LayoutButtonRow(MenuButtonLabels, MenuBtnTop, screenW, _menuBtnRects)
            ? HitTestButtonRow(_menuBtnRects, mouseX, mouseY) : -1;

    /// <summary>Draw the row of core-menu buttons. <paramref name="menuOpenMask"/>
    /// has bit i set when menu i is currently open (highlighted).</summary>
    internal void DrawMenuButtons(int screenW, int menuOpenMask, int mx, int my)
    {
        if (!LayoutButtonRow(MenuButtonLabels, MenuBtnTop, screenW, _menuBtnRects)) return;
        DrawButtonRow(MenuButtonLabels, _menuBtnRects, menuOpenMask, mx, my, MenuRowStyle);
    }

    /// <summary>Returns the editor-button index under the cursor, or -1.</summary>
    public int HitTestEditorButtons(int screenW, int mouseX, int mouseY)
        => LayoutButtonRow(EditorButtonLabels, EditorBtnTop, screenW, _editorBtnRects)
            ? HitTestButtonRow(_editorBtnRects, mouseX, mouseY) : -1;

    /// <summary>Draw the editor-launcher row. <paramref name="editorOpenMask"/> has
    /// bit i set when editor i is the one currently open (highlighted).</summary>
    internal void DrawEditorButtons(int screenW, int editorOpenMask, int mx, int my)
    {
        if (!LayoutButtonRow(EditorButtonLabels, EditorBtnTop, screenW, _editorBtnRects)) return;
        DrawButtonRow(EditorButtonLabels, _editorBtnRects, editorOpenMask, mx, my, EditorRowStyle);
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
    //  Debug Panel (top-left, replaces stat bars when ShowDebugPanel)
    // ═══════════════════════════════════════

    private static readonly Color DebugPanelBg = new(10, 12, 16, 210);
    private static readonly Color DebugPanelBorder = new(80, 200, 120, 220);
    private static readonly Color DebugHeaderColor = new(120, 230, 160);
    private static readonly Color DebugKeyColor = new(150, 170, 190);
    private static readonly Color DebugValColor = new(230, 240, 200);

    /// <summary>Programmer-style debug readout drawn top-left in place of the
    /// player stat bars. For now: live projectile counts broken down by the
    /// <see cref="ProjectileType"/> enum category (RegularHit / Explosive / Potion).
    /// Styled like the text tooltip panel (dark box + thin accent border).</summary>
    private void DrawDebugPanel(Simulation sim)
    {
        if (_smallFont == null) return;

        // Count live projectiles per enum category.
        var names = Enum.GetNames<ProjectileType>();
        var counts = new int[names.Length];
        int total = 0;
        foreach (var proj in sim.Projectiles.Projectiles)
        {
            counts[(int)proj.Type]++;
            total++;
        }

        // Build the "key: value" lines.
        var lines = new List<(string key, string val)>
        {
            ("Projectiles", total.ToString()),
        };
        for (int i = 0; i < names.Length; i++)
            lines.Add(("  " + names[i], counts[i].ToString()));

        // Layout: measure widest key/value so the two columns align.
        const int padX = 8, padY = 6, lineH = 15, colGap = 16;
        const string header = "DEBUG";
        float keyW = _smallFont.MeasureString(header).X;
        float valW = 0f;
        foreach (var (key, val) in lines)
        {
            keyW = MathF.Max(keyW, _smallFont.MeasureString(key).X);
            valW = MathF.Max(valW, _smallFont.MeasureString(val).X);
        }

        int boxW = padX * 2 + (int)keyW + colGap + (int)valW;
        int boxH = padY * 2 + lineH * (lines.Count + 1); // +1 for header
        var box = new Rectangle(BarX, 30, boxW, boxH);

        // Panel background + thin accent border (text-tooltip style).
        Scope.Draw(_pixel, box, DebugPanelBg);
        Scope.Draw(_pixel, new Rectangle(box.X, box.Y, box.Width, 1), DebugPanelBorder);
        Scope.Draw(_pixel, new Rectangle(box.X, box.Bottom - 1, box.Width, 1), DebugPanelBorder);
        Scope.Draw(_pixel, new Rectangle(box.X, box.Y, 1, box.Height), DebugPanelBorder);
        Scope.Draw(_pixel, new Rectangle(box.Right - 1, box.Y, 1, box.Height), DebugPanelBorder);

        int tx = box.X + padX;
        int valX = box.Right - padX - (int)valW;
        int ty = box.Y + padY;
        Text(_smallFont, header, new Vector2(tx, ty), DebugHeaderColor);
        ty += lineH;
        foreach (var (key, val) in lines)
        {
            Text(_smallFont, key, new Vector2(tx, ty), DebugKeyColor);
            Text(_smallFont, val, new Vector2(valX, ty), DebugValColor);
            ty += lineH;
        }
    }


    // ═══════════════════════════════════════
    //  Spell Bar
    // ═══════════════════════════════════════

    private void DrawSpellBar(int screenW, int screenH,
        string[] keys, SpellBarState bar, Simulation sim, GameData gameData,
        Inventory inventory, Action<string, int, int> drawCategoryIcon, int necroIdx, float[]? flash = null)
    {
        // Same caster-level resolution as the cast gate (TryStartSpellCast), so
        // the castability cues below can't drift from what a cast would actually
        // do. One resolve — the same caster serves every slot.
        Func<MagicPath, int>? casterLevel = null;
        if (necroIdx >= 0 && necroIdx < sim.Units.Count)
            casterLevel = SpellCaster.ResolveCasterLevel(
                gameData.Units.Get(sim.Units[necroIdx].UnitDefID), sim.Units, necroIdx);

        for (int s = 0; s < keys.Length; s++)
        {
            var slot = GetSlotRect(screenW, screenH, s);
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
                else if (spell != null)
                   _widgets?.DrawIcon(GamePaths.PlaceholderSpellIcon, innerRect.X, innerRect.Y, innerRect.Width, innerRect.Height);
            });

            if (hovered)
            {
                Scope.Draw(_pixel, inner, new Color(255, 255, 255, 31));
                // Remember what's here so the tooltip can render on top after the bars.
                if (spell != null) _hoverSlotSpell = spell;
                else if (slotSpellId == "melee_gather") _hoverSlotMelee = true;
            }

            // Activation flash: a slot that just fired lights up and fades out, so a
            // keypress (or click) gives immediate visual confirmation. Drawn over the
            // icon but under the cooldown sweep so the sweep still reads on top.
            if (flash != null && s < flash.Length && flash[s] > 0f)
            {
                float t = MathF.Min(flash[s] / SlotFlashDuration, 1f); // 1 → 0 as it fades
                Scope.Draw(_pixel, inner, Core.ColorUtils.Fade(SlotFlashColor, t));
                // Bright frame edges so it pops even on a busy icon.
                var edge = SlotFlashEdge * t;
                Scope.Draw(_pixel, new Rectangle(slot.X, slot.Y, slot.Width, 2), edge);
                Scope.Draw(_pixel, new Rectangle(slot.X, slot.Bottom - 2, slot.Width, 2), edge);
                Scope.Draw(_pixel, new Rectangle(slot.X, slot.Y, 2, slot.Height), edge);
                Scope.Draw(_pixel, new Rectangle(slot.Right - 2, slot.Y, 2, slot.Height), edge);
            }

            // Whether a cast would fail right now for a resource/requirement
            // reason. Deliberately NOT set by cooldown — that's temporary and
            // already has its own sweep overlay.
            bool uncastable = false;

            // Cooldown sweep (over the icon interior).
            if (spell != null)
            {
                float cd = sim.NecroState.GetCooldown(spell.Id);
                if (cd > 0f)
                {
                    float cdFrac = MathF.Min(cd / MathF.Max(spell.Cooldown, 0.1f), 1f);
                    int cdH = (int)(inner.Height * cdFrac);
                    Scope.Draw(_pixel, new Rectangle(inner.X, inner.Bottom - cdH, inner.Width, cdH), CooldownOverlay);
                    if (_smallFont != null)
                        Text(_smallFont, $"{cd:F1}", new Vector2(inner.Center.X - 10, inner.Center.Y - 6), CooldownText);
                }
                // Path-discounted cost, matching what the cast gate deducts —
                // flat ManaCost overstates it for masters of the spell's path.
                float effCost = casterLevel != null ? spell.EffectiveManaCost(casterLevel) : spell.ManaCost;
                if (sim.NecroState.Mana < effCost)
                {
                    Scope.Draw(_pixel, inner, LowManaOverlay);
                    uncastable = true;
                }
                if (casterLevel != null && !spell.MeetsPathRequirements(casterLevel))
                    uncastable = true;

                // Consumable charges (potion-spells): show the inventory count,
                // grey out at 0.
                if (!string.IsNullOrEmpty(spell.ConsumesItem))
                {
                    int qty = inventory.GetItemCount(spell.ConsumesItem);
                    if (qty <= 0)
                    {
                        Scope.Draw(_pixel, inner, PotionEmptyColor);
                        uncastable = true;
                    }
                    if (_smallFont != null)
                    {
                        // WoW-style charge count at the bottom-right, outlined
                        // so it reads on the icon art.
                        string q = qty.ToString();
                        var qs = _smallFont.MeasureString(q);
                        TextOutlined(_smallFont, q,
                            new Vector2(inner.Right - qs.X - 1, inner.Bottom - qs.Y + 2),
                            PotionQtyColor, KeyLabelOutline);
                    }
                }
            }

            if (_smallFont == null) continue;
            // melee_gather has no icon — label it.
            if (slotSpellId == "melee_gather")
                Text(_smallFont, "Melee", new Vector2(inner.X + 1, inner.Center.Y - 6), SpellNameColor);
            // Hotkey label, WoW-style at the parchment's top-right.
            var ks = _smallFont.MeasureString(keys[s]);
            TextOutlined(_smallFont, keys[s], new Vector2(inner.Right - ks.X - 3, inner.Y),
                uncastable ? KeyLabelCantUse : KeyLabelColor, KeyLabelOutline);
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
            Scope.Draw(_pixel, slot, SlotEmptyBg);
            drawInterior(inner);
            return;
        }
        _widgets.DrawWidgetBackground(SpellSlotWidget, slot);
        drawInterior(inner);
        _widgets.DrawWidgetFrameLayer(SpellSlotWidget, slot);
    }


    // ═══════════════════════════════════════
    //  Hit Testing (shared layout with draw)
    // ═══════════════════════════════════════

    /// <summary>Screen rect of a spell-bar slot (0=Q, 1=E, 2-9 = keys 1-8),
    /// laid out like the physical keyboard: numbers in a row on top, Q/E on a
    /// row below offset half a key right, with a one-key gap between them
    /// (W's spot). Drawing, mouse hit-testing and the hit registry all derive
    /// from this one function.</summary>
    public Rectangle GetSlotRect(int screenW, int screenH, int slot)
    {
        int numberLeft = screenW / 2 - NumberRowCenterOffset;
        int qeY = screenH - PrimaryBarBottomOffset;
        if (slot < 2)
        {
            int x = numberLeft + SlotPitch / 2 + slot * 2 * SlotPitch;
            return new Rectangle(x, qeY, PrimarySlotW, PrimarySlotH);
        }
        int numberY = qeY - PrimarySlotH - RowGap;
        return new Rectangle(numberLeft + (slot - 2) * SlotPitch, numberY, PrimarySlotW, PrimarySlotH);
    }

    /// <summary>Top edge of the spell-bar cluster (the raised number row) —
    /// the aggression bar anchors just above this.</summary>
    public int GetSpellBarTopY(int screenH)
        => screenH - PrimaryBarBottomOffset - PrimarySlotH - RowGap;

    /// <summary>
    /// Hit-test a spell bar slot. Returns the slot index, or -1 if not hit.
    /// Uses the same layout as drawing (GetSlotRect).
    /// </summary>
    public int HitTestBarSlot(int screenW, int screenH, int mouseX, int mouseY,
        int slotCount = SpellBarBindings.SlotCount)
    {
        for (int s = 0; s < slotCount; s++)
            if (GetSlotRect(screenW, screenH, s).Contains(mouseX, mouseY))
                return s;
        return -1;
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

    /// <summary>Catalogue the HUD's persistent clickable regions into the central
    /// UI hit registry: spell-bar slots, the time-control strip, and the two
    /// top-right button rows. Uses the exact same layout code as drawing and the
    /// HitTest* methods, so the registry can never desync from the visuals.</summary>
    public void AppendHitRects(Necroking.UI.UIHitRegistry reg, int screenW, int screenH,
        bool showTimeControls)
    {
        for (int s = 0; s < SpellBarBindings.SlotCount; s++)
            reg.Add($"hud.spellbar.{s}", GetSlotRect(screenW, screenH, s));

        if (showTimeControls)
        {
            TimeControlLayout(screenW, screenH, out int tcBaseX, out int tcBaseY);
            int tcW = TcBtnW + TcGap + TcCount * TcBtnW + (TcCount - 1) * TcGap;
            reg.Add("hud.time_controls", new Rectangle(tcBaseX, tcBaseY, tcW, TcBtnH));
        }

        if (LayoutButtonRow(MenuButtonLabels, MenuBtnTop, screenW, _menuBtnRects))
            for (int i = 0; i < MenuBtnCount; i++)
                reg.Add($"hud.menu_row.{i}", _menuBtnRects[i]);
        if (LayoutButtonRow(EditorButtonLabels, EditorBtnTop, screenW, _editorBtnRects))
            for (int i = 0; i < EditorBtnCount; i++)
                reg.Add($"hud.editor_row.{i}", _editorBtnRects[i]);
    }

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
            var bls = new List<string> {
                def.Name.Length > 0 ? def.Name : def.Id,
                $"HP: {rt.HP}/{def.BuildingMaxHP}",
                $"Owner: {ownerStr}",
                procStr
            };
            // Corpse Pile: list the bodies stored here so the player can see what's
            // available to gather/reanimate without opening anything.
            if (string.Equals(def.StoredResource, "Corpse", System.StringComparison.OrdinalIgnoreCase)
                && sim.Workers != null)
            {
                var corpseLines = sim.Workers.PiledCorpseLines(hoveredIdx);
                if (corpseLines.Count > 0)
                {
                    bls.Add("Corpses:");
                    bls.AddRange(corpseLines);
                }
                else bls.Add("Empty");
            }
            lines = bls.ToArray();
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
        else
        {
            // Generic env object (tree, rock, prop, …) — only reachable from the map
            // editor's hover-inspect and the hover_obj dev override; the gameplay pick
            // never returns these. Programmer-facing: name plus the def id when they differ.
            string title = def.Name.Length > 0 ? def.Name : def.Id;
            lines = title == def.Id ? new[] { title } : new[] { title, $"id: {def.Id}" };
        }

        DrawCursorTooltip(lines, screenW, screenH);
    }

    /// <summary>Floating cursor tooltip for a hovered forager (zombie boar): its name
    /// plus a corpse-pile-style list of the mushrooms in its belly. Replaces the normal
    /// right-side unit stat sheet (suppressed for foragers in Game1's hover/'L' paths).</summary>
    private void DrawBellyTooltip(uint unitId, Simulation sim, GameData gameData,
        int screenW, int screenH)
    {
        if (unitId == uint.MaxValue || _smallFont == null) return;
        int idx = sim.ResolveUnitID(unitId);
        if (idx < 0) return;

        var def = gameData.Units.Get(sim.Units[idx].UnitDefID);
        string name = def != null && def.DisplayName.Length > 0 ? def.DisplayName
                    : sim.Units[idx].UnitDefID;

        var lines = new List<string> { name, "Belly:" };
        var belly = sim.BoarBellyLines(unitId);
        if (belly.Count > 0) lines.AddRange(belly);
        else lines.Add("Empty");

        DrawCursorTooltip(lines.ToArray(), screenW, screenH);
    }

    /// <summary>Floating cursor tooltip for the hovered corpse: unit name plus its
    /// reanimation-relevant state (decaying / part-eaten). Gated upstream in Game1
    /// by the ShowCorpseInfo toggle.</summary>
    private void DrawCorpseTooltip(int hoveredCorpseIdx, Simulation sim, GameData gameData,
        int screenW, int screenH)
    {
        if (hoveredCorpseIdx < 0 || hoveredCorpseIdx >= sim.Corpses.Count || _smallFont == null) return;

        var cp = sim.Corpses[hoveredCorpseIdx];
        var def = !string.IsNullOrEmpty(cp.UnitDefID) ? gameData.Units.Get(cp.UnitDefID) : null;
        string name = def != null && def.DisplayName.Length > 0 ? def.DisplayName : cp.UnitType.ToString();

        DrawCursorTooltip(new[] { $"{name} corpse" }, screenW, screenH);
    }

    /// <summary>Floating cursor tooltip for the hovered unit — the lightweight
    /// "what is this" readout matching buildings/foragables/corpses: display name,
    /// HP, and a membership line (in the player horde, in a named village, or its
    /// faction). Deliberately NOT the full stat sheet (that's UnitInfoPanel).
    /// Suppressed for foragers (belly tooltip owns them) and while the auto stat
    /// sheet is enabled (it already shows everything) — except in editor inspect
    /// mode, where the auto stat sheet doesn't run (it's gameplay-only) so this
    /// tooltip is the only unit readout and always shows.</summary>
    private void DrawUnitTooltip(int hoveredIdx, Simulation sim, GameData gameData,
        int screenW, int screenH, bool editorInspect = false)
    {
        if (hoveredIdx < 0 || hoveredIdx >= sim.Units.Count || _smallFont == null) return;
        if (!editorInspect)
        {
            if (!gameData.Settings.Tooltips.ShowUnitInfo) return;
            if (gameData.Settings.Tooltips.AutoShowUnitStats) return; // stat sheet supersedes
        }

        var u = sim.Units[hoveredIdx];
        if (!u.Alive) return;

        var def = gameData.Units.Get(u.UnitDefID);
        if (def?.Tags.Contains("forager") == true) return; // belly tooltip owns foragers

        string name = def != null && def.DisplayName.Length > 0 ? def.DisplayName : u.UnitDefID;

        // What does it belong to? Horde > named village > bare faction.
        string membership;
        if (u.Faction == Faction.Undead && sim.Horde.IsInHorde(u.Id))
            membership = "in player horde";
        else if (u.VillageId >= 0 && sim.Villages?.Get(u.VillageId) is { } vil)
            membership = vil.Name.Length > 0 ? $"in {vil.Name}" : "in a village";
        else
            membership = u.Faction switch
            {
                Faction.Undead => "Undead",
                Faction.Human  => "Human",
                _              => "Wildlife",
            };

        DrawCursorTooltip(new[]
        {
            name,
            $"HP: {u.Stats.HP}/{u.Stats.MaxHP}",
            membership,
        }, screenW, screenH);
    }

    /// <summary>Floating cursor tooltip for the hovered spell-bar slot: ability name
    /// plus school and any mana/cooldown/charge info. Populated by DrawSpellBar via
    /// _hoverSlotSpell / _hoverSlotMelee; a no-op when nothing is hovered.</summary>
    private void DrawSpellSlotTooltip(GameData gameData, Inventory inventory, int screenW, int screenH)
    {
        if (_smallFont == null) return;

        var lines = new List<string>();
        if (_hoverSlotMelee)
        {
            lines.Add("Melee / Gather");
            lines.Add("Strike the nearest enemy, or gather at the cursor.");
        }
        else if (_hoverSlotSpell != null)
        {
            var sp = _hoverSlotSpell;
            lines.Add(gameData.Spells.NameOf(sp.Id));

            string kind = !string.IsNullOrEmpty(sp.School) ? sp.School
                        : !string.IsNullOrEmpty(sp.Category) ? sp.Category : "";
            if (kind.Length > 0) lines.Add(kind);

            // Cost / cooldown — only the parts that apply (orders like Command have neither).
            var stats = new List<string>();
            if (sp.ManaCost > 0f) stats.Add($"{sp.ManaCost:0.#} mana");
            if (sp.Cooldown > 0f) stats.Add($"{sp.Cooldown:0.#}s cooldown");
            if (stats.Count > 0) lines.Add(string.Join("   ", stats));

            // Consumable charges (potion-spells): show how many the player holds.
            if (!string.IsNullOrEmpty(sp.ConsumesItem))
            {
                var item = gameData.Items.Get(sp.ConsumesItem);
                string itemName = item != null && item.DisplayName.Length > 0 ? item.DisplayName : sp.ConsumesItem;
                lines.Add($"Held: {inventory.GetItemCount(sp.ConsumesItem)} {itemName}");
            }
        }
        else return;

        DrawCursorTooltip(lines.ToArray(), screenW, screenH);
    }

    /// <summary>Cursor-anchored text tooltip — box style, placement, and the
    /// topmost draw all live in the global <see cref="Game1.Tooltips"/> queue.</summary>
    private void DrawCursorTooltip(string[] lines, int screenW, int screenH)
    {
        Game1.Tooltips.RequestLines(lines);
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
            Scope.Draw(_pixel, new Rectangle(baseX, baseY, pauseW, TcBtnH), bg);
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
            Scope.Draw(_pixel, new Rectangle(bx, baseY, TcBtnW, TcBtnH), bg);
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
                CombatLogOutcome.Whiff => $"{e.DefenderName} escaped {e.AttackerName}'s attack{weap}",
                CombatLogOutcome.NoteOnly => $"{e.Note}",
                _ => ""
            };
            if (logLine.Length == 0) continue; // unknown outcome: don't burn a visible slot on a blank line

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
            Scope.DrawString(font, text, new Vector2((int)pos.X, (int)pos.Y), color);
    }

    /// <summary>Text with a 1px 8-direction rim behind the face, for labels that
    /// must stay legible over arbitrary art (spell icons). SpriteFont has no
    /// stroke effect (unlike the FontStash widget path), hence the offset passes.</summary>
    private void TextOutlined(SpriteFont? font, string text, Vector2 pos, Color color, Color outline)
    {
        if (font == null) return;
        int x = (int)pos.X, y = (int)pos.Y;
        for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
                if (dx != 0 || dy != 0)
                    Scope.DrawString(font, text, new Vector2(x + dx, y + dy), outline);
        Scope.DrawString(font, text, new Vector2(x, y), color);
    }

    private static int FindNecromancer(Simulation sim)
    {
        return sim.NecromancerIndex;
    }
}
