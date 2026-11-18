// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Text.Json;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Server;
using OKF4net.Agents;

namespace OKF4net.Mcp;

/// <summary>
/// Builds the MCP tool set for one OKF bundle by wrapping the
/// <see cref="AIFunction"/>s produced by <see cref="OkfBundleTools.GetTools()"/>.
/// This is the single seam shared by the stdio host (<c>Program</c>) and the
/// tests, so both expose exactly the same tools.
/// </summary>
public static class OkfMcpToolset
{
    /// <summary>
    /// Creates the MCP tools rooted at <paramref name="bundleRoot"/>. When
    /// <paramref name="readOnly"/> is <see langword="true"/>, the four write
    /// tools (<see cref="OkfBundleTools.WriteToolNames"/>) are omitted so the
    /// bundle is served for consultation only.
    /// </summary>
    /// <param name="bundleRoot">Path to the OKF bundle's root directory.</param>
    /// <param name="readOnly">When true, omit the write tools.</param>
    /// <returns>The MCP tools to register on the server.</returns>
    /// <exception cref="ArgumentException"><paramref name="bundleRoot"/> does not exist.</exception>
    public static IReadOnlyList<McpServerTool> Build(string bundleRoot, bool readOnly)
    {
        var okf = new OkfBundleTools(bundleRoot);

        // The filtering lives in OkfToolMode.ReadOnly rather than here: this
        // used to re-implement it against WriteToolNames, a second copy of a
        // rule that exists precisely so there is only one.
        var mode = readOnly ? OkfToolMode.ReadOnly : OkfToolMode.ReadWrite;

        var result = new List<McpServerTool>();
        foreach (var tool in okf.GetTools(mode))
        {
            result.Add(McpServerTool.Create(new TextResultFunction((AIFunction)tool)));
        }

        return result;
    }

    /// <summary>
    /// Hands the MCP adapter the tool's <see cref="string"/> result as a string
    /// (#255). <see cref="AIFunctionFactory"/> marshals a method's return value
    /// to a <see cref="JsonElement"/>, so a <c>string</c> arrives as a JSON
    /// string element; the SDK turns a <c>string</c> into a text content block
    /// verbatim but serialises any other type to JSON, which delivered every
    /// result as a quoted literal with <c>\n</c> escapes. Delegates everything
    /// else — name, description, schema, and the invocation itself, so the
    /// observation-checking wrappers <see cref="OkfBundleTools.GetTools(OkfToolMode)"/>
    /// binds still run.
    /// </summary>
    private sealed class TextResultFunction(AIFunction inner) : DelegatingAIFunction(inner)
    {
        protected override async ValueTask<object?> InvokeCoreAsync(
            AIFunctionArguments arguments, CancellationToken cancellationToken)
        {
            var value = await base.InvokeCoreAsync(arguments, cancellationToken).ConfigureAwait(false);
            return value is JsonElement { ValueKind: JsonValueKind.String } text ? text.GetString() : value;
        }
    }
}
