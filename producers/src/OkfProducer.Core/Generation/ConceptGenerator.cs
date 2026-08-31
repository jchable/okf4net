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

        var results = new List<GeneratedConcept>
        {
            new(registry.Register(string.Empty, "overview"), BuildOverview(snapshot)),
        };

        foreach (var package in snapshot.Packages)
        {
            var id = UniqueConceptId("packages", package.Name, registry);
            results.Add(new GeneratedConcept(id, BuildPackageConcept(package)));
        }

        foreach (var doc in snapshot.Docs)
        {
            var id = UniqueConceptId("docs", doc.Title, registry);
            results.Add(new GeneratedConcept(id, BuildDocConcept(doc)));
        }

        if (codeGraph is not null)
        {
            results.AddRange(BuildCodeConcepts(codeGraph, registry, options));
        }

        return results;
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

    private static OkfDocument BuildOverview(RepositorySnapshot snapshot)
    {
        var description = snapshot.Packages.Count switch
        {
            0 => $"Repository {snapshot.RepoName}.",
            1 => $"Repository {snapshot.RepoName}, containing 1 detected package.",
            var n => $"Repository {snapshot.RepoName}, containing {n.ToString(CultureInfo.InvariantCulture)} detected packages.",
        };

        return OkfDocumentBuilder
            .ForType("Repository")
            .Title(snapshot.RepoName)
            .Description(description)
            .Tags("repository")
            .Body($"# {snapshot.RepoName}\n\n{description}\n")
            .Build();
    }

    private static OkfDocument BuildPackageConcept(PackageManifest package)
    {
        var description = package.Description ?? $"{package.Ecosystem} package {package.Name}.";

        return OkfDocumentBuilder
            .ForType("Package")
            .Title(package.Name)
            .Description(description)
            .Tags(package.Ecosystem)
            .Resource(package.RelativePath)
            .AddSource(resource: package.RelativePath)
            .Body($"# {package.Name}\n\n{description}\n")
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
    /// Builds the <c>code/</c> family in two passes, and the order matters: every id must be allocated
    /// before any body is written, because a body links to <i>other</i> concepts' ids and those ids
    /// are only final once the registry has resolved every collision (§3.3).
    /// </summary>
    private static List<GeneratedConcept> BuildCodeConcepts(CodeGraphModel graph, ConceptIdRegistry registry, GenerateOptions options)
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

        // Pass 1 -- ids. Registered in the sorted order above so §3.3's numeric tie-break is decided by
        // the Ordinal order of the symbols' own names, not by which file the scanner happened to reach
        // first: a file move or a line shift must not renumber anything.
        var ids = new Dictionary<SymbolKey, ConceptId>();
        var idsByName = new Dictionary<(string Container, string Name), ConceptId>();
        var primaryByName = new Dictionary<(string Container, string Name), SymbolFact>();
        var registeredByRawPath = new Dictionary<string, ConceptId>(StringComparer.Ordinal);

        foreach (var (key, declarations, rawSegments) in groups)
        {
            var profile = ProfileFor(key.Language, options, profiles);
            var id = RegisterCodeId(declarations[0], profile, registry, rawSegments, registeredByRawPath);
            ids[key] = id;
            registeredByRawPath.TryAdd(RawKey(rawSegments), id);

            // Link targets, keyed the way an edge names them: (container, name), no language. The guard
            // above is what makes that key unambiguous rather than merely lucky; TryAdd's first-wins is
            // then only a same-language duplicate guard, which the group key already rules out.
            idsByName.TryAdd((key.Container, key.Name), id);
            primaryByName.TryAdd((key.Container, key.Name), declarations[0]);
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

        // Pass 2 -- documents.
        var concepts = new List<GeneratedConcept>(groups.Count);
        foreach (var (key, declarations, _) in groups)
        {
            var id = ids[key];
            var profile = ProfileFor(key.Language, options, profiles);
            callsByCaller.TryGetValue((key.Container, key.Name), out var edges);

            concepts.Add(new GeneratedConcept(id, BuildCodeConcept(
                id, declarations, profile, edges ?? [], idsByName, primaryByName, options)));
        }

        return concepts;
    }

    private static OkfDocument BuildCodeConcept(
        ConceptId id,
        IReadOnlyList<SymbolFact> declarations,
        LanguageProfile profile,
        IReadOnlyList<ResolvedEdge> edges,
        IReadOnlyDictionary<(string Container, string Name), ConceptId> idsByName,
        IReadOnlyDictionary<(string Container, string Name), SymbolFact> primaryByName,
        GenerateOptions options)
    {
        var primary = declarations[0];
        var title = QualifiedTitle(primary, profile);
        var (description, descriptionSource) = Descriptions.Resolve(primary, options.ExistingFrontmatter?.Invoke(id));

        var builder = OkfDocumentBuilder
            .ForType(ConceptTypeName(primary))
            .Title(title)
            .Description(description)
            .Tags(ConceptTags(primary))
            .Body(BuildCodeBody(title, description, declarations, edges, idsByName, primaryByName, profile));

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
        LanguageProfile profile)
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
