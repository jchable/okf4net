// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Linq;
using OKF4net;
using Xunit;

namespace OKF4net.Tests;

public class FrontmatterResourceTests
{
    [Fact]
    public void Enumerates_and_classifies_the_five_path_valued_fields()
    {
        using var tmp = new TempDir();
        tmp.Write("c/comp.md",
            "---\ntype: Attested Computation\ncomputation: ../refs/revenue.sql\n" +
            "executor: { resource: /skills/run.md, receipt: [job_id] }\n" +
            "attester: { resource: https://ex/att.py }\n" +
            "sources:\n  - { id: s, resource: ./policy.md }\n---\nbody\n");
        var bundle = Bundle.Load(tmp.Path);
        var doc = bundle.Concepts.Single(c => c.Id.ToString() == "c/comp").Document;

        var res = doc.FrontmatterResources();
        Assert.Contains(res, r => r.Field == "computation" && r.Kind == FrontmatterResourceKind.Relative);
        Assert.Contains(res, r => r.Field == "executor.resource" && r.Kind == FrontmatterResourceKind.BundleRelative);
        Assert.Contains(res, r => r.Field == "attester.resource" && r.Kind == FrontmatterResourceKind.Url);
        Assert.Contains(res, r => r.Field == "sources[0].resource" && r.Kind == FrontmatterResourceKind.Relative);
    }

    [Fact]
    public void Resolves_missing_relative_path_as_Missing()
    {
        using var tmp = new TempDir();
        tmp.Write("c/comp.md", "---\ntype: Attested Computation\ncomputation: ./nope.sql\n---\n");
        var bundle = Bundle.Load(tmp.Path);
        var concept = bundle.Concepts.Single();
        Assert.True(bundle.TryResolveResource(concept, "./nope.sql", out var abs, out var status));
        Assert.Equal(ResourceResolutionStatus.Missing, status);

        tmp.Write("c/revenue.sql", "SELECT 1\n");
        var bundle2 = Bundle.Load(tmp.Path);
        var c2 = bundle2.Concepts.Single(c => c.Id.ToString() == "c/comp");
        Assert.True(bundle2.TryResolveResource(c2, "./revenue.sql", out var abs2, out var st2));
        Assert.Equal(ResourceResolutionStatus.Resolved, st2);
        Assert.Equal("SELECT 1\n", bundle2.ReadResourceText(abs2!));
    }

    [Fact]
    public void Url_is_not_resolved()
    {
        using var tmp = new TempDir();
        tmp.Write("c/comp.md", "---\ntype: Attested Computation\n---\n");
        var bundle = Bundle.Load(tmp.Path);
        Assert.True(bundle.TryResolveResource(bundle.Concepts.Single(), "https://x/y", out var abs, out var status));
        Assert.Equal(ResourceResolutionStatus.Url, status);
        Assert.Null(abs);
    }

    [Fact]
    public void Bundle_relative_resolves_from_root_and_escaping_path_is_unsafe()
    {
        using var tmp = new TempDir();
        tmp.Write("skills/run.md", "run\n");
        tmp.Write("c/comp.md", "---\ntype: Attested Computation\n---\n");
        var bundle = Bundle.Load(tmp.Path);
        var concept = bundle.Concepts.Single(c => c.Id.ToString() == "c/comp");

        // "/skills/run.md" resolves from the BUNDLE ROOT (not the concept dir).
        Assert.True(bundle.TryResolveResource(concept, "/skills/run.md", out var abs, out var status));
        Assert.Equal(ResourceResolutionStatus.Resolved, status);
        Assert.Equal("run\n", bundle.ReadResourceText(abs!));

        // A relative path that climbs above the bundle root is Unsafe.
        Assert.True(bundle.TryResolveResource(concept, "../../escape.txt", out _, out var escaped));
        Assert.Equal(ResourceResolutionStatus.Unsafe, escaped);
    }
}
