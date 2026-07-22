// SPDX-License-Identifier: LGPL-3.0-or-later
using Microsoft.Extensions.AI;

namespace OKF4net.Tests.Agents;

/// <summary>
/// One turn of a <see cref="ScriptedChatClient"/>'s script: either a tool call
/// the double asks the (real) function-invoking pipeline to execute, or the
/// final text answer that ends the conversation. Exactly one of
/// <see cref="FunctionName"/>/<see cref="Arguments"/> or <see cref="FinalText"/>
/// is set, enforced by the two factory methods.
/// </summary>
public sealed record ScriptStep
{
    private ScriptStep()
    {
    }

    /// <summary>The tool name to call (e.g. <c>okf_write_concept</c>), or <c>null</c> for a final-answer step.</summary>
    public string? FunctionName { get; private init; }

    /// <summary>The tool call's arguments, or <c>null</c> for a final-answer step.</summary>
    public IDictionary<string, object?>? Arguments { get; private init; }

    /// <summary>The final text answer, or <c>null</c> for a tool-call step.</summary>
    public string? FinalText { get; private init; }

    /// <summary>A step asking the pipeline to invoke <paramref name="functionName"/> with <paramref name="arguments"/>.</summary>
    public static ScriptStep Call(string functionName, IDictionary<string, object?> arguments) =>
        new() { FunctionName = functionName, Arguments = arguments };

    /// <summary>A step ending the conversation with <paramref name="finalText"/> as the assistant's answer.</summary>
    public static ScriptStep Answer(string finalText) =>
        new() { FinalText = finalText };
}

/// <summary>
/// A test-only <see cref="IChatClient"/> double that replays a predefined
/// <see cref="ScriptStep"/> sequence instead of calling a real LLM -- zero
/// network, zero API keys. Each call to <see cref="GetResponseAsync"/> is one
/// turn: the double either emits a <see cref="FunctionCallContent"/> (which
/// the real Agent Framework function-invoking pipeline then actually
/// executes against the <see cref="AIFunction"/>s configured on the agent,
/// feeding the resulting <see cref="FunctionResultContent"/> back as the next
/// turn's incoming messages) or ends the conversation with a plain-text
/// answer.
///
/// Every <see cref="FunctionResultContent"/> this double observes in incoming
/// messages -- i.e. every result the real pipeline actually produced by
/// invoking a tool -- is recorded in <see cref="ObservedFunctionResults"/>,
/// in the order seen, so a test can assert on what the tools actually
/// returned without re-deriving it.
///
/// Not (yet) supported: correlating a <see cref="FunctionResultContent"/>
/// back to the specific <see cref="ScriptStep"/> that requested it via
/// <see cref="FunctionCallContent.CallId"/>. This double assumes exactly one
/// pending tool call per turn (one <see cref="ScriptStep.Call"/> per
/// <see cref="GetResponseAsync"/> invocation), so results are recorded and
/// consumed in the same order the calls were made. A script that issues
/// multiple concurrent tool calls in a single turn would need CallId-based
/// matching to know which result belongs to which call.
/// </summary>
public sealed class ScriptedChatClient : IChatClient
{
    private readonly IReadOnlyList<ScriptStep> _script;
    private int _turn;

    /// <summary>
    /// How many messages have already been scanned by <see cref="RecordFunctionResults"/>.
    /// The pipeline passes the whole, ever-growing conversation on every
    /// call, so only the tail past this count is new since the last turn.
    /// </summary>
    private int _messagesSeen;

    /// <summary>Creates a double that replays <paramref name="script"/> in order, one step per call to <see cref="GetResponseAsync"/>.</summary>
    public ScriptedChatClient(IReadOnlyList<ScriptStep> script)
    {
        _script = script;
    }

    /// <summary>
    /// Every <see cref="FunctionResultContent.Result"/>, rendered via
    /// <c>ToString()</c>, observed across all turns so far, in the order the
    /// real pipeline delivered them back to this double.
    /// </summary>
    public List<string> ObservedFunctionResults { get; } = [];

    /// <summary>How many of <see cref="ScriptStep"/>s in the script have been consumed so far.</summary>
    public int TurnsTaken => _turn;

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        RecordFunctionResults(messages);

        if (_turn >= _script.Count)
        {
            throw new InvalidOperationException(
                $"ScriptedChatClient: the pipeline asked for turn {_turn + 1}, but the script only has {_script.Count} step(s).");
        }

        var step = _script[_turn];
        var callId = $"call_{_turn}";
        _turn++;

        var message = step.FunctionName is not null
            ? new ChatMessage(ChatRole.Assistant, [new FunctionCallContent(callId, step.FunctionName, step.Arguments!)])
            : new ChatMessage(ChatRole.Assistant, step.FinalText);

        return Task.FromResult(new ChatResponse(message));
    }

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

    /// <summary>
    /// Scans only the messages appended since the previous turn (the
    /// pipeline re-passes the whole growing conversation every call, so
    /// re-scanning from the start would re-record earlier turns' results)
    /// for a <see cref="FunctionResultContent"/>, appending each one's
    /// rendered result to <see cref="ObservedFunctionResults"/>.
    /// </summary>
    private void RecordFunctionResults(IEnumerable<ChatMessage> messages)
    {
        var list = messages as IReadOnlyList<ChatMessage> ?? messages.ToList();
        for (var i = _messagesSeen; i < list.Count; i++)
        {
            foreach (var content in list[i].Contents)
            {
                if (content is FunctionResultContent result)
                {
                    ObservedFunctionResults.Add(result.Result?.ToString() ?? string.Empty);
                }
            }
        }

        _messagesSeen = list.Count;
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
        // No unmanaged resources -- this double owns nothing that needs cleanup.
    }
}
