using System;
using Necroking.Data;

namespace Necroking.UI;

/// <summary>
/// In-game wrapper for the GrimoireDyn widget ('J' to toggle). Phase 1:
/// display only — populated from the spell registry on open; tabs/scroll/
/// clicking are phase 2. Taller than most screens (1080), so it top-anchors
/// and clips at the bottom on small windows.
/// </summary>
public class GrimoireOverlay : IModalLayer
{
    private const string InstanceId = "grimoire_ingame";
    private const int PanelW = 706;
    private const int PanelH = 1080;

    private RuntimeWidgetRenderer _renderer = null!;
    private GameData? _gameData;
    private int _x, _y;

    public bool IsVisible { get; private set; }

    public void Init(RuntimeWidgetRenderer renderer, GameData gameData)
    {
        _renderer = renderer;
        _gameData = gameData;
    }

    public void Toggle()
    {
        if (IsVisible) { Hide(); return; }
        IsVisible = true;
        if (_gameData != null)
            GrimoirePanel.Populate(_renderer, _gameData, InstanceId);
        Game1.Popups.Push(this);
    }

    public void Hide()
    {
        if (!IsVisible) return;
        IsVisible = false;
        Game1.Popups.Pop(this);
    }

    public bool ContainsMouse(int mx, int my)
        => IsVisible && mx >= _x && mx < _x + PanelW && my >= _y && my < _y + PanelH;

    public void OnCancel() => Hide();
    public bool LightDismiss => false;
    public bool IsBlocking => false;

    public void Draw(int screenW, int screenH)
    {
        if (!IsVisible) return;
        _x = 16;
        _y = Math.Min(0, (screenH - PanelH) / 2);
        _renderer.DrawWidget(GrimoirePanel.WidgetId, _x, _y, InstanceId);
    }
}
