using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
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

    // Preview state
    private string _selectedFile = "";          // highlighted file (not yet committed)
    private Texture2D? _previewTexture;
    private GraphicsDevice? _graphicsDevice;
    private readonly Dictionary<string, Texture2D> _textureCache = new();

    // Scroll state
    private float _scrollOffset;

    // Layout constants
    private const int PopupW = 660;
    private const int PopupH = 520;
    private const int PreviewW = 160;
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

    public void SetGraphicsDevice(GraphicsDevice device) => _graphicsDevice = device;

    /// <summary>
    /// Open the file browser popup.
    /// </summary>
    public void Open(string rootDir, string currentPath, Action<string> onSelect)
    {
        _isOpen = true;
        _onSelect = onSelect;
        _filterText = "";
        _scrollOffset = 0;
        _selectedFile = "";
        _previewTexture = null;

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

        var mouse = ui._input.Mouse;

        // Block input to layers beneath
        ui.InputLayer = Math.Max(ui.InputLayer, 1);

        // Dark overlay
        ui.DrawRect(new Rectangle(0, 0, screenW, screenH), OverlayBg);

        // Center the popup
        int totalW = PopupW;
        int px = (screenW - totalW) / 2;
        int py = (screenH - PopupH) / 2;
        int listW = PopupW - PreviewW;

        // Background + border
        ui.DrawRect(new Rectangle(px, py, totalW, PopupH), PopupBg);
        ui.DrawBorder(new Rectangle(px, py, totalW, PopupH), BorderColor, 2);

        // Title bar
        ui.DrawRect(new Rectangle(px, py, PopupW, TitleBarH), HeaderBg);
        ui.DrawRect(new Rectangle(px, py + TitleBarH, PopupW, 1), BorderColor);
        ui.DrawText("Texture File Browser", new Vector2(px + Padding, py + 5), EditorBase.TextBright);

        // Close button (X) in title bar — temporarily allow input
        int closeBtnX = px + totalW - 30;
        int closeBtnY = py + 2;
        int layerForClose = ui.InputLayer;
        ui.InputLayer = 0;
        if (ui.DrawButton("X", closeBtnX, closeBtnY, 24, 24, EditorBase.DangerColor))
        {
            ui.InputLayer = layerForClose;
            Close();
            return;
        }
        ui.InputLayer = layerForClose;

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

        int scrollInt = (int)_scrollOffset;
        for (int i = 0; i < displayEntries.Count; i++)
        {
            var entry = displayEntries[i];

            int iy = contentY + i * ItemH - scrollInt;
            if (iy + ItemH < contentY) continue;
            if (iy >= contentY + contentH) break;

            var itemRect = new Rectangle(px + 2, iy, PopupW - 4, ItemH);
            bool hovered = itemRect.Contains(mouse.X, mouse.Y) && contentRect.Contains(mouse.X, mouse.Y);

            bool isSelected = !entry.IsDirectory && entry.FullPath.Replace('\\', '/') == _selectedFile;
            if (isSelected)
                ui.DrawRect(itemRect, SelectedBg);
            if (hovered)
            {
                if (!isSelected) ui.DrawRect(itemRect, HoverBg);

                // Click handling
                if (mouse.LeftButton == ButtonState.Pressed && _prevMouseState.LeftButton == ButtonState.Released)
                {
                    if (entry.IsDirectory)
                    {
                        NavigateTo(entry.FullPath);
                    }
                    else
                    {
                        // File clicked — select for preview (don't commit yet)
                        _selectedFile = entry.FullPath.Replace('\\', '/');
                        _previewTexture = LoadPreviewTexture(_selectedFile);
                    }
                }
            }

            // Icon/prefix and name
            if (entry.IsUpDir)
            {
                ui.DrawText("[..] Parent Directory", new Vector2(px + Padding, itemRect.Y + 3), DirColor);
            }
            else if (entry.IsDirectory)
            {
                ui.DrawText("[DIR] " + entry.Name, new Vector2(px + Padding, itemRect.Y + 3), DirColor);
            }
            else
            {
                ui.DrawText("      " + entry.Name, new Vector2(px + Padding, itemRect.Y + 3), FileColor);
            }
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

        // Preview panel (right side)
        int previewX = px + listW;
        int previewY = py + TitleBarH + 2;
        int previewH = PopupH - TitleBarH - FooterH - 4;
        ui.DrawRect(new Rectangle(previewX, previewY, PreviewW, previewH), new Color(15, 15, 25, 240));
        ui.DrawRect(new Rectangle(previewX, previewY, 1, previewH), BorderColor);

        if (_previewTexture != null)
        {
            // Scale to fit preview area with padding
            int padded = PreviewW - 16;
            float scale = Math.Min((float)padded / _previewTexture.Width, (float)(previewH - 60) / _previewTexture.Height);
            scale = Math.Min(scale, 1f);
            int drawW = (int)(_previewTexture.Width * scale);
            int drawH = (int)(_previewTexture.Height * scale);
            int drawX = previewX + (PreviewW - drawW) / 2;
            int pvDrawY = previewY + 8;

            // Checkerboard background for transparency
            for (int cy2 = pvDrawY; cy2 < pvDrawY + drawH; cy2 += 8)
                for (int cx2 = drawX; cx2 < drawX + drawW; cx2 += 8)
                {
                    bool dark = ((cx2 - drawX) / 8 + (cy2 - pvDrawY) / 8) % 2 == 0;
                    ui.DrawRect(new Rectangle(cx2, cy2,
                        Math.Min(8, drawX + drawW - cx2), Math.Min(8, pvDrawY + drawH - cy2)),
                        dark ? new Color(35, 35, 35) : new Color(55, 55, 55));
                }

            ui.SpriteBatch.Draw(_previewTexture, new Rectangle(drawX, pvDrawY, drawW, drawH), Color.White);
            ui.DrawText($"{_previewTexture.Width}x{_previewTexture.Height}", new Vector2(previewX + 8, pvDrawY + drawH + 4), EditorBase.TextDim);

            // Show filename
            string fname = Path.GetFileName(_selectedFile);
            ui.DrawText(fname, new Vector2(previewX + 8, pvDrawY + drawH + 20), EditorBase.TextBright);
        }
        else
        {
            ui.DrawText("No preview", new Vector2(previewX + 8, previewY + 8), EditorBase.TextDim);
        }

        // Footer: Use + Cancel buttons (temporarily lower InputLayer so buttons work)
        int footerY = py + PopupH - FooterH + 4;
        bool hasSelection = !string.IsNullOrEmpty(_selectedFile);
        int savedLayer = ui.InputLayer;
        ui.InputLayer = 0;

        if (hasSelection && ui.DrawButton("Use", px + totalW - 170, footerY, 70, 24, EditorBase.AccentColor))
        {
            ui.InputLayer = savedLayer;
            _onSelect?.Invoke(Necroking.Core.GamePaths.MakeRelative(_selectedFile));
            Close();
            _prevMouseState = mouse;
            return;
        }
        if (ui.DrawButton("Cancel", px + totalW - 80, footerY, 70, 24, EditorBase.DangerColor))
        {
            ui.InputLayer = savedLayer;
            Close();
            _prevMouseState = mouse;
            return;
        }
        ui.InputLayer = savedLayer;

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

    private Texture2D? LoadPreviewTexture(string path)
    {
        if (string.IsNullOrEmpty(path) || _graphicsDevice == null) return null;
        if (_textureCache.TryGetValue(path, out var cached)) return cached;
        string resolved = System.IO.Path.IsPathRooted(path) ? path : Necroking.Core.GamePaths.Resolve(path);
        if (!File.Exists(resolved)) return null;
        try
        {
            var tex = Necroking.Render.TextureUtil.LoadPremultiplied(_graphicsDevice, path);
            _textureCache[path] = tex;
            return tex;
        }
        catch { return null; }
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
