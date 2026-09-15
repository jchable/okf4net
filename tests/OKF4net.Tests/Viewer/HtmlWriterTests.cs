// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net.Viewer;

namespace OKF4net.Tests.Viewer;

/// <summary>Tests for writing a <see cref="ViewerSite"/> out as a static site.</summary>
public class HtmlWriterTests
{
    private static Bundle SampleBundle(TempDir tmp)
    {
        tmp.Write("index.md", "---\ntype: index\ntitle: Root\ndescription: Root\n---\n");
        tmp.Write("tables/users.md",
            "---\ntype: table\ntitle: Users\ndescription: The users table\n---\nSome **body**.\n");
        return Bundle.Load(tmp.Path);
    }

    [Fact]
    public void Write_creates_the_index_and_one_page_per_concept()
    {
        using var src = new TempDir();
        using var dest = new TempDir();
        var site = SiteModel.Build(SampleBundle(src));

        var written = HtmlWriter.Write(site, dest.Path);

        Assert.True(File.Exists(Path.Combine(dest.Path, "index.html")));
        Assert.True(File.Exists(Path.Combine(dest.Path, "tables", "users.html")));
        Assert.Contains("index.html", written);
    }

    [Fact]
    public void Write_emits_the_shared_assets_once()
    {
        using var src = new TempDir();
        using var dest = new TempDir();
        var site = SiteModel.Build(SampleBundle(src));

        HtmlWriter.Write(site, dest.Path);

        Assert.True(File.Exists(Path.Combine(dest.Path, "assets", "viewer.css")));
        Assert.True(File.Exists(Path.Combine(dest.Path, "assets", "viewer.js")));
        Assert.True(File.Exists(Path.Combine(dest.Path, "assets", "marked.min.js")));
    }

    [Fact]
    public void Write_links_assets_with_a_path_relative_to_the_page_depth()
    {
        using var src = new TempDir();
        using var dest = new TempDir();
        var site = SiteModel.Build(SampleBundle(src));

        HtmlWriter.Write(site, dest.Path);

        var nested = File.ReadAllText(Path.Combine(dest.Path, "tables", "users.html"));
        Assert.Contains("../assets/viewer.css", nested);

        var root = File.ReadAllText(Path.Combine(dest.Path, "index.html"));
        Assert.Contains("assets/viewer.css", root);
        Assert.DoesNotContain("../assets/viewer.css", root);
    }

    [Fact]
    public void Write_embeds_the_body_as_json_rather_than_pre_rendered_html()
    {
        using var src = new TempDir();
        using var dest = new TempDir();
        var site = SiteModel.Build(SampleBundle(src));

        HtmlWriter.Write(site, dest.Path);

        var page = File.ReadAllText(Path.Combine(dest.Path, "tables", "users.html"));
        Assert.Contains("id=\"okf-payload\"", page);
        Assert.Contains("Some **body**.", page);
    }

    [Fact]
    public void Write_renders_the_frontmatter_table()
    {
        using var src = new TempDir();
        using var dest = new TempDir();
        var site = SiteModel.Build(SampleBundle(src));

        HtmlWriter.Write(site, dest.Path);

        var page = File.ReadAllText(Path.Combine(dest.Path, "tables", "users.html"));
        Assert.Contains("The users table", page);
    }

    [Fact]
    public void Write_creates_the_output_directory_when_missing()
    {
        using var src = new TempDir();
        using var dest = new TempDir();
        var site = SiteModel.Build(SampleBundle(src));
        var target = Path.Combine(dest.Path, "does", "not", "exist");

        HtmlWriter.Write(site, target);

        Assert.True(File.Exists(Path.Combine(target, "index.html")));
    }

    [Fact]
    public void Write_leaves_unrelated_files_in_the_output_directory_alone()
    {
        using var src = new TempDir();
        using var dest = new TempDir();
        var keep = Path.Combine(dest.Path, "keep-me.txt");
        File.WriteAllText(keep, "untouched");
        var site = SiteModel.Build(SampleBundle(src));

        HtmlWriter.Write(site, dest.Path);

        Assert.Equal("untouched", File.ReadAllText(keep));
    }

    [Fact]
    public void Write_refuses_to_write_inside_the_bundle_it_renders()
    {
        using var src = new TempDir();
        var site = SiteModel.Build(SampleBundle(src));

        var ex = Assert.Throws<ArgumentException>(
            () => HtmlWriter.Write(site, Path.Combine(src.Path, "site")));
        Assert.Contains("bundle", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Write_refuses_a_hand_constructed_page_whose_path_escapes_the_output_directory()
    {
        // ViewerPage is a public record with a public constructor: nothing
        // stops a third-party host from constructing one directly with a
        // traversal path, bypassing the ConceptId validation that protects
        // the CLI's own call path. WriteFile must catch this on its own.
        using var src = new TempDir();
        using var dest = new TempDir();
        var goodSite = SiteModel.Build(SampleBundle(src));

        var evilPage = new ViewerPage(
            goodSite.Pages[0].Id,
            "Evil",
            "../../../evil.html",
            [],
            "body",
            [],
            []);
        var site = new ViewerSite(src.Path, [evilPage], string.Empty, []);

        var ex = Assert.Throws<ArgumentException>(() => HtmlWriter.Write(site, dest.Path));
        Assert.Contains("outside", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Write_refuses_a_page_escaping_into_a_sibling_differing_only_by_case()
    {
        // On a case-SENSITIVE volume, `<out>/../viewerout/x.html` and an
        // output directory named `ViewerOut` are two genuinely different
        // directories -- so this page escapes. Comparing the containment
        // check OrdinalIgnoreCase would call the escaping path a text prefix
        // of the root and let the write through, which is why this guard
        // must compare Ordinal: it decides whether to ALLOW a write, so its
        // safe failure mode is refusing a legitimate case-variant, never
        // admitting a genuinely different directory. (The opposite-polarity
        // guard on `--out` itself deliberately keeps OrdinalIgnoreCase --
        // see GuardOutputDirectory's remarks.)
        //
        // The decision under test is pure string arithmetic, so this pins
        // the guard on every platform, including a case-insensitive one
        // where the escape itself would not be observable.
        using var src = new TempDir();
        using var dest = new TempDir();
        var outDir = Path.Combine(dest.Path, "ViewerOut");
        var goodSite = SiteModel.Build(SampleBundle(src));

        var escapingPage = new ViewerPage(
            goodSite.Pages[0].Id,
            "Escaping",
            "../viewerout/pwned.html",
            [],
            "body",
            [],
            []);
        var site = new ViewerSite(src.Path, [escapingPage], string.Empty, []);

        var ex = Assert.Throws<ArgumentException>(() => HtmlWriter.Write(site, outDir));
        Assert.Contains("outside", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Write_refuses_two_concept_ids_that_collide_on_a_case_insensitive_volume()
    {
        // "users" and "Users" are two distinct concepts on a case-sensitive
        // bundle volume, but they would render to ONE file on a
        // case-insensitive output volume (NTFS, default APFS, exFAT, SMB):
        // the second write would silently replace the first and the index
        // would link both entries to the survivor. Refused up front, on
        // every volume -- a site that renders differently per filesystem is
        // not a site -- and before any side effect, so a refused site
        // creates nothing on disk (asserted below via a fresh, never-created
        // outDir).
        using var src = new TempDir();
        using var dest = new TempDir();
        var outDir = Path.Combine(dest.Path, "never-created");

        var lower = new ViewerPage(
            ConceptId.Parse("users"), "Users", "users.html", [], "body", [], []);
        var upper = new ViewerPage(
            ConceptId.Parse("Users"), "Users", "Users.html", [], "body", [], []);
        var site = new ViewerSite(src.Path, [lower, upper], string.Empty, []);

        var ex = Assert.Throws<ArgumentException>(() => HtmlWriter.Write(site, outDir));

        Assert.Contains("users", ex.Message);
        Assert.Contains("Users", ex.Message);
        Assert.Contains("case-insensitive", ex.Message);
        Assert.False(Directory.Exists(outDir));
    }

    [Fact]
    public void Write_refuses_a_concept_page_that_collides_with_the_generated_index()
    {
        // Bundle's own reserved-filename check (`case IndexFilename:`) is an
        // ordinal switch, so on a case-sensitive bundle volume a root-level
        // `Index.md`/`INDEX.md` loads as an ordinary concept named "Index"
        // rather than being treated as the bundle's own index -- and its
        // generated page, "Index.html", collides with this method's own
        // "index.html" on a case-insensitive output volume exactly like two
        // concept pages would.
        using var src = new TempDir();
        using var dest = new TempDir();
        var outDir = Path.Combine(dest.Path, "never-created");

        var indexLookalike = new ViewerPage(
            ConceptId.Parse("Index"), "Index", "Index.html", [], "body", [], []);
        var site = new ViewerSite(src.Path, [indexLookalike], string.Empty, []);

        var ex = Assert.Throws<ArgumentException>(() => HtmlWriter.Write(site, outDir));

        Assert.Contains("Index", ex.Message);
        Assert.Contains("index page", ex.Message);
        Assert.Contains("case-insensitive", ex.Message);
        Assert.False(Directory.Exists(outDir));
    }

    [Fact]
    public void Write_refuses_the_bundle_root_itself_as_the_output_directory()
    {
        using var src = new TempDir();
        var site = SiteModel.Build(SampleBundle(src));

        var ex = Assert.Throws<ArgumentException>(() => HtmlWriter.Write(site, src.Path));
        Assert.Contains("bundle", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Write_refuses_an_output_directory_that_differs_from_the_bundle_root_only_by_case()
    {
        // Regression guard: GuardOutputDirectory must compare OrdinalIgnoreCase
        // unconditionally, on every platform -- an OS-conditional comparison
        // (OrdinalIgnoreCase on Windows only) would miss this on a
        // case-insensitive volume such as default macOS APFS, letting the
        // generated site land inside the very bundle it renders.
        using var src = new TempDir();
        var site = SiteModel.Build(SampleBundle(src));

        var caseVariantOfBundleRoot = ToggleCase(src.Path);

        var ex = Assert.Throws<ArgumentException>(() => HtmlWriter.Write(site, caseVariantOfBundleRoot));
        Assert.Contains("bundle", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Write_allows_a_sibling_directory_that_merely_shares_a_name_prefix()
    {
        // A naive prefix check (target.StartsWith(root)) would wrongly reject
        // this: "bundle-site" starts with "bundle" as a raw string, but it is
        // a sibling directory, not something nested inside the bundle. The
        // guard must only reject when root is followed by a directory
        // separator (or is an exact match), which this pins.
        using var tmp = new TempDir();
        var bundleDir = Path.Combine(tmp.Path, "bundle");
        tmp.Write("bundle/index.md", "---\ntype: index\ntitle: Root\ndescription: Root\n---\n");
        var site = SiteModel.Build(Bundle.Load(bundleDir));

        var outDir = Path.Combine(tmp.Path, "bundle-site");
        var written = HtmlWriter.Write(site, outDir);

        Assert.Contains("index.html", written);
        Assert.True(File.Exists(Path.Combine(outDir, "index.html")));
    }

    private static string ToggleCase(string value)
        => new(value.Select(c =>
            char.IsUpper(c) ? char.ToLowerInvariant(c)
            : char.IsLower(c) ? char.ToUpperInvariant(c)
            : c).ToArray());

    [SkippableFact]
    public void Write_refuses_an_out_dir_that_is_a_junction_resolving_inside_the_bundle()
    {
        // Reproduces the demonstrated bypass: `--out` is not lexically inside
        // the bundle (Path.GetFullPath never dereferences a reparse point),
        // but the junction/symlink resolves to a real directory INSIDE the
        // bundle -- exactly what GuardOutputDirectory exists to refuse.
        //   mklink /J <linkHost>\vlink <bundle>\generated-site
        //   okf-render <bundle> --out <linkHost>\vlink
        using var src = new TempDir();
        using var linkHost = new TempDir();
        var site = SiteModel.Build(SampleBundle(src));

        var insideBundle = Path.Combine(src.Path, "generated-site");
        Directory.CreateDirectory(insideBundle);

        Skip.IfNot(linkHost.TryCreateJunctionToExternalDir("vlink", insideBundle), "no junction/symlink privilege on this machine");

        var outDir = Path.Combine(linkHost.Path, "vlink");

        var ex = Assert.Throws<ArgumentException>(() => HtmlWriter.Write(site, outDir));
        Assert.Contains("bundle", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public void Write_refuses_when_a_planted_reparse_point_inside_the_output_directory_escapes_it()
    {
        // Mirrors the same underlying flaw (Path.GetFullPath doesn't
        // dereference reparse points) on GuardWithinOutputDirectory's side:
        // "dest/tables" is planted as a junction pointing OUTSIDE dest before
        // Write ever touches it, so writing "tables/users.html" would
        // otherwise silently land inside `external`, not `dest`.
        using var src = new TempDir();
        using var dest = new TempDir();
        using var external = new TempDir();
        var site = SiteModel.Build(SampleBundle(src));

        Skip.IfNot(dest.TryCreateJunctionToExternalDir("tables", external.Path), "no junction/symlink privilege on this machine");

        var ex = Assert.Throws<ArgumentException>(() => HtmlWriter.Write(site, dest.Path));
        Assert.Contains("outside", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(Path.Combine(external.Path, "users.html")));
    }

    [SkippableFact]
    public void Write_still_refuses_a_second_page_whose_own_directory_is_a_junction_after_a_sibling_directory_was_already_cached_clean()
    {
        // Regression for GuardWithinOutputDirectory's per-directory guard
        // cache (Task D3): "container" is a real directory that hosts the
        // FIRST page written and is verified clean, caching it. "container
        // /inner" -- a DIFFERENT, deeper directory that hosts the SECOND
        // page -- is planted as a junction to `external` before Write ever
        // runs. A caching bug that skipped the walk for any directory once
        // *some* directory had been verified (rather than keying the cache
        // on the exact directory) would let the second write cross the
        // junction the first write's check never even saw. It must not:
        // each directory gets its own walk the first time a file lands in
        // it, cached directory or not.
        using var src = new TempDir();
        using var dest = new TempDir();
        using var external = new TempDir();

        Skip.IfNot(
            dest.TryCreateJunctionToExternalDir(Path.Combine("container", "inner"), external.Path),
            "no junction/symlink privilege on this machine");

        var firstPage = new ViewerPage(
            ConceptId.Parse("first"), "First", "container/first.html", [], "body", [], []);
        var secondPage = new ViewerPage(
            ConceptId.Parse("second"), "Second", "container/inner/second.html", [], "body", [], []);
        var site = new ViewerSite(src.Path, [firstPage, secondPage], string.Empty, []);

        var ex = Assert.Throws<ArgumentException>(() => HtmlWriter.Write(site, dest.Path));
        Assert.Contains("outside", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(Path.Combine(external.Path, "second.html")));
    }

    [SkippableFact]
    public void Write_refuses_a_page_whose_path_crosses_a_junction_whose_link_status_cannot_be_inspected()
    {
        // Task H1, the escape a D3 reviewer executed: "dest/x/y" is a junction
        // to `external` carrying a deny-ReadAttributes ACE, and "dest/x" denies
        // listing, so File.GetAttributes on the junction throws
        // UnauthorizedAccessException while the OS still lets a write traverse
        // it. The lenient IsReparsePoint answered "not a link" and the page
        // landed in external/z/two.html. The guard must fail closed instead:
        // an entry it cannot inspect is refused like a link.
        using var src = new TempDir();
        using var dest = new TempDir();
        using var external = new TempDir();
        using var junction = dest.TryCreateUninspectableJunction(Path.Combine("x", "y"), external.Path);
        Skip.If(junction is null, "needs Windows (a junction plus deny ACEs)");

        var page = new ViewerPage(ConceptId.Parse("x/y/z/two"), "Two", "x/y/z/two.html", [], "body", [], []);
        var site = new ViewerSite(src.Path, [page], string.Empty, []);

        var ex = Assert.Throws<ArgumentException>(() => HtmlWriter.Write(site, dest.Path));
        Assert.StartsWith("refusing to write 'x/y/z/two.html': it resolves outside the output directory", ex.Message, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFileSystemEntries(external.Path));
    }

    [SkippableFact]
    public void Write_refuses_an_out_dir_reached_through_a_junction_whose_link_status_cannot_be_inspected()
    {
        // Task H1, GuardOutputDirectory's side: `--out` is "vlink/site", where
        // "vlink" is a junction into the bundle whose attributes cannot be
        // read. The lenient walk could not see the junction, resolved `--out`
        // to its lexical path, and the site was written into the bundle it
        // renders. Unable to resolve where `--out` lands, the guard must
        // refuse rather than assume. (`--out` being the junction ITSELF was not
        // an escape even on the lenient predicate: Directory.CreateDirectory
        // threw UnauthorizedAccessException on it before anything was written;
        // one level below the junction, it succeeded.)
        using var src = new TempDir();
        using var linkHost = new TempDir();
        var site = SiteModel.Build(SampleBundle(src));
        var insideBundle = Path.Combine(src.Path, "generated-site");
        Directory.CreateDirectory(insideBundle);
        using var junction = linkHost.TryCreateUninspectableJunction(Path.Combine("host", "vlink"), insideBundle);
        Skip.If(junction is null, "needs Windows (a junction plus deny ACEs)");
        var outDir = Path.Combine(junction!.LinkPath, "site");

        var ex = Assert.Throws<ArgumentException>(() => HtmlWriter.Write(site, outDir));
        Assert.StartsWith($"refusing to render into '{outDir}': cannot determine where it resolves", ex.Message, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFileSystemEntries(insideBundle));
    }

    [SkippableFact]
    public void Write_refuses_an_out_dir_through_a_junction_whose_target_cannot_be_read()
    {
        // H1 fix round (M2): "vlink"'s attributes are readable (its parent is
        // listable), so it is seen as a link, but its target cannot be read --
        // Directory.ResolveLinkTarget throws UnauthorizedAccessException.
        // Before, that exception escaped HtmlWriter.Write undocumented; now
        // the resolution fails and the guard refuses with its own message.
        using var src = new TempDir();
        using var linkHost = new TempDir();
        var site = SiteModel.Build(SampleBundle(src));
        var insideBundle = Path.Combine(src.Path, "generated-site");
        Directory.CreateDirectory(insideBundle);
        using var junction = linkHost.TryCreateUninspectableJunction("vlink", insideBundle, denyParentListing: false);
        Skip.If(junction is null, "needs Windows (a junction plus a deny ACE)");
        var outDir = Path.Combine(junction!.LinkPath, "site");

        var ex = Assert.Throws<ArgumentException>(() => HtmlWriter.Write(site, outDir));
        Assert.StartsWith($"refusing to render into '{outDir}': cannot determine where it resolves", ex.Message, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFileSystemEntries(insideBundle));
    }

    [SkippableFact]
    public void Write_to_an_out_dir_on_a_share_that_does_not_exist_reports_the_io_error_not_the_bundle_guard()
    {
        // H1 fix round (I1): a missing share is absence, not an uninspectable
        // entry. The guard must not answer "it is inside the bundle being
        // rendered"; the write reports Windows' own error (network name not
        // found), as it did before the strict predicate.
        //
        // The case is probed first: a host that does not answer a missing share
        // with ERROR_BAD_NET_NAME/ERROR_BAD_NETPATH skips, naming its answer.
        // Once the case is produced, a guard refusal here fails.
        Skip.IfNot(OperatingSystem.IsWindows(), "a UNC share path is Windows-only");
        Assert.StartsWith(new string(Path.DirectorySeparatorChar, 2), AbsentPaths.MissingShareSite, StringComparison.Ordinal); // a UNC path, never a drive-rooted one
        Skip.IfNot(
            AbsentPaths.HostReportsAbsenceAsPlainIOException(AbsentPaths.MissingShareSite, out var observed),
            $"this host answers a missing share with {observed}, not ERROR_BAD_NET_NAME/ERROR_BAD_NETPATH on a plain IOException");
        using var src = new TempDir();
        var site = SiteModel.Build(SampleBundle(src));

        var ex = Assert.ThrowsAny<IOException>(() => HtmlWriter.Write(site, AbsentPaths.MissingShareSite));
        Assert.DoesNotContain("refusing to render", ex.Message, StringComparison.Ordinal);
    }

    [SkippableFact]
    public void Write_to_an_out_dir_on_a_drive_with_no_volume_reports_the_io_error_not_the_bundle_guard()
    {
        // H1 fix round (I1): `okf-render --out F:\site` on an empty drive
        // reported "it is inside the bundle being rendered". It must report
        // the device-not-ready error instead.
        // Probed first, like the share test above.
        var outDir = AbsentPaths.SiteOnADriveWithNoVolume();
        Skip.If(outDir is null, "no drive without a volume on this machine");
        Skip.IfNot(
            AbsentPaths.HostReportsAbsenceAsPlainIOException(outDir!, out var observed),
            $"this host answers the empty drive {outDir} with {observed}, not ERROR_NOT_READY on a plain IOException");
        using var src = new TempDir();
        var site = SiteModel.Build(SampleBundle(src));

        var ex = Assert.ThrowsAny<IOException>(() => HtmlWriter.Write(site, outDir!));
        Assert.DoesNotContain("refusing to render", ex.Message, StringComparison.Ordinal);
    }

    [SkippableFact]
    public void Write_refuses_a_page_file_that_is_an_uninspectable_junction_in_an_already_cached_directory()
    {
        // H1 fix round (M1): "dd/a.html" is written first, so "dd" is cached as
        // verified; "dd/c.html" is then a junction named like the page, whose
        // attributes cannot be read ("dd" denies listing). Only the per-file
        // strict check can refuse it -- the cached directory's walk is
        // skipped. The OS would refuse the write anyway (it is a directory),
        // so this pins the guard, not an escape: the refusal must be the
        // guard's ArgumentException, not the write's UnauthorizedAccessException.
        using var src = new TempDir();
        using var dest = new TempDir();
        using var external = new TempDir();
        Directory.CreateDirectory(Path.Combine(dest.Path, "dd"));
        using var junction = dest.TryCreateUninspectableJunction(Path.Combine("dd", "c.html"), external.Path);
        Skip.If(junction is null, "needs Windows (a junction plus deny ACEs)");
        var first = new ViewerPage(ConceptId.Parse("dd/a"), "A", "dd/a.html", [], "body", [], []);
        var second = new ViewerPage(ConceptId.Parse("dd/c"), "C", "dd/c.html", [], "body", [], []);
        var site = new ViewerSite(src.Path, [first, second], string.Empty, []);

        var ex = Assert.Throws<ArgumentException>(() => HtmlWriter.Write(site, dest.Path));
        Assert.StartsWith("refusing to write 'dd/c.html'", ex.Message, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(dest.Path, "dd", "a.html")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(external.Path));
    }

    [Fact]
    public void Write_still_creates_a_page_in_new_nested_subdirectories()
    {
        // Task H1 regression: the fail-closed guard treats an entry it cannot
        // inspect as a link, but an entry that does not exist yet -- every
        // directory of a page about to be created -- must still be allowed.
        using var src = new TempDir();
        using var dest = new TempDir();
        var page = new ViewerPage(ConceptId.Parse("brand/new/deep/page"), "Page", "brand/new/deep/page.html", [], "body", [], []);
        var site = new ViewerSite(src.Path, [page], string.Empty, []);

        var written = HtmlWriter.Write(site, Path.Combine(dest.Path, "not-yet", "out"));

        Assert.Contains("brand/new/deep/page.html", written);
        Assert.True(File.Exists(Path.Combine(dest.Path, "not-yet", "out", "brand", "new", "deep", "page.html")));
    }

    [Fact]
    public void Write_surfaces_parse_errors_on_the_index_page()
    {
        using var src = new TempDir();
        using var dest = new TempDir();
        src.Write("index.md", "---\ntype: index\ntitle: Root\ndescription: Root\n---\n");
        // A well-formed, terminated frontmatter block that merely omits
        // `type` would NOT reach Bundle.ParseErrors -- neither
        // OkfDocument.Parse nor ConceptId.FromPath require it (§11 `type`
        // conformance is a *validation* concern, not a *parse* one), so it
        // loads as an ordinary concept instead. An unterminated frontmatter
        // block is a genuine parse failure that Bundle.Load does collect
        // into ParseErrors (see SiteModelTests.Build_surfaces_parse_errors_rather_than_dropping_them).
        src.Write("broken.md", "---\ntitle: No closing delimiter\nBody\n");
        var site = SiteModel.Build(Bundle.Load(src.Path));

        HtmlWriter.Write(site, dest.Path);

        var index = File.ReadAllText(Path.Combine(dest.Path, "index.html"));
        Assert.Contains("broken.md", index);
    }

    [Fact]
    public void Write_escapes_html_metacharacters_in_a_concept_title()
    {
        using var src = new TempDir();
        using var dest = new TempDir();
        src.Write("index.md", "---\ntype: index\ntitle: Root\ndescription: Root\n---\n");
        src.Write("evil.md",
            "---\ntype: note\ntitle: \"<img src=x onerror=alert(1)>\"\ndescription: d\n---\nBody\n");
        var site = SiteModel.Build(Bundle.Load(src.Path));

        HtmlWriter.Write(site, dest.Path);

        var page = File.ReadAllText(Path.Combine(dest.Path, "evil.html"));
        Assert.DoesNotContain("<img src=x", page);
        Assert.Contains("&lt;img", page);
    }

    [Fact]
    public void Write_keeps_a_script_closing_tag_in_a_body_inside_the_payload()
    {
        using var src = new TempDir();
        using var dest = new TempDir();
        src.Write("index.md", "---\ntype: index\ntitle: Root\ndescription: Root\n---\n");
        src.Write("evil.md",
            "---\ntype: note\ntitle: Evil\ndescription: d\n---\n</script><img src=x onerror=alert(1)>\n");
        var site = SiteModel.Build(Bundle.Load(src.Path));

        HtmlWriter.Write(site, dest.Path);

        var page = File.ReadAllText(Path.Combine(dest.Path, "evil.html"));
        // RenderShell always emits exactly three <script> elements (the JSON
        // payload, marked, viewer.js). A fourth </script> would mean the
        // body's own literal "</script>" text broke out of the payload
        // container instead of staying HTML-safe-JSON-escaped inside it.
        Assert.Equal(3, CountOccurrences(page, "</script>"));
        Assert.DoesNotContain("<img src=x", page);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }
}
