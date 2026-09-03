// SPDX-License-Identifier: LGPL-3.0-or-later
//
// Spike: can a correct CSharpCompilation be built from MSBuild's own item and
// property queries, WITHOUT MSBuildWorkspace?
//
// This is the experiment that design §7.2 asserted the answer to but never ran.
// The earlier tree-sitter spike fed its compilation from
// AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") — the current process's own
// assemblies — so its error count said nothing about this route either way.
//
// The bar is ZERO errors. A compilation with errors has an incomplete symbol
// table, and a resolver built on it silently mis-attributes calls, which is
// worse than not resolving them at all.
//
// Usage: roslyn-compilation-spike <path-to-csproj> [more.csproj ...]

using System.Diagnostics;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

// --drop-project-refs simulates a repo that has been restored but NOT built:
// ProjectReferences resolve to bin/<config>/<tfm>/*.dll, which only exists
// after a build. Design §7.2 claimed "restored" was the requirement; this flag
// is how that claim gets tested rather than assumed.
var dropProjectRefs = args.Contains("--drop-project-refs");
var projects = args.Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToArray();

if (projects.Length == 0)
{
    Console.Error.WriteLine("usage: roslyn-compilation-spike [--drop-project-refs] <project.csproj> [...]");
    return 2;
}

if (dropProjectRefs)
{
    Console.WriteLine("MODE: dropping repo-internal (bin/) references — simulating a restored-but-unbuilt repo.\n");
}

var failures = 0;
foreach (var project in projects)
{
    try
    {
        failures += Probe(Path.GetFullPath(project), dropProjectRefs) ? 0 : 1;
    }
    catch (Exception e)
    {
        Console.WriteLine($"### {project}\n    HARNESS FAILURE: {e.GetType().Name}: {e.Message}\n");
        failures++;
    }
}

Console.WriteLine(failures == 0
    ? "VERDICT: every probed project compiled with zero errors."
    : $"VERDICT: {failures} of {projects.Length} probed projects did not reach zero errors.");

return failures == 0 ? 0 : 1;

static bool Probe(string projectPath, bool dropProjectRefs)
{
    Console.WriteLine($"### {Path.GetFileName(projectPath)}");

    var sw = Stopwatch.StartNew();
    var inputs = QueryMsBuild(projectPath);
    var queryMs = sw.ElapsedMilliseconds;

    var compileItems = inputs.Items.TryGetValue("Compile", out var c) ? c : [];
    var references = inputs.Items.TryGetValue("ReferencePath", out var r) ? r : [];

    Console.WriteLine($"    msbuild query : {queryMs} ms");
    Console.WriteLine($"    Compile items : {compileItems.Count} ({compileItems.Count(i => IsGenerated(i))} generated)");
    Console.WriteLine($"    References    : {references.Count}");

    // --- Parse options, from the project's real settings -------------------
    var langVersionRaw = inputs.Property("LangVersion");
    if (!LanguageVersionFacts.TryParse(langVersionRaw, out var langVersion))
    {
        // A real constraint for the producer, not a detail: the Roslyn package
        // must track the SDK's language version, or every file using a newer
        // construct fails to parse.
        Console.WriteLine($"    !! LangVersion '{langVersionRaw}' is not known to this Roslyn build; falling back to Preview.");
        langVersion = LanguageVersion.Preview;
    }

    var defines = (inputs.Property("DefineConstants") ?? string.Empty)
        .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    var parseOptions = new CSharpParseOptions(langVersion, DocumentationMode.Parse)
        .WithPreprocessorSymbols(defines);

    var trees = new List<SyntaxTree>(compileItems.Count);
    var unreadable = new List<string>();
    foreach (var item in compileItems)
    {
        var full = item.TryGetValue("FullPath", out var p) ? p : null;
        if (full is null || !File.Exists(full))
        {
            unreadable.Add(full ?? "(no FullPath)");
            continue;
        }

        trees.Add(CSharpSyntaxTree.ParseText(
            SourceText.From(File.ReadAllText(full)), parseOptions, full));
    }

    if (unreadable.Count > 0)
    {
        Console.WriteLine($"    !! {unreadable.Count} Compile items could not be read:");
        foreach (var u in unreadable.Take(5))
        {
            Console.WriteLine($"       {u}");
        }
    }

    // --- Compilation options ------------------------------------------------
    var outputKind = inputs.Property("OutputType") switch
    {
        "Exe" or "WinExe" => OutputKind.ConsoleApplication,
        _ => OutputKind.DynamicallyLinkedLibrary,
    };

    var nullable = string.Equals(inputs.Property("Nullable"), "enable", StringComparison.OrdinalIgnoreCase)
        ? NullableContextOptions.Enable
        : NullableContextOptions.Disable;

    var allowUnsafe = string.Equals(inputs.Property("AllowUnsafeBlocks"), "true", StringComparison.OrdinalIgnoreCase);

    var compilation = CSharpCompilation.Create(
        Path.GetFileNameWithoutExtension(projectPath),
        trees,
        references
            .Where(i => !dropProjectRefs || !IsRepoBinOutput(i))
            .Select(i => i.TryGetValue("FullPath", out var p) ? p : null)
            .Where(p => p is not null && File.Exists(p))
            .Select(p => MetadataReference.CreateFromFile(p!)),
        new CSharpCompilationOptions(outputKind)
            .WithNullableContextOptions(nullable)
            .WithAllowUnsafe(allowUnsafe));

    // --- The measurement ----------------------------------------------------
    var diagnostics = compilation.GetDiagnostics();
    var errors = diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ToList();

    Console.WriteLine($"    ERRORS        : {errors.Count}");

    foreach (var group in errors.GroupBy(e => e.Id).OrderByDescending(g => g.Count()).Take(8))
    {
        var sample = group.First();
        Console.WriteLine($"      {group.Key} x{group.Count(),-5} {Truncate(sample.GetMessage(), 90)}");
        Console.WriteLine($"         first at {Where(sample)}");
    }

    Console.WriteLine();
    return errors.Count == 0;
}

static bool IsRepoBinOutput(Dictionary<string, string> item) =>
    item.TryGetValue("FullPath", out var p)
    && (p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
        || p.Contains("/bin/", StringComparison.Ordinal));

static bool IsGenerated(Dictionary<string, string> item) =>
    item.TryGetValue("FullPath", out var p)
    && (p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
        || p.Contains("/obj/", StringComparison.Ordinal));

static string Where(Diagnostic d)
{
    var span = d.Location.GetLineSpan();
    return span.Path.Length == 0
        ? "(no location)"
        : $"{Path.GetFileName(span.Path)}:{span.StartLinePosition.Line + 1}";
}

static string Truncate(string s, int max) =>
    s.Length <= max ? s : s[..max] + "…";

/// <summary>
/// Runs one <c>dotnet msbuild</c> query and returns its items and properties.
///
/// The target list matters and was found empirically: <c>ResolveReferences</c>
/// alone yields the references but NOT the generated sources, and this repo
/// enables <c>ImplicitUsings</c>, so without <c>GenerateGlobalUsings</c> every
/// file that relies on an implicit global using fails to compile.
/// </summary>
static MsBuildInputs QueryMsBuild(string projectPath)
{
    var psi = new ProcessStartInfo("dotnet")
    {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        WorkingDirectory = Path.GetDirectoryName(projectPath)!,
    };

    foreach (var a in new[]
             {
                 "msbuild", projectPath,
                 "-t:ResolveReferences", "-t:GenerateGlobalUsings", "-t:GenerateAssemblyInfo",
                 "-getItem:ReferencePath", "-getItem:Compile",
                 "-getProperty:DefineConstants", "-getProperty:LangVersion",
                 "-getProperty:Nullable", "-getProperty:AllowUnsafeBlocks",
                 "-getProperty:TargetFramework", "-getProperty:OutputType",
             })
    {
        psi.ArgumentList.Add(a);
    }

    using var process = Process.Start(psi)
        ?? throw new InvalidOperationException("could not start dotnet msbuild");

    var stdout = process.StandardOutput.ReadToEnd();
    var stderr = process.StandardError.ReadToEnd();
    process.WaitForExit();

    if (process.ExitCode != 0)
    {
        throw new InvalidOperationException(
            $"dotnet msbuild exited {process.ExitCode}: {Truncate(stderr.Length > 0 ? stderr : stdout, 400)}");
    }

    using var doc = JsonDocument.Parse(stdout);
    var root = doc.RootElement;

    var properties = new Dictionary<string, string>(StringComparer.Ordinal);
    if (root.TryGetProperty("Properties", out var props))
    {
        foreach (var p in props.EnumerateObject())
        {
            properties[p.Name] = p.Value.GetString() ?? string.Empty;
        }
    }

    var items = new Dictionary<string, List<Dictionary<string, string>>>(StringComparer.Ordinal);
    if (root.TryGetProperty("Items", out var itemsElement))
    {
        foreach (var group in itemsElement.EnumerateObject())
        {
            var list = new List<Dictionary<string, string>>();
            foreach (var entry in group.Value.EnumerateArray())
            {
                var metadata = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var m in entry.EnumerateObject())
                {
                    metadata[m.Name] = m.Value.GetString() ?? string.Empty;
                }

                list.Add(metadata);
            }

            items[group.Name] = list;
        }
    }

    return new MsBuildInputs(properties, items);
}

internal sealed record MsBuildInputs(
    Dictionary<string, string> Properties,
    Dictionary<string, List<Dictionary<string, string>>> Items)
{
    public string? Property(string name) =>
        Properties.TryGetValue(name, out var v) && v.Length > 0 ? v : null;
}
