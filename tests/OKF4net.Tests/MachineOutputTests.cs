// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Text.Json;
using System.Text.RegularExpressions;

namespace OKF4net.Tests;

/// <summary>
/// Full-projection tests for the four machine outputs (design §5.1, §5.3):
/// each compares the ENTIRE document to a hand-derived expectation, so a
/// projected value that no field-by-field test asserted -- the round-1 hole
/// was <c>findings[].status</c> -- cannot drift, and a property that appears
/// or disappears fails the test. The expectations are derived from the
/// fixture files, the hand-verified text snapshots and the source lines that
/// project each value, never captured from a run: a captured expectation is
/// the tautology the update mode exists to refuse.
/// </summary>
public class MachineOutputTests
{
    private static readonly string AppendixA = Path.Combine(TestPaths.RepoRoot(), "tests", "fixtures", "appendix_a");
    private static readonly string OkfV02 = Path.Combine(TestPaths.RepoRoot(), "tests", "fixtures", "okf_v02");
    private static readonly string OkfV02Reserved = Path.Combine(TestPaths.RepoRoot(), "tests", "fixtures", "okf_v02_reserved");

    /// <summary>The date every pinned invocation here evaluates at; before any fixture's <c>stale_after</c>.</summary>
    private const string AsOf = "2026-09-25";

    private static (int Code, string Out, string Err) Run(params string[] args) => TestPaths.Run(args);

    /// <summary>A path with forward slashes, the form the expectations are written in.</summary>
    private static string Fwd(string path) => path.Replace('\\', '/');

    /// <summary>
    /// The one normalisation <c>GoldenParityTests</c> applies to JSON output
    /// (<c>Audit_json_matches_golden</c>): on Windows the projected file paths
    /// carry native separators, escaped by the serializer as the two-character
    /// sequence <c>\\</c>. Collapsed to <c>/</c> on the OUTPUT before parsing,
    /// so the expectations can be written once for every platform. The raw
    /// <c>bundle</c> string is asserted BEFORE this, so a projection that
    /// rewrote the argument's separators would still be caught.
    /// </summary>
    private static string NormalizeJsonPaths(string json) => json.Replace("\\\\", "/");

    /// <summary>The <c>bundle</c> property as projected, before any normalisation: the argument verbatim.</summary>
    private static void AssertRawBundle(string expectedArgument, string rawJson)
    {
        using var doc = JsonDocument.Parse(rawJson);
        Assert.Equal(expectedArgument, doc.RootElement.GetProperty("bundle").GetString());
    }

    [Fact]
    public void Validate_json_projects_a_conformant_bundle_completely()
    {
        var r = Run("validate", AppendixA, "--as-of", AsOf, "--json");
        Assert.Equal(0, r.Code);
        Assert.EndsWith("\n", r.Out, StringComparison.Ordinal);
        AssertRawBundle(AppendixA, r.Out);

        var b = Fwd(AppendixA);
        var expected = $$"""
        {
          "bundle": "{{b}}",
          "asOf": "2026-09-25",
          "evaluatedAt": "2026-09-25T00:00:00Z",
          "conformant": true,
          "conceptCount": 4,
          "errorCount": 0,
          "warningCount": 8,
          "infoCount": 0,
          "diagnostics": [
            { "severity": "warning", "code": "LegacyTimestamp", "path": "{{b}}/datasets/sales.md", "conceptId": "datasets/sales", "field": "timestamp", "message": "`timestamp` is a legacy field; prefer `generated.at`" },
            { "severity": "warning", "code": "MissingRecommendedField", "path": "{{b}}/tables/customers.md", "conceptId": "tables/customers", "field": "resource", "message": "missing recommended frontmatter field `resource`" },
            { "severity": "warning", "code": "MissingRecommendedField", "path": "{{b}}/tables/customers.md", "conceptId": "tables/customers", "field": "tags", "message": "missing recommended frontmatter field `tags`" },
            { "severity": "warning", "code": "LegacyTimestamp", "path": "{{b}}/tables/customers.md", "conceptId": "tables/customers", "field": "timestamp", "message": "`timestamp` is a legacy field; prefer `generated.at`" },
            { "severity": "warning", "code": "LegacyTimestamp", "path": "{{b}}/tables/orders.md", "conceptId": "tables/orders", "field": "timestamp", "message": "`timestamp` is a legacy field; prefer `generated.at`" },
            { "severity": "warning", "code": "MissingRecommendedField", "path": "{{b}}/tables/users.md", "conceptId": "tables/users", "field": "description", "message": "missing recommended frontmatter field `description`" },
            { "severity": "warning", "code": "MissingRecommendedField", "path": "{{b}}/tables/users.md", "conceptId": "tables/users", "field": "resource", "message": "missing recommended frontmatter field `resource`" },
            { "severity": "warning", "code": "MissingRecommendedField", "path": "{{b}}/tables/users.md", "conceptId": "tables/users", "field": "tags", "message": "missing recommended frontmatter field `tags`" }
          ]
        }
        """;

        JsonShape.AssertEquivalent(expected, NormalizeJsonPaths(r.Out));
    }

    [Fact]
    public void Validate_json_projects_a_non_conformant_bundle_completely()
    {
        // The opposite case for every verdict-bearing field: conformant false,
        // errorCount non-zero, warningCount zero, diagnostics without a
        // conceptId (reserved files are not concepts), and one diagnostic with
        // a `field` (RootIndexExtraFrontmatter names okf_version) next to
        // three without.
        var r = Run("validate", OkfV02Reserved, "--as-of", AsOf, "--json");
        Assert.Equal(1, r.Code);
        Assert.EndsWith("\n", r.Out, StringComparison.Ordinal);
        AssertRawBundle(OkfV02Reserved, r.Out);

        var b = Fwd(OkfV02Reserved);
        var expected = $$"""
        {
          "bundle": "{{b}}",
          "asOf": "2026-09-25",
          "evaluatedAt": "2026-09-25T00:00:00Z",
          "conformant": false,
          "conceptCount": 1,
          "errorCount": 4,
          "warningCount": 0,
          "infoCount": 0,
          "diagnostics": [
            { "severity": "error", "code": "UnparseableIndex", "path": "{{b}}/broken/index.md", "conceptId": null, "field": null, "message": "unparseable index.md: Invalid YAML in frontmatter: YAML error at line 1: expected ',' or ']' in flow sequence" },
            { "severity": "error", "code": "RootIndexExtraFrontmatter", "path": "{{b}}/index.md", "conceptId": null, "field": "okf_version", "message": "root index.md frontmatter must declare only `okf_version` (§12)" },
            { "severity": "error", "code": "IndexHasFrontmatter", "path": "{{b}}/sub/index.md", "conceptId": null, "field": null, "message": "index.md must not contain frontmatter (§8)" },
            { "severity": "error", "code": "LogDateInvalid", "path": "{{b}}/log.md", "conceptId": null, "field": null, "message": "log date heading is not ISO-8601 `YYYY-MM-DD`: \"not-a-date\"" }
          ]
        }
        """;

        JsonShape.AssertEquivalent(expected, NormalizeJsonPaths(r.Out));
    }

    [Fact]
    public void Validate_json_projects_an_info_diagnostic_completely()
    {
        // The two projections above both carry infoCount 0, so a projection
        // that returned a constant 0 passed every test (an external review's
        // mutation survived). A broken cross-link is the one diagnostic the
        // validator reports as info (Validate.cs: "Broken cross-links are
        // permitted; report them as info only"): Diagnostic(Severity.Info,
        // path: null, concept: the source, "link target does not resolve to
        // a concept in the bundle: " + the link's target as written,
        // DiagnosticCode.BrokenLink), with no field. The concept carries
        // every recommended field so nothing else is reported, and a
        // conformant bundle with a non-zero infoCount still exits 0.
        using var tmp = new TempDir();
        tmp.Write(
            "a.md",
            "---\ntype: Note\ntitle: A\ndescription: d\nresource: https://example.com/a\ntags: [x]\n---\n\nSee [gone](/missing.md).\n");

        var r = Run("validate", tmp.Path, "--as-of", AsOf, "--json");
        Assert.Equal(0, r.Code);
        Assert.EndsWith("\n", r.Out, StringComparison.Ordinal);
        AssertRawBundle(tmp.Path, r.Out);

        var expected = $$"""
        {
          "bundle": "{{Fwd(tmp.Path)}}",
          "asOf": "2026-09-25",
          "evaluatedAt": "2026-09-25T00:00:00Z",
          "conformant": true,
          "conceptCount": 1,
          "errorCount": 0,
          "warningCount": 0,
          "infoCount": 1,
          "diagnostics": [
            { "severity": "info", "code": "BrokenLink", "path": null, "conceptId": "a", "field": null, "message": "link target does not resolve to a concept in the bundle: /missing.md" }
          ]
        }
        """;

        JsonShape.AssertEquivalent(expected, NormalizeJsonPaths(r.Out));
    }

    [Fact]
    public void Info_json_projects_appendix_a_completely()
    {
        var r = Run("info", AppendixA, "--json");
        Assert.Equal(0, r.Code);
        Assert.EndsWith("\n", r.Out, StringComparison.Ordinal);
        AssertRawBundle(AppendixA, r.Out);

        var expected = $$"""
        {
          "bundle": "{{Fwd(AppendixA)}}",
          "okfVersion": null,
          "conceptCount": 4,
          "indexFileCount": 0,
          "logFileCount": 1,
          "types": { "BigQuery Dataset": 1, "BigQuery Table": 3 },
          "linkCount": 5,
          "brokenLinkCount": 0,
          "parseErrors": []
        }
        """;

        JsonShape.AssertEquivalent(expected, NormalizeJsonPaths(r.Out));
    }

    [Fact]
    public void Info_json_projects_the_opposite_cases_completely()
    {
        // okfVersion present, a broken link, a parse error, an index file and
        // no log file: the opposite of every appendix_a value above.
        using var tmp = new TempDir();
        tmp.Write("index.md", "---\nokf_version: \"0.2\"\n---\n\n# Root\n");
        tmp.Write("a.md", "---\ntype: Note\ntitle: A\ndescription: d\n---\n\nSee [b](/b.md) and [gone](/missing.md).\n");
        tmp.Write("b.md", "---\ntype: Note\ntitle: B\ndescription: d\n---\n\nbody\n");
        tmp.Write("bad.md", "---\ntype: Note\nk: *a\n---\nbody\n");

        var r = Run("info", tmp.Path, "--json");
        Assert.Equal(0, r.Code);
        Assert.EndsWith("\n", r.Out, StringComparison.Ordinal);
        AssertRawBundle(tmp.Path, r.Out);

        // The parse error's message is DERIVED: YamlParser.AliasMessage, wrapped
        // by YamlParseException.FormatMessage with the frontmatter line (the
        // alias is on line 2 of the frontmatter), then by OkfDocument.Parse's
        // "Invalid YAML in frontmatter: " prefix. Not read from r.Out.
        var expected = $$"""
        {
          "bundle": "{{Fwd(tmp.Path)}}",
          "okfVersion": "0.2",
          "conceptCount": 2,
          "indexFileCount": 1,
          "logFileCount": 0,
          "types": { "Note": 2 },
          "linkCount": 2,
          "brokenLinkCount": 1,
          "parseErrors": [
            { "path": "{{Fwd(Path.Combine(tmp.Path, "bad.md"))}}", "message": "Invalid YAML in frontmatter: YAML error at line 2: YAML aliases (*name) are not supported by the OKF YAML subset" }
          ]
        }
        """;

        JsonShape.AssertEquivalent(expected, NormalizeJsonPaths(r.Out));
    }

    [Fact]
    public void Audit_json_projects_a_stale_finding_completely()
    {
        var r = Run("audit", OkfV02, "--as-of", "2099-06-01", "--json");
        Assert.Equal(0, r.Code);
        Assert.EndsWith("\n", r.Out, StringComparison.Ordinal);
        AssertRawBundle(OkfV02, r.Out);

        var b = Fwd(OkfV02);
        var expected = $$"""
        {
          "bundle": "{{b}}",
          "asOf": "2099-06-01",
          "evaluatedAt": "2099-06-01T00:00:00Z",
          "conceptCount": 2,
          "query": { "stale": true, "trust": null, "status": null, "type": null },
          "trust": { "unverified": 1, "machine-confirmed": 0, "human-reviewed": 1 },
          "status": { "draft": 0, "stable": 2, "deprecated": 0 },
          "staleCount": 1,
          "findings": [
            { "conceptId": "metrics/dau", "path": "{{b}}/metrics/dau.md", "type": "Metric", "title": "Daily Active Users", "trust": "human-reviewed", "status": "stable", "staleAfter": "2099-01-01T00:00:00Z", "stale": true }
          ]
        }
        """;

        JsonShape.AssertEquivalent(expected, NormalizeJsonPaths(r.Out));
    }

    [Fact]
    public void Audit_json_projects_an_empty_stale_query_completely()
    {
        // Same bundle before anything is stale: staleCount 0, findings empty,
        // the counts unchanged -- they are bundle-wide, not query-scoped.
        var r = Run("audit", OkfV02, "--as-of", AsOf, "--json");
        Assert.Equal(0, r.Code);
        Assert.EndsWith("\n", r.Out, StringComparison.Ordinal);

        var expected = $$"""
        {
          "bundle": "{{Fwd(OkfV02)}}",
          "asOf": "2026-09-25",
          "evaluatedAt": "2026-09-25T00:00:00Z",
          "conceptCount": 2,
          "query": { "stale": true, "trust": null, "status": null, "type": null },
          "trust": { "unverified": 1, "machine-confirmed": 0, "human-reviewed": 1 },
          "status": { "draft": 0, "stable": 2, "deprecated": 0 },
          "staleCount": 0,
          "findings": []
        }
        """;

        JsonShape.AssertEquivalent(expected, NormalizeJsonPaths(r.Out));
    }

    [Fact]
    public void Audit_json_projects_a_filtered_query_completely()
    {
        // Every query field non-null at once; --stale is off because a filter
        // is a report over the selection, so `dau` is listed for its trust and
        // still reported stale at this date.
        var r = Run("audit", OkfV02, "--as-of", "2099-06-01", "--status", "stable", "--type", "Metric", "--trust", "human-reviewed", "--json");
        Assert.Equal(0, r.Code);
        Assert.EndsWith("\n", r.Out, StringComparison.Ordinal);

        var b = Fwd(OkfV02);
        var expected = $$"""
        {
          "bundle": "{{b}}",
          "asOf": "2099-06-01",
          "evaluatedAt": "2099-06-01T00:00:00Z",
          "conceptCount": 2,
          "query": { "stale": false, "trust": ["human-reviewed"], "status": "stable", "type": "Metric" },
          "trust": { "unverified": 1, "machine-confirmed": 0, "human-reviewed": 1 },
          "status": { "draft": 0, "stable": 2, "deprecated": 0 },
          "staleCount": 1,
          "findings": [
            { "conceptId": "metrics/dau", "path": "{{b}}/metrics/dau.md", "type": "Metric", "title": "Daily Active Users", "trust": "human-reviewed", "status": "stable", "staleAfter": "2099-01-01T00:00:00Z", "stale": true }
          ]
        }
        """;

        JsonShape.AssertEquivalent(expected, NormalizeJsonPaths(r.Out));
    }

    [Fact]
    public void Audit_json_projects_distinct_categories_with_asymmetric_counts()
    {
        // okf_v02 only ever yields stable / human-reviewed, with zeros in the
        // other buckets, so a projection that permuted the vocabularies would
        // pass the three tests above. Here every count differs from its
        // neighbours: trust 1/2/0, status 2/0/1.
        using var tmp = new TempDir();
        tmp.Write("d1.md", "---\ntype: Note\ntitle: D1\nstatus: draft\nverified:\n  - { by: process:bot, at: 2026-06-01T00:00:00Z }\n---\n\nbody\n");
        tmp.Write("d2.md", "---\ntype: Note\ntitle: D2\nstatus: draft\nverified:\n  - { by: process:bot, at: 2026-06-01T00:00:00Z }\n---\n\nbody\n");
        tmp.Write("e.md", "---\ntype: Note\ntitle: E\nstatus: deprecated\n---\n\nbody\n");

        var r = Run("audit", tmp.Path, "--as-of", AsOf, "--status", "draft", "--json");
        Assert.Equal(0, r.Code);
        Assert.EndsWith("\n", r.Out, StringComparison.Ordinal);
        AssertRawBundle(tmp.Path, r.Out);

        var b = Fwd(tmp.Path);
        var expected = $$"""
        {
          "bundle": "{{b}}",
          "asOf": "2026-09-25",
          "evaluatedAt": "2026-09-25T00:00:00Z",
          "conceptCount": 3,
          "query": { "stale": false, "trust": null, "status": "draft", "type": null },
          "trust": { "unverified": 1, "machine-confirmed": 2, "human-reviewed": 0 },
          "status": { "draft": 2, "stable": 0, "deprecated": 1 },
          "staleCount": 0,
          "findings": [
            { "conceptId": "d1", "path": "{{b}}/d1.md", "type": "Note", "title": "D1", "trust": "machine-confirmed", "status": "draft", "staleAfter": null, "stale": false },
            { "conceptId": "d2", "path": "{{b}}/d2.md", "type": "Note", "title": "D2", "trust": "machine-confirmed", "status": "draft", "staleAfter": null, "stale": false }
          ]
        }
        """;

        JsonShape.AssertEquivalent(expected, NormalizeJsonPaths(r.Out));
    }

    /// <summary>
    /// The restricted DOT grammar <c>WriteGraphDot</c> emits: header, the
    /// fixed <c>rankdir</c> line, one edge statement per link, a closing
    /// brace, nothing else. This is the guarantee -- Graphviz is not installed
    /// in CI and is not invoked; a document this grammar accepts that
    /// Graphviz would reject is outside what the test can promise.
    /// </summary>
    private static readonly Regex DotEdgeLine = new(
        "^  \"[^\"]+\" -> \"[^\"]+\"( \\[style=dashed, color=red\\])?;$",
        RegexOptions.CultureInvariant);

    [Fact]
    public void Graph_dot_follows_the_restricted_grammar_with_the_expected_edge_set()
    {
        // Three concepts: one with a resolved link and two broken ones, one
        // linked-to with no outgoing link, one isolated. Edge ORDER is not
        // asserted -- it is presentation (design §1); the SET is, and so is
        // its size, so an edge emitted twice cannot hide in a set. The second
        // broken target is NESTED (`does/not/exist`): the test this one
        // replaced used that shape, and without it an implementation that
        // kept only the last segment of a broken target passed the whole
        // suite (an external review's mutation survived).
        using var tmp = new TempDir();
        tmp.Write("a.md", "---\ntype: Note\ntitle: A\n---\n\nSee [b](/b.md), [gone](/missing.md) and [deep](/does/not/exist.md).\n");
        tmp.Write("b.md", "---\ntype: Note\ntitle: B\n---\n\nno links\n");
        tmp.Write("c.md", "---\ntype: Note\ntitle: C\n---\n\nisolated\n");

        var r = Run("graph", tmp.Path, "--dot");
        Assert.Equal(0, r.Code);
        Assert.EndsWith("}\n", r.Out, StringComparison.Ordinal);

        var lines = r.Out.Split('\n');
        Assert.Equal("", lines[^1]); // the trailing newline
        Assert.Equal("digraph okf {", lines[0]);
        Assert.Equal("  rankdir=LR; node [shape=box, fontsize=10];", lines[1]);
        Assert.Equal("}", lines[^2]);

        var edges = lines[2..^2];
        Assert.Equal(3, edges.Length);
        Assert.All(edges, line => Assert.Matches(DotEdgeLine, line));
        Assert.Equal(
            new HashSet<string>(StringComparer.Ordinal)
            {
                "  \"a\" -> \"b\";",
                "  \"a\" -> \"missing\" [style=dashed, color=red];",
                "  \"a\" -> \"does/not/exist\" [style=dashed, color=red];",
            },
            edges.ToHashSet(StringComparer.Ordinal));
    }

    [Fact]
    public void Graph_dot_is_deterministic_across_runs()
    {
        var first = Run("graph", AppendixA, "--dot");
        var second = Run("graph", AppendixA, "--dot");
        Assert.Equal(0, first.Code);
        Assert.Equal(first.Out, second.Out);
    }

    /// <summary>
    /// The one text line the design keeps under direct test (§5.1): it carries
    /// two counts, and a rewording that drops the second silently loses the
    /// broken-link information. Spacing is presentation and is not asserted.
    /// </summary>
    private static readonly Regex LinksLine = new(@"^links:\s+(\d+) internal \((\d+) broken\)$", RegexOptions.Multiline | RegexOptions.CultureInvariant);

    [Fact]
    public void Info_links_line_reports_both_counts()
    {
        var r = Run("info", AppendixA);
        Assert.Equal(0, r.Code);
        var m = LinksLine.Match(r.Out);
        Assert.True(m.Success, "no `links: N internal (M broken)` line in:\n" + r.Out);
        Assert.Equal("5", m.Groups[1].Value);
        Assert.Equal("0", m.Groups[2].Value);
    }

    [Fact]
    public void Info_links_line_counts_a_broken_link()
    {
        // A broken link is still a link: `linkCount` sums LinksFrom, which
        // includes unresolved targets (JsonOutput.WriteInfo), so one broken
        // link reads `1 internal (1 broken)` -- verified by execution.
        using var tmp = new TempDir();
        tmp.Write("a.md", "---\ntype: Note\ntitle: A\n---\n\n[gone](/missing.md)\n");

        var r = Run("info", tmp.Path);
        Assert.Equal(0, r.Code);
        var m = LinksLine.Match(r.Out);
        Assert.True(m.Success, r.Out);
        Assert.Equal("1", m.Groups[1].Value);
        Assert.Equal("1", m.Groups[2].Value);
    }
}
