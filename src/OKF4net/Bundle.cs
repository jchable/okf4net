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
    private const string IndexFilename = "index.md";
    private const string LogFilename = "log.md";

    /// <summary>Reserved filenames with defined meaning at any level (§3.1). Port of <c>RESERVED_FILENAMES</c> (bundle.rs:19).</summary>
    public static readonly string[] ReservedFilenames = [IndexFilename, LogFilename];

    /// <summary>
    /// UTF-8 decoder configured to throw on invalid byte sequences (no
    /// U+FFFD replacement, no BOM emission), matching the strictness of
    /// Rust's <c>fs::read_to_string</c> (which fails with an
    /// <c>io::Error</c> of kind <c>InvalidData</c> — message "stream did not
    /// contain valid UTF-8" — for any file that is not valid UTF-8).
    /// <see cref="File.ReadAllText(string)"/> is deliberately not used here:
    /// it silently substitutes U+FFFD for invalid bytes instead of failing.
    /// </summary>
    private static readonly System.Text.UTF8Encoding StrictUtf8 =
        new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

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

        mdFiles.Sort(ComparePathsComponentWise);

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
                        text = StrictUtf8.GetString(File.ReadAllBytes(path));
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
                        // Mirrors the io::Error kind ErrorKind::InvalidData
                        // that Rust's fs::read_to_string produces for
                        // non-UTF-8 input, propagated by `?` (bundle.rs:88)
                        // and aborting the whole load — same as any other
                        // I/O failure during the walk.
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
            var rootIndex = System.IO.Path.Combine(Root, IndexFilename);
            string text;
            try
            {
                text = StrictUtf8.GetString(File.ReadAllBytes(rootIndex));
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
                // Mirrors `fs::read_to_string(&root_index).ok()?` (bundle.rs:198):
                // any read failure -- including non-UTF-8 content -- is
                // swallowed and yields None, unlike the concept-file read
                // above where the same failure aborts the whole Load.
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
    /// Compares two absolute file paths component-by-component (splitting
    /// on <c>\</c> and <c>/</c>), ordinal per segment, with a shorter
    /// segment list sorting first when one path is a prefix of the other.
    /// Mirrors Rust's <c>PathBuf</c>'s derived <c>Ord</c> (which compares
    /// via the <c>Component</c> iterator, not raw bytes) — used for the
    /// final <c>md_files.sort()</c> in <c>Bundle::load</c> (bundle.rs:72).
    ///
    /// A flat ordinal string comparison of full paths is NOT equivalent: on
    /// Windows, <c>'.'</c> (0x2E) sorts before <c>'\'</c> (0x5C), so a raw
    /// string sort would place <c>orders.md</c> before <c>orders\extra.md</c>
    /// even though the directory <c>orders</c> should sort before the
    /// sibling file <c>orders.md</c> — inverting the DFS walk order that
    /// <see cref="CollectMarkdown"/> already produced.
    /// </summary>
    private static int ComparePathsComponentWise(string a, string b)
    {
        var segmentsA = a.Split('\\', '/');
        var segmentsB = b.Split('\\', '/');
        var n = Math.Min(segmentsA.Length, segmentsB.Length);
        for (var i = 0; i < n; i++)
        {
            var cmp = string.CompareOrdinal(segmentsA[i], segmentsB[i]);
            if (cmp != 0)
            {
                return cmp;
            }
        }

        return segmentsA.Length.CompareTo(segmentsB.Length);
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
