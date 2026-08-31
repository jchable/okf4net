// SPDX-License-Identifier: LGPL-3.0-or-later
using OkfProducer.Core.CodeGraph;
using OkfProducer.Core.Generation;

namespace OkfProducer.Tests.Generation;

public class CodeConceptIdsTests
{
    [Fact]
    public void A_type_and_its_member_nest_under_the_namespace_path()
    {
        Assert.Equal("code/csharp/okf4net/link-scanner",
            CodeConceptIds.For(Type("OKF4net", "LinkScanner"), CSharp));
        Assert.Equal("code/csharp/okf4net/link-scanner/scan",
            CodeConceptIds.For(Member("OKF4net", "LinkScanner", "Scan"), CSharp));
    }

    [Fact]
    public void A_nested_namespace_becomes_nested_segments()
        => Assert.Equal("code/csharp/okf4net/yaml/yaml-value",
            CodeConceptIds.For(Type("OKF4net.Yaml", "YamlValue"), CSharp));

    [Fact]
    public void Overloads_collapse_to_one_id()
    {
        // §3.2: one concept per (container, name). A numeric suffix would be
        // order-dependent, so adding an overload would renumber its neighbours
        // and churn concepts that did not change.
        var a = CodeConceptIds.For(Member("N", "T", "Validate", "public void Validate()"), CSharp);
        var b = CodeConceptIds.For(Member("N", "T", "Validate", "public void Validate(int x)"), CSharp);

        Assert.Equal(a, b);
    }

    [Fact]
    public void A_member_named_index_is_not_allowed_to_shadow_a_reserved_file()
    {
        // BundleConceptWriter rejects `index` and `log`; a property named Index
        // is perfectly plausible.
        var registry = new ConceptIdRegistry();

        var id = registry.Register("code/csharp/n/t", "Index");

        Assert.NotEqual("code/csharp/n/t/index", id.ToString());
    }

    [Fact]
    public void A_case_only_collision_is_broken_by_ordinal_order_of_the_original_name()
    {
        // §3.3: ordinal on the NAME, not on (file, line), so the tie-break
        // survives a file move or a line shift.
        var registry = new ConceptIdRegistry();

        var first = registry.Register("code/go/pkg", "Parse");
        var second = registry.Register("code/go/pkg", "parse");

        Assert.Equal("code/go/pkg/parse", first.ToString());
        Assert.Equal("code/go/pkg/parse-2", second.ToString());
    }

    [Fact]
    public void The_registry_sees_collisions_across_families()
    {
        // §3.4: usedIds must span overview, packages/, docs/ and code/ — one
        // registry, not one per family.
        var registry = new ConceptIdRegistry();

        var a = registry.Register("packages", "my-lib");
        var b = registry.Register("packages", "my.lib");

        Assert.NotEqual(a.ToString(), b.ToString());
    }

    [Theory]
    // Rule 3, digit -> upper is NOT a boundary: a mid-word digit (this repository's own root
    // namespace) does not split. On its own this case does not distinguish rule 3 from "never split
    // on any digit boundary" -- Utf8Offsets below is the case that does.
    [InlineData("OKF4net", "okf4net")]
    // Rule 3, digit -> upper IS a boundary: two words that happen to meet at a digit do split, unlike
    // OKF4net above where the digit sits mid-word. This is the case that actually pins the direction
    // of the digit rule -- without it, a tokenizer that never splits on any digit boundary would still
    // pass every other case in this table.
    [InlineData("Utf8Offsets", "utf8-offsets")]
    // Rule 2 (acronym run followed by a word splits before the last upper letter of the run).
    [InlineData("IOkfClock", "i-okf-clock")]
    // Rule 1 (plain lower -> upper transition).
    [InlineData("LinkScanner", "link-scanner")]
    [InlineData("YamlEmitter", "yaml-emitter")]
    [InlineData("LfLines", "lf-lines")]
    [InlineData("HtmlWriter", "html-writer")]
    [InlineData("ConceptId", "concept-id")]
    // Rule 1, camelCase entry point rather than PascalCase.
    [InlineData("formatDate", "format-date")]
    // A single all-caps token: an acronym run with no following word must not split.
    [InlineData("HTML", "html")]
    // Already lowercase: no spurious boundary.
    [InlineData("scan", "scan")]
    public void Word_boundaries_match_names_that_actually_occur_in_this_repository(string name, string expectedSlug)
        => Assert.Equal($"code/csharp/n/{expectedSlug}", CodeConceptIds.For(Type("N", name), CSharp));

    private static readonly LanguageProfile CSharp = new(
        Language: "csharp",
        GrammarName: "c_sharp",
        DeclarationQuery: string.Empty,
        CallQuery: string.Empty,
        DocCommentPrefix: "///",
        FileExtensions: [".cs"]);

    private static SymbolFact Type(string container, string name) =>
        new(
            Kind: SymbolKind.Type,
            Language: CSharp.Language,
            Container: container,
            Name: name,
            Signature: $"class {name}",
            Visibility: SymbolVisibility.Public,
            RelativePath: "Fake.cs",
            StartOffset: 0,
            EndOffset: 0,
            StartLine: 1,
            EndLine: 1,
            DocComment: null);

    private static SymbolFact Member(string container, string typeName, string name, string? signature = null) =>
        new(
            Kind: SymbolKind.Member,
            Language: CSharp.Language,
            Container: $"{container}.{typeName}",
            Name: name,
            Signature: signature ?? $"public void {name}()",
            Visibility: SymbolVisibility.Public,
            RelativePath: "Fake.cs",
            StartOffset: 0,
            EndOffset: 0,
            StartLine: 1,
            EndLine: 1,
            DocComment: null);
}
