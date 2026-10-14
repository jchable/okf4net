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
    /// <exception cref="YamlParseException">
    /// The text is not valid within the subset, including when it uses an
    /// anchor, alias, tag, directive, document marker, an indented <c>---</c> line
    /// outside block-scalar content, or text after a closing quote.
    /// </exception>
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
    /// Structural (deep) equality: same type, equal contents all the way
    /// down, mapping entries compared in order (not just by key set). Floats
    /// compare with IEEE-754 semantics (via <c>==</c>) — so <c>NaN</c> never
    /// equals <c>NaN</c>, not even within one value compared with itself, and
    /// <c>-0.0</c> equals <c>0.0</c>.
    /// </summary>
    /// <remarks>
    /// The comparison never recurses, so it answers for a value of any depth,
    /// including one built in code past the parser's nesting rule (#158).
    /// <see cref="YamlMapping"/> is mutable, so a value built in code can also
    /// share a subtree or contain itself. Two values are then equal when they
    /// unfold into the same tree, however their sharing or cycles are laid
    /// out. Past the first few, each pair of collections is compared once, so
    /// the answer comes even when the unfolding is infinite or exponentially
    /// large. The comparison stops at the first difference.
    /// </remarks>
    public override bool Equals(object? obj) => obj is YamlValue other && ValueEquals(this, other);

    /// <inheritdoc cref="Equals(object?)"/>
    private static bool ValueEquals(YamlValue a, YamlValue b)
    {
        // Most comparisons are of two scalars (the emitter asks one for every
        // string it writes): those allocate nothing.
        if (!IsCollection(a) || !IsCollection(b))
        {
            return ScalarEquals(a, b);
        }

        if (!SameShape(a, b))
        {
            return false;
        }

        // One frame per pair of collections being compared, with the index of
        // its next child pair: children are compared in order, the walk stops
        // at the first difference, and the stack grows with depth, not width.
        //
        // Equality holds when every pair reachable from (a, b) matches. A pair
        // of collections met again has been or is being compared already, so
        // past the first UnrememberedPairs collection pairs each one is
        // remembered and a repeat is skipped. That ends the walk on a cycle and
        // keeps shared subtrees linear; on a tree no pair repeats, so the
        // result is plain structural equality. Below the threshold nothing is
        // remembered, so a small value allocates no set; a repeat there is
        // compared again, which costs time but changes no answer. The pair is
        // remembered as a pair: one node met twice with two different partners
        // is compared with each.
        var frames = new Stack<(YamlValue X, YamlValue Y, int Next)>();
        HashSet<(YamlValue, YamlValue)>? remembered = null;
        var opened = 1;
        frames.Push((a, b, 0));
        while (frames.TryPop(out var frame))
        {
            var (x, y, next) = frame;
            if (next == ChildCount(x))
            {
                continue;
            }

            frames.Push((x, y, next + 1));
            var (childX, childY) = Child(x, y, next);
            if (!IsCollection(childX) || !IsCollection(childY))
            {
                if (!ScalarEquals(childX, childY))
                {
                    return false;
                }

                continue;
            }

            if (!SameShape(childX, childY))
            {
                return false;
            }

            if (++opened > UnrememberedPairs
                && !(remembered ??= new HashSet<(YamlValue, YamlValue)>(ReferencePairComparer.Instance)).Add((childX, childY)))
            {
                continue;
            }

            frames.Push((childX, childY, 0));
        }

        return true;
    }

    /// <summary>
    /// How many pairs of collections <see cref="ValueEquals"/> compares before
    /// it starts remembering them. A typical frontmatter holds fewer, so its
    /// comparison allocates no set.
    /// </summary>
    private const int UnrememberedPairs = 64;

    /// <summary>Both sequences, or both mappings, with as many items or entries.</summary>
    private static bool SameShape(YamlValue x, YamlValue y) => (x, y) switch
    {
        (YamlSequence xs, YamlSequence ys) => xs.Items.Count == ys.Items.Count,
        (YamlMapping xm, YamlMapping ym) => xm.Count == ym.Count,
        _ => false,
    };

    /// <summary>The number of child pairs of a collection: its items, or a key and a value per entry.</summary>
    private static int ChildCount(YamlValue collection) =>
        collection is YamlSequence seq ? seq.Items.Count : 2 * ((YamlMapping)collection).Count;

    /// <summary>Child pair <paramref name="index"/> of two collections of the same shape.</summary>
    private static (YamlValue, YamlValue) Child(YamlValue x, YamlValue y, int index)
    {
        if (x is YamlSequence xs)
        {
            return (xs.Items[index], ((YamlSequence)y).Items[index]);
        }

        var xe = ((YamlMapping)x).Entries[index / 2];
        var ye = ((YamlMapping)y).Entries[index / 2];
        return index % 2 == 0 ? (xe.Key, ye.Key) : (xe.Value, ye.Value);
    }

    private static bool IsCollection(YamlValue value) => value is YamlSequence or YamlMapping;

    /// <summary>Equality of two values that are not both sequences or both mappings.</summary>
    private static bool ScalarEquals(YamlValue a, YamlValue b) => (a, b) switch
    {
        (YamlNull, YamlNull) => true,
        (YamlBool x, YamlBool y) => x.Value == y.Value,
        (YamlInt x, YamlInt y) => x.Value == y.Value,
        (YamlFloat x, YamlFloat y) => x.Value == y.Value,
        (YamlString x, YamlString y) => string.Equals(x.Value, y.Value, StringComparison.Ordinal),
        _ => false,
    };

    /// <summary>
    /// How many nodes of a value <see cref="GetHashCode"/> reads, breadth
    /// first from the root. A frontmatter rarely holds as many, so in practice
    /// the whole value is hashed. Values that differ only past it share a
    /// hash: legal for a hash, and the price of a bounded cost.
    /// </summary>
    private const int HashedNodeBudget = 1024;

    /// <summary>
    /// Consistent with <see cref="Equals(object?)"/>: equal values always
    /// hash equally (in particular, <c>0.0</c> and <c>-0.0</c> are normalized
    /// to the same hash, matching their IEEE-754 equality above).
    /// </summary>
    /// <remarks>
    /// Reads at most the first <see cref="HashedNodeBudget"/> nodes of the
    /// value's unfolding, breadth first, without recursing: the cost is bounded
    /// whatever the value's depth, sharing or cycles (#158). Equal values
    /// unfold into the same tree, so they present the same nodes in the same
    /// order and hash equally.
    /// </remarks>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        if (!IsCollection(this))
        {
            AddScalar(ref hash, this);
            return hash.ToHashCode();
        }

        var queue = new Queue<YamlValue>();
        queue.Enqueue(this);
        for (var budget = HashedNodeBudget; budget > 0 && queue.TryDequeue(out var node); budget--)
        {
            // Reads left after this node: nothing past them is queued.
            var room = budget - 1;
            switch (node)
            {
                case YamlSequence seq:
                    hash.Add(typeof(YamlSequence));
                    hash.Add(seq.Items.Count);
                    for (var i = 0; i < seq.Items.Count && queue.Count < room; i++)
                    {
                        queue.Enqueue(seq.Items[i]);
                    }

                    break;
                case YamlMapping map:
                    hash.Add(typeof(YamlMapping));
                    hash.Add(map.Count);
                    var entries = map.Entries;
                    for (var i = 0; i < entries.Count && queue.Count + 2 <= room; i++)
                    {
                        queue.Enqueue(entries[i].Key);
                        queue.Enqueue(entries[i].Value);
                    }

                    break;
                default:
                    AddScalar(ref hash, node);
                    break;
            }
        }

        return hash.ToHashCode();
    }

    /// <summary>Adds one value that is not a collection to <paramref name="hash"/>.</summary>
    private static void AddScalar(ref HashCode hash, YamlValue value)
    {
        switch (value)
        {
            case YamlNull:
                hash.Add(typeof(YamlNull));
                break;
            case YamlBool b:
                hash.Add(typeof(YamlBool));
                hash.Add(b.Value);
                break;
            case YamlInt i:
                hash.Add(typeof(YamlInt));
                hash.Add(i.Value);
                break;
            case YamlFloat f:
                hash.Add(typeof(YamlFloat));
                hash.Add(f.Value == 0.0 ? 0.0 : f.Value);
                break;
            case YamlString s:
                hash.Add(typeof(YamlString));
                hash.Add(s.Value);
                break;
        }
    }

    /// <summary>Compares pairs of values by reference: the set of pairs <see cref="ValueEquals"/> has already met.</summary>
    private sealed class ReferencePairComparer : IEqualityComparer<(YamlValue, YamlValue)>
    {
        public static readonly ReferencePairComparer Instance = new();

        public bool Equals((YamlValue, YamlValue) x, (YamlValue, YamlValue) y) =>
            ReferenceEquals(x.Item1, y.Item1) && ReferenceEquals(x.Item2, y.Item2);

        public int GetHashCode((YamlValue, YamlValue) pair) =>
            HashCode.Combine(
                System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(pair.Item1),
                System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(pair.Item2));
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
