// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net.Agents;
using OKF4net.Attestation;

namespace OKF4net.Tests.Attestation;

/// <summary>
/// #154: the host's allowlist of sanctioned-code digests. Configured, a run executes only when the computation
/// (inline or file) and the attester source are both in it; a single changed byte in either is refused before
/// any runtime stage is called, and the refused digests reach the host on the outcome, never the reasons a
/// model is shown. Not configured, nothing changes.
/// </summary>
public class CodeDigestAllowlistTests
{
    private const string Attester = "def attest(**_):\n    return {}\n";
    private const string InlineCode = "SELECT @year"; // the fence's content as the capture holds it: no final line break
    private const string FileCode = "SELECT revenue FROM t;\n";

    private static Dictionary<string, object?> Year => new() { ["year"] = 2026 };

    private static void WriteInline(TempDir tmp, string code = InlineCode, string attester = Attester)
    {
        tmp.Write("c/rev.md",
            "---\ntype: Attested Computation\nruntime: bigquery\n" +
            "parameters:\n  - { name: year, type: integer, required: true }\n" +
            $"executor: {{ receipt: [job_id] }}\nattester: {{ resource: att.py }}\n---\n# Computation\n\n```sql\n{code}\n```\n");
        tmp.Write("att.py", attester);
    }

    private static void WriteFile(TempDir tmp, string code = FileCode)
    {
        tmp.Write("c/rev.md",
            "---\ntype: Attested Computation\nruntime: bigquery\ncomputation: references/revenue.sql\n" +
            "parameters:\n  - { name: year, type: integer, required: true }\n" +
            "executor: { receipt: [job_id] }\nattester: { resource: att.py }\n---\n");
        tmp.Write("references/revenue.sql", code);
        tmp.Write("att.py", Attester);
    }

    /// <summary>A runtime that records every stage it is asked to run.</summary>
    private static (AttestationRuntimeRegistry Registry, List<string> Stages) Recording()
    {
        var stages = new List<string>();
        var runtime = FakeRuntime.Passing(receipt: new Receipt(new Dictionary<string, object?> { ["job_id"] = "j1" }));
        var bind = runtime.BindFunc;
        var execute = runtime.ExecuteFunc;
        var attest = runtime.AttestFunc;
        runtime.BindFunc = (c, s, v, ct) =>
        {
            stages.Add("bind");
            return bind(c, s, v, ct);
        };
        runtime.ExecuteFunc = (b, c, ct) =>
        {
            stages.Add("execute");
            return execute(b, c, ct);
        };
        runtime.AttestFunc = (x, ct) =>
        {
            stages.Add("attest");
            return attest(x, ct);
        };
        return (new AttestationRuntimeRegistry(new Dictionary<string, IAttestationRuntime> { ["bigquery"] = runtime }), stages);
    }

    private static AttestationOrchestrator Orchestrator(AttestationRuntimeRegistry registry, params string[] allowed) =>
        AttestationOrchestrator.Create(registry, new AttestationOrchestratorOptions { AllowedCodeDigests = allowed.ToHashSet() });

    [Fact]
    public void The_digest_is_SHA256_of_the_exact_UTF8_text_with_no_normalisation()
    {
        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", AttestationCodeDigest.Of("abc"));
        Assert.NotEqual(AttestationCodeDigest.Of("a\nb\n"), AttestationCodeDigest.Of("a\r\nb\r\n"));
        Assert.Equal(64, AttestationCodeDigest.Of("é").Length);
    }

    [Fact]
    public async Task Approved_inline_computation_and_attester_run()
    {
        using var tmp = new TempDir();
        WriteInline(tmp);
        var (registry, stages) = Recording();

        // What an operator hashes is what the capture holds, the text the runtime receives.
        var bundle = Bundle.Load(tmp.Path);
        var capture = AttestationOrchestrator.Capture(bundle, bundle.Get(ConceptId.Parse("c/rev"))!).Capture!;
        Assert.Equal(InlineCode, capture.Computation.InlineCode);
        Assert.Equal(Attester, capture.AttesterSourceText);

        var outcome = await Orchestrator(registry, AttestationCodeDigest.Of(InlineCode), AttestationCodeDigest.Of(Attester))
            .RunAsync(Bundle.Load(tmp.Path), ConceptId.Parse("c/rev"), Year);

        Assert.True(outcome.Displayable, string.Join("; ", outcome.Reasons));
        Assert.Empty(outcome.UnapprovedCode);
        Assert.Equal(["bind", "execute", "attest"], stages);
    }

    [Fact]
    public async Task Approved_file_computation_runs_and_a_one_byte_change_to_the_file_is_refused_before_any_stage()
    {
        using var tmp = new TempDir();
        WriteFile(tmp);
        var (registry, stages) = Recording();
        var orchestrator = Orchestrator(registry, AttestationCodeDigest.Of(FileCode), AttestationCodeDigest.Of(Attester));

        Assert.True((await orchestrator.RunAsync(Bundle.Load(tmp.Path), ConceptId.Parse("c/rev"), Year)).Displayable);
        stages.Clear();

        const string Changed = "SELECT revenue FROM u;\n";
        WriteFile(tmp, Changed);
        var refused = await orchestrator.RunAsync(Bundle.Load(tmp.Path), ConceptId.Parse("c/rev"), Year);

        Assert.False(refused.Displayable);
        Assert.Empty(stages);
        var unapproved = Assert.Single(refused.UnapprovedCode);
        Assert.Equal(new UnapprovedCode(SanctionedCodeRole.Computation, AttestationCodeDigest.Of(Changed)), unapproved);
        Assert.Equal("the computation is not approved by the host's allowlist of sanctioned code, and is not run", Assert.Single(refused.Reasons));
        Assert.DoesNotContain(refused.Reasons, r => r.Contains(unapproved.Digest, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_one_byte_change_to_the_inline_computation_is_refused_before_any_stage()
    {
        using var tmp = new TempDir();
        WriteInline(tmp, code: "SELECT @yeaR");
        var (registry, stages) = Recording();

        var refused = await Orchestrator(registry, AttestationCodeDigest.Of(InlineCode), AttestationCodeDigest.Of(Attester))
            .RunAsync(Bundle.Load(tmp.Path), ConceptId.Parse("c/rev"), Year);

        Assert.False(refused.Displayable);
        Assert.Empty(stages);
        Assert.Equal(SanctionedCodeRole.Computation, Assert.Single(refused.UnapprovedCode).Role);
    }

    [Fact]
    public async Task A_one_byte_change_to_the_attester_is_refused_before_any_stage()
    {
        using var tmp = new TempDir();
        const string Changed = "def attest(**_):\n    return {}\n\n";
        WriteInline(tmp, attester: Changed);
        var (registry, stages) = Recording();

        var refused = await Orchestrator(registry, AttestationCodeDigest.Of(InlineCode), AttestationCodeDigest.Of(Attester))
            .RunAsync(Bundle.Load(tmp.Path), ConceptId.Parse("c/rev"), Year);

        Assert.Empty(stages);
        Assert.Equal(new UnapprovedCode(SanctionedCodeRole.Attester, AttestationCodeDigest.Of(Changed)), Assert.Single(refused.UnapprovedCode));
        Assert.Equal("the attester is not approved by the host's allowlist of sanctioned code, and is not run", Assert.Single(refused.Reasons));
    }

    [Fact]
    public async Task An_empty_allowlist_refuses_both_roles_and_names_both_digests_for_the_host()
    {
        using var tmp = new TempDir();
        WriteInline(tmp);
        var (registry, stages) = Recording();

        var refused = await Orchestrator(registry).RunAsync(Bundle.Load(tmp.Path), ConceptId.Parse("c/rev"), Year);

        Assert.Empty(stages);
        Assert.Equal(
            [new UnapprovedCode(SanctionedCodeRole.Computation, AttestationCodeDigest.Of(InlineCode)), new UnapprovedCode(SanctionedCodeRole.Attester, AttestationCodeDigest.Of(Attester))],
            refused.UnapprovedCode);
        Assert.Equal(2, refused.Reasons.Count);
    }

    [Fact]
    public async Task Without_an_allowlist_every_capture_runs_as_before()
    {
        using var tmp = new TempDir();
        WriteInline(tmp, code: "anything at all");
        var (registry, stages) = Recording();

        var outcome = await new AttestationOrchestrator(registry).RunAsync(Bundle.Load(tmp.Path), ConceptId.Parse("c/rev"), Year);

        Assert.True(outcome.Displayable);
        Assert.Equal(3, stages.Count);
    }

    [Fact]
    public async Task Digests_are_compared_ignoring_case_and_the_hosts_set_is_copied_at_construction()
    {
        using var tmp = new TempDir();
        WriteInline(tmp);
        var (registry, stages) = Recording();
        var allowed = new HashSet<string> { AttestationCodeDigest.Of(InlineCode).ToUpperInvariant(), AttestationCodeDigest.Of(Attester) };
        var orchestrator = AttestationOrchestrator.Create(registry, new AttestationOrchestratorOptions { AllowedCodeDigests = allowed });
        allowed.Clear();

        Assert.True((await orchestrator.RunAsync(Bundle.Load(tmp.Path), ConceptId.Parse("c/rev"), Year)).Displayable);
        Assert.Equal(3, stages.Count);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-digest")]
    [InlineData("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015a")] // 63 digits
    [InlineData("ga7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad")] // not hex
    public void A_malformed_allowlist_entry_is_refused_at_construction(string entry)
    {
        var (registry, _) = Recording();
        Assert.Throws<ArgumentException>(() => AttestationOrchestrator.Create(registry, new AttestationOrchestratorOptions { AllowedCodeDigests = new HashSet<string> { entry } }));
    }

    [Fact]
    public async Task The_tool_text_a_model_sees_names_the_refusal_without_the_digest()
    {
        using var tmp = new TempDir();
        WriteInline(tmp);
        var (registry, stages) = Recording();
        var tools = new OkfBundleTools(tmp.Path, Orchestrator(registry, AttestationCodeDigest.Of(Attester)));

        var rendered = await tools.RunComputationAsync("c/rev", Year);

        Assert.Empty(stages);
        Assert.Contains("the computation is not approved by the host's allowlist of sanctioned code", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain(AttestationCodeDigest.Of(InlineCode), rendered, StringComparison.Ordinal);
    }
}
