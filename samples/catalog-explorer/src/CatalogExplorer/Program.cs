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

var resolver = new KnowledgeResolverRouter(catalog);
const string queryText = "revenue purchase";

PrintHeader("2. Multi-source search (default: GroupedBySource)");
PrintContext(await resolver.SearchAsync(new KnowledgeQuery(queryText)));

PrintHeader("3. Ranking strategies compared");
foreach (var strategy in new[] { KnowledgeResolverStrategy.GroupedBySource, KnowledgeResolverStrategy.Merged, KnowledgeResolverStrategy.PriorityWeighted })
{
    Console.WriteLine($"-- {strategy} --");
    PrintContext(await resolver.SearchAsync(new KnowledgeQuery(queryText) { ResolverStrategy = strategy }));
}

PrintHeader("4. Visibility");
var employeeScope = new KnowledgeAccessScope(userId: "acme-employee-jsmith");

Console.WriteLine("-- unscoped caller (no restriction) --");
PrintContext(await resolver.SearchAsync(new KnowledgeQuery(queryText)));

Console.WriteLine("-- external-partner caller (PermittedSourceIds = { \"ga4-reference\" }) --");
PrintContext(await resolver.SearchAsync(new KnowledgeQuery(queryText)
{
    PermittedSourceIds = new HashSet<string> { "ga4-reference" },
}));

Console.WriteLine("-- acme-employee caller (SourceVisibilityPolicy) --");
PrintContext(await resolver.SearchAsync(new KnowledgeQuery(queryText)
{
    Scope = employeeScope,
    SourceVisibilityPolicy = AcmeIfEmployee,
}));

Console.WriteLine("-- non-employee caller (SourceVisibilityPolicy, fails closed) --");
PrintContext(await resolver.SearchAsync(new KnowledgeQuery(queryText)
{
    Scope = new KnowledgeAccessScope(userId: "external-bob"),
    SourceVisibilityPolicy = AcmeIfEmployee,
}));

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

static void PrintContext(KnowledgeContext context)
{
    foreach (var passage in context.Passages)
    {
        Console.WriteLine($"  [{passage.SourceId}] {passage.ConceptId} ({passage.Score}): {passage.Title}");
    }

    foreach (var diagnostic in context.Diagnostics)
    {
        Console.WriteLine($"  diagnostic: [{diagnostic.Code}] source={diagnostic.SourceId} {diagnostic.Message}");
    }

    if (context.Passages.Count == 0 && context.Diagnostics.Count == 0)
    {
        Console.WriteLine("  (no passages, no diagnostics)");
    }
}

static bool AcmeIfEmployee(KnowledgeAccessScope scope, KnowledgeCatalogSource source) =>
    source.Id == "ga4-reference"
    || (scope.UserId is { } userId && userId.StartsWith("acme-employee-", StringComparison.Ordinal) && source.Id == "acme");
