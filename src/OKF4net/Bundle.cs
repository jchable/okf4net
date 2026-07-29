// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net.Internal;

namespace OKF4net;

/// <summary>
/// Error raised when loading or operating on a bundle on disk, restricted to
/// the conditions <see cref="Bundle.Load"/> itself can raise: I/O failures and
/// a non-directory root. Per-file parse failures are recorded in
/// <see cref="Bundle.ParseErrors"/> instead (loading is permissive, §11).
/// </summary>
public sealed class BundleLoadException : OkfException
{
    /// <summary>Creates the exception with a descriptive message.</summary>
    public BundleLoadException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// A single concept within a bundle (one markdown document).
/// </summary>
public sealed record Concept(ConceptId Id, string Path, OkfDocument Document);

/// <summary>
/// A cross-link from one concept to another, after resolution (§5.3).
/// </summary>
public sealed record ResolvedLink(ConceptId Target, bool Exists, string Text, string Raw);

/// <summary>
/// Loading and traversing an OKF *bundle*: a directory tree of markdown
/// files (§3).
///
/// <see cref="Load"/> walks a directory, parses every non-reserved
/// <c>.md</c> file into a <see cref="Concept"/>, records the reserved
/// <c>index.md</c> / <c>log.md</c> files, and builds the cross-link graph
/// (§5). Loading is **permissive** by design (§11): files whose frontmatter
/// cannot be parsed are collected into <see cref="ParseErrors"/> rather than
/// aborting the load, and broken links are retained as edges to
/// non-existent concepts.
/// </summary>
public sealed class Bundle
{
    private const string IndexFilename = "index.md";
    private const string LogFilename = "log.md";

    /// <summary>Reserved filenames with defined meaning at any level (§3.1).</summary>
    public static readonly string[] ReservedFilenames = [IndexFilename, LogFilename];

    private readonly Dictionary<ConceptId, int> _index;
    private readonly Dictionary<ConceptId, List<ResolvedLink>> _outbound;
    private readonly Dictionary<ConceptId, List<ConceptId>> _backlinks;
    private readonly string? _okfVersion;

    private Bundle(
        string root,
        List<Concept> concepts,
        Dictionary<ConceptId, int> index,
        List<string> indexFiles,
        List<string> logFiles,
        List<(string Path, string Error)> parseErrors,
        Dictionary<ConceptId, List<ResolvedLink>> outbound,
        Dictionary<ConceptId, List<ConceptId>> backlinks)
    {
        Root = root;
        Concepts = concepts;
        _index = index;
        IndexFiles = indexFiles;
        LogFiles = logFiles;
        ParseErrors = parseErrors;
        _outbound = outbound;
        _backlinks = backlinks;
        // Computed eagerly here (not deferred) so OkfVersion reflects the same
        // load-time snapshot as the rest of the bundle: a later change to the
        // root index.md on disk cannot alter an already-loaded instance. Root
        // is set above and is the only field ComputeOkfVersion reads.
        _okfVersion = ComputeOkfVersion();
    }

    /// <summary>
    /// Loads a bundle from a directory tree.
    ///
    /// Throws only for I/O failures or a non-directory root. Per-file parse
    /// failures are recorded in <see cref="ParseErrors"/>.
    /// </summary>
    /// <exception cref="BundleLoadException"><paramref name="root"/> does not exist or is not a directory, or an I/O error occurred.</exception>
    public static Bundle Load(string root)
    {
        if (!Directory.Exists(root))
        {
            throw new BundleLoadException($"bundle root is not a directory: {root}");
        }

        List<string> mdFiles;
        try
        {
            mdFiles = new List<string>();
            CollectMarkdown(root, mdFiles);
        }
        catch (IOException e)
        {
            throw new BundleLoadException($"I/O error: {e.Message}");
        }
        catch (UnauthorizedAccessException e)
        {
            throw new BundleLoadException($"I/O error: {e.Message}");
        }

        mdFiles.Sort(PathOrdering.CompareComponentWise);

        var concepts = new List<Concept>();
        var indexFiles = new List<string>();
        var logFiles = new List<string>();
        var parseErrors = new List<(string Path, string Error)>();

        foreach (var path in mdFiles)
        {
            var filename = System.IO.Path.GetFileName(path);
            switch (filename)
            {
                case IndexFilename:
                    indexFiles.Add(path);
                    break;
                case LogFilename:
                    logFiles.Add(path);
                    break;
                default:
                    {
                        string text;
                        try
                        {
                            text = OkfEncodings.Strict.GetString(File.ReadAllBytes(path));
                        }
                        catch (IOException e)
                        {
                            throw new BundleLoadException($"I/O error: {e.Message}");
                        }
                        catch (UnauthorizedAccessException e)
                        {
                            throw new BundleLoadException($"I/O error: {e.Message}");
                        }
                        catch (System.Text.DecoderFallbackException)
                        {
                            // Non-UTF-8 content is treated as an I/O failure and
                            // aborts the whole load, like any other error during
                            // the walk (bundle files must be valid UTF-8, §3).
                            throw new BundleLoadException("I/O error: stream did not contain valid UTF-8");
                        }

                        OkfDocument document;
                        try
                        {
                            document = OkfDocument.Parse(text);
                        }
                        catch (DocumentParseException e)
                        {
                            parseErrors.Add((path, e.Message));
                            break;
                        }

                        try
                        {
                            var id = ConceptId.FromPath(root, path);
                            concepts.Add(new Concept(id, path, document));
                        }
                        catch (ConceptIdException e)
                        {
                            parseErrors.Add((path, $"Missing required frontmatter keys: {e.Message}"));
                        }

                        break;
                    }
            }
        }

        var index = new Dictionary<ConceptId, int>();
        for (var i = 0; i < concepts.Count; i++)
        {
            index[concepts[i].Id] = i;
        }

        var (outbound, backlinks) = BuildGraph(concepts, index);

        return new Bundle(root, concepts, index, indexFiles, logFiles, parseErrors, outbound, backlinks);
    }

    /// <summary>The bundle's root directory.</summary>
    public string Root { get; }

    /// <summary>All successfully parsed concepts, in path order.</summary>
    public IReadOnlyList<Concept> Concepts { get; }

    /// <summary>Number of concepts.</summary>
    public int Count => Concepts.Count;

    /// <summary><c>true</c> if the bundle has no concepts.</summary>
    public bool IsEmpty => Concepts.Count == 0;

    /// <summary>Looks up a concept by id.</summary>
    public Concept? Get(ConceptId id) => _index.TryGetValue(id, out var i) ? Concepts[i] : null;

    /// <summary><c>true</c> if a concept with this id exists.</summary>
    public bool Contains(ConceptId id) => _index.ContainsKey(id);

    /// <summary>Paths of all <c>index.md</c> files found (§6).</summary>
    public IReadOnlyList<string> IndexFiles { get; }

    /// <summary>Paths of all <c>log.md</c> files found (§7).</summary>
    public IReadOnlyList<string> LogFiles { get; }

    /// <summary>Files whose frontmatter could not be parsed during loading, as (path, error message) pairs.</summary>
    public IReadOnlyList<(string Path, string Error)> ParseErrors { get; }

    /// <summary>The resolved outbound cross-links from a concept.</summary>
    public IReadOnlyList<ResolvedLink> LinksFrom(ConceptId id) =>
        _outbound.TryGetValue(id, out var v) ? v : [];

    /// <summary>The ids of concepts that link to the given concept ("cited by" / backlinks).</summary>
    public IReadOnlyList<ConceptId> Backlinks(ConceptId id) =>
        _backlinks.TryGetValue(id, out var v) ? v : [];

    /// <summary>
    /// All broken internal links in the bundle, as (source, raw target)
    /// pairs. Broken links are permitted by the spec (§5.3) — this is
    /// informational.
    /// </summary>
    public IReadOnlyList<(ConceptId Source, string RawTarget)> BrokenLinks()
    {
        var result = new List<(ConceptId Source, string RawTarget)>();
        foreach (var c in Concepts)
        {
            foreach (var link in LinksFrom(c.Id))
            {
                if (!link.Exists)
                {
                    result.Add((c.Id, link.Raw));
                }
            }
        }

        return result;
    }

    /// <summary>
    /// The declared OKF version from the bundle-root <c>index.md</c>
    /// frontmatter, if present (<c>okf_version</c>, §11). This is the only
    /// place frontmatter is permitted in an <c>index.md</c>.
    ///
    /// Computed once while <see cref="Load"/> builds the bundle and stored, so
    /// it reflects the same load-time snapshot as the rest of this
    /// <see cref="Bundle"/>: a later change to the root <c>index.md</c> on disk
    /// does not affect an already-loaded instance. A legitimate <c>null</c> (no
    /// root <c>index.md</c>, no <c>okf_version</c> key, or an unreadable file) is
    /// a stored value like any other. The backing field is <c>readonly</c> and
    /// set in the constructor before the instance is published, so
    /// <see cref="Bundle"/> instances shared and read concurrently (e.g. across
    /// tool invocations in <c>OkfBundleTools</c>) need no lock.
    /// </summary>
    public string? OkfVersion => _okfVersion;

    private string? ComputeOkfVersion()
    {
        var rootIndex = System.IO.Path.Combine(Root, IndexFilename);
        string text;
        try
        {
            text = OkfEncodings.Strict.GetString(File.ReadAllBytes(rootIndex));
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        catch (System.Text.DecoderFallbackException)
        {
            // Any read failure here -- including non-UTF-8 content -- is
            // swallowed and yields null, unlike the concept-file read above
            // where the same failure aborts the whole Load. A missing or
            // unreadable root index.md simply means "no declared version".
            return null;
        }

        OkfDocument doc;
        try
        {
            doc = OkfDocument.Parse(text);
        }
        catch (DocumentParseException)
        {
            return null;
        }

        return doc.Frontmatter.Get("okf_version")?.AsDisplayString();
    }

    /// <summary>
    /// Resolves a §6.2 path-valued frontmatter raw value (as enumerated by
    /// <see cref="OkfDocument.FrontmatterResources"/>) to a filesystem path,
    /// relative to <paramref name="concept"/>.
    ///
    /// A <see cref="FrontmatterResourceKind.Url"/> value (<c>scheme://...</c>)
    /// is never resolved: <paramref name="absolutePath"/> is <c>null</c> and
    /// <paramref name="status"/> is <see cref="ResourceResolutionStatus.Url"/>.
    ///
    /// Otherwise the candidate path is computed and checked for containment
    /// within the bundle root: a <see cref="FrontmatterResourceKind.BundleRelative"/>
    /// value (leading <c>/</c> or <c>\</c>) is combined with <see cref="Root"/>
    /// after stripping the leading separator(s) -- <see cref="Path.Combine(string, string)"/>
    /// otherwise discards <paramref name="concept"/>'s directory (or, here,
    /// <see cref="Root"/>) whenever the second argument looks rooted, which on
    /// Windows an absolute-looking <c>/x</c> does. A
    /// <see cref="FrontmatterResourceKind.Relative"/> value is combined with
    /// the concept's own directory instead.
    ///
    /// The candidate is <see cref="ResourceResolutionStatus.Unsafe"/> if it
    /// would escape the bundle root, or if it (or any ancestor directory up to
    /// the root) is a filesystem reparse point -- see
    /// <see cref="ReparsePoints.IsWithinBundleRoot"/>,
    /// <see cref="ReparsePoints.IsReparsePoint"/>, and
    /// <see cref="ReparsePoints.HasReparsePointAncestor(string, string)"/>.
    /// Otherwise it is <see cref="ResourceResolutionStatus.Resolved"/> if the
    /// file exists, or <see cref="ResourceResolutionStatus.Missing"/> if it
    /// does not.
    ///
    /// Always returns <c>true</c>: resolution never fails outright (§3,
    /// permissive), it only reports which of the above statuses applies.
    /// </summary>
    public bool TryResolveResource(Concept concept, string rawPath, out string? absolutePath, out ResourceResolutionStatus status)
    {
        if (FrontmatterResourceClassifier.KindOf(rawPath) == FrontmatterResourceKind.Url)
        {
            absolutePath = null;
            status = ResourceResolutionStatus.Url;
            return true;
        }

        string candidate;
        if (rawPath.Length > 0 && (rawPath[0] == '/' || rawPath[0] == '\\'))
        {
            // BundleRelative: strip the leading separator(s) BEFORE combining
            // with Root -- Path.Combine(root, "/x") discards `root` entirely
            // on Windows, since "/x" looks rooted to it.
            var stripped = rawPath.TrimStart('/', '\\');
            candidate = Path.GetFullPath(Path.Combine(Root, stripped));
        }
        else
        {
            var conceptDir = Path.GetDirectoryName(concept.Path) ?? Root;
            candidate = Path.GetFullPath(Path.Combine(conceptDir, rawPath));
        }

        var fullRoot = ReparsePoints.CanonicalizeRoot(Root);
        if (!ReparsePoints.IsWithinBundleRoot(fullRoot, candidate)
            || ReparsePoints.IsReparsePoint(candidate)
            || ReparsePoints.HasReparsePointAncestor(fullRoot, candidate))
        {
            // Deliberately null, like the Url case: an Unsafe candidate must
            // never be handed to ReadResourceText, so it is never exposed
            // even though it was computed above.
            absolutePath = null;
            status = ResourceResolutionStatus.Unsafe;
            return true;
        }

        absolutePath = candidate;
        status = File.Exists(candidate) ? ResourceResolutionStatus.Resolved : ResourceResolutionStatus.Missing;
        return true;
    }

    /// <summary>
    /// Reads a resolved resource's text content as strict UTF-8 (throws on
    /// invalid byte sequences rather than substituting U+FFFD). Intended to be
    /// called only on an <paramref name="absolutePath"/> produced by
    /// <see cref="TryResolveResource"/> with
    /// <see cref="ResourceResolutionStatus.Resolved"/> -- path safety is
    /// established there, not here.
    /// </summary>
    public string ReadResourceText(string absolutePath) => File.ReadAllText(absolutePath, OkfEncodings.Strict);

    /// <summary>
    /// Recursively collects <c>*.md</c> file paths under <paramref name="dir"/>,
    /// in deterministic order (directory entries sorted by name, ordinal).
    /// </summary>
    private static void CollectMarkdown(string dir, List<string> output)
    {
        var entries = Directory.GetFileSystemEntries(dir)
            .Select(System.IO.Path.GetFileName)
            .Where(name => name is not null)
            .Select(name => name!)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        foreach (var name in entries)
        {
            var path = System.IO.Path.Combine(dir, name);

            // lstat-based semantics: a symlink or junction is neither a
            // directory nor a file, so it matches neither arm below and is
            // skipped outright -- never recursed into, never collected even
            // if it has a `.md` name. Directory.Exists/File.Exists would
            // instead follow the link, so reparse points are excluded
            // explicitly here to keep bundle walks from crossing them.
            if (ReparsePoints.IsReparsePoint(path))
            {
                continue;
            }

            if (Directory.Exists(path))
            {
                CollectMarkdown(path, output);
            }
            else if (File.Exists(path) && MarkdownPaths.HasMarkdownExtension(path))
            {
                output.Add(path);
            }
        }
    }

    /// <summary>
    /// Builds the outbound link and backlink maps for all concepts.
    /// </summary>
    private static (Dictionary<ConceptId, List<ResolvedLink>> Outbound, Dictionary<ConceptId, List<ConceptId>> Backlinks) BuildGraph(
        List<Concept> concepts,
        Dictionary<ConceptId, int> index)
    {
        var outbound = new Dictionary<ConceptId, List<ResolvedLink>>();
        var backlinks = new Dictionary<ConceptId, List<ConceptId>>();

        foreach (var c in concepts)
        {
            var resolved = new List<ResolvedLink>();
            foreach (var link in c.Document.Links())
            {
                var target = link.Resolve(c.Id);
                if (target is null)
                {
                    continue;
                }

                var exists = index.ContainsKey(target);
                if (exists)
                {
                    if (!backlinks.TryGetValue(target, out var entry))
                    {
                        entry = [];
                        backlinks[target] = entry;
                    }

                    if (!entry.Contains(c.Id))
                    {
                        entry.Add(c.Id);
                    }
                }

                resolved.Add(new ResolvedLink(target, exists, link.Text, link.Target));
            }

            outbound[c.Id] = resolved;
        }

        return (outbound, backlinks);
    }
}
