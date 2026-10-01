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
}
