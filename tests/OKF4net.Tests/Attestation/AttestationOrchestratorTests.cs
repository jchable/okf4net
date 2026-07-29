// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Collections.Generic;
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
        var orch = new AttestationOrchestrator(reg, clock: new FixedClock(new(2026, 1, 1)));
        var outcome = await orch.RunAsync(bundle, id, new Dictionary<string, object?> { ["year"] = 2026 });
        Assert.True(outcome.Displayable);
        Assert.True(outcome.Verdict!.Value.Passed);
        Assert.True(outcome.ReceiptShapeOk);
        Assert.Null(outcome.Error);
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
        var orch = new AttestationOrchestrator(reg, clock: new FixedClock(new(2026, 1, 1)));
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
        var orch = new AttestationOrchestrator(reg, clock: new FixedClock(new(2026, 1, 1)));
        var outcome = await orch.RunAsync(Bundle.Load(tmp.Path), ConceptId.Parse("c/rev"),
            new Dictionary<string, object?>(), policy: StalePolicy.Strict);
        Assert.Equal(StaleState.Fresh, outcome.Stale);
        Assert.True(outcome.Displayable);
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
}
