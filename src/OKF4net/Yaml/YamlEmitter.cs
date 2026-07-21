// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Globalization;
using System.Text;

namespace OKF4net.Yaml;

/// <summary>
/// Block-style YAML emitter for the OKF subset. Port of src/yaml/emitter.rs
/// (the Rust reference — authoritative for quoting rules, escape sequences,
/// indentation, and float formatting).
///
/// The emitter targets one property: re-parsing its output reproduces the
/// input value (<c>parse(emit(v)) == v</c>), with mapping key order
/// preserved. It is not intended to be byte-identical to any other YAML
/// writer.
/// </summary>
public static class YamlEmitter
{
    private const int IndentStep = 2;

    /// <summary>
    /// Emits a value as YAML text (always ends with "\n", like PyYAML's
    /// <c>safe_dump</c>). Port of Rust's <c>emit</c> (emitter.rs lines 13-24).
    /// </summary>
    public static string Emit(YamlValue value)
    {
        var outSb = new StringBuilder();
        switch (value)
        {
            case YamlMapping m when !m.IsEmpty:
                EmitMapping(m, 0, outSb);
                break;
            case YamlSequence s when s.Items.Count > 0:
                EmitSequence(s.Items, 0, outSb);
                break;
            default:
                outSb.Append(EmitScalar(value));
                outSb.Append('\n');
                break;
        }

        return outSb.ToString();
    }

    /// <summary>Port of Rust's <c>emit_mapping</c> (emitter.rs lines 26-42).</summary>
    private static void EmitMapping(YamlMapping map, int indent, StringBuilder outSb)
    {
        var pad = new string(' ', indent);
        foreach (var (key, value) in map.Entries)
        {
            var keyText = EmitString(key);
            switch (value)
            {
                case YamlMapping m when !m.IsEmpty:
                    outSb.Append(pad).Append(keyText).Append(":\n");
                    EmitMapping(m, indent + IndentStep, outSb);
                    break;
                case YamlSequence s when s.Items.Count > 0:
                    outSb.Append(pad).Append(keyText).Append(":\n");
                    EmitSequence(s.Items, indent + IndentStep, outSb);
                    break;
                default:
                    outSb.Append(pad).Append(keyText).Append(": ").Append(EmitScalar(value)).Append('\n');
                    break;
            }
        }
    }

    /// <summary>Port of Rust's <c>emit_sequence</c> (emitter.rs lines 44-59).</summary>
    private static void EmitSequence(IReadOnlyList<YamlValue> seq, int indent, StringBuilder outSb)
    {
        var pad = new string(' ', indent);
        foreach (var item in seq)
        {
            switch (item)
            {
                case YamlMapping m when !m.IsEmpty:
                    outSb.Append(pad).Append("-\n");
                    EmitMapping(m, indent + IndentStep, outSb);
                    break;
                case YamlSequence s when s.Items.Count > 0:
                    outSb.Append(pad).Append("-\n");
                    EmitSequence(s.Items, indent + IndentStep, outSb);
                    break;
                default:
                    outSb.Append(pad).Append("- ").Append(EmitScalar(item)).Append('\n');
                    break;
            }
        }
    }

    /// <summary>
    /// Emits a scalar (or an empty collection) inline. Port of Rust's
    /// <c>emit_scalar</c> (emitter.rs lines 62-75). Non-empty collections
    /// never reach here in block context; matching the Rust source, both
    /// fall back to "[]" (not "{}" for a non-empty mapping) if they somehow
    /// do.
    /// </summary>
    private static string EmitScalar(YamlValue value) => value switch
    {
        YamlNull => "null",
        YamlBool { Value: true } => "true",
        YamlBool { Value: false } => "false",
        YamlInt i => i.Value.ToString(CultureInfo.InvariantCulture),
        YamlFloat f => FormatFloat(f.Value),
        YamlString s => EmitString(s.Value),
        YamlSequence { Items.Count: 0 } => "[]",
        YamlMapping { IsEmpty: true } => "{}",
        YamlSequence or YamlMapping => "[]",
        _ => throw new InvalidOperationException($"unreachable YamlValue kind: {value.GetType()}"),
    };

    /// <summary>
    /// Port of Rust's <c>format_float</c> (emitter.rs lines 77-95): special
    /// tokens for non-finite values, otherwise the shortest round-tripping
    /// representation (Rust's <c>{:?}</c> Debug format for f64), with a "."
    /// guaranteed to be present so the value re-parses as a float rather than
    /// a string (see <see cref="DebugFormat"/>).
    /// </summary>
    internal static string FormatFloat(double f)
    {
        if (double.IsNaN(f))
        {
            return ".nan";
        }

        if (double.IsPositiveInfinity(f))
        {
            return ".inf";
        }

        if (double.IsNegativeInfinity(f))
        {
            return "-.inf";
        }

        var s = DebugFormat(f);
        if (s.Contains('.'))
        {
            return s;
        }

        var eIdx = s.IndexOf('e');
        return eIdx >= 0
            ? string.Concat(s.AsSpan(0, eIdx), ".0", s.AsSpan(eIdx))
            : s + ".0";
    }

    /// <summary>
    /// Reproduces Rust's <c>format!("{f:?}")</c> (the <c>Debug</c> impl for
    /// f64): the shortest round-tripping decimal digit sequence, rendered in
    /// plain decimal notation with a forced fractional digit
    /// (e.g. <c>1.0</c>) when the magnitude is in Rust's "general format"
    /// range (<c>1e-4 &lt;= |f| &lt; 1e16</c>, or exactly zero), and in
    /// lowercase exponential notation with no forced fractional digit
    /// (e.g. <c>1e30</c>) otherwise.
    /// </summary>
    private static string DebugFormat(double f)
    {
        var negative = double.IsNegative(f);
        var abs = Math.Abs(f);
        var (digits, exp) = abs == 0.0 ? ("0", 0) : DecomposeShortest(abs);
        var sign = negative ? "-" : "";
        var useDecimal = abs == 0.0 || (abs >= 1e-4 && abs < 1e16);

        if (useDecimal)
        {
            var (intPart, fracPart) = PlaceDecimalPoint(digits, exp + 1);
            if (fracPart.Length == 0)
            {
                fracPart = "0"; // Debug forces at least one fractional digit.
            }

            return $"{sign}{intPart}.{fracPart}";
        }

        var mantissa = digits.Length > 1 ? $"{digits[0]}.{digits[1..]}" : digits;
        return $"{sign}{mantissa}e{exp.ToString(CultureInfo.InvariantCulture)}";
    }

    /// <summary>
    /// Reproduces Rust's <c>format!("{f}")</c> (the <c>Display</c> impl for
    /// f64, used by <c>as_display_string</c> — src/yaml/mod.rs line 207):
    /// like <see cref="DebugFormat"/> but always plain decimal notation
    /// (Display never uses scientific notation, regardless of magnitude) and
    /// no forced fractional digit (e.g. <c>3</c>, not <c>3.0</c>).
    /// </summary>
    internal static string FormatDisplayFloat(double f)
    {
        if (double.IsNaN(f))
        {
            return "NaN";
        }

        if (double.IsPositiveInfinity(f))
        {
            return "inf";
        }

        if (double.IsNegativeInfinity(f))
        {
            return "-inf";
        }

        var negative = double.IsNegative(f);
        var abs = Math.Abs(f);
        var (digits, exp) = abs == 0.0 ? ("0", 0) : DecomposeShortest(abs);
        var (intPart, fracPart) = PlaceDecimalPoint(digits, exp + 1);
        var sign = negative ? "-" : "";
        return fracPart.Length == 0 ? $"{sign}{intPart}" : $"{sign}{intPart}.{fracPart}";
    }

    /// <summary>
    /// Splits a significant-digit string at <paramref name="pointPos"/>
    /// (counted from the left, may be outside <c>[0, digits.Length]</c>),
    /// padding with zeros as needed, to render plain (non-exponential)
    /// decimal notation.
    /// </summary>
    private static (string IntPart, string FracPart) PlaceDecimalPoint(string digits, int pointPos)
    {
        if (pointPos <= 0)
        {
            return ("0", new string('0', -pointPos) + digits);
        }

        if (pointPos >= digits.Length)
        {
            return (digits + new string('0', pointPos - digits.Length), "");
        }

        return (digits[..pointPos], digits[pointPos..]);
    }

    /// <summary>
    /// Decomposes a positive finite <paramref name="absValue"/> into its
    /// shortest round-tripping significant digits and a decimal exponent,
    /// such that the value equals <c>digits[0].digits[1..] * 10^exponent</c>.
    /// Relies on .NET's default (culture-invariant) <see cref="double.ToString()"/>
    /// already being the shortest round-tripping representation (true since
    /// .NET Core 3.0), just re-normalized here since .NET's own choice of
    /// decimal vs. scientific notation, and exponent padding, differ from
    /// Rust's.
    /// </summary>
    private static (string Digits, int Exponent) DecomposeShortest(double absValue)
    {
        var s = absValue.ToString(CultureInfo.InvariantCulture);
        var exp = 0;
        var eIdx = s.IndexOfAny(['E', 'e']);
        var mantissa = s;
        if (eIdx >= 0)
        {
            mantissa = s[..eIdx];
            exp = int.Parse(s[(eIdx + 1)..], CultureInfo.InvariantCulture);
        }

        var dotIdx = mantissa.IndexOf('.');
        var intPart = dotIdx >= 0 ? mantissa[..dotIdx] : mantissa;
        var fracPart = dotIdx >= 0 ? mantissa[(dotIdx + 1)..] : "";

        var allDigits = intPart + fracPart;
        var pointPos = intPart.Length;

        var firstNonZero = 0;
        while (firstNonZero < allDigits.Length && allDigits[firstNonZero] == '0')
        {
            firstNonZero++;
        }

        if (firstNonZero == allDigits.Length)
        {
            return ("0", 0); // absValue was zero (shouldn't normally reach here).
        }

        var lastNonZero = allDigits.Length - 1;
        while (lastNonZero > firstNonZero && allDigits[lastNonZero] == '0')
        {
            lastNonZero--;
        }

        var digits = allDigits[firstNonZero..(lastNonZero + 1)];
        var exponent = pointPos - firstNonZero - 1 + exp;
        return (digits, exponent);
    }

    /// <summary>Port of Rust's <c>emit_string</c> (emitter.rs lines 97-103).</summary>
    private static string EmitString(string s) => IsSafePlain(s) ? s : DoubleQuote(s);

    private static readonly char[] Indicators =
    [
        '-', '?', ':', ',', '[', ']', '{', '}', '#', '&', '*', '!', '|', '>', '\'', '"', '%', '@', '`', ' ',
    ];

    /// <summary>
    /// Whether a string can be emitted as a plain (unquoted) scalar without
    /// being misread on re-parse. Port of Rust's <c>is_safe_plain</c>
    /// (emitter.rs lines 105-137).
    /// </summary>
    private static bool IsSafePlain(string s)
    {
        if (s.Length == 0)
        {
            return false;
        }

        // Must not be reinterpreted as null/bool/number.
        YamlValue parsed;
        try
        {
            parsed = YamlValue.Parse(s);
        }
        catch (YamlParseException)
        {
            // parse() of a multiline/odd string may error; fall through to quoting.
            return false;
        }

        if (!parsed.Equals(new YamlString(s)))
        {
            return false;
        }

        if (s.StartsWith(' ') || s.EndsWith(' '))
        {
            return false;
        }

        var first = s[0];
        if (Indicators.Contains(first))
        {
            return false;
        }

        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            switch (c)
            {
                case '\n' or '\t' or '\r':
                    return false;
                case ':' when i + 1 >= s.Length || s[i + 1] == ' ':
                    return false;
                case '#' when i > 0 && s[i - 1] == ' ':
                    return false;
            }
        }

        return true;
    }

    /// <summary>Port of Rust's <c>double_quote</c> (emitter.rs lines 139-158).</summary>
    private static string DoubleQuote(string s)
    {
        var outSb = new StringBuilder(s.Length + 2);
        outSb.Append('"');
        foreach (var c in s)
        {
            switch (c)
            {
                case '\\':
                    outSb.Append("\\\\");
                    break;
                case '"':
                    outSb.Append("\\\"");
                    break;
                case '\n':
                    outSb.Append("\\n");
                    break;
                case '\t':
                    outSb.Append("\\t");
                    break;
                case '\r':
                    outSb.Append("\\r");
                    break;
                case '':
                    outSb.Append("\\b");
                    break;
                case '':
                    outSb.Append("\\f");
                    break;
                case '\0':
                    outSb.Append("\\0");
                    break;
                default:
                    if (c < 0x20)
                    {
                        outSb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        outSb.Append(c);
                    }

                    break;
            }
        }

        outSb.Append('"');
        return outSb.ToString();
    }
}
