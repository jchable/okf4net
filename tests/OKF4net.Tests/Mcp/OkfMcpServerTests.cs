// SPDX-License-Identifier: LGPL-3.0-or-later
using System.IO.Pipelines;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using OKF4net.Mcp;

namespace OKF4net.Tests.Mcp;

public sealed class OkfMcpServerTests
{
    private static string NewBundleDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "okf-mcp-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Spins up an in-memory MCP server over the given tools and returns a connected client.</summary>
    private static async Task<(McpServer Server, McpClient Client)> ConnectAsync(IReadOnlyList<McpServerTool> tools)
    {
        var clientToServer = new Pipe();
        var serverToClient = new Pipe();

        var server = McpServer.Create(
            new StreamServerTransport(clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream()),
            new McpServerOptions { ToolCollection = [.. tools] });
        _ = server.RunAsync();

        var client = await McpClient.CreateAsync(
            new StreamClientTransport(clientToServer.Writer.AsStream(), serverToClient.Reader.AsStream()));

        return (server, client);
    }

    [Fact]
    public async Task Build_exposes_all_nine_tools()
    {
        var bundle = NewBundleDir();
        try
        {
            var tools = OkfMcpToolset.Build(bundle, readOnly: false);
            var (server, client) = await ConnectAsync(tools);
            await using var _ = server;
            await using var __ = client;

            var listed = await client.ListToolsAsync();
            var names = listed.Select(t => t.Name).OrderBy(n => n).ToArray();

            Assert.Equal(
                new[]
                {
                    "okf_append_log", "okf_browse", "okf_changes_since", "okf_graph",
                    "okf_read_concept", "okf_regenerate_indexes", "okf_search",
                    "okf_validate_bundle", "okf_write_concept",
                },
                names);
        }
        finally
        {
            Directory.Delete(bundle, recursive: true);
        }
    }
}
