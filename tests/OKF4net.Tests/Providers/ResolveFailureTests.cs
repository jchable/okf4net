// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net.Agents;
using OKF4net.Providers;

namespace OKF4net.Tests.Providers;

/// <summary>Spec of 2026-11-16 (S3) §4.7 (b).</summary>
public class ResolveFailureTests
{
    private const string Sentinel = "https://secret.example/bucket-name?X-Amz-Signature=SENTINEL";
    // A `sources` entry is read only when it is a mapping (Provenance.ParseSources): `- resource: …`, never a bare string.
    private const string WithResource = "---\ntype: Note\ntitle: T\ndescription: D.\nsources:\n  - resource: refs/q.sql\n---\n\nBody.\n";

    /// <summary>A provider whose ExistsAsync throws what the test gives it, counting the calls.</summary>
    private sealed class FailingExists(InMemoryBundleProvider inner, Exception failure) : IBundleProvider
    {
        public int ExistsCalls;
        public string Id => inner.Id;
        public string Location => inner.Location;
        public BundleProviderCapabilities Capabilities => inner.Capabilities;
        public BundleKeyConstraints NativeConstraints => inner.NativeConstraints;
        public ValueTask<BundleVersion> GetVersionAsync(BundleLimits limits, CancellationToken ct) => inner.GetVersionAsync(limits, ct);
        public IAsyncEnumerable<BundleListing> EnumerateAsync(BundleKey? prefix, BundleLimits limits, CancellationToken ct) => inner.EnumerateAsync(prefix, limits, ct);
        public ValueTask<BundleObject> ReadAsync(BundleKey key, BundleObjectVersion? expected, CancellationToken ct) => inner.ReadAsync(key, expected, ct);
        public ValueTask<bool> ExistsAsync(BundleKey key, CancellationToken ct)
        {
            Interlocked.Increment(ref ExistsCalls);
            return ValueTask.FromException<bool>(failure);
        }

        public ValueTask<BundleObjectVersion> WriteAsync(BundleKey key, ReadOnlyMemory<byte> content, BundleWriteCondition condition, CancellationToken ct) => inner.WriteAsync(key, content, condition, ct);
        public ValueTask DeleteAsync(BundleKey key, BundleWriteCondition condition, CancellationToken ct) => inner.DeleteAsync(key, condition, ct);
    }

    private sealed class SomeProviderFailure(string m) : BundleProviderException(m);

    public static TheoryData<Exception> Failures => new()
    {
        new IOException(Sentinel),
        new UnauthorizedAccessException(Sentinel),
        new SomeProviderFailure(Sentinel),
    };

    private static InMemoryBundleProvider Store() =>
        new InMemoryBundleProvider().Add("index.md", "# x\n").Add("a.md", WithResource).Add("refs/q.sql", "SELECT 1\n");

    [Fact]
    public async Task A_refusal_is_still_Unsafe()
    {
        var bundle = await Bundle.LoadAsync(Store().RefuseExistsOf("refs/q.sql"));
        var (_, status) = await bundle.ResolveResourceKeyAsync(bundle.Concepts.Single(c => c.Id.ToString() == "a"), "refs/q.sql");
        Assert.Equal(ResourceResolutionStatus.Unsafe, status);
    }

    [Theory]
    [MemberData(nameof(Failures))]
    public async Task Any_other_failure_surfaces_translated_without_the_providers_text(Exception failure)
    {
        var bundle = await Bundle.LoadAsync(new FailingExists(Store(), failure));
        var concept = bundle.Concepts.Single(c => c.Id.ToString() == "a");

        var e = await Assert.ThrowsAsync<BundleLoadException>(async () => await bundle.ResolveResourceKeyAsync(concept, "refs/q.sql"));
        Assert.DoesNotContain("SENTINEL", e.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("secret.example", e.Message, StringComparison.Ordinal);
        Assert.Contains(failure.GetType().Name, e.Message, StringComparison.Ordinal);
        Assert.Same(failure, e.InnerException);

        var provider = (FailingExists)bundle.Provider;
        var before = provider.ExistsCalls;
        await Assert.ThrowsAsync<BundleLoadException>(async () => await BundleValidator.ValidateAsync(bundle));
        Assert.True(provider.ExistsCalls > before, "the validation resolved the frontmatter resource"); // the test exercises the resolver
    }

    [Theory]
    [MemberData(nameof(Failures))]
    public async Task The_validation_tool_reports_a_translated_failure_without_the_providers_text(Exception failure)
    {
        var provider = new FailingExists(Store(), failure);
        using var handle = new BundleHandle(provider);
        var text = await new OkfBundleTools(handle).ValidateBundleAsync();
        Assert.True(provider.ExistsCalls > 0);
        Assert.Contains("provider failure resolving 'refs/q.sql'", text, StringComparison.Ordinal); // the core's own message
        Assert.DoesNotContain("SENTINEL", text, StringComparison.Ordinal);
        Assert.DoesNotContain("secret.example", text, StringComparison.Ordinal);
        Assert.DoesNotContain("escapes the bundle", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_cancellation_still_propagates_as_a_cancellation()
    {
        using var cts = new CancellationTokenSource();
        var bundle = await Bundle.LoadAsync(new FailingExists(Store(), new OperationCanceledException(cts.Token)));
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await bundle.ResolveResourceKeyAsync(bundle.Concepts.Single(c => c.Id.ToString() == "a"), "refs/q.sql", cts.Token));
    }
}
