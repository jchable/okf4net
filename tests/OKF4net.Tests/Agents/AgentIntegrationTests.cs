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

    private static readonly string BundlePath = Path.Combine(RepoRoot(), "tests", "fixtures", "appendix_a");

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "OKF4net.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName
            ?? throw new InvalidOperationException($"could not locate OKF4net.sln above {AppContext.BaseDirectory}");
    }

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
}
