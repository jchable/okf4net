// SPDX-License-Identifier: LGPL-3.0-or-later
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OKF4net.Agents;

namespace OKF4net.Tests.Agents;

/// <summary>
/// End-to-end, zero-network, zero-API-key integration test: a real
/// <see cref="ChatClientAgent"/> (built via <c>AsAIAgent</c> from the
/// installed Microsoft.Agents.AI 1.14.0 package) driven by a
/// <see cref="ScriptedChatClient"/> double, exercising the framework's real
/// function-invocation pipeline against the real <see cref="OkfBundleTools"/>
/// <see cref="AIFunction"/>s -- no mocking of tool execution, only the LLM
/// call itself is scripted.
///
/// Scenario (mirrors a user asking "add a concept tables/refunds then update
/// the indexes"): the script calls <c>okf_write_concept</c> with a full valid
/// frontmatter, then <c>okf_regenerate_indexes</c>, then <c>okf_validate_bundle</c>,
/// then returns a final scripted summary. Every step in between is a real
/// tool invocation against a throwaway copy of <c>tests/fixtures/appendix_a</c>.
/// </summary>
public class AgentIntegrationTests
{
    private const string ValidFrontmatter =
        "type: BigQuery Table\n"
        + "title: Refunds\n"
        + "description: One row per refund.\n"
        + "timestamp: 2026-07-22T00:00:00Z\n";

    private const string RefundsBody = "# Refunds\n\nOne row per refund.\n";

    private const string FinalAnswer =
        "Added tables/refunds, regenerated the indexes, and the bundle is conformant.";

    private static readonly string BundlePath = Path.Combine(TestPaths.RepoRoot(), "tests", "fixtures", "appendix_a");

    private static void CopyDirectory(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);
        foreach (var file in Directory.GetFiles(sourceDir))
        {
            File.Copy(file, Path.Combine(destDir, Path.GetFileName(file)));
        }

        foreach (var dir in Directory.GetDirectories(sourceDir))
        {
            CopyDirectory(dir, Path.Combine(destDir, Path.GetFileName(dir)));
        }
    }

    [Fact]
    public async Task Agent_writes_concept_regenerates_indexes_and_validates_end_to_end()
    {
        using var tmp = new TempDir();
        CopyDirectory(BundlePath, tmp.Path);
        var tools = new OkfBundleTools(tmp.Path);

        var scriptedClient = new ScriptedChatClient(
        [
            ScriptStep.Call("okf_write_concept", new Dictionary<string, object?>
            {
                ["conceptId"] = "tables/refunds",
                ["frontmatterYaml"] = ValidFrontmatter,
                ["body"] = RefundsBody,
            }),
            ScriptStep.Call("okf_regenerate_indexes", new Dictionary<string, object?>()),
            ScriptStep.Call("okf_validate_bundle", new Dictionary<string, object?>()),
            ScriptStep.Answer(FinalAnswer),
        ]);

        // THE key API decision: build a real ChatClientAgent over the raw
        // scripted IChatClient via the Microsoft.Agents.AI 1.14.0 AsAIAgent
        // extension (Microsoft.Extensions.AI.ChatClientExtensions.AsAIAgent),
        // handing it the nine OkfBundleTools AIFunctions as its tool list.
        // ChatClientAgent inserts its own FunctionInvokingChatClient in front
        // of the chat client when one isn't already present, so the real
        // function-invocation pipeline runs here -- no manual
        // UseFunctionInvocation() wrapping, and no extra package reference,
        // are needed.
        AIAgent agent = scriptedClient.AsAIAgent(tools: tools.GetTools());

        var response = await agent.RunAsync("Ajoute un concept tables/refunds puis mets à jour les index.");

        // The agent's final answer is exactly the text the script supplied
        // for its last (non-tool-call) turn.
        Assert.Equal(FinalAnswer, response.Text);

        // The pipeline actually executed okf_write_concept: the file exists
        // on disk with the expected frontmatter.
        var refundsPath = Path.Combine(tmp.Path, "tables", "refunds.md");
        Assert.True(File.Exists(refundsPath));
        var refundsContent = await File.ReadAllTextAsync(refundsPath);
        Assert.Contains("type: BigQuery Table", refundsContent);
        Assert.Contains("title: Refunds", refundsContent);
        Assert.Contains("description: One row per refund.", refundsContent);
        Assert.Contains("timestamp: 2026-07-22T00:00:00Z", refundsContent);

        // The pipeline actually executed okf_regenerate_indexes: the
        // directory index lists the new concept.
        var tablesIndex = await File.ReadAllTextAsync(Path.Combine(tmp.Path, "tables", "index.md"));
        Assert.Contains("refunds.md", tablesIndex);
        Assert.Contains("Refunds", tablesIndex);

        // Four round-trips to the scripted "model": the three tool-call
        // turns above, plus the final plain-text-answer turn.
        Assert.Equal(4, scriptedClient.TurnsTaken);

        // The scripted client observed the real FunctionResultContent for
        // each of the three tool calls, in order -- proof the framework's
        // pipeline round-tripped genuine tool output back to the "model".
        Assert.Equal(3, scriptedClient.ObservedFunctionResults.Count);
        Assert.Contains("Written", scriptedClient.ObservedFunctionResults[0]);

        // ObservedFunctionResults[1] (okf_regenerate_indexes's result) is
        // not content-asserted here: its actual effect (tables/index.md
        // listing the new concept) is already verified above by reading the
        // regenerated index.md straight off disk, which is the stronger
        // check. The count/order assertions on ObservedFunctionResults
        // already prove this turn's result did reach the scripted client.

        // "Contains conformant" alone would also match "✗ not conformant
        // with OKF v0.1" and could never fail -- assert the success marker
        // (BundleValidator's verdict line, e.g. "✓ conformant with OKF
        // v0.1") and explicitly rule out the failure marker.
        Assert.Contains("✓ conformant", scriptedClient.ObservedFunctionResults[2]);
        Assert.DoesNotContain("✗", scriptedClient.ObservedFunctionResults[2]);
    }

    /// <summary>
    /// The read-only counterpart of the write scenario, and the question the
    /// audit surface exists for: "which concepts are past stale_after and were
    /// never verified by a human?". It is the only test that
    /// drives <c>okf_audit</c> through the framework's real function-invoking
    /// pipeline rather than calling the C# method directly, so it is what
    /// proves the JSON argument binding works: the scripted "model" passes a
    /// boolean and a comma-separated tier list as JSON, and the framework
    /// must bind them to the method's <c>bool</c> and <c>string?</c>
    /// parameters for the filter to take effect.
    ///
    /// The bundle is built inline so the observation date can be pinned via
    /// the <c>UtcNow</c> seam: <c>metrics/orphan</c> is stale AND unverified
    /// (the answer), while <c>metrics/dau</c> is equally stale but
    /// human-reviewed (the concept the trust filter must exclude). A binding
    /// failure that silently dropped the <c>trust</c> argument would return
    /// both and fail here.
    /// </summary>
    [Fact]
    public async Task Agent_audits_the_bundle_for_stale_never_human_verified_concepts()
    {
        using var tmp = new TempDir();
        tmp.Write(
            "metrics/dau.md",
            "---\ntype: Metric\ntitle: Daily Active Users\nstale_after: 2026-01-01\n"
            + "verified:\n  - { by: human:ada, at: 2026-01-01T00:00:00Z }\n---\n");
        tmp.Write(
            "metrics/orphan.md",
            "---\ntype: Metric\ntitle: Orphaned Metric\nstale_after: 2026-01-01\n---\n");

        var tools = new OkfBundleTools(tmp.Path)
        {
            UtcNow = () => new DateTime(2026, 8, 21, 0, 0, 0, DateTimeKind.Utc),
        };

        const string auditAnswer = "One concept is stale and was never verified by a human: metrics/orphan.";

        var scriptedClient = new ScriptedChatClient(
        [
            ScriptStep.Call("okf_audit", new Dictionary<string, object?>
            {
                ["stale"] = true,
                ["trust"] = "unverified,machine-confirmed",
            }),
            ScriptStep.Answer(auditAnswer),
        ]);

        AIAgent agent = scriptedClient.AsAIAgent(tools: tools.GetTools());

        var response = await agent.RunAsync(
            "Quels concepts ont dépassé leur stale_after sans avoir jamais été vérifiés par un humain ?");

        Assert.Equal(auditAnswer, response.Text);

        // Two round-trips: the tool-call turn and the final-answer turn.
        Assert.Equal(2, scriptedClient.TurnsTaken);

        var auditResult = Assert.Single(scriptedClient.ObservedFunctionResults);

        // The filters bound and took effect: the stale, unverified concept is
        // listed under the worklist heading; the stale but human-reviewed one
        // is not.
        Assert.Contains("needs attention (1):", auditResult);
        Assert.Contains("metrics/orphan  stale 2026-01-01  unverified", auditResult);
        Assert.DoesNotContain("metrics/dau", auditResult);

        // The counters still describe the whole bundle, not the selection --
        // the distinction the audit surface is built on.
        Assert.Contains("concepts:   2", auditResult);
        Assert.Contains("stale:      2 of 2 past stale_after", auditResult);
    }
}
