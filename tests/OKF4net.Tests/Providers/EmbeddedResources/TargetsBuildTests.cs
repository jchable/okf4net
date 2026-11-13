// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net.Tests.Providers.EmbeddedResources;

/// <summary>
/// The .targets of OKF4net.Providers.EmbeddedResources, run by real child builds (spec §5, §6.2).
/// xunit cannot execute MSBuild; these builds are the .targets' only executable guard besides the
/// AOT CI job, so every case here asserts on the built assembly or on a named error code.
/// </summary>
public class TargetsBuildTests
{
    private const string Bundle = """    <OkfBundle Include="knowledge/" Prefix="kb" />""";

    private static readonly string[] CommonNames =
    [
        "a.md", "a.b.c.md", "glossary.fr.md", "x.en-US.md", "sp ace.md", "pct%25.md", "pct%3B.md",
        "$(P).md", "@(I).md", "%(M).md", "q'uote.md", "c,omma.md", "par(en).md", "hash#.md", "amp&.md",
        "brack[et].md", "tilde~.md", ".hidden", ".dir/inner.md", "étoile.md", "emoji-\U0001F600.md",
        "l.licx", "t.restext", "sub;semi/deep/b.md", "nested/one/two/three.md",
    ];

    private static readonly string[] UnixOnlyNames =
    [
        "trailingdot.", " lead.md", "trail .md", "nfd-café.md", "star*.md", "q?.md", "co:lon.md", "lt<gt>.md",
    ];

    private static void WriteTree(string root, IEnumerable<string> names)
    {
        foreach (var name in names)
        {
            var path = Path.Combine(root, name.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "content of " + name + "\n");
        }

        File.WriteAllText(Path.Combine(root, "x.cs"), "this is not C#\n");   // must not be compiled
        File.WriteAllText(Path.Combine(root, "r.resx"), "<not xml\n");      // must not reach ResGen
    }

    [Fact]
    public void Embeds_every_file_byte_for_byte_under_the_names_the_filesystem_provider_lists()
    {
        using var tmp = new TempDir();
        var knowledge = Path.Combine(tmp.Path, "knowledge");
        WriteTree(knowledge, OperatingSystem.IsWindows() ? CommonNames : CommonNames.Concat(UnixOnlyNames));

        var result = EmbeddedBuild.Build(tmp.Path, Bundle);

        Assert.True(result.ExitCode == 0, result.Output);
        var expected = EmbeddedBuild.ReadFileSystemView(knowledge);
        var embedded = EmbeddedBuild.ReadOkfResources(result.AssemblyPath);
        Assert.Equal(expected.Keys.Select(k => "okf:kb/" + k), embedded.Keys);
        foreach (var (name, bytes) in expected)
        {
            Assert.Equal(bytes, embedded["okf:kb/" + name]);
        }

        // No satellite assembly: glossary.fr.md and x.en-US.md stayed in the main assembly.
        Assert.Empty(Directory.GetDirectories(Path.GetDirectoryName(result.AssemblyPath)!));
    }

    [Fact]
    public void Is_inert_without_OkfBundle()
    {
        using var tmp = new TempDir();
        tmp.Write("notes/a.md", "a\n");
        tmp.Write("Class1.cs", "namespace Probe; public static class Class1 { }\n");

        var result = EmbeddedBuild.Build(tmp.Path, "");

        Assert.True(result.ExitCode == 0, result.Output);
        Assert.Empty(EmbeddedBuild.ReadOkfResources(result.AssemblyPath));
    }

    [Fact]
    public void Accepts_a_directory_without_trailing_slash_and_two_bundles()
    {
        using var tmp = new TempDir();
        WriteTree(Path.Combine(tmp.Path, "knowledge"), ["a.md"]);
        tmp.Write("other/o.md", "o\n");

        var result = EmbeddedBuild.Build(tmp.Path, """
                <OkfBundle Include="knowledge" Prefix="kb" />
                <OkfBundle Include="other" Prefix="docs" />
            """);

        Assert.True(result.ExitCode == 0, result.Output);
        Assert.Equal(["okf:docs/o.md", "okf:kb/a.md", "okf:kb/r.resx", "okf:kb/x.cs"], EmbeddedBuild.ReadOkfResources(result.AssemblyPath).Keys);
    }

    [Theory]
    [InlineData("""<OkfBundle Include="knowledge/" />""")]
    [InlineData("""<OkfBundle Include="knowledge/" Prefix="" />""")]
    [InlineData("""<OkfBundle Include="knowledge/" Prefix="a/b" />""")]
    public void Rejects_an_invalid_prefix(string item)
    {
        using var tmp = new TempDir();
        tmp.Write("knowledge/a.md", "a\n");

        var result = EmbeddedBuild.Build(tmp.Path, item);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("OKFEMB001", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void Rejects_a_missing_or_empty_directory()
    {
        using var tmp = new TempDir();
        var missing = EmbeddedBuild.Build(tmp.Path, """<OkfBundle Include="nowhere/" Prefix="kb" />""");
        Assert.Contains("OKFEMB002", missing.Output, StringComparison.Ordinal);

        Directory.CreateDirectory(Path.Combine(tmp.Path, "empty"));
        var empty = EmbeddedBuild.Build(tmp.Path, """<OkfBundle Include="empty/" Prefix="kb" />""");
        Assert.Contains("OKFEMB002", empty.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void Rejects_prefixes_equal_ignoring_case()
    {
        using var tmp = new TempDir();
        tmp.Write("knowledge/a.md", "a\n");
        tmp.Write("other/o.md", "o\n");

        var result = EmbeddedBuild.Build(tmp.Path, """
                <OkfBundle Include="knowledge/" Prefix="kb" />
                <OkfBundle Include="other/" Prefix="KB" />
            """);

        Assert.Contains("OKFEMB003", result.Output, StringComparison.Ordinal);
    }

    [SkippableTheory]
    [InlineData("dq\".md")]
    [InlineData("pi|pe.md")]
    [InlineData("back\\slash.md")]
    [InlineData("ctl\u0001.md")]
    public void Rejects_a_name_an_assembly_cannot_hold(string name)
    {
        Skip.If(OperatingSystem.IsWindows(), "Windows cannot create these names.");
        using var tmp = new TempDir();
        tmp.Write("knowledge/a.md", "a\n");
        File.WriteAllText(Path.Combine(tmp.Path, "knowledge", name), "x\n");

        var result = EmbeddedBuild.Build(tmp.Path, Bundle);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("OKFEMB004", result.Output, StringComparison.Ordinal);
    }

    [SkippableFact]
    public void Rejects_case_twins()
    {
        using var tmp = new TempDir();
        tmp.Write("knowledge/Case.md", "upper\n");
        File.WriteAllText(Path.Combine(tmp.Path, "knowledge", "case.md"), "lower\n");
        Skip.If(Directory.GetFiles(Path.Combine(tmp.Path, "knowledge")).Length < 2, "This volume folds case.");

        var result = EmbeddedBuild.Build(tmp.Path, Bundle);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("OKFEMB004", result.Output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("./", "")]
    [InlineData("../", "proj")]
    public void Refuses_a_bundle_directory_that_holds_the_project(string include, string projectSubdir)
    {
        // Final review, 2026-11-13: such a bundle detached every source (an empty assembly) and embedded
        // obj/project.assets.json, the .csproj and whatever else sat beside them (.git/, .env), with no error.
        using var tmp = new TempDir();
        tmp.Write("a.md", "a\n");
        if (projectSubdir.Length > 0)
        {
            Directory.CreateDirectory(Path.Combine(tmp.Path, projectSubdir));
        }

        var result = EmbeddedBuild.Build(Path.Combine(tmp.Path, projectSubdir), $"""    <OkfBundle Include="{include}" Prefix="kb" />""");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("OKFEMB007", result.Output, StringComparison.Ordinal);
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern uint GetShortPathName(string longPath, System.Text.StringBuilder shortPath, uint length);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern uint GetLongPathName(string shortPath, System.Text.StringBuilder longPath, uint length);

    private static string Win32Path(string path, bool shortForm)
    {
        var buffer = new System.Text.StringBuilder(1024);
        var n = shortForm ? GetShortPathName(path, buffer, (uint)buffer.Capacity) : GetLongPathName(path, buffer, (uint)buffer.Capacity);
        return n == 0 ? path : buffer.ToString();
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void Refuses_the_project_directory_named_through_a_Windows_8_3_alias(bool projectIsShort)
    {
        // External review, 2026-11-13: the project opened by its long path while OkfBundle named the SAME directory
        // by its 8.3 alias (or the reverse) compared as two different paths: the build succeeded and embedded
        // .env, probe.csproj and obj/ under okf:kb/.
        Skip.IfNot(OperatingSystem.IsWindows(), "8.3 aliases are a Windows volume feature.");
        using var tmp = new TempDir();
        var created = Path.Combine(tmp.Path, "a-project-with-a-long-name");
        Directory.CreateDirectory(created);
        File.WriteAllText(Path.Combine(created, "a.md"), "a\n");
        File.WriteAllText(Path.Combine(created, ".env"), "SECRET=synthetic\n");
        var longDir = Win32Path(created, shortForm: false);
        var shortDir = Win32Path(longDir, shortForm: true);
        Skip.If(string.Equals(longDir, shortDir, StringComparison.OrdinalIgnoreCase), "This volume holds no 8.3 alias.");

        var result = EmbeddedBuild.Build(
            projectIsShort ? shortDir : longDir,
            $"""    <OkfBundle Include="{(projectIsShort ? longDir : shortDir)}{Path.DirectorySeparatorChar}" Prefix="kb" />""");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("OKFEMB007", result.Output, StringComparison.Ordinal);
    }

    [SkippableFact]
    public void Refuses_the_project_directory_named_through_a_link_to_it()
    {
        // Same class as the 8.3 alias, one MSBuild never normalises: the project built through a link (a junction,
        // a symlink, a subst drive) while OkfBundle names the real directory. The paths differ as text; only the
        // file system knows they are one directory, which is what the check on the project file asks it.
        using var tmp = new TempDir();
        using var real = new TempDir();
        real.Write("a.md", "a\n");
        real.Write(".env", "SECRET=synthetic\n");
        Skip.IfNot(tmp.TryCreateJunctionToExternalDir("alias", real.Path), "This process cannot create a link.");

        var result = EmbeddedBuild.Build(Path.Combine(tmp.Path, "alias"), $"""    <OkfBundle Include="{real.Path}{Path.DirectorySeparatorChar}" Prefix="kb" />""");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("OKFEMB007", result.Output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""<Target Name="OkfTestLate" AfterTargets="PrepareResourceNames" BeforeTargets="CoreCompile"><ItemGroup><Compile Include="knowledge/x.cs" /></ItemGroup></Target>""")]
    [InlineData("""<Target Name="OkfTestLate" BeforeTargets="CoreCompile"><ItemGroup><Compile Include="knowledge/x.cs" /></ItemGroup></Target>""")]
    public void Refuses_a_Compile_item_that_a_late_target_adds_back_before_compilation(string targetXml)
    {
        // External review, 2026-11-13: a target running after PrepareResourceNames, past the one check of the
        // leftovers, compiled (and ran) a bundle file with no error.
        using var tmp = new TempDir();
        tmp.Write("knowledge/a.md", "a\n");
        tmp.Write("knowledge/x.cs", "class Late { }\n");

        var result = EmbeddedBuild.Build(tmp.Path, Bundle, directoryBuildTargetsXml: $"<Project>{targetXml}</Project>");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("OKFEMB006", result.Output, StringComparison.Ordinal);
    }

    [SkippableFact]
    public void A_xaml_file_of_a_bundle_is_not_handed_to_the_WPF_compiler()
    {
        // External review, 2026-11-13: the WPF SDK classes **/*.xaml as Page, which the detach did not
        // remove: a deliberately incomplete sample.xaml failed the build with MC3000.
        Skip.IfNot(OperatingSystem.IsWindows(), "WPF builds only on Windows.");
        using var tmp = new TempDir();
        tmp.Write("knowledge/a.md", "a\n");
        tmp.Write("knowledge/sample.xaml", "<Page xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\"><Grid>\n");

        var result = EmbeddedBuild.Build(tmp.Path, Bundle, targetFramework: "net10.0-windows", extraPropertiesXml: "<UseWPF>true</UseWPF>");

        Assert.True(result.ExitCode == 0, result.Output);
        Assert.Equal(["okf:kb/a.md", "okf:kb/sample.xaml"], EmbeddedBuild.ReadOkfResources(result.AssemblyPath).Keys);
    }

    [SkippableFact]
    public void Detaches_by_exact_case_on_a_case_sensitive_file_system()
    {
        // Final review, 2026-11-13: the path comparison folded case on every OS, so on Linux a source in
        // knowledge/ was dropped from the build because the bundle is Knowledge/.
        Skip.IfNot(OperatingSystem.IsLinux(), "Only a case-sensitive file system tells the two directories apart.");
        using var tmp = new TempDir();
        tmp.Write("Knowledge/a.md", "a\n");
        tmp.Write("knowledge/Lost.cs", "class LostType { }\n");

        var result = EmbeddedBuild.Build(tmp.Path, """    <OkfBundle Include="Knowledge/" Prefix="kb" />""");

        Assert.True(result.ExitCode == 0, result.Output);
        Assert.Contains("LostType", EmbeddedBuild.ReadTypeNames(result.AssemblyPath));
    }

    [SkippableFact]
    public void Rejects_a_backslash_name_that_collides_with_a_real_path()
    {
        // Outside Windows MSBuild reads "back\slash.md" as "back/slash.md": here a real file, so the glob
        // yields the same path twice and only the twin check can catch it (verified in a Linux container
        // on 2026-11-13; CI is the arbiter on Linux and macOS).
        Skip.If(OperatingSystem.IsWindows(), "Windows cannot create these names.");
        using var tmp = new TempDir();
        tmp.Write("knowledge/back/slash.md", "real\n");
        File.WriteAllText(Path.Combine(tmp.Path, "knowledge", "back\\slash.md"), "literal\n");

        var result = EmbeddedBuild.Build(tmp.Path, Bundle);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("OKFEMB004", result.Output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("knowledge/", "knowledge/")]
    [InlineData("knowledge/", "knowledge")]
    public void Refuses_two_OkfBundle_items_on_one_directory(string first, string second)
    {
        // Final review, 2026-11-13: batching on Identity merged the two, and the last prefix silently won.
        using var tmp = new TempDir();
        tmp.Write("knowledge/a.md", "a\n");

        var result = EmbeddedBuild.Build(tmp.Path, $"""
                <OkfBundle Include="{first}" Prefix="kb" />
                <OkfBundle Include="{second}" Prefix="kc" />
            """);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("OKFEMB003", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void A_prefix_changed_through_a_property_is_picked_up_by_an_incremental_build()
    {
        // Final review, 2026-11-13: LogicalName is no CoreCompile input, so the second build kept "okf:one/".
        using var tmp = new TempDir();
        tmp.Write("knowledge/a.md", "a\n");
        const string Items = """    <OkfBundle Include="knowledge/" Prefix="$(KP)" />""";

        var first = EmbeddedBuild.Build(tmp.Path, Items, extraArgs: ["-p:KP=one"]);
        Assert.True(first.ExitCode == 0, first.Output);
        Assert.Equal(["okf:one/a.md"], EmbeddedBuild.ReadOkfResources(first.AssemblyPath).Keys);

        var second = EmbeddedBuild.Build(tmp.Path, Items, extraArgs: ["-p:KP=two"]);
        Assert.True(second.ExitCode == 0, second.Output);
        Assert.Equal(["okf:two/a.md"], EmbeddedBuild.ReadOkfResources(second.AssemblyPath).Keys);
    }

    [SkippableFact]
    public void Rejects_a_directory_name_with_a_backslash_with_the_named_error()
    {
        // Final review, 2026-11-13: the directory case still ended in a raw MSB4184 outside Windows.
        Skip.If(OperatingSystem.IsWindows(), "Windows cannot create these names.");
        using var tmp = new TempDir();
        tmp.Write("knowledge/a.md", "a\n");
        Directory.CreateDirectory(Path.Combine(tmp.Path, "knowledge", "back\\dir"));
        File.WriteAllText(Path.Combine(tmp.Path, "knowledge", "back\\dir", "x.md"), "x\n");

        var result = EmbeddedBuild.Build(tmp.Path, Bundle);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("OKFEMB004", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("MSB4184", result.Output, StringComparison.Ordinal);
    }

    public static TheoryData<string> LinkKinds => new() { "outward", "cycle", "dangling", "file" };

    [SkippableTheory]
    [MemberData(nameof(LinkKinds))]
    public void Refuses_a_link_without_listing_its_target(string kind)
    {
        using var tmp = new TempDir();
        using var outside = new TempDir();
        outside.Write("secret-inner-dir-marker/secret.md", "secret\n");
        tmp.Write("knowledge/a.md", "a\n");
        tmp.Write("knowledge/sub/b.md", "b\n");
        var created = kind switch
        {
            "outward" => TryDirectoryLink(tmp, "knowledge/sub/link", outside.Path),
            "cycle" => TryDirectoryLink(tmp, "knowledge/sub/link", Path.Combine(tmp.Path, "knowledge")),
            "dangling" => TryDirectoryLink(tmp, "knowledge/sub/link", Path.Combine(outside.Path, "gone")),
            _ => tmp.TryCreateFileSymlinkToExternalFile(Native("knowledge/sub/link.md"), Path.Combine(outside.Path, "secret-inner-dir-marker", "secret.md")),
        };
        // Only the file symlink may legitimately be impossible (Windows without Developer Mode); the
        // three directory cases must run on every OS — the gate (step 7) checks they are not skipped.
        Skip.IfNot(created, "This environment cannot create that link.");

        var result = EmbeddedBuild.Build(tmp.Path, Bundle, verbosity: "detailed");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("OKFEMB005", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("MSB4184", result.Output, StringComparison.Ordinal);
        // The walk never listed the link's target: its inner directory appears nowhere in the detailed log.
        Assert.DoesNotContain("secret-inner-dir-marker", result.Output, StringComparison.Ordinal);
    }

    [SkippableFact]
    public void Refuses_a_link_to_an_unreadable_target_without_trying_to_list_it()
    {
        // Spec §6.2/§8: the strongest form of "never descends": listing the target would THROW
        // (UnauthorizedAccessException -> MSB4184), so OKFEMB005 alone, with no MSB4184, proves no listing was attempted.
        using var tmp = new TempDir();
        using var outside = new TempDir();
        outside.Write("locked/inner/secret.md", "secret\n");
        var locked = Path.Combine(outside.Path, "locked");
        tmp.Write("knowledge/a.md", "a\n");
        tmp.Write("knowledge/sub/b.md", "b\n");

        UninspectableJunction? junction = null;
        try
        {
            if (OperatingSystem.IsWindows())
            {
                junction = tmp.TryCreateUninspectableJunction(Native("knowledge/sub/link"), locked, denyParentListing: false);
                Skip.If(junction is null, "Could not create a junction whose target is unreadable.");
            }
            else
            {
                Skip.IfNot(TryUnixDirectorySymlink(Path.Combine(tmp.Path, Native("knowledge/sub/link")), locked), "Could not create the symlink.");
                File.SetUnixFileMode(locked, UnixFileMode.None);
                var denied = false;
                try
                {
                    Directory.GetDirectories(locked);
                }
                catch (UnauthorizedAccessException)
                {
                    denied = true;
                }

                Skip.IfNot(denied, "The restriction is not effective here (running as root).");
            }

            var result = EmbeddedBuild.Build(tmp.Path, Bundle);

            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains("OKFEMB005", result.Output, StringComparison.Ordinal);
            Assert.DoesNotContain("MSB4184", result.Output, StringComparison.Ordinal);
        }
        finally
        {
            junction?.Dispose();
            if (!OperatingSystem.IsWindows() && Directory.Exists(locked))
            {
                File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }
    }

    /// <summary>TempDir's link helpers combine the relative path as given; on Windows `mklink` rejects '/' ("Option non valide").</summary>
    private static string Native(string relative) => relative.Replace('/', Path.DirectorySeparatorChar);

    /// <summary>A junction on Windows (no privilege needed), a directory symlink elsewhere; a dangling target is created then deleted.</summary>
    private static bool TryDirectoryLink(TempDir tmp, string relativeLink, string target)
    {
        var dangling = !Directory.Exists(target);
        if (dangling)
        {
            Directory.CreateDirectory(target);
        }

        var ok = OperatingSystem.IsWindows()
            ? tmp.TryCreateJunctionToExternalDir(Native(relativeLink), target)
            : TryUnixDirectorySymlink(Path.Combine(tmp.Path, Native(relativeLink)), target);
        if (dangling)
        {
            Directory.Delete(target);
        }

        return ok;
    }

    private static bool TryUnixDirectorySymlink(string link, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(link, target);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    [Fact]
    public void A_bundle_file_is_never_compiled_however_many_sources_the_project_has()
    {
        // Found on 2026-11-13 building OKF4net.Tests: an evaluation-time `Compile Remove` built from an item
        // transform silently stops removing once the project holds 100 Compile items (98 -> 100 flips it),
        // so a real project would compile the bundle's x.cs. The detach runs in a target, on the items themselves.
        using var tmp = new TempDir();
        tmp.Write("knowledge/a.md", "a\n");
        tmp.Write("knowledge/x.cs", "this is not C#\n");
        tmp.Write("knowledge/r.resx", "<not xml\n");
        for (var i = 0; i < 120; i++)
        {
            tmp.Write($"gen/G{i:000}.cs", $"class G{i:000} {{ }}\n");
        }

        var result = EmbeddedBuild.Build(tmp.Path, Bundle);

        Assert.True(result.ExitCode == 0, result.Output);
        Assert.Equal(["okf:kb/a.md", "okf:kb/r.resx", "okf:kb/x.cs"], EmbeddedBuild.ReadOkfResources(result.AssemblyPath).Keys);

        // And only the bundle's files left the Compile list: the project's own sources are still compiled.
        var types = EmbeddedBuild.ReadTypeNames(result.AssemblyPath);
        Assert.Equal(120, types.Count(t => t.StartsWith('G')));
    }

    [Fact]
    public void A_bundle_declared_in_Directory_Build_targets_is_detached_too()
    {
        // The detach runs when the build runs, after every OkfBundle declaration, wherever it sits.
        using var tmp = new TempDir();
        tmp.Write("knowledge/a.md", "a\n");
        tmp.Write("knowledge/x.cs", "this is not C#\n");

        var result = EmbeddedBuild.Build(tmp.Path, "", directoryBuildTargetsXml: """
            <Project><ItemGroup><OkfBundle Include="knowledge/" Prefix="kb" /></ItemGroup></Project>
            """);

        Assert.True(result.ExitCode == 0, result.Output);
        Assert.Equal(["okf:kb/a.md", "okf:kb/x.cs"], EmbeddedBuild.ReadOkfResources(result.AssemblyPath).Keys);
    }

    [Fact]
    public void Refuses_a_default_item_that_a_target_adds_back_after_the_detach()
    {
        using var tmp = new TempDir();
        tmp.Write("knowledge/a.md", "a\n");
        tmp.Write("knowledge/x.cs", "this is not C#\n");

        var result = EmbeddedBuild.Build(tmp.Path, Bundle, directoryBuildTargetsXml: """
            <Project>
              <Target Name="OkfTestReAdd" AfterTargets="AssignTargetPaths" BeforeTargets="OkfEmbedBundles">
                <ItemGroup><Compile Include="knowledge/x.cs" /></ItemGroup>
              </Target>
            </Project>
            """);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("OKFEMB006", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void The_okf_targets_file_sits_at_NuGets_import_point()
    {
        // Spec §8 item 2: an OkfBundle from Directory.Build.targets is NOT yet declared at our import point.
        using var tmp = new TempDir();
        tmp.Write("knowledge/a.md", "a\n");

        var early = EmbeddedBuild.Build(tmp.Path, Bundle);
        Assert.Contains("SEEN_AT_IMPORT=knowledge/", early.Output, StringComparison.Ordinal);

        using var late = new TempDir();
        late.Write("knowledge/a.md", "a\n");
        var lateResult = EmbeddedBuild.Build(late.Path, "", directoryBuildTargetsXml: """
            <Project><ItemGroup><OkfBundle Include="knowledge/" Prefix="kb" /></ItemGroup></Project>
            """);
        Assert.Contains("SEEN_AT_IMPORT=\n", lateResult.Output.Replace("\r\n", "\n"), StringComparison.Ordinal);
    }
}
