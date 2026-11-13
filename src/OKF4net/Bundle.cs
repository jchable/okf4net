// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Collections.ObjectModel;
using System.Runtime.ExceptionServices;
using OKF4net.Internal;
using OKF4net.Providers;

namespace OKF4net;

/// <summary>
/// Error raised by <see cref="Bundle.Load(string)"/> and
/// <see cref="Bundle.Load(IBundleProvider, BundleLoadOptions?)"/>, and only by them: a root
/// that is not a directory (the <see cref="string"/> overload); an I/O error while listing
/// the bundle, taking its version or reading a concept; a concept that is not valid UTF-8;
/// a provider failure or refusal (a path through a reparse point or an entry that cannot be
/// inspected, a root outside its confinement boundary, an object gone or changed while it
/// was read); or a global bound exceeded (entries, depth). The message is library-authored
/// (spec §7); the raw cause, when there is one, is the inner exception. Per-file problems —
/// a rejected name, an oversized or unparseable concept — are recorded in
/// <see cref="Bundle.ParseProblems"/> instead (loading is permissive, §11).
/// </summary>
public sealed class BundleLoadException : OkfException
{
    /// <summary>Creates the exception with a descriptive message.</summary>
    public BundleLoadException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with the raw provider failure as <see cref="Exception.InnerException"/> (the host-log channel, spec §7).</summary>
    public BundleLoadException(string message, Exception inner)
        : base(message, inner)
    {
    }
}

/// <summary>
/// A single concept within a bundle (one markdown document).
/// </summary>
/// <remarks>
/// Equality, hashing and <see cref="object.ToString"/> cover the three positional members
/// only, as they did before <see cref="Key"/> existed: a loaded concept equals a hand-built
/// one with the same id, path and document. <c>with</c> still copies <see cref="Key"/>.
/// </remarks>
public sealed record Concept(ConceptId Id, string Path, OkfDocument Document)
{
    /// <summary>The concept's key in its provider; set by <see cref="Bundle.Load(IBundleProvider, BundleLoadOptions?)"/>, <see langword="null"/> for a hand-built instance.</summary>
    public BundleKey? Key { get; init; }

    /// <summary>Compares the three positional members (<see cref="Id"/>, <see cref="Path"/>, <see cref="Document"/>), never <see cref="Key"/>.</summary>
    public bool Equals(Concept? other) =>
        ReferenceEquals(this, other)
        || (other is not null
            && EqualityContract == other.EqualityContract
            && EqualityComparer<ConceptId>.Default.Equals(Id, other.Id)
            && EqualityComparer<string>.Default.Equals(Path, other.Path)
            && EqualityComparer<OkfDocument>.Default.Equals(Document, other.Document));

    /// <summary>Hashes the three positional members, as the compiler-synthesised hash did before <see cref="Key"/> existed.</summary>
    public override int GetHashCode() => unchecked(
        (((EqualityComparer<Type>.Default.GetHashCode(EqualityContract) * -1521134295)
            + EqualityComparer<ConceptId>.Default.GetHashCode(Id)) * -1521134295
            + EqualityComparer<string>.Default.GetHashCode(Path)) * -1521134295
            + EqualityComparer<OkfDocument>.Default.GetHashCode(Document));

    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        builder.Append("Id = ");
        builder.Append(Id);
        builder.Append(", Path = ");
        builder.Append(Path);
        builder.Append(", Document = ");
        builder.Append(Document);
        return true;
    }
}

/// <summary>
/// A cross-link from one concept to another, after resolution (§6.1).
/// </summary>
public sealed record ResolvedLink(ConceptId Target, bool Exists, string Text, string Raw);

/// <summary>
/// Loading and traversing an OKF *bundle*: a directory tree of markdown
/// files (§3).
///
/// <see cref="Load(string)"/> walks a directory, parses every non-reserved
/// <c>.md</c> file into a <see cref="Concept"/>, records the reserved
/// <c>index.md</c> / <c>log.md</c> files, and builds the cross-link graph
/// (§6). Loading is **permissive** by design (§11): files whose frontmatter
/// cannot be parsed are collected into <see cref="ParseProblems"/> rather than
/// aborting the load, and broken links are retained as edges to
/// non-existent concepts.
/// </summary>
public sealed class Bundle
{
    private const string IndexFilename = "index.md";
    private const string LogFilename = "log.md";

    /// <summary>
    /// The comparison used for the §6.2 bundle-root containment check in
    /// <see cref="TryResolveResource"/>. Deliberately
    /// <see cref="StringComparison.Ordinal"/> on EVERY platform. Case-sensitivity
    /// is a runtime property of the specific volume, NOT of the OS: APFS/HFS+ can
    /// be mounted case-sensitive on macOS, and Windows has case-sensitive
    /// directories (per-directory flag, ReFS, WSL) -- so an OS-based heuristic
    /// (e.g. "OrdinalIgnoreCase on Windows/macOS") leaves this security boundary
    /// bypassable on those volumes, exactly as on Linux. Ordinal rejects only a
    /// candidate whose root-prefix differs from <see cref="Root"/> in CASE; since
    /// a legitimate candidate is built from <see cref="Root"/> / <c>concept.Path</c>
    /// (and <see cref="Path.GetFullPath(string)"/> preserves case), that can happen
    /// only via a <c>..</c> climb re-entering a case-variant sibling of the root --
    /// precisely the escape §6.2 must block. Legitimate paths keep Root's exact
    /// case and are unaffected. See <see cref="ReparsePoints.IsWithin"/>'s remarks.
    /// </summary>
    private static readonly StringComparison PathComparison = StringComparison.Ordinal;

    /// <summary>Reserved filenames with defined meaning at any level (§3.1).</summary>
    public static readonly string[] ReservedFilenames = [IndexFilename, LogFilename];

    private readonly Dictionary<ConceptId, int> _index;
    private readonly Dictionary<ConceptId, List<ResolvedLink>> _outbound;
    private readonly Dictionary<ConceptId, List<ConceptId>> _backlinks;
    private readonly string? _okfVersion;

    private Bundle(
        IBundleProvider provider,
        BundleLoadOptions options,
        BundleKeyValidator validator,
        BundleVersion version,
        string? okfVersion,
        List<Concept> concepts,
        Dictionary<ConceptId, int> index,
        List<BundleKey> indexKeys,
        List<BundleKey> logKeys,
        List<BundleParseError> parseProblems,
        HashSet<BundleKey> rejectedKeys,
        List<BundleKey> rejectedPrefixes,
        List<BundlePortabilityIssue> portabilityIssues,
        Dictionary<BundleKey, BundleObjectVersion> objectVersions,
        Dictionary<ConceptId, List<ResolvedLink>> outbound,
        Dictionary<ConceptId, List<ConceptId>> backlinks)
    {
        Provider = provider;
        Options = options;
        _validator = validator;
        Version = version;
        _okfVersion = okfVersion;
        Concepts = concepts;
        _index = index;
        IndexKeys = indexKeys;
        LogKeys = logKeys;
        ParseProblems = parseProblems;
#pragma warning disable CS0618 // the display-path projections are kept, obsolete, until their removal (spec §10)
        IndexFiles = indexKeys.Select(k => BundleDisplayPath.Compose(provider.Location, k)).ToList();
        LogFiles = logKeys.Select(k => BundleDisplayPath.Compose(provider.Location, k)).ToList();
        ParseErrors = parseProblems.Select(p => (DisplayPathOf(provider.Location, p), p.Error)).ToList();
#pragma warning restore CS0618
        RejectedKeys = rejectedKeys;
        RejectedPrefixes = rejectedPrefixes;
        _rejectedPrefixFolds = rejectedPrefixes.Select(validator.FoldKeyText).ToHashSet(StringComparer.Ordinal);
        PortabilityIssues = portabilityIssues;
        ObjectVersions = objectVersions;
        _outbound = outbound;
        _backlinks = backlinks;
    }

    /// <summary>
    /// The display path of a load problem (spec §8): the key's path when the name is a key,
    /// otherwise the raw name as listed, unquoted, with <c>/</c> turned into the OS separator —
    /// the same <c>Path.Combine(root, …)</c> string the loader printed before the provider
    /// seam, whatever the reason the name was rejected. Escaping, when an output needs it,
    /// is that output's own (the JSON writer, the site's HTML encoding), never a second layer here.
    /// </summary>
    private static string DisplayPathOf(string location, BundleParseError problem)
    {
        if ((problem.Key ?? (BundleKey.TryParse(problem.RawName, out var parsed, out _) ? parsed : null)) is { } key)
        {
            return BundleDisplayPath.Compose(location, key);
        }

        return System.IO.Path.Combine(location, problem.RawName.Replace('/', System.IO.Path.DirectorySeparatorChar));
    }

    /// <summary>
    /// The display path of one of this bundle's <see cref="ParseProblems"/> (spec §8): the
    /// <see cref="Location"/>, then the key's segments joined by the OS separator when the name
    /// is a key, else the raw name as listed with <c>/</c> turned into the OS separator — the
    /// same string the loader printed for an unparseable file before the provider seam. The one
    /// text every output (the CLI, its JSON, the viewer's index page) prints for a problem; it
    /// escapes nothing, that is each output's own job.
    /// </summary>
    /// <param name="problem">One of <see cref="ParseProblems"/> (or any problem to display under this bundle's location).</param>
    public string DisplayPath(BundleParseError problem)
    {
        ArgumentNullException.ThrowIfNull(problem);
        return DisplayPathOf(Location, problem);
    }

    /// <summary>
    /// Loads a bundle from a directory tree through the default
    /// <see cref="FileSystemBundleProvider"/> (spec §8).
    ///
    /// Throws for a non-directory root, an I/O failure, a concept that is not valid UTF-8, a
    /// global bound exceeded (<see cref="BundleLimits.Default"/>: entries, depth) and a refusal
    /// by the provider's STRICT guards — a directory under the root whose link status cannot be
    /// inspected aborts the load before it is listed. A symlink or junction under the root is
    /// skipped, never followed. Per-file problems — a rejected name, an oversized or
    /// unparseable concept — are recorded in <see cref="ParseProblems"/>.
    /// </summary>
    /// <exception cref="BundleLoadException"><paramref name="root"/> does not exist or is not a directory, an I/O error occurred, a concept is not valid UTF-8, a global bound was exceeded, or the provider refused an access.</exception>
    public static Bundle Load(string root)
    {
        if (!Directory.Exists(root))
        {
            throw new BundleLoadException($"bundle root is not a directory: {root}");
        }

        return Load(new FileSystemBundleProvider(root), null);
    }

    /// <summary>
    /// Loads a bundle through <paramref name="provider"/> (spec §1, §2, §6): consumes the
    /// listing under the core's own entry, depth and key-length bounds, validates the names
    /// as a tree under <paramref name="options"/>' policy, reads every <c>.md</c> object within
    /// the byte bound, parses the concepts and builds the link graph. Permissive (§11): a
    /// rejected name, an oversized or unparseable concept goes to <see cref="ParseProblems"/>;
    /// only a provider failure or a global bound (entries, depth) throws.
    /// </summary>
    /// <param name="provider">Where the bundle is read.</param>
    /// <param name="options">The key policy, extra validators and bounds, or <see langword="null"/> for <see cref="BundleLoadOptions.Default"/>.</param>
    /// <param name="ct">Cancels the load: it ends with an <see cref="OperationCanceledException"/>, never a <see cref="BundleLoadException"/>.</param>
    /// <exception cref="BundleLoadException">The provider failed, refused the access, or a global bound was exceeded. The raw cause is the inner exception.</exception>
    public static ValueTask<Bundle> LoadAsync(IBundleProvider provider, BundleLoadOptions? options = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(provider);
        return LoadCoreAsync(provider, options, ct, (options ?? BundleLoadOptions.Default).Limits.MaxConcurrentRequests);
    }

    /// <summary>
    /// The load, reading the concepts with at most <paramref name="readDegree"/> reads in flight (the
    /// asynchronous spelling uses <see cref="BundleLimits.MaxConcurrentRequests"/>; the synchronous one,
    /// 1: its path is the sequential one, so what the CLI prints never depends on a scheduler).
    /// Whatever the degree, the outcome is the same: concepts in key order, and a failure
    /// that ends the load names the failing key of the smallest rank.
    /// </summary>
    private static async ValueTask<Bundle> LoadCoreAsync(IBundleProvider provider, BundleLoadOptions? options, CancellationToken ct, int readDegree)
    {
        options ??= BundleLoadOptions.Default;
        var limits = options.Limits;
        var validator = new BundleKeyValidator(provider.NativeConstraints, options.KeyPolicy, options.ExtraKeyValidators);

        // One provider scope for the whole load: a provider that supports it lists each
        // directory once for the listing, the version and every read, and keeps nothing past
        // this call (FileSystemBundleProvider's BeginScope says what is and is not reused).
        using var scope = ProviderScopes.Begin(provider);

        List<string> treeInput;
        List<string> overLength;
        try
        {
            (treeInput, overLength) = await ListNamesAsync(provider, limits, ct).ConfigureAwait(false);
        }
        catch (BundleProviderException e)
        {
            throw Translate(e, null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new BundleLoadException($"I/O error while listing the bundle ({e.GetType().Name})", e);
        }

        var parseProblems = new List<BundleParseError>();
        var rejectedKeys = new HashSet<BundleKey>();
        foreach (var raw in overLength)
        {
            BundleKey.TryParse(raw, out var longKey, out _);
            if (longKey is not null)
            {
                rejectedKeys.Add(longKey);
            }

            if (MarkdownPaths.HasMarkdownExtension(raw))
            {
                parseProblems.Add(new BundleParseError(raw, longKey, $"key is longer than the {limits.MaxKeyBytes}-byte bound"));
            }
        }

        BundleVersion version;
        try
        {
            version = await provider.GetVersionAsync(limits, ct).ConfigureAwait(false);
        }
        catch (BundleProviderException e)
        {
            throw Translate(e, null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new BundleLoadException($"I/O error while reading the bundle version ({e.GetType().Name})", e);
        }

        var tree = validator.ValidateTree(treeInput, overLength);
        foreach (var rejection in tree.Rejected)
        {
            if (BundleKey.TryParse(rejection.RawName, out var rejectedKey, out _))
            {
                rejectedKeys.Add(rejectedKey);
            }

            // Only a .md name is diagnosed (spec §2, enumeration scope); other names were never examined.
            if (MarkdownPaths.HasMarkdownExtension(rejection.RawName))
            {
                parseProblems.Add(new BundleParseError(rejection.RawName, null, rejection.Reason));
            }
        }

        var mdKeys = tree.Accepted.Where(k => k.IsMarkdown).ToList();
        mdKeys.Sort((a, b) => PathOrdering.CompareComponentWise(a.ToString(), b.ToString()));
        var portabilityIssues = FindPortabilityIssues(provider, options, treeInput, overLength, mdKeys);

        var concepts = new List<Concept>();
        var indexKeys = new List<BundleKey>();
        var logKeys = new List<BundleKey>();
        var objectVersions = new Dictionary<BundleKey, BundleObjectVersion>();
        var chunk = new byte[ReadChunkBytes]; // one copy buffer for every object of this load
        var prefetched = await PrefetchConceptsAsync(provider, mdKeys.Where(k => k.Name is not (IndexFilename or LogFilename)).ToList(), limits, readDegree, ct).ConfigureAwait(false);

        foreach (var key in mdKeys)
        {
            switch (key.Name)
            {
                case IndexFilename:
                    indexKeys.Add(key);
                    break;
                case LogFilename:
                    logKeys.Add(key);
                    break;
                default:
                    {
                        string text;
                        try
                        {
                            ct.ThrowIfCancellationRequested();
                            (string Text, BundleObjectVersion Version) read;
                            if (prefetched.TryGetValue(key, out var ahead))
                            {
                                ahead.Failure?.Throw(); // the failure of this object, at its own rank: the loop meets the lowest first
                                read = (ahead.Text!, ahead.Version);
                            }
                            else
                            {
                                read = await ReadTextAtAsync(provider, key, limits, chunk, null, ct).ConfigureAwait(false);
                            }

                            text = read.Text;
                            objectVersions[key] = read.Version;
                        }
                        catch (BundleObjectTooLargeException e)
                        {
                            parseProblems.Add(new BundleParseError(key.ToString(), key, e.Message));
                            break;
                        }
                        catch (BundleProviderException e)
                        {
                            throw Translate(e, key);
                        }
                        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                        {
                            throw new BundleLoadException($"I/O error reading '{key}' ({e.GetType().Name})", e);
                        }
                        catch (System.Text.DecoderFallbackException)
                        {
                            throw new BundleLoadException("I/O error: stream did not contain valid UTF-8");
                        }

                        OkfDocument document;
                        try
                        {
                            document = OkfDocument.Parse(text);
                        }
                        catch (DocumentParseException e)
                        {
                            parseProblems.Add(new BundleParseError(key.ToString(), key, e.Message));
                            break;
                        }

                        try
                        {
                            var id = ConceptId.FromKey(key);
                            concepts.Add(new Concept(id, BundleDisplayPath.Compose(provider.Location, key), document) { Key = key });
                        }
                        catch (ConceptIdException e)
                        {
                            parseProblems.Add(new BundleParseError(key.ToString(), key, $"Missing required frontmatter keys: {e.Message}"));
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
        var okfVersion = await ComputeOkfVersionAsync(provider, limits, indexKeys, chunk, ct).ConfigureAwait(false);

        // One path order for every problem, whatever stage found it (a bound, the tree
        // validation, the read, the parse): the order the loader before the seam reported its
        // errors in — every `.md` path sorted component-wise, `/` and `\` both separating —
        // so a rejected name sits among the others instead of ahead of them. Stable: two
        // names that compare equal (`a\b.md` and `a/b.md`) keep the order they were found in.
        parseProblems = [.. parseProblems.OrderBy(p => p.RawName, Comparer<string>.Create(PathOrdering.CompareComponentWise))];

        var bundle = new Bundle(provider, options, validator, version, okfVersion, concepts, index, indexKeys, logKeys, parseProblems, rejectedKeys, tree.RejectedPrefixes.ToList(), portabilityIssues, objectVersions, outbound, backlinks);

        // The executable resources of every §10 concept are read and hashed now (spec §5.1),
        // inside the load's scope, so that an execution can later be pinned to the version
        // this image was loaded with. A resource's own problem is a state, never a load failure.
        await bundle.SurveyExecutableResourcesAsync(chunk, ct).ConfigureAwait(false);
        return bundle;
    }

    /// <summary>
    /// The synchronous load over a provider: <see cref="LoadAsync"/> through the non-blocking
    /// bridge (#152), for the code that has not become asynchronous. It never waits: a load that
    /// is still pending when it returns is cancelled and abandoned, and the call throws
    /// <see cref="InvalidOperationException"/> naming the async overload. Not public API: a host
    /// loads through <see cref="LoadAsync"/>, or <see cref="Load(string)"/> for a directory.
    /// </summary>
    internal static Bundle Load(IBundleProvider provider, BundleLoadOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(provider);
        return SyncBridge.RunCancellable(ct => LoadCoreAsync(provider, options, ct, readDegree: 1));
    }

    /// <summary>What reading one concept object gave: its text and version, or the failure the loop will meet at its rank.</summary>
    private readonly record struct PrefetchedRead(string? Text, BundleObjectVersion Version, ExceptionDispatchInfo? Failure);

    /// <summary>
    /// Reads the concept objects with at most <paramref name="degree"/> reads in flight, each into the
    /// slot of its own key (#152). Empty — the loop reads inline, as the synchronous load always did —
    /// when the degree is 1 or there is nothing to overlap. Reads START in key order and none is
    /// started once a lower-ranked one has failed with a failure that ends the load, so the loop, which
    /// meets the slots in key order, finds the smallest rank first: the error text never depends on
    /// which read finished first. A per-object failure that is only a parse problem (an object over the
    /// byte bound) does not stop the starting. A cancellation of the caller's token propagates once every
    /// started read has ended.
    /// </summary>
    private static async ValueTask<Dictionary<BundleKey, PrefetchedRead>> PrefetchConceptsAsync(IBundleProvider provider, List<BundleKey> keys, BundleLimits limits, int degree, CancellationToken ct)
    {
        var outcomes = new Dictionary<BundleKey, PrefetchedRead>();
        if (degree <= 1 || keys.Count <= 1)
        {
            return outcomes;
        }

        using var slots = new SemaphoreSlim(Math.Min(degree, keys.Count));
        var lowestFailed = int.MaxValue;
        var started = new List<(BundleKey Key, Task<PrefetchedRead> Read)>(keys.Count);
        ExceptionDispatchInfo? cancelled = null;
        try
        {
            for (var rank = 0; rank < keys.Count; rank++)
            {
                await slots.WaitAsync(ct).ConfigureAwait(false);
                if (Volatile.Read(ref lowestFailed) < rank)
                {
                    slots.Release();
                    break;
                }

                started.Add((keys[rank], ReadOneAsync(keys[rank], rank)));
            }
        }
        catch (OperationCanceledException e) when (ct.IsCancellationRequested)
        {
            cancelled = ExceptionDispatchInfo.Capture(e);
        }

        // Every started read ends before this method returns or throws: the provider scope and the
        // semaphore outlive them, and the caller never sees the load come back while the host's reads still run.
        foreach (var (key, read) in started)
        {
            try
            {
                outcomes[key] = await read.ConfigureAwait(false);
            }
            catch (OperationCanceledException e) when (ct.IsCancellationRequested)
            {
                cancelled ??= ExceptionDispatchInfo.Capture(e);
            }
        }

        cancelled?.Throw();
        return outcomes;

        async Task<PrefetchedRead> ReadOneAsync(BundleKey key, int rank)
        {
            var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(ReadChunkBytes);
            try
            {
                var read = await ReadTextAtAsync(provider, key, limits, buffer, null, ct).ConfigureAwait(false);
                return new PrefetchedRead(read.Text, read.Version, null);
            }
            catch (Exception e) when (e is not OperationCanceledException && e is not OutOfMemoryException)
            {
                if (e is not BundleObjectTooLargeException)
                {
                    int seen;
                    while (rank < (seen = Volatile.Read(ref lowestFailed)) && Interlocked.CompareExchange(ref lowestFailed, rank, seen) != seen)
                    {
                    }
                }

                return new PrefetchedRead(null, default, ExceptionDispatchInfo.Capture(e));
            }
            finally
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
                slots.Release();
            }
        }
    }

    /// <summary>
    /// Surveys the <c>computation</c> file and the <c>attester.resource</c> of every Attested
    /// Computation concept (spec §5.1): each is resolved like any §6.2 value
    /// (<see cref="TryResolveResourceKey"/>) and, when it resolves, read through the one bounded
    /// reader so that its version is known. The rule is local: a missing, unreadable, oversized
    /// or refused resource is a state of that resource; a global failure (a bound of spec §6, a
    /// provider refusal that is not the resource's own path) still fails the load, as it would
    /// without the survey (spec §5.1, revision 6).
    /// </summary>
    private async ValueTask SurveyExecutableResourcesAsync(byte[] chunk, CancellationToken ct)
    {
        // One read per KEY per load (design decision 17): N concepts sharing one file cost one read.
        var readKeys = new Dictionary<BundleKey, (ExecutableResourceState State, BundleObjectVersion? Version)>();
        foreach (var concept in Concepts)
        {
            var frontmatter = concept.Document.Frontmatter;
            if (!frontmatter.IsAttestedComputation)
            {
                continue;
            }

            var contract = frontmatter.ComputationContract;
            _executableResources[concept.Id] = new ExecutableResourceSurvey(
                await SurveyResourceAsync(concept, "computation", contract.ComputationPath, chunk, readKeys, ct).ConfigureAwait(false),
                await SurveyResourceAsync(concept, "attester.resource", contract.Attester?.Resource, chunk, readKeys, ct).ConfigureAwait(false));
        }

        ExecutableResources = new ReadOnlyDictionary<ConceptId, ExecutableResourceSurvey>(_executableResources);
    }

    private async ValueTask<ExecutableResource> SurveyResourceAsync(Concept concept, string field, string? rawPath, byte[] chunk, Dictionary<BundleKey, (ExecutableResourceState State, BundleObjectVersion? Version)> readKeys, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(rawPath))
        {
            return new ExecutableResource(field, null, ExecutableResourceState.Undeclared, null, null);
        }

        var (key, status) = await ResolveResourceKeyAsync(concept, rawPath, ct).ConfigureAwait(false);
        switch (status)
        {
            case ResourceResolutionStatus.Url:
                return new ExecutableResource(field, rawPath, ExecutableResourceState.Url, null, null);
            case ResourceResolutionStatus.Unsafe:
                return new ExecutableResource(field, rawPath, ExecutableResourceState.Unsafe, null, null);
            case ResourceResolutionStatus.Missing:
                return new ExecutableResource(field, rawPath, ExecutableResourceState.Missing, null, null);
        }

        if (readKeys.TryGetValue(key!, out var known))
        {
            return new ExecutableResource(field, rawPath, known.State, key, known.Version);
        }

        var result = await ReadForSurveyAsync(key!, chunk, ct).ConfigureAwait(false);
        readKeys[key!] = (result.State, result.Version);
        return new ExecutableResource(field, rawPath, result.State, key, result.Version);
    }

    private async ValueTask<(ExecutableResourceState State, BundleObjectVersion? Version)> ReadForSurveyAsync(BundleKey key, byte[] chunk, CancellationToken ct)
    {
        try
        {
            var read = await ReadTextAtAsync(Provider, key, Options.Limits, chunk, null, ct).ConfigureAwait(false);
            return (ExecutableResourceState.Pinned, read.Version);
        }
        catch (BundleObjectTooLargeException)
        {
            return (ExecutableResourceState.TooLarge, null);
        }
        catch (BundleObjectNotFoundException)
        {
            return (ExecutableResourceState.Missing, null);
        }
        catch (BundleAccessRefusedException)
        {
            return (ExecutableResourceState.Unsafe, null);
        }
        catch (BundleProviderException e)
        {
            throw Translate(e, key); // a bound or a global refusal: the load fails, as without the survey
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.DecoderFallbackException)
        {
            return (ExecutableResourceState.Unreadable, null);
        }
    }

    /// <summary>
    /// The second, portability pass of a load under a policy other than
    /// <see cref="BundleKeyPolicy.Strict"/> (spec §2, layer 3): the same raw names — the
    /// unadmitted ones included — through a <see cref="BundleKeyPolicy.Strict"/> validator with
    /// the same provider constraints and extra validators. Every accepted <c>.md</c> key in
    /// <paramref name="acceptedMarkdown"/> that it refuses, by <see cref="BundleKeyValidator.CheckKey"/>
    /// or by a tree rejection, gives one issue with its reason, in the order of
    /// <paramref name="acceptedMarkdown"/>. One more linear tree validation over names already
    /// in memory: no provider I/O. Empty under <see cref="BundleKeyPolicy.Strict"/>.
    /// </summary>
    private static List<BundlePortabilityIssue> FindPortabilityIssues(IBundleProvider provider, BundleLoadOptions options, List<string> treeInput, List<string> overLength, List<BundleKey> acceptedMarkdown)
    {
        var issues = new List<BundlePortabilityIssue>();
        if (options.KeyPolicy == BundleKeyPolicy.Strict || acceptedMarkdown.Count == 0)
        {
            return issues;
        }

        var strict = new BundleKeyValidator(provider.NativeConstraints, BundleKeyPolicy.Strict, options.ExtraKeyValidators);
        var reasons = new Dictionary<BundleKey, string>();
        foreach (var rejection in strict.ValidateTree(treeInput, overLength).Rejected)
        {
            if (BundleKey.TryParse(rejection.RawName, out var key, out _))
            {
                reasons.TryAdd(key, rejection.Reason);
            }
        }

        foreach (var key in acceptedMarkdown)
        {
            if (reasons.TryGetValue(key, out var reason))
            {
                issues.Add(new BundlePortabilityIssue(key, reason));
            }
        }

        return issues;
    }

    /// <summary>
    /// Builds the public message of a provider failure from the exception's type and fields
    /// only (spec §7): a provider may have put storage internals in its message, which stays
    /// on the inner exception for the host's log.
    /// </summary>
    private static BundleLoadException Translate(BundleProviderException e, BundleKey? key) => e switch
    {
        BundleLimitExceededException l => new BundleLoadException($"the bundle exceeds the {BundleLimitExceededException.Describe(l.Bound)} bound ({l.Limit})", e),
        BundleAccessRefusedException r => new BundleLoadException(
            (r.Key ?? key) is { } k ? $"access to '{k}' was refused by the provider" : "access to the bundle was refused by its provider", e),
        BundleObjectNotFoundException n => new BundleLoadException($"no object under '{n.Key}'", e),
        BundleVersionMismatchException m => new BundleLoadException($"'{m.Key}' changed while it was being read", e),
        BundleReadOnlyException => new BundleLoadException("the provider refused a write", e),
        _ => new BundleLoadException(key is null ? $"provider failure ({e.GetType().Name})" : $"provider failure reading '{key}' ({e.GetType().Name})", e),
    };

    /// <summary>
    /// Consumes <paramref name="provider"/>'s listing of the whole bundle under the core's own
    /// bounds, whatever the provider does (spec §6): entries while consuming; depth and key
    /// length on every RAW name, before any parsing, so a name that also breaks a base rule
    /// cannot bypass a global bound (the third review slipped <c>bad\name/a/b.md</c> past
    /// <c>MaxDepth = 1</c> that way). Returns the names to validate as a tree and, apart, the
    /// names refused for their length — still physically there: the tree validation counts their
    /// ancestors as prefixes, so <c>x.md</c> beside an over-long <c>x.md/…</c> is a conflict.
    /// Provider exceptions and the bounds' <see cref="BundleLimitExceededException"/> propagate.
    /// The one listing of the whole bundle that applies every bound (entries, depth, key length) for a load or a regeneration: <see cref="Load(IBundleProvider, BundleLoadOptions?)"/>
    /// and <see cref="IndexGenerator"/> both list through it.
    /// </summary>
    internal static async ValueTask<(List<string> TreeInput, List<string> OverLength)> ListNamesAsync(IBundleProvider provider, BundleLimits limits, CancellationToken ct)
    {
        var treeInput = new List<string>();
        var overLength = new List<string>();
        var count = 0;
        await foreach (var listing in provider.EnumerateAsync(null, limits, ct).WithCancellation(ct).ConfigureAwait(false))
        {
            if (++count > limits.MaxEntries)
            {
                throw new BundleLimitExceededException(BundleBound.Entries, limits.MaxEntries);
            }

            var raw = listing.RawName;
            if (raw.Split('/').Length > limits.MaxDepth)
            {
                throw new BundleLimitExceededException(BundleBound.Depth, limits.MaxDepth);
            }

            if (System.Text.Encoding.UTF8.GetByteCount(raw) > limits.MaxKeyBytes)
            {
                overLength.Add(raw);
                continue;
            }

            treeInput.Add(raw);
        }

        return (treeInput, overLength);
    }

    /// <summary>
    /// <see cref="ListNamesAsync"/> through the non-blocking bridge (#152), for the writer and the
    /// index generator until they have their own asynchronous form.
    /// </summary>
    internal static (List<string> TreeInput, List<string> OverLength) ListNames(IBundleProvider provider, BundleLimits limits) =>
        SyncBridge.RunCancellable(ct => ListNamesAsync(provider, limits, ct));

    /// <summary>The size of the copy buffer <see cref="ReadText"/> reads through.</summary>
    internal const int ReadChunkBytes = 81920;

    /// <summary>
    /// Reads one object as strict UTF-8, counting bytes against the bound (spec §6) whatever the
    /// provider says. <paramref name="chunk"/> is the caller's copy buffer, reused across the
    /// objects of one load. The destination is pre-sized from <see cref="BundleObject.Length"/>
    /// when the provider declares one within the bound — a hint only: the bytes are still
    /// counted and an overrun still throws, whatever the declared length. The one bounded reader
    /// of the core: <see cref="BundleConceptWriter"/> reads the objects it rewrites through it.
    /// </summary>
    internal static string ReadText(IBundleProvider provider, BundleKey key, BundleLimits limits, byte[] chunk, out BundleObjectVersion version) =>
        ReadTextAt(provider, key, limits, chunk, null, out version);

    /// <summary>As <see cref="ReadText"/>, pinned: with <paramref name="expected"/>, the provider reads only the object at that version (spec §5.4) and throws <see cref="BundleVersionMismatchException"/> otherwise.</summary>
    internal static string ReadTextAt(IBundleProvider provider, BundleKey key, BundleLimits limits, byte[] chunk, BundleObjectVersion? expected, out BundleObjectVersion version)
    {
        var read = SyncBridge.RunCancellable(ct => ReadTextAtAsync(provider, key, limits, chunk, expected, ct));
        version = read.Version;
        return read.Text;
    }

    /// <summary>
    /// The asynchronous bounded reader (#152): the same reading as <see cref="ReadText"/> — strict
    /// UTF-8, bytes counted against the bound whatever the provider says, destination pre-sized from
    /// a declared length within the bound — and, with <paramref name="expected"/>, pinned (spec §5.4).
    /// Returns the text with the version of the object read. The one bounded reader of the core.
    /// </summary>
    internal static async ValueTask<(string Text, BundleObjectVersion Version)> ReadTextAtAsync(IBundleProvider provider, BundleKey key, BundleLimits limits, byte[] chunk, BundleObjectVersion? expected, CancellationToken ct)
    {
        var obj = await provider.ReadAsync(key, expected, ct).ConfigureAwait(false);
        try
        {
            using var buffer = obj.Length is { } declared && declared >= 0 && declared <= limits.MaxObjectBytes && declared <= Array.MaxLength
                ? new MemoryStream((int)declared)
                : new MemoryStream();
            long total = 0;
            int read;
            while ((read = await obj.Content.ReadAsync(chunk.AsMemory(), ct).ConfigureAwait(false)) > 0)
            {
                total += read;
                if (total > limits.MaxObjectBytes)
                {
                    throw new BundleObjectTooLargeException(key, total, limits.MaxObjectBytes);
                }

                buffer.Write(chunk, 0, read);
            }

            return (OkfEncodings.Strict.GetString(buffer.GetBuffer(), 0, (int)buffer.Length), obj.Version);
        }
        finally
        {
            await obj.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>The bundle's root: the provider's displayable location (the root directory for the filesystem). Obsolete: the same string as <see cref="Location"/>.</summary>
    [Obsolete("Bundle.Root is the provider's displayable location: use Bundle.Location (or Bundle.Provider). The path-based members are removed with the asynchronous provider contract (#152).")]
    public string Root => Provider.Location;

    /// <summary>The provider this bundle was loaded from (spec §1).</summary>
    public IBundleProvider Provider { get; }

    /// <summary>The provider's displayable location; the obsolete <c>Root</c> is the same string.</summary>
    public string Location => Provider.Location;

    /// <summary>The options the bundle was loaded with.</summary>
    public BundleLoadOptions Options { get; }

    /// <summary>The provider's bundle token at load time (spec §5.1).</summary>
    public BundleVersion Version { get; }

    /// <summary>The keys of every <c>index.md</c>, in load order; <see cref="BundleDisplayPath.Compose"/> with <see cref="Location"/> gives their display paths (the obsolete <c>IndexFiles</c>).</summary>
    public IReadOnlyList<BundleKey> IndexKeys { get; }

    /// <summary>The keys of every <c>log.md</c>, in load order; <see cref="BundleDisplayPath.Compose"/> with <see cref="Location"/> gives their display paths (the obsolete <c>LogFiles</c>).</summary>
    public IReadOnlyList<BundleKey> LogKeys { get; }

    /// <summary>Every problem met while loading (spec §2): rejected <c>.md</c> names, oversized or unparseable concepts. <see cref="DisplayPath"/> gives each one's display path (the obsolete <c>ParseErrors</c> is that projection).</summary>
    public IReadOnlyList<BundleParseError> ParseProblems { get; }

    /// <summary>Keys the tree validation or a bound rejected (spec §2), whatever their extension: they stay rejected for <see cref="IsKeyAllowed"/> even though the provider may still hold them.</summary>
    public IReadOnlySet<BundleKey> RejectedKeys { get; }

    /// <summary>The colliding or conflicting keys the tree validation condemned (spec §2), as listed. <see cref="IsKeyAllowed"/> matches them in their <b>folded</b> form, so every spelling of a condemned prefix is refused, even for an object created after the load.</summary>
    public IReadOnlyList<BundleKey> RejectedPrefixes { get; }

    /// <summary>
    /// The accepted <c>.md</c> keys whose names the portable profile
    /// (<see cref="BundleKeyPolicy.Strict"/>) would have refused (spec §2, layer 3), in path
    /// order: they loaded, but would not survive every filesystem.
    /// <see cref="BundleValidator.Validate"/> reports each as a
    /// <see cref="DiagnosticCode.KeyNotPortable"/> warning. Always empty when the bundle was
    /// loaded under <see cref="BundleKeyPolicy.Strict"/>.
    /// </summary>
    public IReadOnlyList<BundlePortabilityIssue> PortabilityIssues { get; }

    /// <summary>The version of every concept object read at load time (spec §5.1).</summary>
    public IReadOnlyDictionary<BundleKey, BundleObjectVersion> ObjectVersions { get; }

    private readonly Dictionary<ConceptId, ExecutableResourceSurvey> _executableResources = [];

    /// <summary>
    /// The executable resources of every Attested Computation concept of this image — its
    /// <c>computation</c> file and its <c>attester.resource</c> — with the state each was found
    /// in at the load (spec §5.1): <see cref="ExecutableResourceState.Pinned"/> with its version,
    /// or why it cannot be executed. One entry per concept whose frontmatter
    /// <see cref="Frontmatter.IsAttestedComputation"/>; a concept with a problem is still in
    /// <see cref="Concepts"/>, readable, validated and audited.
    /// </summary>
    public IReadOnlyDictionary<ConceptId, ExecutableResourceSurvey> ExecutableResources { get; private set; } = new ReadOnlyDictionary<ConceptId, ExecutableResourceSurvey>(new Dictionary<ConceptId, ExecutableResourceSurvey>());

    private readonly BundleKeyValidator _validator;
    private readonly HashSet<string> _rejectedPrefixFolds;

    /// <summary>
    /// The one guard in front of every late access to a key (spec §2, decision 11): the key
    /// policy, the keys and prefixes the tree validation rejected, and the depth and
    /// key-length bounds this bundle was loaded with. <paramref name="reason"/> says which.
    /// </summary>
    public bool IsKeyAllowed(BundleKey key, out string? reason)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.Segments.Count > Options.Limits.MaxDepth)
        {
            reason = $"'{key}' exceeds the depth bound ({Options.Limits.MaxDepth})";
            return false;
        }

        if (key.Utf8ByteCount > Options.Limits.MaxKeyBytes)
        {
            reason = $"'{key}' exceeds the key-length bound ({Options.Limits.MaxKeyBytes} bytes)";
            return false;
        }

        if (_validator.CheckKey(key) is { } policyReason)
        {
            reason = policyReason;
            return false;
        }

        if (RejectedKeys.Contains(key))
        {
            reason = $"'{key}' was rejected when the bundle was loaded";
            return false;
        }

        // Prefixes are compared in their folded form (decision 11): a prefix condemned for a
        // case collision condemns every spelling of itself, not only the ones that were listed
        // (the third review re-entered `Ab`/`aB` through `AB`).
        for (var depth = 1; depth <= key.Segments.Count; depth++)
        {
            var prefix = BundleKey.FromSegments(key.Segments.Take(depth).ToList());
            if (_rejectedPrefixFolds.Contains(_validator.FoldKeyText(prefix)))
            {
                reason = $"'{key}' is under '{prefix}', which was rejected when the bundle was loaded";
                return false;
            }
        }

        reason = null;
        return true;
    }

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

    /// <summary>Paths of all <c>index.md</c> files found (§8). Obsolete: the display paths of <see cref="IndexKeys"/>.</summary>
    [Obsolete("Bundle.IndexFiles projects Bundle.IndexKeys through the display rule: use IndexKeys, and BundleDisplayPath.Compose(bundle.Location, key) for a path to print. The path-based members are removed with the asynchronous provider contract (#152).")]
    public IReadOnlyList<string> IndexFiles { get; }

    /// <summary>Paths of all <c>log.md</c> files found (§9). Obsolete: the display paths of <see cref="LogKeys"/>.</summary>
    [Obsolete("Bundle.LogFiles projects Bundle.LogKeys through the display rule: use LogKeys, and BundleDisplayPath.Compose(bundle.Location, key) for a path to print. The path-based members are removed with the asynchronous provider contract (#152).")]
    public IReadOnlyList<string> LogFiles { get; }

    /// <summary>Files whose frontmatter could not be parsed during loading, as (path, error message) pairs. Obsolete: <see cref="ParseProblems"/> with <see cref="DisplayPath"/>.</summary>
    [Obsolete("Bundle.ParseErrors projects Bundle.ParseProblems through the display rule: use ParseProblems (the raw name, the key when the name is one, the error), and bundle.DisplayPath(problem) for a path to print. The path-based members are removed with the asynchronous provider contract (#152).")]
    public IReadOnlyList<(string Path, string Error)> ParseErrors { get; }

    /// <summary>The resolved outbound cross-links from a concept.</summary>
    public IReadOnlyList<ResolvedLink> LinksFrom(ConceptId id) =>
        _outbound.TryGetValue(id, out var v) ? v : [];

    /// <summary>The ids of concepts that link to the given concept ("cited by" / backlinks).</summary>
    public IReadOnlyList<ConceptId> Backlinks(ConceptId id) =>
        _backlinks.TryGetValue(id, out var v) ? v : [];

    /// <summary>
    /// All broken internal links in the bundle, as (source, raw target)
    /// pairs. Broken links are permitted by the spec (§6.1) — this is
    /// informational.
    /// </summary>
    public IReadOnlyList<(ConceptId Source, string RawTarget)> BrokenLinks()
    {
        var result = new List<(ConceptId Source, string RawTarget)>();
        foreach (var id in Concepts.Select(c => c.Id))
        {
            foreach (var link in LinksFrom(id))
            {
                if (!link.Exists)
                {
                    result.Add((id, link.Raw));
                }
            }
        }

        return result;
    }

    /// <summary>
    /// The declared OKF version from the bundle-root <c>index.md</c>
    /// frontmatter, if present (<c>okf_version</c>, §12). This is the only
    /// place frontmatter is permitted in an <c>index.md</c>.
    ///
    /// Computed once while <see cref="Load(IBundleProvider, BundleLoadOptions?)"/> builds the
    /// bundle and stored, so it reflects the same load-time snapshot as the rest of this
    /// <see cref="Bundle"/>: a later change to the root <c>index.md</c> in storage
    /// does not affect an already-loaded instance. A legitimate <c>null</c> (no
    /// root <c>index.md</c>, no <c>okf_version</c> key, unparseable frontmatter, or a
    /// root <c>index.md</c> that is gone, over the byte bound, unreadable or not valid
    /// UTF-8 when it is read) is a stored value like any other. Any other provider failure
    /// while reading it — a refusal by the provider's STRICT guards, for instance — is not a
    /// <c>null</c>: the load throws <see cref="BundleLoadException"/>. The backing field is <c>readonly</c> and
    /// set in the constructor before the instance is published, so
    /// <see cref="Bundle"/> instances shared and read concurrently (e.g. across
    /// tool invocations in <c>OkfBundleTools</c>) need no lock.
    /// </summary>
    public string? OkfVersion => _okfVersion;

    private static async ValueTask<string?> ComputeOkfVersionAsync(IBundleProvider provider, BundleLimits limits, List<BundleKey> indexKeys, byte[] chunk, CancellationToken ct)
    {
        var key = BundleKey.Parse(IndexFilename);
        if (!indexKeys.Contains(key))
        {
            return null;
        }

        string text;
        try
        {
            text = (await ReadTextAtAsync(provider, key, limits, chunk, null, ct).ConfigureAwait(false)).Text;
        }
        catch (Exception e) when (e is BundleObjectNotFoundException or BundleObjectTooLargeException or IOException or UnauthorizedAccessException or System.Text.DecoderFallbackException)
        {
            // Only the LOCAL failures mean "no declared version": a missing, oversized,
            // unreadable or non-UTF-8 root index. (BundleObjectTooLargeException derives from
            // IOException, so the IOException filter already covers it; it is named for the reader.)
            return null;
        }
        catch (BundleProviderException e)
        {
            // A global failure (a bound, a refusal) is a load failure, not an absent version.
            throw Translate(e, key);
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
    /// within the bundle root. A <see cref="FrontmatterResourceKind.BundleRelative"/>
    /// value -- one with a leading <c>/</c> or <c>\</c>, and equally one written
    /// bare, per Appendix A -- is combined with <see cref="Root"/> after
    /// stripping any leading separator(s); <see cref="Path.Combine(string, string)"/>
    /// otherwise discards <paramref name="concept"/>'s directory (or, here,
    /// <see cref="Root"/>) whenever the second argument looks rooted, which on
    /// Windows an absolute-looking <c>/x</c> does. A
    /// <see cref="FrontmatterResourceKind.ConceptRelative"/> value (an explicit
    /// <c>./</c> or <c>../</c> prefix) is combined with the concept's own
    /// directory instead.
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
    /// Always returns <c>true</c>: resolution never fails outright (§11,
    /// permissive), it only reports which of the above statuses applies.
    /// Obsolete: it resolves through the OS filesystem, outside the provider
    /// seam, so it applies neither the key policy nor the exact-name rules
    /// <see cref="TryResolveResourceKey"/> applies; no library code calls it.
    /// </summary>
    /// <exception cref="NotSupportedException">The bundle's provider is not a <see cref="FileSystemBundleProvider"/>.</exception>
    [Obsolete("Bundle.TryResolveResource resolves a §6.2 path on the OS filesystem, outside the provider seam: use Bundle.TryResolveResourceKey, which resolves on keys under the key policy, then Bundle.ReadResourceText(BundleKey). The path-based members are removed with the asynchronous provider contract (#152).")]
    public bool TryResolveResource(Concept concept, string rawPath, out string? absolutePath, out ResourceResolutionStatus status)
    {
        RequireFileSystemForPathMembers();
        var kind = FrontmatterResourceClassifier.KindOf(rawPath);
        if (kind == FrontmatterResourceKind.Url)
        {
            absolutePath = null;
            status = ResourceResolutionStatus.Url;
            return true;
        }

        string candidate;
        try
        {
            // Strip the leading separator(s) BEFORE combining -- Path.Combine(base, "/x")
            // discards `base` entirely on Windows, since "/x" looks rooted to it. A
            // ConceptRelative value starts with "." and so is never affected.
            var relative = rawPath.TrimStart('/', '\\');

            if (Path.IsPathRooted(relative))
            {
                // Still rooted after stripping: a drive-relative or drive-absolute form
                // (e.g. "e:query.sql", "C:\x", or "/e:query.sql"). Path.Combine/GetFullPath
                // would resolve it against that drive's own current directory rather than
                // the base we chose -- unpredictable, and potentially inside the bundle
                // root by accident, which the containment check below would then accept.
                // Reject as Unsafe.
                absolutePath = null;
                status = ResourceResolutionStatus.Unsafe;
                return true;
            }

            // §6.2: only an explicit "./" or "../" resolves against the concept's own
            // directory. Everything else -- a leading "/", and equally a bare path --
            // resolves from the bundle root (see the remarks above).
            var basis = kind == FrontmatterResourceKind.ConceptRelative
                ? Path.GetDirectoryName(concept.Path) ?? Root
                : Root;

            candidate = Path.GetFullPath(Path.Combine(basis, relative));
        }
        catch (Exception e) when (e is ArgumentException or PathTooLongException or NotSupportedException)
        {
            // A malformed raw path -- e.g. one containing an embedded NUL,
            // reachable through the YAML subset's own `\0` escape inside a
            // quoted scalar -- makes Path.Combine/Path.GetFullPath throw.
            // Resolution must never throw (§11, permissive): treat it the same
            // as any other candidate that can't be trusted, never exposing a
            // path to the caller.
            absolutePath = null;
            status = ResourceResolutionStatus.Unsafe;
            return true;
        }

        var fullRoot = ReparsePoints.CanonicalizeRoot(Root);
        if (!ReparsePoints.IsWithin(fullRoot, candidate, PathComparison)
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
    /// invalid byte sequences rather than substituting U+FFFD, and never
    /// reinterprets a byte-order mark as a different encoding). Intended to be
    /// called only on an <paramref name="absolutePath"/> produced by
    /// <see cref="TryResolveResource"/> with
    /// <see cref="ResourceResolutionStatus.Resolved"/>.
    /// </summary>
    /// <remarks>
    /// Path safety is established by <see cref="TryResolveResource"/>, and
    /// re-checked here, STRICTLY, immediately before the read: the path must
    /// still be inside <see cref="Root"/>, and neither it nor any directory
    /// between it and <see cref="Root"/> may be a reparse point or an entry
    /// whose link status cannot be inspected. <see cref="TryResolveResource"/>
    /// keeps the lenient predicate, because its status is what <c>okf validate</c>
    /// reports and that must not change; but it answers "not a link" for a
    /// junction whose attributes the current user may not read, and the OS
    /// still follows that junction on the read -- measured returning a file
    /// outside the bundle root through <c>okf_get_computation</c>. A read is a
    /// guard, so it fails closed; see
    /// <c>ReparsePoints.IsReparsePointOrUninspectable</c>. Every in-repo reader
    /// of a resolved resource (<c>okf_get_computation</c>, and
    /// <c>AttestationOrchestrator</c>'s computation and attester source) goes
    /// through this method, and each already reports a thrown
    /// <see cref="UnauthorizedAccessException"/> as an unreadable resource.
    ///
    /// The re-check applies to whatever path a caller passes, not only to
    /// <see cref="TryResolveResource"/> output: a path outside <see cref="Root"/>
    /// handed in directly is refused too. This method no longer reads an
    /// arbitrary file, which is a breaking change for any caller that used it
    /// that way, outside its documented contract.
    /// </remarks>
    /// <exception cref="NotSupportedException">The bundle's provider is not a <see cref="FileSystemBundleProvider"/>.</exception>
    /// <exception cref="UnauthorizedAccessException">
    /// <paramref name="absolutePath"/> is outside <see cref="Root"/>, or it or a
    /// directory between it and <see cref="Root"/> is a reparse point or could
    /// not be inspected; or the OS denies the read.
    /// </exception>
    /// <exception cref="IOException">The file cannot be read.</exception>
    /// <exception cref="System.Text.DecoderFallbackException">The content is not valid UTF-8.</exception>
    [Obsolete("Bundle.ReadResourceText(string) reads a host path, outside the provider seam: use Bundle.ReadResourceText(BundleKey) on the key Bundle.TryResolveResourceKey resolved. The path-based members are removed with the asynchronous provider contract (#152).")]
    public string ReadResourceText(string absolutePath)
    {
        RequireFileSystemForPathMembers();
        var fullRoot = ReparsePoints.CanonicalizeRoot(Root);
        var fullPath = System.IO.Path.GetFullPath(absolutePath);
        if (!ReparsePoints.IsWithin(fullRoot, fullPath, PathComparison)
            || ReparsePoints.IsReparsePointOrUninspectable(fullPath)
            || ReparsePoints.HasReparsePointOrUninspectableAncestor(fullRoot, fullPath, PathComparison))
        {
            throw new UnauthorizedAccessException(
                "The resource is outside the bundle root, or resolves through a reparse point (symlink/junction) or an entry that could not be inspected inside it; refusing to read it.");
        }

        return OkfEncodings.Strict.GetString(File.ReadAllBytes(fullPath));
    }

    /// <summary>D7: the path-based members have no meaning over a provider other than the filesystem (its Root is a display string).</summary>
    private void RequireFileSystemForPathMembers()
    {
        if (Provider is not FileSystemBundleProvider)
        {
            throw new NotSupportedException(
                "This bundle is not served by a FileSystemBundleProvider, so its Root is a display string, not a path: use Bundle.TryResolveResourceKey, then Bundle.ReadResourceText(BundleKey).");
        }
    }

    /// <summary>
    /// Resolves a §6.2 path-valued field to a key (spec §2): lexically, through
    /// <see cref="ResourceKeyResolver"/>, then <see cref="IsKeyAllowed"/>, then the provider's
    /// <see cref="IBundleProvider.ExistsAsync"/>. Never throws (§11, permissive): a provider refusal
    /// reads as <see cref="ResourceResolutionStatus.Unsafe"/>. Always returns <see langword="true"/>:
    /// resolution never fails outright, only its status varies.
    /// <para>
    /// A value that names no object is <see cref="ResourceResolutionStatus.Missing"/>, as it was
    /// when the validator resolved it on the filesystem: one that resolves to the bundle root
    /// itself (<c>/</c>, <c>.</c> from a top-level concept, <c>sub/..</c>; <paramref name="key"/>
    /// is <see langword="null"/>), and one ending with <c>/</c> (<c>references/a.sql/</c>), which
    /// names a prefix — its key goes through the policy and the provider's guards like any
    /// candidate, so a refused path stays <see cref="ResourceResolutionStatus.Unsafe"/>, and is
    /// then missing whatever object the provider holds under it. A value climbing above the root
    /// stays <see cref="ResourceResolutionStatus.Unsafe"/>.
    /// </para>
    /// <para>
    /// <paramref name="key"/> is non-<see langword="null"/> only when the status is
    /// <see cref="ResourceResolutionStatus.Resolved"/>; for every other status it is
    /// <see langword="null"/>, so a prefix key is never handed out as an object to read.
    /// </para>
    /// </summary>
    /// <exception cref="InvalidOperationException">The provider answers later (it does not complete synchronously): this synchronous spelling never waits, so it throws; use <see cref="ResolveResourceKeyAsync"/>.</exception>
    public bool TryResolveResourceKey(Concept concept, string rawPath, out BundleKey? key, out ResourceResolutionStatus status)
    {
        (key, status) = SyncBridge.RunCancellable(ct => ResolveResourceKeyAsync(concept, rawPath, ct));
        return true;
    }

    /// <summary>
    /// <see cref="TryResolveResourceKey"/> without the blocking: the same resolution, the provider
    /// asked asynchronously (#152). Never throws for a provider's refusal (§11, permissive): that
    /// reads as <see cref="ResourceResolutionStatus.Unsafe"/>; only a cancellation propagates.
    /// </summary>
    /// <returns>The key (non-<see langword="null"/> only for <see cref="ResourceResolutionStatus.Resolved"/>) and the status.</returns>
    public async ValueTask<(BundleKey? Key, ResourceResolutionStatus Status)> ResolveResourceKeyAsync(Concept concept, string rawPath, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(concept);
        ArgumentNullException.ThrowIfNull(rawPath);
        var conceptKey = concept.Key ?? concept.Id.ToKey();
        var outcome = ResourceKeyResolver.Resolve(conceptKey, rawPath, out var candidate);
        switch (outcome)
        {
            case ResourceKeyOutcome.Url:
                return (null, ResourceResolutionStatus.Url);
            case ResourceKeyOutcome.Unsafe:
                return (null, ResourceResolutionStatus.Unsafe);
            case ResourceKeyOutcome.Root:
                return (null, ResourceResolutionStatus.Missing);
        }

        if (!IsKeyAllowed(candidate!, out _))
        {
            return (null, ResourceResolutionStatus.Unsafe);
        }

        try
        {
            // A prefix is asked about too, for the provider's refusal only: a path through a
            // reparse point is Unsafe whether or not it ends with '/'.
            var exists = await Provider.ExistsAsync(candidate!, ct).ConfigureAwait(false);
            return exists && outcome == ResourceKeyOutcome.Candidate
                ? (candidate, ResourceResolutionStatus.Resolved)
                : (null, ResourceResolutionStatus.Missing);
        }
        catch (Exception e) when (e is BundleProviderException or IOException or UnauthorizedAccessException)
        {
            return (null, ResourceResolutionStatus.Unsafe);
        }
    }

    /// <summary>
    /// Reads a resolved resource as strict UTF-8 through the provider, after
    /// <see cref="IsKeyAllowed"/> (spec §2) and the provider's own STRICT guards and
    /// confinement boundary (spec §8). Messages are library-authored (§7); the raw cause is
    /// the inner exception. A refusal — by this bundle's guard or by the provider — surfaces
    /// as <see cref="UnauthorizedAccessException"/> and a missing object as
    /// <see cref="FileNotFoundException"/>, so existing callers classify both as before.
    /// </summary>
    /// <exception cref="UnauthorizedAccessException">The key is not allowed, or the provider refused the access.</exception>
    /// <exception cref="IOException">The object is missing, too large, or cannot be read.</exception>
    /// <exception cref="System.Text.DecoderFallbackException">The content is not valid UTF-8.</exception>
    public string ReadResourceText(BundleKey key) => ReadResourceText(key, null, out _);

    /// <summary>
    /// As <see cref="ReadResourceText(BundleKey)"/>, reporting the object's
    /// <paramref name="version"/> and, with <paramref name="expected"/>, pinned to it (spec §5.4):
    /// the provider reads only the object at that version, so sanctioned code that changed since
    /// it was observed or surveyed is never handed to a runtime.
    /// </summary>
    /// <param name="key">The resolved key.</param>
    /// <param name="expected">The version the read is pinned to, or <see langword="null"/> for the current object.</param>
    /// <param name="version">The version of the object read.</param>
    /// <exception cref="BundleVersionMismatchException">The object's version is not <paramref name="expected"/>; nothing is returned.</exception>
    /// <exception cref="UnauthorizedAccessException">The key is not allowed, or the provider refused the access.</exception>
    /// <exception cref="IOException">The object is missing, too large, or cannot be read.</exception>
    /// <exception cref="System.Text.DecoderFallbackException">The content is not valid UTF-8.</exception>
    /// <exception cref="InvalidOperationException">The provider answers later (it does not complete synchronously): this synchronous spelling never waits, so it throws; use <see cref="ReadResourceTextAsync"/>.</exception>
    public string ReadResourceText(BundleKey key, BundleObjectVersion? expected, out BundleObjectVersion version)
    {
        var read = SyncBridge.RunCancellable(ct => ReadResourceTextAsync(key, expected, ct));
        version = read.Version;
        return read.Text;
    }

    /// <summary>
    /// <see cref="ReadResourceText(BundleKey, BundleObjectVersion?, out BundleObjectVersion)"/>
    /// without the blocking (#152): the same bounds, pinning and exception mapping, the provider
    /// read asynchronously. Returns the text and the version of the object read.
    /// </summary>
    /// <exception cref="BundleVersionMismatchException">The object's version is not <paramref name="expected"/>; nothing is returned.</exception>
    /// <exception cref="UnauthorizedAccessException">The key is not allowed, or the provider refused the access.</exception>
    /// <exception cref="IOException">The object is missing, too large, or cannot be read.</exception>
    /// <exception cref="System.Text.DecoderFallbackException">The content is not valid UTF-8.</exception>
    public async ValueTask<(string Text, BundleObjectVersion Version)> ReadResourceTextAsync(BundleKey key, BundleObjectVersion? expected = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (!IsKeyAllowed(key, out var reason))
        {
            throw new UnauthorizedAccessException(reason);
        }

        try
        {
            return await ReadTextAtAsync(Provider, key, Options.Limits, new byte[ReadChunkBytes], expected, ct).ConfigureAwait(false);
        }
        catch (BundleVersionMismatchException)
        {
            throw;
        }
        catch (BundleAccessRefusedException e)
        {
            throw new UnauthorizedAccessException($"access to '{key}' was refused by the provider", e);
        }
        catch (BundleObjectNotFoundException e)
        {
            throw new FileNotFoundException($"no object under '{key}'", key.ToString(), e);
        }
        catch (BundleObjectTooLargeException)
        {
            throw;
        }
        catch (BundleProviderException e)
        {
            throw new IOException($"provider failure reading '{key}' ({e.GetType().Name})", e);
        }
        catch (UnauthorizedAccessException e)
        {
            throw new UnauthorizedAccessException($"access to '{key}' was denied ({e.GetType().Name})", e);
        }
        catch (IOException e)
        {
            throw new IOException($"I/O error reading '{key}' ({e.GetType().Name})", e);
        }
    }

    /// <summary>
    /// Builds the outbound link and backlink maps for all concepts.
    /// <para>
    /// Called by <see cref="Load(IBundleProvider, BundleLoadOptions?)"/>. It is internal rather than private so that
    /// <c>BundleBacklinksScaleTests</c> can run it on in-memory concepts at a scale
    /// no test should write to disk. Being internal also makes it callable from
    /// every assembly this one grants <c>InternalsVisibleTo</c> (<c>okf</c>,
    /// <c>okf-render</c>, <c>OKF4net.Agents</c>, <c>OKF4net.Catalog</c>,
    /// <c>OKF4net.Viewer</c>), but only the tests call it.
    /// </para>
    /// <para>
    /// Precondition, not checked: <paramref name="concepts"/> and <paramref name="index"/>
    /// come from the same snapshot, with <paramref name="index"/> mapping every id in
    /// <paramref name="concepts"/>, as <see cref="Load(IBundleProvider, BundleLoadOptions?)"/> builds them. The index decides
    /// whether a link target exists; a mismatched pair gives wrong existence flags and
    /// backlinks, with no error.
    /// </para>
    /// </summary>
    internal static (Dictionary<ConceptId, List<ResolvedLink>> Outbound, Dictionary<ConceptId, List<ConceptId>> Backlinks) BuildGraph(
        List<Concept> concepts,
        Dictionary<ConceptId, int> index)
    {
        var outbound = new Dictionary<ConceptId, List<ResolvedLink>>();
        var backlinks = new Dictionary<ConceptId, List<ConceptId>>();

        // The (target, source) edges already credited as backlinks. A source is
        // added to a target's list only on its first citation of that target, so
        // each list stays de-duplicated in first-citation order, i.e. in concept
        // order; the set makes that membership test O(1) where a scan of the
        // list was quadratic in a target's citer count (#148). It is exactly
        // equivalent to scanning the list, since a target's list holds precisely
        // the sources recorded in the set for that target -- including when two
        // concepts share an id, which Load does not prevent.
        var credited = new HashSet<(ConceptId Target, ConceptId Source)>();

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
                if (exists && credited.Add((target, c.Id)))
                {
                    if (!backlinks.TryGetValue(target, out var entry))
                    {
                        entry = [];
                        backlinks[target] = entry;
                    }

                    entry.Add(c.Id);
                }

                resolved.Add(new ResolvedLink(target, exists, link.Text, link.Target));
            }

            outbound[c.Id] = resolved;
        }

        return (outbound, backlinks);
    }
}
