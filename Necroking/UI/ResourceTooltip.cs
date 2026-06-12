using Microsoft.Xna.Framework;

namespace Necroking.UI;

/// <summary>
/// Binder for the auto-size ResourceTooltipDyn widget: shows how a value is
/// arrived at via an arbitrary-length list of labeled (+/-) entries.
/// Bind(...) writes all overrides; DrawWidget then lays the panel out at its
/// measured height (rows collapse, sections stack — see AutoSizeHeight).
/// </summary>
public static class ResourceTooltip
{
    public const string WidgetId = "ResourceTooltipDyn";
    private const int MaxRows = 12;

    // Child indices within the root widget (header / box / desc).
    private const int HeaderIdx = 0;
    private const int BoxIdx = 1;

    public static readonly Color ValueDefault = new(202, 179, 143);
    public static readonly Color ValueGreen = new(88, 130, 90);
    public static readonly Color ValueRed = new(153, 45, 37);

    public readonly record struct Row(string Label, string Value, Color Color);

    /// <summary>Convenience: green for positive deltas, red for negative,
    /// parchment for anything else.</summary>
    public static Row Entry(string label, int value, bool signed = true)
    {
        string text = signed && value > 0 ? "+" + value : value.ToString();
        var col = !signed ? ValueDefault : value > 0 ? ValueGreen : value < 0 ? ValueRed : ValueDefault;
        return new Row(label, text, col);
    }

    public static void Bind(RuntimeWidgetRenderer r, string instanceId,
        string title, string headerValue, Color headerColor,
        System.Collections.Generic.IReadOnlyList<Row> rows, string description)
    {
        r.ClearOverridesRecursive(instanceId);

        string header = $"{instanceId}.{HeaderIdx}";
        r.SetText(header, "title", title);
        r.SetText(header, "value", headerValue);
        r.SetTextColor(header, "value", headerColor.R, headerColor.G, headerColor.B);

        // Min one row: an all-'-' row keeps the box visible when empty.
        string box = $"{instanceId}.{BoxIdx}";
        int count = System.Math.Max(1, System.Math.Min(rows.Count, MaxRows));
        for (int i = 0; i < MaxRows; i++)
        {
            if (i >= count) { r.SetHidden(box, $"row{i}", true); continue; }
            string rowInst = $"{box}.{i}";
            if (i < rows.Count)
            {
                r.SetText(rowInst, "label", rows[i].Label);
                r.SetText(rowInst, "value", rows[i].Value);
                var c = rows[i].Color;
                r.SetTextColor(rowInst, "value", c.R, c.G, c.B);
            }
            else
            {
                r.SetText(rowInst, "label", "-");
                r.SetText(rowInst, "value", "-");
            }
        }

        if (string.IsNullOrEmpty(description))
            r.SetHidden(instanceId, "rtd_desc", true);
        else
            r.SetText(instanceId, "rtd_desc", description);
    }
}
