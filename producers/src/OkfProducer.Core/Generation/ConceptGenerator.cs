// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Globalization;
using System.Text;
using OKF4net;
using OKF4net.Yaml;
using OkfProducer.Core.CodeGraph;
using OkfProducer.Core.Scanning;

// In any namespace with a sibling `CodeGraph` NAMESPACE in scope -- OkfProducer.Core.Generation has
// OkfProducer.Core.CodeGraph, and OkfProducer.Tests.Generation has OkfProducer.Tests.CodeGraph -- the
// bare name binds to that namespace before it can bind to the type of the same name a `using` brought
// in (CS0118), so the type needs an alias. Not a workaround for a naming mistake here: the type is
// named after what it is, and its namespace after what it holds.
using CodeGraphModel = OkfProducer.Core.CodeGraph.CodeGraph;

namespace OkfProducer.Core.Generation;

/// <summary>
/// Maps a <see cref="RepositorySnapshot"/> -- and, on the code path, a
/// <see cref="OkfProducer.Core.CodeGraph.CodeGraph"/> -- to concepts via <see cref="OkfDocumentBuilder"/>:
/// one repository overview (fixed id <c>overview</c>), one <c>packages/&lt;slug&gt;</c> concept per
/// detected package, one <c>docs/&lt;slug&gt;</c> concept per detected doc, and one
/// <c>code/&lt;language&gt;/&lt;container...&gt;/&lt;name&gt;</c> concept per extracted symbol (§3.1).
///
/// Every id, in all four families, is allocated through a single <see cref="ConceptIdRegistry"/>
/// (§3.4), so a collision between families is seen rather than silently producing two concepts that
/// want the same file. A collision is disambiguated with a numeric suffix (<c>-2</c>, <c>-3</c>, ...)
/// -- <see cref="ConceptId.Slugify"/> itself never deduplicates, that responsibility belongs to its
/// caller (this class).
/// </summary>
public sealed class ConceptGenerator : IConceptGenerator
{
    /// <summary>
    /// The §7 actor this producer writes into every generated concept's <c>generated.by</c>. Derived
    /// from the assembly version rather than hard-coded so it cannot drift from the version the tool
    /// actually ships as; the informational version (which can carry a git hash) is deliberately not
    /// used, because it would churn every concept on every build.
    /// </summary>
    public static string ProducerActor { get; } =
        $"okfgen/{typeof(ConceptGenerator).Assembly.GetName().Version?.ToString(3) ?? "0.0.0"}";

    /// <summary>
    /// §4.2's chain, in order: a doc comment wins outright (the code stays the source of truth), and
    /// <see cref="SignatureSource"/> is the terminal fallback that always produces something. A future
    /// LLM enrichment step is one more <see cref="IDescriptionSource"/> appended here and nothing else.
    /// </summary>
    private static readonly DescriptionResolver Descriptions = new([new DocCommentSource(), new SignatureSource()]);

    /// <summary>
    /// §4.2's chain for a container concept, which is a different chain because neither of the sources
    /// above can describe one: nothing declared a container, so there is no doc comment and no
    /// signature, and <see cref="SignatureSource"/> would have to name one of
    /// <see cref="SymbolKind"/>'s three nouns where the honest answer is the neutral one
    /// (see <see cref="ContainerToken"/>). The <i>resolver</i> is the same type, deliberately: §4.2's
    /// field preservation is its rule, so a hand-written description on a namespace concept survives
    /// regeneration exactly as it does on every other concept, with no second copy of that rule.
    /// </summary>
    private static readonly DescriptionResolver ContainerDescriptions = new([new ContainerSource()]);

    /// <summary>
    /// The one description source a container has: its own name and its owner, said in the neutral
    /// vocabulary <see cref="ContainerToken"/> explains. Labelled <c>generated</c>, like
    /// <see cref="SignatureSource"/>, so it is re-derived on every run rather than mistaken for a human
    /// edit.
    /// </summary>
    private sealed class ContainerSource : IDescriptionSource
    {
        /// <inheritdoc/>
        public (string Text, string Source)? Describe(SymbolFact fact) =>
            (fact.Container.Length == 0
                ? $"{fact.Name}, a top-level code container."
                : $"{fact.Name}, a code container in {fact.Container}.",
                SignatureSource.SourceLabel);
    }

    /// <inheritdoc/>
    public IReadOnlyList<GeneratedConcept> Generate(RepositorySnapshot snapshot) =>
        Generate(snapshot, codeGraph: null, GenerateOptions.Default);

    /// <summary>
    /// Generates every concept for <paramref name="snapshot"/>, each paired with its concept id. When
    /// <paramref name="codeGraph"/> is non-null, the <c>code/</c> family is generated too, from its
    /// symbols and resolved edges; when it is <see langword="null"/> (the <c>--no-code</c> path, and
    /// what <see cref="Generate(RepositorySnapshot)"/> calls) the output is exactly what it always was.
    ///
    /// Relies on -- and does not re-check -- <see cref="OkfProducer.Core.CodeGraph.CodeGraph"/>'s own
    /// invariant that no edge references a symbol absent from
    /// <see cref="OkfProducer.Core.CodeGraph.CodeGraph.Symbols"/>: an edge whose caller was
    /// scope-filtered is already dropped, and one whose target was filtered is already degraded to
    /// <see cref="EdgeConfidence.Unresolved"/>.
    /// </summary>
    public IReadOnlyList<GeneratedConcept> Generate(RepositorySnapshot snapshot, CodeGraphModel? codeGraph, GenerateOptions options)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(options);

        // §3.4: one registry for all four families -- one allocation record for the whole run, keyed on
        // the full `prefix/segment`. Being precise about what that does and does not buy, because the
        // obvious claim is false: today's four families sit under disjoint prefixes, so a doc that
        // slugifies to "overview" lands on `docs/overview` and CANNOT collide with the bare `overview`
        // id -- the two coexist, and a test says so. What the single registry actually buys is that the
        // `code/` family, which the old `Generate`-local `usedIds` never covered at all, now shares one
        // record with the other three; that `overview` is allocated rather than assumed, so it is not a
        // magic string that could drift out of sync with the set; and that a family added later under an
        // existing prefix is checked by construction instead of needing a second mechanism.
        var registry = new ConceptIdRegistry();

        // Every id is allocated before any body is written, in all four families and not just within
        // `code/` -- §5.2's containment spine makes `overview` and each `packages/*` concept link one
        // level down, so their bodies now depend on ids the registry only finalises later in the run.
        var overviewId = registry.Register(string.Empty, "overview");

        var packages = new List<(ConceptId Id, PackageManifest Manifest)>(snapshot.Packages.Count);
        foreach (var package in snapshot.Packages)
        {
            packages.Add((UniqueConceptId("packages", package.Name, registry), package));
        }

        var docs = new List<(ConceptId Id, DocFile Doc)>(snapshot.Docs.Count);
        foreach (var doc in snapshot.Docs)
        {
            docs.Add((UniqueConceptId("docs", doc.Title, registry), doc));
        }

        var code = codeGraph is null
            ? CodeFamily.Empty
            : BuildCodeConcepts(codeGraph, registry, options, packages);

        var results = new List<GeneratedConcept>(1 + packages.Count + docs.Count + code.Concepts.Count);

        // §5.2: one level down, and nothing further. `overview` naming all ~480 concepts would be
        // rewritten by the addition of a single type; naming the ~10 it directly contains is rewritten
        // only when a package or a doc appears or disappears.
        var overviewChildren = packages
            .Select(p => new Child(p.Id, p.Manifest.Name))
            .Concat(docs.Select(d => new Child(d.Id, d.Doc.Title)))
            .ToList();
        results.Add(new GeneratedConcept(overviewId, BuildOverview(snapshot, overviewChildren)));

        foreach (var (id, manifest) in packages)
        {
            results.Add(new GeneratedConcept(id, BuildPackageConcept(manifest, code.ChildrenOf(id))));
        }

        foreach (var (id, doc) in docs)
        {
            results.Add(new GeneratedConcept(id, BuildDocConcept(doc)));
        }

        results.AddRange(code.Concepts);

        return results;
    }

    /// <summary>
    /// One descending containment edge: the id a parent concept links to, and the link text it shows.
    /// </summary>
    private readonly record struct Child(ConceptId Id, string Title);

    /// <summary>
    /// What the <c>code/</c> pass hands back: its own concepts, plus the one thing the non-code
    /// families cannot work out for themselves -- which namespaces each package concept links down to
    /// (§5.1). Keyed on the package id's string form rather than on <see cref="ConceptId"/>, which has
    /// no ordering to key a dictionary on safely; nothing enumerates this map, it is only indexed.
    /// </summary>
    private sealed record CodeFamily(
        IReadOnlyList<GeneratedConcept> Concepts,
        IReadOnlyDictionary<string, List<Child>> PackageChildren)
    {
        /// <summary>The families a run with no code graph produces: no code concepts, no package children.</summary>
        public static CodeFamily Empty { get; } = new([], new Dictionary<string, List<Child>>(StringComparer.Ordinal));

        /// <summary>The namespaces (or, with no namespace anywhere, the top-level symbols) <paramref name="packageId"/> contains.</summary>
        public IReadOnlyList<Child> ChildrenOf(ConceptId packageId) =>
            PackageChildren.TryGetValue(packageId.ToString(), out var children) ? children : [];
    }

    private static ConceptId UniqueConceptId(string prefix, string name, ConceptIdRegistry registry)
    {
        string baseSlug;
        try
        {
            baseSlug = ConceptId.Slugify(name);
        }
        catch (ConceptIdException)
        {
            // `name` normalized to nothing (e.g. entirely non-ASCII, or empty) -- fall back to a
            // generic slug derived from the prefix; the registry's collision loop still disambiguates
            // multiple equally-unnameable entries under the same prefix with a numeric suffix.
            baseSlug = prefix switch
            {
                "packages" => "package",
                "docs" => "doc",
                _ => prefix,
            };
        }

        // A concept id segment ending in ".md" would double up when BundleConceptWriter appends its
        // own ".md" extension to serialize the file (e.g. a doc literally titled "README.md" would
        // otherwise become "docs/readme.md.md"). Scoped to docs only: for a doc, the id is derived
        // straight from a human-facing title, so trimming a redundant ".md" is a harmless, expected
        // normalization. For a package, the id is derived from an ecosystem identifier (e.g. a NuGet
        // PackageId such as "Foo.Md") where ".md" can be a meaningful, distinguishing part of the
        // name -- silently stripping it would make the strip invisible in the id and could collide an
        // unrelated sibling package named "Foo" into "packages/foo-2". A package whose id ends in
        // ".md" still risks the same double-extension filename on write, but that's the lesser, more
        // honest failure mode: the id itself stays a faithful, non-colliding representation of the
        // package name.
        if (prefix == "docs" && baseSlug.Length > ".md".Length
            && baseSlug.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
        {
            baseSlug = baseSlug[..^".md".Length];
        }

        return registry.Register(prefix, baseSlug);
    }

    /// <summary>
    /// True when <paramref name="segment"/> would collide with a concept id
    /// <see cref="OKF4net.BundleConceptWriter"/> reserves for the bundle's own
    /// <c>index.md</c>/<c>log.md</c>. Internal (not private) so <see cref="ConceptIdRegistry"/> --
    /// the single registry spanning all four id families -- can reuse this rule instead of forking a
    /// second copy of it.
    /// </summary>
    internal static bool IsReservedSegment(string segment) =>
        string.Equals(segment, "index", StringComparison.OrdinalIgnoreCase)
        || string.Equals(segment, "log", StringComparison.OrdinalIgnoreCase);

    private static OkfDocument BuildOverview(RepositorySnapshot snapshot, IReadOnlyList<Child> children)
    {
        var description = snapshot.Packages.Count switch
        {
            0 => $"Repository {snapshot.RepoName}.",
            1 => $"Repository {snapshot.RepoName}, containing 1 detected package.",
            var n => $"Repository {snapshot.RepoName}, containing {n.ToString(CultureInfo.InvariantCulture)} detected packages.",
        };

        var body = new StringBuilder();
        body.Append("# ").Append(snapshot.RepoName).Append("\n\n").Append(description).Append('\n');
        AppendContains(body, children);

        return OkfDocumentBuilder
            .ForType("Repository")
            .Title(snapshot.RepoName)
            .Description(description)
            .Tags("repository")
            .Body(body.ToString())
            .Build();
    }

    private static OkfDocument BuildPackageConcept(PackageManifest package, IReadOnlyList<Child> children)
    {
        var description = package.Description ?? $"{package.Ecosystem} package {package.Name}.";

        var body = new StringBuilder();
        body.Append("# ").Append(package.Name).Append("\n\n").Append(description).Append('\n');
        AppendContains(body, children);

        return OkfDocumentBuilder
            .ForType("Package")
            .Title(package.Name)
            .Description(description)
            .Tags(package.Ecosystem)
            .Resource(package.RelativePath)
            .AddSource(resource: package.RelativePath)
            .Body(body.ToString())
            .Build();
    }

    private static OkfDocument BuildDocConcept(DocFile doc)
    {
        return OkfDocumentBuilder
            .ForType("Documentation")
            .Title(doc.Title)
            .Description($"Repository documentation file {doc.RelativePath}.")
            .Tags("documentation")
            .Resource(doc.RelativePath)
            .AddSource(resource: doc.RelativePath)
            .Body($"# {doc.Title}\n\nSee `{doc.RelativePath}` in the repository.\n")
            .Build();
    }

    // ---------------------------------------------------------------------------------------------
    // §4: the code family.
    // ---------------------------------------------------------------------------------------------

    /// <summary>The key one code concept is built from: overloads collapse onto it (§3.2).</summary>
    private readonly record struct SymbolKey(string Language, string Container, string Name);

    /// <summary>
    /// The shortest raw segment path that names a container concept: <c>code</c>, the language, and at
    /// least one container segment. <c>[code, csharp]</c> is the language root -- a directory in the
    /// bundle, never a concept -- so nothing shorter than this is ever registered.
    /// </summary>
    private const int MinimumContainerDepth = 3;

    /// <summary>
    /// The word this producer uses for a code container, in its tag and as the id segment a container
    /// falls back to when its own name cannot form one (§2.3: unusual input degrades the output, it
    /// never aborts the run).
    ///
    /// <para><b>"container" and not "namespace", and that is a measured choice, not a hedge.</b> §5.1
    /// asks for a concept per namespace; what this pass can actually identify is a level of the path
    /// tree that no extracted declaration claims, which is a namespace <i>most</i> of the time and
    /// demonstrably not always. Run against this repository, 8 of the ~31 synthesized containers are
    /// private nested types -- <c>YamlParser.BlockParser</c>, <c>OkfContextProvider.ScopeBox</c>,
    /// <c>HtmlSafeJson</c> -- whose own declaration the default visibility scope excludes while their
    /// members survive it. Labelling those "C# Namespace" would put a plainly false statement about the
    /// code into a knowledge bundle, ~25% of the time; "C# Container" is true of a namespace, of a
    /// module, and of a type alike. The inference that would recover "namespace" precisely -- a
    /// container with a member child is a type -- is right for C# and wrong for the next profile, where
    /// a module's members are functions, so it is not taken. If the scope filter is ever fixed to drop
    /// the members of a type it excluded, every synthesized container becomes a real namespace and this
    /// choice is worth revisiting.</para>
    /// </summary>
    private const string ContainerToken = "container";

    /// <summary>
    /// Builds the <c>code/</c> family in two passes, and the order matters: every id must be allocated
    /// before any body is written, because a body links to <i>other</i> concepts' ids and those ids
    /// are only final once the registry has resolved every collision (§3.3).
    /// </summary>
    private static CodeFamily BuildCodeConcepts(
        CodeGraphModel graph,
        ConceptIdRegistry registry,
        GenerateOptions options,
        IReadOnlyList<(ConceptId Id, PackageManifest Manifest)> packages)
    {
        var profiles = new Dictionary<string, LanguageProfile>(StringComparer.Ordinal);

        // Sorted, never grouped-and-iterated: a Dictionary/HashSet enumeration order must never reach
        // the output (§6.2). CodeGraphBuilder already sorts its symbols, but sorting again here is what
        // makes this method deterministic on its own, for any CodeGraph a caller hands it. Both sorts
        // below are load-bearing and pinned by tests whose fixtures put the input in a DIFFERENT order
        // from the sorted one, so deleting either chain turns a test red rather than passing by luck.
        var unsorted = new List<(SymbolKey Key, IReadOnlyList<SymbolFact> Declarations, string[] RawSegments)>();
        foreach (var group in graph.Symbols.GroupBy(s => new SymbolKey(s.Language, s.Container, s.Name)))
        {
            // Within one concept, the declaration order decides which span `resource` points at and the
            // order of the `## Signatures` bullets.
            var declarations = (IReadOnlyList<SymbolFact>)group
                .OrderBy(s => s.RelativePath, StringComparer.Ordinal)
                .ThenBy(s => s.StartOffset)
                .ThenBy(s => s.Signature, StringComparer.Ordinal)
                .ToList();

            var profile = ProfileFor(group.Key.Language, options, profiles);
            unsorted.Add((group.Key, declarations, RawSegments(declarations[0], profile)));
        }

        // Shallowest first, so a symbol's parent is always registered before the symbol itself -- see
        // RegisterCodeId for why that matters. Ordering by depth first does not weaken §3.3's Ordinal
        // tie-break: two symbols can only compete for one id if they share a parent path, and sharing a
        // parent path means sharing a depth, so the tie-break still runs on Ordinal name order within
        // every group that could actually collide.
        //
        // The depth key is doing real work even though the ThenBy on Container usually reaches the same
        // answer on its own. In the canonical case a child's container is its parent's container with
        // the parent's name appended, which makes the parent's container a proper Ordinal PREFIX of the
        // child's, and a prefix sorts first -- so Container order already puts parents ahead of
        // children, and deleting this key would look free. It is not: SplitContainer drops empty
        // entries, so more than one container spelling denotes the same structural path, and the moment
        // two spellings differ textually (`.N.Log` and `N.Log` split identically, but `.` is 0x2E and
        // `N` is 0x4E) the textual order stops tracking the structural one and a child sorts ahead of
        // its own parent. That is what CodeConceptGeneratorTests pins, so this key cannot be removed as
        // dead code on the grounds that nothing observes it.
        var groups = unsorted
            .OrderBy(g => g.RawSegments.Length)
            .ThenBy(g => g.Key.Language, StringComparer.Ordinal)
            .ThenBy(g => g.Key.Container, StringComparer.Ordinal)
            .ThenBy(g => g.Key.Name, StringComparer.Ordinal)
            .ToList();

        // A call site names its caller as (container, name) and its target as (container, name) -- and
        // CallSite carries no language at all. Both joins below are therefore language-agnostic, which is
        // unambiguous today only because v1 ships exactly one profile. It stops being true the moment a
        // second one lands: two languages declaring the same container and name would attribute the same
        // call to BOTH concepts, and nothing anywhere would say so. A wrong edge in a knowledge bundle is
        // worse than a missing one -- an agent reading it gets a confidently false answer -- so the
        // assumption fails loudly here rather than sitting in a comment for someone to not read. This
        // throw is the specification of what to fix: give both joins a language component, which means
        // CallSite gaining a Language field, since the resolvers are what would have to supply it.
        var languages = groups
            .Select(g => g.Key.Language)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(l => l, StringComparer.Ordinal)
            .ToList();

        if (languages.Count > 1)
        {
            throw new InvalidOperationException(
                "ConceptGenerator: this code graph carries symbols in more than one language ("
                + string.Join(", ", languages)
                + "), but call edges are joined to their caller and to their target on (container, name) alone"
                + " -- CallSite carries no language -- so two languages declaring the same container and name"
                + " would attribute the same call to both concepts. Give both joins in"
                + " ConceptGenerator.BuildCodeConcepts a language component (CallSite needs a Language field)"
                + " before generating a multi-language bundle.");
        }

        // §5.1: a namespace needs a concept of its own, because a link to its DIRECTORY cannot exist --
        // `index.md` is a reserved file, not a concept, so `/code/csharp/okf4net/index` would be a
        // BrokenLink. A container concept coexists with its directory exactly as a type's does
        // (`okf4net.md` beside `okf4net/`).
        //
        // Which containers need one is read off the raw paths rather than from a `SymbolKind.Namespace`
        // symbol, because no shipped extractor emits one: every proper prefix of a symbol's raw path,
        // down to `[code, language, X]`, that no symbol group already claims. An unclaimed prefix is a
        // namespace or module in every case the C# profile can produce (types ARE extracted, so a
        // member's type prefix is claimed). It is presented as a namespace even in the one case it
        // might not be -- a type whose own declaration was never extracted while its members were. The
        // tempting inference from the other end, "a prefix with a MEMBER child is a type", is what a
        // reader of C# would expect and is exactly wrong for the next profile: a TypeScript member's
        // immediate container is a module, not a type, so that rule would mislabel every module.
        var claimed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in groups)
        {
            claimed.Add(RawKey(group.RawSegments));
        }

        var containerSegments = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var group in groups)
        {
            for (var length = MinimumContainerDepth; length < group.RawSegments.Length; length++)
            {
                var prefix = group.RawSegments[..length];
                var key = RawKey(prefix);
                if (!claimed.Contains(key))
                {
                    containerSegments.TryAdd(key, prefix);
                }
            }
        }

        // Sorted out of the dictionary immediately: from here on this list, not the hash table, is what
        // decides registration order and output order (§6.2).
        var containers = containerSegments
            .OrderBy(entry => entry.Key, StringComparer.Ordinal)
            .Select(entry => entry.Value)
            .ToList();

        // Pass 1 -- ids. Registered in the sorted order above so §3.3's numeric tie-break is decided by
        // the Ordinal order of the symbols' own names, not by which file the scanner happened to reach
        // first: a file move or a line shift must not renumber anything.
        //
        // Level by level rather than in one sweep, because containers and symbols interleave by depth:
        // a container must be registered before the symbols nested under it (they hang off its
        // REGISTERED id, so that an escaped name -- `Index`, `Log` -- takes its children with it), and
        // the symbols one level up must be registered before it. Within a level, symbols go first: a
        // real declaration outranks a synthesized container when both want the same id, and the loser
        // takes the numeric suffix.
        var ids = new Dictionary<SymbolKey, ConceptId>();
        var idsByName = new Dictionary<(string Container, string Name), ConceptId>();
        var primaryByName = new Dictionary<(string Container, string Name), SymbolFact>();
        var registeredByRawPath = new Dictionary<string, ConceptId>(StringComparer.Ordinal);
        var containerIds = new Dictionary<string, ConceptId>(StringComparer.Ordinal);
        var titlesByRawPath = new Dictionary<string, string>(StringComparer.Ordinal);

        var maxDepth = 0;
        foreach (var group in groups)
        {
            maxDepth = Math.Max(maxDepth, group.RawSegments.Length);
        }

        foreach (var container in containers)
        {
            maxDepth = Math.Max(maxDepth, container.Length);
        }

        for (var depth = MinimumContainerDepth; depth <= maxDepth; depth++)
        {
            foreach (var (key, declarations, rawSegments) in groups.Where(g => g.RawSegments.Length == depth))
            {
                var profile = ProfileFor(key.Language, options, profiles);
                var id = RegisterCodeId(declarations[0], profile, registry, rawSegments, registeredByRawPath);
                ids[key] = id;
                registeredByRawPath.TryAdd(RawKey(rawSegments), id);
                titlesByRawPath.TryAdd(RawKey(rawSegments), QualifiedTitle(declarations[0], profile));

                // Link targets, keyed the way an edge names them: (container, name), no language. The guard
                // above is what makes that key unambiguous rather than merely lucky; TryAdd's first-wins is
                // then only a same-language duplicate guard, which the group key already rules out.
                idsByName.TryAdd((key.Container, key.Name), id);
                primaryByName.TryAdd((key.Container, key.Name), declarations[0]);
            }

            foreach (var segments in containers.Where(c => c.Length == depth))
            {
                var containerKey = RawKey(segments);
                var id = RegisterContainerId(segments, registry, registeredByRawPath);
                containerIds[containerKey] = id;
                registeredByRawPath.TryAdd(containerKey, id);
                titlesByRawPath.TryAdd(containerKey, ContainerTitle(segments));
            }
        }

        // Calls are hung off the CALLER's concept (§4.5 rules out the reverse direction), so index the
        // edges by caller once rather than rescanning the edge list per concept.
        var callsByCaller = new Dictionary<(string Container, string Name), List<ResolvedEdge>>();
        foreach (var edge in graph.Edges
            .OrderBy(e => e.Site.CallerContainer, StringComparer.Ordinal)
            .ThenBy(e => e.Site.CallerName, StringComparer.Ordinal)
            .ThenBy(e => e.Site.CalledName, StringComparer.Ordinal)
            .ThenBy(e => e.Site.RelativePath, StringComparer.Ordinal)
            .ThenBy(e => e.Site.Offset))
        {
            var callerKey = (edge.Site.CallerContainer, edge.Site.CallerName);
            if (!callsByCaller.TryGetValue(callerKey, out var list))
            {
                list = [];
                callsByCaller[callerKey] = list;
            }

            list.Add(edge);
        }

        // The containment spine itself (§5.2): one edge per parent, pointing exactly one level down.
        // A node's parent is its raw path minus the last segment, which is why the raw paths -- not the
        // ids, which escaping and numeric suffixes can move -- are what the tree is built on.
        var childrenByParent = new Dictionary<string, List<Child>>(StringComparer.Ordinal);

        void Attach(string[] segments)
        {
            if (segments.Length <= MinimumContainerDepth)
            {
                return;
            }

            var key = RawKey(segments);
            var parentKey = RawKey(segments[..^1]);
            if (!registeredByRawPath.TryGetValue(key, out var id) || !registeredByRawPath.ContainsKey(parentKey))
            {
                return;
            }

            if (!childrenByParent.TryGetValue(parentKey, out var siblings))
            {
                siblings = [];
                childrenByParent[parentKey] = siblings;
            }

            siblings.Add(new Child(id, titlesByRawPath[key]));
        }

        foreach (var group in groups)
        {
            Attach(group.RawSegments);
        }

        foreach (var container in containers)
        {
            Attach(container);
        }

        var attribution = AttributePackages(groups, containerIds, registeredByRawPath, titlesByRawPath, packages, options);

        // Pass 2 -- documents.
        var concepts = new List<GeneratedConcept>(groups.Count + containers.Count);
        for (var depth = MinimumContainerDepth; depth <= maxDepth; depth++)
        {
            foreach (var (key, declarations, rawSegments) in groups.Where(g => g.RawSegments.Length == depth))
            {
                var id = ids[key];
                var profile = ProfileFor(key.Language, options, profiles);
                callsByCaller.TryGetValue((key.Container, key.Name), out var edges);
                var extras = ExtrasFor(RawKey(rawSegments), childrenByParent, attribution, options.SourceOwnership, declarations);

                concepts.Add(new GeneratedConcept(id, BuildCodeConcept(
                    id, declarations, profile, edges ?? [], idsByName, primaryByName, options, extras)));
            }

            foreach (var segments in containers.Where(c => c.Length == depth))
            {
                // No ownership and no declarations: a container is not declared in a file, so it has no
                // framework of its own to be absent from -- that is a fact about a symbol, and it is
                // stated on the symbol's own concept.
                var containerKey = RawKey(segments);
                var extras = ExtrasFor(containerKey, childrenByParent, attribution, ownership: null, declarations: []);

                concepts.Add(new GeneratedConcept(
                    containerIds[containerKey],
                    BuildContainerConcept(containerIds[containerKey], segments, titlesByRawPath[containerKey], extras, options)));
            }
        }

        return new CodeFamily(concepts, attribution.PackageChildren);
    }

    /// <summary>
    /// Everything a concept's body needs beyond its own declarations: the one level of containment
    /// below it, the other packages that also compile its sources, and the target frameworks it is
    /// absent from.
    /// </summary>
    private sealed record NodeExtras(
        IReadOnlyList<Child> Children,
        IReadOnlyList<string> AlsoCompiledBy,
        IReadOnlyList<string> AbsentFrameworks)
    {
        /// <summary>A leaf with nothing to add: no children, no shared sources, no absent framework.</summary>
        public static NodeExtras None { get; } = new([], [], []);
    }

    private static NodeExtras ExtrasFor(
        string rawKey,
        IReadOnlyDictionary<string, List<Child>> childrenByParent,
        Attribution attribution,
        SourceOwnershipMap? ownership,
        IReadOnlyList<SymbolFact> declarations)
    {
        var children = childrenByParent.TryGetValue(rawKey, out var found) ? found : (IReadOnlyList<Child>)[];
        var shared = attribution.AlsoCompiledBy.TryGetValue(rawKey, out var others) ? others : (IReadOnlyList<string>)[];
        var absent = AbsentFrameworks(ownership, declarations);

        return children.Count == 0 && shared.Count == 0 && absent.Count == 0
            ? NodeExtras.None
            : new NodeExtras(children, shared, absent);
    }

    /// <summary>
    /// The package half of the containment spine: which namespaces each package concept links down to,
    /// and, where sources are shared, which other packages a namespace names.
    /// </summary>
    private sealed record Attribution(
        IReadOnlyDictionary<string, List<Child>> PackageChildren,
        IReadOnlyDictionary<string, List<string>> AlsoCompiledBy);

    /// <summary>
    /// Attributes each code container to the package that compiles its sources (§5.1), from the
    /// <c>Compile</c> item set the composition root supplied -- never from the directory tree.
    ///
    /// <para><b>With no map, nothing is attributed</b> and the run says so through
    /// <see cref="GenerateOptions.Note"/>. That is the whole point of the seam: a missing link leaves
    /// the spine incomplete, which is visible and costs a reader one hop; a link guessed from directory
    /// layout is wrong whenever a project adds, removes or links sources across directories, and a
    /// wrong edge in a knowledge bundle is a confidently false answer.</para>
    ///
    /// <para>Three rules, all of them §5.1's. A file claimed by several projects belongs to the
    /// <see cref="StringComparer.Ordinal"/>-first <c>.csproj</c>, and the others are named in the
    /// concept rather than duplicating it. A package links to the <i>minimal</i> containers it declares
    /// into -- if it declares into both <c>N</c> and <c>N.Sub</c>, only <c>N</c> is linked, because
    /// <c>N</c> already lists <c>N.Sub</c> one level down (§5.2). And a group whose nearest container is
    /// none at all (a type in the global namespace) is attached directly, so a package with no
    /// namespace anywhere still has a level below it.</para>
    /// </summary>
    private static Attribution AttributePackages(
        IReadOnlyList<(SymbolKey Key, IReadOnlyList<SymbolFact> Declarations, string[] RawSegments)> groups,
        IReadOnlyDictionary<string, ConceptId> containerIds,
        IReadOnlyDictionary<string, ConceptId> registeredByRawPath,
        IReadOnlyDictionary<string, string> titlesByRawPath,
        IReadOnlyList<(ConceptId Id, PackageManifest Manifest)> packages,
        GenerateOptions options)
    {
        var children = new Dictionary<string, List<Child>>(StringComparer.Ordinal);
        var alsoCompiledBy = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        if (options.SourceOwnership is not { } ownership)
        {
            if (packages.Count > 0 && groups.Count > 0)
            {
                options.Note?.Invoke(
                    "no source-ownership map was supplied, so no package -> namespace containment link was emitted."
                    + " A namespace's package is read from MSBuild's `Compile` item set, never from the directory"
                    + " tree: a project can add, remove and link sources across directories, so a directory-derived"
                    + " link would attribute a namespace to the wrong package.");
            }

            return new Attribution(children, alsoCompiledBy);
        }

        // Normalized by the map's own rule, not by this file's NormalizeSeparators: this is the join
        // key between a project and its package concept, and two normalization rules would make the
        // join miss silently rather than fail.
        var packagesByProject = new Dictionary<string, (ConceptId Id, string Name)>(StringComparer.Ordinal);
        foreach (var (id, manifest) in packages)
        {
            packagesByProject.TryAdd(SourceOwnershipMap.Normalize(manifest.RelativePath), (id, manifest.Name));
        }

        var targets = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        var shared = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        var candidates = new HashSet<string>(StringComparer.Ordinal);
        var attributed = new HashSet<string>(StringComparer.Ordinal);

        foreach (var group in groups)
        {
            var attachKey = AttachmentKey(group.RawSegments, containerIds);
            candidates.Add(attachKey);

            foreach (var path in group.Declarations
                .Select(declaration => NormalizeSeparators(declaration.RelativePath))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(path => path, StringComparer.Ordinal))
            {
                // The Ordinal-first claimant, among the claimants that ARE a detected package. A project
                // with no package concept -- a test project, a tool outside the solution -- is not
                // something to attach to, and letting it win the tie would drop the link entirely even
                // though a real package compiles the same file. The order is still the .csproj paths'
                // own Ordinal order, so which package wins does not depend on scan order.
                (ConceptId Id, string Name)? owner = null;
                var others = new List<string>();

                foreach (var project in ownership.ClaimantsOf(path))
                {
                    if (!packagesByProject.TryGetValue(project, out var package))
                    {
                        continue;
                    }

                    if (owner is null)
                    {
                        owner = package;
                    }
                    else
                    {
                        others.Add(package.Name);
                    }
                }

                if (owner is not { } attachedTo)
                {
                    continue;
                }

                AddSorted(targets, attachedTo.Id.ToString(), attachKey);
                attributed.Add(attachKey);

                foreach (var other in others)
                {
                    AddSorted(shared, attachKey, other);
                }
            }
        }

        foreach (var (packageId, keys) in targets)
        {
            // Minimal under the ancestor relation: a key whose own ancestor is also claimed by this
            // package is already reachable one level down from that ancestor (§5.2).
            children[packageId] =
            [
                .. keys
                    .Where(key => !keys.Any(other => IsProperAncestor(other, key)) && registeredByRawPath.ContainsKey(key))
                    .Select(key => new Child(registeredByRawPath[key], titlesByRawPath[key])),
            ];
        }

        foreach (var (key, names) in shared)
        {
            alsoCompiledBy[key] = [.. names];
        }

        var orphans = candidates.Count - attributed.Count;
        if (orphans > 0)
        {
            options.Note?.Invoke(
                $"{orphans.ToString(CultureInfo.InvariantCulture)} code container(s) were not attributed to a package:"
                + " no detected package's `Compile` item set claims the sources that declare them, so they are"
                + " reachable from their own parent but not from any package concept.");
        }

        return new Attribution(children, alsoCompiledBy);
    }

    /// <summary>
    /// The node a package attaches a symbol group to: the deepest container above it, or -- for a
    /// symbol with no container at all, such as a type in the global namespace -- the group itself.
    /// </summary>
    private static string AttachmentKey(string[] rawSegments, IReadOnlyDictionary<string, ConceptId> containerIds)
    {
        for (var length = rawSegments.Length - 1; length >= MinimumContainerDepth; length--)
        {
            var key = RawKey(rawSegments[..length]);
            if (containerIds.ContainsKey(key))
            {
                return key;
            }
        }

        return RawKey(rawSegments);
    }

    /// <summary>
    /// True when <paramref name="candidate"/> is a strict ancestor of <paramref name="key"/> in the raw
    /// path tree. Both are <see cref="RawKey"/> strings, whose <c>NUL</c> join is what makes the prefix
    /// test exact: <c>[A, BC]</c> and <c>[AB, C]</c> share no textual prefix once the separator cannot
    /// occur inside a segment.
    /// </summary>
    private static bool IsProperAncestor(string candidate, string key) =>
        key.Length > candidate.Length && key.StartsWith(candidate + char.MinValue, StringComparison.Ordinal);

    /// <summary>
    /// The target frameworks every declaration of this symbol is excluded from -- so a symbol behind an
    /// <c>#if</c>-conditioned <c>Compile</c> item says so instead of silently claiming to exist under
    /// every framework its package targets (§5.1's multi-TFM rule: the symbols are the union, and the
    /// gaps are stated). Intersected across declarations, never unioned: a symbol declared in a file
    /// that IS compiled for a framework exists there, whatever its other declarations do.
    /// </summary>
    private static IReadOnlyList<string> AbsentFrameworks(SourceOwnershipMap? ownership, IReadOnlyList<SymbolFact> declarations)
    {
        if (ownership is null || declarations.Count == 0)
        {
            return [];
        }

        List<string>? absent = null;
        foreach (var path in declarations
            .Select(declaration => NormalizeSeparators(declaration.RelativePath))
            .Distinct(StringComparer.Ordinal))
        {
            var fileAbsent = ownership.FrameworksAbsentFrom(path);
            if (fileAbsent.Count == 0)
            {
                return [];
            }

            absent = absent is null ? [.. fileAbsent] : [.. absent.Intersect(fileAbsent, StringComparer.Ordinal)];
            if (absent.Count == 0)
            {
                return [];
            }
        }

        absent?.Sort(StringComparer.Ordinal);

        return absent ?? [];
    }

    private static void AddSorted(Dictionary<string, SortedSet<string>> map, string key, string value)
    {
        if (!map.TryGetValue(key, out var values))
        {
            values = new SortedSet<string>(StringComparer.Ordinal);
            map[key] = values;
        }

        values.Add(value);
    }

    private static OkfDocument BuildCodeConcept(
        ConceptId id,
        IReadOnlyList<SymbolFact> declarations,
        LanguageProfile profile,
        IReadOnlyList<ResolvedEdge> edges,
        IReadOnlyDictionary<(string Container, string Name), ConceptId> idsByName,
        IReadOnlyDictionary<(string Container, string Name), SymbolFact> primaryByName,
        GenerateOptions options,
        NodeExtras extras)
    {
        var primary = declarations[0];
        var title = QualifiedTitle(primary, profile);
        var (description, descriptionSource) = Descriptions.Resolve(primary, options.ExistingFrontmatter?.Invoke(id));

        var builder = OkfDocumentBuilder
            .ForType(ConceptTypeName(primary))
            .Title(title)
            .Description(description)
            .Tags(ConceptTags(primary))
            .Body(BuildCodeBody(title, description, declarations, edges, idsByName, primaryByName, profile, extras));

        // §4.3: a URL short-circuits the validator's path classifier, so it is the only shape of
        // `resource` a code concept can carry without earning a warning. No URL => no field.
        if (ResourceUrl(primary, options) is { } resource)
        {
            builder = builder.Resource(resource);
        }

        // §4.5: no `sources` block -- it would duplicate `resource` on every one of ~470 concepts.
        builder = builder.Extension(DescriptionResolver.DescriptionSourceKey, new YamlString(descriptionSource));

        // §4.4: `by` and never `at`. All ~470 concepts are generated in one pass, so a per-concept
        // timestamp would store a single fact 470 times AND rewrite all 470 files on every
        // regeneration -- the bundle's `git diff` would show 470 timestamps instead of what changed in
        // the code. `at` (and `revision`) belong to `overview` alone.
        var generated = new YamlMapping();
        generated.Insert("by", new YamlString(ProducerActor));
        builder = builder.Extension("generated", generated);

        return builder.Build();
    }

    private static string BuildCodeBody(
        string title,
        string description,
        IReadOnlyList<SymbolFact> declarations,
        IReadOnlyList<ResolvedEdge> edges,
        IReadOnlyDictionary<(string Container, string Name), ConceptId> idsByName,
        IReadOnlyDictionary<(string Container, string Name), SymbolFact> primaryByName,
        LanguageProfile profile,
        NodeExtras extras)
    {
        var body = new StringBuilder();
        body.Append("# ").Append(title).Append("\n\n");
        body.Append(description.TrimEnd()).Append('\n');

        // §3.2: one concept per (container, name), so an overload set is one concept listing every
        // signature with its own span, rather than `validate-2`/`validate-3` ids that renumber their
        // neighbours whenever an overload is added.
        body.Append("\n## Signatures\n\n");
        foreach (var declaration in declarations)
        {
            body.Append("- ").Append(CodeSpan(declaration.Signature))
                .Append(" — ").Append(CodeSpan(SpanLabel(declaration))).Append('\n');
        }

        // §5.1's multi-TFM rule, stated where a reader of this symbol will see it.
        AppendAbsentFrameworks(body, extras.AbsentFrameworks);

        // §5.2's descending family, kept in its own section: `okf graph` sees containment and calls
        // alike, and it is the heading that says which is which.
        AppendContains(body, extras.Children);
        AppendAlsoCompiledBy(body, extras.AlsoCompiledBy);

        // Both Exact and ByName become links; only Unresolved stays text. Measured: 54-58% of call
        // sites have no declaration anywhere in the repository (they are BCL or NuGet), so linking
        // them would emit that many BrokenLink diagnostics and drown `okf validate`. In a code span
        // they stay readable and greppable without polluting the graph (§4.5).
        var links = new List<string>();
        var unresolved = new List<string>();

        foreach (var edge in edges)
        {
            if (edge.Confidence != EdgeConfidence.Unresolved
                && edge.TargetContainer is { } targetContainer
                && edge.TargetName is { } targetName
                && idsByName.TryGetValue((targetContainer, targetName), out var targetId))
            {
                var targetTitle = primaryByName.TryGetValue((targetContainer, targetName), out var targetFact)
                    ? QualifiedTitle(targetFact, profile)
                    : targetName;

                // Absolute (§6.1's recommended form): the generator never does relative-path
                // arithmetic, and `okf graph` resolves the link from the id alone.
                links.Add($"- [{LinkText(targetTitle)}](/{targetId})");
            }
            else
            {
                // Defensive: CodeGraph guarantees a resolved target exists in Symbols, so the lookup
                // above cannot miss. If it ever did, rendering text is the safe direction -- a link to
                // a concept that was never generated is a broken link in the user's bundle.
                unresolved.Add($"- {CodeSpan(edge.Site.CalledName)}");
            }
        }

        AppendSection(body, "## Calls", links);
        AppendSection(body, "## Calls (unresolved)", unresolved);

        // §4.5: no `## Called by`. Bundle.Backlinks(id) is public and computed at load, so
        // materialising reverse links would duplicate derivable information and double the churn --
        // adding one call would rewrite the callee's concept too.
        return body.ToString();
    }

    /// <summary>
    /// The concept for one container -- a namespace, a module, or a type nothing declared in scope --
    /// which exists so the level above it has something to link to at all (§5.1: <c>index.md</c> is
    /// reserved, so a directory is not a link target). It carries no <c>resource</c>: a container is not
    /// declared in one file, and §4.3 admits only a URL there.
    /// </summary>
    private static OkfDocument BuildContainerConcept(
        ConceptId id,
        string[] segments,
        string title,
        NodeExtras extras,
        GenerateOptions options)
    {
        var language = segments[1];
        var (description, descriptionSource) =
            ContainerDescriptions.Resolve(ContainerFact(segments), options.ExistingFrontmatter?.Invoke(id));

        var body = new StringBuilder();
        body.Append("# ").Append(title).Append("\n\n");
        body.Append(description.TrimEnd()).Append('\n');
        AppendContains(body, extras.Children);
        AppendAlsoCompiledBy(body, extras.AlsoCompiledBy);

        string[] tags = language.Length == 0 ? [ContainerToken] : [language, ContainerToken];

        var builder = OkfDocumentBuilder
            .ForType($"{LanguageDisplayName(language)} Container")
            .Title(title)
            .Description(description)
            .Tags(tags)
            .Body(body.ToString())
            .Extension(DescriptionResolver.DescriptionSourceKey, new YamlString(descriptionSource));

        // §4.4, exactly as for a symbol concept: `by` and never `at`.
        var generated = new YamlMapping();
        generated.Insert("by", new YamlString(ProducerActor));

        return builder.Extension("generated", generated).Build();
    }

    /// <summary>
    /// A container's title: the container qualified by its immediate owner (<c>OKF4net.Yaml</c>), which
    /// is the same shape <see cref="QualifiedTitle"/> gives a symbol, joined the same way -- with
    /// <c>.</c>, whatever the language's own separator, because <see cref="LanguageProfile"/> exposes
    /// the split direction only and a title is prose, not an id.
    /// </summary>
    private static string ContainerTitle(string[] segments) =>
        segments.Length <= MinimumContainerDepth ? segments[^1] : $"{segments[^2]}.{segments[^1]}";

    /// <summary>
    /// A container rendered as a <see cref="SymbolFact"/> <b>for the description chain only</b>, so a
    /// namespace gets §4.2's field preservation (a hand-written description survives regeneration) from
    /// the same <see cref="DescriptionResolver"/> as every other concept, rather than a second, forkable
    /// copy of that rule.
    ///
    /// <para><b>It is not a declaration and must never be treated as one.</b> Its
    /// <see cref="SymbolFact.RelativePath"/> is empty and its offsets are zero, so handing it to
    /// <see cref="ResourceUrl"/> would mint a permalink to line 0 of the repository root -- a link that
    /// looks right and points nowhere. It goes to <see cref="DescriptionResolver.Resolve"/> and nowhere
    /// else.</para>
    ///
    /// <para><see cref="SymbolKind.Namespace"/> is the nearest of the three kinds and is not read by
    /// <see cref="ContainerSource"/>, which is the only source this fact ever reaches; the concept's own
    /// vocabulary is <see cref="ContainerToken"/>'s, precisely because the kind is what this pass cannot
    /// know.</para>
    /// </summary>
    private static SymbolFact ContainerFact(string[] segments) => new(
        SymbolKind.Namespace,
        Language: segments[1],
        Container: string.Join('.', segments[2..^1]),
        Name: segments[^1],
        Signature: string.Empty,
        SymbolVisibility.Public,
        RelativePath: string.Empty,
        StartOffset: 0,
        EndOffset: 0,
        StartLine: 0,
        EndLine: 0,
        DocComment: null);

    /// <summary>The one level of containment below this concept (§5.2), as absolute links (§6.1).</summary>
    private static void AppendContains(StringBuilder body, IReadOnlyList<Child> children) =>
        AppendSection(body, "## Contains", [.. children.Select(child => $"- [{LinkText(child.Title)}](/{child.Id})")]);

    /// <summary>
    /// The other packages whose <c>Compile</c> item set also claims this concept's sources (§5.1).
    /// Deliberately plain text rather than links: containment is one edge per parent, and turning this
    /// mention into a link would give the concept a second incoming parent -- the duplication the
    /// Ordinal-first rule exists to prevent.
    /// </summary>
    private static void AppendAlsoCompiledBy(StringBuilder body, IReadOnlyList<string> packageNames) =>
        AppendSection(body, "## Also compiled by", [.. packageNames.Select(name => $"- {CodeSpan(name)}")]);

    /// <summary>The frameworks this symbol's package targets but does not compile it for (§5.1).</summary>
    private static void AppendAbsentFrameworks(StringBuilder body, IReadOnlyList<string> frameworks) =>
        AppendSection(body, "## Target frameworks", [.. frameworks.Select(framework => $"- Absent from {CodeSpan(framework)}.")]);

    /// <summary>
    /// Appends one bullet section, deduplicated and sorted <see cref="StringComparer.Ordinal"/>, or
    /// nothing at all when there is nothing to list. The distinct/sort is on the fully rendered line,
    /// so two call sites from the same caller to the same target collapse to one bullet.
    /// </summary>
    private static void AppendSection(StringBuilder body, string heading, List<string> lines)
    {
        var rendered = lines.Distinct(StringComparer.Ordinal).OrderBy(l => l, StringComparer.Ordinal).ToList();
        if (rendered.Count == 0)
        {
            return;
        }

        body.Append('\n').Append(heading).Append("\n\n");
        foreach (var line in rendered)
        {
            body.Append(line).Append('\n');
        }
    }

    /// <summary>
    /// Allocates this symbol's concept id.
    ///
    /// <para><b>Preferred path: hang the symbol under its parent's REGISTERED id, not under its parent's
    /// raw name.</b> The two differ whenever the parent's own segment had to be escaped, and a type
    /// named <c>Log</c> or <c>Index</c> is enough to trigger it -- <c>System.Index</c> ships in the BCL.
    /// Deriving from the raw name there would register the type at <c>code/csharp/n/log-2</c> while its
    /// members kept the untouched container segment and landed in <c>code/csharp/n/log/</c>: a concept
    /// file sitting beside a directory that is not its own. That breaks §3.3's invariant that a type
    /// becomes both <c>log.md</c> AND <c>log/</c> -- the correspondence Task 9's containment spine is
    /// built on -- and <c>IndexGenerator</c> would list the orphaned directory as a child of the
    /// namespace rather than of the type. Groups are registered shallowest-first precisely so the
    /// parent's id is already known here.</para>
    ///
    /// <para><b>Fallback path.</b> With no registered parent (a member whose type was not extracted, or
    /// a type whose namespace has no concept until Task 9), the id is built from the raw names by
    /// <see cref="CodeConceptIds.For"/>. That call can fail outright, and not hypothetically: a C#
    /// identifier may legally be entirely non-ASCII (<c>public void 概要()</c>), and
    /// <see cref="ConceptId.Slugify"/> maps every such character to <c>-</c> and then rejects the empty
    /// result. §2.3's policy is that hostile or merely unusual input degrades the output and never
    /// aborts the run, so the candidates are tried in order, each keeping as much of the real path as
    /// the previous failure allows: the symbol's own name replaced by its kind, then the container
    /// dropped, then both. The registry disambiguates whatever lands.</para>
    /// </summary>
    private static ConceptId RegisterCodeId(
        SymbolFact fact,
        LanguageProfile profile,
        ConceptIdRegistry registry,
        IReadOnlyList<string> rawSegments,
        IReadOnlyDictionary<string, ConceptId> registeredByRawPath)
    {
        var token = KindTag(fact.Kind);

        if (registeredByRawPath.TryGetValue(RawKey(rawSegments.Take(rawSegments.Count - 1)), out var parentId))
        {
            // Only the leaf can fail here -- the parent id is already a registered, valid ConceptId --
            // so the ladder collapses to two rungs: the symbol's own name, then its kind.
            foreach (var candidateName in new[] { fact.Name, token })
            {
                try
                {
                    return registry.Register(parentId.ToString(), LeafSegment(fact with { Name = candidateName }, profile));
                }
                catch (ConceptIdException)
                {
                }
            }
        }

        SymbolFact[] candidates =
        [
            fact,
            fact with { Name = token },
            fact with { Container = string.Empty },
            fact with { Container = string.Empty, Name = token },
        ];

        foreach (var candidate in candidates)
        {
            try
            {
                var path = CodeConceptIds.For(candidate, profile);
                var slash = path.LastIndexOf('/');

                // The last segment is already slugified by CodeConceptIds; Register slugifies again,
                // which is idempotent, and runs the reserved-segment and collision rules (§3.3).
                return slash < 0
                    ? registry.Register(string.Empty, path)
                    : registry.Register(path[..slash], path[(slash + 1)..]);
            }
            catch (ConceptIdException)
            {
                // Either the name/container could not be slugified, or -- since CodeConceptIds does not
                // validate them -- the "code"/language segments could not form a concept id.
            }
        }

        // Reachable only when the language token itself is not a usable segment; "code" and the kind
        // token both always are, so this cannot throw.
        return registry.Register("code", token);
    }

    /// <summary>
    /// Allocates a container's concept id, on the same two paths and for the same reasons as
    /// <see cref="RegisterCodeId"/>: under its parent's <b>registered</b> id when there is one -- so a
    /// namespace under an escaped ancestor follows that ancestor rather than splitting off into a
    /// directory nobody owns -- and otherwise from the raw path through
    /// <see cref="CodeConceptIds.ForContainer"/>.
    ///
    /// <para>The ladder is the same shape too, because the same input can defeat it: a namespace segment
    /// may be entirely non-ASCII, which <see cref="ConceptId.Slugify"/> rejects. Each rung keeps as much
    /// of the real path as the previous failure allows -- the segment's own name, then the generic
    /// container token -- and §2.3 forbids the alternative of aborting the run.</para>
    /// </summary>
    private static ConceptId RegisterContainerId(
        string[] segments,
        ConceptIdRegistry registry,
        IReadOnlyDictionary<string, ConceptId> registeredByRawPath)
    {
        var language = segments[1];

        if (registeredByRawPath.TryGetValue(RawKey(segments[..^1]), out var parentId))
        {
            foreach (var candidate in new[] { segments[^1], ContainerToken })
            {
                try
                {
                    return registry.Register(parentId.ToString(), ContainerLeafSegment(language, candidate));
                }
                catch (ConceptIdException)
                {
                }
            }
        }

        string[][] candidates = [segments[2..], [ContainerToken]];
        foreach (var candidateSegments in candidates)
        {
            try
            {
                var path = CodeConceptIds.ForContainer(language, candidateSegments);
                var slash = path.LastIndexOf('/');

                return slash < 0
                    ? registry.Register(string.Empty, path)
                    : registry.Register(path[..slash], path[(slash + 1)..]);
            }
            catch (ConceptIdException)
            {
                // Either a container segment could not be slugified, or -- since CodeConceptIds does not
                // validate them -- the "code"/language segments could not form a concept id.
            }
        }

        // Reachable only when the language token itself is not a usable segment; "code" and the
        // container token both always are, so this cannot throw.
        return registry.Register("code", ContainerToken);
    }

    /// <summary>
    /// The final id segment for a container's own name, taken from
    /// <see cref="CodeConceptIds.ForContainer"/> on a single-segment path rather than reimplemented --
    /// the same reason <see cref="LeafSegment"/> reads <see cref="CodeConceptIds.For"/>: that type owns
    /// the word-boundary tokenizer (§3.1), and a second copy of it here could drift.
    /// </summary>
    /// <exception cref="ConceptIdException">The name (or the language) cannot form an id segment.</exception>
    private static string ContainerLeafSegment(string language, string name)
    {
        var path = CodeConceptIds.ForContainer(language, [name]);
        var slash = path.LastIndexOf('/');
        return slash < 0 ? path : path[(slash + 1)..];
    }

    /// <summary>
    /// The symbol's id path in <b>unslugified</b> form: <c>code</c>, the language, the container's own
    /// segments, then the name. Two uses, both keyed on the fact that a parent's raw segments are
    /// exactly its child's minus the last one -- a member of <c>N.Log</c> splits to
    /// <c>[code, csharp, N, Log]</c>, which is precisely the type <c>N.Log</c>'s own raw path. That is
    /// what lets <see cref="RegisterCodeId"/> find a parent without reconstructing a dotted name (which
    /// would have to guess the language's join separator, and <see cref="LanguageProfile"/> only offers
    /// the split direction). Its length also gives the depth the groups are sorted by.
    /// </summary>
    private static string[] RawSegments(SymbolFact fact, LanguageProfile profile)
    {
        var segments = new List<string>(4) { "code", fact.Language };
        segments.AddRange(profile.SplitContainer(fact.Container));
        segments.Add(fact.Name);
        return [.. segments];
    }

    /// <summary>
    /// Joins raw segments into one lookup key with <c>NUL</c>, which no identifier or namespace segment
    /// in any supported language can contain -- so the key is unambiguous where a <c>.</c> or <c>/</c>
    /// join would merge <c>[A.B]</c> and <c>[A, B]</c> into the same string.
    /// </summary>
    private static string RawKey(IEnumerable<string> segments) => string.Join(char.MinValue, segments);

    /// <summary>
    /// The final id segment for this symbol's own name, taken from <see cref="CodeConceptIds.For"/> on
    /// a container-less copy rather than reimplemented: that method owns the word-boundary tokenizer
    /// (§3.1), and it appends the name segment identically whether or not there is a container, so this
    /// reads the real rule instead of forking a second copy of it that could drift.
    /// </summary>
    /// <exception cref="ConceptIdException">The name (or the language) cannot form an id segment.</exception>
    private static string LeafSegment(SymbolFact fact, LanguageProfile profile)
    {
        var path = CodeConceptIds.For(fact with { Container = string.Empty }, profile);
        var slash = path.LastIndexOf('/');
        return slash < 0 ? path : path[(slash + 1)..];
    }

    /// <summary>
    /// The profile to cut <paramref name="language"/>'s containers with. A symbol whose language matches
    /// none of <see cref="GenerateOptions.Profiles"/> is <b>not</b> skipped: this synthesizes a
    /// throwaway profile carrying only that language, so the symbol still gets a concept and gets the
    /// same id the real profile would have given it.
    ///
    /// <b>That substitution is valid under exactly one condition</b>, and it is stated at the other end
    /// too, on <see cref="LanguageProfile.SplitContainer"/> itself (neither note is complete alone --
    /// whoever changes that method will read its own doc, not this one): container splitting must stay a
    /// pure function of <see cref="LanguageProfile.Language"/>. Every other field synthesized here is
    /// empty, so the moment <see cref="LanguageProfile.SplitContainer"/> consults one of them, this
    /// profile stops standing in for the real one and starts silently producing different concept ids --
    /// which is an id churn, not a cosmetic difference. If that day comes, this method must become a
    /// hard requirement instead: no profile for the language, no concept.
    /// </summary>
    private static LanguageProfile ProfileFor(string language, GenerateOptions options, Dictionary<string, LanguageProfile> cache)
    {
        if (cache.TryGetValue(language, out var cached))
        {
            return cached;
        }

        var profile = options.Profiles.FirstOrDefault(p => string.Equals(p.Language, language, StringComparison.Ordinal))
            ?? new LanguageProfile(
                Language: language,
                GrammarName: string.Empty,
                DeclarationQuery: string.Empty,
                CallQuery: string.Empty,
                DocCommentPrefix: string.Empty,
                FileExtensions: []);

        cache[language] = profile;
        return profile;
    }

    /// <summary>
    /// The concept's <c>title</c>: the symbol qualified by its <i>immediate</i> owner
    /// (<c>Scanner.Scan</c>, <c>OKF4net.LinkScanner</c>), which is §4.1's shape. The full dotted
    /// container is deliberately not repeated -- the concept id already carries the whole hierarchy,
    /// and a title is what a reader and <c>ConceptSearch</c>'s title weighting see.
    /// </summary>
    private static string QualifiedTitle(SymbolFact fact, LanguageProfile profile)
    {
        var segments = profile.SplitContainer(fact.Container);
        return segments.Count == 0 ? fact.Name : $"{segments[^1]}.{fact.Name}";
    }

    /// <summary>The <c>type</c> field: e.g. <c>C# Member</c>, <c>C# Type</c>.</summary>
    private static string ConceptTypeName(SymbolFact fact) =>
        $"{LanguageDisplayName(fact.Language)} {KindNoun(fact.Kind)}";

    private static string LanguageDisplayName(string language) => language switch
    {
        "csharp" => "C#",
        "" => "Code",
        // Invariant, never culture-aware: a Turkish-locale build must not title a "typescript" symbol
        // "Typescript" with a dotless capital I lurking anywhere in the pipeline (§6.2).
        _ => char.ToUpperInvariant(language[0]) + language[1..],
    };

    private static string KindNoun(SymbolKind kind) => kind switch
    {
        SymbolKind.Type => "Type",
        SymbolKind.Namespace => "Namespace",
        _ => "Member",
    };

    private static string KindTag(SymbolKind kind) => kind switch
    {
        SymbolKind.Type => "type",
        SymbolKind.Namespace => "namespace",
        _ => "member",
    };

    private static string VisibilityTag(SymbolVisibility visibility) => visibility switch
    {
        SymbolVisibility.Public => "public",
        SymbolVisibility.Internal => "internal",
        _ => "private",
    };

    private static string[] ConceptTags(SymbolFact fact) =>
        fact.Language.Length == 0
            ? [KindTag(fact.Kind), VisibilityTag(fact.Visibility)]
            : [fact.Language, KindTag(fact.Kind), VisibilityTag(fact.Visibility)];

    /// <summary>
    /// The forge permalink for one declaration, or <see langword="null"/> when this run has nothing to
    /// build one from. See <see cref="GenerateOptions.RepoUrl"/> for why the alternative is no field
    /// at all rather than a repo-relative path. Built segment by segment with escaping, never by raw
    /// concatenation (§4.3), and the ref keeps its own <c>/</c> separators so <c>feature/x</c> still
    /// addresses a blob.
    /// </summary>
    private static string? ResourceUrl(SymbolFact declaration, GenerateOptions options)
    {
        if (options.RepoUrl is not { Length: > 0 } repoUrl || options.Rev is not { Length: > 0 } rev)
        {
            return null;
        }

        // A value that is not an absolute http(s) URL would not be classified as
        // FrontmatterResourceKind.Url by the validator, and would then be resolved as a path against
        // the concept's own directory -- the exact warning-per-concept outcome §4.3 exists to avoid.
        if (!Uri.TryCreate(repoUrl, UriKind.Absolute, out var parsed)
            || parsed.Scheme is not ("http" or "https"))
        {
            return null;
        }

        // From the PARSED uri, never the raw string: `https://github.com/o/r?x=1` trimmed as text yields
        // `https://github.com/o/r?x=1/blob/main/...`, which the validator still classifies as a Url and
        // still passes with no warning -- a silently wrong link, the worst of the available outcomes.
        // GetLeftPart(UriPartial.Path) drops the query and the fragment and keeps scheme, authority and
        // path, which is exactly the base a blob URL is built on.
        var basePart = parsed.GetLeftPart(UriPartial.Path).TrimEnd('/');
        var revPart = EscapePath(rev);
        var pathPart = EscapePath(NormalizeSeparators(declaration.RelativePath));

        return $"{basePart}/blob/{revPart}/{pathPart}{LineSpan(declaration)}";
    }

    private static string EscapePath(string path) =>
        string.Join('/', path.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString));

    private static string NormalizeSeparators(string path) => path.Replace('\\', '/');

    private static string LineSpan(SymbolFact declaration) =>
        declaration.StartLine == declaration.EndLine
            ? $"#L{declaration.StartLine.ToString(CultureInfo.InvariantCulture)}"
            : $"#L{declaration.StartLine.ToString(CultureInfo.InvariantCulture)}-L{declaration.EndLine.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>The <c>path#Lstart-Lend</c> label shown next to a signature in the body.</summary>
    private static string SpanLabel(SymbolFact declaration) =>
        NormalizeSeparators(declaration.RelativePath) + LineSpan(declaration);

    /// <summary>
    /// Wraps <paramref name="text"/> in a markdown code span that survives the text itself: the fence
    /// is one backtick longer than the longest backtick run inside, padded with spaces when the content
    /// starts or ends with a backtick, and control characters (a code span cannot contain a newline)
    /// are flattened to spaces. Signatures and called names come out of source files, which §2.3 treats
    /// as untrusted input -- a naive <c>$"`{text}`"</c> would let one of them close the span early and
    /// corrupt the rest of the document.
    /// </summary>
    private static string CodeSpan(string text)
    {
        var flat = Flatten(text);
        var fence = new string('`', LongestBacktickRun(flat) + 1);
        var pad = flat.Length == 0 || flat[0] == '`' || flat[^1] == '`' ? " " : string.Empty;

        return fence + pad + flat + pad + fence;
    }

    private static string Flatten(string text)
    {
        var chars = text.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (char.IsControl(chars[i]))
            {
                chars[i] = ' ';
            }
        }

        return new string(chars).Trim();
    }

    private static int LongestBacktickRun(string text)
    {
        var longest = 0;
        var current = 0;
        foreach (var c in text)
        {
            current = c == '`' ? current + 1 : 0;
            longest = Math.Max(longest, current);
        }

        return longest;
    }

    /// <summary>
    /// Escapes the display half of a markdown link. Symbol names are identifiers in practice, but they
    /// reach here from untrusted source text (§2.3), and a stray <c>]</c> would end the link text early
    /// and turn the rest of the line into prose.
    /// </summary>
    private static string LinkText(string text) =>
        Flatten(text).Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("[", "\\[", StringComparison.Ordinal)
            .Replace("]", "\\]", StringComparison.Ordinal);
}
