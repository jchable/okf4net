// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;

namespace OKF4net.Yaml;

/// <summary>
/// Block-style YAML emitter for the OKF subset, defining this
/// implementation's quoting rules, escape sequences, indentation, and float
/// formatting.
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
    /// The nesting rule is the parser's, read from one place
    /// (<see cref="YamlParser.MaxNestingDepth"/>): a node's depth is the number
    /// of collections, empty ones included, from the root down to and
    /// including it; the root collection has depth 1; a value nested deeper
    /// than the limit is refused with a catchable <see cref="YamlEmitException"/>
    /// rather than written. So, as far as nesting goes, a value the parser reads
    /// this emitter writes, and a value this emitter writes the parser reads
    /// back. It is also this emitter's stack guard: a pathologically deep
    /// <see cref="YamlValue"/> tree, however it was constructed, is refused
    /// before it can overflow the stack. On a thread too small for a value
    /// within the rule, the same exception is raised when
    /// <see cref="RuntimeHelpers.TryEnsureSufficientExecutionStack"/> reports no
    /// stack left (<see cref="YamlParser.InsufficientStackMessage"/>).
    /// Reaching the refusal is errors-as-data on every caller's path — see
    /// <see cref="YamlEmitException"/>.
    /// </summary>
    private static void CheckDepth(int depth)
    {
        if (depth > YamlParser.MaxNestingDepth)
        {
            throw new YamlEmitException(YamlParser.NestingDepthExceededMessage);
        }

        if (!RuntimeHelpers.TryEnsureSufficientExecutionStack())
        {
            throw new YamlEmitException(YamlParser.InsufficientStackMessage);
        }
    }

    /// <summary>
    /// Counts <paramref name="value"/> at <paramref name="depth"/> when it is
    /// an EMPTY collection, which is written inline (<c>[]</c>, <c>{}</c>)
    /// without recursing but is a collection all the same.
    /// </summary>
    private static void CheckInlineCollection(YamlValue value, int depth)
    {
        if (value is YamlMapping or YamlSequence)
        {
            CheckDepth(depth);
        }
    }

    /// <summary>
    /// The sharing rule: how many characters, at most, one emit writes for
    /// values it has already written (16 MiB). A <see cref="YamlValue"/> built
    /// in code can hold one object in several places: a collection
    /// (<see cref="YamlMapping"/> is mutable; an immutable
    /// <see cref="YamlSequence"/> is meant to be shared), or a string — the
    /// same <see cref="YamlString"/>, or several sharing one .NET string, as
    /// values or as mapping keys. The OKF YAML subset has no anchors or
    /// aliases, so the only encoding of such a reuse is to write it again in
    /// full, and that is what this emitter does — re-parsing the text gives an
    /// equal value, since equality is structural. But reuse can multiply the
    /// output without bound: a chain of mappings each holding the next one
    /// under two keys doubles it per level (60 levels, 60 objects, 2^60
    /// leaves), one collection held by many slots multiplies it by their count,
    /// and so does one long string (a 1 000 000-character string held 3 000
    /// times: about 2 MB of value, 3 billion characters of text).
    ///
    /// So the emitter tracks, by reference, the non-empty collections and the
    /// .NET strings of at least <see cref="LongStringChars"/> characters it has
    /// written during the current emit. It counts the characters written while
    /// re-writing one of those collections (a reuse inside a reuse counted
    /// once) and every line holding one of those strings again outside such a
    /// collection, and refuses with a catchable <see cref="YamlEmitException"/>
    /// (<see cref="SharedOutputExceededMessage"/>) before writing the line that
    /// would take the count past this limit — never after. The limit is on the
    /// characters reuse writes, not on how fast the text grows: reuse that
    /// grows it only linearly is refused too once it writes that much (3 000 000
    /// slots alternating between two one-item sequences, about 24 MB of text, is
    /// refused, while the same value built from distinct sequences is written).
    ///
    /// What the rule does not count stays small: the first write of anything,
    /// and strings shorter than <see cref="LongStringChars"/>, which cost at
    /// most that much (plus indentation) per slot the value itself holds.
    /// Values the parser returns share nothing, so the rule never counts
    /// anything for them; reuse that stays small (one list under two keys) is
    /// written as before. A limit, rather than a refusal of every reuse, keeps
    /// that ordinary case working. Whatever the rule lets through, the text is
    /// still bounded by <see cref="MaxOutputChars"/>. A collection that holds
    /// itself is refused by the nesting rule (<see cref="CheckDepth"/>), which
    /// it reaches first. Reaching the refusal is errors-as-data on every
    /// caller's path, like the nesting refusal — see <see cref="YamlEmitException"/>.
    /// </summary>
    internal const int MaxRepeatedOutputChars = 16 * 1024 * 1024;

    /// <summary>
    /// The length from which a string written again counts for the sharing rule
    /// (<see cref="MaxRepeatedOutputChars"/>): 1 KiB. Shorter strings are reused
    /// all the time and legitimately — interned literals, one producer name in
    /// every stamp — and re-writing one costs at most this much output per slot
    /// the value allocated, so leaving them out keeps ordinary emits free of
    /// bookkeeping (no set lookup per scalar) at a bounded price, which
    /// <see cref="MaxOutputChars"/> caps in total. The price is also time: each
    /// slot is rendered anew, and a string that may be written plain is
    /// re-checked by the parser each time, so a value made of one short string
    /// held by millions of slots costs CPU in proportion to its text until the
    /// output rule refuses it — seconds, not a hang.
    /// </summary>
    internal const int LongStringChars = 1024;

    /// <summary>
    /// The output rule: the longest text one emit writes, in characters
    /// (256 MiB, 512 MB of UTF-16). Any value whose text would be longer is
    /// refused with a catchable <see cref="YamlEmitException"/>
    /// (<see cref="OutputSizeExceededMessage"/>) before the line that would pass
    /// it is written; a string longer than the limit is refused before any work
    /// on it, and quoting stops once past it. So the text can never outgrow the
    /// buffer (no <see cref="ArgumentOutOfRangeException"/> from it, which no
    /// caller's errors-as-data path catches), and memory is bounded by a small
    /// multiple of the limit, not by the shape of the value: the buffer and the
    /// final string, each at most the limit, plus, while one line is written, a
    /// few copies of its longest string (its quoted form, or the parser's check
    /// of one that may be plain) — a few GB at the very worst. That is a bound,
    /// not a guarantee the process has it: on a smaller heap, a value near the
    /// limit can still end in an <see cref="OutOfMemoryException"/>. The limit
    /// is a quarter of .NET's longest string, and orders of magnitude above any
    /// frontmatter: a value that large is not a legitimate concept header. It
    /// is the one place where a value the parser reads may be refused by the
    /// emitter, and only past a document of comparable size.
    /// </summary>
    internal const int MaxOutputChars = 256 * 1024 * 1024;

    /// <summary>The message of the sharing refusal, stating its constants.</summary>
    internal const string SharedOutputExceededMessage =
        "shared value limit exceeded: the OKF YAML subset has no anchors or aliases, so every reuse of a collection "
        + "or of a string of 1024 characters or more is written in full, and the reuses would write more than 16777216 characters";

    /// <summary>The message of the output refusal, stating its constant.</summary>
    internal const string OutputSizeExceededMessage =
        "output size limit exceeded: the text would be longer than 268435456 characters";

    /// <summary>
    /// One emit's output and its bookkeeping for the sharing rule
    /// (<see cref="MaxRepeatedOutputChars"/>) and the output rule
    /// (<see cref="MaxOutputChars"/>). Every line goes through
    /// <see cref="WriteLine"/>, which checks both before appending.
    /// </summary>
    private sealed class EmitState(int maxRepeatedChars, int maxOutputChars)
    {
        /// <summary>
        /// The non-empty collections and long strings written so far, by
        /// reference: equality is structural, sharing is not. Created on first
        /// use, so a scalar emit allocates nothing for it.
        /// </summary>
        private HashSet<object>? _written;

        /// <summary>How many collections being re-written enclose the current one (0 outside any).</summary>
        private int _repeatDepth;

        /// <summary>The output length when the outermost re-written collection was entered.</summary>
        private int _repeatStart;

        /// <summary>Characters counted so far, outside the re-written collection being written, if any.</summary>
        private long _repeatedChars;

        public StringBuilder Output { get; } = new();

        /// <summary>The output limit this emit enforces (<see cref="YamlEmitter.MaxOutputChars"/> in production).</summary>
        public int OutputLimit => maxOutputChars;

        /// <summary>Records <paramref name="collection"/> as entered; returns whether it was written before.</summary>
        public bool Enter(YamlValue collection)
        {
            _written ??= new HashSet<object>(ReferenceEqualityComparer.Instance);
            if (_written.Add(collection))
            {
                return false;
            }

            if (_repeatDepth++ == 0)
            {
                _repeatStart = Output.Length;
            }

            return true;
        }

        /// <summary>Records leaving a collection <see cref="Enter"/> answered <paramref name="repeated"/> for.</summary>
        public void Leave(bool repeated)
        {
            if (repeated && --_repeatDepth == 0)
            {
                _repeatedChars += Output.Length - _repeatStart;
            }
        }

        /// <summary>
        /// Whether <paramref name="value"/> is a string of at least
        /// <see cref="LongStringChars"/> characters whose .NET string this emit
        /// already wrote; records it otherwise.
        /// </summary>
        public bool IsRepeatedLongString(YamlValue value)
        {
            if (value is not YamlString { Value: { Length: >= LongStringChars } s })
            {
                return false;
            }

            _written ??= new HashSet<object>(ReferenceEqualityComparer.Instance);
            return !_written.Add(s);
        }

        /// <summary>
        /// Writes <c>pad key marker[ scalar]\n</c> (a header line when
        /// <paramref name="scalar"/> is null) after checking that it passes
        /// neither limit. The line counts for the sharing rule when it is
        /// inside a re-written collection, or holds a repeated long string
        /// (<paramref name="repeatedLongString"/>).
        /// </summary>
        public void WriteLine(string pad, string? key, char marker, string? scalar, bool repeatedLongString)
        {
            var length = (long)pad.Length + (key?.Length ?? 0) + 2 + (scalar is null ? 0 : 1 + scalar.Length);
            if (Output.Length + length > maxOutputChars)
            {
                throw new YamlEmitException(OutputSizeExceededMessage);
            }

            if (_repeatDepth > 0 || repeatedLongString)
            {
                var inCollection = _repeatDepth > 0 ? Output.Length - _repeatStart : 0;
                if (_repeatedChars + inCollection + length > maxRepeatedChars)
                {
                    throw new YamlEmitException(SharedOutputExceededMessage);
                }

                if (_repeatDepth == 0)
                {
                    _repeatedChars += length;
                }
            }

            Output.Append(pad).Append(key).Append(marker);
            if (scalar is not null)
            {
                Output.Append(' ').Append(scalar);
            }

            Output.Append('\n');
        }
    }

    /// <summary>
    /// Emits a value as YAML text (always ends with "\n").
    /// </summary>
    /// <exception cref="YamlEmitException">
    /// <paramref name="value"/> nests collections deeper than the parser's
    /// limit (<see cref="CheckDepth"/>), reuses collections or long strings so
    /// much that writing each reuse in full passes
    /// <see cref="MaxRepeatedOutputChars"/>, or would be written as a text
    /// longer than <see cref="MaxOutputChars"/>.
    /// </exception>
    public static string Emit(YamlValue value) => EmitWithLimits(value, MaxRepeatedOutputChars, MaxOutputChars);

    /// <summary>
    /// <see cref="Emit(YamlValue)"/> with the two limits as parameters, so tests
    /// can reach them with small values. The refusal messages still state the
    /// production constants.
    /// </summary>
    internal static string EmitWithLimits(YamlValue value, int maxRepeatedChars, int maxOutputChars)
    {
        var state = new EmitState(maxRepeatedChars, maxOutputChars);
        switch (value)
        {
            case YamlMapping m when !m.IsEmpty:
                EmitMapping(m, 0, 1, state);
                break;
            case YamlSequence s when s.Items.Count > 0:
                EmitSequence(s, 0, 1, state);
                break;
            default:
                CheckInlineCollection(value, 1);
                var text = EmitScalar(value, maxOutputChars);
                if ((long)text.Length + 1 > maxOutputChars)
                {
                    throw new YamlEmitException(OutputSizeExceededMessage);
                }

                state.Output.Append(text).Append('\n');
                break;
        }

        return state.Output.ToString();
    }

    /// <summary>Emits a non-empty mapping, at depth <paramref name="depth"/>, in block style.</summary>
    private static void EmitMapping(YamlMapping map, int indent, int depth, EmitState state)
    {
        CheckDepth(depth);
        var repeated = state.Enter(map);

        var pad = new string(' ', indent);
        foreach (var (key, value) in map.Entries)
        {
            var keyText = EmitKey(key, state.OutputLimit);
            var repeatedKey = state.IsRepeatedLongString(key);
            switch (value)
            {
                case YamlMapping m when !m.IsEmpty:
                    state.WriteLine(pad, keyText, ':', null, repeatedKey);
                    EmitMapping(m, indent + IndentStep, depth + 1, state);
                    break;
                case YamlSequence s when s.Items.Count > 0:
                    state.WriteLine(pad, keyText, ':', null, repeatedKey);
                    EmitSequence(s, indent + IndentStep, depth + 1, state);
                    break;
                default:
                    CheckInlineCollection(value, depth + 1);
                    var repeatedValue = state.IsRepeatedLongString(value);
                    state.WriteLine(pad, keyText, ':', EmitScalar(value, state.OutputLimit), repeatedKey || repeatedValue);
                    break;
            }
        }

        state.Leave(repeated);
    }

    /// <summary>Emits a non-empty sequence, at depth <paramref name="depth"/>, in block style.</summary>
    private static void EmitSequence(YamlSequence seq, int indent, int depth, EmitState state)
    {
        CheckDepth(depth);
        var repeated = state.Enter(seq);

        var pad = new string(' ', indent);
        foreach (var item in seq.Items)
        {
            switch (item)
            {
                case YamlMapping m when !m.IsEmpty:
                    state.WriteLine(pad, null, '-', null, false);
                    EmitMapping(m, indent + IndentStep, depth + 1, state);
                    break;
                case YamlSequence s when s.Items.Count > 0:
                    state.WriteLine(pad, null, '-', null, false);
                    EmitSequence(s, indent + IndentStep, depth + 1, state);
                    break;
                default:
                    CheckInlineCollection(item, depth + 1);
                    state.WriteLine(pad, null, '-', EmitScalar(item, state.OutputLimit), state.IsRepeatedLongString(item));
                    break;
            }
        }

        state.Leave(repeated);
    }

    /// <summary>
    /// Emits a scalar (or an empty collection) inline. Non-empty collections
    /// never reach here in block context; as a defensive fallback both render
    /// as "[]" (not "{}" for a non-empty mapping) if they somehow do. A string
    /// is refused rather than rendered past <paramref name="maxOutputChars"/>.
    /// </summary>
    private static string EmitScalar(YamlValue value, int maxOutputChars) => value switch
    {
        YamlNull => "null",
        YamlBool { Value: true } => "true",
        YamlBool { Value: false } => "false",
        YamlInt i => i.Value.ToString(CultureInfo.InvariantCulture),
        YamlFloat f => FormatFloat(f.Value),
        YamlString s => EmitString(s.Value, maxOutputChars),
        YamlSequence { Items.Count: 0 } => "[]",
        YamlMapping { IsEmpty: true } => "{}",
        YamlSequence or YamlMapping => "[]",
        _ => throw new InvalidOperationException($"unreachable YamlValue kind: {value.GetType()}"),
    };

    /// <summary>
    /// Emits a float scalar: special tokens for non-finite values, otherwise
    /// the shortest round-tripping representation (see <see cref="DebugFormat"/>)
    /// with a "." guaranteed to be present so the value re-parses as a float
    /// rather than a string.
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
    /// Renders the shortest round-tripping decimal digit sequence in plain
    /// decimal notation with a forced fractional digit (e.g. <c>1.0</c>) when
    /// the magnitude is in the "general format" range
    /// (<c>1e-4 &lt;= |f| &lt; 1e16</c>, or exactly zero), and in lowercase
    /// exponential notation with no forced fractional digit (e.g. <c>1e30</c>)
    /// otherwise. Used for the float <c>Debug</c>-style rendering this emitter
    /// targets.
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
    /// The plain <c>Display</c>-style rendering used by
    /// <see cref="YamlValue.AsDisplayString"/>: like <see cref="DebugFormat"/>
    /// but always plain decimal notation (never scientific, regardless of
    /// magnitude) and no forced fractional digit (e.g. <c>3</c>, not <c>3.0</c>).
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
    /// decimal vs. scientific notation, and exponent padding, differ from the
    /// format this emitter targets.
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

    /// <summary>Emits a string as a plain scalar when safe, otherwise double-quoted.</summary>
    private static string EmitString(string s, int maxOutputChars)
    {
        CheckStringLength(s, maxOutputChars);
        return IsSafePlain(s) ? s : DoubleQuote(s, maxOutputChars);
    }

    /// <summary>
    /// Refuses, before any work on it, a string longer than the whole text may
    /// be (<paramref name="maxOutputChars"/>, <see cref="MaxOutputChars"/> in
    /// production): it could never be written, and quoting it could take more
    /// memory than the text itself.
    /// </summary>
    private static void CheckStringLength(string s, int maxOutputChars)
    {
        if (s.Length > maxOutputChars)
        {
            throw new YamlEmitException(OutputSizeExceededMessage);
        }
    }

    /// <summary>
    /// Emits a mapping key. A non-string key, and a string key that
    /// <see cref="IsSafePlain"/> rejects, come out exactly as the same value would.
    /// A string key that <see cref="IsSafePlain"/> accepts is still written
    /// double-quoted, with the same escapes as a quoted value, when the parser
    /// would not split the line <c>key: value</c> at that key's own colon
    /// (<see cref="YamlParser.SplitsAtKeyEnd"/>). The parser's own split decides,
    /// so the rule cannot drift from it. For a string <see cref="IsSafePlain"/>
    /// accepts, that happens in exactly two cases, each one a state of the
    /// split's left-to-right scan in which a <c>:</c> does not end the key:
    /// <list type="bullet">
    /// <item>a quote left open: a <c>'</c> or <c>"</c> with no closing quote later
    /// in the key. The scan reads text after a quote as a quoted scalar, anywhere
    /// in the line; inside <c>'</c> a doubled <c>''</c> does not close it, and
    /// inside <c>"</c> a <c>\</c> skips the next character.</item>
    /// <item>a flow level left open: outside quotes, a <c>[</c> or <c>{</c> not
    /// balanced by a later <c>]</c> or <c>}</c>. The scan counts <c>[</c>/<c>{</c>
    /// up and <c>]</c>/<c>}</c> down (either closer closes either opener; one with
    /// no level open is ignored), and splits only at level zero.</item>
    /// </list>
    /// The split's other outcomes cannot happen to such a key: <see cref="IsSafePlain"/>
    /// already rejects a <c>:</c> followed by a space or ending the string (an
    /// earlier split), a <c>#</c> after a space and any tab (a comment, which
    /// ends the scan), and leading or trailing whitespace (which the split trims
    /// off the key). Keys whose quotes and brackets all close stay plain
    /// (<c>a[b]</c>, <c>a"b"c</c>), as do ordinary keys.
    /// </summary>
    private static string EmitKey(YamlValue key, int maxOutputChars)
    {
        if (key is not YamlString { Value: var s })
        {
            return EmitScalar(key, maxOutputChars);
        }

        // One IsSafePlain call: a key it rejects is quoted like a value, one it
        // accepts stays plain unless the split would not end at its colon.
        CheckStringLength(s, maxOutputChars);
        return IsSafePlain(s) && YamlParser.SplitsAtKeyEnd(s) ? s : DoubleQuote(s, maxOutputChars);
    }

    private static readonly char[] Indicators =
    [
        '-', '?', ':', ',', '[', ']', '{', '}', '#', '&', '*', '!', '|', '>', '\'', '"', '%', '@', '`', ' ',
    ];

    /// <summary>
    /// Whether a string can be emitted as a plain (unquoted) scalar without
    /// being misread on re-parse. The conditions are all required, so their
    /// order changes no answer; the character checks come first so that the
    /// parser, last, only ever reads one line that cannot open a structure.
    /// Handing it the whole string first cost about 23 times the string's size
    /// for a multi-line one (the parser keeps an entry per line and copies
    /// them), enough to run a legal value out of memory.
    /// </summary>
    private static bool IsSafePlain(string s)
    {
        if (s.Length == 0)
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

        // Must not be reinterpreted as null/bool/number.
        try
        {
            return YamlValue.Parse(s).Equals(new YamlString(s));
        }
        catch (YamlParseException)
        {
            // An odd string the parser refuses: fall through to quoting.
            return false;
        }
    }

    /// <summary>
    /// Renders a string as a double-quoted scalar with C-style escapes. An
    /// escape is up to six characters, so the quoted text is refused as soon
    /// as it passes <paramref name="maxOutputChars"/> rather than grown past it.
    /// </summary>
    private static string DoubleQuote(string s, int maxOutputChars)
    {
        var outSb = new StringBuilder(Math.Min(s.Length, maxOutputChars) + 2);
        outSb.Append('"');
        foreach (var c in s)
        {
            if (outSb.Length > maxOutputChars)
            {
                throw new YamlEmitException(OutputSizeExceededMessage);
            }

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
