// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net;
using OkfProducer.Core.CodeGraph;

namespace OkfProducer.Core.Generation;

/// <summary>
/// Derives the <c>code/&lt;language&gt;/&lt;container...&gt;/&lt;name&gt;</c> concept id path for an
/// extracted <see cref="SymbolFact"/> (§3.1).
/// </summary>
public static class CodeConceptIds
{
    /// <summary>
    /// Builds the concept id path for <paramref name="fact"/>: <c>code/&lt;language&gt;/</c> followed
    /// by <paramref name="profile"/>'s <see cref="LanguageProfile.SplitContainer"/> of
    /// <see cref="SymbolFact.Container"/> (each segment independently slugified with
    /// <see cref="ConceptId.Slugify"/>), then the symbol's own <see cref="SymbolFact.Name"/> (also
    /// slugified), joined with <c>/</c>. Each segment is first split at PascalCase/camelCase word
    /// boundaries (<see cref="SplitWordBoundaries"/>) -- <see cref="ConceptId.Slugify"/> case-folds
    /// and validates characters but never inserts a separator inside a run of letters, so
    /// <c>LinkScanner</c> would otherwise slugify to <c>linkscanner</c> rather than the readable
    /// <c>link-scanner</c> the id scheme wants.
    ///
    /// Deliberately ignores <see cref="SymbolFact.Signature"/>: two overloads of the same member
    /// share one container and name, so they collapse to the same id (§3.2) rather than being
    /// disambiguated by a numeric suffix that would be order-dependent and would renumber unrelated
    /// overloads whenever one was added or removed.
    /// </summary>
    public static string For(SymbolFact fact, LanguageProfile profile)
    {
        var segments = new List<string>(4) { "code", fact.Language };

        foreach (var part in profile.SplitContainer(fact.Container))
        {
            segments.Add(ConceptId.Slugify(SplitWordBoundaries(part)));
        }

        segments.Add(ConceptId.Slugify(SplitWordBoundaries(fact.Name)));

        return string.Join("/", segments);
    }

    /// <summary>
    /// Inserts a <c>-</c> at PascalCase/camelCase word boundaries -- before an uppercase letter that
    /// follows a lowercase letter or digit (<c>LinkScanner</c> -&gt; <c>Link-Scanner</c>), and before
    /// the last letter of an uppercase run that continues into a lowercase one (<c>HTTPServer</c> -&gt;
    /// <c>HTTP-Server</c>) -- so <see cref="ConceptId.Slugify"/>'s character-level normalization has
    /// word breaks to collapse onto <c>-</c> instead of running identifiers together.
    /// </summary>
    private static string SplitWordBoundaries(string input)
    {
        if (input.Length <= 1)
        {
            return input;
        }

        var builder = new System.Text.StringBuilder(input.Length + 4);
        builder.Append(input[0]);

        for (var i = 1; i < input.Length; i++)
        {
            var c = input[i];
            var prev = input[i - 1];

            var isNewWord =
                char.IsUpper(c) && (char.IsLower(prev) || char.IsDigit(prev))
                || (char.IsUpper(c) && char.IsUpper(prev) && i + 1 < input.Length && char.IsLower(input[i + 1]));

            if (isNewWord)
            {
                builder.Append('-');
            }

            builder.Append(c);
        }

        return builder.ToString();
    }
}

/// <summary>
/// One collision-free set of concept ids spanning every id family a generation run produces --
/// <c>overview</c>, <c>packages/*</c>, <c>docs/*</c>, and <c>code/*</c> alike -- so that, for example,
/// a package named the same as a doc still gets two distinct ids. A single <see cref="Register"/>
/// call handles one candidate id at a time; when several candidates could collide, callers must
/// register them in <see cref="StringComparer.Ordinal"/> order of their original (pre-slugify) name
/// so the numeric tie-break (§3.3) is stable across a file move or a line shift, not dependent on
/// scan order.
/// </summary>
public sealed class ConceptIdRegistry
{
    private readonly HashSet<string> _usedIds = new(StringComparer.Ordinal);

    /// <summary>
    /// Slugifies <paramref name="naturalName"/> with <see cref="ConceptId.Slugify"/>, then finds the
    /// first of <c>&lt;prefix&gt;/&lt;slug&gt;</c>, <c>&lt;prefix&gt;/&lt;slug&gt;-2</c>,
    /// <c>&lt;prefix&gt;/&lt;slug&gt;-3</c>, ... not yet registered and whose final segment is not
    /// reserved (<see cref="ConceptGenerator.IsReservedSegment"/> -- <c>index</c>/<c>log</c> would
    /// collide with the bundle's own <c>index.md</c>/<c>log.md</c>), registers it, and returns it.
    /// </summary>
    /// <exception cref="ConceptIdException">
    /// <paramref name="naturalName"/> normalizes to an empty slug (see <see cref="ConceptId.Slugify"/>).
    /// </exception>
    public ConceptId Register(string prefix, string naturalName)
    {
        var baseSlug = ConceptId.Slugify(naturalName);

        var segment = baseSlug;
        var suffix = 2;
        while (ConceptGenerator.IsReservedSegment(segment) || !_usedIds.Add($"{prefix}/{segment}"))
        {
            segment = $"{baseSlug}-{suffix}";
            suffix++;
        }

        return ConceptId.Parse($"{prefix}/{segment}");
    }
}
