// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net.Catalog;

namespace OKF4net.Tests.Catalog;

/// <summary>
/// One red test per <see cref="CatalogDiagnosticCode"/> reject rule, plus accept
/// cases for defaults, explicit values, and source-order preservation.
/// <see cref="CatalogManifestParser.TryParse"/> never throws; every case here
/// asserts a specific diagnostic code (or success), not just "returns false".
/// </summary>
public class CatalogManifestTests
{
    private const string Dir = "/bundles";

    private static bool TryParse(string json, out KnowledgeCatalogSnapshot? snapshot, out IReadOnlyList<CatalogDiagnostic> diagnostics) =>
        CatalogManifestParser.TryParse(json, Dir, out snapshot, out diagnostics);

    // ---- Accept cases ----------------------------------------------------

    [Fact]
    public void Accepts_minimal_manifest_with_defaults()
    {
        const string json = """
            { "version": 1, "sources": [ { "id": "docs", "path": "./docs" } ] }
            """;

        Assert.True(TryParse(json, out var snapshot, out var diagnostics));
        Assert.Empty(diagnostics);
        Assert.NotNull(snapshot);
        Assert.Equal(1, snapshot!.Version);
        Assert.Equal(Dir, snapshot.ManifestDirectory);
        Assert.Equal(0, snapshot.Generation);
        var source = Assert.Single(snapshot.Sources);
        Assert.Equal("docs", source.Id);
        Assert.Equal("./docs", source.Path);
        Assert.Equal(0, source.Priority);
        Assert.True(source.Enabled);
        Assert.Equal(SourceRole.Knowledge, source.Role);
    }

    [Fact]
    public void Accepts_explicit_priority_enabled_and_role()
    {
        const string json = """
            {
              "version": 1,
              "sources": [
                { "id": "docs", "path": "./docs", "priority": 7, "enabled": false, "role": "knowledge" }
              ]
            }
            """;

        Assert.True(TryParse(json, out var snapshot, out var diagnostics));
        Assert.Empty(diagnostics);
        var source = Assert.Single(snapshot!.Sources);
        Assert.Equal(7, source.Priority);
        Assert.False(source.Enabled);
        Assert.Equal(SourceRole.Knowledge, source.Role);
    }

    [Fact]
    public void Preserves_source_order()
    {
        const string json = """
            {
              "version": 1,
              "sources": [
                { "id": "third", "path": "./c" },
                { "id": "first", "path": "./a" },
                { "id": "second", "path": "./b" }
              ]
            }
            """;

        Assert.True(TryParse(json, out var snapshot, out var diagnostics));
        Assert.Empty(diagnostics);
        Assert.Equal(["third", "first", "second"], snapshot!.Sources.Select(s => s.Id));
    }

    // ---- ParseError --------------------------------------------------------

    [Fact]
    public void Rejects_syntactically_invalid_json()
    {
        Assert.False(TryParse("{ not valid json", out var snapshot, out var diagnostics));
        Assert.Null(snapshot);
        Assert.Contains(diagnostics, d => d.Code == CatalogDiagnosticCode.ParseError);
    }

    [Fact]
    public void Rejects_non_object_root()
    {
        Assert.False(TryParse("[1, 2, 3]", out var snapshot, out var diagnostics));
        Assert.Null(snapshot);
        Assert.Contains(diagnostics, d => d.Code == CatalogDiagnosticCode.ParseError);
    }

    // ---- UnknownRootProperty -------------------------------------------------

    [Fact]
    public void Rejects_unknown_root_property()
    {
        const string json = """
            {
              "version": 1,
              "sources": [ { "id": "docs", "path": "./docs" } ],
              "unexpected": true
            }
            """;

        Assert.False(TryParse(json, out var snapshot, out var diagnostics));
        Assert.Null(snapshot);
        Assert.Contains(diagnostics, d => d.Code == CatalogDiagnosticCode.UnknownRootProperty);
    }

    // ---- UnknownSourceProperty -------------------------------------------------

    [Fact]
    public void Rejects_unknown_source_property()
    {
        const string json = """
            {
              "version": 1,
              "sources": [ { "id": "docs", "path": "./docs", "bogus": 1 } ]
            }
            """;

        Assert.False(TryParse(json, out var snapshot, out var diagnostics));
        Assert.Null(snapshot);
        Assert.Contains(diagnostics, d => d.Code == CatalogDiagnosticCode.UnknownSourceProperty);
        Assert.DoesNotContain(diagnostics, d => d.Code == CatalogDiagnosticCode.UnknownRootProperty);
    }

    // ---- WrongVersion --------------------------------------------------------

    [Fact]
    public void Rejects_missing_version()
    {
        const string json = """
            { "sources": [ { "id": "docs", "path": "./docs" } ] }
            """;

        Assert.False(TryParse(json, out var snapshot, out var diagnostics));
        Assert.Null(snapshot);
        Assert.Contains(diagnostics, d => d.Code == CatalogDiagnosticCode.WrongVersion);
    }

    [Fact]
    public void Rejects_version_other_than_1()
    {
        const string json = """
            { "version": 2, "sources": [ { "id": "docs", "path": "./docs" } ] }
            """;

        Assert.False(TryParse(json, out var snapshot, out var diagnostics));
        Assert.Null(snapshot);
        Assert.Contains(diagnostics, d => d.Code == CatalogDiagnosticCode.WrongVersion);
    }

    [Fact]
    public void Rejects_non_integer_version()
    {
        const string json = """
            { "version": 1.5, "sources": [ { "id": "docs", "path": "./docs" } ] }
            """;

        Assert.False(TryParse(json, out var snapshot, out var diagnostics));
        Assert.Null(snapshot);
        Assert.Contains(diagnostics, d => d.Code == CatalogDiagnosticCode.WrongVersion);
    }

    // ---- EmptySources --------------------------------------------------------

    [Fact]
    public void Rejects_missing_sources()
    {
        const string json = """{ "version": 1 }""";

        Assert.False(TryParse(json, out var snapshot, out var diagnostics));
        Assert.Null(snapshot);
        Assert.Contains(diagnostics, d => d.Code == CatalogDiagnosticCode.EmptySources);
    }

    [Fact]
    public void Rejects_empty_sources_array()
    {
        const string json = """{ "version": 1, "sources": [] }""";

        Assert.False(TryParse(json, out var snapshot, out var diagnostics));
        Assert.Null(snapshot);
        Assert.Contains(diagnostics, d => d.Code == CatalogDiagnosticCode.EmptySources);
    }

    // ---- DuplicateSourceId -----------------------------------------------------

    [Fact]
    public void Rejects_duplicate_source_id()
    {
        const string json = """
            {
              "version": 1,
              "sources": [
                { "id": "docs", "path": "./a" },
                { "id": "docs", "path": "./b" }
              ]
            }
            """;

        Assert.False(TryParse(json, out var snapshot, out var diagnostics));
        Assert.Null(snapshot);
        Assert.Contains(diagnostics, d => d.Code == CatalogDiagnosticCode.DuplicateSourceId);
    }

    // ---- InvalidSourceId -------------------------------------------------------

    [Fact]
    public void Rejects_id_starting_with_a_dash()
    {
        const string json = """
            { "version": 1, "sources": [ { "id": "-bad", "path": "./docs" } ] }
            """;

        Assert.False(TryParse(json, out var snapshot, out var diagnostics));
        Assert.Null(snapshot);
        Assert.Contains(diagnostics, d => d.Code == CatalogDiagnosticCode.InvalidSourceId);
    }

    [Fact]
    public void Rejects_missing_id()
    {
        const string json = """
            { "version": 1, "sources": [ { "path": "./docs" } ] }
            """;

        Assert.False(TryParse(json, out var snapshot, out var diagnostics));
        Assert.Null(snapshot);
        Assert.Contains(diagnostics, d => d.Code == CatalogDiagnosticCode.InvalidSourceId);
    }

    [Fact]
    public void Rejects_id_with_disallowed_characters()
    {
        const string json = """
            { "version": 1, "sources": [ { "id": "a b", "path": "./docs" } ] }
            """;

        Assert.False(TryParse(json, out var snapshot, out var diagnostics));
        Assert.Null(snapshot);
        Assert.Contains(diagnostics, d => d.Code == CatalogDiagnosticCode.InvalidSourceId);
    }

    // ---- EmptyPath -------------------------------------------------------------

    [Fact]
    public void Rejects_missing_path()
    {
        const string json = """
            { "version": 1, "sources": [ { "id": "docs" } ] }
            """;

        Assert.False(TryParse(json, out var snapshot, out var diagnostics));
        Assert.Null(snapshot);
        Assert.Contains(diagnostics, d => d.Code == CatalogDiagnosticCode.EmptyPath);
    }

    [Fact]
    public void Rejects_empty_path()
    {
        const string json = """
            { "version": 1, "sources": [ { "id": "docs", "path": "" } ] }
            """;

        Assert.False(TryParse(json, out var snapshot, out var diagnostics));
        Assert.Null(snapshot);
        Assert.Contains(diagnostics, d => d.Code == CatalogDiagnosticCode.EmptyPath);
    }

    // ---- EmbeddedNul ------------------------------------------------------------

    [Fact]
    public void Rejects_embedded_nul_in_path()
    {
        var json = "{ \"version\": 1, \"sources\": [ { \"id\": \"docs\", \"path\": \"." + "\\u0000" + "docs\" } ] }";

        Assert.False(TryParse(json, out var snapshot, out var diagnostics));
        Assert.Null(snapshot);
        Assert.Contains(diagnostics, d => d.Code == CatalogDiagnosticCode.EmbeddedNul);
    }

    // ---- MalformedPriority -------------------------------------------------------

    [Fact]
    public void Rejects_non_integer_priority()
    {
        const string json = """
            { "version": 1, "sources": [ { "id": "docs", "path": "./docs", "priority": "high" } ] }
            """;

        Assert.False(TryParse(json, out var snapshot, out var diagnostics));
        Assert.Null(snapshot);
        Assert.Contains(diagnostics, d => d.Code == CatalogDiagnosticCode.MalformedPriority);
    }

    [Fact]
    public void Rejects_fractional_priority()
    {
        const string json = """
            { "version": 1, "sources": [ { "id": "docs", "path": "./docs", "priority": 1.5 } ] }
            """;

        Assert.False(TryParse(json, out var snapshot, out var diagnostics));
        Assert.Null(snapshot);
        Assert.Contains(diagnostics, d => d.Code == CatalogDiagnosticCode.MalformedPriority);
    }

    // ---- MalformedEnabled --------------------------------------------------------

    [Fact]
    public void Rejects_non_bool_enabled()
    {
        const string json = """
            { "version": 1, "sources": [ { "id": "docs", "path": "./docs", "enabled": "yes" } ] }
            """;

        Assert.False(TryParse(json, out var snapshot, out var diagnostics));
        Assert.Null(snapshot);
        Assert.Contains(diagnostics, d => d.Code == CatalogDiagnosticCode.MalformedEnabled);
    }

    // ---- IllegalRole --------------------------------------------------------------

    [Fact]
    public void Rejects_role_other_than_knowledge()
    {
        const string json = """
            { "version": 1, "sources": [ { "id": "docs", "path": "./docs", "role": "memory" } ] }
            """;

        Assert.False(TryParse(json, out var snapshot, out var diagnostics));
        Assert.Null(snapshot);
        Assert.Contains(diagnostics, d => d.Code == CatalogDiagnosticCode.IllegalRole);
    }

    [Fact]
    public void Rejects_non_string_role()
    {
        const string json = """
            { "version": 1, "sources": [ { "id": "docs", "path": "./docs", "role": 1 } ] }
            """;

        Assert.False(TryParse(json, out var snapshot, out var diagnostics));
        Assert.Null(snapshot);
        Assert.Contains(diagnostics, d => d.Code == CatalogDiagnosticCode.IllegalRole);
    }

    // ---- Never-throw guarantee ---------------------------------------------------

    [Fact]
    public void Never_throws_for_deeply_malformed_input()
    {
        var exception = Record.Exception(() => TryParse("not json at all }{", out _, out _));
        Assert.Null(exception);
    }

    [Fact]
    public void Never_throws_for_null_json_and_reports_ParseError()
    {
        KnowledgeCatalogSnapshot? snapshot = null;
        IReadOnlyList<CatalogDiagnostic> diagnostics = [];
        bool result = false;

        var exception = Record.Exception(() => result = TryParse(null!, out snapshot, out diagnostics));

        Assert.Null(exception);
        Assert.False(result);
        Assert.Null(snapshot);
        Assert.Contains(diagnostics, d => d.Code == CatalogDiagnosticCode.ParseError);
    }

    // ---- F4: diagnostics is a genuine read-only view on a parse failure too --

    [Fact]
    public void Diagnostics_cannot_be_downcast_to_a_mutable_list_on_parse_failure()
    {
        const string json = """{ "version": 2, "sources": [ { "id": "docs", "path": "./docs" } ] }""";

        Assert.False(TryParse(json, out var snapshot, out var diagnostics));
        Assert.Null(snapshot);
        Assert.NotEmpty(diagnostics);

        var castAttempt = Record.Exception(() =>
        {
            var mutable = (List<CatalogDiagnostic>)diagnostics;
            mutable.Clear();
        });

        Assert.IsType<InvalidCastException>(castAttempt);
    }

    // ---- F4 (extended): the EARLY-exit failure paths also hand out a read-only view --
    // Regression: null JSON / malformed JSON / non-object root used to return the
    // raw mutable List<CatalogDiagnostic> (only the late validation path wrapped
    // via AsReadOnly). FileKnowledgeCatalog republishes this list verbatim through
    // LastReloadDiagnostics, so a caller could downcast and mutate the published
    // diagnostics after an invalid reload. Every early exit now wraps identically.

    [Theory]
    [InlineData("{")]           // malformed JSON  -> JsonException early exit
    [InlineData("[1, 2, 3]")]   // non-object root -> ParseError early exit
    public void Diagnostics_cannot_be_downcast_to_a_mutable_list_on_early_exit(string json)
    {
        Assert.False(TryParse(json, out _, out var diagnostics));
        Assert.NotEmpty(diagnostics);

        var castAttempt = Record.Exception(() =>
        {
            var mutable = (List<CatalogDiagnostic>)diagnostics;
            mutable.Clear();
        });

        Assert.IsType<InvalidCastException>(castAttempt);
    }

    [Fact]
    public void Diagnostics_cannot_be_downcast_to_a_mutable_list_on_null_json()
    {
        Assert.False(TryParse(null!, out _, out var diagnostics));
        Assert.NotEmpty(diagnostics);

        var castAttempt = Record.Exception(() =>
        {
            var mutable = (List<CatalogDiagnostic>)diagnostics;
            mutable.Clear();
        });

        Assert.IsType<InvalidCastException>(castAttempt);
    }

    // ---- Sources is a genuine read-only view (not just a List<T> hidden behind an interface) --

    [Fact]
    public void Sources_cannot_be_downcast_to_a_mutable_list_and_mutated()
    {
        const string json = """
            { "version": 1, "sources": [ { "id": "docs", "path": "./docs" } ] }
            """;

        Assert.True(TryParse(json, out var snapshot, out var diagnostics));
        Assert.Empty(diagnostics);

        Assert.IsType<System.Collections.ObjectModel.ReadOnlyCollection<KnowledgeCatalogSource>>(snapshot!.Sources);

        var castAttempt = Record.Exception(() =>
        {
            var mutable = (List<KnowledgeCatalogSource>)snapshot.Sources;
            mutable.Add(new KnowledgeCatalogSource("intruder", "./x", 0, true, SourceRole.Knowledge));
        });

        Assert.IsType<InvalidCastException>(castAttempt);
    }
}
