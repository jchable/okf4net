// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net.Yaml;

/// <summary>
/// A parsed YAML value: Null, Bool, Int, Float, Str, Seq, Map.
/// </summary>
public abstract class YamlValue
{
    /// <summary>
    /// Parses a single YAML value from text (the OKF frontmatter subset).
    /// </summary>
    public static YamlValue Parse(string text) => YamlParser.Parse(text);

    /// <summary>
    /// Emits this value as YAML text using block style, preserving key order.
    /// </summary>
    public string ToYamlString() => YamlEmitter.Emit(this);

    /// <summary>
    /// Wraps a string as a <see cref="YamlString"/>.
    /// </summary>
    public static implicit operator YamlValue(string value) => new YamlString(value);

    /// <summary>
    /// Wraps a bool as a <see cref="YamlBool"/>.
    /// </summary>
    public static implicit operator YamlValue(bool value) => new YamlBool(value);

    /// <summary>
    /// Wraps a 64-bit integer as a <see cref="YamlInt"/>.
    /// </summary>
    public static implicit operator YamlValue(long value) => new YamlInt(value);

    /// <summary>
    /// Wraps an array of values as a <see cref="YamlSequence"/>.
    /// </summary>
    public static implicit operator YamlValue(YamlValue[] items) => new YamlSequence(items);

    // No conversion is needed for a mapping: YamlMapping already IS-A
    // YamlValue (a subclass, not a wrapped field), so using one as a
    // YamlValue is just a no-op upcast the compiler performs.
    //
    // There is intentionally no implicit `double` -> YamlValue operator:
    // floats must be wrapped explicitly via `new YamlFloat(...)` to keep the
    // conversion surface small and unambiguous.

    /// <summary>Returns the string contents if this is a <see cref="YamlString"/>.</summary>
    public string? AsString() => (this as YamlString)?.Value;

    /// <summary>Returns the boolean if this is a <see cref="YamlBool"/>.</summary>
    public bool? AsBool() => (this as YamlBool)?.Value;

    /// <summary>Returns the integer if this is a <see cref="YamlInt"/>.</summary>
    public long? AsInt() => (this as YamlInt)?.Value;

    /// <summary>Returns the sequence elements if this is a <see cref="YamlSequence"/>.</summary>
    public IReadOnlyList<YamlValue>? AsSequence() => (this as YamlSequence)?.Items;

    /// <summary>Returns the mapping if this is a <see cref="YamlMapping"/>.</summary>
    public YamlMapping? AsMapping() => this as YamlMapping;

    /// <summary>
    /// Coerces <paramref name="value"/> to a list of strings: <c>[]</c> unless
    /// it is a <see cref="YamlSequence"/>, in which case each element is
    /// rendered via <see cref="AsDisplayString"/> and non-scalar (<c>null</c>)
    /// elements are dropped.
    /// </summary>
    internal static IReadOnlyList<string> AsStringList(YamlValue? value)
        => value is YamlSequence seq
            ? seq.Items.Select(v => v.AsDisplayString()).Where(s => s is not null).Select(s => s!).ToList()
            : [];

    /// <summary>
    /// True for null, an empty string, an empty sequence, an empty mapping,
    /// <c>false</c>, or <c>0</c>. Note there is deliberately no Float arm, so
    /// <see cref="YamlFloat"/>(0.0) is NOT empty.
    /// </summary>
    public bool IsEmptyValue => this switch
    {
        YamlNull => true,
        YamlString s => s.Value.Length == 0,
        YamlSequence seq => seq.Items.Count == 0,
        YamlMapping map => map.IsEmpty,
        YamlBool b => !b.Value,
        YamlInt i => i.Value == 0,
        _ => false,
    };

    /// <summary>
    /// Renders a scalar as a plain display string (used for typed frontmatter
    /// accessors that coerce scalars to text).
    ///
    /// The float branch uses a *different* format than
    /// <see cref="ToYamlString"/>'s emitter: the plain display format never
    /// uses scientific notation and never forces a trailing ".0", and
    /// non-finite values print as "NaN"/"inf"/"-inf" (not the YAML tokens
    /// ".nan"/".inf"/"-.inf"). See <see cref="YamlEmitter.FormatDisplayFloat"/>
    /// for that exact format.
    /// </summary>
    public string? AsDisplayString() => this switch
    {
        YamlString s => s.Value,
        YamlBool b => b.Value ? "true" : "false",
        YamlInt i => i.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
        YamlFloat f => YamlEmitter.FormatDisplayFloat(f.Value),
        _ => null,
    };

    /// <summary>
    /// Renders this value as YAML text (equivalent to <see cref="ToYamlString"/>).
    /// </summary>
    public override string ToString() => ToYamlString();

    /// <summary>
    /// Structural (deep) equality: same type, recursively equal contents,
    /// mapping entries compared in order (not just by key set). Floats compare
    /// with IEEE-754 semantics (via <c>==</c>) — so <c>NaN</c> never equals
    /// <c>NaN</c>, and <c>-0.0</c> equals <c>0.0</c>.
    /// </summary>
    public override bool Equals(object? obj) => obj is YamlValue other && ValueEquals(this, other);

    /// <inheritdoc cref="Equals(object?)"/>
    private static bool ValueEquals(YamlValue a, YamlValue b) => (a, b) switch
    {
        (YamlNull, YamlNull) => true,
        (YamlBool x, YamlBool y) => x.Value == y.Value,
        (YamlInt x, YamlInt y) => x.Value == y.Value,
        (YamlFloat x, YamlFloat y) => x.Value == y.Value,
        (YamlString x, YamlString y) => string.Equals(x.Value, y.Value, StringComparison.Ordinal),
        (YamlSequence x, YamlSequence y) => x.Items.Count == y.Items.Count && x.Items.SequenceEqual(y.Items),
        (YamlMapping x, YamlMapping y) => MappingEquals(x, y),
        _ => false,
    };

    private static bool MappingEquals(YamlMapping a, YamlMapping b)
    {
        if (a.Count != b.Count)
        {
            return false;
        }

        using var ea = a.Entries.GetEnumerator();
        using var eb = b.Entries.GetEnumerator();
        while (ea.MoveNext() && eb.MoveNext())
        {
            if (!ValueEquals(ea.Current.Key, eb.Current.Key))
            {
                return false;
            }

            if (!ValueEquals(ea.Current.Value, eb.Current.Value))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Consistent with <see cref="Equals(object?)"/>: equal values always
    /// hash equally (in particular, <c>0.0</c> and <c>-0.0</c> are normalized
    /// to the same hash, matching their IEEE-754 equality above).
    /// </summary>
    public override int GetHashCode() => this switch
    {
        YamlNull => HashCode.Combine(typeof(YamlNull)),
        YamlBool b => HashCode.Combine(typeof(YamlBool), b.Value),
        YamlInt i => HashCode.Combine(typeof(YamlInt), i.Value),
        YamlFloat f => HashCode.Combine(typeof(YamlFloat), f.Value == 0.0 ? 0.0 : f.Value),
        YamlString s => HashCode.Combine(typeof(YamlString), s.Value),
        YamlSequence seq => SequenceHashCode(seq.Items),
        YamlMapping map => MappingHashCode(map),
        _ => 0,
    };

    private static int SequenceHashCode(IReadOnlyList<YamlValue> items)
    {
        var hash = new HashCode();
        hash.Add(typeof(YamlSequence));
        foreach (var item in items)
        {
            hash.Add(item);
        }

        return hash.ToHashCode();
    }

    private static int MappingHashCode(YamlMapping map)
    {
        var hash = new HashCode();
        hash.Add(typeof(YamlMapping));
        foreach (var (key, value) in map.Entries)
        {
            hash.Add(key);
            hash.Add(value);
        }

        return hash.ToHashCode();
    }
}

/// <summary>`null`, `~`, or an empty value.</summary>
public sealed class YamlNull : YamlValue
{
    /// <summary>The singleton null value.</summary>
    public static readonly YamlNull Instance = new();

    private YamlNull()
    {
    }
}

/// <summary>`true` / `false`.</summary>
public sealed class YamlBool : YamlValue
{
    /// <summary>Wraps <paramref name="value"/> as a YAML boolean.</summary>
    public YamlBool(bool value) => Value = value;

    /// <summary>The boolean value.</summary>
    public bool Value { get; }
}

/// <summary>An integer scalar.</summary>
public sealed class YamlInt : YamlValue
{
    /// <summary>Wraps <paramref name="value"/> as a YAML integer.</summary>
    public YamlInt(long value) => Value = value;

    /// <summary>The integer value.</summary>
    public long Value { get; }
}

/// <summary>A floating-point scalar.</summary>
public sealed class YamlFloat : YamlValue
{
    /// <summary>Wraps <paramref name="value"/> as a YAML float.</summary>
    public YamlFloat(double value) => Value = value;

    /// <summary>The floating-point value.</summary>
    public double Value { get; }
}

/// <summary>A string scalar.</summary>
public sealed class YamlString : YamlValue
{
    /// <summary>Wraps <paramref name="value"/> as a YAML string.</summary>
    public YamlString(string value) => Value = value;

    /// <summary>The string value.</summary>
    public string Value { get; }
}

/// <summary>A sequence (`[...]` or block `- ...`).</summary>
public sealed class YamlSequence : YamlValue
{
    /// <summary>
    /// Wraps <paramref name="items"/> as a YAML sequence, defensively copying
    /// it. A plain reference assignment would alias the caller's list and let
    /// post-construction mutation leak through; copying makes the sequence
    /// immutable once constructed.
    /// </summary>
    public YamlSequence(IReadOnlyList<YamlValue> items) => Items = [.. items];

    /// <summary>The sequence elements, in document order.</summary>
    public IReadOnlyList<YamlValue> Items { get; }
}
