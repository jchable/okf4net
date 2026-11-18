// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Text;
using OKF4net.Agents;
using OKF4net.Attestation;
using OKF4net.Internal;
using OKF4net.Providers;

namespace OKF4net.Tests.Providers;

/// <summary>
/// #247: a failure of the PROVIDER (its storage or transport down, a timeout, a credential, a
/// bound) is not a finding about the object being read. <see cref="Bundle.ReadResourceTextAsync"/>
/// keeps the two apart — a local problem keeps its I/O, access or decoding exception, a global
/// one is a <see cref="BundleLoadException"/> naming the exception type only — and every
/// consumer follows the error-priority rule of #139 (2026-10-12: global failures abort,
/// per-resource states are local): the validator fails instead of reporting
/// <c>UnparseableIndex</c>/<c>UnparseableLog</c>, the load fails instead of surveying a resource
/// as <c>Unreadable</c>, a capture fails instead of refusing, and a tool reports an error instead
/// of a skip note. No provider text reaches a message.
/// </summary>
public class GlobalProviderFailureTests
{
    private const string Secret = "secret-token=123 at https://store.example/bucket";

    private const string Attester = "def attest(**_):\n    return {}\n";

    private const string Computation =
        "---\ntype: Attested Computation\ntitle: Rev\ndescription: Revenue.\nruntime: bigquery\nexecutor: { receipt: [job_id] }\n"
        + "computation: q.sql\nattester: { resource: att.py }\n---\n";

    /// <summary>A host provider's own failure — a network, a credential — with storage internals in its message.</summary>
    private sealed class HostNetworkException() : BundleProviderException(Secret);

    /// <summary>
    /// A raw <see cref="IOException"/> as the filesystem gives one when its storage is
    /// unreachable: <c>ERROR_BAD_NETPATH</c> on Windows, <c>ETIMEDOUT</c> elsewhere (110 on Linux,
    /// 60 on Darwin and the BSDs).
    /// </summary>
    private static IOException StorageUnreachable() => new(
        Secret,
        OperatingSystem.IsWindows() ? unchecked((int)0x80070035) : OperatingSystem.IsLinux() ? 110 : 60);

    private static InMemoryBundleProvider Storage() => new InMemoryBundleProvider()
        .Add("index.md", "# Bundle\n\n* [Rev](rev.md) - Revenue.\n")
        .Add("rev.md", Computation)
        .Add("q.sql", "SELECT 1\n")
        .Add("att.py", Attester)
        .Add("log.md", "# Log\n\n## 2026-07-22\n\n* **Creation**: Bootstrapped.\n");

    private static void AssertNamesTypeOnly(BundleLoadException e, string key, Exception cause)
    {
        Assert.Contains($"'{key}'", e.Message, StringComparison.Ordinal);
        Assert.Contains(cause.GetType().Name, e.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", e.Message, StringComparison.Ordinal);
        Assert.Same(cause, e.InnerException);
    }

    // ---- Bundle.ReadResourceTextAsync -------------------------------------------------

    [Fact]
    public async Task A_provider_failure_reading_a_resource_is_a_BundleLoadException_not_an_IOException()
    {
        var storage = Storage();
        var bundle = Bundle.Load(storage);
        var cause = new HostNetworkException();
        storage.FailReadOf("q.sql", cause);

        var e = await Assert.ThrowsAsync<BundleLoadException>(async () => await bundle.ReadResourceTextAsync(BundleKey.Parse("q.sql")));
        AssertNamesTypeOnly(e, "q.sql", cause);

        // The synchronous spelling is the same implementation.
        var sync = Assert.Throws<BundleLoadException>(() => bundle.ReadResourceText(BundleKey.Parse("q.sql")));
        AssertNamesTypeOnly(sync, "q.sql", cause);
    }

    [Fact]
    public async Task A_bound_exceeded_by_the_provider_is_global_and_named_by_its_category()
    {
        var storage = Storage();
        var bundle = Bundle.Load(storage);
        storage.FailReadOf("q.sql", new BundleLimitExceededException(BundleBound.Entries, 7));

        var e = await Assert.ThrowsAsync<BundleLoadException>(async () => await bundle.ReadResourceTextAsync(BundleKey.Parse("q.sql")));
        Assert.Contains("entries", e.Message, StringComparison.Ordinal);
        Assert.IsType<BundleLimitExceededException>(e.InnerException);
    }

    [Fact]
    public async Task A_raw_IOException_saying_the_storage_is_unreachable_is_global()
    {
        var storage = Storage();
        var bundle = Bundle.Load(storage);
        var cause = StorageUnreachable();
        storage.FailReadOf("q.sql", cause);

        var e = await Assert.ThrowsAsync<BundleLoadException>(async () => await bundle.ReadResourceTextAsync(BundleKey.Parse("q.sql")));
        AssertNamesTypeOnly(e, "q.sql", cause);
    }

    [Fact]
    public async Task A_problem_of_the_object_keeps_its_local_exception()
    {
        var storage = Storage().Add("bad.sql", [0x53, 0xFF, 0xFE]);
        var bundle = Bundle.Load(storage);
        var key = BundleKey.Parse("q.sql");

        storage.FailReadOf("q.sql", new BundleObjectNotFoundException(key));
        await Assert.ThrowsAsync<FileNotFoundException>(async () => await bundle.ReadResourceTextAsync(key));

        storage.FailReadOf("q.sql", new BundleObjectTooLargeException(key, 10, 5));
        await Assert.ThrowsAsync<BundleObjectTooLargeException>(async () => await bundle.ReadResourceTextAsync(key));

        storage.FailReadOf("q.sql", new BundleAccessRefusedException(key, Secret));
        var refused = await Assert.ThrowsAsync<UnauthorizedAccessException>(async () => await bundle.ReadResourceTextAsync(key));
        Assert.DoesNotContain("secret", refused.Message, StringComparison.Ordinal);

        storage.FailReadOf("q.sql", new UnauthorizedAccessException(Secret));
        var denied = await Assert.ThrowsAsync<UnauthorizedAccessException>(async () => await bundle.ReadResourceTextAsync(key));
        Assert.DoesNotContain("secret", denied.Message, StringComparison.Ordinal);

        // An I/O failure nothing says is the storage's — a lock, a plain IOException — is the object's.
        storage.FailReadOf("q.sql", new IOException(Secret));
        var io = await Assert.ThrowsAsync<IOException>(async () => await bundle.ReadResourceTextAsync(key));
        Assert.DoesNotContain("secret", io.Message, StringComparison.Ordinal);

        await Assert.ThrowsAsync<DecoderFallbackException>(async () => await bundle.ReadResourceTextAsync(BundleKey.Parse("bad.sql")));
    }

    // ---- StorageFailure: which raw IOExceptions are global --------------------------

    [Fact]
    public void Only_the_codes_of_an_unreachable_storage_are_global()
    {
        Assert.True(StorageFailure.IsStorageFailure(StorageUnreachable()));

        // The object's own failures, whatever the platform.
        Assert.False(StorageFailure.IsStorageFailure(new IOException("x")));
        Assert.False(StorageFailure.IsStorageFailure(new FileNotFoundException("x")));
        Assert.False(StorageFailure.IsStorageFailure(new DirectoryNotFoundException("x")));
        Assert.False(StorageFailure.IsStorageFailure(new PathTooLongException("x")));
        Assert.False(StorageFailure.IsStorageFailure(new BundleObjectTooLargeException(BundleKey.Parse("a"), 2, 1)));

        if (OperatingSystem.IsWindows())
        {
            Assert.True(StorageFailure.IsStorageFailure(new IOException("x", unchecked((int)0x80070015)))); // ERROR_NOT_READY
            Assert.True(StorageFailure.IsStorageFailure(new IOException("x", unchecked((int)0x80070040)))); // ERROR_NETNAME_DELETED
            Assert.True(StorageFailure.IsStorageFailure(new IOException("x", unchecked((int)0x800704CF)))); // ERROR_NETWORK_UNREACHABLE
            Assert.False(StorageFailure.IsStorageFailure(new IOException("x", unchecked((int)0x80070020)))); // ERROR_SHARING_VIOLATION: the file is locked
            Assert.False(StorageFailure.IsStorageFailure(new IOException("x", unchecked((int)0x80070021)))); // ERROR_LOCK_VIOLATION
            Assert.False(StorageFailure.IsStorageFailure(new IOException("x", 0x35))); // the right code, but not a Win32 HRESULT
        }
        else
        {
            Assert.True(StorageFailure.IsStorageFailure(new IOException("x", 5))); // EIO
            Assert.True(StorageFailure.IsStorageFailure(new IOException("x", 6))); // ENXIO
            Assert.True(StorageFailure.IsStorageFailure(new IOException("x", OperatingSystem.IsLinux() ? 116 : 70))); // ESTALE
            Assert.False(StorageFailure.IsStorageFailure(new IOException("x", 11))); // EAGAIN / EDEADLK: a lock
            Assert.False(StorageFailure.IsStorageFailure(new IOException("x", 21))); // EISDIR
        }
    }

    // ---- BundleValidator: index.md and log.md -----------------------------------------

    [Theory]
    [InlineData("index.md")]
    [InlineData("log.md")]
    public async Task A_provider_failure_reading_a_reserved_file_fails_the_validation(string reserved)
    {
        var storage = Storage();
        var bundle = Bundle.Load(storage);
        var cause = new HostNetworkException();
        storage.FailReadOf(reserved, cause);

        var e = await Assert.ThrowsAsync<BundleLoadException>(async () => await BundleValidator.ValidateAsync(bundle, new FixedClock(new DateOnly(2026, 8, 1))));
        AssertNamesTypeOnly(e, reserved, cause);
        Assert.Throws<BundleLoadException>(() => BundleValidator.Validate(bundle, new FixedClock(new DateOnly(2026, 8, 1))));
    }

    [Theory]
    [InlineData("index.md")]
    [InlineData("log.md")]
    public async Task An_unreachable_filesystem_storage_fails_the_validation(string reserved)
    {
        var storage = Storage();
        var bundle = Bundle.Load(storage);
        storage.FailReadOf(reserved, StorageUnreachable());

        await Assert.ThrowsAsync<BundleLoadException>(async () => await BundleValidator.ValidateAsync(bundle, new FixedClock(new DateOnly(2026, 8, 1))));
    }

    [Fact]
    public void A_local_problem_of_a_reserved_file_is_still_a_diagnostic()
    {
        var storage = Storage();
        var bundle = Bundle.Load(storage);
        var clock = new FixedClock(new DateOnly(2026, 8, 1));

        storage.FailReadOf("index.md", new BundleObjectTooLargeException(BundleKey.Parse("index.md"), 10, 5));
        storage.Add("log.md", [0x23, 0x20, 0xFF, 0xFE, 0x0A]); // not UTF-8
        var report = BundleValidator.Validate(bundle, clock);
        Assert.Contains(report.Diagnostics, d => d.Code == DiagnosticCode.UnparseableIndex && d.Message.StartsWith("index.md could not be read", StringComparison.Ordinal));
        Assert.Contains(report.Diagnostics, d => d.Code == DiagnosticCode.UnparseableLog && d.Message.StartsWith("log.md could not be read", StringComparison.Ordinal));

        // A plain I/O failure of the object, and a vanished one, stay local too.
        storage.FailReadOf("index.md", new IOException(Secret));
        storage.FailReadOf("log.md", new BundleObjectNotFoundException(BundleKey.Parse("log.md")));
        report = BundleValidator.Validate(bundle, clock);
        var index = Assert.Single(report.Diagnostics, d => d.Code == DiagnosticCode.UnparseableIndex);
        Assert.DoesNotContain("secret", index.Message, StringComparison.Ordinal);
        Assert.Contains(report.Diagnostics, d => d.Code == DiagnosticCode.UnparseableLog);
    }

    // ---- Bundle.Load: the survey and the root index ------------------------------------

    [Fact]
    public void An_unreachable_storage_during_the_survey_fails_the_load_where_a_local_IOException_is_a_state()
    {
        var cause = StorageUnreachable();
        var e = Assert.Throws<BundleLoadException>(() => Bundle.Load(Storage().FailReadOf("att.py", cause)));
        AssertNamesTypeOnly(e, "att.py", cause);

        var local = Bundle.Load(Storage().FailReadOf("att.py", new IOException("disk")));
        Assert.Equal(ExecutableResourceState.Unreadable, local.ExecutableResources[ConceptId.Parse("rev")].Attester.State);
    }

    [Fact]
    public void An_unreachable_storage_reading_the_root_index_fails_the_load_where_a_local_IOException_declares_no_version()
    {
        var cause = StorageUnreachable();
        var e = Assert.Throws<BundleLoadException>(() => Bundle.Load(Storage().FailReadOf("index.md", cause)));
        AssertNamesTypeOnly(e, "index.md", cause);

        var local = Bundle.Load(Storage().Add("index.md", "---\nokf_version: \"0.2\"\n---\n# B\n").FailReadOf("index.md", new IOException("disk")));
        Assert.Null(local.OkfVersion);
    }

    // ---- AttestationOrchestrator.Capture -------------------------------------------------

    [Fact]
    public async Task A_provider_failure_during_a_capture_propagates_instead_of_refusing()
    {
        var storage = Storage();
        var bundle = Bundle.Load(storage);
        var concept = bundle.Get(ConceptId.Parse("rev"))!;
        Assert.NotNull(AttestationOrchestrator.Capture(bundle, concept).Capture);

        var cause = new HostNetworkException();
        storage.FailReadOf("att.py", cause);
        var e = Assert.Throws<BundleLoadException>(() => AttestationOrchestrator.Capture(bundle, concept));
        AssertNamesTypeOnly(e, "att.py", cause);

        storage.FailReadOf("att.py", new IOException("disk")); // still the object's: a refusal, the image possibly behind
        storage.FailReadOf("q.sql", StorageUnreachable());
        await Assert.ThrowsAsync<BundleLoadException>(async () => await AttestationOrchestrator.CaptureAsync(bundle, concept, null, CancellationToken.None));

        storage.FailReadOf("q.sql", new IOException("disk"));
        var refused = AttestationOrchestrator.Capture(bundle, concept);
        Assert.Null(refused.Capture);
        Assert.True(refused.ImageOutdated);
    }

    // ---- OkfBundleTools ---------------------------------------------------------------------

    [Fact]
    public async Task ChangesSince_reports_a_provider_failure_as_an_error_not_a_skipped_log()
    {
        var storage = Storage();
        using var handle = new BundleHandle(storage);
        var tools = new OkfBundleTools(handle);
        Assert.Contains("Bootstrapped.", await tools.ChangesSinceAsync("2026-07-01"), StringComparison.Ordinal);

        storage.FailReadOf("log.md", new HostNetworkException());
        var report = await tools.ChangesSinceAsync("2026-07-01");

        Assert.StartsWith("Error: provider failure reading 'log.md' (HostNetworkException)", report, StringComparison.Ordinal);
        Assert.DoesNotContain("Skipped", report, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", report, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetComputation_reports_a_provider_failure_as_an_error_naming_the_type_only()
    {
        var storage = Storage();
        using var handle = new BundleHandle(storage);
        var tools = new OkfBundleTools(handle);

        storage.FailReadOf("q.sql", StorageUnreachable());
        var shown = await tools.GetComputationAsync("rev");

        Assert.StartsWith("Error: provider failure reading 'q.sql' (IOException)", shown, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", shown, StringComparison.Ordinal);
    }
}
