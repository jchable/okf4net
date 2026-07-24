// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OKF4net.Mcp;

// Resolve configuration up front; a misconfigured launch must fail loudly on
// stderr (stdout is reserved for the JSON-RPC stream) with a non-zero code.
if (!OkfMcpConfig.TryResolve(args, Environment.GetEnvironmentVariable, out var bundleRoot, out var readOnly, out var error))
{
    Console.Error.WriteLine($"okf-mcp: {error}");
    Console.Error.WriteLine("Usage: okf-mcp <bundle-root>   (or set OKF_BUNDLE_ROOT; OKF_MCP_READONLY=1 for read-only)");
    return 2;
}

var builder = Host.CreateApplicationBuilder();

// stdio invariant: every log line goes to stderr so it can never corrupt the
// JSON-RPC protocol carried on stdout.
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);

var informational = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
var version = (informational ?? "0.0.0").Split('+')[0];

OkfMcpHost.ConfigureServices(builder.Services, bundleRoot, readOnly, version).WithStdioServerTransport();

await builder.Build().RunAsync();
return 0;
