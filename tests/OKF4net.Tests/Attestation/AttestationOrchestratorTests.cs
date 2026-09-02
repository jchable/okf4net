// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using OKF4net;
using OKF4net.Attestation;
using Xunit;

namespace OKF4net.Tests.Attestation;

/// <summary>
/// <see cref="AttestationOrchestrator"/>: the §10.5 load → bind → execute →
/// validate-receipt → attest → gate sequence, errors-as-data throughout —
/// every expected failure surfaces as a non-displayable <see cref="AttestationOutcome"/>
/// with <see cref="AttestationOutcome.Reasons"/>, and binder/executor/attester
/// exceptions are caught rather than propagated. Exercised against
/// <see cref="FakeRuntime"/> (shared with <c>AttestationValuesTests</c>), never
/// touching <c>tests/fixtures</c>.
/// </summary>
public class AttestationOrchestratorTests
{
    private static (Bundle, ConceptId) InlineComputation(TempDir tmp)
    {
        tmp.Write("c/rev.md",
            "---\ntype: Attested Computation\nruntime: bigquery\n" +
            "parameters:\n  - { name: year, type: integer, required: true }\n" +
            "executor: { resource: references/run.md, receipt: [job_id, result] }\n" +
            "attester: { resource: references/att.py }\n---\n# Computation\n\n```sql\nSELECT @year\n```\n");
        return (Bundle.Load(tmp.Path), ConceptId.Parse("c/rev"));
    }

    [Fact]
    public async Task Happy_path_is_displayable()
    {
        using var tmp = new TempDir();
        var (bundle, id) = InlineComputation(tmp);
        var reg = new AttestationRuntimeRegistry(new Dictionary<string, IAttestationRuntime>
        {
            ["bigquery"] = FakeRuntime.Passing(receipt: new Receipt(new Dictionary<string, object?> { ["job_id"] = "j1", ["result"] = 42 })),
        });
        var orch = new AttestationOrchestrator(reg, clock: new FixedClock(new DateOnly(2026, 1, 1)));
        var outcome = await orch.RunAsync(bundle, id, new Dictionary<string, object?> { ["year"] = 2026 });
        Assert.True(outcome.Displayable);
        Assert.True(outcome.Verdict!.Value.Passed);
        Assert.True(outcome.ReceiptShapeOk);
        Assert.Null(outcome.Error);
    }

    /// <summary>
    /// Cancellation is control flow, not data. Every stage's catch was a bare
    /// `catch (Exception)`, so an OperationCanceledException raised by a
    /// host-plugged stage was caught with everything else and converted into a
    /// business outcome — `RunAsync(ct)` with a cancelled token returned a
    /// normal-looking result, and a caller could not tell "the executor failed"
    /// from "I asked it to stop".
    ///
    /// Errors-as-data is the contract for FAILURES and stays; an OCE is not one.
    /// </summary>
    [Theory]
    [InlineData("binder")]
    [InlineData("executor")]
    [InlineData("attester")]
    public async Task A_cancelled_stage_propagates_rather_than_becoming_an_outcome(string stage)
    {
        using var tmp = new TempDir();
        var (bundle, id) = InlineComputation(tmp);
        using var cts = new CancellationTokenSource();

        // Each stage observes the token and honours it, the way a real
        // implementation awaiting I/O would.
        var runtime = new FakeRuntime();
        switch (stage)
        {
            case "binder":
                runtime.BindFunc = (_, _, _, ct) => { cts.Cancel(); ct.ThrowIfCancellationRequested(); throw new InvalidOperationException("unreachable"); };
                break;
            case "executor":
                runtime.ExecuteFunc = (_, _, ct) => { cts.Cancel(); ct.ThrowIfCancellationRequested(); throw new InvalidOperationException("unreachable"); };
                break;
            default:
                // Step 8 runs only when the receipt shape is trustworthy, so the
                // executor has to return the two fields the concept declares --
                // FakeRuntime's default empty Receipt would skip attestation
                // entirely and the stage under test would never be reached.
                runtime.ExecuteFunc = (_, _, _) =>
                    ValueTask.FromResult(new Receipt(new Dictionary<string, object?> { ["job_id"] = "j1", ["result"] = 42 }));
                runtime.AttestFunc = (_, ct) => { cts.Cancel(); ct.ThrowIfCancellationRequested(); throw new InvalidOperationException("unreachable"); };
                break;
        }

        var reg = new AttestationRuntimeRegistry(new Dictionary<string, IAttestationRuntime> { ["bigquery"] = runtime });
        var orch = new AttestationOrchestrator(reg, clock: new FixedClock(new DateOnly(2026, 1, 1)));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await orch.RunAsync(bundle, id, new Dictionary<string, object?> { ["year"] = 2026 }, cancellationToken: cts.Token));
    }

    [Fact]
    public async Task Receipt_missing_declared_field_is_not_displayable()
    {
        using var tmp = new TempDir();
        var (bundle, id) = InlineComputation(tmp);
        var reg = new AttestationRuntimeRegistry(new Dictionary<string, IAttestationRuntime>
        {
            ["bigquery"] = FakeRuntime.Passing(receipt: new Receipt(new Dictionary<string, object?> { ["job_id"] = "j1" })), // 'result' missing
        });
        var outcome = await new AttestationOrchestrator(reg).RunAsync(bundle, id, new Dictionary<string, object?> { ["year"] = 2026 });
        Assert.False(outcome.ReceiptShapeOk);
        Assert.False(outcome.Displayable);
        Assert.Null(outcome.Verdict); // not attested: shape check happens before attest
        Assert.Contains(outcome.Reasons, r => r.Contains("result"));
    }

    [Fact]
    public async Task Missing_required_parameter_fails_before_binding()
    {
        using var tmp = new TempDir();
        var (bundle, id) = InlineComputation(tmp);
        var reg = new AttestationRuntimeRegistry(new Dictionary<string, IAttestationRuntime> { ["bigquery"] = FakeRuntime.Passing() });
        var outcome = await new AttestationOrchestrator(reg).RunAsync(bundle, id, new Dictionary<string, object?>());
        Assert.False(outcome.Displayable);
        Assert.Contains(outcome.Reasons, r => r.Contains("year"));
        Assert.Null(outcome.Receipt); // never reached bind/execute
    }

    [Fact]
    public async Task Unregistered_runtime_reports_reason()
    {
        using var tmp = new TempDir();
        var (bundle, id) = InlineComputation(tmp);
        var outcome = await new AttestationOrchestrator(new AttestationRuntimeRegistry(new Dictionary<string, IAttestationRuntime>()))
            .RunAsync(bundle, id, new Dictionary<string, object?> { ["year"] = 2026 });
        Assert.False(outcome.Displayable);
        Assert.Contains(outcome.Reasons, r => r.Contains("runtime"));
    }

    [Fact]
    public async Task Stale_concept_gated_under_strict_policy()
    {
        using var tmp = new TempDir();
        tmp.Write("c/rev.md",
            "---\ntype: Attested Computation\nruntime: bigquery\nstale_after: 2025-01-01\n---\n# Computation\n\n```\nX\n```\n");
        var reg = new AttestationRuntimeRegistry(new Dictionary<string, IAttestationRuntime> { ["bigquery"] = FakeRuntime.Passing() });
        var orch = new AttestationOrchestrator(reg, clock: new FixedClock(new DateOnly(2026, 1, 1)));
        var outcome = await orch.RunAsync(bundle: Bundle.Load(tmp.Path), conceptId: ConceptId.Parse("c/rev"),
            parameterValues: new Dictionary<string, object?>(), policy: StalePolicy.Strict);
        Assert.Equal(StaleState.Stale, outcome.Stale);
        Assert.False(outcome.Displayable);
        Assert.True(outcome.Verdict!.Value.Passed); // attested fine; only the staleness gate blocks display
        Assert.Contains(outcome.Reasons, r => r.Contains("stale"));
    }

    [Fact]
    public async Task Fresh_concept_is_not_gated_by_stale_after()
    {
        using var tmp = new TempDir();
        tmp.Write("c/rev.md",
            "---\ntype: Attested Computation\nruntime: bigquery\nstale_after: 2099-01-01\n---\n# Computation\n\n```\nX\n```\n");
        var reg = new AttestationRuntimeRegistry(new Dictionary<string, IAttestationRuntime> { ["bigquery"] = FakeRuntime.Passing() });
        var orch = new AttestationOrchestrator(reg, clock: new FixedClock(new DateOnly(2026, 1, 1)));
        var outcome = await orch.RunAsync(Bundle.Load(tmp.Path), ConceptId.Parse("c/rev"),
            new Dictionary<string, object?>(), policy: StalePolicy.Strict);
        Assert.Equal(StaleState.Fresh, outcome.Stale);
        Assert.True(outcome.Displayable);
    }

    /// <summary>
    /// Regression test for the §5 bug this branch fixes, at §10.6's gate.
    /// <see cref="Stale_concept_gated_under_strict_policy"/> above uses the
    /// legacy date-only <c>2025-01-01</c>, which the pre-fix <c>Lifecycle</c>
    /// parsed too (<c>DateOnly.TryParseExact("yyyy-MM-dd")</c>) — so it would
    /// stay green even if the gate regressed to that parser. A §5-conformant
    /// instant is what it could not read: <c>StaleAfter</c> came back null,
    /// <c>ComputeStale</c> returned <see cref="StaleState.Unknown"/>, and
    /// <c>StalePolicy.Strict</c> admits a null <c>StaleAfter</c> — so a concept
    /// six months past its expiry was attested and displayed. This test fails
    /// on the pre-fix parser and passes on the shipped one.
    /// </summary>
    [Fact]
    public async Task Stale_concept_with_a_conformant_instant_stale_after_is_gated_under_strict_policy()
    {
        using var tmp = new TempDir();
        tmp.Write("c/rev.md",
            "---\ntype: Attested Computation\nruntime: bigquery\nstale_after: 2025-06-30T14:00:00Z\n---\n# Computation\n\n```\nX\n```\n");
        var reg = new AttestationRuntimeRegistry(new Dictionary<string, IAttestationRuntime> { ["bigquery"] = FakeRuntime.Passing() });
        var orch = new AttestationOrchestrator(reg, clock: new FixedClock(new DateOnly(2026, 1, 1)));
        var outcome = await orch.RunAsync(bundle: Bundle.Load(tmp.Path), conceptId: ConceptId.Parse("c/rev"),
            parameterValues: new Dictionary<string, object?>(), policy: StalePolicy.Strict);
        Assert.Equal(StaleState.Stale, outcome.Stale);
        Assert.False(outcome.Displayable);
        Assert.True(outcome.Verdict!.Value.Passed); // attested fine; only the staleness gate blocks display
        Assert.Contains(outcome.Reasons, r => r.Contains("stale"));
    }

    [Fact]
    public async Task Executor_exception_is_captured_not_thrown()
    {
        using var tmp = new TempDir();
        var (bundle, id) = InlineComputation(tmp);
        var reg = new AttestationRuntimeRegistry(new Dictionary<string, IAttestationRuntime> { ["bigquery"] = FakeRuntime.ThrowingExecutor() });
        var outcome = await new AttestationOrchestrator(reg).RunAsync(bundle, id, new Dictionary<string, object?> { ["year"] = 2026 });
        Assert.False(outcome.Displayable);
        Assert.NotNull(outcome.Error);
        Assert.Null(outcome.Receipt);
    }

    [Fact]
    public async Task Attest_negative_verdict_not_displayable()
    {
        using var tmp = new TempDir();
        var (bundle, id) = InlineComputation(tmp);
        var reg = new AttestationRuntimeRegistry(new Dictionary<string, IAttestationRuntime>
        {
            ["bigquery"] = FakeRuntime.Passing(
                receipt: new Receipt(new Dictionary<string, object?> { ["job_id"] = "j1", ["result"] = 42 }),
                verdict: new AttestationVerdict(false, "sql does not match sanctioned computation")),
        });
        var outcome = await new AttestationOrchestrator(reg).RunAsync(bundle, id, new Dictionary<string, object?> { ["year"] = 2026 });
        Assert.True(outcome.ReceiptShapeOk);
        Assert.False(outcome.Verdict!.Value.Passed);
        Assert.False(outcome.Displayable);
        Assert.Contains(outcome.Reasons, r => r.Contains("sanctioned computation"));
    }

    [Fact]
    public async Task File_based_computation_resolved_and_read()
    {
        using var tmp = new TempDir();
        tmp.Write("c/rev.md",
            "---\ntype: Attested Computation\nruntime: bigquery\ncomputation: references/revenue.sql\n" +
            "executor: { resource: references/run.md, receipt: [job_id] }\n---\n");
        tmp.Write("c/references/revenue.sql", "SELECT revenue FROM t;\n");
        string? capturedText = null;
        var runtime = FakeRuntime.Passing(receipt: new Receipt(new Dictionary<string, object?> { ["job_id"] = "j1" }));
        runtime.BindFunc = (contract, computation, values, ct) =>
        {
            capturedText = computation.InlineCode;
            return ValueTask.FromResult(new BoundComputation(contract.Runtime ?? "bigquery", computation.InlineCode, null, values));
        };
        var reg = new AttestationRuntimeRegistry(new Dictionary<string, IAttestationRuntime> { ["bigquery"] = runtime });
        var outcome = await new AttestationOrchestrator(reg).RunAsync(Bundle.Load(tmp.Path), ConceptId.Parse("c/rev"), new Dictionary<string, object?>());
        Assert.True(outcome.Displayable);
        Assert.Equal("SELECT revenue FROM t;\n", capturedText);
    }

    [Fact]
    public async Task Unresolvable_computation_file_fails_before_binding()
    {
        using var tmp = new TempDir();
        tmp.Write("c/rev.md",
            "---\ntype: Attested Computation\nruntime: bigquery\ncomputation: references/missing.sql\n---\n");
        var reg = new AttestationRuntimeRegistry(new Dictionary<string, IAttestationRuntime> { ["bigquery"] = FakeRuntime.Passing() });
        var outcome = await new AttestationOrchestrator(reg).RunAsync(Bundle.Load(tmp.Path), ConceptId.Parse("c/rev"), new Dictionary<string, object?>());
        Assert.False(outcome.Displayable);
        Assert.Contains(outcome.Reasons, r => r.Contains("missing.sql"));
    }

    [Fact]
    public async Task Unreadable_computation_file_is_captured_not_thrown()
    {
        using var tmp = new TempDir();
        tmp.Write("c/rev.md",
            "---\ntype: Attested Computation\nruntime: bigquery\ncomputation: references/revenue.sql\n---\n");
        var sqlPath = System.IO.Path.Combine(tmp.Path, "c", "references", "revenue.sql");
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(sqlPath)!);
        // Lone UTF-8 continuation bytes with no leading byte: invalid UTF-8 that
        // isn't also a recognized BOM prefix (unlike e.g. 0xFF 0xFE), so it reliably
        // trips OkfEncodings.Strict's decoder instead of being silently reinterpreted
        // as a different encoding by File.ReadAllText's BOM auto-detection.
        File.WriteAllBytes(sqlPath, [0x80, 0x81, 0x82]);
        var reg = new AttestationRuntimeRegistry(new Dictionary<string, IAttestationRuntime> { ["bigquery"] = FakeRuntime.Passing() });
        var outcome = await new AttestationOrchestrator(reg).RunAsync(Bundle.Load(tmp.Path), ConceptId.Parse("c/rev"), new Dictionary<string, object?>());
        Assert.False(outcome.Displayable);
        Assert.Contains(outcome.Reasons, r => r.Contains("revenue.sql"));
    }

    /// <summary>
    /// Regression test for the BOM-sniff hole in <see cref="Bundle.ReadResourceText"/>:
    /// the old implementation was <c>File.ReadAllText(absolutePath, OkfEncodings.Strict)</c>,
    /// and <see cref="File.ReadAllText(string, System.Text.Encoding)"/> hardcodes
    /// <c>detectEncodingFromByteOrderMarks: true</c> regardless of the encoding
    /// passed in -- so a UTF-16-BOM-prefixed file was silently reinterpreted as
    /// UTF-16 instead of tripping the strict UTF-8 decoder, the exact hole
    /// <see cref="OkfEncodings.Strict"/> exists to prevent. 0xFF 0xFE is a UTF-16 LE
    /// BOM; the trailing 0x00 0xD8 is an unpaired low/high surrogate byte pair that
    /// decodes without throwing under .NET's default (replacement-character) UTF-16
    /// handling, so under the old code this file was read as a garbage-but-non-throwing
    /// string and the run proceeded to a normal, displayable outcome. Under strict
    /// UTF-8 (no BOM sniffing), 0xFF and 0xFE are never valid UTF-8 lead bytes, so the
    /// fixed <see cref="Bundle.ReadResourceText"/> must trip the decoder, which the
    /// orchestrator's existing guarded read (<see cref="AttestationOrchestrator.RunAsync"/>'s
    /// file-computation step) catches as a non-displayable outcome -- never an
    /// unhandled exception.
    /// </summary>
    [Fact]
    public async Task Utf16_bom_prefixed_computation_file_trips_strict_decoder()
    {
        using var tmp = new TempDir();
        tmp.Write("c/rev.md",
            "---\ntype: Attested Computation\nruntime: bigquery\ncomputation: references/revenue.sql\n---\n");
        var sqlPath = System.IO.Path.Combine(tmp.Path, "c", "references", "revenue.sql");
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(sqlPath)!);
        File.WriteAllBytes(sqlPath, new byte[] { 0xFF, 0xFE, 0x00, 0xD8 });
        var reg = new AttestationRuntimeRegistry(new Dictionary<string, IAttestationRuntime> { ["bigquery"] = FakeRuntime.Passing() });
        var outcome = await new AttestationOrchestrator(reg).RunAsync(Bundle.Load(tmp.Path), ConceptId.Parse("c/rev"), new Dictionary<string, object?>());
        Assert.False(outcome.Displayable);
        Assert.Contains(outcome.Reasons, r => r.Contains("revenue.sql"));
    }

    /// <summary>
    /// Step 2's <c>default</c> arm (<see cref="AttestationOrchestrator.RunAsync"/>):
    /// a concept can decline into neither switch case above -- no
    /// <c>computation:</c> frontmatter path AND no inline <c># Computation</c>
    /// fence in the body -- and must fail before the runtime is even
    /// resolved, with the orchestrator's dedicated "has no computation"
    /// reason rather than some other generic message.
    /// </summary>
    [Fact]
    public async Task Neither_inline_nor_file_computation_is_not_displayable()
    {
        using var tmp = new TempDir();
        tmp.Write("c/rev.md",
            "---\ntype: Attested Computation\nruntime: bigquery\n---\n" +
            "Just prose -- no `# Computation` fence and no `computation:` path.\n");
        var reg = new AttestationRuntimeRegistry(new Dictionary<string, IAttestationRuntime> { ["bigquery"] = FakeRuntime.Passing() });
        var outcome = await new AttestationOrchestrator(reg).RunAsync(Bundle.Load(tmp.Path), ConceptId.Parse("c/rev"), new Dictionary<string, object?>());
        Assert.False(outcome.Displayable);
        Assert.Contains(outcome.Reasons, r => r.Contains("has no computation"));
    }

    [Fact]
    public async Task Not_found_concept_is_not_displayable()
    {
        using var tmp = new TempDir();
        var (bundle, _) = InlineComputation(tmp);
        var reg = new AttestationRuntimeRegistry(new Dictionary<string, IAttestationRuntime> { ["bigquery"] = FakeRuntime.Passing() });
        var outcome = await new AttestationOrchestrator(reg).RunAsync(bundle, ConceptId.Parse("c/nope"), new Dictionary<string, object?>());
        Assert.False(outcome.Displayable);
        Assert.NotEmpty(outcome.Reasons);
    }

    [Fact]
    public async Task Non_attested_computation_concept_is_not_displayable()
    {
        using var tmp = new TempDir();
        tmp.Write("c/other.md", "---\ntype: Metric\n---\n# Body\n");
        var reg = new AttestationRuntimeRegistry(new Dictionary<string, IAttestationRuntime>());
        var outcome = await new AttestationOrchestrator(reg).RunAsync(Bundle.Load(tmp.Path), ConceptId.Parse("c/other"), new Dictionary<string, object?>());
        Assert.False(outcome.Displayable);
        Assert.NotEmpty(outcome.Reasons);
    }

    /// <summary>
    /// P2b regression: <see cref="AttestationOrchestrator.RunAsync"/> is a
    /// direct public API (not just reached through the agent wrapper, which
    /// already normalizes a null parameter dictionary before calling in), so
    /// it must uphold its own errors-as-data promise for a caller who passes
    /// <c>null!</c> directly -- never an unhandled <see cref="NullReferenceException"/>.
    /// Before the fix, the required-parameter gate dereferenced
    /// <c>parameterValues</c> (<c>.ContainsKey</c>) before any guarded host
    /// call, so a null dictionary threw immediately. <see cref="InlineComputation"/>
    /// declares a required "year" parameter, so a normalized empty dictionary
    /// must still surface the normal missing-required-parameter outcome.
    /// </summary>
    [Fact]
    public async Task Null_parameter_dictionary_does_not_throw_and_reports_missing_required_parameter()
    {
        using var tmp = new TempDir();
        var (bundle, id) = InlineComputation(tmp);
        var reg = new AttestationRuntimeRegistry(new Dictionary<string, IAttestationRuntime> { ["bigquery"] = FakeRuntime.Passing() });
        var outcome = await new AttestationOrchestrator(reg).RunAsync(bundle, id, null!);
        Assert.False(outcome.Displayable);
        Assert.Contains(outcome.Reasons, r => r.Contains("year"));
        Assert.Null(outcome.Receipt); // never reached bind/execute
    }
}
