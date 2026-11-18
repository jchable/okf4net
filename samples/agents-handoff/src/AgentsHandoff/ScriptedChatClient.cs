// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;

namespace OKF4net.Samples.AgentsHandoff;

/// <summary>
/// One turn of a <see cref="ScriptedChatClient"/>'s script: either a tool call
/// for the agent to execute, or the final text answer that ends the run.
/// </summary>
public sealed record ScriptStep
{
    private ScriptStep()
    {
    }

    /// <summary>The tool to call (e.g. <c>okf_search</c>), or <see langword="null"/> for a final-answer step.</summary>
    public string? FunctionName { get; private init; }

    /// <summary>The tool call's arguments, or <see langword="null"/> for a final-answer step.</summary>
    public IDictionary<string, object?>? Arguments { get; private init; }

    /// <summary>The final answer, or <see langword="null"/> for a tool-call step.</summary>
    public string? FinalText { get; private init; }

    /// <summary>A step asking the agent to invoke <paramref name="functionName"/> with <paramref name="arguments"/>.</summary>
    public static ScriptStep Call(string functionName, IDictionary<string, object?> arguments) =>
        new() { FunctionName = functionName, Arguments = arguments };

    /// <summary>A step ending the run with <paramref name="finalText"/> as the assistant's answer.</summary>
    public static ScriptStep Answer(string finalText) =>
        new() { FinalText = finalText };
}

/// <summary>
/// A stand-in for a language model: an <see cref="IChatClient"/> that replays
/// a fixed list of <see cref="ScriptStep"/>s instead of calling anything. No
/// network, no API key. The same pattern as <c>samples/agents-quickstart</c>'s
/// copy, with two additions to its narration: the first line of each tool
/// result, and the ids of the <c>&lt;okf-context&gt;</c> blocks an
/// <c>OkfContextProvider</c> injected into the turn.
///
/// <para>
/// Only the model is fake. Each call to <see cref="GetResponseAsync"/> is one
/// model turn; when a step is a tool call, the Agent Framework's real
/// function-invoking pipeline executes the real <c>okf_*</c> tool and sends
/// its result back in the next turn's messages, exactly as it would for a
/// hosted model. The reply never depends on those results: reacting to them is
/// the one thing a real model does that this class does not.
/// </para>
///
/// <para>
/// Assumes one tool call per turn, so the newest tool result in the incoming
/// messages always belongs to the previous step.
/// </para>
/// </summary>
/// <param name="name">The agent's name, prefixed to every narrated line.</param>
/// <param name="script">The steps to replay, in order, one per model turn.</param>
/// <param name="narrate">Receives the lines describing what the "model" saw and what it replies.</param>
public sealed partial class ScriptedChatClient(string name, IReadOnlyList<ScriptStep> script, Action<string> narrate) : IChatClient
{
    private int _turn;

    /// <inheritdoc />
    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (_turn >= script.Count)
        {
            throw new InvalidOperationException(
                $"ScriptedChatClient '{name}': the agent asked for turn {_turn + 1}, but the script only has {script.Count} step(s).");
        }

        var step = script[_turn];
        var turnNumber = ++_turn;
        var prefix = $"[{name}, turn {turnNumber}]";
        var history = messages.ToList();

        // What arrived from the previous step: the most recent tool result, if any.
        var lastResult = history
            .SelectMany(m => m.Contents.OfType<FunctionResultContent>())
            .LastOrDefault();
        if (lastResult is null)
        {
            narrate($"{prefix} received the user's question ({options?.Tools?.Count ?? 0} tools offered)");

            // Context an AIContextProvider added to this invocation arrives as ordinary
            // messages; the OkfContextProvider frames each block as <okf-context id="...">.
            var contextIds = history
                .Where(m => m.Role != ChatRole.Assistant && m.Role != ChatRole.Tool)
                .SelectMany(m => ContextBlockId().Matches(m.Text).Select(match => match.Groups[1].Value))
                .ToList();
            if (contextIds.Count > 0)
            {
                narrate($"{prefix} injected context blocks: {string.Join(", ", contextIds)}");
            }
        }
        else
        {
            var text = lastResult.Result?.ToString() ?? string.Empty;
            var firstLine = text.ReplaceLineEndings("\n").Split('\n').FirstOrDefault(l => l.Trim().Length > 0) ?? string.Empty;
            narrate($"{prefix} received a tool result ({text.Length} chars): {Shorten(firstLine.Trim(), 110)}");
        }

        ChatMessage reply;
        if (step.FunctionName is not null)
        {
            narrate($"{prefix} replies with a call to {step.FunctionName}");
            reply = new ChatMessage(ChatRole.Assistant, [new FunctionCallContent($"{name}_call_{turnNumber}", step.FunctionName, step.Arguments!)]);
        }
        else
        {
            narrate($"{prefix} replies with its final answer");
            reply = new ChatMessage(ChatRole.Assistant, step.FinalText);
        }

        return Task.FromResult(new ChatResponse(reply));
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
        foreach (var update in response.ToChatResponseUpdates())
        {
            yield return update;
        }
    }

    /// <inheritdoc />
    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    /// <inheritdoc />
    public void Dispose()
    {
        // Nothing to release: this client owns no connection.
    }

    private static string Shorten(string text, int max) =>
        text.Length <= max ? text : text[..(max - 3)] + "...";

    [GeneratedRegex("<okf-context id=\"([^\"]+)\">")]
    private static partial Regex ContextBlockId();
}
