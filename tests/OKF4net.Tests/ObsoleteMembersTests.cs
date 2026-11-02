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
    private static readonly Assembly[] Assemblies = [typeof(Bundle).Assembly, typeof(OkfBundleTools).Assembly];

    private static IEnumerable<(string Member, ObsoleteAttribute Attribute)> ObsoleteMembers() =>
        Assemblies
            .SelectMany(a => a.GetExportedTypes())
            .SelectMany(t => t.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Select(m => (Member: $"{t.Name}.{m.Name}{Signature(m)}", Attribute: m.GetCustomAttribute<ObsoleteAttribute>()!)))
            .Where(x => x.Attribute is not null);

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

    /// <summary>Obsolete before lot D, kept as they were: listed so that the inventory below is the whole public surface.</summary>
    private static readonly string[] EarlierObsoleteMembers =
    [
        "OkfBundleTools.RunComputation(String, IReadOnlyDictionary`2)",
        "OkfContextProviderOptions.MemoryDirectory",
    ];

    [Fact]
    public void Exactly_the_path_based_members_are_obsolete_each_naming_its_replacement()
    {
        var members = ObsoleteMembers().ToList();

        Assert.Equal(
            PathBasedMembers.Concat(EarlierObsoleteMembers).Order(StringComparer.Ordinal),
            members.Select(m => m.Member).Order(StringComparer.Ordinal));

        foreach (var (member, attribute) in members.Where(m => PathBasedMembers.Contains(m.Member, StringComparer.Ordinal)))
        {
            Assert.False(attribute.IsError, member);
            Assert.Contains("use ", attribute.Message, StringComparison.Ordinal);
            Assert.Contains("(#152)", attribute.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Diagnostics_and_audit_findings_carry_the_key_their_display_path_projects()
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

        foreach (var diagnostic in report.Diagnostics)
        {
            var expected = diagnostic.Path is { } path
                ? KeyOf(bundle, path)
                : diagnostic.Concept is { } id ? bundle.Get(id)?.Key : null;
            Assert.Equal(expected, diagnostic.Key);
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
        var finding = Assert.Single(audit.Findings);
        Assert.Equal(BundleKey.Parse("tables/a.md"), finding.Key);
        Assert.Equal(bundle.Get(finding.Id)!.Path, finding.Path);
    }

    private static BundleKey? KeyOf(Bundle bundle, string displayPath)
    {
        foreach (var concept in bundle.Concepts)
        {
            if (concept.Path == displayPath)
            {
                return concept.Key;
            }
        }

        foreach (var key in bundle.IndexKeys.Concat(bundle.LogKeys))
        {
            if (BundleDisplayPath.Compose(bundle.Location, key) == displayPath)
            {
                return key;
            }
        }

        foreach (var problem in bundle.ParseProblems)
        {
            if (bundle.DisplayPath(problem) == displayPath)
            {
                return problem.Key;
            }
        }

        return null;
    }
}
