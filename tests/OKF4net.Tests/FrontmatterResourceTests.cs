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

    [Fact]
    public void Embedded_NUL_in_a_raw_path_resolves_as_Unsafe_instead_of_throwing()
    {
        using var tmp = new TempDir();
        // The YAML `\0` escape inside a double-quoted scalar yields a literal
        // NUL character in the parsed string (YamlParser.cs), which
        // Path.GetFullPath/Path.Combine reject with an ArgumentException.
        tmp.Write("c/comp.md", "---\ntype: Attested Computation\ncomputation: \"a\\0b\"\n---\n");
        var bundle = Bundle.Load(tmp.Path);
        var concept = bundle.Concepts.Single(c => c.Id.ToString() == "c/comp");

        var rawPath = concept.Document.FrontmatterResources().Single(r => r.Field == "computation").RawPath;
        Assert.Contains('\0', rawPath);

        Assert.True(bundle.TryResolveResource(concept, rawPath, out var abs, out var status));
        Assert.Equal(ResourceResolutionStatus.Unsafe, status);
        Assert.Null(abs);
    }

    /// <summary>
    /// P1 regression: on a case-sensitive filesystem (Linux), a bundle rooted
    /// at ".../Bundle" must not treat the sibling directory ".../bundle"
    /// (differing only in case) as contained within it. Before the fix,
    /// <see cref="Bundle.TryResolveResource"/> delegated the §6.2 containment
    /// check to <see cref="Internal.ReparsePoints.IsWithinBundleRoot"/>, which
    /// hardcodes <see cref="System.StringComparison.OrdinalIgnoreCase"/> -- on
    /// Linux this wrongly accepted "../../bundle/secret.sql" (climbing from a
    /// nested concept back up and into the sibling "bundle" dir) as contained
    /// within the "Bundle" root, reading a file entirely outside the intended
    /// bundle. The fix uses <see cref="System.StringComparison.Ordinal"/>
    /// UNCONDITIONALLY (case-sensitivity is a per-volume runtime property, not an
    /// OS one -- so an OS-based heuristic would leave the same hole on a
    /// case-sensitive macOS/Windows volume). This end-to-end test is gated to
    /// Linux only because a case-insensitive dev filesystem cannot hold both
    /// "Bundle" and "bundle" as distinct sibling directories; the OS-independent
    /// containment guarantee itself is locked portably by the
    /// <c>ReparsePoints.IsWithin</c> (Ordinal) unit test in <c>ReparsePointsTests</c>.
    /// </summary>
    [Fact]
    public void Case_variant_sibling_directory_is_unsafe_on_case_sensitive_filesystem()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var tmp = new TempDir();
        tmp.Write("Bundle/nested/concept.md", "---\ntype: Attested Computation\n---\n");
        tmp.Write("bundle/secret.sql", "SELECT secret;\n");

        var root = System.IO.Path.Combine(tmp.Path, "Bundle");
        var bundle = Bundle.Load(root);
        var concept = bundle.Concepts.Single(c => c.Id.ToString() == "nested/concept");

        Assert.True(bundle.TryResolveResource(concept, "../../bundle/secret.sql", out var abs, out var status));
        Assert.Equal(ResourceResolutionStatus.Unsafe, status);
        Assert.Null(abs);
    }

    /// <summary>
    /// P2a regression: on Windows, "E:query.sql" is a DRIVE-RELATIVE path
    /// (not drive-absolute) -- <see cref="System.IO.Path.GetFullPath(string)"/>
    /// resolves it against drive E:'s own current directory, not the concept's
    /// directory. Before the fix, a raw value classified as
    /// <see cref="FrontmatterResourceKind.Relative"/> was combined directly via
    /// <c>Path.Combine(conceptDir, rawPath)</c>/<c>Path.GetFullPath</c>, which
    /// discards <c>conceptDir</c> entirely for a rooted second argument -- so
    /// this could resolve to a path unrelated to (and potentially inside) the
    /// bundle root depending on drive E:'s current directory, and be wrongly
    /// accepted instead of rejected. Windows-only: <see cref="System.IO.Path.IsPathRooted(string)"/>
    /// only recognizes this drive-relative shape there.
    /// </summary>
    [Fact]
    public void Drive_relative_raw_path_is_unsafe_on_windows()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var tmp = new TempDir();
        tmp.Write("c/comp.md", "---\ntype: Attested Computation\n---\n");
        var bundle = Bundle.Load(tmp.Path);
        var concept = bundle.Concepts.Single(c => c.Id.ToString() == "c/comp");

        var driveLetter = System.IO.Path.GetPathRoot(tmp.Path)![0];
        var rawPath = $"{driveLetter}:query.sql";

        Assert.True(bundle.TryResolveResource(concept, rawPath, out var abs, out var status));
        Assert.Equal(ResourceResolutionStatus.Unsafe, status);
        Assert.Null(abs);
    }

    /// <summary>
    /// Guards against the P2a fix over-rejecting: a genuine relative path must
    /// still resolve normally on every OS -- <see cref="System.IO.Path.IsPathRooted(string)"/>
    /// is false for "./x.sql" everywhere, so it never reaches the new
    /// drive-relative guard.
    /// </summary>
    [Fact]
    public void Genuine_relative_path_still_resolves_after_drive_relative_guard()
    {
        using var tmp = new TempDir();
        tmp.Write("c/comp.md", "---\ntype: Attested Computation\n---\n");
        tmp.Write("c/x.sql", "SELECT 1\n");
        var bundle = Bundle.Load(tmp.Path);
        var concept = bundle.Concepts.Single(c => c.Id.ToString() == "c/comp");

        Assert.True(bundle.TryResolveResource(concept, "./x.sql", out var abs, out var status));
        Assert.Equal(ResourceResolutionStatus.Resolved, status);
        Assert.Equal("SELECT 1\n", bundle.ReadResourceText(abs!));
    }
}
