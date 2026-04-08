using System;
using System.Collections.Generic;

namespace Necroking.Editor;

/// <summary>
/// Reusable state for editor windows with a filtered, scrollable, selectable list.
/// Eliminates duplicated _selectedIdx, _searchFilter, _filteredIds, _detailScroll
/// fields across SpellEditor, ItemEditor, UnitEditor, EnvObjectEditor.
/// </summary>
public class EditorListState
{
    public int SelectedIdx = -1;
    public string SearchFilter = "";
    public List<string> FilteredIds = new();
    public float DetailScroll;

    /// <summary>
    /// Rebuild the filtered ID list from a registry's IDs.
    /// Filters by display name or ID matching the search string.
    /// </summary>
    /// <param name="allIds">All IDs from the registry</param>
    /// <param name="getDisplayName">Function to get display name for an ID (for search matching)</param>
    public void RebuildFilter(IReadOnlyList<string> allIds, Func<string, string?> getDisplayName)
    {
        FilteredIds.Clear();
        for (int i = 0; i < allIds.Count; i++)
        {
            if (string.IsNullOrEmpty(SearchFilter))
            {
                FilteredIds.Add(allIds[i]);
                continue;
            }
            string display = getDisplayName(allIds[i]) ?? allIds[i];
            if (display.Contains(SearchFilter, StringComparison.OrdinalIgnoreCase)
                || allIds[i].Contains(SearchFilter, StringComparison.OrdinalIgnoreCase))
            {
                FilteredIds.Add(allIds[i]);
            }
        }
    }

    /// <summary>
    /// Get the index into FilteredIds that corresponds to SelectedIdx in the full list.
    /// Returns -1 if the selected item isn't in the filtered set.
    /// </summary>
    public int GetFilteredSelectedIdx(IReadOnlyList<string> allIds)
    {
        if (SelectedIdx < 0 || SelectedIdx >= allIds.Count) return -1;
        string selectedId = allIds[SelectedIdx];
        return FilteredIds.IndexOf(selectedId);
    }

    /// <summary>
    /// Handle a click on a filtered list item. Updates SelectedIdx to the
    /// corresponding index in the full ID list.
    /// </summary>
    public void SelectFilteredItem(int filteredIdx, IReadOnlyList<string> allIds)
    {
        if (filteredIdx < 0 || filteredIdx >= FilteredIds.Count) return;
        string clickedId = FilteredIds[filteredIdx];
        for (int i = 0; i < allIds.Count; i++)
        {
            if (allIds[i] == clickedId) { SelectedIdx = i; break; }
        }
        DetailScroll = 0;
    }
}
