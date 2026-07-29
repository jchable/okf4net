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
    public void Accepts_parent_traversal_that_stays_within_the_root()
    {
        using var tmp = new TempDir();
        var root = Path.Combine(tmp.Path, "root");
        var manifestDirectory = Path.Combine(root, "config");
        var productDirectory = Path.Combine(root, "bundles", "products");
        Directory.CreateDirectory(manifestDirectory);
        Directory.CreateDirectory(productDirectory);

        var ok = CatalogPathResolver.TryResolve(
            root, manifestDirectory, Path.Combine("..", "bundles", "products"), out var resolved, out var diagnostic);

        Assert.True(ok);
        Assert.Null(diagnostic);
        Assert.Equal(Path.GetFullPath(productDirectory), resolved);
    }

    [Fact]
    public void Accepts_catalog_root_with_a_trailing_separator()
    {
        using var tmp = new TempDir();
        Directory.CreateDirectory(Path.Combine(tmp.Path, "docs"));
        var rootWithSeparator = tmp.Path + Path.DirectorySeparatorChar;

        var ok = CatalogPathResolver.TryResolve(rootWithSeparator, tmp.Path, "docs", out var resolved, out var diagnostic);

        Assert.True(ok);
        Assert.Null(diagnostic);
        Assert.Equal(Path.GetFullPath(Path.Combine(tmp.Path, "docs")), resolved);
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
    // F1 [Security]: catalog.json source paths are less-trusted input and
    // may legitimately contain ".." (the spec uses "../bundles/product").
    // The containment boundary must therefore be strict and independent of
    // the host volume's case-sensitivity: the configured root's exact path
    // spelling is the authority. A path which leaves that spelling then
    // re-enters through a case variant is rejected on every platform before
    // any filesystem access can follow it. This prevents an escape on
    // case-sensitive APFS while remaining deterministic on macOS CI's usual
    // case-insensitive volume.
    // ----------------------------------------------------------------
    [Fact]
    public void Rejects_case_variant_of_root_as_escape_on_every_platform()
    {
        using var tmp = new TempDir();
        var root = Path.Combine(tmp.Path, "root");
        Directory.CreateDirectory(root);

        var ok = CatalogPathResolver.TryResolve(root, root, Path.Combine("..", "ROOT"), out var resolved, out var diagnostic);

        Assert.False(ok);
        Assert.Null(resolved);
        Assert.Equal(CatalogDiagnosticCode.OutsideRoot, diagnostic!.Code);
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
