using System.Text;

namespace OKF4net;

/// <summary>
/// A concept identifier: an ordered list of path segments (e.g.
/// <c>["tables", "users"]</c> for <c>tables/users</c>) — the path of a
/// concept's file within the bundle with the <c>.md</c> suffix removed
/// (§2). Port of the Rust <c>ConceptId</c> (src/concept_id.rs), which in
/// turn ports the reference <c>bundle/paths.py</c>, including its segment
/// validation rule.
/// </summary>
public sealed class ConceptId : IEquatable<ConceptId>
{
    /// <summary>The id's segments, in order.</summary>
    public IReadOnlyList<string> Segments { get; }

    /// <summary>The final segment (the concept's own name, without directories).</summary>
    public string Name => Segments.Count > 0 ? Segments[^1] : string.Empty;

    /// <summary>The id of the directory that contains this concept, if any.</summary>
    public ConceptId? Parent =>
        Segments.Count <= 1 ? null : new ConceptId(Segments.Take(Segments.Count - 1).ToList());

    private ConceptId(IReadOnlyList<string> segments)
    {
        Segments = segments;
    }

    /// <summary>
    /// Builds a concept id from segments, validating each. Port of
    /// <c>ConceptId::new</c> (concept_id.rs:33-41). Unlike <see cref="Parse"/>,
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
    /// are dropped (so leading/trailing/duplicate slashes are tolerated),
    /// matching the reference <c>parse_concept_id</c>. Port of
    /// <c>ConceptId::parse</c> (concept_id.rs:46-55).
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
            throw new ConceptIdException($"Empty concept id: {DebugQuote(s)}");
        }

        foreach (var seg in segments)
        {
            ValidateSegment(seg);
        }

        return new ConceptId(segments);
    }

    /// <summary>Like <see cref="Parse"/>, but returns <c>false</c> instead of throwing.</summary>
    public static bool TryParse(string s, out ConceptId? id)
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
    /// Port of <c>ConceptId::from_path</c> (concept_id.rs:91-106).
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
            throw new ConceptIdException($"{path} is not under bundle root");
        }

        var segments = rel.Split('/', StringSplitOptions.RemoveEmptyEntries).ToList();
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
    /// Port of <c>ConceptId::to_path</c> (concept_id.rs:79-87).
    /// </summary>
    public string ToPath(string bundleRoot)
    {
        var parts = new List<string> { bundleRoot };
        parts.AddRange(Segments.Take(Segments.Count - 1));
        parts.Add(Name + ".md");
        return Path.Combine(parts.ToArray());
    }

    /// <summary>
    /// Validates a single path segment against the reference rule
    /// <c>[A-Za-z0-9_][A-Za-z0-9_.\-]*</c>. Port of <c>validate_segment</c>
    /// (concept_id.rs:124-136).
    /// </summary>
    /// <exception cref="ConceptIdException">The segment is empty or malformed.</exception>
    public static void ValidateSegment(string segment)
    {
        if (segment.Length == 0 || !IsValidFirstChar(segment[0]))
        {
            throw new ConceptIdException($"Invalid concept id segment: {DebugQuote(segment)}");
        }

        for (var i = 1; i < segment.Length; i++)
        {
            if (!IsValidLaterChar(segment[i]))
            {
                throw new ConceptIdException($"Invalid concept id segment: {DebugQuote(segment)}");
            }
        }
    }

    private static bool IsValidFirstChar(char c) => char.IsAsciiLetterOrDigit(c) || c == '_';

    private static bool IsValidLaterChar(char c) =>
        char.IsAsciiLetterOrDigit(c) || c == '_' || c == '.' || c == '-';

    /// <summary>
    /// Renders a string the way Rust's <c>{:?}</c> (Debug) format does for
    /// <c>&amp;str</c>: double-quoted, with <c>\</c>, <c>"</c>, and common
    /// control characters escaped. Used to keep error messages byte-for-byte
    /// identical to the Rust crate's <c>format!("...{s:?}")</c> calls.
    /// </summary>
    private static string DebugQuote(string s)
    {
        var sb = new StringBuilder(s.Length + 2);
        sb.Append('"');
        foreach (var c in s)
        {
            switch (c)
            {
                case '"':
                    sb.Append("\\\"");
                    break;
                case '\\':
                    sb.Append("\\\\");
                    break;
                case '\n':
                    sb.Append("\\n");
                    break;
                case '\r':
                    sb.Append("\\r");
                    break;
                case '\t':
                    sb.Append("\\t");
                    break;
                default:
                    if (char.IsControl(c))
                    {
                        sb.Append("\\u{").Append(((int)c).ToString("x")).Append('}');
                    }
                    else
                    {
                        sb.Append(c);
                    }

                    break;
            }
        }

        sb.Append('"');
        return sb.ToString();
    }

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
}
