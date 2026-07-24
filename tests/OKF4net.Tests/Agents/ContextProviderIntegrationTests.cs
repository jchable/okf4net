// SPDX-License-Identifier: LGPL-3.0-or-later
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OKF4net.Agents;

namespace OKF4net.Tests.Agents;

/// <summary>
/// End-to-end, zero-network, zero-API-key integration test proving the real
/// registration point for <see cref="OkfContextProvider"/> --
/// <see cref="ChatClientAgentOptions.AIContextProviders"/> -- actually wires
/// the provider into a real <see cref="ChatClientAgent"/>'s invocation
/// pipeline. Unlike <see cref="OkfContextProviderTests"/> and
/// <see cref="OkfContextProviderMemoryTests"/> (which reach
/// <c>ProvideAIContextAsync</c>/<c>StoreAIContextAsync</c> directly through
/// the internal <c>ProvideForTest</c>/<c>StoreForTest</c> test seams), this
/// test never touches the provider's methods itself: it only asserts on what
/// the scripted "model" -- the innermost <see cref="IChatClient"/> in the
/// real pipeline -- actually received, which is only possible if the real
/// <c>AIContextProviderChatClient</c> decorator that <see cref="ChatClientAgent"/>
/// installs from <see cref="ChatClientAgentOptions.AIContextProviders"/>
/// actually ran.
///
/// Mirrors <see cref="AgentIntegrationTests"/>'s pattern (a <see cref="ScriptedChatClient"/>
/// double driving the real framework pipeline against a throwaway copy of
/// <c>tests/fixtures/appendix_a</c>), extended across two turns of the same
/// session: turn 1 proves injection (progressive disclosure), the post-turn
/// assertions prove memory capture landed on disk, and turn 2 proves the
/// captured memory concept is itself scored and re-injected -- the full
/// capture-then-recall loop.
/// </summary>
public class ContextProviderIntegrationTests
{
    private static readonly string BundlePath = Path.Combine(TestPaths.RepoRoot(), "tests", "fixtures", "appendix_a");

    /// <summary>
    /// Pins <see cref="OkfBundleTools.UtcNow"/> so the memory concept this
    /// test's turn 1 creates always lands at a deterministic path
    /// (<c>memory/2026-07-24.md</c>), regardless of the day the test
    /// actually runs.
    /// </summary>
    private static readonly DateTime PinnedNow = new(2026, 7, 24, 12, 0, 0, DateTimeKind.Utc);

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
    public async Task Provider_injects_context_captures_memory_and_recalls_it_next_turn()
    {
        using var tmp = new TempDir();
        CopyDirectory(BundlePath, tmp.Path);
        var tools = new OkfBundleTools(tmp.Path) { UtcNow = () => PinnedNow };
        var provider = new OkfContextProvider(tools, new OkfContextProviderOptions { EnableMemoryCapture = true });

        // "nonce-zqxw77" is a made-up token that appears nowhere in the
        // fixture bundle -- once turn 1's answer is captured into memory, it
        // is the ONLY place that token exists, so a turn-2 query built from
        // it deterministically scores memory/2026-07-24 above zero (and
        // every real bundle concept at exactly zero), regardless of how
        // OkfBundleTools.ScoreConcept happens to weigh real content. This is
        // the "assert deterministically" fallback the task brief calls out
        // for the recall assertion.
        const string turn1Answer =
            "There is a tables/orders concept: one row per completed customer order. (nonce-zqxw77)";
        const string turn2Answer = "Yes -- we discussed tables/orders a moment ago.";

        var scriptedClient = new ScriptedChatClient(
        [
            ScriptStep.Answer(turn1Answer),
            ScriptStep.Answer(turn2Answer),
        ]);

        // THE key API decision: build the ChatClientAgent via
        // ChatClientAgentOptions (Microsoft.Extensions.AI.ChatClientExtensions.AsAIAgent(
        // this IChatClient, ChatClientAgentOptions, ILoggerFactory?, IServiceProvider?)),
        // not the simpler `AsAIAgent(tools:)` convenience overload
        // AgentIntegrationTests uses -- AIContextProviders only lives on
        // ChatClientAgentOptions (confirmed against the installed
        // Microsoft.Agents.AI.Abstractions 1.14.0 XML docs in the Task 1
        // report; there is no "tools + providers" convenience overload).
        // ChatClientAgent still inserts its own FunctionInvokingChatClient
        // (tools wired via ChatOptions.Tools) and AIContextProviderChatClient
        // (providers wired via AIContextProviders) decorators around the raw
        // chat client automatically, exactly as the simpler overload does.
        var agentOptions = new ChatClientAgentOptions
        {
            ChatOptions = new ChatOptions { Tools = tools.GetTools() },
            AIContextProviders = [provider],
        };
        AIAgent agent = scriptedClient.AsAIAgent(agentOptions);

        // One session across both turns, so turn 2 is a continuation of the
        // same conversation turn 1 started (its history -- including turn
        // 1's own question and answer -- carries over automatically).
        var session = await agent.CreateSessionAsync();

        // --- Turn 1: "what do we know about orders?" ---------------------
        // Deliberately no trailing "?": OkfBundleTools.ScoreConceptsFor/
        // ScoreConcept splits the query on whitespace and does a per-term
        // Contains(...) match, so a punctuation-glued final term ("orders?")
        // would never match the bundle's plain "orders" text and could flake
        // the injection down to zero concept blocks -- the same
        // determinism concern the task brief calls out for turn 2's query.
        var response1 = await agent.RunAsync("What do we know about orders", session);
        Assert.Equal(turn1Answer, response1.Text);

        // The scripted "model" received exactly one call for turn 1 (a
        // single Answer step, no tool calls) -- and the messages IT actually
        // received (not a direct call into the provider) contain the
        // injected <okf-context id="tables/orders"> block. This is only
        // possible if ChatClientAgentOptions.AIContextProviders really wired
        // OkfContextProvider into the live pipeline; see the report for the
        // sabotage run that fails this exact assertion when that
        // registration is removed.
        var firstCallMessages = Assert.Single(scriptedClient.ObservedRequestMessages);
        Assert.Contains(
            firstCallMessages,
            m => m.Text.Contains("<okf-context id=\"tables/orders\">", StringComparison.Ordinal));

        // --- Post-turn 1: memory capture actually landed on disk ---------
        var memoryPath = Path.Combine(tmp.Path, "memory", "2026-07-24.md");
        Assert.True(File.Exists(memoryPath));

        var memoryDoc = OkfDocument.Parse(await File.ReadAllTextAsync(memoryPath));
        memoryDoc.Validate(); // producer-grade validation must pass -- throws on failure.
        Assert.Contains("> What do we know about orders", memoryDoc.Body, StringComparison.Ordinal);
        Assert.Contains("> " + turn1Answer, memoryDoc.Body, StringComparison.Ordinal);

        var logText = await File.ReadAllTextAsync(Path.Combine(tmp.Path, "log.md"));
        Assert.Contains("**Memory**: Captured exchange in memory/2026-07-24", logText, StringComparison.Ordinal);

        // The bundle as a whole -- fixture concepts plus the newly-written
        // memory concept -- still validates conformant.
        var validationReport = BundleValidator.Validate(tools.GetBundle());
        Assert.True(validationReport.IsConformant);

        // --- Turn 2: capture -> recall loop -------------------------------
        var response2 = await agent.RunAsync("nonce-zqxw77", session);
        Assert.Equal(turn2Answer, response2.Text);

        Assert.Equal(2, scriptedClient.ObservedRequestMessages.Count);
        var secondCallMessages = scriptedClient.ObservedRequestMessages[1];
        var recalled = Assert.Single(
            secondCallMessages,
            m => m.Text.Contains("<okf-context id=\"memory/2026-07-24\">", StringComparison.Ordinal));
        Assert.Contains("nonce-zqxw77", recalled.Text, StringComparison.Ordinal);
    }
}
