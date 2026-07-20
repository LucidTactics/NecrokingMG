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

    // Flipbook preview: grid parsed from the selected filename (0 = plain
    // image, static preview). Drawing/animation lives in FlipbookPreviewPanel.
    private int _previewCols;
    private int _previewRows;
    private long _previewAnimStart;

    // Scroll state
    private float _scrollOffset;

    // Keyboard navigation: cursor into the filtered display-entry list
    // (-1 = none). Rebuilt-every-frame entries are re-clamped in Draw.
    private int _keyboardIndex = -1;
    // Latch so the Enter that commits the search field doesn't also commit
    // the file selection the same frame (the field deactivates on Enter).
    private bool _textInputWasActive;

    // Overlay layer for the editor widget system: above sub-editor/manager
    // popups (1), below confirm dialogs (3).
    private const int OverlayLayer = 2;

    // Footer warning (e.g. picking a file outside the project root)
    private string _statusMsg = "";

    // Layout constants (preview column sized so a flipbook frame reads clearly)
    private const int PopupW = 780;
    private const int PopupH = 560;
    private const int PreviewW = 280;
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
        _previewCols = 0;
        _previewRows = 0;
        _keyboardIndex = -1;

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

        // Path bar: show current directory. Clamp to the LIST column — the
        // preview panel starts at this row and draws later, so a full-width bar
        // would be overpainted while its hidden part still hit-tests
        // (draw-vs-click divergence).
        ui.DrawRect(new Rectangle(px + 2, curY, listW - 4, PathBarH), EditorBase.InputBg);
        string displayDir = _currentDir.Replace('\\', '/');
        ui.DrawText(displayDir, new Vector2(px + Padding, curY + 4), EditorBase.TextDim);
        curY += PathBarH + 2;

        // Filter text field (list column only — see path bar note)
        _filterText = ui.DrawSearchField("texbrowser_filter", _filterText, px + Padding, curY, listW - Padding * 2);
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

        // --- Keyboard navigation: Up/Down move the cursor (files preview as
        // they're passed), Enter opens a directory / commits the selected file.
        // Raw press edges per editor convention — no consumption needed: the
        // overlay input-blocks the host's widgets, editors suppress camera
        // panning, and ESC stays with PopupManager (OnCancel). Stands down
        // while the search field is being typed in; the one-frame latch keeps
        // the Enter that commits the field from also committing a file.
        bool typing = ui.IsTextInputActive || _textInputWasActive;
        _textInputWasActive = ui.IsTextInputActive;
        // Stand down when a HIGHER overlay owns input (house convention —
        // DrawScrollableList gates its keyboard nav the same way).
        bool kbBlocked = ui.IsInputBlocked(ui.EffectiveLayer(0));
        if (_keyboardIndex >= displayEntries.Count) _keyboardIndex = displayEntries.Count - 1;
        if (!typing && !kbBlocked && displayEntries.Count > 0)
        {
            var kb = ui._kb; var prevKb = ui._prevKb;
            bool navUp = kb.IsKeyDown(Keys.Up) && prevKb.IsKeyUp(Keys.Up);
            bool navDown = kb.IsKeyDown(Keys.Down) && prevKb.IsKeyUp(Keys.Down);
            if (navUp ^ navDown)
            {
                _keyboardIndex = _keyboardIndex < 0
                    ? (navDown ? 0 : displayEntries.Count - 1)
                    : Math.Clamp(_keyboardIndex + (navDown ? 1 : -1), 0, displayEntries.Count - 1);
                var focused = displayEntries[_keyboardIndex];
                if (!focused.IsDirectory) SelectFile(focused.FullPath);

                // Scroll the focused row into view
                float itemTop = _keyboardIndex * ItemH;
                if (itemTop < _scrollOffset) _scrollOffset = itemTop;
                else if (itemTop + ItemH > _scrollOffset + contentH) _scrollOffset = itemTop + ItemH - contentH;
                _scrollOffset = Math.Clamp(_scrollOffset, 0, maxScroll);
            }
            if (kb.IsKeyDown(Keys.Enter) && prevKb.IsKeyUp(Keys.Enter))
            {
                if (_keyboardIndex >= 0 && displayEntries[_keyboardIndex].IsDirectory)
                {
                    NavigateTo(displayEntries[_keyboardIndex].FullPath);
                }
                else if (!string.IsNullOrEmpty(_selectedFile) && CommitSelection())
                {
                    ui.EndOverlay();
                    return;
                }
            }
        }

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
            // Keyboard cursor (matters mostly on directories, which have no
            // selection background of their own)
            if (i == _keyboardIndex)
                ui.DrawBorder(itemRect, EditorBase.AccentColor, 1);
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
                        SelectFile(entry.FullPath);
                        _keyboardIndex = i;   // keep arrow keys continuing from here
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

        FlipbookPreviewPanel.Draw(ui,
            new Rectangle(previewX + 1, previewY, PreviewW - 1, previewH),
            _previewTexture, _previewCols, _previewRows, _previewAnimStart,
            _previewTexture != null ? Path.GetFileName(_selectedFile) : null);

        // Footer: Use + Cancel buttons
        int footerY = py + PopupH - FooterH + 4;
        bool hasSelection = !string.IsNullOrEmpty(_selectedFile);

        if (!string.IsNullOrEmpty(_statusMsg))
            ui.DrawText(_statusMsg, new Vector2(px + Padding, footerY + 4), EditorBase.DangerColor);

        if (hasSelection && ui.DrawButton("Use", px + totalW - 170, footerY, 70, 24, EditorBase.AccentColor)
            && CommitSelection())
        {
            ui.EndOverlay();
            return;
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
        _keyboardIndex = -1;
        RefreshListing();
    }

    /// <summary>Commit the current selection (Use button / Enter / dev hook).
    /// Returns true when the browser closed; false leaves it open with the
    /// outside-project warning in the footer.</summary>
    private bool CommitSelection()
    {
        // Never persist an absolute path: asset paths in JSON must be
        // project-relative. MakeRelative returns its input unchanged when
        // the file is outside the project root — refuse instead of committing.
        string rel = Necroking.Core.GamePaths.MakeRelative(_selectedFile);
        if (Path.IsPathRooted(rel))
        {
            _statusMsg = "File is outside the project — pick one under assets/";
            return false;
        }
        _onSelect?.Invoke(rel);
        Close();
        return true;
    }

    /// <summary>Select a file for preview (not yet committed). If its filename
    /// carries a flipbook grid token, the preview panel animates it.</summary>
    private void SelectFile(string fullPath)
    {
        _selectedFile = fullPath.Replace('\\', '/');
        _previewTexture = LoadPreviewTexture(_selectedFile);
        _statusMsg = "";
        if (!Necroking.Render.Flipbook.TryParseGridFromFileName(_selectedFile, out _previewCols, out _previewRows))
        { _previewCols = 0; _previewRows = 0; }
        _previewAnimStart = Environment.TickCount64;
    }

    /// <summary>Dev-server hook (flipbook_ui pick): navigate to a file's folder
    /// and select it exactly as a row click would.</summary>
    internal bool DevSelectFile(string fullPath)
    {
        if (!_isOpen || !File.Exists(fullPath)) return false;
        string dir = Path.GetDirectoryName(fullPath)?.Replace('\\', '/') ?? "";
        if (!string.IsNullOrEmpty(dir)) NavigateTo(dir);
        SelectFile(fullPath);
        return true;
    }

    /// <summary>Dev-server hook (flipbook_ui use): commit the current selection
    /// exactly as the Use button would (same relative-path guard).</summary>
    internal bool DevCommitSelection()
        => _isOpen && !string.IsNullOrEmpty(_selectedFile) && CommitSelection();

    // Fall back to the game's device directly — most host editors never call
    // SetGraphicsDevice, which silently left their preview panel dead.
    private Texture2D? LoadPreviewTexture(string path)
        => _textureCache.GetOrLoad(_graphicsDevice ?? Necroking.Game1.Instance?.GraphicsDevice, path);

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

            // Image files the preview pipeline can decode (png natively;
            // tga/exr via ExrTgaTextures — the flipbook library's formats)
            foreach (var pattern in new[] { "*.png", "*.tga", "*.exr" })
                foreach (var f in Directory.GetFiles(_currentDir, pattern))
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
