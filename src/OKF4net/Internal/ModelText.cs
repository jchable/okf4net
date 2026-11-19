// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Text;

namespace OKF4net.Internal;

/// <summary>
/// The class <see cref="ModelText.Classify"/> puts a character in: what
/// <see cref="ModelText.Fold"/> does with it.
/// </summary>
internal enum ModelTextCharClass
{
    /// <summary>An ordinary character, left as is.</summary>
    Ordinary,

    /// <summary>A line terminator (CR, LF, NEL, VT, FF, U+2028, U+2029): folded to one space.</summary>
    LineTerminator,

    /// <summary>Any other C0/C1 control (ESC, NUL, DEL, …), TAB excepted: replaced by U+FFFD.</summary>
    Control,

    /// <summary>A bidirectional control (U+061C, U+200E, U+200F, U+202A–U+202E, U+2066–U+2069): removed.</summary>
    Bidi,

    /// <summary>
    /// An invisible format character a model reads and a reviewer does not see (#249): U+200B, U+2060–U+2064,
    /// U+FEFF, U+FFF9–U+FFFB, and the tag characters U+E0000–U+E007F (a surrogate pair, classified by
    /// <see cref="ModelText.IsTagPair"/>): removed.
    /// </summary>
    Invisible,
}

/// <summary>
/// The single definition of "safe inside one line of model-facing text" (#122,
/// design <c>docs/superpowers/specs/2026-11-16-model-facing-output-boundary-design.md</c>
/// §2.1): every value an agent tool or the context provider interpolates into
/// a line goes through <see cref="Fold"/>, so it occupies exactly the line it
/// was given and cannot forge a second one, a heading or a bullet.
///
/// <para>The rule, by character class (<see cref="Classify"/>):</para>
/// <list type="bullet">
/// <item>every line terminator becomes one space: CR, LF, CRLF (one space for
/// the pair), NEL (U+0085), VT, FF, U+2028 and U+2029 — the set a markdown or
/// JavaScript splitter downstream may break a line on;</item>
/// <item>every other C0/C1 control (ESC, NUL, DEL, …) becomes U+FFFD, visible
/// and meaningless (arbitration Q1);</item>
/// <item>the twelve bidirectional controls are removed: each reorders what a
/// reader is shown without changing the bytes;</item>
/// <item>the invisible format characters a model reads and a reviewer does not
/// see are removed (#249): the tag characters U+E0000–U+E007F, which mirror
/// ASCII invisibly and can carry a hidden instruction, the zero-width space
/// U+200B, the word joiner and invisible operators U+2060–U+2064, U+FEFF inside
/// a value, and the interlinear annotation characters U+FFF9–U+FFFB. ZWNJ and
/// ZWJ (U+200C, U+200D) are kept: emoji sequences and scripts such as Persian
/// and the Indic ones need them;</item>
/// <item>everything else is left as is — accents, right-to-left letters,
/// supplementary-plane scalars (a surrogate pair is never split) — so an
/// ordinary value prints unchanged.</item>
/// </list>
///
/// <para><b>TAB is ordinary.</b> It is whitespace inside a line, never a
/// terminator; the <c>log.md</c> write guard accepts it verbatim in an entry
/// (<see cref="BundleConceptWriter.GuardLogField"/>), and the read side has
/// always shown it as written. <see cref="LineSafeText.ContainsControlCharacter"/>
/// still counts it, because it is a refusal predicate with callers of its own
/// (a §7 actor never carries a tab); that predicate is a superset of what
/// <see cref="NeedsFold"/> flags, so a value it accepts is one
/// <see cref="Fold"/> leaves unchanged.</para>
///
/// <para>Character constants are numeric on purpose: every one of these is
/// invisible in source, in a diff and in a review.</para>
/// </summary>
internal static class ModelText
{
    /// <summary>The character a stray control becomes: U+FFFD REPLACEMENT CHARACTER.</summary>
    internal const char Replacement = (char)0xFFFD;

    /// <summary>The class of <paramref name="c"/>: what <see cref="Fold"/> does with it.</summary>
    /// <param name="c">The UTF-16 code unit to classify (a surrogate is always ordinary).</param>
    internal static ModelTextCharClass Classify(char c)
    {
        if (c is '\r' or '\n' or (char)0x000B or (char)0x000C or (char)0x0085 or (char)0x2028 or (char)0x2029)
        {
            return ModelTextCharClass.LineTerminator;
        }

        if (c == '\t')
        {
            return ModelTextCharClass.Ordinary;
        }

        if (char.IsControl(c))
        {
            return ModelTextCharClass.Control;
        }

        if (IsBidiControl(c))
        {
            return ModelTextCharClass.Bidi;
        }

        return c is (char)0x200B or (char)0x2060 or (char)0x2061 or (char)0x2062 or (char)0x2063 or (char)0x2064
            or (char)0xFEFF or (char)0xFFF9 or (char)0xFFFA or (char)0xFFFB
            ? ModelTextCharClass.Invisible
            : ModelTextCharClass.Ordinary;
    }

    /// <summary>
    /// Whether <paramref name="value"/> holds, at <paramref name="i"/>, the surrogate pair of a tag character
    /// U+E0000–U+E007F (high surrogate U+DB40, low surrogate U+DC00–U+DC7F): a character <see cref="Classify"/>,
    /// which sees one UTF-16 unit at a time, cannot recognise.
    /// </summary>
    /// <param name="value">The text.</param>
    /// <param name="i">The index of a candidate high surrogate.</param>
    internal static bool IsTagPair(string value, int i) =>
        value[i] == (char)0xDB40 && i + 1 < value.Length && value[i + 1] is >= (char)0xDC00 and <= (char)0xDC7F;

    /// <summary>
    /// The twelve Unicode bidirectional CONTROL characters: the marks U+061C
    /// (ALM), U+200E (LRM) and U+200F (RLM); the embeddings and overrides
    /// U+202A–U+202E; the isolates U+2066–U+2069. The one list of them in this
    /// repository: <see cref="BundleConceptWriter.IsBidiControl"/> reads it.
    /// Ordinary right-to-left TEXT carries its own strong direction and needs
    /// none of them.
    /// </summary>
    /// <param name="c">The character to classify.</param>
    internal static bool IsBidiControl(char c) =>
        c is (char)0x061C or (char)0x200E or (char)0x200F
            or (char)0x202A or (char)0x202B or (char)0x202C or (char)0x202D or (char)0x202E
            or (char)0x2066 or (char)0x2067 or (char)0x2068 or (char)0x2069;

    /// <summary>True when <see cref="Fold"/> would change <paramref name="value"/>.</summary>
    /// <param name="value">The value about to be interpolated into one line.</param>
    internal static bool NeedsFold(string value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            if (Classify(value[i]) != ModelTextCharClass.Ordinary || IsTagPair(value, i))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// <paramref name="value"/>, made safe to sit inside one line of
    /// model-facing text: see the type's remarks for the rule (a tag character
    /// the removals would assemble from two lone halves is removed too, so the
    /// output never holds one). Idempotent, and
    /// the identity on any value <see cref="NeedsFold"/> does not flag (the
    /// same instance is returned).
    /// </summary>
    /// <param name="value">Any value about to be interpolated into one line.</param>
    internal static string Fold(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!NeedsFold(value))
        {
            return value;
        }

        var sb = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (IsTagPair(value, i))
            {
                i++; // the whole tag character, both halves of its pair, is removed
                continue;
            }

            switch (Classify(c))
            {
                case ModelTextCharClass.LineTerminator:
                    // CRLF is one line ending, so one space, as ReplaceLineEndings has it.
                    if (c == '\r' && i + 1 < value.Length && value[i + 1] == '\n')
                    {
                        i++;
                    }

                    sb.Append(' ');
                    break;
                case ModelTextCharClass.Control:
                    sb.Append(Replacement);
                    break;
                case ModelTextCharClass.Bidi:
                case ModelTextCharClass.Invisible:
                    break;
                case ModelTextCharClass.Ordinary when c is >= (char)0xDC00 and <= (char)0xDC7F && sb.Length > 0 && sb[^1] == (char)0xDB40:
                    // A lone tag high surrogate that a removal just put next to a tag low
                    // surrogate (#251 review): joined, they would be a tag character in the
                    // output. Both halves are removed, so the output never holds one.
                    sb.Length--;
                    break;
                default:
                    sb.Append(c);
                    break;
            }
        }

        return sb.ToString();
    }
}
