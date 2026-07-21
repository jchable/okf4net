namespace OKF4net;

/// <summary>
/// Error raised when loading or operating on a bundle on disk. Port of the
/// Rust <c>BundleError</c> (error.rs:46), restricted to the conditions
/// <see cref="Bundle.Load"/> itself can raise: I/O failures and a
/// non-directory root. Per-file parse failures are recorded in
/// <see cref="Bundle.ParseErrors"/> instead (loading is permissive, §9).
/// </summary>
public sealed class BundleLoadException : OkfException
{
    public BundleLoadException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// A single concept within a bundle (one markdown document). Port of the
/// Rust <c>Concept</c> (src/bundle.rs:22-30).
/// </summary>
public sealed record Concept(ConceptId Id, string Path, OkfDocument Document);

/// <summary>
/// A cross-link from one concept to another, after resolution (§5.3). Port
/// of the Rust <c>ResolvedLink</c> (src/bundle.rs:32-44).
/// </summary>
public sealed record ResolvedLink(ConceptId Target, bool Exists, string Text, string Raw);

/// <summary>
/// Loading and traversing an OKF *bundle*: a directory tree of markdown
/// files (§3). Port of the Rust <c>Bundle</c> (src/bundle.rs).
///
/// <see cref="Load"/> walks a directory, parses every non-reserved
/// <c>.md</c> file into a <see cref="Concept"/>, records the reserved
/// <c>index.md</c> / <c>log.md</c> files, and builds the cross-link graph
/// (§5). Loading is **permissive** by design (§9): files whose frontmatter
/// cannot be parsed are collected into <see cref="ParseErrors"/> rather than
/// aborting the load, and broken links are retained as edges to
/// non-existent concepts.
/// </summary>
public sealed class Bundle
{
    /// <summary>Reserved filenames with defined meaning at any level (§3.1). Port of <c>RESERVED_FILENAMES</c> (bundle.rs:19).</summary>
    public static readonly string[] ReservedFilenames = ["index.md", "log.md"];

    private readonly Dictionary<ConceptId, int> _index;
    private readonly Dictionary<ConceptId, List<ResolvedLink>> _outbound;
    private readonly Dictionary<ConceptId, List<ConceptId>> _backlinks;

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
    }

    /// <summary>
    /// Loads a bundle from a directory tree.
    ///
    /// Throws only for I/O failures or a non-directory root. Per-file parse
    /// failures are recorded in <see cref="ParseErrors"/>. Port of
    /// <c>Bundle::load</c> (bundle.rs:64-120).
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

        mdFiles.Sort(StringComparer.Ordinal);

        var concepts = new List<Concept>();
        var indexFiles = new List<string>();
        var logFiles = new List<string>();
        var parseErrors = new List<(string Path, string Error)>();

        foreach (var path in mdFiles)
        {
            var filename = System.IO.Path.GetFileName(path);
            switch (filename)
            {
                case "index.md":
                    indexFiles.Add(path);
                    break;
                case "log.md":
                    logFiles.Add(path);
                    break;
                default:
                {
                    string text;
                    try
                    {
                        text = File.ReadAllText(path);
                    }
                    catch (IOException e)
                    {
                        throw new BundleLoadException($"I/O error: {e.Message}");
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
    /// informational. Port of <c>Bundle::broken_links</c> (bundle.rs:181-191).
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
    /// place frontmatter is permitted in an <c>index.md</c>. Port of
    /// <c>Bundle::okf_version</c> (bundle.rs:196-203).
    /// </summary>
    public string? OkfVersion
    {
        get
        {
            var rootIndex = System.IO.Path.Combine(Root, "index.md");
            string text;
            try
            {
                text = File.ReadAllText(rootIndex);
            }
            catch (IOException)
            {
                return null;
            }
            catch (UnauthorizedAccessException)
            {
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
    }

    /// <summary>
    /// Recursively collects <c>*.md</c> file paths under <paramref name="dir"/>,
    /// in deterministic order (directory entries sorted by name, ordinal).
    /// Port of <c>collect_markdown</c> (bundle.rs:207-222).
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
            if (Directory.Exists(path))
            {
                CollectMarkdown(path, output);
            }
            else if (File.Exists(path) && path.EndsWith(".md", StringComparison.Ordinal))
            {
                output.Add(path);
            }
        }
    }

    /// <summary>
    /// Builds the outbound link and backlink maps for all concepts. Port of
    /// <c>build_graph</c> (bundle.rs:225-258).
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
