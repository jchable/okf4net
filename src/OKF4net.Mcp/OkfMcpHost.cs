// SPDX-License-Identifier: LGPL-3.0-or-later
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace OKF4net.Mcp;

/// <summary>Shared MCP server service registration used by both the host entry point and tests.</summary>
internal static class OkfMcpHost
{
    /// <summary>
    /// Registers the MCP server (named "okf", reporting <paramref name="version"/>) and every OKF
    /// tool from <see cref="OkfMcpToolset.Build(string, bool)"/> as a singleton, so the SDK's options
    /// setup collects them into the server's tool collection. Returns the builder so the caller can
    /// attach a transport (e.g. stdio). Does NOT register a transport itself.
    /// </summary>
    internal static IMcpServerBuilder ConfigureServices(IServiceCollection services, string bundleRoot, bool readOnly, string version)
    {
        var mcp = services.AddMcpServer(options => options.ServerInfo = new Implementation { Name = "okf", Version = version });
        foreach (var tool in OkfMcpToolset.Build(bundleRoot, readOnly))
        {
            services.AddSingleton(tool);
        }

        return mcp;
    }
}
