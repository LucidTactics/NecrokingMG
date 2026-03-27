using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;

namespace Necroking.Editor;

/// <summary>
/// Reusable modal overlay for browsing PNG files on the filesystem.
/// Opens over the current editor, shows directory listing with navigation,
/// filter field, and returns the selected relative path via callback.
/// </summary>
public class TextureFileBrowser
{
    // Popup state
    private bool _isOpen;
    private string _rootDir = "";
    private string _currentDir = "";
    private Action<string>? _onSelect;

    // Directory listing cache
    private List<string> _dirs = new();
    private List<string> _files = new();
    private string _filterText = "";

    // Scroll state
    private float _scrollOffset;

    // Layout constants
    private const int PopupW = 500;
    private const int PopupH = 520;
    private const int TitleBarH = 28;
    private const int PathBarH = 24;
    private const int FilterBarH = 26;
    private const int ItemH = 22;
    private const int Padding = 8;
    private const int FooterH = 32;

    // Colors
    private static readonly Color OverlayBg = new(0, 0, 0, 180);
    private static readonly Color PopupBg = new(25, 25, 40, 245);
    private static readonly Color HeaderBg = new(40, 40, 65, 250);
    private static readonly Color BorderColor = new(80, 80, 120);
    private static readonly Color DirColor = new(120, 180, 255);
    private static readonly Color FileColor = new(200, 200, 220);
    private static readonly Color HoverBg = new(50, 50, 80, 230);
    private static readonly Color SelectedBg = new(60, 60, 110, 240);

    public bool IsOpen => _isOpen;

    /// <summary>
    /// Open the file browser popup.
    /// </summary>
    /// <param name="rootDir">Root directory to browse from (e.g., "assets/Environment/").
    /// If empty, defaults to "assets/".</param>
    /// <param name="currentPath">Current texture path value, used to set initial directory.</param>
    /// <param name="onSelect">Callback invoked with the selected relative path (e.g., "assets/Environment/Ground/Grass.png").</param>
    public void Open(string rootDir, string currentPath, Action<string> onSelect)
    {
        _isOpen = true;
        _onSelect = onSelect;
        _filterText = "";
        _scrollOffset = 0;

        // Normalize root dir
        _rootDir = string.IsNullOrEmpty(rootDir) ? "assets" : rootDir.TrimEnd('/', '\\');

        // If currentPath has a directory portion, start there
        if (!string.IsNullOrEmpty(currentPath))
        {
            string dir = Path.GetDirectoryName(currentPath)?.Replace('\\', '/') ?? "";
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                _currentDir = dir;
            else
                _currentDir = _rootDir;
        }
        else
        {
            _currentDir = _rootDir;
        }

        // Ensure the directory exists; fall back to root
        if (!Directory.Exists(_currentDir))
        {
            _currentDir = _rootDir;
            if (!Directory.Exists(_currentDir))
                _currentDir = "assets";
            if (!Directory.Exists(_currentDir))
                _currentDir = ".";
        }

        RefreshListing();
    }

    public void Close()
    {
        _isOpen = false;
        _onSelect = null;
    }

    /// <summary>
    /// Update input. Call each frame while IsOpen is true.
    /// </summary>
    public void Update(MouseState mouse, MouseState prevMouse, KeyboardState kb, KeyboardState prevKb)
    {
        if (!_isOpen) return;

        // Escape to close
        if (kb.IsKeyDown(Keys.Escape) && prevKb.IsKeyUp(Keys.Escape))
        {
            Close();
        }
    }

    /// <summary>
    /// Draw the file browser overlay. Call each frame while IsOpen is true.
    /// Uses EditorBase drawing primitives for consistent look.
    /// </summary>
    public void Draw(EditorBase ui, int screenW, int screenH)
    {
        if (!_isOpen) return;

        var mouse = Mouse.GetState();

        // Block input to layers beneath
        ui.InputLayer = Math.Max(ui.InputLayer, 1);

        // Dark overlay
        ui.DrawRect(new Rectangle(0, 0, screenW, screenH), OverlayBg);

        // Center the popup
        int px = (screenW - PopupW) / 2;
        int py = (screenH - PopupH) / 2;

        // Background + border
        ui.DrawRect(new Rectangle(px, py, PopupW, PopupH), PopupBg);
        ui.DrawBorder(new Rectangle(px, py, PopupW, PopupH), BorderColor, 2);

        // Title bar
        ui.DrawRect(new Rectangle(px, py, PopupW, TitleBarH), HeaderBg);
        ui.DrawRect(new Rectangle(px, py + TitleBarH, PopupW, 1), BorderColor);
        ui.DrawText("Texture File Browser", new Vector2(px + Padding, py + 5), EditorBase.TextBright);

        // Close button (X) in title bar
        int closeBtnX = px + PopupW - 30;
        int closeBtnY = py + 2;
        if (ui.DrawButton("X", closeBtnX, closeBtnY, 24, 24, EditorBase.DangerColor))
        {
            Close();
            return;
        }

        int curY = py + TitleBarH + 2;

        // Path bar: show current directory
        ui.DrawRect(new Rectangle(px + 2, curY, PopupW - 4, PathBarH), EditorBase.InputBg);
        string displayDir = _currentDir.Replace('\\', '/');
        ui.DrawText(displayDir, new Vector2(px + Padding, curY + 4), EditorBase.TextDim);
        curY += PathBarH + 2;

        // Filter text field
        _filterText = ui.DrawSearchField("texbrowser_filter", _filterText, px + Padding, curY, PopupW - Padding * 2);
        curY += FilterBarH + 2;

        // Content area
        int contentY = curY;
        int contentH = PopupH - (curY - py) - FooterH;
        var contentRect = new Rectangle(px + 2, contentY, PopupW - 4, contentH);
        ui.DrawRect(contentRect, new Color(20, 20, 35, 200));

        // Build filtered list of entries
        var displayEntries = BuildDisplayEntries();
        int totalItemH = displayEntries.Count * ItemH;

        // Mouse wheel scrolling
        if (contentRect.Contains(mouse.X, mouse.Y))
        {
            int scrollDelta = mouse.ScrollWheelValue - _prevScrollValue;
            if (scrollDelta != 0)
            {
                _scrollOffset -= scrollDelta * 0.15f;
                float maxScroll = Math.Max(0, totalItemH - contentH);
                _scrollOffset = Math.Clamp(_scrollOffset, 0, maxScroll);
            }
        }
        _prevScrollValue = mouse.ScrollWheelValue;

        // Draw entries with clipping
        ui.BeginClip(contentRect);

        float drawY = contentY - _scrollOffset;
        for (int i = 0; i < displayEntries.Count; i++)
        {
            var entry = displayEntries[i];

            if (drawY + ItemH < contentY) { drawY += ItemH; continue; }
            if (drawY >= contentY + contentH) break;

            var itemRect = new Rectangle(px + 2, (int)drawY, PopupW - 4, ItemH);
            bool hovered = itemRect.Contains(mouse.X, mouse.Y) && contentRect.Contains(mouse.X, mouse.Y);

            if (hovered)
            {
                ui.DrawRect(itemRect, HoverBg);

                // Click handling
                if (mouse.LeftButton == ButtonState.Pressed && _prevMouseState.LeftButton == ButtonState.Released)
                {
                    if (entry.IsDirectory)
                    {
                        NavigateTo(entry.FullPath);
                    }
                    else
                    {
                        // File selected - return relative path
                        string relativePath = entry.FullPath.Replace('\\', '/');
                        _onSelect?.Invoke(relativePath);
                        Close();
                        ui.EndClip();
                        return;
                    }
                }
            }

            // Icon/prefix and name
            if (entry.IsUpDir)
            {
                ui.DrawText("[..] Parent Directory", new Vector2(px + Padding, drawY + 3), DirColor);
            }
            else if (entry.IsDirectory)
            {
                ui.DrawText("[DIR] " + entry.Name, new Vector2(px + Padding, drawY + 3), DirColor);
            }
            else
            {
                ui.DrawText("      " + entry.Name, new Vector2(px + Padding, drawY + 3), FileColor);
            }

            drawY += ItemH;
        }

        ui.EndClip();

        // Scrollbar
        if (totalItemH > contentH)
        {
            float scrollRatio = _scrollOffset / (totalItemH - contentH);
            int barH = Math.Max(20, contentH * contentH / totalItemH);
            int barY = contentY + (int)(scrollRatio * (contentH - barH));
            ui.DrawRect(new Rectangle(px + PopupW - 10, barY, 7, barH), new Color(100, 100, 140, 180));
        }

        // Footer: cancel button
        int footerY = py + PopupH - FooterH + 4;
        if (ui.DrawButton("Cancel", px + PopupW - 80, footerY, 70, 24, EditorBase.DangerColor))
        {
            Close();
            return;
        }

        // Store mouse for next frame click detection
        _prevMouseState = mouse;
    }

    // Previous mouse state for click detection inside Draw
    private MouseState _prevMouseState;
    private int _prevScrollValue;

    // ================================================================
    //  Internal helpers
    // ================================================================

    private void NavigateTo(string dir)
    {
        _currentDir = dir.Replace('\\', '/');
        _scrollOffset = 0;
        RefreshListing();
    }

    private void RefreshListing()
    {
        _dirs.Clear();
        _files.Clear();

        if (!Directory.Exists(_currentDir))
            return;

        try
        {
            // Get subdirectories
            foreach (var d in Directory.GetDirectories(_currentDir))
            {
                _dirs.Add(d.Replace('\\', '/'));
            }
            _dirs.Sort(StringComparer.OrdinalIgnoreCase);

            // Get PNG files
            foreach (var f in Directory.GetFiles(_currentDir, "*.png"))
            {
                _files.Add(f.Replace('\\', '/'));
            }
            _files.Sort(StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            // Directory access error - just show empty
        }
    }

    private struct DisplayEntry
    {
        public string Name;
        public string FullPath;
        public bool IsDirectory;
        public bool IsUpDir;
    }

    private List<DisplayEntry> BuildDisplayEntries()
    {
        var entries = new List<DisplayEntry>();
        string normalizedCurrent = _currentDir.Replace('\\', '/').TrimEnd('/');
        string normalizedRoot = _rootDir.Replace('\\', '/').TrimEnd('/');

        // ".." entry to go up (unless we're at or above the root)
        bool canGoUp = normalizedCurrent.Length > normalizedRoot.Length
                       && normalizedCurrent.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);

        // Also allow going up if we're not inside root at all (shouldn't normally happen, but be safe)
        if (!normalizedCurrent.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase))
        {
            string? parent = Path.GetDirectoryName(_currentDir)?.Replace('\\', '/');
            if (!string.IsNullOrEmpty(parent))
            {
                entries.Add(new DisplayEntry
                {
                    Name = "..",
                    FullPath = parent,
                    IsDirectory = true,
                    IsUpDir = true
                });
            }
        }

        // Directories
        foreach (var d in _dirs)
        {
            string name = Path.GetFileName(d);
            if (!string.IsNullOrEmpty(_filterText) &&
                !name.Contains(_filterText, StringComparison.OrdinalIgnoreCase))
                continue;

            entries.Add(new DisplayEntry
            {
                Name = name,
                FullPath = d,
                IsDirectory = true,
                IsUpDir = false
            });
        }

        // Files
        foreach (var f in _files)
        {
            string name = Path.GetFileName(f);
            if (!string.IsNullOrEmpty(_filterText) &&
                !name.Contains(_filterText, StringComparison.OrdinalIgnoreCase))
                continue;

            entries.Add(new DisplayEntry
            {
                Name = name,
                FullPath = f,
                IsDirectory = false,
                IsUpDir = false
            });
        }

        return entries;
    }
}
