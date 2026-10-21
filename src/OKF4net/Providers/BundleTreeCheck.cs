// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net.Internal;

namespace OKF4net.Providers;

/// <summary>
/// The tree check before a creation (spec §2, "the writes must not create these conflicts";
/// decision B10). Validating a listing does not stop a write from introducing a collision: on a
/// case-sensitive volume holding <c>A/x.md</c>, creating <c>a/y.md</c> passes the key-by-key
/// validation and <see cref="BundleWriteCondition.MustNotExist"/>, and the next load would
/// discard both subtrees. So before the first byte of a creation the core asks the provider —
/// never an image, which may be stale — whether the tree that would result is one the loader
/// accepts under the same <see cref="BundleKeyValidator"/>:
/// <list type="bullet">
/// <item><description>under <see cref="BundleKeyConstraints.CaseInsensitiveCollisions"/> or
/// <see cref="BundleKeyConstraints.UnicodeFormCollisions"/>, no existing sibling of any segment
/// of the new key differs from it but folds to the same name through
/// <see cref="BundleKeyValidator.FoldSegment"/> — the loader's own fold, not a second one;</description></item>
/// <item><description>under <see cref="BundleKeyConstraints.FileDirectoryConflict"/>, no proper
/// prefix of the new key is an existing object, and no object exists under the new key taken
/// as a prefix.</description></item>
/// </list>
/// A validator that sets none of these flags needs no check, and none is made.
/// <para><b>What it reads.</b> When names fold, one listing of the whole bundle
/// (<see cref="IBundleProvider.Enumerate"/> with no prefix): the children of every parent prefix
/// of the new key are in it, and so are the objects on and under its path. Otherwise one
/// <see cref="IBundleProvider.Exists"/> per proper prefix and one listing under the key itself.
/// Every listed entry counts against <see cref="BundleLimits.MaxEntries"/>, enforced here even
/// when the provider ignores it (spec §6). A provider exception propagates: a write refuses what
/// it cannot see.</para>
/// <para><b>Not atomic across processes</b> (issue #178): the check and the write that follows
/// are two operations; the caller holds its in-process write lock across both, and a conflict
/// another process creates in between is discarded, with a diagnostic, by the next load.</para>
/// </summary>
internal static class BundleTreeCheck
{
    /// <summary>
    /// Checks the creation of <paramref name="key"/>, which the caller has just found absent at
    /// <paramref name="provider"/>; an update needs no check and the caller makes none.
    /// </summary>
    /// <returns><see langword="null"/> when the tree that results is valid; otherwise a
    /// library-authored sentence (no final period) naming the key and the conflicting name.</returns>
    internal static string? CheckCreation(IBundleProvider provider, BundleKeyValidator validator, BundleKey key, BundleLimits limits)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(validator);
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(limits);
        var folds = validator.Effective.HasFlag(BundleKeyConstraints.CaseInsensitiveCollisions)
            || validator.Effective.HasFlag(BundleKeyConstraints.UnicodeFormCollisions);
        var fileDirectory = validator.Effective.HasFlag(BundleKeyConstraints.FileDirectoryConflict);
        if (folds)
        {
            return CheckAgainstListing(provider, validator, key, limits, fileDirectory);
        }

        return fileDirectory ? CheckPrefixes(provider, key, limits) : null;
    }

    /// <summary>Without folding: an exact object on a proper prefix, or an object under the key.</summary>
    private static string? CheckPrefixes(IBundleProvider provider, BundleKey key, BundleLimits limits)
    {
        for (var depth = 1; depth < key.Segments.Count; depth++)
        {
            var prefix = BundleKey.FromSegments([.. key.Segments.Take(depth)]);
            if (provider.Exists(prefix))
            {
                return PrefixIsObject(key, prefix.ToString());
            }
        }

        foreach (var listing in provider.Enumerate(key, limits))
        {
            return ObjectsUnder(key, listing.RawName); // the first one is enough
        }

        return null;
    }

    /// <summary>With folding: one listing of the bundle answers the three questions; the shallowest conflict is reported.</summary>
    private static string? CheckAgainstListing(IBundleProvider provider, BundleKeyValidator validator, BundleKey key, BundleLimits limits, bool fileDirectory)
    {
        var segments = key.Segments;
        var folded = segments.Select(validator.FoldSegment).ToArray();
        (int Level, int Kind, string Name)? found = null;
        var count = 0;
        foreach (var listing in provider.Enumerate(null, limits))
        {
            if (++count > limits.MaxEntries)
            {
                throw new BundleLimitExceededException(BundleBound.Entries, limits.MaxEntries);
            }

            var raw = listing.RawName;
            var parts = raw.Split('/');
            for (var level = 0; level < parts.Length && level < segments.Count; level++)
            {
                var name = parts[level];
                if (string.Equals(name, segments[level], StringComparison.Ordinal))
                {
                    if (fileDirectory && level == parts.Length - 1 && level < segments.Count - 1)
                    {
                        Keep(ref found, (level, 0, raw)); // an object on a proper prefix of the key
                    }

                    if (fileDirectory && level == segments.Count - 1 && parts.Length > segments.Count)
                    {
                        Keep(ref found, (segments.Count, 2, raw)); // an object under the key
                    }

                    continue;
                }

                if (string.Equals(validator.FoldSegment(name), folded[level], StringComparison.Ordinal))
                {
                    Keep(ref found, (level, 1, string.Join('/', parts.Take(level + 1)))); // a twin
                }

                break;
            }
        }

        return found switch
        {
            null => null,
            { Kind: 0 } f => PrefixIsObject(key, f.Name),
            { Kind: 1 } f => Twin(key, f.Name, string.Join('/', segments.Take(f.Level + 1))),
            { } f => ObjectsUnder(key, f.Name),
        };
    }

    /// <summary>Keeps the conflict that sorts first: shallowest level, then kind, then name (ordinal), so the message does not depend on listing order.</summary>
    private static void Keep(ref (int Level, int Kind, string Name)? found, (int Level, int Kind, string Name) candidate)
    {
        if (found is not { } current
            || candidate.Level < current.Level
            || (candidate.Level == current.Level && candidate.Kind < current.Kind)
            || (candidate.Level == current.Level && candidate.Kind == current.Kind && string.CompareOrdinal(candidate.Name, current.Name) < 0))
        {
            found = candidate;
        }
    }

    private static string PrefixIsObject(BundleKey key, string prefix) =>
        $"{DebugQuote.Quote(key.ToString())} cannot be created: {DebugQuote.Quote(prefix)} is an object, so it cannot also be a prefix of other keys";

    private static string ObjectsUnder(BundleKey key, string under) =>
        $"{DebugQuote.Quote(key.ToString())} cannot be created: objects exist under it (such as {DebugQuote.Quote(under)}), so it cannot also be an object";

    private static string Twin(BundleKey key, string existing, string mine) =>
        $"{DebugQuote.Quote(key.ToString())} cannot be created: {DebugQuote.Quote(existing)} already exists and differs from {DebugQuote.Quote(mine)} only in case or Unicode form";
}
