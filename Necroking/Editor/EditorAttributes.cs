using System;

namespace Necroking.Editor;

/// <summary>
/// Marks a property for automatic rendering in the reflection-based property editor.
/// The widget type is inferred from the CLR type unless overridden by [EditorCombo].
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public class EditorFieldAttribute : Attribute
{
    /// <summary>Display label. Defaults to property name if empty.</summary>
    public string Label { get; set; } = "";

    /// <summary>Section header name. Empty = ungrouped (no header drawn).</summary>
    public string Group { get; set; } = "";

    /// <summary>Sort order within group. Lower numbers render first.</summary>
    public int Order { get; set; }

    /// <summary>Float drag step size. Only used for float properties.</summary>
    public float Step { get; set; } = 0.1f;

    /// <summary>Section header color RGB (only read from the first field in a group).</summary>
    public int GroupColorR { get; set; } = 200;
    public int GroupColorG { get; set; } = 200;
    public int GroupColorB { get; set; } = 200;

    /// <summary>Display as read-only text instead of editable widget.</summary>
    public bool ReadOnly { get; set; }

    /// <summary>Float rounding decimals. 0=no rounding, 1=round to 0.1, 2=round to 0.01.</summary>
    public int Decimals { get; set; }

    /// <summary>For HdrColor: use compact clickable swatch (1 row) instead of full editor (6 rows).</summary>
    public bool Compact { get; set; }
}

/// <summary>
/// Renders a string property as a combo dropdown with fixed options.
/// Must be combined with [EditorField].
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public class EditorComboAttribute : Attribute
{
    public string[] Options { get; }

    public EditorComboAttribute(params string[] options)
    {
        Options = options;
    }
}

/// <summary>
/// Excludes a property from reflection-based rendering.
/// Use on properties that are internal linkage fields or require custom rendering.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public class EditorHideAttribute : Attribute { }

/// <summary>
/// Conditional visibility: only render this field when the named property has one of the given values.
/// Multiple [EditorVisible] on the same field: OR within same property, AND across different properties.
/// For bool properties, use "True" or "False" as values.
/// </summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = true)]
public class EditorVisibleAttribute : Attribute
{
    public string Property { get; }
    public string[] Values { get; }

    public EditorVisibleAttribute(string property, params string[] values)
    {
        Property = property;
        Values = values;
    }
}

/// <summary>
/// Renders a string property as a combo dropdown populated from a GameData registry.
/// The registry provides display names; the stored value is the ID.
/// Supported registries: "Buffs", "Units", "Flipbooks".
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public class EditorRegistryDropdownAttribute : Attribute
{
    public string RegistryName { get; }

    public EditorRegistryDropdownAttribute(string registryName)
    {
        RegistryName = registryName;
    }
}

/// <summary>
/// Renders a List&lt;string&gt; property as a multi-column checkbox grid
/// populated from a GameData registry.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public class EditorCheckboxGridAttribute : Attribute
{
    public string RegistryName { get; }
    public int Columns { get; set; } = 2;
    public string Header { get; set; } = "";
    public int HeaderColorR { get; set; } = 200;
    public int HeaderColorG { get; set; } = 180;
    public int HeaderColorB { get; set; } = 255;

    public EditorCheckboxGridAttribute(string registryName)
    {
        RegistryName = registryName;
    }
}

/// <summary>
/// Draws an inline sub-section header before this field.
/// Different from Group headers — these are lightweight labels within a group.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public class EditorHeaderAttribute : Attribute
{
    public string Text { get; }
    public int ColorR { get; set; } = 120;
    public int ColorG { get; set; } = 120;
    public int ColorB { get; set; } = 140;

    public EditorHeaderAttribute(string text)
    {
        Text = text;
    }
}
