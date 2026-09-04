// SPDX-License-Identifier: LGPL-3.0-or-later
using OkfProducer.Core.Generation;

namespace OkfProducer.Tests.Generation;

/// <summary>
/// §5.1's ownership rules on their own, away from the concepts built from them: what MSBuild reports
/// (absolute paths, one item set per <c>(project, TFM)</c>, a file claimed by several projects) turned
/// into the repository-relative, Ordinal-ordered answers <c>ConceptGenerator</c> joins on.
/// </summary>
public class SourceOwnershipMapTests
{
    [Fact]
    public void An_absolute_compile_path_is_relativized_against_the_repository_root()
    {
        // MSBuild reports `FullPath`, while a SymbolFact carries a repository-relative path. If the map
        // stored what MSBuild said, every lookup would miss and no package would ever own a namespace.
        var root = Path.Combine(Path.GetTempPath(), "okfproducer-ownership");
        var map = SourceOwnershipMap.From(root,
            [
                new ProjectCompileItems(
                    Path.Combine(root, "src", "A", "A.csproj"),
                    "net10.0",
                    [Path.Combine(root, "src", "A", "Scanner.cs")]),
            ]);

        Assert.Equal("src/A/A.csproj", map.OwnerOf("src/A/Scanner.cs"));
    }

    [Fact]
    public void A_file_outside_the_repository_is_dropped_rather_than_keyed_absolutely()
    {
        // A linked file from outside the repository was never scanned, so it declares no symbol this
        // producer knows about -- and §6.2 forbids an absolute path reaching anything the bundle is
        // built from.
        var root = Path.Combine(Path.GetTempPath(), "okfproducer-ownership");
        var outside = Path.Combine(Path.GetTempPath(), "elsewhere", "Far.cs");
        var map = SourceOwnershipMap.From(root,
            [new ProjectCompileItems(Path.Combine(root, "src", "A", "A.csproj"), "net10.0", [outside])]);

        Assert.Null(map.OwnerOf(outside));
        Assert.Empty(map.ClaimantsOf(outside.Replace('\\', '/')));
    }

    [Fact]
    public void Claimants_come_back_in_ordinal_order_whatever_order_they_arrived_in()
    {
        // The Ordinal-first rule is only a rule if the order does not depend on which project the
        // caller queried first, so the fixture supplies them in the opposite order.
        var map = SourceOwnershipMap.From("/repo",
            [
                new ProjectCompileItems("src/Z/Z.csproj", "net10.0", ["shared/Thing.cs"]),
                new ProjectCompileItems("src/A/A.csproj", "net10.0", ["shared/Thing.cs"]),
            ]);

        Assert.Equal(["src/A/A.csproj", "src/Z/Z.csproj"], map.ClaimantsOf("shared/Thing.cs"));
        Assert.Equal("src/A/A.csproj", map.OwnerOf("shared/Thing.cs"));
    }

    [Fact]
    public void A_project_reported_under_one_framework_never_reports_an_absent_framework()
    {
        // The ordinary case: one query, one TFM. Nothing may be said about frameworks that were never
        // reported on, so this must stay empty rather than "absent from everything else".
        var map = SourceOwnershipMap.From("/repo",
            [new ProjectCompileItems("src/A/A.csproj", "net10.0", ["src/A/Scanner.cs"])]);

        Assert.Empty(map.FrameworksAbsentFrom("src/A/Scanner.cs"));
    }

    [Fact]
    public void A_file_missing_from_one_of_a_projects_frameworks_reports_that_framework()
    {
        // §5.1's multi-TFM rule: the file set is the union across frameworks, and the gaps are
        // recoverable rather than silently flattened.
        //
        // The map is synthetic on purpose, and this test is the only thing exercising this rule: no
        // caller supplies a multi-framework map today, because MsBuildProjectQuery.Query answers for one
        // framework at a time. See SourceOwnershipMap.FrameworksAbsentFrom.
        var map = SourceOwnershipMap.From("/repo",
            [
                new ProjectCompileItems("src/A/A.csproj", "net10.0", ["src/A/Modern.cs", "src/A/Shared.cs"]),
                new ProjectCompileItems("src/A/A.csproj", "net8.0", ["src/A/Shared.cs"]),
            ]);

        Assert.Equal(["net8.0"], map.FrameworksAbsentFrom("src/A/Modern.cs"));
        Assert.Empty(map.FrameworksAbsentFrom("src/A/Shared.cs"));
        Assert.Equal("src/A/A.csproj", map.OwnerOf("src/A/Modern.cs"));
    }

    [Fact]
    public void One_spelling_of_a_path_is_enough_to_find_it()
    {
        // Separator and `./` normalization on both sides of the lookup, so a caller that happened to
        // spell a path the other way round does not silently get "no owner" -- which would degrade to a
        // missing containment link with no diagnosis.
        var map = SourceOwnershipMap.From("/repo",
            [new ProjectCompileItems("src/A/A.csproj", "net10.0", ["./src\\A\\Scanner.cs"])]);

        Assert.Equal("src/A/A.csproj", map.OwnerOf("src/A/Scanner.cs"));
        Assert.Equal("src/A/A.csproj", map.OwnerOf("src\\A\\Scanner.cs"));
    }

    [Fact]
    public void An_empty_map_claims_nothing()
    {
        Assert.Null(SourceOwnershipMap.Empty.OwnerOf("src/A/Scanner.cs"));
        Assert.Empty(SourceOwnershipMap.Empty.ClaimantsOf("src/A/Scanner.cs"));
        Assert.Empty(SourceOwnershipMap.Empty.FrameworksAbsentFrom("src/A/Scanner.cs"));
    }
}
