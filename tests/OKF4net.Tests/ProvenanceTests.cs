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
}
