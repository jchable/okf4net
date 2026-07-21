// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net.Yaml;

/// <summary>
/// A parsed YAML value. Mirror of the Rust <c>Value</c> enum
/// (src/yaml/mod.rs, lines 114-211): Null, Bool, Int, Float, Str, Seq, Map.
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
    /// Wraps a string as a <see cref="YamlString"/>. Port of Rust's
    /// <c>impl From&lt;&amp;str&gt; for Value</c> (yaml/mod.rs:219-223).
    /// </summary>
    public static implicit operator YamlValue(string value) => new YamlString(value);

    /// <summary>
    /// Wraps a bool as a <see cref="YamlBool"/>. Port of Rust's
    /// <c>impl From&lt;bool&gt; for Value</c> (yaml/mod.rs:231-235).
    /// </summary>
    public static implicit operator YamlValue(bool value) => new YamlBool(value);

    /// <summary>
    /// Wraps a 64-bit integer as a <see cref="YamlInt"/>. Port of Rust's
    /// <c>impl From&lt;i64&gt; for Value</c> (yaml/mod.rs:237-241).
    /// </summary>
    public static implicit operator YamlValue(long value) => new YamlInt(value);

    /// <summary>
    /// Wraps an array of values as a <see cref="YamlSequence"/>. Closest C#
    /// idiom to Rust's <c>impl&lt;T: Into&lt;Value&gt;&gt; From&lt;Vec&lt;T&gt;&gt; for Value</c>
    /// (yaml/mod.rs:243-247).
    /// </summary>
    public static implicit operator YamlValue(YamlValue[] items) => new YamlSequence(items);

    // Rust also has `impl From<Mapping> for Value` (yaml/mod.rs:249-253), but
    // that has no C# equivalent to write: YamlMapping already IS-A YamlValue
    // (it's a subclass, not a wrapped field), so a YamlMapping needs no
    // conversion to be used as a YamlValue -- the "conversion" is a no-op
    // upcast the compiler already performs.
    //
    // Rust has no `impl From<f64> for Value`, so there is intentionally no
    // implicit `double` -> YamlValue operator here (would introduce a
    // conversion the Rust surface doesn't have).

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
    /// True for null, an empty string, an empty sequence, an empty mapping,
    /// <c>false</c>, or <c>0</c>. Port of the Rust <c>is_empty_value</c>
    /// (src/yaml/mod.rs lines 187-197) — note there is no Float arm there,
    /// so <see cref="YamlFloat"/>(0.0) is intentionally NOT empty.
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
    /// accessors that coerce scalars to text). Port of the Rust
    /// <c>as_display_string</c> (src/yaml/mod.rs lines 199-210).
    ///
    /// The float branch there is <c>format!("{f}")</c> — Rust's f64
    /// <c>Display</c> impl, which is a *different* format than the emitter's
    /// <c>format_float</c> (Rust's f64 <c>Debug</c> impl, used by
    /// <see cref="ToYamlString"/>): Display never uses scientific notation
    /// and never forces a trailing ".0", and non-finite values print as
    /// "NaN"/"inf"/"-inf" (not the YAML tokens ".nan"/".inf"/"-.inf"). See
    /// <see cref="YamlEmitter.FormatDisplayFloat"/> for the port of that
    /// exact format (previously this used
    /// <c>double.ToString(InvariantCulture)</c>, which diverges from Rust on
    /// both points).
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
    /// Renders this value as YAML text, matching Rust's <c>impl fmt::Display
    /// for Value</c> (src/yaml/mod.rs lines 213-217), which writes
    /// <c>to_yaml_string()</c>.
    /// </summary>
    public override string ToString() => ToYamlString();

    /// <summary>
    /// Structural (deep) equality, mirroring Rust's derived <c>PartialEq</c>
    /// for <c>Value</c>/<c>Mapping</c> (src/yaml/mod.rs lines 42 and 116):
    /// same variant/type, recursively equal contents, mapping entries
    /// compared in order (not just by key set). Floats compare with IEEE-754
    /// semantics (via <c>==</c>), like Rust's <c>f64: PartialEq</c> — so
    /// <c>NaN</c> never equals <c>NaN</c>, and <c>-0.0</c> equals <c>0.0</c>.
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
    public static readonly YamlNull Instance = new();

    private YamlNull()
    {
    }
}

/// <summary>`true` / `false`.</summary>
public sealed class YamlBool : YamlValue
{
    public YamlBool(bool value) => Value = value;

    public bool Value { get; }
}

/// <summary>An integer scalar.</summary>
public sealed class YamlInt : YamlValue
{
    public YamlInt(long value) => Value = value;

    public long Value { get; }
}

/// <summary>A floating-point scalar.</summary>
public sealed class YamlFloat : YamlValue
{
    public YamlFloat(double value) => Value = value;

    public double Value { get; }
}

/// <summary>A string scalar.</summary>
public sealed class YamlString : YamlValue
{
    public YamlString(string value) => Value = value;

    public string Value { get; }
}

/// <summary>A sequence (`[...]` or block `- ...`).</summary>
public sealed class YamlSequence : YamlValue
{
    public YamlSequence(IReadOnlyList<YamlValue> items) => Items = items;

    public IReadOnlyList<YamlValue> Items { get; }
}
