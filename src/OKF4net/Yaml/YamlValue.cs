namespace OKF4net.Yaml;

/// <summary>
/// A parsed YAML value. Mirror of the Rust <c>Value</c> enum
/// (src/yaml/mod.rs, lines 114-211): Null, Bool, Int, Float, Str, Seq, Map.
/// </summary>
public abstract class YamlValue
{
    /// <summary>
    /// Parses a single YAML value from text (the OKF frontmatter subset).
    /// Implemented in Task 2.
    /// </summary>
    public static YamlValue Parse(string text) => throw new NotImplementedException();

    /// <summary>
    /// Emits this value as YAML text using block style, preserving key order.
    /// Implemented in Task 3 (delegates to YamlEmitter.Emit).
    /// </summary>
    public string ToYamlString() => throw new NotImplementedException();

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
    /// <c>as_display_string</c>.
    /// </summary>
    public string? AsDisplayString() => this switch
    {
        YamlString s => s.Value,
        YamlBool b => b.Value ? "true" : "false",
        YamlInt i => i.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
        YamlFloat f => f.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
        _ => null,
    };
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
