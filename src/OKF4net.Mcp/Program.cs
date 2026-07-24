// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net.Mcp;

/// <summary>
/// Placeholder entry point for the <c>okf-mcp</c> stdio host. The real
/// implementation (config resolution and wiring up <c>OkfMcpToolset</c>
/// to a stdio-transport <c>McpServer</c>) lands in a later task; this
/// placeholder exists only so the <c>OutputType=Exe</c> project builds.
/// </summary>
internal static class Program
{
    private static void Main()
    {
    }
}
