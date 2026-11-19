// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Reflection;
using OKF4net.Agents;
using OKF4net.Providers;
using OKF4net.Tests.Providers;

namespace OKF4net.Tests;

/// <summary>
/// The path-based members the provider seam replaced are kept one version, marked obsolete
/// (spec §9, §10): exactly these, each with a message that names its replacement and the
/// version that removes them, as a warning (never an error, so a host upgrading keeps
/// building); and the key-based members they project are populated.
/// </summary>
public class ObsoleteMembersTests
{
    /// <summary>Every assembly the test project references: the packaged libraries and the leaf executables.</summary>
    private static readonly Assembly[] Assemblies =
    [
        typeof(Bundle).Assembly,
        typeof(OkfBundleTools).Assembly,
        typeof(global::OKF4net.Attestation.AttestationOrchestrator).Assembly,
        typeof(global::OKF4net.Attestation.Containers.CliContainerEngine).Assembly,
        typeof(global::OKF4net.Catalog.CatalogManifestParser).Assembly,
        typeof(global::OKF4net.Catalog.Hosting.KnowledgeServiceCollectionExtensions).Assembly,
        typeof(global::OKF4net.Viewer.ViewerAssets).Assembly,
        typeof(global::OKF4net.Cli.OkfCli).Assembly,
        typeof(global::OKF4net.Render.OkfRenderCli).Assembly,
        typeof(global::OKF4net.Mcp.OkfMcpConfig).Assembly,
    ];

    /// <summary>Every exported type, and every public member of one (nested types counted once, as types), carrying <see cref="ObsoleteAttribute"/>.</summary>
    private static IEnumerable<(string Member, ObsoleteAttribute Attribute)> ObsoleteMembers() =>
        Assemblies
            .SelectMany(a => a.GetExportedTypes())
            .SelectMany(t => t.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Where(m => m.MemberType != MemberTypes.NestedType && !IsRequiredMembersGuard(m))
                .Select(m => (Member: $"{t.Name}.{m.Name}{Signature(m)}", Attribute: m.GetCustomAttribute<ObsoleteAttribute>()))
                .Append((Member: t.Name, Attribute: t.GetCustomAttribute<ObsoleteAttribute>())))
            .Where(x => x.Attribute is not null)
            .Select(x => (x.Member, x.Attribute!));

    /// <summary>
    /// The compiler marks the constructors of a type with <c>required</c> members with the synthesised
    /// <c>[Obsolete("Constructors of types with required members ...", error: true)]</c> (beside
    /// <c>CompilerFeatureRequired</c>) so that an old compiler refuses them: not an API decision, never
    /// counted. Only that synthesised guard is excluded: a user-written <c>[Obsolete]</c> constructor on such
    /// a type carries <c>CompilerFeatureRequired</c> too, and must stay in the inventory.
    /// </summary>
    private static bool IsRequiredMembersGuard(MemberInfo member) =>
        member is ConstructorInfo
        && member.GetCustomAttribute<ObsoleteAttribute>() is { IsError: true } obsolete
        && obsolete.Message?.StartsWith("Constructors of types with required members", StringComparison.Ordinal) == true
        && member.GetCustomAttributes().Any(a => a.GetType().Name == "CompilerFeatureRequiredAttribute");

    private sealed class RequiredMembersType
    {
        public required string Name { get; init; }
    }

    private sealed class RequiredMembersTypeWithObsoleteConstructor
    {
        [Obsolete("use the other constructor (#152)")]
        public RequiredMembersTypeWithObsoleteConstructor()
        {
        }

        public required string Name { get; init; }
    }

    private static string Signature(MemberInfo member) =>
        member is MethodInfo method ? $"({string.Join(", ", method.GetParameters().Select(p => p.ParameterType.Name))})" : string.Empty;

    /// <summary>The members lot D marked obsolete: their message names a replacement ("use …") and the version that removes them (#152).</summary>
    private static readonly string[] PathBasedMembers =
    [
        "Bundle.Root",
        "Bundle.IndexFiles",
        "Bundle.LogFiles",
        "Bundle.ParseErrors",
        "Bundle.TryResolveResource(Concept, String, String&, ResourceResolutionStatus&)",
        "Bundle.ReadResourceText(String)",
        "BundleConceptWriter.BundleRoot",
        "OkfBundleTools.BundleRoot",
    ];

    /// <summary>Obsolete before lot D, kept as they were: listed so that the inventory below is the whole public surface of every assembly this project references (types and members).</summary>
    private static readonly string[] EarlierObsoleteMembers =
    [
        "OkfContextProviderOptions.MemoryDirectory",
    ];

    /// <summary>
    /// The synchronous <c>string</c> tool methods of <c>OkfBundleTools</c>, obsolete since the asynchronous
    /// contract (#152) and removed in the release after 0.8.0 (decision Q3): each names its
    /// <c>…Async</c> replacement. The synchronous <c>RunComputation</c>, obsolete since 0.7.0, was removed with it.
    /// </summary>
    private static readonly string[] SynchronousToolMembers =
    [
        "OkfBundleTools.ReadConcept(String)",
        "OkfBundleTools.Browse(String)",
        "OkfBundleTools.Graph(String)",
        "OkfBundleTools.Search(String, String)",
        "OkfBundleTools.Audit(Nullable`1, String, String, String)",
        "OkfBundleTools.WriteConcept(String, String, String)",
        "OkfBundleTools.Verify(String, String, String)",
        "OkfBundleTools.AppendLog(String, String)",
        "OkfBundleTools.RegenerateIndexes()",
        "OkfBundleTools.ValidateBundle()",
        "OkfBundleTools.ChangesSince(String)",
        "OkfBundleTools.GetComputation(String)",
    ];

    [Fact]
    public void Exactly_the_path_based_members_are_obsolete_each_naming_its_replacement()
    {
        var members = ObsoleteMembers().ToList();

        Assert.Equal(
            PathBasedMembers.Concat(EarlierObsoleteMembers).Concat(SynchronousToolMembers).Order(StringComparer.Ordinal),
            members.Select(m => m.Member).Order(StringComparer.Ordinal));

        foreach (var (member, attribute) in members.Where(m => PathBasedMembers.Contains(m.Member, StringComparer.Ordinal)))
        {
            Assert.False(attribute.IsError, member);
            Assert.Contains("use ", attribute.Message, StringComparison.Ordinal);
            Assert.Contains("(#152)", attribute.Message, StringComparison.Ordinal);
        }

        foreach (var (member, attribute) in members.Where(m => SynchronousToolMembers.Contains(m.Member, StringComparer.Ordinal)))
        {
            Assert.False(attribute.IsError, member);
            Assert.Contains("Async", attribute.Message, StringComparison.Ordinal);
            Assert.Contains("removed in the release after 0.8.0", attribute.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Only_the_synthesised_required_members_guard_is_excluded_from_the_inventory()
    {
        var synthesised = Assert.Single(typeof(RequiredMembersType).GetConstructors());
        Assert.NotNull(synthesised.GetCustomAttribute<ObsoleteAttribute>());
        Assert.True(IsRequiredMembersGuard(synthesised));

        var userWritten = Assert.Single(typeof(RequiredMembersTypeWithObsoleteConstructor).GetConstructors());
        Assert.Contains(userWritten.GetCustomAttributes(), a => a.GetType().Name == "CompilerFeatureRequiredAttribute");
        Assert.Equal("use the other constructor (#152)", userWritten.GetCustomAttribute<ObsoleteAttribute>()!.Message);
        Assert.False(IsRequiredMembersGuard(userWritten));
    }

    [Fact]
    public void Diagnostics_and_audit_findings_carry_the_key_the_load_gave_the_object()
    {
        var storage = new InMemoryBundleProvider(id: "k")
            .Add("index.md", "---\nokf_version: 0.2\n---\n# Root\n\n- [a](tables/a.md)\n")
            .Add("tables/index.md", "---\nextra: 1\n---\n# Tables\n")
            .Add("tables/a.md", "---\ntype: Note\ntitle: A\ndescription: d\nresource: missing.sql\n---\nSee [b](b.md).\n")
            .Add("tables/broken.md", "---\ntype: Note\nk: *a\n---\n")
            .Add("bad\tname.md", "---\ntype: Note\n---\n")
            .Add("log.md", "## not-a-date\n\n- x\n");
        var bundle = Bundle.Load(storage);

        var report = BundleValidator.Validate(bundle);

        // The oracle never inverts a display path: a concept's id names its key, an unparseable
        // document's message carries its load error, and the rest is asserted by code below.
        foreach (var diagnostic in report.Diagnostics.Where(d => d.Concept is not null))
        {
            Assert.Equal(bundle.Get(diagnostic.Concept!)!.Key, diagnostic.Key);
        }

        foreach (var diagnostic in report.Diagnostics.Where(d => d.Code == DiagnosticCode.UnparseableDocument))
        {
            var problem = Assert.Single(bundle.ParseProblems, p => diagnostic.Message.EndsWith(p.Error, StringComparison.Ordinal));
            Assert.Equal(problem.Key, diagnostic.Key);
        }

        Assert.Equal(BundleKey.Parse("tables/a.md"), Assert.Single(report.Diagnostics, d => d.Code == DiagnosticCode.FrontmatterPathMissing).Key);
        Assert.Equal(BundleKey.Parse("tables/index.md"), Assert.Single(report.Diagnostics, d => d.Code == DiagnosticCode.IndexHasFrontmatter).Key);
        Assert.Equal(BundleKey.Parse("log.md"), Assert.Single(report.Diagnostics, d => d.Code == DiagnosticCode.LogDateInvalid).Key);
        Assert.Equal(BundleKey.Parse("tables/broken.md"), Assert.Single(report.Diagnostics, d => d.Code == DiagnosticCode.UnparseableDocument && d.Path!.EndsWith("broken.md", StringComparison.Ordinal)).Key);
        Assert.Null(Assert.Single(report.Diagnostics, d => d.Code == DiagnosticCode.UnparseableDocument && d.Path!.EndsWith("bad\tname.md", StringComparison.Ordinal)).Key);
        var broken = Assert.Single(report.Diagnostics, d => d.Code == DiagnosticCode.BrokenLink);
        Assert.Null(broken.Path);
        Assert.Equal(BundleKey.Parse("tables/a.md"), broken.Key);

        var audit = ConceptAudit.Run(bundle, new AuditQuery { Type = "Note" }, new SystemClock());
        var finding = Assert.Single(audit.Findings, f => f.Id.Equals(ConceptId.Parse("tables/a")));
        Assert.Equal(BundleKey.Parse("tables/a.md"), finding.Key);
        Assert.Equal(bundle.Get(finding.Id)!.Path, finding.Path);
    }

    /// <summary>
    /// A display path is not a key: on Windows the refused raw name <c>tables</c>, a backslash, <c>a.md</c>
    /// displays exactly as the real concept <c>tables/a.md</c> does. The refused name's diagnostic keeps
    /// a null key and the concept's findings keep the concept's key, on every platform. Only on Windows do the two
    /// display paths actually coincide (the precondition asserted below), so only there does a display-path
    /// inversion bite and can this test fail; elsewhere it pins the contract without the collision.
    /// </summary>
    [Fact]
    public void A_refused_name_sharing_its_display_path_with_a_concept_has_no_key_and_does_not_take_the_concepts()
    {
        var refusedName = "tables" + (char)92 + "a.md";
        var storage = new InMemoryBundleProvider(id: "collision")
            .Add("tables/a.md", "---\ntype: Note\n---\n")
            .Add(refusedName, "---\ntype: Note\n---\n");
        var bundle = Bundle.Load(storage);

        var concept = Assert.Single(bundle.Concepts);
        var problem = Assert.Single(bundle.ParseProblems);
        Assert.Equal(refusedName, problem.RawName);
        Assert.Null(problem.Key);
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(concept.Path, bundle.DisplayPath(problem));
        }

        var report = BundleValidator.Validate(bundle);

        var refused = Assert.Single(report.Diagnostics, d => d.Code == DiagnosticCode.UnparseableDocument);
        Assert.Null(refused.Key);
        var own = report.Diagnostics.Where(d => d.Concept is not null).ToList();
        Assert.NotEmpty(own);
        Assert.All(own, d => Assert.Equal(BundleKey.Parse("tables/a.md"), d.Key));
    }

    [Fact]
    public void A_not_portable_name_carries_its_key_whether_it_is_a_concept_or_not()
    {
        var doc = "---\ntype: Note\ntitle: T\ndescription: d\n---\n";
        var storage = new InMemoryBundleProvider(BundleKeyConstraintSets.Linux)
            .Add("con.md", doc)
            .Add("aux/index.md", "# Aux\n");
        var bundle = Bundle.Load(storage);

        var warnings = BundleValidator.Validate(bundle).Diagnostics.Where(d => d.Code == DiagnosticCode.KeyNotPortable).ToList();

        Assert.Equal(2, warnings.Count);
        Assert.Equal(BundleKey.Parse("con.md"), Assert.Single(warnings, d => d.Concept is not null).Key);
        Assert.Equal(BundleKey.Parse("aux/index.md"), Assert.Single(warnings, d => d.Concept is null).Key);
    }

    [Fact]
    public void A_hand_built_audit_finding_has_no_key()
    {
        var finding = new AuditFinding(ConceptId.Parse("a"), "a.md", "Note", "A", TrustTier.Unverified, default, false);

        Assert.Null(finding.Key);
    }
}
