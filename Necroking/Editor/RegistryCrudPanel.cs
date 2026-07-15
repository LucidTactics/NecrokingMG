using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Necroking.Data.Registries;

namespace Necroking.Editor;

/// <summary>Non-generic surface of <see cref="RegistryCrudPanel{TDef}"/> so a window can
/// hold panels over different def types behind one reference (e.g. UnitEditorWindow's
/// weapon/armor/shield sub-editors dispatch through this).</summary>
public interface IRegistryCrudPanel
{
    /// <summary>Index into the registry's GetIDs() order; -1 = nothing selected.</summary>
    int SelectedIdx { get; set; }

    /// <summary>Search-filtered scroll list on the left + clipped detail form on the
    /// right. Layout matches the sub-editor popup: list at (leftX, listY, listW, listH),
    /// detail right of the list inside (popX..popX+popW, contentY..contentY+contentH).
    /// <paramref name="onSelectionChanged"/> fires when a list click changes the
    /// selection (e.g. reset the detail scroll).</summary>
    void DrawListAndDetail(string searchFilter, int leftX, int listY, int listW, int listH,
        int popX, int popW, int contentY, int contentH, Action? onSelectionChanged = null);

    /// <summary>The +New / Copy / Delete / Save button row. When Delete hits an entry
    /// with live references, <paramref name="requestConfirmDelete"/> is invoked with the
    /// id instead of deleting — the caller shows its confirm dialog and, on confirm,
    /// calls <see cref="DeleteWithReferences"/>.</summary>
    void DrawCrudButtons(int popX, int bottomY, int popW, Action<string> requestConfirmDelete);

    /// <summary>Ctrl+C: snapshot the selected def into the panel clipboard.</summary>
    void CopyToClipboard();

    /// <summary>Ctrl+V: clone the clipboard def under a unique "_paste" id and select it.
    /// No-op when the clipboard is empty.</summary>
    void PasteFromClipboard();

    /// <summary>Reference count for the delete guard (0 when no guard configured).</summary>
    int CountReferences(string id);

    /// <summary>Confirmed guarded delete: strip references, remove from the registry,
    /// clamp the selection.</summary>
    void DeleteWithReferences(string id);
}

/// <summary>
/// Generic registry list+detail+CRUD sub-editor scaffold: "browse a registry, pick an
/// entry, edit fields, New/Copy/Delete/Save" over a <see cref="RegistryBase{TDef}"/>.
///
/// The panel owns the MECHANICS (consolidation-review editor-parallel-subeditors F1):
/// search-filtered list via EditorBase.DrawScrollableList (which supplies SetMouseOverUI
/// on hover and ClearActiveField on selection change — the drift fixes), selection index,
/// the +New/Copy/Delete/Save row (CloneDef + "_copy"/"_paste" uniquing + AddAfter),
/// Ctrl+C/V clipboard, and the reference-guarded delete flow.
///
/// The CALLER owns the DATA: which registry, id prefix, save path, delete guard
/// (count/remove references), and the detail form callback. Clones go through
/// RegistryBase.CloneDef (JSON round-trip) so new def fields can never be silently
/// dropped by Copy/Paste.
/// </summary>
public sealed class RegistryCrudPanel<TDef> : IRegistryCrudPanel where TDef : class, INamedDef, new()
{
    private readonly EditorBase _ui;
    private readonly RegistryBase<TDef> _registry;
    private readonly string _listId;         // scroll-list panel id (per-panel scroll state)
    private readonly string _idPrefix;       // e.g. "weapon_" — +New ids are prefix + HHmmss
    private readonly string _newDisplayName; // e.g. "New Weapon"
    private readonly string _noun;           // e.g. "weapon" — status messages
    private readonly string _savePath;       // e.g. "data/weapons.json"
    private readonly Action<TDef, int, int, int, int> _drawDetail; // (def, x, y, w, h)
    private readonly Action<string> _setStatus;
    private readonly Action _markUnsaved;
    private readonly Func<string, int>? _countReferences;  // delete guard (null = none)
    private readonly Action<string>? _removeReferences;    // strip refs on confirmed delete

    public int SelectedIdx { get; set; } = -1;
    private TDef? _clipboard;

    public RegistryCrudPanel(EditorBase ui, RegistryBase<TDef> registry, string listId,
        string idPrefix, string newDisplayName, string noun, string savePath,
        Action<TDef, int, int, int, int> drawDetail,
        Action<string> setStatus, Action markUnsaved,
        Func<string, int>? countReferences = null, Action<string>? removeReferences = null)
    {
        _ui = ui;
        _registry = registry;
        _listId = listId;
        _idPrefix = idPrefix;
        _newDisplayName = newDisplayName;
        _noun = noun;
        _savePath = savePath;
        _drawDetail = drawDetail;
        _setStatus = setStatus;
        _markUnsaved = markUnsaved;
        _countReferences = countReferences;
        _removeReferences = removeReferences;
    }

    public void DrawListAndDetail(string searchFilter, int leftX, int listY, int listW, int listH,
        int popX, int popW, int contentY, int contentH, Action? onSelectionChanged = null)
    {
        var ids = _registry.GetIDs();
        var displayItems = new List<string>();
        var filteredIds = new List<string>();
        foreach (var id in ids)
        {
            string name = _registry.NameOf(id);
            if (!string.IsNullOrEmpty(searchFilter) &&
                !id.Contains(searchFilter, StringComparison.OrdinalIgnoreCase) &&
                !name.Contains(searchFilter, StringComparison.OrdinalIgnoreCase))
                continue;
            filteredIds.Add(id);
            displayItems.Add(name);
        }

        int filteredSelIdx = SelectedIdx >= 0 && SelectedIdx < ids.Count
            ? filteredIds.IndexOf(ids[SelectedIdx]) : -1;

        int clicked = _ui.DrawScrollableList(_listId, displayItems, filteredSelIdx,
            leftX, listY, listW, listH, null);
        if (clicked >= 0 && clicked < filteredIds.Count)
        {
            SelectedIdx = EditorBase.IndexOf(ids, filteredIds[clicked]);
            onSelectionChanged?.Invoke();
        }

        // --- Right: detail ---
        int rightX = popX + listW + 12;
        int rightW = popW - listW - 20;
        _ui.DrawRect(new Rectangle(rightX - 2, contentY, 1, contentH), EditorBase.PanelBorder);

        if (SelectedIdx >= 0 && SelectedIdx < ids.Count)
        {
            var def = _registry.Get(ids[SelectedIdx]);
            if (def != null)
            {
                _ui.BeginClip(new Rectangle(rightX, contentY, rightW, contentH));
                _drawDetail(def, rightX, contentY, rightW, contentH);
                _ui.EndClip();
            }
        }
    }

    public void DrawCrudButtons(int popX, int bottomY, int popW, Action<string> requestConfirmDelete)
    {
        int bx = popX + 8;
        int btnW = 70;
        int btnH = 24;

        if (_ui.DrawButton("+ New", bx, bottomY, btnW, btnH))
        {
            // UniqueId guard: Registry.Add is an UPSERT, so two "+ New" clicks in
            // the same wall-clock second would silently overwrite the first entry.
            string newId = UniqueId(_idPrefix + DateTime.Now.ToString("HHmmss"), "");
            var newDef = new TDef { Id = newId, DisplayName = _newDisplayName };
            _registry.Add(newDef);
            SelectedIdx = EditorBase.IndexOf(_registry.GetIDs(), newId);
            _markUnsaved();
            _setStatus($"Added {_noun}: {newId}");
        }
        bx += btnW + 4;

        var ids = _registry.GetIDs();
        if (SelectedIdx >= 0 && SelectedIdx < ids.Count)
        {
            // Copy button
            if (_ui.DrawButton("Copy", bx, bottomY, btnW, btnH))
            {
                var src = _registry.Get(ids[SelectedIdx]);
                if (src != null)
                {
                    string newId = UniqueId(src.Id, "_copy");
                    var newDef = Clone(src, newId);
                    newDef.DisplayName = src.DisplayName + " (Copy)";
                    _registry.AddAfter(newDef, src.Id);
                    SelectedIdx = EditorBase.IndexOf(_registry.GetIDs(), newId);
                    _markUnsaved();
                    _setStatus($"Copied {_noun}: {newId}");
                }
            }
            bx += btnW + 4;

            // Delete button with confirmation if referenced
            if (_ui.DrawButton("Delete", bx, bottomY, btnW, btnH, EditorBase.DangerColor))
            {
                string removeId = ids[SelectedIdx];
                if (CountReferences(removeId) > 0)
                {
                    requestConfirmDelete(removeId);
                }
                else
                {
                    _registry.Remove(removeId);
                    SelectedIdx = Math.Min(SelectedIdx, _registry.Count - 1);
                    _markUnsaved();
                    _setStatus($"Removed {_noun}: {removeId}");
                }
            }
        }

        // Save
        if (_ui.DrawButton("Save", popX + popW - 80, bottomY, 70, btnH, EditorBase.SuccessColor))
        {
            bool ok = _registry.Save(Core.GamePaths.Resolve(_savePath));
            _setStatus(ok ? $"Saved {System.IO.Path.GetFileName(_savePath)}" : "SAVE FAILED!");
        }
    }

    public void CopyToClipboard()
    {
        var ids = _registry.GetIDs();
        if (SelectedIdx < 0 || SelectedIdx >= ids.Count) return;
        var src = _registry.Get(ids[SelectedIdx]);
        if (src == null) return;
        _clipboard = Clone(src, src.Id);
        _setStatus($"Copied {_noun}: {src.Id}");
    }

    public void PasteFromClipboard()
    {
        if (_clipboard == null) return;
        string newId = UniqueId(_clipboard.Id, "_paste");
        var newDef = Clone(_clipboard, newId);
        newDef.DisplayName = _clipboard.DisplayName + " (Paste)";
        _registry.Add(newDef);
        SelectedIdx = EditorBase.IndexOf(_registry.GetIDs(), newId);
        _markUnsaved();
        _setStatus($"Pasted {_noun}: {newId}");
    }

    public int CountReferences(string id) => _countReferences?.Invoke(id) ?? 0;

    public void DeleteWithReferences(string id)
    {
        _removeReferences?.Invoke(id);
        _registry.Remove(id);
        SelectedIdx = Math.Min(SelectedIdx, _registry.Count - 1);
        _markUnsaved();
        _setStatus($"Removed {_noun}: {id}");
    }

    /// <summary>JSON round-trip clone via the registry (fidelity == save/load fidelity).</summary>
    private TDef Clone(TDef src, string newId)
        => _registry.CloneDef(src, newId) ?? new TDef { Id = newId, DisplayName = src.DisplayName };

    /// <summary>baseId + suffix, then suffix2/suffix3/... until unused.</summary>
    private string UniqueId(string baseId, string suffix)
    {
        string newId = baseId + suffix;
        int n = 1;
        while (_registry.Get(newId) != null)
            newId = baseId + suffix + (++n);
        return newId;
    }
}
