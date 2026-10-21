// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net.Internal;
using OKF4net.Providers;

namespace OKF4net;

/// <summary>
/// Generation of <c>index.md</c> directory listings (§8).
///
/// Some producers synthesize subdirectory descriptions with an LLM; since
/// OKF tooling must not require any particular model or network access, the
/// description synthesizer here is a pluggable delegate with a deterministic,
/// dependency-free default (<see cref="DefaultSynthesize"/>).
/// </summary>
public static class IndexGenerator
{
    private const string IndexFile = "index.md";

    /// <summary>
    /// A synthesizer for subdirectory descriptions: given the directory's
    /// path (relative to the bundle root) and its child (title, description)
    /// pairs, returns a one-line description.
    /// </summary>
    public delegate string Synthesize(string relativeDir, IReadOnlyList<(string Title, string Description)> children);

    /// <summary>
    /// Builds the markdown text of an <c>index.md</c> from a set of entries:
    /// entries are grouped by type under <c>#</c>-headings (types sorted
    /// ascending, ordinal), and within each group sorted by title
    /// (case-insensitive).
    /// </summary>
    public static string BuildIndexText(IReadOnlyList<IndexEntry> entries)
    {
        var grouped = new SortedDictionary<string, List<(string Title, string Link, string Description)>>(StringComparer.Ordinal);
        foreach (var e in entries)
        {
            var key = e.Type.Length == 0 ? "Other" : e.Type;
            if (!grouped.TryGetValue(key, out var list))
            {
                list = [];
                grouped[key] = list;
            }

            list.Add((e.Title, e.Link, e.Description));
        }

        var sections = new List<string>();
        foreach (var (typ, items) in grouped)
        {
            // Stable sort by lowercased title, using UnicodeCaseFold for full
            // Unicode case folding over the Basic Multilingual Plane rather
            // than string.ToLowerInvariant()/StringComparer.Ordinal, which
            // handle characters like U+0130 (İ) and code points outside the
            // Basic Multilingual Plane differently -- see UnicodeCaseFold's doc
            // comments.
            var sorted = items
                .Select((item, ordinal) => (item, ordinal))
                .OrderBy(x => UnicodeCaseFold.ToLowercase(x.item.Title), Comparer<string>.Create(UnicodeCaseFold.CompareCodePoints))
                .ThenBy(x => x.ordinal)
                .Select(x => x.item)
                .ToList();

            var lines = new List<string> { $"# {typ}", string.Empty };
            foreach (var (title, link, description) in sorted)
            {
                var suffix = description.Length == 0 ? string.Empty : $" - {description}";
                lines.Add($"* [{title}]({link}){suffix}");
            }

            sections.Add(string.Join('\n', lines));
        }

        return string.Join("\n\n", sections) + "\n";
    }

    /// <summary>
    /// The default, deterministic synthesizer: lists the child titles. Used
    /// when no custom (e.g. LLM-backed) synthesizer is supplied.
    /// </summary>
    public static string DefaultSynthesize(string relativeDir, IReadOnlyList<(string Title, string Description)> children)
    {
        if (children.Count == 0)
        {
            return string.Empty;
        }

        var titles = children.Select(c => c.Title);
        return $"Contains {children.Count}: {string.Join(", ", titles)}.";
    }

    /// <summary>
    /// Regenerates every <c>index.md</c> in the bundle using
    /// <see cref="DefaultSynthesize"/>.
    /// </summary>
    public static IReadOnlyList<string> RegenerateIndexes(string bundleRoot) =>
        RegenerateIndexesWith(bundleRoot, DefaultSynthesize);

    /// <summary>
    /// Test-only hook invoked once per directory about to get its
    /// <c>index.md</c>, after its entries are known and before that
    /// <c>index.md</c> is read and written through the provider -- where the
    /// provider's late guards run (see <see cref="RegenerateIndexesWith(string, Synthesize)"/>'s
    /// remarks). Its argument is the directory's display path: the bundle root, or
    /// the root followed by the directory's segments joined with the OS separator.
    /// Used by <c>IndexTests</c> to deterministically substitute a directory that
    /// was listed as a real one with a junction/symlink, in the exact narrow window
    /// such a race would need to land in. <c>internal</c> rather than
    /// test-conditional compilation, consistent with this assembly's
    /// <c>InternalsVisibleTo</c> grant to <c>OKF4net.Tests</c>. No-op (null) in
    /// production.
    /// </summary>
    internal static Action<string>? BeforeLateReparseCheckForTest { get; set; }

    /// <summary>
    /// Regenerates every <c>index.md</c> in the bundle, deriving each
    /// subdirectory's description with the supplied synthesizer.
    ///
    /// Directories are processed deepest-first so a parent index can reuse
    /// the descriptions computed for its children. Empty directories are
    /// skipped. Returns the paths of the index files written.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Runs through a <see cref="FileSystemBundleProvider"/> over
    /// <paramref name="bundleRoot"/> (trailing separators trimmed), exactly as
    /// <see cref="RegenerateIndexesWith(IBundleProvider, Synthesize, BundleLoadOptions?)"/>
    /// does with the default <see cref="BundleLoadOptions"/>; each returned path is
    /// that root followed by the <c>index.md</c>'s key segments joined with the OS
    /// separator, in the order the files were written. <paramref name="synthesize"/>
    /// receives the directory's path relative to the root with the OS separator
    /// (<c>a\b</c> on Windows). A <paramref name="bundleRoot"/> that is not an
    /// existing directory gives an empty list.
    /// </para>
    /// <para>
    /// <b>Reparse-point safety.</b> The bundle is listed by the provider's walk,
    /// which skips every symlink and junction under the root (never follows one)
    /// and refuses to enter a directory it cannot inspect; so a linked
    /// directory is neither indexed nor referenced. Every <c>index.md</c> is then
    /// read and written by the provider, whose guards are STRICT
    /// (<see cref="ReparsePoints.IsReparsePointOrUninspectable"/>) and run at the
    /// moment of the access: the whole path from the root, the <c>index.md</c>
    /// entry itself, again under the commit lock right before the commit. A
    /// directory replaced by a link, or an <c>index.md</c> planted as one, after
    /// the listing is refused there, and that <c>index.md</c> is SKIPPED (not in
    /// the returned list); regeneration continues with the remaining directories.
    /// The bundle root itself is never inspected: a symlinked or mounted root is a
    /// legitimate setup and is indexed like any other.
    /// </para>
    /// <para>
    /// <b>Atomic, conditional writes.</b> Each <c>index.md</c> is committed
    /// atomically, conditioned on the version just read (or on its absence), and a
    /// conflict with another writer is retried, at most three attempts in all, reading again
    /// each time. A regeneration takes no lock of its own (its callers lock, or
    /// not): a concurrent write surfaces as a conflict and a retry, never as a lost
    /// update.
    /// </para>
    /// <para>
    /// <b>Failures.</b> A root <c>index.md</c> that cannot be read or parsed is left
    /// untouched and skipped. Any other failure — an I/O error, a name the storage
    /// cannot hold, a write that kept conflicting — propagates and ends the run, as
    /// an I/O error did before the provider: the <c>index.md</c> files written
    /// until then stay written.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<string> RegenerateIndexesWith(string bundleRoot, Synthesize synthesize)
    {
        if (!Directory.Exists(bundleRoot))
        {
            return [];
        }

        // §8: a trailing separator is trimmed (ReparsePoints.CanonicalizeRoot), so the
        // display paths below are the same for "dir" and "dir\".
        var root = ReparsePoints.CanonicalizeRoot(bundleRoot);
        var report = Regenerate(new FileSystemBundleProvider(root), synthesize, BundleLoadOptions.Default, legacy: true);
        return [.. report.Written.Select(key => BundleDisplayPath.Compose(root, key))];
    }

    /// <summary>
    /// Regenerates every <c>index.md</c> of the bundle held by <paramref name="provider"/>
    /// with <see cref="DefaultSynthesize"/>; see
    /// <see cref="RegenerateIndexesWith(IBundleProvider, Synthesize, BundleLoadOptions?)"/>.
    /// </summary>
    /// <param name="provider">Where the bundle is read and written.</param>
    /// <param name="options">The key policy and bounds the bundle is loaded with; <see langword="null"/>: <see cref="BundleLoadOptions.Default"/>.</param>
    /// <returns>The keys written, in order, and the <c>index.md</c> keys that could not be written, with why.</returns>
    /// <exception cref="BundleReadOnlyException">The provider cannot write; nothing was attempted.</exception>
    public static BundleWriteReport RegenerateIndexes(IBundleProvider provider, BundleLoadOptions? options = null) =>
        RegenerateIndexesWith(provider, DefaultSynthesize, options);

    /// <summary>
    /// Regenerates every <c>index.md</c> of the bundle held by <paramref name="provider"/>
    /// (spec §8, §9): the bundle is listed and validated as a load under
    /// <paramref name="options"/> does, each directory of a <c>.md</c> key gets the
    /// index of its <c>.md</c> children and of its subdirectories — prefixes with at
    /// least one object under them, so a directory with no file is not referenced —
    /// deepest-first, and each <c>index.md</c> is written through the provider,
    /// conditioned on the version read (<see cref="BundleWriteCondition.IfMatch"/>, or
    /// <see cref="BundleWriteCondition.MustNotExist"/> for a new one), a creation first
    /// passing the tree check every creation of the core passes. A conflict is retried
    /// (at most three attempts in all), reading again each time.
    /// </summary>
    /// <param name="provider">Where the bundle is read and written.</param>
    /// <param name="synthesize">The subdirectory-description synthesizer; it receives the directory's key prefix with <c>/</c> as separator (<c>a/b</c>).</param>
    /// <param name="options">The key policy and bounds the bundle is loaded with; <see langword="null"/>: <see cref="BundleLoadOptions.Default"/>.</param>
    /// <returns>
    /// The keys written, in order, and one failure per <c>index.md</c> that was not:
    /// refused by the key policy of <paramref name="options"/> (a host's own validator
    /// included; that <c>index.md</c> is neither read nor written), refused by the provider (a
    /// link on its path), a name the storage cannot hold or a creation the tree check refuses, a
    /// write that kept conflicting, an I/O error, or a root <c>index.md</c> that could not be
    /// read or parsed (left untouched). The reasons are library-authored, but for the key
    /// policy's own reason. A failure ends nothing: the other directories are written.
    /// </returns>
    /// <exception cref="BundleReadOnlyException">The provider cannot write; nothing was attempted.</exception>
    /// <exception cref="BundleProviderException">Listing the bundle failed or exceeded a bound of <paramref name="options"/>.</exception>
    /// <exception cref="IOException">Listing the bundle failed.</exception>
    public static BundleWriteReport RegenerateIndexesWith(IBundleProvider provider, Synthesize synthesize, BundleLoadOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(provider);
        if (!provider.Capabilities.CanWrite)
        {
            throw new BundleReadOnlyException(provider.Id);
        }

        ArgumentNullException.ThrowIfNull(synthesize);
        return Regenerate(provider, synthesize, options ?? BundleLoadOptions.Default, legacy: false);
    }

    /// <summary>
    /// The regeneration behind both families of overloads. <paramref name="legacy"/> is the
    /// <c>string</c> overloads' contract: an <c>index.md</c> key the policy refuses, a provider
    /// refusal (<see cref="BundleAccessRefusedException"/>, a STRICT guard) and an unreadable root
    /// <c>index.md</c> skip that directory silently,
    /// and every other failure propagates, ending the run; <c>relativeDir</c> uses the OS
    /// separator. Otherwise every per-directory failure becomes a <see cref="BundleWriteFailure"/>
    /// and the run goes on; <c>relativeDir</c> uses <c>/</c>.
    /// </summary>
    internal static BundleWriteReport Regenerate(IBundleProvider provider, Synthesize synthesize, BundleLoadOptions options, bool legacy)
    {
        var limits = options.Limits;
        var validator = new BundleKeyValidator(provider.NativeConstraints, options.KeyPolicy, options.ExtraKeyValidators);
        var tree = new Dictionary<string, Dictionary<string, Child>>(StringComparer.Ordinal);
        var directories = new HashSet<string>(StringComparer.Ordinal);
        var documents = new Dictionary<string, IndexEntry>(StringComparer.Ordinal);

        // Listing and reading, inside one provider scope (each directory listed once): the
        // keys a load would accept, then every .md child's entry. Nothing is written yet.
        using (ProviderScopes.Begin(provider))
        {
            var (treeInput, overLength) = Bundle.ListNames(provider, limits);
            var chunk = new byte[Bundle.ReadChunkBytes];
            foreach (var key in validator.ValidateTree(treeInput, overLength).Accepted)
            {
                var segments = key.Segments;
                for (var depth = 0; depth < segments.Count; depth++)
                {
                    var parent = string.Join('/', segments.Take(depth));
                    if (!tree.TryGetValue(parent, out var children))
                    {
                        children = new Dictionary<string, Child>(StringComparer.Ordinal);
                        tree[parent] = children;
                    }

                    var leaf = depth == segments.Count - 1;
                    children.TryGetValue(segments[depth], out var child);
                    children[segments[depth]] = leaf ? child with { IsObject = true } : child with { IsPrefix = true };
                    if (key.IsMarkdown)
                    {
                        directories.Add(parent);
                    }
                }

                if (key.IsMarkdown && key.Name != IndexFile && ReadEntry(provider, key, limits, chunk) is { } entry)
                {
                    documents[key.ToString()] = entry;
                }
            }
        }

        // Deepest-first; ties broken by path for determinism.
        var order = directories.ToList();
        order.Sort((a, b) =>
        {
            var cmp = Depth(b).CompareTo(Depth(a));
            return cmp != 0 ? cmp : PathOrdering.CompareComponentWise(a, b);
        });

        var written = new List<BundleKey>();
        var failed = new List<BundleWriteFailure>();
        var dirDescriptions = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var directory in order)
        {
            var entries = new List<IndexEntry>();
            var names = tree[directory].Keys.ToList();
            names.Sort(PathOrdering.CompareComponentWise);
            foreach (var name in names)
            {
                if (name == IndexFile)
                {
                    continue;
                }

                var child = tree[directory][name];
                var path = directory.Length == 0 ? name : directory + "/" + name;
                if (child.IsObject && MarkdownPaths.HasMarkdownExtension(name))
                {
                    if (documents.TryGetValue(path, out var document))
                    {
                        entries.Add(document);
                    }
                }
                else if (child.IsPrefix)
                {
                    var description = dirDescriptions.GetValueOrDefault(path, string.Empty);
                    entries.Add(new IndexEntry("Subdirectories", name, $"{name}/{IndexFile}", description));
                }
            }

            if (entries.Count == 0)
            {
                continue;
            }

            BeforeLateReparseCheckForTest?.Invoke(directory.Length == 0 ? provider.Location : BundleDisplayPath.Compose(provider.Location, BundleKey.Parse(directory)));

            var indexKey = BundleKey.Parse(directory.Length == 0 ? IndexFile : directory + "/" + IndexFile);
            var failure = WriteIndex(provider, validator, limits, indexKey, isRoot: directory.Length == 0, BuildIndexText(entries), legacy);
            if (failure is not null)
            {
                if (failure.Length > 0)
                {
                    failed.Add(new BundleWriteFailure(indexKey, failure));
                }

                continue;
            }

            written.Add(indexKey);
            if (directory.Length == 0)
            {
                continue;
            }

            var pairs = entries.Select(e => (e.Title, e.Description)).ToList();
            string desc;
            if (pairs.Count == 1 && pairs[0].Description.Length != 0)
            {
                desc = pairs[0].Description;
            }
            else
            {
                var rel = legacy ? directory.Replace('/', Path.DirectorySeparatorChar) : directory;
                desc = synthesize(rel, pairs);
            }

            dirDescriptions[directory] = desc;
        }

        return new BundleWriteReport(written, [], failed);
    }

    /// <summary>
    /// Reads and writes one <c>index.md</c>, retrying a conflict (spec §4, decision B5):
    /// <see langword="null"/> once written; otherwise the reason it was not, or an empty
    /// string for a silent skip (<paramref name="legacy"/>: a key the policy refuses, a provider
    /// refusal, an unreadable root). In <paramref name="legacy"/> mode every other failure is thrown.
    /// </summary>
    private static string? WriteIndex(IBundleProvider provider, BundleKeyValidator validator, BundleLimits limits, BundleKey key, bool isRoot, string body, bool legacy)
    {
        // The run's key policy covers the index keys it produces, as it covers every concept the
        // writer writes (spec §2, decision B2): an index.md the validator refuses — a host's own
        // rule included — is neither read nor written, whether it exists or not.
        if (validator.CheckKey(key) is { } refused)
        {
            return legacy ? string.Empty : $"index.md cannot be written under the bundle's key policy: {refused}";
        }

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var (current, unreadable) = ReadCurrentIndex(provider, key, isRoot, limits);
                if (unreadable)
                {
                    // A pre-existing root index.md that fails to read or parse (locked by
                    // another process, malformed YAML, bad encoding) is left untouched rather
                    // than silently overwritten frontmatter-free, so a transient read failure
                    // can never drop the okf_version marker okf-mcp's discovery relies on.
                    return legacy ? string.Empty : "the existing index.md could not be read or parsed, so it was left as it is";
                }

                var content = body;

                // The bundle-root index.md is the only place frontmatter is spec-sanctioned
                // at all (§12: the okf_version marker, see Bundle.OkfVersion), and even there
                // ONLY the okf_version key is sanctioned (§8) -- every other index.md must
                // have none. So only the root's existing okf_version value (never the rest
                // of its frontmatter) is carried forward; a non-root index.md is body only.
                // Both self-heal every §8 violation regeneration can reach.
                if (current?.Document?.Frontmatter.Get("okf_version") is { } okfVersion)
                {
                    var rootFrontmatter = new Frontmatter();
                    rootFrontmatter.Set("okf_version", okfVersion);
                    content = new OkfDocument(rootFrontmatter, body).Serialize();
                }

                if (current is null && BundleTreeCheck.CheckCreation(provider, validator, key, limits) is { } conflict)
                {
                    return legacy ? throw new BundleWriteConflictException(key, BundleWriteConflictReason.NameUnavailable) : conflict;
                }

                provider.Write(key, OkfEncodings.NoBom.GetBytes(content), current is { } c ? BundleWriteCondition.IfMatch(c.Version) : BundleWriteCondition.MustNotExist);
                return null;
            }
            catch (BundleWriteConflictException e) when (e.Reason == BundleWriteConflictReason.ConditionFailed && attempt < BundleConceptWriter.MaxAttempts)
            {
                Thread.Sleep(BundleConceptWriter.RetryDelay(attempt));
            }
            catch (BundleWriteConflictException e) when (e.Reason == BundleWriteConflictReason.ConditionFailed && !legacy)
            {
                return $"index.md kept changing while it was being written; the write was refused after {BundleConceptWriter.MaxAttempts} attempts";
            }
            catch (BundleAccessRefusedException e)
            {
                return legacy ? string.Empty : e.Message;
            }
            catch (Exception e) when (!legacy && e is not BundleReadOnlyException && (e is BundleProviderException or IOException or UnauthorizedAccessException))
            {
                return e is BundleProviderException ? e.Message : $"I/O error while regenerating the index ({e.GetType().Name})";
            }
        }
    }

    /// <summary>
    /// The <c>index.md</c> under <paramref name="key"/> as it is now: <see langword="null"/>
    /// when there is none; otherwise its version and, for the root, its parsed document
    /// (strict UTF-8, the core's one bounded reader). <c>Unreadable</c> is set for a root
    /// <c>index.md</c> that exists but cannot be read, decoded or parsed.
    /// </summary>
    private static ((BundleObjectVersion Version, OkfDocument? Document)? Current, bool Unreadable) ReadCurrentIndex(IBundleProvider provider, BundleKey key, bool isRoot, BundleLimits limits)
    {
        try
        {
            if (!isRoot)
            {
                using var obj = provider.Read(key);
                return ((obj.Version, null), false);
            }

            var text = Bundle.ReadText(provider, key, limits, new byte[Bundle.ReadChunkBytes], out var version);
            return ((version, OkfDocument.Parse(text)), false);
        }
        catch (BundleObjectNotFoundException)
        {
            return (null, false);
        }
        catch (Exception e) when (isRoot && e is IOException or UnauthorizedAccessException or System.Text.DecoderFallbackException or DocumentParseException)
        {
            return (null, true);
        }
    }

    /// <summary>
    /// The index entry of one <c>.md</c> child, or <see langword="null"/> when it cannot be read,
    /// decoded or parsed — such a document is left out of its directory's index, as before. The
    /// title is the frontmatter's, or the file stem; the link is the file name.
    /// </summary>
    private static IndexEntry? ReadEntry(IBundleProvider provider, BundleKey key, BundleLimits limits, byte[] chunk)
    {
        OkfDocument doc;
        try
        {
            doc = OkfDocument.Parse(Bundle.ReadText(provider, key, limits, chunk, out _));
        }
        catch (Exception e) when (e is BundleProviderException or IOException or UnauthorizedAccessException or System.Text.DecoderFallbackException or DocumentParseException)
        {
            return null;
        }

        var stem = Path.GetFileNameWithoutExtension(key.Name);
        return new IndexEntry(doc.Frontmatter.Type ?? string.Empty, doc.Frontmatter.Title ?? stem, key.Name, doc.Frontmatter.Description ?? string.Empty);
    }

    /// <summary>The number of segments of a key prefix (0 for the root).</summary>
    private static int Depth(string prefix) => prefix.Length == 0 ? 0 : prefix.Count(c => c == '/') + 1;

    /// <summary>What a name stands for under one prefix: an object, the prefix of other keys, or both (a tree the policy lets through).</summary>
    private readonly record struct Child(bool IsObject, bool IsPrefix);
}

/// <summary>
/// One row in a generated index: a <c>(type, title, relative_link,
/// description)</c> tuple.
/// </summary>
/// <param name="Type">The concept type, or <c>"Subdirectories"</c> for a child directory.</param>
/// <param name="Title">Display title.</param>
/// <param name="Link">Relative link target.</param>
/// <param name="Description">One-line description (may be empty).</param>
public sealed record IndexEntry(string Type, string Title, string Link, string Description);
