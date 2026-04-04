using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.Xna.Framework;
using Necroking.Core;

namespace Necroking.Editor;

/// <summary>
/// Draws editable fields for any object based on [EditorField] attributes.
/// Caches type layouts so reflection only runs once per CLR type.
///
/// <para><b>Usage in an editor window:</b></para>
/// <code>
/// // 1. Add a field and create in constructor:
/// private readonly ReflectionPropertyRenderer _renderer;
/// _renderer = new ReflectionPropertyRenderer(ui);
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
/// [EditorField(Label = "Speed", Order = 2, Step = 0.5f)] // custom drag step
/// public float Speed { get; set; } = 1.0f;
///
/// [EditorField(Label = "Core Color", Order = 3, Group = "VISUALS",
///     GroupColorR = 120, GroupColorG = 200, GroupColorB = 255)]
/// public HdrColor CoreColor { get; set; } = new(...);
///
/// [EditorHide]   // exclude from reflection rendering
/// public string InternalField { get; set; } = "";
/// </code>
///
/// <para><b>Supported field types:</b></para>
/// <list type="bullet">
///   <item><c>string</c> — text field (or combo dropdown with [EditorCombo])</item>
///   <item><c>int</c> — integer drag field</item>
///   <item><c>float</c> — float drag field (step size via EditorField.Step)</item>
///   <item><c>bool</c> — checkbox toggle</item>
///   <item><c>HdrColor</c> — R/G/B/A + intensity editor (variable height)</item>
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
    private readonly Dictionary<Type, TypeLayout> _layoutCache = new();

    private const int RowH = 24;

    public ReflectionPropertyRenderer(EditorBase ui)
    {
        _ui = ui;
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
            // Draw section header when group changes
            if (entry.Group != lastGroup && !string.IsNullOrEmpty(entry.Group))
            {
                curY += 4;
                _ui.DrawRect(new Rectangle(x, curY, w, 1), new Color(60, 60, 80));
                curY += 6;
                _ui.DrawText(entry.Group, new Vector2(x, curY), entry.GroupColor);
                curY += 22;
                lastGroup = entry.Group;
            }
            else if (entry.Group != lastGroup)
            {
                lastGroup = entry.Group;
            }

            string fieldId = $"{prefix}.{entry.Property.Name}";
            bool changed = DrawField(fieldId, entry, obj, x, ref curY, w);
            if (changed) anyChanged = true;
        }

        return (curY, anyChanged);
    }

    private bool DrawField(string fieldId, FieldEntry entry, object obj, int x, ref int curY, int w)
    {
        var prop = entry.Property;
        var value = prop.GetValue(obj);

        if (entry.ComboOptions != null)
        {
            string strVal = (string?)value ?? "";
            string newVal = _ui.DrawCombo(fieldId, entry.Label, strVal, entry.ComboOptions, x, curY, w);
            curY += RowH;
            if (newVal != strVal) { prop.SetValue(obj, newVal); return true; }
            return false;
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
            var (newColor, h) = _ui.DrawHdrColorField(fieldId, entry.Label, hdrVal, x, curY, w);
            curY += h;
            // HdrColor is a struct so always write back
            prop.SetValue(obj, newColor);
            return !hdrVal.Equals(newColor);
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

            entries.Add(new FieldEntry
            {
                Property = prop,
                Label = string.IsNullOrEmpty(editorAttr.Label) ? prop.Name : editorAttr.Label,
                Group = editorAttr.Group,
                Order = editorAttr.Order,
                Step = editorAttr.Step,
                GroupColor = new Color(editorAttr.GroupColorR, editorAttr.GroupColorG, editorAttr.GroupColorB),
                ComboOptions = comboAttr?.Options,
            });
        }

        // Sort by group order (first field's Order in group), then by Order within group
        entries.Sort((a, b) =>
        {
            int groupCmp = string.Compare(a.Group, b.Group, StringComparison.Ordinal);
            if (groupCmp != 0) return groupCmp;
            return a.Order.CompareTo(b.Order);
        });

        var layout = new TypeLayout { Entries = entries };
        _layoutCache[type] = layout;
        return layout;
    }

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
        public Color GroupColor = new(200, 200, 200);
        public string[]? ComboOptions;
    }
}
