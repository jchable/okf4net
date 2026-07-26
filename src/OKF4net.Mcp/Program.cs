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
    // Spec: print a one-line usage/error to stderr and exit non-zero. Kept as a
    // single line via OkfMcpConfig.FormatStartupError (unit-tested) rather than
    // an error line + a separate usage line.
    Console.Error.WriteLine(OkfMcpConfig.FormatStartupError(error));
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
