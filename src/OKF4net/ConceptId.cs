// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using OKF4net.Internal;

namespace OKF4net;

/// <summary>
/// A concept identifier: an ordered list of path segments (e.g.
/// <c>["tables", "users"]</c> for <c>tables/users</c>) — the path of a
/// concept's file within the bundle with the <c>.md</c> suffix removed
/// (§2), including its segment validation rule.
/// </summary>
public sealed class ConceptId : IEquatable<ConceptId>, IComparable<ConceptId>, IComparable
{
    /// <summary>The id's segments, in order.</summary>
    public IReadOnlyList<string> Segments { get; }

    /// <summary>The final segment (the concept's own name, without directories).</summary>
    public string Name => Segments.Count > 0 ? Segments[^1] : string.Empty;

    /// <summary>The id of the directory that contains this concept, if any.</summary>
    public ConceptId? Parent =>
        Segments.Count <= 1 ? null : new ConceptId(Segments.Take(Segments.Count - 1).ToList());

    /// <summary>
    /// Wraps <paramref name="segments"/> in a <see cref="ReadOnlyCollection{T}"/>
    /// directly (no further copy) -- every call site (<see cref="New"/>,
    /// <see cref="Parse"/>, <see cref="Parent"/>) already builds and owns a
    /// fresh <see cref="List{T}"/> before reaching this constructor, so this
    /// wrapper is the single allocation guarding <see cref="Segments"/>
    /// against downcast mutation (e.g. <c>(List&lt;string&gt;)id.Segments</c>)
    /// that could desync a <see cref="ConceptId"/> used as a dictionary key.
    /// </summary>
    private ConceptId(List<string> segments)
    {
        Segments = new ReadOnlyCollection<string>(segments);
    }

    /// <summary>
    /// Builds a concept id from segments, validating each. Unlike
    /// <see cref="Parse"/>,
    /// this does NOT drop empty strings from <paramref name="segments"/> —
    /// an empty segment is passed straight to <see cref="ValidateSegment"/>
    /// and rejected there.
    /// </summary>
    /// <exception cref="ConceptIdException">
    /// The list is empty, or a segment fails <see cref="ValidateSegment"/>.
    /// </exception>
    public static ConceptId New(IReadOnlyList<string> segments)
    {
        if (segments.Count == 0)
        {
            throw new ConceptIdException("concept_id must have at least one segment");
        }

        foreach (var seg in segments)
        {
            ValidateSegment(seg);
        }

        return new ConceptId(segments.ToList());
    }

    /// <summary>
    /// Parses a concept id from a <c>/</c>-separated string. Empty segments
    /// are dropped (so leading/trailing/duplicate slashes are tolerated).
    /// </summary>
    /// <exception cref="ConceptIdException">
    /// <paramref name="s"/> has no non-empty segments, or a segment fails
    /// <see cref="ValidateSegment"/>.
    /// </exception>
    public static ConceptId Parse(string s)
    {
        var segments = s.Split('/').Where(p => p.Length > 0).ToList();
        if (segments.Count == 0)
        {
            throw new ConceptIdException($"Empty concept id: {DebugQuote.Quote(s)}");
        }

        foreach (var seg in segments)
        {
            ValidateSegment(seg);
        }

        return new ConceptId(segments);
    }

    /// <summary>Like <see cref="Parse"/>, but returns <c>false</c> instead of throwing.</summary>
    public static bool TryParse(string s, [NotNullWhen(true)] out ConceptId? id)
    {
        try
        {
            id = Parse(s);
            return true;
        }
        catch (ConceptIdException)
        {
            id = null;
            return false;
        }
    }

    /// <summary>
    /// Derives a concept id from a file path relative to
    /// <paramref name="bundleRoot"/>, stripping the <c>.md</c> suffix.
    /// Both inputs are normalized by replacing <c>\</c> with <c>/</c> so
    /// Windows-style paths work regardless of the host OS.
    /// </summary>
    /// <exception cref="ConceptIdException">
    /// <paramref name="path"/> is not under <paramref name="bundleRoot"/>,
    /// or the resulting segments are empty or invalid (see
    /// <see cref="New"/> / <see cref="ValidateSegment"/>).
    /// </exception>
    public static ConceptId FromPath(string bundleRoot, string path)
    {
        var normalizedRoot = bundleRoot.Replace('\\', '/').TrimEnd('/');
        var normalizedPath = path.Replace('\\', '/');

        string rel;
        if (normalizedPath == normalizedRoot)
        {
            rel = string.Empty;
        }
        else if (normalizedPath.StartsWith(normalizedRoot + "/", StringComparison.Ordinal))
        {
            rel = normalizedPath.Substring(normalizedRoot.Length + 1);
        }
        else
        {
            throw new ConceptIdException(
                $"{DebugQuote.Quote(path)} is not under bundle root {DebugQuote.Quote(bundleRoot)}");
        }

        // Non-leading "." segments are normalized away (filtered out here).
        // ".." is deliberately NOT filtered: it is left literal and still
        // fails ValidateSegment, so a traversal-style segment is rejected.
        var segments = rel.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Where(s => s != ".")
            .ToList();
        if (segments.Count > 0)
        {
            var last = segments[^1];
            if (last.EndsWith(".md", StringComparison.Ordinal))
            {
                segments[^1] = last.Substring(0, last.Length - 3);
            }
        }

        return New(segments);
    }

    /// <summary>
    /// Resolves this id to a file path under <paramref name="bundleRoot"/>
    /// (appending <c>.md</c>): <c>&lt;root&gt;/&lt;a&gt;/&lt;b&gt;.md</c>.
    /// </summary>
    public string ToPath(string bundleRoot)
    {
        var parts = new List<string> { bundleRoot };
        parts.AddRange(Segments.Take(Segments.Count - 1));
        parts.Add(Name + ".md");
        return Path.Combine(parts.ToArray());
    }

    /// <summary>
    /// Validates a single path segment against the rule
    /// <c>[A-Za-z0-9_][A-Za-z0-9_.\-]*</c>.
    /// </summary>
    /// <exception cref="ConceptIdException">The segment is empty or malformed.</exception>
    public static void ValidateSegment(string segment)
    {
        if (segment.Length == 0 || !IsValidFirstChar(segment[0]))
        {
            throw new ConceptIdException($"Invalid concept id segment: {DebugQuote.Quote(segment)}");
        }

        for (var i = 1; i < segment.Length; i++)
        {
            if (!IsValidLaterChar(segment[i]))
            {
                throw new ConceptIdException($"Invalid concept id segment: {DebugQuote.Quote(segment)}");
            }
        }
    }

    /// <summary>
    /// Normalizes a free-form string into a segment that always passes <see cref="ValidateSegment"/>.
    ///
    /// Algorithm, in order: (1) full-Unicode case-fold via
    /// <see cref="OKF4net.Internal.UnicodeCaseFold.ToLowercase"/> (not <c>string.ToLowerInvariant</c>,
    /// which misses Final_Sigma and İ); (2) map each character to itself if it satisfies
    /// <see cref="IsValidLaterChar"/>, otherwise to <c>'-'</c>; (3) collapse every run of 2+ <c>'-'</c>
    /// (whether original or substituted) into one; (4) strip characters from the front while the
    /// first character fails <see cref="IsValidFirstChar"/> (a leading <c>'-'</c> or <c>'.'</c>) —
    /// nothing is trimmed from the end, since a trailing <c>'-'</c>/<c>'.'</c> is a valid
    /// <see cref="IsValidLaterChar"/>. Operates on <see cref="char"/> (UTF-16 code units, not code
    /// points): a surrogate pair (e.g. an emoji) simply becomes two adjacent substitutions, merged by
    /// step 3 like any other run.
    ///
    /// Does not attempt transliteration: a non-ASCII letter (e.g. an accented or non-Latin character)
    /// is replaced by <c>'-'</c>, not folded to an ASCII approximation — seeded from the ASCII-only
    /// rule <see cref="ValidateSegment"/> already enforces (see the design spec and the upstream
    /// issue tracking whether that restriction should ever be relaxed).
    /// </summary>
    /// <exception cref="ConceptIdException">The result, after normalization, is an empty string.</exception>
    public static string Slugify(string input)
    {
        // UnicodeCaseFold resolves via this file's existing `using OKF4net.Internal;` (same
        // using DebugQuote below already relies on) -- no extra qualification needed.
        var folded = UnicodeCaseFold.ToLowercase(input);

        var mapped = new System.Text.StringBuilder(folded.Length);
        foreach (var c in folded)
        {
            mapped.Append(IsValidLaterChar(c) ? c : '-');
        }

        var collapsed = new System.Text.StringBuilder(mapped.Length);
        var previousWasDash = false;
        foreach (var c in mapped.ToString())
        {
            var isDash = c == '-';
            if (isDash && previousWasDash)
            {
                continue;
            }

            collapsed.Append(c);
            previousWasDash = isDash;
        }

        var candidate = collapsed.ToString();
        var start = 0;
        while (start < candidate.Length && !IsValidFirstChar(candidate[start]))
        {
            start++;
        }

        var result = candidate[start..];
        if (result.Length == 0)
        {
            throw new ConceptIdException($"Cannot derive a non-empty concept id segment from {DebugQuote.Quote(input)}.");
        }

        return result;
    }

    private static bool IsValidFirstChar(char c) => char.IsAsciiLetterOrDigit(c) || c == '_';

    private static bool IsValidLaterChar(char c) =>
        char.IsAsciiLetterOrDigit(c) || c == '_' || c == '.' || c == '-';

    /// <inheritdoc/>
    public override string ToString() => string.Join("/", Segments);

    /// <inheritdoc/>
    public bool Equals(ConceptId? other)
    {
        if (other is null)
        {
            return false;
        }

        if (ReferenceEquals(this, other))
        {
            return true;
        }

        if (Segments.Count != other.Segments.Count)
        {
            return false;
        }

        for (var i = 0; i < Segments.Count; i++)
        {
            if (!string.Equals(Segments[i], other.Segments[i], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    /// <inheritdoc/>
    public override bool Equals(object? obj) => Equals(obj as ConceptId);

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var s in Segments)
        {
            hash.Add(s, StringComparer.Ordinal);
        }

        return hash.ToHashCode();
    }

    /// <summary>
    /// Lexicographic, element-wise ordering over <see cref="Segments"/>:
    /// segments are compared pairwise with ordinal (byte-wise) string
    /// comparison, and if one sequence is a strict prefix of the other, the
    /// shorter sequence sorts first — standard sequence ordering semantics.
    /// </summary>
    public int CompareTo(ConceptId? other)
    {
        if (other is null)
        {
            // IComparable convention: a non-null instance sorts after null.
            return 1;
        }

        var count = Math.Min(Segments.Count, other.Segments.Count);
        for (var i = 0; i < count; i++)
        {
            var cmp = string.CompareOrdinal(Segments[i], other.Segments[i]);
            if (cmp != 0)
            {
                return cmp;
            }
        }

        return Segments.Count.CompareTo(other.Segments.Count);
    }

    /// <inheritdoc/>
    int IComparable.CompareTo(object? obj)
    {
        if (obj is null)
        {
            return 1;
        }

        if (obj is not ConceptId other)
        {
            throw new ArgumentException($"Object must be of type {nameof(ConceptId)}.", nameof(obj));
        }

        return CompareTo(other);
    }

    /// <summary>Ordinal-by-segment less-than comparison.</summary>
    public static bool operator <(ConceptId left, ConceptId right) => Compare(left, right) < 0;

    /// <summary>Ordinal-by-segment less-than-or-equal comparison.</summary>
    public static bool operator <=(ConceptId left, ConceptId right) => Compare(left, right) <= 0;

    /// <summary>Ordinal-by-segment greater-than comparison.</summary>
    public static bool operator >(ConceptId left, ConceptId right) => Compare(left, right) > 0;

    /// <summary>Ordinal-by-segment greater-than-or-equal comparison.</summary>
    public static bool operator >=(ConceptId left, ConceptId right) => Compare(left, right) >= 0;

    private static int Compare(ConceptId? left, ConceptId? right)
    {
        if (left is null)
        {
            return right is null ? 0 : -1;
        }

        return left.CompareTo(right);
    }
}
