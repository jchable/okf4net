// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net.Attestation;
using OKF4net.Providers;

namespace OKF4net.Tests.Providers;

/// <summary>
/// #152 step 4: the other asynchronous readers of an image — <c>ValidateAsync</c> and
/// <c>CaptureAsync</c> — give the verdicts of their synchronous spellings, and ask the provider
/// asynchronously.
/// </summary>
public class AsyncReadersTests
{
    private const string Fm = "type: Note\ntitle: Probe\ndescription: A probe concept.";

    [Fact]
    public async Task ValidateAsync_gives_the_same_findings_as_Validate_including_the_unsafe_ones()
    {
        using var tmp = new TempDir();
        tmp.Write("index.md", "---\nokf_version: \"0.2\"\n---\n# Root\n\n- [Probe](probe.md) - a probe\n");
        tmp.Write("log.md", "# Log\n\n## 2026-01-01\n\n- note\n");
        tmp.Write("probe.md", $"---\n{Fm}\nresource: refs/absent.sql\n---\n\nbody\n");
        tmp.Write("probe2.md", $"---\n{Fm}\nresource: ../../../escape.sql\n---\n\nbody\n");
        tmp.Write("refs/present.sql", "select 1;");
        var bundle = Bundle.Load(tmp.Path);

        var sync = BundleValidator.Validate(bundle);
        var async = await BundleValidator.ValidateAsync(bundle);

        Assert.Equal(sync.Diagnostics.Select(d => (d.Severity, d.Code, d.Message)), async.Diagnostics.Select(d => (d.Severity, d.Code, d.Message)));
        Assert.Contains(async.Diagnostics, d => d.Code == DiagnosticCode.FrontmatterPathMissing);
        Assert.Contains(async.Diagnostics, d => d.Code == DiagnosticCode.FrontmatterPathUnsafe);
    }

    [Fact]
    public async Task ValidateAsync_over_a_provider_that_completes_later_is_pending_not_blocked()
    {
        var store = new InMemoryBundleProvider()
            .Add("index.md", "---\nokf_version: \"0.2\"\n---\n# Root\n")
            .Add("probe.md", $"---\n{Fm}\nresource: refs/x.sql\n---\n\nbody\n");
        var gated = new GatedProvider(store);
        gated.Gate.SetResult();
        var bundle = await Bundle.LoadAsync(gated);

        // The image is loaded; the provider now answers later: validation asks it (the existence
        // of the resource, index.md) and waits asynchronously instead of blocking.
        gated.Close();
        var pending = BundleValidator.ValidateAsync(bundle);
        Assert.False(pending.IsCompleted);
        gated.Gate.SetResult();
        var report = await pending;
        Assert.Contains(report.Diagnostics, d => d.Code == DiagnosticCode.FrontmatterPathMissing);

        // The synchronous spelling refuses to wait: it throws, naming the async overload.
        gated.Close();
        Assert.Throws<InvalidOperationException>(() => BundleValidator.Validate(bundle));
    }

    [Fact]
    public async Task CaptureAsync_reads_only_pinned_versions_like_Capture()
    {
        using var tmp = new TempDir();
        tmp.Write("references/revenue.sql", "SELECT 1\n");
        tmp.Write("att.py", "def attest(**_):\n    return {}\n");
        tmp.Write("c/rev.md", "---\ntype: Attested Computation\nruntime: bigquery\ncomputation: references/revenue.sql\nattester: { resource: att.py }\n---\n");
        var bundle = Bundle.Load(tmp.Path);
        var concept = bundle.Get(ConceptId.Parse("c/rev"))!;
        var observed = bundle.ExecutableResources[concept.Id].Computation.Version;

        var pinned = await AttestationOrchestrator.CaptureAsync(bundle, concept, observed);
        Assert.NotNull(pinned.Capture);
        Assert.Equal("SELECT 1\n", pinned.Capture!.Computation.InlineCode);
        Assert.Equal("def attest(**_):\n    return {}\n", pinned.Capture.AttesterSourceText);

        File.WriteAllText(Path.Combine(tmp.Path, "references", "revenue.sql"), "SELECT 2\n");

        var refused = await AttestationOrchestrator.CaptureAsync(bundle, concept, observed);
        Assert.Null(refused.Capture);
        Assert.True(refused.ImageOutdated);
        var unpinned = await AttestationOrchestrator.CaptureAsync(bundle, concept);
        Assert.Null(unpinned.Capture);
        Assert.Equal("computation file 'references/revenue.sql' changed since the bundle was loaded", Assert.Single(unpinned.Failure!.Reasons));
    }

    [Fact]
    public async Task CaptureAsync_can_be_cancelled_while_it_reads()
    {
        var store = new InMemoryBundleProvider()
            .Add("references/revenue.sql", "SELECT 1\n")
            .Add("att.py", "def attest(**_):\n    return {}\n")
            .Add("c/rev.md", "---\ntype: Attested Computation\nruntime: bigquery\ncomputation: references/revenue.sql\nattester: { resource: att.py }\n---\n");
        var gated = new GatedProvider(store);
        gated.Gate.SetResult();
        var bundle = await Bundle.LoadAsync(gated);
        var concept = bundle.Get(ConceptId.Parse("c/rev"))!;
        gated.Close();
        using var cts = new CancellationTokenSource();

        var capture = AttestationOrchestrator.CaptureAsync(bundle, concept, null, cts.Token).AsTask();
        Assert.False(capture.IsCompleted);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => capture);
    }
}
