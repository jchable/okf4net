// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net.Catalog;

namespace OKF4net.Tests.Catalog;

/// <summary>
/// One red test per <see cref="CatalogPathResolver.TryResolve"/> reject rule, plus the
/// accept cases (plain nested path, and a symlinked catalog root itself). Every rejection
/// case asserts a specific <see cref="CatalogDiagnosticCode"/>, not just "returns false".
/// </summary>
public class CatalogPathSafetyTests
{
    [Fact]
    public void Accepts_normal_nested_path()
    {
        using var tmp = new TempDir();
        Directory.CreateDirectory(Path.Combine(tmp.Path, "docs"));

        var ok = CatalogPathResolver.TryResolve(tmp.Path, tmp.Path, "docs", out var resolved, out var diagnostic);

        Assert.True(ok);
        Assert.Null(diagnostic);
        Assert.Equal(Path.GetFullPath(Path.Combine(tmp.Path, "docs")), resolved);
    }

    [Fact]
    public void Accepts_deeper_nested_relative_path()
    {
        using var tmp = new TempDir();
        Directory.CreateDirectory(Path.Combine(tmp.Path, "a", "b", "c"));

        var ok = CatalogPathResolver.TryResolve(tmp.Path, tmp.Path, "./a/b/c", out var resolved, out var diagnostic);

        Assert.True(ok);
        Assert.Null(diagnostic);
        Assert.Equal(Path.GetFullPath(Path.Combine(tmp.Path, "a", "b", "c")), resolved);
    }

    [Fact]
    public void Rejects_absolute_source_path()
    {
        using var tmp = new TempDir();
        var absolute = Path.Combine(tmp.Path, "docs");
        Directory.CreateDirectory(absolute);

        var ok = CatalogPathResolver.TryResolve(tmp.Path, tmp.Path, absolute, out var resolved, out var diagnostic);

        Assert.False(ok);
        Assert.Null(resolved);
        Assert.Equal(CatalogDiagnosticCode.AbsolutePath, diagnostic!.Code);
    }

    [Fact]
    public void Rejects_parent_traversal_escaping_the_root()
    {
        using var tmp = new TempDir();
        var root = Path.Combine(tmp.Path, "root");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.Combine(tmp.Path, "outside"));

        var ok = CatalogPathResolver.TryResolve(root, root, "../../outside", out var resolved, out var diagnostic);

        Assert.False(ok);
        Assert.Null(resolved);
        Assert.Equal(CatalogDiagnosticCode.OutsideRoot, diagnostic!.Code);
    }

    [Fact]
    public void Rejects_target_that_is_not_an_existing_directory()
    {
        using var tmp = new TempDir();

        var ok = CatalogPathResolver.TryResolve(tmp.Path, tmp.Path, "does-not-exist", out var resolved, out var diagnostic);

        Assert.False(ok);
        Assert.Null(resolved);
        Assert.Equal(CatalogDiagnosticCode.TargetNotFound, diagnostic!.Code);
    }

    [Fact]
    public void Rejects_target_that_is_a_file_not_a_directory()
    {
        using var tmp = new TempDir();
        tmp.Write("docs.md", "not a directory");

        var ok = CatalogPathResolver.TryResolve(tmp.Path, tmp.Path, "docs.md", out var resolved, out var diagnostic);

        Assert.False(ok);
        Assert.Null(resolved);
        Assert.Equal(CatalogDiagnosticCode.TargetNotFound, diagnostic!.Code);
    }

    [Fact]
    public void Rejects_empty_source_path()
    {
        using var tmp = new TempDir();

        var ok = CatalogPathResolver.TryResolve(tmp.Path, tmp.Path, "", out var resolved, out var diagnostic);

        Assert.False(ok);
        Assert.Null(resolved);
        Assert.Equal(CatalogDiagnosticCode.EmptyPath, diagnostic!.Code);
    }

    [Fact]
    public void Rejects_source_path_with_embedded_nul_as_invalid()
    {
        using var tmp = new TempDir();

        var ok = CatalogPathResolver.TryResolve(tmp.Path, tmp.Path, "docs\0evil", out var resolved, out var diagnostic);

        Assert.False(ok);
        Assert.Null(resolved);
        Assert.Equal(CatalogDiagnosticCode.InvalidPath, diagnostic!.Code);
    }

    // ----------------------------------------------------------------
    // Reparse-point ancestor: the resolved target itself is a genuine
    // directory, but a directory strictly between it and the catalog root
    // is a junction/symlink to somewhere else entirely. Lexical containment
    // alone (Path.GetFullPath + prefix check) would accept this -- the OS
    // is what actually follows the junction the moment anything touches
    // disk -- so this must be caught by the reparse-point walk, not the
    // containment check. Requires junction/symlink-creation privilege;
    // skips cleanly if unavailable, per the repo's other reparse-point
    // tests (see IndexTests.cs, OkfBundleToolsTests.cs).
    // ----------------------------------------------------------------
    [Fact]
    public void Rejects_reparse_point_ancestor_within_root()
    {
        using var tmp = new TempDir();
        using var external = new TempDir();
        Directory.CreateDirectory(Path.Combine(external.Path, "inner"));

        if (!tmp.TryCreateJunctionToExternalDir("link", external.Path))
        {
            return; // no junction/symlink privilege on this machine -- skip.
        }

        var ok = CatalogPathResolver.TryResolve(
            tmp.Path, tmp.Path, Path.Combine("link", "inner"), out var resolved, out var diagnostic);

        Assert.False(ok);
        Assert.Null(resolved);
        Assert.Equal(CatalogDiagnosticCode.ReparsePointInPath, diagnostic!.Code);
    }

    // ----------------------------------------------------------------
    // Reparse-point target: the resolved directory itself (not merely an
    // ancestor of it) is the junction/symlink. Requires junction/symlink-
    // creation privilege; skips cleanly if unavailable.
    // ----------------------------------------------------------------
    [Fact]
    public void Rejects_reparse_point_target_itself()
    {
        using var tmp = new TempDir();
        using var external = new TempDir();

        if (!tmp.TryCreateJunctionToExternalDir("link", external.Path))
        {
            return; // no junction/symlink privilege on this machine -- skip.
        }

        var ok = CatalogPathResolver.TryResolve(tmp.Path, tmp.Path, "link", out var resolved, out var diagnostic);

        Assert.False(ok);
        Assert.Null(resolved);
        Assert.Equal(CatalogDiagnosticCode.ReparsePointInPath, diagnostic!.Code);
    }

    // ----------------------------------------------------------------
    // F1 [Security]: case-insensitive containment must not escape the
    // catalog root on a case-sensitive filesystem. catalog.json source paths
    // are LESS-TRUSTED input and ".." is legitimately allowed (spec example
    // uses "../bundles/product"), so containment is the primary defense. On
    // a case-sensitive filesystem (Linux, the CI/container target), "root"
    // and "ROOT" are two distinct, real directories -- an
    // OrdinalIgnoreCase-only containment check would wrongly treat a source
    // path resolving through the case-variant as "in root" even though it
    // points at a completely different directory the operator never
    // intended to expose. CatalogPathResolver must compare with an
    // OS-appropriate StringComparison (Ordinal on case-sensitive
    // filesystems), so this test asserts per-platform: on a case-insensitive
    // filesystem the case-variant genuinely IS the same directory (accepting
    // it is correct there); on a case-sensitive one, it is a real escape and
    // must be rejected as OutsideRoot. See ReparsePointsTests for the
    // platform-independent pin of the underlying comparison behavior.
    // ----------------------------------------------------------------
    [Fact]
    public void Rejects_case_variant_of_root_as_escape_on_case_sensitive_filesystems()
    {
        using var tmp = new TempDir();
        var root = Path.Combine(tmp.Path, "root");
        Directory.CreateDirectory(root);

        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
        {
            // Case-insensitive filesystem: "ROOT" and "root" are the SAME
            // physical directory, so resolving through the case-variant
            // spelling is legitimately in-root -- matches
            // CatalogPathResolver's own platform-aware comparison choice.
            var ok = CatalogPathResolver.TryResolve(root, root, Path.Combine("..", "ROOT"), out _, out var diagnostic);

            Assert.True(ok);
            Assert.Null(diagnostic);
        }
        else
        {
            // Case-sensitive filesystem (Linux): "ROOT" is a genuinely
            // different, real directory from "root". Creating it here
            // simulates an attacker-controlled catalog.json path riding a
            // case-variant of the root to reach a directory the operator
            // never intended to expose -- must be rejected, not silently
            // treated as in-root (the F1 regression this test guards
            // against).
            var escapeDir = Path.Combine(tmp.Path, "ROOT");
            Directory.CreateDirectory(Path.Combine(escapeDir, "x"));

            var ok = CatalogPathResolver.TryResolve(root, root, Path.Combine("..", "ROOT", "x"), out var resolved, out var diagnostic);

            Assert.False(ok);
            Assert.Null(resolved);
            Assert.Equal(CatalogDiagnosticCode.OutsideRoot, diagnostic!.Code);
        }
    }

    // ----------------------------------------------------------------
    // Symlinked catalog root itself: exclusive-of-root regression guard,
    // mirroring IndexTests.Symlinked_bundle_root_still_gets_its_index_written.
    // The catalog root being reached through a junction/symlink is a
    // legitimate, explicit operator choice (symlinked project directories,
    // container/WSL bind mounts) -- it must not block resolution of its own
    // legitimate children. The reparse-point walk must never inspect the
    // root itself, only directories strictly between the resolved target
    // and the root. Requires junction/symlink-creation privilege; skips
    // cleanly if unavailable.
    // ----------------------------------------------------------------
    [Fact]
    public void Accepts_symlinked_catalog_root_itself()
    {
        using var content = new TempDir();
        Directory.CreateDirectory(Path.Combine(content.Path, "docs"));

        using var parent = new TempDir();
        if (!parent.TryCreateJunctionToExternalDir("root-link", content.Path))
        {
            return; // no junction/symlink privilege on this machine -- skip.
        }

        var catalogRoot = Path.Combine(parent.Path, "root-link");

        var ok = CatalogPathResolver.TryResolve(catalogRoot, catalogRoot, "docs", out var resolved, out var diagnostic);

        Assert.True(ok);
        Assert.Null(diagnostic);
        Assert.Equal(Path.GetFullPath(Path.Combine(catalogRoot, "docs")), resolved);
    }
}
