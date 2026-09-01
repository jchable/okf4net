// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net.Yaml;

namespace OKF4net.Tests;

public class ProvenanceTests
{
    private static YamlValue Yaml(string s) => YamlValue.Parse(s);

    [Fact]
    public void ParseSources_reads_all_signals()
    {
        var s = Provenance.ParseSources(Yaml(
            "- id: ga4-schema\n" +
            "  resource: https://example.com/schema\n" +
            "  title: GA4 schema\n" +
            "  author: team:ga4\n" +
            "  usage_count: 5000\n" +
            "  last_modified: 2026-05-30\n"));
        Assert.Single(s);
        Assert.Equal("ga4-schema", s[0].Id);
        Assert.Equal("https://example.com/schema", s[0].Resource);
        Assert.Equal("GA4 schema", s[0].Title);
        Assert.Equal("team:ga4", s[0].Author!.Value.Raw);
        Assert.Equal(5000L, s[0].UsageCount);
        Assert.Equal("2026-05-30", s[0].LastModified);
    }

    [Fact]
    public void ParseSources_missing_resource_yields_empty_string()
    {
        var s = Provenance.ParseSources(Yaml("- title: no resource here\n"));
        Assert.Single(s);
        Assert.Equal("", s[0].Resource);
    }

    [Fact]
    public void ParseSources_non_sequence_is_empty()
    {
        Assert.Empty(Provenance.ParseSources(Yaml("scalar")));
        Assert.Empty(Provenance.ParseSources(null));
    }

    [Fact]
    public void ParseUsageWindow_reads_from_and_to()
    {
        var w = Provenance.ParseUsageWindow(Yaml("from: 2026-06-01\nto: 2026-06-30\n"));
        Assert.NotNull(w);
        Assert.Equal("2026-06-01", w!.Value.From);
        Assert.Equal("2026-06-30", w.Value.To);
    }

    [Fact]
    public void ParseUsageWindow_null_or_non_mapping_is_null()
    {
        Assert.Null(Provenance.ParseUsageWindow(null));
        Assert.Null(Provenance.ParseUsageWindow(Yaml("scalar")));
    }

    [Fact]
    public void ParseSources_reads_per_entry_usage_window_with_both_bounds()
    {
        var s = Provenance.ParseSources(Yaml(
            "- resource: r\n" +
            "  usage_window:\n" +
            "    from: '2026-01-01T00:00:00Z'\n" +
            "    to: '2026-01-31T00:00:00Z'\n"));
        Assert.Single(s);
        Assert.NotNull(s[0].UsageWindow);
        Assert.Equal("2026-01-01T00:00:00Z", s[0].UsageWindow!.Value.From);
        Assert.Equal("2026-01-31T00:00:00Z", s[0].UsageWindow!.Value.To);
    }

    [Fact]
    public void ParseSources_entry_with_no_usage_window_is_null()
    {
        var s = Provenance.ParseSources(Yaml("- resource: r\n"));
        Assert.Single(s);
        Assert.Null(s[0].UsageWindow);
    }

    [Fact]
    public void ParseSources_entry_usage_window_not_a_mapping_is_null()
    {
        var s = Provenance.ParseSources(Yaml("- resource: r\n  usage_window: hello\n"));
        Assert.Single(s);
        Assert.Null(s[0].UsageWindow);
    }

    [Fact]
    public void ParseSources_entry_usage_window_with_only_from_leaves_to_null()
    {
        var s = Provenance.ParseSources(Yaml(
            "- resource: r\n" +
            "  usage_window:\n" +
            "    from: '2026-01-01T00:00:00Z'\n"));
        Assert.Single(s);
        Assert.NotNull(s[0].UsageWindow);
        Assert.Equal("2026-01-01T00:00:00Z", s[0].UsageWindow!.Value.From);
        Assert.Null(s[0].UsageWindow!.Value.To);
    }

    [Fact]
    public void ToYaml_writes_a_present_usage_window_after_last_modified()
    {
        var yaml = Provenance.ToYaml([
            new Source(Id: null, Resource: "r", Title: null, Author: null, UsageCount: null, LastModified: "2026-01-01",
                UsageWindow: new UsageWindow("2026-01-01T00:00:00Z", "2026-01-31T00:00:00Z")),
        ]);

        var entry = Assert.IsType<YamlMapping>(yaml.Items[0]);
        Assert.Equal(["resource", "last_modified", "usage_window"], entry.Keys.ToList());
        var windowMap = Assert.IsType<YamlMapping>(entry.Get("usage_window"));
        Assert.Equal("2026-01-01T00:00:00Z", windowMap.Get("from")!.AsString());
        Assert.Equal("2026-01-31T00:00:00Z", windowMap.Get("to")!.AsString());
    }

    [Fact]
    public void ToYaml_omits_usage_window_key_entirely_when_null()
    {
        var yaml = Provenance.ToYaml([new Source(Id: null, Resource: "r", Title: null, Author: null, UsageCount: null, LastModified: null, UsageWindow: null)]);

        var entry = Assert.IsType<YamlMapping>(yaml.Items[0]);
        Assert.False(entry.ContainsKey("usage_window"));
    }

    [Fact]
    public void ToYaml_writes_a_present_but_empty_usage_window_as_an_empty_mapping_not_absent()
    {
        var yaml = Provenance.ToYaml([new Source(Id: null, Resource: "r", Title: null, Author: null, UsageCount: null, LastModified: null, UsageWindow: new UsageWindow(null, null))]);

        var entry = Assert.IsType<YamlMapping>(yaml.Items[0]);
        Assert.True(entry.ContainsKey("usage_window"));
        var windowMap = Assert.IsType<YamlMapping>(entry.Get("usage_window"));
        Assert.True(windowMap.IsEmpty);
    }

    [Fact]
    public void ToYaml_ParseSources_round_trip_preserves_a_present_usage_window()
    {
        var sources = new List<Source>
        {
            new(Id: null, Resource: "r", Title: null, Author: null, UsageCount: null, LastModified: null,
                UsageWindow: new UsageWindow("2026-01-01T00:00:00Z", "2026-01-31T00:00:00Z")),
        };

        var roundTripped = Provenance.ParseSources(Provenance.ToYaml(sources));

        Assert.Equal(sources[0], roundTripped[0]);
        Assert.NotNull(roundTripped[0].UsageWindow);
        Assert.Equal("2026-01-01T00:00:00Z", roundTripped[0].UsageWindow!.Value.From);
        Assert.Equal("2026-01-31T00:00:00Z", roundTripped[0].UsageWindow!.Value.To);
    }

    [Fact]
    public void ToYaml_ParseSources_round_trip_preserves_a_present_but_empty_usage_window_as_present_not_null()
    {
        var sources = new List<Source>
        {
            new(Id: null, Resource: "r", Title: null, Author: null, UsageCount: null, LastModified: null,
                UsageWindow: new UsageWindow(null, null)),
        };

        var roundTripped = Provenance.ParseSources(Provenance.ToYaml(sources));

        Assert.Equal(sources[0], roundTripped[0]);
        Assert.NotNull(roundTripped[0].UsageWindow);
        Assert.Null(roundTripped[0].UsageWindow!.Value.From);
        Assert.Null(roundTripped[0].UsageWindow!.Value.To);
    }

    [Fact]
    public void ToYaml_round_trips_through_ParseSources_in_order()
    {
        var sources = new List<Source>
        {
            new(Id: "ga4-schema", Resource: "https://example.com/schema", Title: "GA4 schema",
                Author: Actor.Parse("team:ga4"), UsageCount: 5000, LastModified: "2026-05-30"),
            new(Id: null, Resource: "README.md", Title: null, Author: null, UsageCount: null, LastModified: null),
        };

        var yaml = Provenance.ToYaml(sources);
        var roundTripped = Provenance.ParseSources(yaml);

        Assert.Equal(2, roundTripped.Count);
        Assert.Equal(sources[0], roundTripped[0]);
        Assert.Equal(sources[1], roundTripped[1]);
    }

    [Fact]
    public void ToYaml_omits_absent_optional_fields_from_the_mapping()
    {
        var yaml = Provenance.ToYaml([new Source(Id: null, Resource: "README.md", Title: null, Author: null, UsageCount: null, LastModified: null)]);

        var entry = Assert.IsType<YamlMapping>(yaml.Items[0]);
        Assert.False(entry.ContainsKey("id"));
        Assert.True(entry.ContainsKey("resource"));
        Assert.False(entry.ContainsKey("title"));
        Assert.False(entry.ContainsKey("author"));
        Assert.False(entry.ContainsKey("usage_count"));
        Assert.False(entry.ContainsKey("last_modified"));
    }

    [Fact]
    public void ToYaml_uses_canonical_per_entry_key_order()
    {
        var yaml = Provenance.ToYaml([
            new Source(Id: "x", Resource: "y", Title: "z", Author: Actor.Parse("process:p"), UsageCount: 1, LastModified: "2026-01-01",
                UsageWindow: new UsageWindow("2026-01-01T00:00:00Z", "2026-01-31T00:00:00Z")),
        ]);

        var entry = Assert.IsType<YamlMapping>(yaml.Items[0]);
        Assert.Equal(["id", "resource", "title", "author", "usage_count", "last_modified", "usage_window"], entry.Keys.ToList());
    }

    [Fact]
    public void ToYaml_serializes_author_via_actor_raw_for_every_actor_kind()
    {
        foreach (var raw in new[] { "human:alice", "process:etl-job", "team:ga4" })
        {
            var yaml = Provenance.ToYaml([new Source(Id: null, Resource: "r", Title: null, Author: Actor.Parse(raw), UsageCount: null, LastModified: null)]);
            var entry = Assert.IsType<YamlMapping>(yaml.Items[0]);
            Assert.Equal(raw, entry.Get("author")!.AsString());
        }
    }

    [Fact]
    public void ToYaml_enumerates_the_source_sequence_exactly_once()
    {
        var counting = new CountingSources([new Source(Id: null, Resource: "r", Title: null, Author: null, UsageCount: null, LastModified: null)]);

        Provenance.ToYaml(counting);

        Assert.Equal(1, counting.EnumerationCount);
    }

    [Fact]
    public void ToYaml_treats_a_null_resource_as_empty_string_instead_of_throwing()
    {
        var yaml = Provenance.ToYaml([default(Source)]);

        var entry = Assert.IsType<YamlMapping>(yaml.Items[0]);
        Assert.Equal("", entry.Get("resource")!.AsString());
    }

    private sealed class CountingSources(IReadOnlyList<Source> items) : IEnumerable<Source>
    {
        public int EnumerationCount { get; private set; }

        public IEnumerator<Source> GetEnumerator()
        {
            EnumerationCount++;
            return items.GetEnumerator();
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
