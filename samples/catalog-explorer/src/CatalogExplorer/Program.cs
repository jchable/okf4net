// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net.Catalog;

var repoRoot = FindRepoRoot()
    ?? throw new InvalidOperationException(
        "catalog-explorer: could not locate OKF4net.sln by walking up from the running assembly.");

var catalogFilePath = Path.GetFullPath(Path.Combine(repoRoot, "samples", "catalog-explorer", "config", "catalog.json"));
var options = new KnowledgeCatalogOptions
{
    CatalogFilePath = catalogFilePath,
    CatalogRoot = repoRoot,
    WatchForChanges = false,
};

using var catalog = new FileKnowledgeCatalog(options);

PrintHeader("1. Load & inspect");
foreach (var source in catalog.Current.Sources)
{
    Console.WriteLine($"  [{(source.Enabled ? "enabled " : "disabled")}] {source.Id,-14} role={source.Role,-9} priority={source.Priority,-3} path={source.Path}");
}

if (catalog.LastReloadDiagnostics.Count > 0)
{
    foreach (var d in catalog.LastReloadDiagnostics)
    {
        Console.WriteLine($"  diagnostic: [{d.Code}] {d.Message}");
    }
}

return 0;

static string? FindRepoRoot()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "OKF4net.sln")))
    {
        dir = dir.Parent;
    }

    return dir?.FullName;
}

static void PrintHeader(string title)
{
    Console.WriteLine();
    Console.WriteLine($"=== {title} ===");
}
