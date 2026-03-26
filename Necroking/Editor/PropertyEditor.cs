using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.Json.Serialization;
using Microsoft.Xna.Framework;
using Necroking.Core;
using Necroking.Data.Registries;

namespace Necroking.Editor;

/// <summary>
/// Reflection-based property editor that draws editable fields for any object.
/// Uses the EditorBase drawing primitives to render appropriate widgets per property type.
/// </summary>
public class PropertyEditor
{
    private readonly EditorBase _ui;
    private readonly Dictionary<string, bool> _expandedSections = new();
    private readonly Dictionary<string, float> _scrollOffsets = new();

    public PropertyEditor(EditorBase ui)
    {
        _ui = ui;
    }

    /// <summary>
    /// Draw all editable properties of an object. Returns the total height used.
    /// </summary>
    public int DrawObject(string prefix, object obj, int x, int y, int w, int clipY, int clipH)
    {
        if (obj == null) return 0;

        var type = obj.GetType();
        var props = type.GetProperties(BindingFlags.Public | BindingFlags.Instance);
        int curY = y;
        int rowH = 24;

        foreach (var prop in props)
        {
            if (!prop.CanRead) continue;
            if (!prop.CanWrite && !IsCollectionType(prop.PropertyType)) continue;

            // Skip if entirely outside clip region (but still count height)
            string fieldId = $"{prefix}.{prop.Name}";
            string displayName = GetDisplayName(prop);

            var propType = prop.PropertyType;
            var value = prop.GetValue(obj);

            // Only render if visible in clip region
            bool visible = curY + rowH > clipY && curY < clipY + clipH;

            if (propType == typeof(string))
            {
                if (visible)
                {
                    string strVal = (string?)value ?? "";

                    // Check if this string property maps to an enum
                    string[]? enumValues = GetEnumValuesForProperty(prop);
                    if (enumValues != null)
                    {
                        string newVal = _ui.DrawCombo(fieldId, displayName, strVal, enumValues, x, curY, w);
                        if (newVal != strVal && prop.CanWrite)
                            prop.SetValue(obj, newVal);
                    }
                    else
                    {
                        string newVal = _ui.DrawTextField(fieldId, displayName, strVal, x, curY, w);
                        if (newVal != strVal && prop.CanWrite)
                            prop.SetValue(obj, newVal);
                    }
                }
                curY += rowH;
            }
            else if (propType == typeof(int))
            {
                if (visible)
                {
                    int intVal = (int)(value ?? 0);
                    int newVal = _ui.DrawIntField(fieldId, displayName, intVal, x, curY, w);
                    if (newVal != intVal && prop.CanWrite)
                        prop.SetValue(obj, newVal);
                }
                curY += rowH;
            }
            else if (propType == typeof(float))
            {
                if (visible)
                {
                    float fVal = (float)(value ?? 0f);
                    float step = GetFloatStep(prop);
                    float newVal = _ui.DrawFloatField(fieldId, displayName, fVal, x, curY, w, step);
                    if (MathF.Abs(newVal - fVal) > 0.0001f && prop.CanWrite)
                        prop.SetValue(obj, newVal);
                }
                curY += rowH;
            }
            else if (propType == typeof(bool))
            {
                if (visible)
                {
                    bool bVal = (bool)(value ?? false);
                    bool newVal = _ui.DrawCheckbox(displayName, bVal, x, curY);
                    if (newVal != bVal && prop.CanWrite)
                        prop.SetValue(obj, newVal);
                }
                curY += rowH;
            }
            else if (propType == typeof(byte))
            {
                if (visible)
                {
                    int byteVal = (byte)(value ?? (byte)0);
                    int newVal = _ui.DrawIntField(fieldId, displayName, byteVal, x, curY, w);
                    newVal = Math.Clamp(newVal, 0, 255);
                    if (newVal != byteVal && prop.CanWrite)
                        prop.SetValue(obj, (byte)newVal);
                }
                curY += rowH;
            }
            else if (propType == typeof(HdrColor))
            {
                if (visible)
                {
                    var hdrVal = (HdrColor)(value ?? new HdrColor());
                    var (newColor, h) = _ui.DrawHdrColorField(fieldId, displayName, hdrVal, x, curY, w);
                    if (prop.CanWrite)
                        prop.SetValue(obj, newColor);
                    curY += h;
                }
                else
                {
                    curY += rowH * 6; // approximate height for HDR color fields
                }
            }
            else if (propType == typeof(List<string>))
            {
                curY += DrawStringList(fieldId, displayName, prop, obj, x, curY, w, visible);
            }
            else if (propType.IsClass && value != null && !propType.IsArray)
            {
                // Nested object - use collapsible tree node
                string sectionKey = fieldId;
                if (!_expandedSections.ContainsKey(sectionKey))
                    _expandedSections[sectionKey] = false;

                if (visible)
                {
                    bool expanded = _expandedSections[sectionKey];
                    string arrow = expanded ? "v " : "> ";
                    Color headerColor = value == null ? EditorBase.TextDim : EditorBase.AccentColor;

                    // Draw section header
                    var headerRect = new Rectangle(x, curY, w, 22);
                    bool hovered = headerRect.Contains(_ui._mouse.X, _ui._mouse.Y);
                    _ui.DrawRect(headerRect, hovered ? EditorBase.ItemHover : new Color(35, 35, 55, 180));
                    _ui.DrawText(arrow + displayName, new Vector2(x + 4, curY + 2), headerColor);

                    if (hovered && _ui._mouse.LeftButton == Microsoft.Xna.Framework.Input.ButtonState.Pressed
                        && _ui._prevMouse.LeftButton == Microsoft.Xna.Framework.Input.ButtonState.Released)
                    {
                        _expandedSections[sectionKey] = !expanded;
                    }

                    // If null, show [null] and option to create
                    if (value == null)
                    {
                        _ui.DrawText("[null]", new Vector2(x + w - 60, curY + 2), EditorBase.TextDim);
                    }
                }

                curY += rowH;

                if (_expandedSections.GetValueOrDefault(sectionKey) && value != null)
                {
                    curY += DrawObject(fieldId, value, x + 16, curY, w - 16, clipY, clipH);
                }
            }
            else if (value == null && propType.IsClass)
            {
                // Null object with create button
                if (visible)
                {
                    _ui.DrawText(displayName, new Vector2(x, curY + 2), EditorBase.TextDim);
                    _ui.DrawText("[null]", new Vector2(x + 120, curY + 2), new Color(100, 80, 80));
                    if (prop.CanWrite && _ui.DrawButton("New", x + w - 40, curY, 36, 20))
                    {
                        try
                        {
                            var instance = Activator.CreateInstance(propType);
                            prop.SetValue(obj, instance);
                        }
                        catch { }
                    }
                }
                curY += rowH;
            }
        }

        return curY - y;
    }

    private int DrawStringList(string fieldId, string label, PropertyInfo prop, object obj,
        int x, int y, int w, bool visible)
    {
        var list = (List<string>?)prop.GetValue(obj) ?? new List<string>();
        int curY = y;
        int rowH = 22;

        // Header with count
        string sectionKey = fieldId + "_list";
        if (!_expandedSections.ContainsKey(sectionKey))
            _expandedSections[sectionKey] = false;

        if (visible)
        {
            bool expanded = _expandedSections[sectionKey];
            string arrow = expanded ? "v " : "> ";
            var headerRect = new Rectangle(x, curY, w, rowH);
            bool hovered = headerRect.Contains(_ui._mouse.X, _ui._mouse.Y);
            _ui.DrawRect(headerRect, hovered ? EditorBase.ItemHover : new Color(35, 35, 55, 180));
            _ui.DrawText($"{arrow}{label} [{list.Count}]", new Vector2(x + 4, curY + 2), EditorBase.AccentColor);

            if (hovered && _ui._mouse.LeftButton == Microsoft.Xna.Framework.Input.ButtonState.Pressed
                && _ui._prevMouse.LeftButton == Microsoft.Xna.Framework.Input.ButtonState.Released)
            {
                _expandedSections[sectionKey] = !expanded;
            }
        }
        curY += rowH;

        if (!_expandedSections.GetValueOrDefault(sectionKey))
            return curY - y;

        // List items
        for (int i = 0; i < list.Count; i++)
        {
            bool itemVisible = curY + rowH > 0; // simplified visibility
            if (itemVisible)
            {
                string val = _ui.DrawTextField($"{fieldId}[{i}]", $"  [{i}]", list[i], x, curY, w - 24);
                if (val != list[i]) list[i] = val;

                // Remove button
                if (_ui.DrawButton("X", x + w - 22, curY, 20, 20, EditorBase.DangerColor))
                {
                    list.RemoveAt(i);
                    i--;
                    curY += rowH;
                    continue;
                }
            }
            curY += rowH;
        }

        // Add button
        if (_ui.DrawButton("+ Add", x, curY, 60, 20))
        {
            list.Add("");
        }
        curY += rowH;

        return curY - y;
    }

    private static string GetDisplayName(PropertyInfo prop)
    {
        // Use JsonPropertyName attribute if available for a friendlier name
        var jsonAttr = prop.GetCustomAttribute<JsonPropertyNameAttribute>();
        if (jsonAttr != null)
            return jsonAttr.Name;
        return prop.Name;
    }

    /// <summary>
    /// Try to determine enum values for string-typed properties based on naming conventions.
    /// </summary>
    private static string[]? GetEnumValuesForProperty(PropertyInfo prop)
    {
        string name = prop.Name.ToLowerInvariant();

        // Map property names to known enum types
        if (name == "unittype" || name == "type")
            return GetEnumNames<Data.UnitType>();
        if (name == "faction")
            return GetEnumNames<Data.Faction>();
        if (name == "ai")
            return GetEnumNames<Data.AIBehavior>();
        if (name == "category")
            return GetEnumNames<Data.SpellCategory>();
        if (name == "aoetype")
            return GetEnumNames<Data.AOEType>();
        if (name == "trajectory")
            return GetEnumNames<Data.Trajectory>();
        if (name == "targetfilter")
            return GetEnumNames<Data.SpellTargetFilter>();
        if (name == "summontargetreq")
            return GetEnumNames<Data.SummonTargetReq>();
        if (name == "summonmode")
            return GetEnumNames<Data.SummonMode>();
        if (name == "spawnlocation")
            return GetEnumNames<Data.SpawnLocation>();
        if (name == "blendmode")
            return GetEnumNames<Data.EffectBlendMode>();
        if (name == "alignment")
            return GetEnumNames<Data.EffectAlignment>();
        if (name == "strikevisualtype" || name == "strikevisual")
            return GetEnumNames<Data.StrikeVisual>();

        return null;
    }

    private static string[] GetEnumNames<T>() where T : struct, Enum
    {
        return Enum.GetNames<T>();
    }

    private static float GetFloatStep(PropertyInfo prop)
    {
        string name = prop.Name.ToLowerInvariant();
        if (name.Contains("speed") || name.Contains("range") || name.Contains("radius"))
            return 0.5f;
        if (name.Contains("scale") || name.Contains("width"))
            return 0.1f;
        if (name.Contains("delay") || name.Contains("duration") || name.Contains("cooldown"))
            return 0.05f;
        if (name.Contains("intensity") || name.Contains("strength"))
            return 0.1f;
        return 0.1f;
    }

    private static bool IsCollectionType(Type type)
    {
        return type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>);
    }
}
