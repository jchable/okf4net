// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net.Yaml;

namespace OKF4net.Tests;

public class TrustTests
{
    private static YamlValue Yaml(string s) => YamlValue.Parse(s);

    [Fact]
    public void ParseGenerated_reads_by_and_at()
    {
        var g = Trust.ParseGenerated(Yaml("by: okf4net/0.3.0\nat: 2026-07-27T10:00:00Z\n"));
        Assert.NotNull(g);
        Assert.Equal("okf4net/0.3.0", g!.Value.By!.Value.Raw);
        Assert.Equal("2026-07-27T10:00:00Z", g.Value.At);
    }

    [Fact]
    public void ParseGenerated_missing_by_yields_null_by()
    {
        var g = Trust.ParseGenerated(Yaml("at: 2026-07-27\n"));
        Assert.NotNull(g);
        Assert.Null(g!.Value.By);
    }

    [Fact]
    public void ParseGenerated_non_mapping_is_null()
    {
        Assert.Null(Trust.ParseGenerated(Yaml("just a scalar")));
        Assert.Null(Trust.ParseGenerated(null));
    }

    [Fact]
    public void ParseVerified_bare_mapping_becomes_one_element_list()
    {
        var v = Trust.ParseVerified(Yaml("by: human:ada\nat: 2026-07-01\n"));
        Assert.Single(v);
        Assert.Equal("human:ada", v[0].By!.Value.Raw);
    }

    [Fact]
    public void ParseVerified_sequence_reads_each_entry()
    {
        var v = Trust.ParseVerified(Yaml("- by: human:ada\n- by: bot/1\n"));
        Assert.Equal(2, v.Count);
    }

    [Fact]
    public void DeriveTier_unverified_when_empty()
        => Assert.Equal(TrustTier.Unverified, Trust.DeriveTier([]));

    [Fact]
    public void DeriveTier_human_beats_machine()
    {
        var v = Trust.ParseVerified(Yaml("- by: bot/1\n- by: human:ada\n"));
        Assert.Equal(TrustTier.HumanReviewed, Trust.DeriveTier(v));
    }

    [Fact]
    public void DeriveTier_machine_confirmed_when_only_non_human()
    {
        var v = Trust.ParseVerified(Yaml("- by: bot/1\n"));
        Assert.Equal(TrustTier.MachineConfirmed, Trust.DeriveTier(v));
    }
}
