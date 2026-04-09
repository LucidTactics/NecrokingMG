using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.Xna.Framework;
using Necroking.Core;
using Necroking.Data;
using Necroking.Data.Registries;

namespace Necroking.Editor;

/// <summary>
/// Draws editable fields for any object based on [EditorField] attributes.
/// Caches type layouts so reflection only runs once per CLR type.
///
/// <para><b>Usage in an editor window:</b></para>
/// <code>
/// // 1. Add a field and create in constructor:
/// private readonly ReflectionPropertyRenderer _renderer;
/// _renderer = new ReflectionPropertyRenderer(ui, gameData);
///
/// // 2. Call in your Draw method where you'd normally have manual field blocks:
/// var (nextY, changed) = _renderer.DrawAnnotatedProperties("myprefix", def, x, curY, w);
/// curY = nextY;          // continue drawing custom controls below
/// if (changed) MarkDirty();
///
/// // 3. Annotate properties on the data Def class:
/// [EditorField(Label = "Display Name", Order = 0)]
/// public string DisplayName { get; set; } = "";
///
/// [EditorField(Label = "Category", Order = 1)]
/// [EditorCombo("material", "potion", "consumable")]    // renders as dropdown
/// public string Category { get; set; } = "";
///
/// [EditorField(Label = "Speed", Order = 2, Step = 0.5f, Decimals = 1)]
/// public float Speed { get; set; } = 1.0f;
///
/// [EditorField(Label = "Core Color", Order = 3, Compact = true, Group = "VISUALS",
///     GroupColorR = 120, GroupColorG = 200, GroupColorB = 255)]
/// public HdrColor CoreColor { get; set; } = new(...);
///
/// [EditorField(Label = "Buff ID", Order = 4)]
/// [EditorRegistryDropdown("Buffs")]   // combo populated from GameData registry
/// public string BuffID { get; set; } = "";
///
/// [EditorVisible("Category", "Buff", "Debuff")]  // only show when Category is Buff or Debuff
/// [EditorField(Label = "Friendly Only", Order = 5)]
/// public bool FriendlyOnly { get; set; }
///
/// [EditorHide]   // exclude from reflection rendering
/// public string InternalField { get; set; } = "";
/// </code>
///
/// <para><b>Supported field types:</b></para>
/// <list type="bullet">
///   <item><c>string</c> — text field, combo with [EditorCombo], or registry dropdown with [EditorRegistryDropdown]</item>
///   <item><c>int</c> — integer drag field</item>
///   <item><c>float</c> — float drag field (Step, Decimals via EditorField)</item>
///   <item><c>bool</c> — checkbox toggle</item>
///   <item><c>HdrColor</c> — full editor or compact swatch (Compact via EditorField)</item>
///   <item><c>List&lt;string&gt;</c> — checkbox grid with [EditorCheckboxGrid]</item>
///   <item>Nullable class objects — collapsible nested section (if type has [EditorField] properties)</item>
/// </list>
///
/// <para><b>Adding support for a new field type:</b></para>
/// <para>
/// Add an <c>else if (propType == typeof(MyType))</c> branch in <see cref="DrawField"/>.
/// Read the current value, call the appropriate <c>EditorBase.Draw*</c> method,
/// advance <c>curY</c>, compare old vs new, and return true if changed.
/// If the new type needs attribute configuration (e.g. min/max range), add a property
/// to <see cref="EditorFieldAttribute"/> — existing cached layouts will pick it up
/// automatically since the cache is per-type and built once.
/// </para>
/// </summary>
public class ReflectionPropertyRenderer
{
    private readonly EditorBase _ui;
    private readonly GameData? _gameData;
    private readonly Dictionary<Type, TypeLayout> _layoutCache = new();
    private readonly Dictionary<string, bool> _expandedSections = new();

    private const int RowH = 24;
    private const int LabelW = 130;

    public ReflectionPropertyRenderer(EditorBase ui, GameData? gameData = null)
    {
        _ui = ui;
        _gameData = gameData;
    }

    /// <summary>
    /// Draw all [EditorField]-annotated properties on obj.
    /// Returns (nextY, changed) so the caller can continue drawing below
    /// and call MarkDirty() if changed is true.
    /// </summary>
    public (int nextY, bool changed) DrawAnnotatedProperties(
        string prefix, object obj, int x, int y, int w)
    {
        var layout = GetOrBuildLayout(obj.GetType());
        int curY = y;
        bool anyChanged = false;
        string lastGroup = "";

        foreach (var entry in layout.Entries)
        {
            // Check visibility conditions
            if (!IsVisible(entry, obj))
                continue;

            // Draw section header when group changes
            if (entry.Group != lastGroup && !string.IsNullOrEmpty(entry.Group))
            {
                // Check if any field in this group is visible
                bool anyVisibleInGroup = layout.Entries.Any(e =>
                    e.Group == entry.Group && IsVisible(e, obj));
                if (anyVisibleInGroup)
                {
                    curY += 4;
                    _ui.DrawRect(new Rectangle(x, curY, w, 1), new Color(60, 60, 80));
                    curY += 6;
                    _ui.DrawText(entry.Group, new Vector2(x, curY), entry.GroupColor);
                    curY += 22;
                }
                lastGroup = entry.Group;
            }
            else if (entry.Group != lastGroup)
            {
                lastGroup = entry.Group;
            }

            // Draw inline sub-header if present
            if (entry.HeaderText != null)
            {
                curY += 4;
                _ui.DrawText(entry.HeaderText, new Vector2(x, curY), entry.HeaderColor);
                curY += 18;
            }

            string fieldId = $"{prefix}.{entry.Property.Name}";
            bool changed = DrawField(fieldId, entry, obj, x, ref curY, w);
            if (changed) anyChanged = true;
        }

        return (curY, anyChanged);
    }

    // ===========================
    //  Visibility
    // ===========================

    private static bool IsVisible(FieldEntry entry, object obj)
    {
        if (entry.VisibilityRules == null || entry.VisibilityRules.Count == 0)
            return true;

        // Group by property name: OR within group, AND across groups
        foreach (var group in entry.VisibilityRules)
        {
            string propName = group.Key;
            var allowedValues = group.Value;

            // Read the property value
            var propInfo = obj.GetType().GetProperty(propName, BindingFlags.Public | BindingFlags.Instance);
            if (propInfo == null) return false;

            var propValue = propInfo.GetValue(obj);
            string strValue = propValue is bool b ? (b ? "True" : "False") : propValue?.ToString() ?? "";

            bool anyMatch = false;
            foreach (var allowed in allowedValues)
            {
                if (string.Equals(strValue, allowed, StringComparison.OrdinalIgnoreCase))
                {
                    anyMatch = true;
                    break;
                }
            }
            if (!anyMatch) return false;
        }
        return true;
    }

    // ===========================
    //  Field drawing
    // ===========================

    private bool DrawField(string fieldId, FieldEntry entry, object obj, int x, ref int curY, int w)
    {
        var prop = entry.Property;
        var value = prop.GetValue(obj);

        // Read-only display
        if (entry.ReadOnly)
        {
            _ui.DrawText(entry.Label, new Vector2(x, curY + 2), EditorBase.TextDim);
            _ui.DrawText(value?.ToString() ?? "", new Vector2(x + LabelW, curY + 2), new Color(140, 140, 165));
            curY += RowH;
            return false;
        }

        // Registry dropdown (string property backed by GameData registry)
        if (entry.RegistryName != null && _gameData != null)
        {
            string strVal = (string?)value ?? "";
            bool changed = DrawRegistryDropdown(fieldId, entry.Label, ref strVal, entry.RegistryName, x, curY, w);
            curY += RowH;
            if (changed) { prop.SetValue(obj, strVal); return true; }
            return false;
        }

        // Fixed combo options
        if (entry.ComboOptions != null)
        {
            string strVal = (string?)value ?? "";
            string newVal = _ui.DrawCombo(fieldId, entry.Label, strVal, entry.ComboOptions, x, curY, w);
            curY += RowH;
            if (newVal != strVal) { prop.SetValue(obj, newVal); return true; }
            return false;
        }

        // Checkbox grid for List<string>
        if (entry.CheckboxGrid != null && _gameData != null)
        {
            return DrawCheckboxGridField(entry, obj, x, ref curY, w);
        }

        var propType = prop.PropertyType;

        if (propType == typeof(string))
        {
            string strVal = (string?)value ?? "";
            string newVal = _ui.DrawTextField(fieldId, entry.Label, strVal, x, curY, w);
            curY += RowH;
            if (newVal != strVal) { prop.SetValue(obj, newVal); return true; }
        }
        else if (propType == typeof(int))
        {
            int intVal = (int)(value ?? 0);
            int newVal = _ui.DrawIntField(fieldId, entry.Label, intVal, x, curY, w);
            curY += RowH;
            if (newVal != intVal) { prop.SetValue(obj, newVal); return true; }
        }
        else if (propType == typeof(float))
        {
            float fVal = (float)(value ?? 0f);
            float newVal = _ui.DrawFloatField(fieldId, entry.Label, fVal, x, curY, w, entry.Step);
            if (entry.Decimals > 0)
            {
                float pow = MathF.Pow(10, entry.Decimals);
                newVal = MathF.Round(newVal * pow) / pow;
            }
            curY += RowH;
            if (MathF.Abs(newVal - fVal) > 0.0001f) { prop.SetValue(obj, newVal); return true; }
        }
        else if (propType == typeof(bool))
        {
            bool bVal = (bool)(value ?? false);
            bool newVal = _ui.DrawCheckbox(entry.Label, bVal, x, curY);
            curY += RowH;
            if (newVal != bVal) { prop.SetValue(obj, newVal); return true; }
        }
        else if (propType == typeof(HdrColor))
        {
            var hdrVal = (HdrColor)(value ?? new HdrColor());
            if (entry.Compact)
            {
                bool changed = DrawCompactHdrColor(fieldId, entry.Label, ref hdrVal, x, ref curY, w);
                prop.SetValue(obj, hdrVal);
                return changed;
            }
            else
            {
                var (newColor, h) = _ui.DrawHdrColorField(fieldId, entry.Label, hdrVal, x, curY, w);
                curY += h;
                prop.SetValue(obj, newColor);
                return !hdrVal.Equals(newColor);
            }
        }
        else if (propType.IsClass && HasAnnotatedProperties(propType))
        {
            // Nullable nested annotated object (e.g. FlipbookRef?)
            return DrawNestedObject(fieldId, entry, obj, x, ref curY, w);
        }
        else
        {
            // Unsupported type — skip with a label
            _ui.DrawText($"{entry.Label}: (unsupported type {propType.Name})",
                new Vector2(x, curY + 2), EditorBase.TextDim);
            curY += RowH;
        }

        return false;
    }

    // ===========================
    //  Registry dropdown
    // ===========================

    private bool DrawRegistryDropdown(string fieldId, string label, ref string currentId,
        string registryName, int x, int y, int w)
    {
        var (ids, getDisplayName) = GetRegistryInfo(registryName);
        if (ids == null) return false;

        // Build options array with display names
        var options = new string[ids.Count];
        for (int i = 0; i < ids.Count; i++)
            options[i] = getDisplayName!(ids[i]) ?? ids[i];

        // Find current display name
        string currentDisplay = "";
        if (!string.IsNullOrEmpty(currentId))
        {
            int idx = IndexOfId(ids, currentId);
            if (idx >= 0) currentDisplay = options[idx];
            else currentDisplay = currentId; // fallback to raw ID
        }

        string newDisplay = _ui.DrawCombo(fieldId, label, currentDisplay, options, x, y, w, allowNone: true);

        // Map back to ID
        string newId = "";
        if (!string.IsNullOrEmpty(newDisplay))
        {
            for (int i = 0; i < options.Length; i++)
            {
                if (options[i] == newDisplay)
                {
                    newId = ids[i];
                    break;
                }
            }
        }

        if (newId != currentId) { currentId = newId; return true; }
        return false;
    }

    private (IReadOnlyList<string>? ids, Func<string, string?>? getDisplayName) GetRegistryInfo(string registryName)
    {
        if (_gameData == null) return (null, null);

        return registryName switch
        {
            "Buffs" => (_gameData.Buffs.GetIDs(), id => _gameData.Buffs.Get(id)?.DisplayName),
            "Units" => (_gameData.Units.GetIDs(), id => _gameData.Units.Get(id)?.DisplayName),
            "Flipbooks" => (_gameData.Flipbooks.GetIDs(), id => _gameData.Flipbooks.Get(id)?.DisplayName),
            "Weapons" => (_gameData.Weapons.GetIDs(), id => _gameData.Weapons.Get(id)?.DisplayName),
            "Armors" => (_gameData.Armors.GetIDs(), id => _gameData.Armors.Get(id)?.DisplayName),
            "Shields" => (_gameData.Shields.GetIDs(), id => _gameData.Shields.Get(id)?.DisplayName),
            "Items" => (_gameData.Items.GetIDs(), id => _gameData.Items.Get(id)?.DisplayName),
            "Spells" => (_gameData.Spells.GetIDs(), id => _gameData.Spells.Get(id)?.DisplayName),
            _ => (null, null),
        };
    }

    // ===========================
    //  Checkbox grid
    // ===========================

    private bool DrawCheckboxGridField(FieldEntry entry, object obj, int x, ref int curY, int w)
    {
        var grid = entry.CheckboxGrid!;
        var prop = entry.Property;
        var list = (List<string>?)prop.GetValue(obj);
        if (list == null)
        {
            list = new List<string>();
            prop.SetValue(obj, list);
        }

        bool anyChanged = false;

        // Header
        curY += 4;
        _ui.DrawRect(new Rectangle(x, curY, w, 1), new Color(60, 60, 80));
        curY += 4;
        _ui.DrawText(grid.Header, new Vector2(x, curY), grid.HeaderColor);
        curY += 18;

        // Hint when empty
        if (list.Count == 0)
        {
            _ui.DrawText("(all units - check to restrict)", new Vector2(x + 4, curY + 2), new Color(120, 120, 140));
            curY += RowH;
        }

        // Get registry items
        var (ids, getDisplayName) = GetRegistryInfo(grid.RegistryName);
        if (ids == null) return false;

        int cols = grid.Columns;
        int colW = (w - 8) / cols;

        for (int i = 0; i < ids.Count; i++)
        {
            int col = i % cols;
            int colX = x + 4 + col * colW;
            string displayLabel = getDisplayName!(ids[i]) ?? ids[i];
            bool isChecked = list.Contains(ids[i]);
            bool newChecked = _ui.DrawCheckbox(displayLabel, isChecked, colX, curY);
            if (newChecked != isChecked)
            {
                if (newChecked) list.Add(ids[i]);
                else list.Remove(ids[i]);
                anyChanged = true;
            }
            if (col == cols - 1 || i == ids.Count - 1)
                curY += RowH;
        }

        return anyChanged;
    }

    // ===========================
    //  Compact HdrColor swatch
    // ===========================

    private bool DrawCompactHdrColor(string fieldId, string label, ref HdrColor color, int x, ref int curY, int w)
    {
        _ui.DrawText(label, new Vector2(x, curY + 2), EditorBase.TextDim);
        int swatchX = x + LabelW;
        bool changed = _ui.DrawColorSwatch(fieldId, swatchX, curY, 40, 18, ref color);
        string info = $"({color.R},{color.G},{color.B},{color.A}) x{color.Intensity:F1}";
        _ui.DrawText(info, new Vector2(swatchX + 46, curY + 2), new Color(120, 120, 140));
        curY += RowH;
        return changed;
    }

    // ===========================
    //  Nested annotated objects
    // ===========================

    private bool DrawNestedObject(string fieldId, FieldEntry entry, object obj, int x, ref int curY, int w)
    {
        var prop = entry.Property;
        var value = prop.GetValue(obj);

        // Section label
        _ui.DrawText(entry.Label, new Vector2(x, curY + 2), EditorBase.AccentColor);
        curY += RowH;

        if (value == null)
        {
            // Create button
            if (_ui.DrawButton($"Create {entry.Label}", x, curY, 180, 28))
            {
                try
                {
                    var instance = Activator.CreateInstance(prop.PropertyType);
                    prop.SetValue(obj, instance);
                    return true;
                }
                catch (Exception ex) { DebugLog.Log("error", $"Failed to create instance of {prop.PropertyType.Name}: {ex.Message}"); }
            }
            curY += RowH + 4;
            return false;
        }

        // Recursively draw nested object's annotated properties
        var (nextY, changed) = DrawAnnotatedProperties(fieldId, value, x, curY, w);
        curY = nextY;
        return changed;
    }

    private bool HasAnnotatedProperties(Type type)
    {
        var props = type.GetProperties(BindingFlags.Public | BindingFlags.Instance);
        return props.Any(p => p.GetCustomAttribute<EditorFieldAttribute>() != null);
    }

    // ===========================
    //  Layout caching
    // ===========================

    private TypeLayout GetOrBuildLayout(Type type)
    {
        if (_layoutCache.TryGetValue(type, out var cached))
            return cached;

        var entries = new List<FieldEntry>();
        var props = type.GetProperties(BindingFlags.Public | BindingFlags.Instance);

        foreach (var prop in props)
        {
            if (!prop.CanRead || !prop.CanWrite) continue;
            if (prop.GetCustomAttribute<EditorHideAttribute>() != null) continue;

            var editorAttr = prop.GetCustomAttribute<EditorFieldAttribute>();
            if (editorAttr == null) continue;

            var comboAttr = prop.GetCustomAttribute<EditorComboAttribute>();
            var registryAttr = prop.GetCustomAttribute<EditorRegistryDropdownAttribute>();
            var checkboxGridAttr = prop.GetCustomAttribute<EditorCheckboxGridAttribute>();
            var headerAttr = prop.GetCustomAttribute<EditorHeaderAttribute>();
            var visAttrs = prop.GetCustomAttributes<EditorVisibleAttribute>().ToList();

            // Build visibility rules: Dictionary<propertyName, HashSet<allowedValues>>
            Dictionary<string, HashSet<string>>? visRules = null;
            if (visAttrs.Count > 0)
            {
                visRules = new();
                foreach (var va in visAttrs)
                {
                    if (!visRules.TryGetValue(va.Property, out var set))
                    {
                        set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        visRules[va.Property] = set;
                    }
                    foreach (var v in va.Values)
                        set.Add(v);
                }
            }

            CheckboxGridInfo? gridInfo = null;
            if (checkboxGridAttr != null)
            {
                gridInfo = new CheckboxGridInfo
                {
                    RegistryName = checkboxGridAttr.RegistryName,
                    Columns = checkboxGridAttr.Columns,
                    Header = checkboxGridAttr.Header,
                    HeaderColor = new Color(checkboxGridAttr.HeaderColorR, checkboxGridAttr.HeaderColorG, checkboxGridAttr.HeaderColorB),
                };
            }

            entries.Add(new FieldEntry
            {
                Property = prop,
                Label = string.IsNullOrEmpty(editorAttr.Label) ? prop.Name : editorAttr.Label,
                Group = editorAttr.Group,
                Order = editorAttr.Order,
                Step = editorAttr.Step,
                Decimals = editorAttr.Decimals,
                ReadOnly = editorAttr.ReadOnly,
                Compact = editorAttr.Compact,
                GroupColor = new Color(editorAttr.GroupColorR, editorAttr.GroupColorG, editorAttr.GroupColorB),
                ComboOptions = comboAttr?.Options,
                RegistryName = registryAttr?.RegistryName,
                CheckboxGrid = gridInfo,
                HeaderText = headerAttr?.Text,
                HeaderColor = headerAttr != null ? new Color(headerAttr.ColorR, headerAttr.ColorG, headerAttr.ColorB) : Color.White,
                VisibilityRules = visRules,
            });
        }

        // Sort by global Order value. Group headers draw when group name changes.
        entries.Sort((a, b) => a.Order.CompareTo(b.Order));

        var layout = new TypeLayout { Entries = entries };
        _layoutCache[type] = layout;
        return layout;
    }

    private static int IndexOfId(IReadOnlyList<string> list, string value)
    {
        for (int i = 0; i < list.Count; i++)
            if (list[i] == value) return i;
        return -1;
    }

    // ===========================
    //  Internal types
    // ===========================

    private class TypeLayout
    {
        public List<FieldEntry> Entries = new();
    }

    private class FieldEntry
    {
        public PropertyInfo Property = null!;
        public string Label = "";
        public string Group = "";
        public int Order;
        public float Step = 0.1f;
        public int Decimals;
        public bool ReadOnly;
        public bool Compact;
        public Color GroupColor = new(200, 200, 200);
        public string[]? ComboOptions;
        public string? RegistryName;
        public CheckboxGridInfo? CheckboxGrid;
        public string? HeaderText;
        public Color HeaderColor = Color.White;
        public Dictionary<string, HashSet<string>>? VisibilityRules;
    }

    private class CheckboxGridInfo
    {
        public string RegistryName = "";
        public int Columns = 2;
        public string Header = "";
        public Color HeaderColor = new(200, 180, 255);
    }
}
