// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net.Internal;

/// <summary>
/// The single test for "would this value break a line-oriented rendering of
/// itself" — the shared guard behind every caller-supplied string this library
/// echoes into stdout, stderr or a tool result.
/// </summary>
internal static class LineSafeText
{
    /// <summary>
    /// True when <paramref name="value"/> carries a character that would break
    /// a line-oriented rendering of it: any C0/C1 control character
    /// (<see cref="char.IsControl(char)"/> — <c>\n</c> and <c>\r</c> among
    /// them, plus <c>ESC</c>, which forges appearance in a terminal), plus
    /// U+2028/U+2029, which <see cref="char.IsControl(char)"/> does not
    /// classify as control but which JavaScript-family line splitters treat as
    /// terminators.
    ///
    /// <b>One predicate, every call site.</b> Two distinct values reach it, for
    /// two different reasons, and both were real injections before it existed:
    ///
    /// <list type="bullet">
    /// <item>the §7 actor of a verification, which is WRITTEN — refused by
    /// <see cref="BundleConceptWriter.RecordVerifications"/>, the governed
    /// writer, so no such value can be stored; the CLI verb and
    /// <c>okf_verify</c> re-run it only to phrase a better message.</item>
    /// <item>the <c>at</c> timestamp, which is never written when malformed but
    /// IS echoed back in the rejection message — so a strict parse alone did
    /// not close it. The value is refused before it is quoted.</item>
    /// </list>
    ///
    /// That is what lets the renderers stay simple: they interpolate these
    /// values into a line with no escaping at all, which is safe precisely
    /// because a control-bearing one never reaches them. A forked character
    /// test would let the call sites drift — the failure mode
    /// <see cref="LfLines"/> and <c>ConceptSearch</c> exist to prevent — and it
    /// is how the <c>at</c> path stayed open after the actor path was closed.
    ///
    /// Two limits, stated plainly. <see cref="Actor.Parse"/> is deliberately
    /// NOT tightened by this: it is also the READ path
    /// (<c>Trust.DeriveTier</c>, <c>BundleValidator</c>), and an already-stored
    /// actor must keep parsing as it did. And <c>okf_write_concept</c> can
    /// still write a whole frontmatter, <c>verified</c> included, with no such
    /// check — deliberately unguarded (see the README). So a bundle can hold a
    /// control-bearing actor this gate never saw: any FUTURE feature that
    /// renders a stored value owes its output its own escaping, and must not
    /// assume this predicate ran.
    /// </summary>
    /// <param name="value">The caller-supplied string about to be stored or echoed.</param>
    internal static bool ContainsControlCharacter(string value)
    {
        foreach (var c in value)
        {
            // The two separators are written as numeric constants on purpose:
            // a literal U+2028 in source is invisible in every editor and diff
            // that would have to review this line.
            if (char.IsControl(c) || c is (char)0x2028 or (char)0x2029)
            {
                return true;
            }
        }

        return false;
    }
}
