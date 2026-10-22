// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Text;
using OKF4net.Providers;

namespace OKF4net.Tests.Providers;

/// <summary>
/// The survey of executable resources at the load (spec §5.1): every Attested Computation
/// concept gets the state of its <c>computation</c> file and of its <c>attester.resource</c>,
/// pinned to a version when they were read; a resource's own problem is a state, never a load
/// failure; and <see cref="Bundle.ReadResourceText(BundleKey, BundleObjectVersion?, out BundleObjectVersion)"/>
/// reads only the object at the pinned version.
/// </summary>
public class BundleExecutableResourceTests
{
    private const string Attester = "def attest(**_):\n    return {}\n";

    private static string Concept(string? computation, string? attester) =>
        "---\ntype: Attested Computation\nruntime: bigquery\nexecutor: { receipt: [job_id] }\n"
        + (computation is null ? string.Empty : $"computation: {computation}\n")
        + (attester is null ? string.Empty : $"attester: {{ resource: {attester} }}\n")
        + "---\n# Computation\n\n```sql\nSELECT 1\n```\n";

    private static BundleObjectVersion VersionOf(IBundleProvider provider, string key)
    {
        using var obj = provider.Read(BundleKey.Parse(key));
        return obj.Version;
    }

    [Fact]
    public void An_inline_computation_with_a_file_attester_pins_the_attester_only()
    {
        var storage = new InMemoryBundleProvider().Add("c/rev.md", Concept(null, "att.py")).Add("att.py", Attester);
        var bundle = Bundle.Load(storage);

        var survey = bundle.ExecutableResources[ConceptId.Parse("c/rev")];
        Assert.Equal(ExecutableResourceState.Undeclared, survey.Computation.State);
        Assert.Null(survey.Computation.RawPath);
        Assert.Equal(ExecutableResourceState.Pinned, survey.Attester.State);
        Assert.True(survey.Attester.IsPinned);
        Assert.Equal("attester.resource", survey.Attester.Field);
        Assert.Equal("att.py", survey.Attester.RawPath);
        Assert.Equal(BundleKey.Parse("att.py"), survey.Attester.Key);
        Assert.Equal(VersionOf(storage, "att.py"), survey.Attester.Version);
    }

    [Fact]
    public void A_computation_file_is_pinned_too_and_a_plain_concept_has_no_entry()
    {
        var storage = new InMemoryBundleProvider()
            .Add("c/rev.md", Concept("q.sql", "att.py")).Add("q.sql", "SELECT 2\n").Add("att.py", Attester)
            .Add("n.md", "---\ntype: Note\ntitle: N\ndescription: n.\n---\n\nN.\n");
        var bundle = Bundle.Load(storage);

        var survey = bundle.ExecutableResources[ConceptId.Parse("c/rev")];
        Assert.Equal(ExecutableResourceState.Pinned, survey.Computation.State);
        Assert.Equal("computation", survey.Computation.Field);
        Assert.Equal(VersionOf(storage, "q.sql"), survey.Computation.Version);
        Assert.Single(bundle.ExecutableResources);
        Assert.Equal(2, bundle.Count);
    }

    [Theory]
    [InlineData(null, ExecutableResourceState.Undeclared)]
    [InlineData("https://example.com/att.py", ExecutableResourceState.Url)]
    [InlineData("../../att.py", ExecutableResourceState.Unsafe)]
    [InlineData("nope.py", ExecutableResourceState.Missing)]
    public void Every_negative_attester_state_is_surveyed_and_the_concept_is_still_loaded(string? attester, ExecutableResourceState expected)
    {
        var storage = new InMemoryBundleProvider().Add("c/rev.md", Concept(null, attester)).Add("att.py", Attester);
        var bundle = Bundle.Load(storage);

        var resource = bundle.ExecutableResources[ConceptId.Parse("c/rev")].Attester;
        Assert.Equal(expected, resource.State);
        Assert.False(resource.IsPinned);
        Assert.Null(resource.Version);
        Assert.Equal(attester, resource.RawPath);
        Assert.NotNull(bundle.Get(ConceptId.Parse("c/rev")));
        Assert.Empty(bundle.ParseErrors);
        Assert.NotNull(BundleValidator.Validate(bundle));
    }

    [Fact]
    public void An_unreadable_or_oversized_resource_is_a_state_not_a_load_failure()
    {
        var unreadable = new InMemoryBundleProvider().Add("c/rev.md", Concept("q.sql", "att.py")).Add("q.sql", "x").Add("att.py", Attester)
            .FailReadOf("att.py", new IOException("disk"));
        var survey = Bundle.Load(unreadable).ExecutableResources[ConceptId.Parse("c/rev")];
        Assert.Equal(ExecutableResourceState.Unreadable, survey.Attester.State);
        Assert.Equal(BundleKey.Parse("att.py"), survey.Attester.Key);
        Assert.Equal(ExecutableResourceState.Pinned, survey.Computation.State);

        // A bound the concept (about 140 bytes) fits and a 400-byte attester does not.
        var small = new InMemoryBundleProvider().Add("c/rev.md", Concept(null, "att.py")).Add("att.py", new string('#', 400) + "\n");
        var bounded = Bundle.Load(small, new BundleLoadOptions { Limits = new BundleLimits { MaxObjectBytes = 256 } });
        var tooLarge = bounded.ExecutableResources[ConceptId.Parse("c/rev")].Attester;
        Assert.Equal(ExecutableResourceState.TooLarge, tooLarge.State);
        Assert.Equal(1, bounded.Count);
    }

    [Fact]
    public void A_resource_that_is_not_UTF8_is_unreadable()
    {
        using var tmp = new TempDir();
        tmp.Write("c/rev.md", Concept(null, "att.py"));
        File.WriteAllBytes(Path.Combine(tmp.Path, "att.py"), [0x64, 0x65, 0x66, 0xFF, 0xFE]);

        var survey = Bundle.Load(tmp.Path).ExecutableResources[ConceptId.Parse("c/rev")];

        Assert.Equal(ExecutableResourceState.Unreadable, survey.Attester.State);
    }

    [Fact]
    public void A_global_failure_during_the_survey_fails_the_load()
    {
        var storage = new InMemoryBundleProvider().Add("c/rev.md", Concept(null, "att.py")).Add("att.py", Attester)
            .FailReadOf("att.py", new BundleLimitExceededException(BundleBound.Entries, 1));

        var e = Assert.Throws<BundleLoadException>(() => Bundle.Load(storage));

        Assert.Contains("entries", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_pinned_read_serves_the_observed_version_only()
    {
        var storage = new InMemoryBundleProvider().Add("c/rev.md", Concept(null, "att.py")).Add("att.py", Attester);
        var bundle = Bundle.Load(storage);
        var pinned = bundle.ExecutableResources[ConceptId.Parse("c/rev")].Attester;

        var text = bundle.ReadResourceText(pinned.Key!, pinned.Version, out var version);
        Assert.Equal(Attester, text);
        Assert.Equal(pinned.Version, version);

        storage.Add("att.py", "def attest(**_):\n    return {'tampered': True}\n");

        var e = Assert.Throws<BundleVersionMismatchException>(() => bundle.ReadResourceText(pinned.Key!, pinned.Version, out _));
        Assert.Equal(BundleKey.Parse("att.py"), e.Key);
        Assert.Equal(Encoding.UTF8.GetString(Encoding.UTF8.GetBytes("def attest(**_):\n    return {'tampered': True}\n")), bundle.ReadResourceText(pinned.Key!, null, out var current));
        Assert.NotEqual(pinned.Version, current);
    }
}
