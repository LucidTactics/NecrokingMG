using System;

namespace Necroking.Data;

/// <summary>
/// Identity-keyed parse cache for a JSON string field backed by an enum. Registry
/// defs store enum-ish values as strings (the serialized form — converting the
/// properties to enum types would rewrite every file via JsonStringEnumConverter's
/// camelCase policy); hot game code should read the parsed form through one of
/// these instead of calling Enum.TryParse per use. The cache re-parses only when
/// the string INSTANCE changes (the in-game editors assign new strings when a
/// combo changes), so per-read cost is a reference compare, and a stale cache is
/// impossible. Unparseable/empty strings yield the caller's fallback — bad values
/// are reported once at load by <see cref="EnumJson"/> validation, not per read.
/// </summary>
public struct CachedEnum<TEnum> where TEnum : struct, Enum
{
    private string? _source;
    private TEnum _value;
    private bool _parsed;

    public TEnum Get(string? source, TEnum fallback)
    {
        if (!_parsed || !ReferenceEquals(_source, source))
        {
            _value = Enum.TryParse(source, ignoreCase: true, out TEnum parsed)
                ? parsed : fallback;
            _source = source;
            _parsed = true;
        }
        return _value;
    }
}

/// <summary>
/// Load-time validation helpers for enum-string (and fixed-vocabulary) fields in
/// data/*.json. Used by the per-registry <c>ValidateDef</c> overrides so a typo'd
/// value is reported loudly at startup instead of silently falling back to a
/// default at runtime (see todos/validate_json_integrity.md).
/// </summary>
public static class EnumJson
{
    /// <summary>True when <paramref name="value"/> names a member of
    /// <typeparamref name="TEnum"/> (case-insensitive). Numeric strings are
    /// rejected — data files author names, never ordinals.</summary>
    public static bool IsValid<TEnum>(string value) where TEnum : struct, Enum
    {
        if (string.IsNullOrEmpty(value)) return false;
        char c = value[0];
        if (char.IsDigit(c) || c == '-' || c == '+') return false;
        return Enum.TryParse(value, ignoreCase: true, out TEnum _);
    }

    /// <summary>Report an error if <paramref name="value"/> doesn't name a member
    /// of <typeparamref name="TEnum"/>. Empty/null is allowed by default (pruned
    /// defaults load as the field initializer; "" conventionally means unset).</summary>
    public static void Check<TEnum>(string? value, string field, Action<string> report,
        bool allowEmpty = true) where TEnum : struct, Enum
    {
        if (string.IsNullOrEmpty(value))
        {
            if (!allowEmpty)
                report($"{field} is empty — expected one of: {string.Join(", ", Enum.GetNames(typeof(TEnum)))}");
            return;
        }
        if (!IsValid<TEnum>(value))
            report($"{field}=\"{value}\" is not a valid {typeof(TEnum).Name} — expected one of: "
                + string.Join(", ", Enum.GetNames(typeof(TEnum))));
    }

    /// <summary>Report an error if <paramref name="value"/> isn't in the allowed
    /// vocabulary — for fixed string sets that have no backing enum (spell School,
    /// CastAnim, item categories, ...). Case-sensitive: these strings are compared
    /// verbatim at their use sites.</summary>
    public static void CheckSet(string? value, string field, string[] allowed,
        Action<string> report, bool allowEmpty = true)
    {
        if (string.IsNullOrEmpty(value))
        {
            if (!allowEmpty)
                report($"{field} is empty — expected one of: {string.Join(", ", allowed)}");
            return;
        }
        foreach (var a in allowed)
            if (value == a) return;
        report($"{field}=\"{value}\" is not recognized — expected one of: {string.Join(", ", allowed)}");
    }
}
