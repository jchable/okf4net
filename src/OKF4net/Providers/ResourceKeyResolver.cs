// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net.Providers;

/// <summary>What <see cref="ResourceKeyResolver.Resolve"/> made of a §6.2 value.</summary>
public enum ResourceKeyOutcome
{
    /// <summary>An absolute URL; never dereferenced (spec §7).</summary>
    Url,

    /// <summary>The value climbs above the root, resolves to the root itself, uses a drive form or a backslash, or breaks a base key rule.</summary>
    Unsafe,

    /// <summary>A syntactically valid key under the root; existence and policy are checked by the caller.</summary>
    Candidate,
}

/// <summary>
/// Resolves a §6.2 path-valued field lexically on keys (spec §2): no OS path semantics,
/// no percent-decoding, no filesystem access. <c>/x</c> and a bare <c>x</c> resolve from
/// the root (this implementation's reading of §6.2, docs/spec-conformance S6.2-1);
/// <c>./x</c> and <c>../x</c> from the concept's own prefix.
/// </summary>
public static class ResourceKeyResolver
{
    /// <summary>Resolves <paramref name="rawPath"/> relative to <paramref name="conceptKey"/>'s prefix or the root.</summary>
    public static ResourceKeyOutcome Resolve(BundleKey conceptKey, string rawPath, out BundleKey? key)
    {
        ArgumentNullException.ThrowIfNull(conceptKey);
        ArgumentNullException.ThrowIfNull(rawPath);
        key = null;

        var kind = FrontmatterResourceClassifier.KindOf(rawPath);
        if (kind == FrontmatterResourceKind.Url)
        {
            return ResourceKeyOutcome.Url;
        }

        if (rawPath.Contains('\\'))
        {
            return ResourceKeyOutcome.Unsafe;
        }

        var relative = rawPath.TrimStart('/');
        if (relative.Length >= 2 && char.IsAsciiLetter(relative[0]) && relative[1] == ':')
        {
            // A drive-relative or drive-absolute form (e:q.sql, C:/x): never a bundle path.
            return ResourceKeyOutcome.Unsafe;
        }

        var segments = new List<string>();
        if (kind == FrontmatterResourceKind.ConceptRelative && conceptKey.Parent is { } parent)
        {
            segments.AddRange(parent.Segments);
        }

        foreach (var segment in relative.Split('/'))
        {
            switch (segment)
            {
                case "":
                case ".":
                    continue;
                case "..":
                    if (segments.Count == 0)
                    {
                        return ResourceKeyOutcome.Unsafe;
                    }

                    segments.RemoveAt(segments.Count - 1);
                    continue;
                default:
                    segments.Add(segment);
                    continue;
            }
        }

        if (segments.Count == 0)
        {
            return ResourceKeyOutcome.Unsafe;
        }

        if (!BundleKey.TryParse(string.Join('/', segments), out var parsed, out _))
        {
            return ResourceKeyOutcome.Unsafe;
        }

        key = parsed;
        return ResourceKeyOutcome.Candidate;
    }
}
