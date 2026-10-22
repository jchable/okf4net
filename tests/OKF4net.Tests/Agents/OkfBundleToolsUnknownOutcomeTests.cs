// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net.Agents;
using OKF4net.Providers;

namespace OKF4net.Tests.Agents;

/// <summary>
/// A write whose outcome is unknown (<see cref="BundleWriteOutcomeUnknownException"/>) is
/// reported by the writer as an <c>Error:</c> text and never notified as committed, although
/// its content may have landed. The tool set caches the loaded bundle, so each write tool
/// drops that cache on an <c>Error:</c> result: the next read shows what the file holds.
/// </summary>
public class OkfBundleToolsUnknownOutcomeTests
{
    private const string Fm = "type: Metric\ntitle: DAU\ndescription: Daily active users.\n";

    /// <summary>A filesystem provider whose every write commits and then fails with an unknown outcome, as a commit that took effect but whose success could not be established.</summary>
    private sealed class CommitThenUnknown(FileSystemBundleProvider inner) : IBundleProvider
    {
        public string Id => inner.Id;

        public string Location => inner.Location;

        public BundleProviderCapabilities Capabilities => inner.Capabilities;

        public BundleKeyConstraints NativeConstraints => inner.NativeConstraints;

        public BundleVersion GetVersion(BundleLimits limits) => inner.GetVersion(limits);

        public IEnumerable<BundleListing> Enumerate(BundleKey? prefix, BundleLimits limits) => inner.Enumerate(prefix, limits);

        public BundleObject Read(BundleKey key, BundleObjectVersion? expected = null) => inner.Read(key, expected);

        public bool Exists(BundleKey key) => inner.Exists(key);

        public BundleObjectVersion Write(BundleKey key, ReadOnlyMemory<byte> content, BundleWriteCondition condition)
        {
            inner.Write(key, content, condition);
            throw new BundleWriteOutcomeUnknownException(key, new IOException("simulated: the commit's outcome could not be read back"));
        }

        public void Delete(BundleKey key, BundleWriteCondition condition) => inner.Delete(key, condition);
    }

    private static OkfBundleTools ToolsOver(TempDir tmp)
    {
        tmp.Write("metrics/dau.md", "---\n" + Fm + "---\n\nOLD BODY\n");
        return new OkfBundleTools(tmp.Path, orchestrator: null, new CommitThenUnknown(new FileSystemBundleProvider(tmp.Path)));
    }

    [Fact]
    public void WriteConcept_with_an_unknown_outcome_drops_the_cache()
    {
        using var tmp = new TempDir();
        var tools = ToolsOver(tmp);
        Assert.Contains("OLD BODY", tools.ReadConcept("metrics/dau"), StringComparison.Ordinal);

        var result = tools.WriteConcept("metrics/dau", Fm, "NEW BODY\n");

        Assert.StartsWith("Error: ", result, StringComparison.Ordinal);
        Assert.Contains("unknown outcome", result, StringComparison.Ordinal);
        Assert.Contains("NEW BODY", tools.ReadConcept("metrics/dau"), StringComparison.Ordinal);
    }

    [Fact]
    public void Verify_with_an_unknown_outcome_drops_the_cache()
    {
        using var tmp = new TempDir();
        var tools = ToolsOver(tmp);
        Assert.DoesNotContain("human:ada", tools.ReadConcept("metrics/dau"), StringComparison.Ordinal);

        var result = tools.Verify("metrics/dau", "human:ada");

        Assert.Contains("unknown outcome", result, StringComparison.Ordinal);
        Assert.Contains("human:ada", tools.ReadConcept("metrics/dau"), StringComparison.Ordinal);
    }

    [Fact]
    public void AppendToConceptAtomic_with_an_unknown_outcome_drops_the_cache()
    {
        using var tmp = new TempDir();
        var tools = ToolsOver(tmp);
        Assert.DoesNotContain("APPENDED", tools.ReadConcept("metrics/dau"), StringComparison.Ordinal);

        var result = tools.AppendToConceptAtomic("metrics/dau", Fm, body => (body ?? string.Empty) + "APPENDED\n");

        Assert.StartsWith("Error: ", result, StringComparison.Ordinal);
        Assert.Contains("APPENDED", tools.ReadConcept("metrics/dau"), StringComparison.Ordinal);
    }

    [Fact]
    public void AppendLog_with_an_unknown_outcome_reports_it_and_leaves_the_cache_consistent()
    {
        using var tmp = new TempDir();
        var tools = ToolsOver(tmp);
        Assert.Contains("OLD BODY", tools.ReadConcept("metrics/dau"), StringComparison.Ordinal);

        var result = tools.AppendLog("Update", "an entry");

        Assert.StartsWith("Error: ", result, StringComparison.Ordinal);
        Assert.Contains("unknown outcome", result, StringComparison.Ordinal);
        Assert.Contains("an entry", File.ReadAllText(Path.Combine(tmp.Path, "log.md")), StringComparison.Ordinal);
        Assert.Contains("OLD BODY", tools.ReadConcept("metrics/dau"), StringComparison.Ordinal);
    }
}
