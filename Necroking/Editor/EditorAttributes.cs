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
