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
public class TextureFileBrowser : Necroking.UI.IModalLayer
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
    private readonly Necroking.Render.TextureCache _textureCache = new();

    // Scroll state
    private float _scrollOffset;

    // Overlay layer for the editor widget system: above sub-editor/manager
    // popups (1), below confirm dialogs (3).
    private const int OverlayLayer = 2;

    // Footer warning (e.g. picking a file outside the project root)
    private string _statusMsg = "";

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
    public void Open(string rootDir, string currentPath, Action<string> onSelect, string? defaultDir = null)
    {
        _isOpen = true;
        _onSelect = onSelect;
        _filterText = "";
        _scrollOffset = 0;
        _selectedFile = "";
        _previewTexture = null;

        Necroking.Game1.Popups.Push(this);

        // Resolve the root to an ABSOLUTE path. Callers pass project-relative roots
        // like "assets", but the process working directory is the exe folder (e.g.
        // bin/Publish) where "assets" doesn't exist — so the old relative path fell
        // through to "." and the browser opened in the exe folder (the "unfamiliar
        // directory"). The root also bounds how far ".." can climb up.
        _rootDir = ResolveDir(string.IsNullOrEmpty(rootDir) ? Necroking.Core.GamePaths.AssetsDir : rootDir);

        // Start directory preference: the folder of the current value if it has
        // one, else the caller's default dir (e.g. assets/UI for UI images), else
        // the root. All resolved to absolute so they're valid regardless of CWD.
        string startDir = "";
        if (!string.IsNullOrEmpty(currentPath))
        {
            string dir = Path.GetDirectoryName(currentPath)?.Replace('\\', '/') ?? "";
            if (!string.IsNullOrEmpty(dir)) startDir = ResolveDir(dir);
        }
        if (string.IsNullOrEmpty(startDir) && !string.IsNullOrEmpty(defaultDir))
            startDir = ResolveDir(defaultDir);

        _currentDir = (!string.IsNullOrEmpty(startDir) && Directory.Exists(startDir)) ? startDir : _rootDir;

        // Last-resort fallbacks if neither the start dir nor the root exists.
        if (!Directory.Exists(_currentDir))
        {
            _currentDir = Directory.Exists(_rootDir) ? _rootDir : ResolveDir(Necroking.Core.GamePaths.AssetsDir);
            if (!Directory.Exists(_currentDir)) _currentDir = ".";
        }

        RefreshListing();
    }

    /// <summary>Resolve a possibly project-relative path to an absolute, forward-
    /// slashed, trailing-slash-trimmed directory. Already-absolute paths pass
    /// through. Keeps the browser correct no matter the process working dir.</summary>
    private static string ResolveDir(string p)
    {
        if (string.IsNullOrEmpty(p)) return p;
        string resolved = Path.IsPathRooted(p) ? p : Necroking.Core.GamePaths.Resolve(p);
        return resolved.Replace('\\', '/').TrimEnd('/');
    }

    public void Close()
    {
        _isOpen = false;
        _onSelect = null;
        _statusMsg = "";
        // Preview textures are session-scoped: keep them cached while browsing
        // (avoids reloading on every click), release them all on close so VRAM
        // doesn't grow unboundedly across sessions.
        _textureCache.DisposeAll();
        _previewTexture = null;
        Necroking.Game1.Popups.Pop(this);
    }

    /// <summary>
    /// Update input. Call each frame while IsOpen is true.
    /// </summary>
    public void Update(EditorBase ui, MouseState mouse, MouseState prevMouse, KeyboardState kb, KeyboardState prevKb)
    {
        if (!_isOpen) return;
        // Declare the overlay NOW (in Update, not Draw) so any HandlePanelScroll /
        // DrawScrollableList calls in the underlying editor's Draw pass that run
        // before our own Draw already see IsInputBlocked == true this frame.
        ui.NotifyOverlay(OverlayLayer);
        // ESC handling moved to OnCancel — PopupManager dispatches when the
        // browser is the top of the stack.
    }

    // === IModalLayer ===
    public bool LightDismiss => false;  // hard modal; outside click doesn't close
    public bool IsBlocking => true;
    public bool ContainsMouse(int mx, int my) => _panelRect.Contains(mx, my);
    public void OnCancel() => Close();
    /// <summary>Updated by Draw each frame so PopupManager has the live screen
    /// rect for outside-hit-tests. Browser is centered, so changes only when
    /// the window resizes.</summary>
    private Microsoft.Xna.Framework.Rectangle _panelRect;

    /// <summary>
    /// Draw the file browser overlay. Call each frame while IsOpen is true.
    /// Uses EditorBase drawing primitives for consistent look.
    /// </summary>
    public void Draw(EditorBase ui, int screenW, int screenH)
    {
        if (!_isOpen) return;

        var mouse = ui.GetMouseState();
        var prevMouse = ui.GetPrevMouseState();

        // Overlay contract: blocks the host's widgets (this frame + next-frame
        // pre-raise) and lets our own widgets interact at OverlayLayer.
        ui.BeginOverlay(OverlayLayer);

        // Dark overlay
        ui.DrawRect(new Rectangle(0, 0, screenW, screenH), OverlayBg);

        // Center the popup
        int totalW = PopupW;
        int px = (screenW - totalW) / 2;
        int py = (screenH - PopupH) / 2;
        int listW = PopupW - PreviewW;

        // Cache rect for IModalLayer.ContainsMouse so PopupManager can decide
        // outside-vs-inside on click events the same frame they happen.
        _panelRect = new Rectangle(px, py, totalW, PopupH);

        // Background + border
        ui.DrawRect(new Rectangle(px, py, totalW, PopupH), PopupBg);
        ui.DrawBorder(new Rectangle(px, py, totalW, PopupH), BorderColor, 2);

        // Title bar
        ui.DrawRect(new Rectangle(px, py, PopupW, TitleBarH), HeaderBg);
        ui.DrawRect(new Rectangle(px, py + TitleBarH, PopupW, 1), BorderColor);
        ui.DrawText("Texture File Browser", new Vector2(px + Padding, py + 5), EditorBase.TextBright);

        // Close button (X) in title bar
        int closeBtnX = px + totalW - 30;
        int closeBtnY = py + 2;
        if (ui.DrawButton("X", closeBtnX, closeBtnY, 24, 24, EditorBase.DangerColor))
        {
            Close();
            ui.EndOverlay();
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

        // Content area — the list occupies only the left column; the preview
        // panel owns the right PreviewW pixels (rows must not extend under it).
        int contentY = curY;
        int contentH = PopupH - (curY - py) - FooterH;
        var contentRect = new Rectangle(px + 2, contentY, listW - 4, contentH);
        ui.DrawRect(contentRect, new Color(20, 20, 35, 200));

        // Build filtered list of entries
        var displayEntries = BuildDisplayEntries();
        int totalItemH = displayEntries.Count * ItemH;

        // Mouse wheel scrolling (consumes the shared scroll flag so it can't
        // also scroll a panel beneath). Re-clamp every frame so a shrinking
        // filtered list can't leave the offset stranded past the end.
        float maxScroll = Math.Max(0, totalItemH - contentH);
        ui.HandlePanelScroll(contentRect, ref _scrollOffset, maxScroll, 0.15f);
        _scrollOffset = Math.Clamp(_scrollOffset, 0, maxScroll);

        bool blocked = ui.IsInputBlocked(ui.EffectiveLayer(0));

        // Rows under the scrollbar column must not react to clicks meant for the bar.
        var sbHit = totalItemH > contentH
            ? EditorBase.VScrollbarHitRect(px + listW - 8, contentY, contentH)
            : Rectangle.Empty;

        // Draw entries with clipping
        ui.BeginClip(contentRect);

        int scrollInt = (int)_scrollOffset;
        for (int i = 0; i < displayEntries.Count; i++)
        {
            var entry = displayEntries[i];

            int iy = contentY + i * ItemH - scrollInt;
            if (iy + ItemH < contentY) continue;
            if (iy >= contentY + contentH) break;

            var itemRect = new Rectangle(px + 2, iy, listW - 4, ItemH);
            bool hovered = !blocked && ui.HitTest(itemRect) && !sbHit.Contains(mouse.X, mouse.Y);

            bool isSelected = !entry.IsDirectory && entry.FullPath.Replace('\\', '/') == _selectedFile;
            if (isSelected)
                ui.DrawRect(itemRect, SelectedBg);
            if (hovered)
            {
                if (!isSelected) ui.DrawRect(itemRect, HoverBg);

                // Click handling
                if (mouse.LeftButton == ButtonState.Pressed && prevMouse.LeftButton == ButtonState.Released)
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
                        _statusMsg = "";
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

        // Scrollbar — inside the list column, not under the preview panel (draggable).
        if (totalItemH > contentH)
            _scrollOffset = ui.DrawVScrollbar("texbrowser_files", px + listW - 8, contentY, contentH, totalItemH, _scrollOffset);

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

            ui.Scope.Draw(_previewTexture, new Rectangle(drawX, pvDrawY, drawW, drawH), Color.White);
            ui.DrawText($"{_previewTexture.Width}x{_previewTexture.Height}", new Vector2(previewX + 8, pvDrawY + drawH + 4), EditorBase.TextDim);

            // Show filename
            string fname = Path.GetFileName(_selectedFile);
            ui.DrawText(fname, new Vector2(previewX + 8, pvDrawY + drawH + 20), EditorBase.TextBright);
        }
        else
        {
            ui.DrawText("No preview", new Vector2(previewX + 8, previewY + 8), EditorBase.TextDim);
        }

        // Footer: Use + Cancel buttons
        int footerY = py + PopupH - FooterH + 4;
        bool hasSelection = !string.IsNullOrEmpty(_selectedFile);

        if (!string.IsNullOrEmpty(_statusMsg))
            ui.DrawText(_statusMsg, new Vector2(px + Padding, footerY + 4), EditorBase.DangerColor);

        if (hasSelection && ui.DrawButton("Use", px + totalW - 170, footerY, 70, 24, EditorBase.AccentColor))
        {
            // Never persist an absolute path: asset paths in JSON must be
            // project-relative. MakeRelative returns its input unchanged when
            // the file is outside the project root — refuse instead of committing.
            string rel = Necroking.Core.GamePaths.MakeRelative(_selectedFile);
            if (Path.IsPathRooted(rel))
            {
                _statusMsg = "File is outside the project — pick one under assets/";
            }
            else
            {
                _onSelect?.Invoke(rel);
                Close();
                ui.EndOverlay();
                return;
            }
        }
        if (ui.DrawButton("Cancel", px + totalW - 80, footerY, 70, 24, EditorBase.DangerColor))
        {
            Close();
            ui.EndOverlay();
            return;
        }

        ui.EndOverlay();
    }

    // ================================================================
    //  Internal helpers
    // ================================================================

    private void NavigateTo(string dir)
    {
        _currentDir = dir.Replace('\\', '/');
        _scrollOffset = 0;
        RefreshListing();
    }

    private Texture2D? LoadPreviewTexture(string path) => _textureCache.GetOrLoad(_graphicsDevice, path);

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

        // ".." entry to go up — never above the root. If Open() landed us
        // OUTSIDE the root (current value pointed elsewhere), the up-entry
        // jumps straight back to the root instead of climbing the drive.
        bool insideRoot = normalizedCurrent.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
        bool canGoUp = insideRoot && normalizedCurrent.Length > normalizedRoot.Length;

        if (canGoUp)
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
        else if (!insideRoot)
        {
            entries.Add(new DisplayEntry
            {
                Name = "..",
                FullPath = _rootDir,
                IsDirectory = true,
                IsUpDir = true
            });
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
