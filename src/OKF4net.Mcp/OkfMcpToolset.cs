// SPDX-License-Identifier: LGPL-3.0-or-later
using Microsoft.Extensions.AI;
using ModelContextProtocol.Server;
using OKF4net.Agents;

namespace OKF4net.Mcp;

/// <summary>
/// Builds the MCP tool set for one OKF bundle by wrapping the
/// <see cref="AIFunction"/>s produced by <see cref="OkfBundleTools.GetTools"/>.
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

        var result = new List<McpServerTool>();
        foreach (var tool in okf.GetTools())
        {
            if (readOnly && OkfBundleTools.WriteToolNames.Contains(tool.Name))
            {
                continue;
            }

            result.Add(McpServerTool.Create((AIFunction)tool));
        }

        return result;
    }
}
