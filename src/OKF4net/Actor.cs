// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net;

/// <summary>Which of the three §7 actor forms a value matches.</summary>
public enum ActorKind
{
    /// <summary><c>human:&lt;id&gt;</c> — a person (drives the human-reviewed trust tier, §5.3).</summary>
    Human,

    /// <summary><c>process:&lt;id&gt;</c> — an automated process.</summary>
    Process,

    /// <summary><c>&lt;producer&gt;/&lt;version&gt;</c> — an agent/tool, or any value not matching the two prefixes.</summary>
    Producer,
}

/// <summary>
/// A parsed §7 actor (<c>generated.by</c>, <c>verified[].by</c>, <c>sources[].author</c>).
/// Classification keys off the <c>human:</c>/<c>process:</c> prefixes; anything else is a
/// producer form. <see cref="IsWellFormed"/> is false when the value matches none of the three
/// exact forms — the validator warns on that (loading never rejects it).
/// </summary>
public readonly record struct Actor(string Raw, ActorKind Kind, string? Id, string? Producer, string? Version, bool IsWellFormed)
{
    /// <summary>True if this actor carries the <c>human:</c> prefix (regardless of well-formedness).</summary>
    public bool IsHuman => Kind == ActorKind.Human;

    /// <summary>Parses a raw actor string into its §7 form. Never throws.</summary>
    public static Actor Parse(string raw)
    {
        if (raw.StartsWith("human:", StringComparison.Ordinal))
        {
            var id = raw["human:".Length..];
            return new Actor(raw, ActorKind.Human, id.Length == 0 ? null : id, null, null, id.Length > 0);
        }

        if (raw.StartsWith("process:", StringComparison.Ordinal))
        {
            var id = raw["process:".Length..];
            return new Actor(raw, ActorKind.Process, id.Length == 0 ? null : id, null, null, id.Length > 0);
        }

        var slash = raw.IndexOf('/');
        if (slash > 0 && slash < raw.Length - 1)
        {
            return new Actor(raw, ActorKind.Producer, null, raw[..slash], raw[(slash + 1)..], true);
        }

        return new Actor(raw, ActorKind.Producer, null, null, null, false);
    }

    /// <summary>
    /// True when <paramref name="raw"/> carries a character that would break a
    /// line-oriented rendering of it: any C0/C1 control character
    /// (<see cref="char.IsControl(char)"/> — <c>\n</c> and <c>\r</c> among
    /// them, plus <c>ESC</c>, which forges appearance in a terminal), plus
    /// U+2028/U+2029, which <see cref="char.IsControl(char)"/> does not
    /// classify as control but which JavaScript-family line splitters treat as
    /// terminators.
    ///
    /// <b>This predicate is the whole defense, and it belongs on the WRITE
    /// path.</b> <see cref="BundleConceptWriter.RecordVerifications"/> — the
    /// single governed writer of the §5.2 <c>verified</c> field — refuses an
    /// actor it rejects, so no such value can be stored by <c>okf verify</c>
    /// or <c>okf_verify</c>; the CLI verb and the <c>okf_verify</c> tool call
    /// it again only to phrase a better message, never as a second line of
    /// defense. That is what lets both renderers stay simple: they interpolate
    /// <c>by</c> into a line with no escaping at all, which is safe precisely
    /// because a control-bearing actor never reaches them. One predicate, three
    /// call sites — a forked character test would let the three drift, exactly
    /// the failure mode <c>ConceptSearch</c> and <c>Internal/LfLines</c> exist
    /// to prevent.
    ///
    /// Two limits, stated plainly. <see cref="Parse"/> is deliberately NOT
    /// tightened: it is also the READ path (<c>Trust.DeriveTier</c>,
    /// <c>BundleValidator</c>), and an already-stored actor must keep parsing
    /// as it did. And <c>okf_write_concept</c> can still write a whole
    /// frontmatter, <c>verified</c> included, with no such check — deliberately
    /// unguarded (see the README). So a bundle can hold a control-bearing actor
    /// this gate never saw: any FUTURE feature that renders a stored actor owes
    /// its output its own escaping, and must not assume this predicate ran.
    /// </summary>
    /// <param name="raw">The raw actor string.</param>
    internal static bool ContainsControlCharacter(string raw)
    {
        foreach (var c in raw)
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
