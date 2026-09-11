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
            "sources:\n  - { id: s, resource: ./policy.md }\n  - { id: t, resource: policies/margin.md }\n---\nbody\n");
        var bundle = Bundle.Load(tmp.Path);
        var doc = bundle.Concepts.Single(c => c.Id.ToString() == "c/comp").Document;

        var res = doc.FrontmatterResources();
        Assert.Contains(res, r => r.Field == "computation" && r.Kind == FrontmatterResourceKind.ConceptRelative);
        Assert.Contains(res, r => r.Field == "executor.resource" && r.Kind == FrontmatterResourceKind.BundleRelative);
        Assert.Contains(res, r => r.Field == "attester.resource" && r.Kind == FrontmatterResourceKind.Url);
        Assert.Contains(res, r => r.Field == "sources[0].resource" && r.Kind == FrontmatterResourceKind.ConceptRelative);

        // A bare path classifies as BundleRelative: §6.2's relative form is the
        // explicitly dot-prefixed one, and Appendix A resolves the bare form
        // from the bundle root.
        Assert.Contains(res, r => r.Field == "sources[1].resource" && r.Kind == FrontmatterResourceKind.BundleRelative);
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
    /// directory. Before the fix, a raw value classified as concept-relative
    /// (the enum member then named <c>Relative</c>) was combined directly via
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

    /// <summary>
    /// §6.2 defense-in-depth, exercised directly through <see cref="Bundle.TryResolveResource"/>
    /// (the lower-level <see cref="Internal.ReparsePoints"/> helpers already
    /// have their own unit tests in <c>ReparsePointsTests</c>): a
    /// legitimate-looking concept-relative path ("../linked/secret.sql")
    /// that stays lexically WITHIN the bundle root once resolved
    /// (<c>tmp/linked/secret.sql</c>, so the plain string-prefix
    /// <see cref="Internal.ReparsePoints.IsWithin"/> check alone would accept
    /// it) must still be rejected as <see cref="ResourceResolutionStatus.Unsafe"/>
    /// when "linked" is itself a directory reparse point into a location
    /// OUTSIDE the bundle -- <see cref="Bundle.TryResolveResource"/> also
    /// walks every ancestor up to the root via
    /// <see cref="Internal.ReparsePoints.HasReparsePointAncestor(string, string)"/>,
    /// catching the escape the OS would otherwise silently follow the moment
    /// the resolved path is actually read.
    /// </summary>
    [SkippableFact]
    public void TryResolveResource_rejects_a_path_through_a_reparse_point()
    {
        using var tmp = new TempDir();
        using var external = new TempDir();

        tmp.Write("c/comp.md", "---\ntype: Attested Computation\n---\n");
        external.Write("secret.sql", "SELECT 1\n");

        Skip.IfNot(tmp.TryCreateJunctionToExternalDir("linked", external.Path), "no junction/symlink privilege on this machine");

        var bundle = Bundle.Load(tmp.Path);
        var concept = bundle.Concepts.Single(c => c.Id.ToString() == "c/comp");

        Assert.True(bundle.TryResolveResource(concept, "../linked/secret.sql", out var abs, out var status));
        Assert.Equal(ResourceResolutionStatus.Unsafe, status);
        Assert.Null(abs);
    }

    /// <summary>
    /// §6.2 lists three shapes for a path-valued field but never names the base
    /// a bare relative path resolves against. The spec's own worked example
    /// settles it: Appendix A lays out <c>computations/revenue.md</c> alongside
    /// a bundle-root <c>references/</c> directory, and that concept declares
    /// <c>executor.resource: references/skills/run-on-bq.md</c> and
    /// <c>attester.resource: references/attesters/sql-equality.py</c> (§10.2,
    /// echoed by §6.3's <c>references/attesters/revenue.py</c>). Resolving a
    /// bare path against the concept's own directory would point those at
    /// <c>computations/references/...</c>, which Appendix A's layout does not
    /// contain -- so a bare path resolves from the BUNDLE ROOT. The sibling
    /// decoy file pins that: it is what a concept-relative reading would find.
    /// </summary>
    [Fact]
    public void Bare_path_resolves_from_the_bundle_root_per_Appendix_A()
    {
        using var tmp = new TempDir();
        tmp.Write("references/attesters/sql-equality.py", "# the real attester\n");
        tmp.Write("computations/references/attesters/sql-equality.py", "# decoy beside the concept\n");
        tmp.Write("computations/revenue.md",
            "---\ntype: Attested Computation\nruntime: bigquery\n" +
            "attester: { resource: references/attesters/sql-equality.py }\n---\n# Computation\n");
        var bundle = Bundle.Load(tmp.Path);
        var concept = bundle.Concepts.Single(c => c.Id.ToString() == "computations/revenue");

        Assert.True(bundle.TryResolveResource(concept, "references/attesters/sql-equality.py", out var abs, out var status));
        Assert.Equal(ResourceResolutionStatus.Resolved, status);
        Assert.Equal("# the real attester\n", bundle.ReadResourceText(abs!));
    }

    /// <summary>
    /// The drive-relative guard runs on the value AFTER the leading-separator
    /// strip, so <c>"/e:query.sql"</c> is rejected by the same rule as the bare
    /// <c>"e:query.sql"</c> of <c>Drive_relative_raw_path_is_unsafe_on_windows</c>.
    ///
    /// What the guard buys is a DETERMINISTIC verdict, not containment on its
    /// own: without it this spelling reaches
    /// <see cref="System.IO.Path.GetFullPath(string)"/>, which resolves a
    /// drive-relative value against that drive's own current directory. The
    /// containment check then usually rejects it -- verified: this test still
    /// passes with the guard narrowed back to the concept-relative branch --
    /// but "usually" is the problem, since the outcome depends on where the
    /// drive's current directory happens to point rather than on the bundle.
    /// The guard makes the rejection a property of the value. Windows-only:
    /// only there does <c>e:query.sql</c> read as rooted.
    /// </summary>
    [Fact]
    public void Drive_relative_raw_path_behind_a_leading_separator_is_unsafe_on_windows()
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

        Assert.True(bundle.TryResolveResource(concept, $"/{driveLetter}:query.sql", out var abs, out var status));
        Assert.Equal(ResourceResolutionStatus.Unsafe, status);
        Assert.Null(abs);
    }

    /// <summary>
    /// A UNC-looking raw value must not reach the network: the leading-separator
    /// strip turns <c>\\server\share\x</c> into the ordinary bundle-relative
    /// <c>server\share\x</c>, so it resolves (and fails to exist) inside the
    /// bundle rather than naming a remote host.
    ///
    /// This is a pure regression guard, not a test of the new rule: a leading
    /// <c>\</c> already took the bundle-relative branch before the §6.2 change,
    /// so this test is green against the old code too.
    /// </summary>
    [Fact]
    public void Unc_shaped_raw_path_stays_inside_the_bundle()
    {
        using var tmp = new TempDir();
        tmp.Write("c/comp.md", "---\ntype: Attested Computation\n---\n");
        var bundle = Bundle.Load(tmp.Path);
        var concept = bundle.Concepts.Single(c => c.Id.ToString() == "c/comp");

        Assert.True(bundle.TryResolveResource(concept, @"\\server\share\x.sql", out var abs, out var status));
        Assert.Equal(ResourceResolutionStatus.Missing, status);
        Assert.StartsWith(System.IO.Path.GetFullPath(tmp.Path), abs!, System.StringComparison.Ordinal);
    }

    /// <summary>
    /// Only a whole first segment of <c>.</c> or <c>..</c> makes a path
    /// document-relative. A leading dot that is merely part of a directory name
    /// (<c>.hidden/</c>) is an ordinary bare path and so resolves from the
    /// bundle root, like any other.
    /// </summary>
    [Fact]
    public void Leading_dot_in_a_directory_name_is_not_the_document_relative_form()
    {
        using var tmp = new TempDir();
        tmp.Write(".hidden/x.sql", "SELECT 'at the root'\n");
        tmp.Write("c/.hidden/x.sql", "SELECT 'beside the concept'\n");
        tmp.Write("c/comp.md", "---\ntype: Attested Computation\n---\n");
        var bundle = Bundle.Load(tmp.Path);
        var concept = bundle.Concepts.Single(c => c.Id.ToString() == "c/comp");

        Assert.True(bundle.TryResolveResource(concept, ".hidden/x.sql", out var abs, out var status));
        Assert.Equal(ResourceResolutionStatus.Resolved, status);
        Assert.Equal("SELECT 'at the root'\n", bundle.ReadResourceText(abs!));
    }

    /// <summary>
    /// The other half of the §6.2 rule: <c>./</c> and <c>../</c> are the
    /// explicitly document-relative forms (§6.2's own example is
    /// <c>../computations/revenue.md</c>), so they keep resolving against the
    /// concept's directory even though a same-named file sits at the bundle
    /// root. The root decoy is what the bare-path rule would find.
    ///
    /// Like the UNC test below, this is a regression guard rather than a test
    /// of the new rule: the old code resolved <c>./refs/query.sql</c> against
    /// the concept's directory too, so it is green against the old code. What
    /// it pins is that the change did not sweep the dot-prefixed form into the
    /// bundle-root branch along with the bare one.
    /// </summary>
    [Fact]
    public void Dot_prefixed_path_resolves_relative_to_the_concept()
    {
        using var tmp = new TempDir();
        tmp.Write("refs/query.sql", "SELECT 'root decoy'\n");
        tmp.Write("computations/refs/query.sql", "SELECT 'beside the concept'\n");
        tmp.Write("computations/comp.md", "---\ntype: Attested Computation\ncomputation: ./refs/query.sql\n---\n");
        var bundle = Bundle.Load(tmp.Path);
        var concept = bundle.Concepts.Single(c => c.Id.ToString() == "computations/comp");

        Assert.True(bundle.TryResolveResource(concept, "./refs/query.sql", out var abs, out var status));
        Assert.Equal(ResourceResolutionStatus.Resolved, status);
        Assert.Equal("SELECT 'beside the concept'\n", bundle.ReadResourceText(abs!));
    }

    // The three tests below are POSIX-only and therefore do NOT run on a Windows
    // developer machine -- CI's ubuntu-latest and macos-latest legs are what
    // exercises them. They exist because §6.2's shapes read differently on the
    // two platforms: a leading "/" is an absolute filesystem path on POSIX but
    // not on Windows, and a colon is an ordinary filename character on POSIX but
    // a drive separator on Windows. The Windows-side counterparts are
    // Drive_relative_raw_path_is_unsafe_on_windows and
    // Drive_relative_raw_path_behind_a_leading_separator_is_unsafe_on_windows.
    //
    // They are SkippableFact, not Fact with an early return. The difference is
    // what the run summary says: an early return counts as PASSED, so on Windows
    // these would report success while verifying nothing -- the exact shape that
    // left three symlink tests in this repo green while they guarded nothing.
    // Skip.If makes the absence visible ("3 skipped"), which is the honest
    // answer, and lets a reader tell "covered" from "not executed here".

    /// <summary>
    /// The §6.2 hazard that only exists on POSIX: a leading <c>/</c> means the
    /// BUNDLE root, never the filesystem root. <c>/etc/passwd</c> must name a
    /// (missing) file inside the bundle, not the real one -- the leading
    /// separator is stripped before the value is ever combined with a base, so
    /// <see cref="System.IO.Path.Combine(string, string)"/> is never handed
    /// something it would treat as rooted.
    /// </summary>
    [SkippableFact]
    public void Posix_absolute_looking_path_names_a_file_inside_the_bundle()
    {
        Skip.If(OperatingSystem.IsWindows(), "POSIX-only: \"/etc/passwd\" is not an absolute path on Windows.");

        using var tmp = new TempDir();
        tmp.Write("c/comp.md", "---\ntype: Attested Computation\n---\n");
        var bundle = Bundle.Load(tmp.Path);
        var concept = bundle.Concepts.Single(c => c.Id.ToString() == "c/comp");

        Assert.True(bundle.TryResolveResource(concept, "/etc/passwd", out var abs, out var status));
        Assert.Equal(ResourceResolutionStatus.Missing, status);
        Assert.StartsWith(System.IO.Path.GetFullPath(tmp.Path), abs!, System.StringComparison.Ordinal);
    }

    /// <summary>
    /// The same hazard where the target actually exists outside the bundle: the
    /// resolver must still stay inside, so the outside file is never read. This
    /// is the case that would matter — a real <c>/etc/passwd</c>-shaped read —
    /// and it is pinned against a file this test creates rather than a system
    /// one, so it asserts containment without depending on the host's contents.
    /// </summary>
    [SkippableFact]
    public void Posix_absolute_path_to_an_existing_outside_file_is_not_read()
    {
        Skip.If(OperatingSystem.IsWindows(), "POSIX-only, as above.");

        using var tmp = new TempDir();
        using var external = new TempDir();
        external.Write("secret.sql", "SELECT 'outside the bundle'\n");
        tmp.Write("c/comp.md", "---\ntype: Attested Computation\n---\n");
        var bundle = Bundle.Load(tmp.Path);
        var concept = bundle.Concepts.Single(c => c.Id.ToString() == "c/comp");

        var outsideAbsolute = System.IO.Path.Combine(System.IO.Path.GetFullPath(external.Path), "secret.sql");
        Assert.True(bundle.TryResolveResource(concept, outsideAbsolute, out var abs, out var status));
        Assert.NotEqual(ResourceResolutionStatus.Resolved, status);
        if (abs is not null)
        {
            Assert.StartsWith(System.IO.Path.GetFullPath(tmp.Path), abs, System.StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The drive-relative guard must not misfire on POSIX, where a colon is an
    /// ordinary filename character: <c>c:query.sql</c> is a legitimate bare
    /// relative path there, not a drive-relative form, and
    /// <see cref="System.IO.Path.IsPathRooted(string)"/> agrees. It must resolve
    /// from the bundle root like any other bare path, not be rejected as
    /// <see cref="ResourceResolutionStatus.Unsafe"/>.
    /// </summary>
    [SkippableFact]
    public void Colon_in_a_filename_is_an_ordinary_bare_path_on_posix()
    {
        Skip.If(OperatingSystem.IsWindows(), "POSIX-only: on Windows this same string IS drive-relative.");

        using var tmp = new TempDir();
        tmp.Write("c:query.sql", "SELECT 1\n");
        tmp.Write("c/comp.md", "---\ntype: Attested Computation\n---\n");
        var bundle = Bundle.Load(tmp.Path);
        var concept = bundle.Concepts.Single(c => c.Id.ToString() == "c/comp");

        Assert.True(bundle.TryResolveResource(concept, "c:query.sql", out var abs, out var status));
        Assert.Equal(ResourceResolutionStatus.Resolved, status);
        Assert.Equal("SELECT 1\n", bundle.ReadResourceText(abs!));
    }
}
