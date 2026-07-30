// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net.Samples.AcmeRetailAgent;

var bundleRoot = ResolveBundleRoot(Environment.GetEnvironmentVariable("OKF_BUNDLE_ROOT"));
if (bundleRoot is null)
{
    Console.Error.WriteLine(
        "acme-retail-agent: could not locate bundles/acme_retail (no OKF4net.sln found "
        + "above " + AppContext.BaseDirectory + "). Set OKF_BUNDLE_ROOT to an absolute path instead.");
    return 2;
}

if (!Directory.Exists(bundleRoot))
{
    Console.Error.WriteLine($"acme-retail-agent: bundle root not found: {bundleRoot}. Set OKF_BUNDLE_ROOT to override.");
    return 2;
}

if (!ChatClientFactory.TryCreate(Environment.GetEnvironmentVariable, out var chatClient, out var chatError))
{
    Console.Error.WriteLine(ChatClientFactory.FormatStartupError(chatError));
    return 2;
}

Console.WriteLine($"chat client ready (bundle root: {bundleRoot})");
return 0;

static string? ResolveBundleRoot(string? overridePath)
{
    if (!string.IsNullOrWhiteSpace(overridePath))
    {
        return Path.GetFullPath(overridePath.Trim());
    }

    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "OKF4net.sln")))
    {
        dir = dir.Parent;
    }

    return dir is null ? null : Path.Combine(dir.FullName, "bundles", "acme_retail");
}
