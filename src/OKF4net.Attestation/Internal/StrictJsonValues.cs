// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Globalization;
using System.Text.Json;

namespace OKF4net.Attestation.Internal;

/// <summary>Which rule of the strict JSON value contract a document broke.</summary>
internal enum StrictJsonViolation
{
    /// <summary>An object carried the same property name twice.</summary>
    DuplicateProperty,

    /// <summary>A number literal has no exact <see langword="long"/> or <see langword="double"/> counterpart.</summary>
    InexactNumber,

    /// <summary>
    /// A string or property name escapes a lone UTF-16 surrogate, which has no
    /// <see langword="string"/> System.Text.Json will produce: it throws
    /// <see cref="InvalidOperationException"/> instead.
    /// </summary>
    InvalidString,
}

/// <summary>
/// Thrown by <see cref="StrictJsonValues.Normalize"/>. It carries no text taken from
/// the document: each caller maps <see cref="Violation"/> to its own exception and its
/// own fixed wording, because the document is untrusted output (a container's stdout,
/// a model's tool arguments) and a property name or a literal copied into a message
/// would travel into model-facing text.
/// </summary>
internal sealed class StrictJsonException(StrictJsonViolation violation) : Exception(violation.ToString())
{
    /// <summary>The rule the document broke.</summary>
    public StrictJsonViolation Violation { get; } = violation;
}

/// <summary>
/// The one strict JSON-to-CLR normaliser behind every §10 value a JSON document
/// carries into OKF4net: a container's receipt and attester verdict
/// (<c>OKF4net.Attestation.Containers</c>) and <c>okf_run_computation</c>'s parameter
/// values (<c>OKF4net.Agents</c>). Both reach it through <c>InternalsVisibleTo</c>, so
/// the rule lives once. A value that would otherwise be silently resolved — a
/// duplicated property, a number rounded, overflowed to infinity or underflowed to
/// zero — is rejected instead, because §10.5 has the attester judge the receipt, and a
/// receipt that reads differently to two readers, or that carries a number the
/// producer never wrote, is not the one it judged.
/// </summary>
internal static class StrictJsonValues
{
    /// <summary>
    /// Normalizes <paramref name="element"/> into <see langword="string"/>,
    /// <see langword="long"/>/<see langword="double"/>, <see langword="bool"/>,
    /// <see langword="null"/>, <see cref="List{T}"/> of <see cref="object"/>, or
    /// <see cref="Dictionary{TKey,TValue}"/> of <see langword="string"/> to
    /// <see cref="object"/> — never a boxed <see cref="JsonElement"/>.
    /// </summary>
    /// <remarks>
    /// Numbers: a literal with no <c>.</c>, <c>e</c> or <c>E</c> is an integer and must
    /// fit a <see langword="long"/>; any other literal must parse to a finite
    /// <see langword="double"/> whose shortest round-trip form (<c>"R"</c>) denotes the
    /// same decimal value as the literal. Objects: a property name seen twice in one
    /// object (compared after unescaping, ordinally) is rejected. A document parsed with
    /// <c>AllowDuplicateProperties = false</c> never reaches that check, but a
    /// <see cref="JsonElement"/> handed over by a serializer on its default options
    /// does, which is why the walk keeps it.
    /// </remarks>
    /// <exception cref="StrictJsonException">The element breaks a rule above, or escapes a lone surrogate in a string or name, at any depth. Nothing else is thrown.</exception>
    public static object? Normalize(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => ReadString(element.GetString),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Number => NormalizeNumber(element),
        JsonValueKind.Array => element.EnumerateArray().Select(Normalize).ToList(),
        JsonValueKind.Object => NormalizeObject(element),
        _ => null,
    };

    private static Dictionary<string, object?> NormalizeObject(JsonElement element)
    {
        var map = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            var name = ReadString(() => property.Name);
            if (!map.TryAdd(name, Normalize(property.Value)))
            {
                throw new StrictJsonException(StrictJsonViolation.DuplicateProperty);
            }
        }

        return map;
    }

    /// <summary>
    /// Unescapes a string or property name, turning the one failure System.Text.Json
    /// defers to this point — a <c>\uD800</c>-style lone surrogate, which it accepts at
    /// parse time — into a <see cref="StrictJsonException"/> rather than a raw
    /// <see cref="InvalidOperationException"/>.
    /// </summary>
    private static string ReadString(Func<string?> read)
    {
        try
        {
            return read() ?? string.Empty;
        }
        catch (InvalidOperationException)
        {
            throw new StrictJsonException(StrictJsonViolation.InvalidString);
        }
    }

    private static object NormalizeNumber(JsonElement element)
    {
        var literal = element.GetRawText();
        if (literal.AsSpan().IndexOfAny('.', 'e', 'E') < 0)
        {
            return element.TryGetInt64(out var integer)
                ? integer
                : throw new StrictJsonException(StrictJsonViolation.InexactNumber);
        }

        // TryGetDouble reports success for 1e400 (as infinity) and 1e-400 (as zero),
        // so success alone proves nothing: the round-trip comparison is the check.
        if (element.TryGetDouble(out var value)
            && double.IsFinite(value)
            && SameDecimalValue(literal, value.ToString("R", CultureInfo.InvariantCulture)))
        {
            return value;
        }

        throw new StrictJsonException(StrictJsonViolation.InexactNumber);
    }

    /// <summary>
    /// Whether two decimal literals denote the same value, compared as normalised
    /// sign + significant digits + exponent. Called with the JSON literal and the
    /// double's shortest round-trip (<c>"R"</c>) form, so "the same decimal value" means
    /// the same value as that shortest form, not the double's exact binary value:
    /// <c>0.1</c> is accepted although no double equals one tenth, and the exact
    /// expansion <c>0.3000000000000000444089209850062616169452667236328125</c> is
    /// rejected although it is the double's exact value, because its shortest form is
    /// <c>0.30000000000000004</c>. Not through <see langword="decimal"/>:
    /// <c>decimal.TryParse</c> succeeds on <c>1e-400</c> and <c>5e-324</c> by rounding
    /// both to zero, and on a 31-digit fraction by rounding it to 28 digits, so whether
    /// a literal "fits" a decimal cannot be asked of the parser without the very exact
    /// comparison it would stand in for; where a decimal is exact, both comparisons
    /// agree.
    /// </summary>
    private static bool SameDecimalValue(string left, string right) =>
        TryNormalizeDecimal(left, out var leftNegative, out var leftDigits, out var leftExponent)
        && TryNormalizeDecimal(right, out var rightNegative, out var rightDigits, out var rightExponent)
        && leftNegative == rightNegative
        && leftDigits.SequenceEqual(rightDigits)
        && leftExponent == rightExponent;

    /// <summary>
    /// The most exponent digits, after its leading zeros, that are parsed into a
    /// <see langword="long"/>: 18 digits stay below 10^18, so adding or subtracting a
    /// digit count (below 2^31, more than a .NET string can hold) cannot overflow.
    /// </summary>
    private const int MaxExponentDigits = 18;

    /// <summary>
    /// Splits a decimal literal (<c>-?digits(.digits)?([eE][+-]?digits)?</c>) into its
    /// value as <c>(-1)^negative × digits × 10^exponent</c>, with no leading or trailing
    /// zero in <paramref name="digits"/>, in time linear in the literal's length with
    /// small constants — it runs on the host after the container exited, where no
    /// ceiling bounds it, so an unbounded exponent is never parsed into a big integer.
    /// </summary>
    /// <remarks>
    /// The exact rule: zero is <c>(false, "", 0)</c> whatever its sign or exponent
    /// (<c>-0.0</c>, <c>0e999…</c> of any length). A non-zero mantissa whose exponent has
    /// more than <see cref="MaxExponentDigits"/> significant digits returns
    /// <see langword="false"/>: its magnitude's decimal exponent is at least
    /// 10^18 minus the literal's length (below 2^31), so it cannot equal any finite
    /// double's round-trip form, whose decimal exponent lies within ±400. Otherwise the
    /// exponent is a <see langword="long"/>, adjusted by the fraction length and the
    /// trailing zeros removed — every step within <see langword="long"/> range, and a
    /// long mantissa pulling a long exponent back into range (<c>1000…0e-999999</c>)
    /// normalises to the ordinary value it denotes.
    /// </remarks>
    private static bool TryNormalizeDecimal(string text, out bool negative, out ReadOnlySpan<char> digits, out long exponent)
    {
        negative = false;
        digits = default;
        exponent = 0;
        var span = text.AsSpan();

        if (span.Length > 0 && (span[0] == '-' || span[0] == '+'))
        {
            negative = span[0] == '-';
            span = span[1..];
        }

        var exponentAt = span.IndexOfAny('e', 'E');
        var mantissa = exponentAt < 0 ? span : span[..exponentAt];
        var exponentNegative = false;
        ReadOnlySpan<char> exponentDigits = [];
        if (exponentAt >= 0)
        {
            exponentDigits = span[(exponentAt + 1)..];
            if (exponentDigits.Length > 0 && (exponentDigits[0] == '-' || exponentDigits[0] == '+'))
            {
                exponentNegative = exponentDigits[0] == '-';
                exponentDigits = exponentDigits[1..];
            }

            if (exponentDigits.IsEmpty || exponentDigits.ContainsAnyExceptInRange('0', '9'))
            {
                return false;
            }

            exponentDigits = exponentDigits.TrimStart('0');
        }

        var pointAt = mantissa.IndexOf('.');
        var integerPart = pointAt < 0 ? mantissa : mantissa[..pointAt];
        ReadOnlySpan<char> fractionPart = pointAt < 0 ? [] : mantissa[(pointAt + 1)..];
        if (integerPart.IsEmpty || integerPart.ContainsAnyExceptInRange('0', '9') || fractionPart.ContainsAnyExceptInRange('0', '9'))
        {
            return false;
        }

        if (!integerPart.ContainsAnyExcept('0') && !fractionPart.ContainsAnyExcept('0'))
        {
            negative = false;
            return true;
        }

        if (exponentDigits.Length > MaxExponentDigits)
        {
            return false;
        }

        foreach (var digit in exponentDigits)
        {
            exponent = (exponent * 10) + (digit - '0');
        }

        if (exponentNegative)
        {
            exponent = -exponent;
        }

        var all = string.Concat(integerPart, fractionPart).AsSpan();
        exponent -= fractionPart.Length;

        var significant = all.TrimStart('0');
        var trimmed = significant.TrimEnd('0');
        exponent += significant.Length - trimmed.Length;
        digits = trimmed;
        return true;
    }
}
