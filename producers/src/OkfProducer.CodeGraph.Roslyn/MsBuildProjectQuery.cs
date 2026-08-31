// SPDX-License-Identifier: LGPL-3.0-or-later
using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;

namespace OkfProducer.CodeGraph.Roslyn;

/// <summary>
/// Thrown when <see cref="MsBuildProjectQuery.Query"/> cannot obtain a project's inputs -- the
/// <c>dotnet</c> CLI is absent, the project has not been restored, MSBuild exited non-zero, or its
/// output was not the JSON document <c>-getItem</c>/<c>-getProperty</c> promise.
///
/// <para>
/// A distinct exception type rather than a bare <see cref="InvalidOperationException"/> because
/// <see cref="RoslynResolver"/> treats the two completely differently: this one is the degradation
/// path (report the project unavailable, let the name-matching baseline stand), whereas the
/// <see cref="InvalidOperationException"/> <see cref="CompilationFactory.Create"/> throws for an
/// unknown <c>LangVersion</c> is the loud-failure path and is deliberately not caught anywhere.
/// </para>
/// </summary>
public sealed class MsBuildQueryException : Exception
{
    /// <summary>Creates the exception with a message describing what the query could not do.</summary>
    public MsBuildQueryException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception wrapping the underlying failure.</summary>
    public MsBuildQueryException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Reads one project's compiler inputs straight out of MSBuild, with no
/// <c>Microsoft.CodeAnalysis.Workspaces.MSBuild</c> anywhere: <c>dotnet msbuild</c>'s own
/// <c>-getItem</c>/<c>-getProperty</c> switches print exactly the item and property values a target
/// run produced, as JSON, which is all a <c>CSharpCompilation</c> needs.
/// </summary>
public static class MsBuildProjectQuery
{
    /// <summary>
    /// How long one <c>dotnet msbuild</c> invocation may take before it is killed and the project
    /// reported unavailable. The spike measured 533-1194 ms per project on a warm SDK; two minutes is
    /// slack for a cold first run (which JITs the SDK's MSBuild) without letting a wedged process hang
    /// a producer run until <c>ExtractionLimits.Timeout</c>.
    /// </summary>
    private static readonly TimeSpan QueryTimeout = TimeSpan.FromMinutes(2);

    /// <summary>
    /// The exact target list the spike validated, and the reason it is longer than the obvious one.
    /// <c>ResolveReferences</c> alone yields the <c>ReferencePath</c> items but leaves the
    /// <c>Compile</c> set missing the SDK's own generated sources, <c>*.GlobalUsings.g.cs</c> and
    /// <c>*.AssemblyInfo.cs</c>. <c>ImplicitUsings</c> is on by default for SDK-style projects, so
    /// without <c>GenerateGlobalUsings</c> every file relying on an implicit global using fails to
    /// compile -- and a compilation with errors has an incomplete symbol table, which mis-attributes
    /// calls rather than merely missing them. Measured, not reasoned: adding these two targets is
    /// what took the spike's three probe projects to zero errors.
    /// </summary>
    private static readonly string[] Targets =
    [
        "-t:ResolveReferences", "-t:GenerateGlobalUsings", "-t:GenerateAssemblyInfo",
    ];

    private static readonly string[] Items =
    [
        "-getItem:ReferencePath", "-getItem:Compile",
    ];

    private static readonly string[] Properties =
    [
        "-getProperty:DefineConstants", "-getProperty:LangVersion",
        "-getProperty:Nullable", "-getProperty:AllowUnsafeBlocks",
        "-getProperty:TargetFramework", "-getProperty:OutputType",
        "-getProperty:AssemblyName",
    ];

    /// <summary>
    /// Runs the MSBuild query for <paramref name="projectPath"/> and returns its inputs.
    ///
    /// <para>
    /// A multi-targeting project is queried for one target framework: its outer build has no
    /// <c>ResolveReferences</c> target at all (MSBuild answers <c>MSB4057</c>) and no single reference
    /// set to report, so the first framework its <c>TargetFrameworks</c> lists is selected and the
    /// query re-run against that. First-listed rather than newest is a deliberate choice of a rule
    /// that is stable and readable from the project file itself: "newest" would have this producer
    /// silently change which symbols exist whenever a TFM is added.
    /// </para>
    /// </summary>
    /// <param name="projectPath">Path to a <c>.csproj</c>; relative paths are made absolute.</param>
    /// <exception cref="MsBuildQueryException">
    /// <c>dotnet</c> could not be started, the query did not finish within its timeout, MSBuild
    /// exited non-zero (an unrestored project is the common case), or its output was not parseable
    /// JSON. Every one of these means "this project's inputs are unknown", never "this project has no
    /// references" -- the caller must degrade, not compile from a half-answer.
    /// </exception>
    public static ProjectInputs Query(string projectPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(projectPath);

        var fullPath = Path.GetFullPath(projectPath);

        string json;
        try
        {
            json = RunQuery(fullPath, targetFramework: null);
        }
        catch (MsBuildQueryException)
        {
            // Optimistic first, probe only on failure: asking every project for TargetFrameworks up
            // front would add an MSBuild round trip per project to spare the rare multi-targeting one.
            // An unrestored project fails here too and its probe comes back null, so the original --
            // and far more useful -- failure is what propagates.
            var framework = FirstTargetFramework(fullPath);
            if (framework is null)
            {
                throw;
            }

            json = RunQuery(fullPath, framework);
        }

        using var document = ParseJson(fullPath, json);
        var root = document.RootElement;

        var properties = ReadProperties(root);
        var items = ReadItems(root);

        return new ProjectInputs(
            fullPath,
            Property(properties, "AssemblyName") ?? Path.GetFileNameWithoutExtension(fullPath),
            ReadCompileFiles(items),
            ReadReferences(items),
            Property(properties, "DefineConstants") ?? string.Empty,
            Property(properties, "LangVersion") ?? string.Empty,
            string.Equals(Property(properties, "Nullable"), "enable", StringComparison.OrdinalIgnoreCase),
            string.Equals(Property(properties, "AllowUnsafeBlocks"), "true", StringComparison.OrdinalIgnoreCase),
            Property(properties, "OutputType") ?? "Library",
            Property(properties, "TargetFramework") ?? string.Empty);
    }

    /// <summary>
    /// The first framework a multi-targeting project lists, or <see langword="null"/> when the project
    /// is not multi-targeting (or cannot be evaluated at all, in which case the caller's original
    /// failure is the one worth reporting). Evaluation only -- no targets run -- so this is the cheap
    /// probe, taken only after the full query has already failed.
    /// </summary>
    private static string? FirstTargetFramework(string projectPath)
    {
        string json;
        try
        {
            json = Run(projectPath, ["-getProperty:TargetFrameworks", "-getProperty:TargetFramework"]);
        }
        catch (MsBuildQueryException)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var properties = ReadProperties(document.RootElement);

            // A single-targeting project reports TargetFramework and (usually) no TargetFrameworks;
            // only the outer build of a multi-targeting one reports the plural with the singular empty.
            if (Property(properties, "TargetFramework") is not null)
            {
                return null;
            }

            return (Property(properties, "TargetFrameworks") ?? string.Empty)
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string RunQuery(string projectPath, string? targetFramework)
    {
        var arguments = new List<string>();
        if (targetFramework is not null)
        {
            arguments.Add($"-p:TargetFramework={targetFramework}");
        }

        arguments.AddRange(Targets);
        arguments.AddRange(Items);
        arguments.AddRange(Properties);

        return Run(projectPath, arguments);
    }

    private static string Run(string projectPath, IReadOnlyList<string> arguments)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // MSBuild resolves Directory.Build.props/targets from the project's own directory, so run
            // there rather than wherever the producer happened to be invoked from.
            WorkingDirectory = Path.GetDirectoryName(projectPath)!,
        };

        startInfo.ArgumentList.Add("msbuild");
        startInfo.ArgumentList.Add(projectPath);
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        Process process;
        try
        {
            process = Process.Start(startInfo)
                ?? throw new MsBuildQueryException($"could not start `dotnet msbuild` for {projectPath}.");
        }
        catch (Win32Exception e)
        {
            // The "MSBuild absent" degradation path from the brief: no dotnet on PATH at all.
            throw new MsBuildQueryException(
                $"could not start `dotnet msbuild` for {projectPath}: the dotnet CLI was not found.", e);
        }

        using (process)
        {
            // Both streams are drained concurrently, never one ReadToEnd() after the other: MSBuild
            // writes enough to fill a pipe buffer, and a sequential read deadlocks the moment the
            // stream being read second fills up while the process blocks writing to it.
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit((int)QueryTimeout.TotalMilliseconds))
            {
                TryKill(process);
                throw new MsBuildQueryException(
                    $"`dotnet msbuild` for {projectPath} did not finish within {QueryTimeout.TotalSeconds:0} s.");
            }

            var stdout = stdoutTask.GetAwaiter().GetResult();
            var stderr = stderrTask.GetAwaiter().GetResult();

            if (process.ExitCode != 0)
            {
                throw new MsBuildQueryException(
                    $"`dotnet msbuild` for {projectPath} exited {process.ExitCode}. "
                    + $"A project that has not been restored fails here. {Truncate(stderr.Length > 0 ? stderr : stdout, 400)}");
            }

            return stdout;
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // Already exited between the timeout and here; nothing to kill.
        }
        catch (Win32Exception)
        {
            // Access denied killing the tree; the process is left to the OS rather than failing the run twice.
        }
    }

    private static JsonDocument ParseJson(string projectPath, string json)
    {
        try
        {
            return JsonDocument.Parse(json);
        }
        catch (JsonException e)
        {
            throw new MsBuildQueryException(
                $"`dotnet msbuild` for {projectPath} did not print the JSON that -getItem/-getProperty promise: {Truncate(json, 400)}", e);
        }
    }

    private static Dictionary<string, string> ReadProperties(JsonElement root)
    {
        // A lookup only -- Property() indexes it, nothing ever iterates it into output.
        var properties = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!root.TryGetProperty("Properties", out var element))
        {
            return properties;
        }

        foreach (var property in element.EnumerateObject())
        {
            properties[property.Name] = property.Value.GetString() ?? string.Empty;
        }

        return properties;
    }

    private static Dictionary<string, List<Dictionary<string, string>>> ReadItems(JsonElement root)
    {
        var items = new Dictionary<string, List<Dictionary<string, string>>>(StringComparer.Ordinal);
        if (!root.TryGetProperty("Items", out var element))
        {
            return items;
        }

        foreach (var group in element.EnumerateObject())
        {
            var entries = new List<Dictionary<string, string>>();
            foreach (var entry in group.Value.EnumerateArray())
            {
                var metadata = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var value in entry.EnumerateObject())
                {
                    metadata[value.Name] = value.Value.GetString() ?? string.Empty;
                }

                entries.Add(metadata);
            }

            items[group.Name] = entries;
        }

        return items;
    }

    /// <summary>
    /// The <c>Compile</c> items' absolute paths, in MSBuild's own order, with duplicates removed. An
    /// item can legitimately appear twice (a glob plus an explicit include), and handing Roslyn the
    /// same file twice is a compile error (CS0101 on every type it declares), not a harmless repeat.
    /// </summary>
    private static List<string> ReadCompileFiles(Dictionary<string, List<Dictionary<string, string>>> items)
    {
        var files = new List<string>();
        var seen = new HashSet<string>(PathComparer);

        foreach (var item in Group(items, "Compile"))
        {
            if (item.TryGetValue("FullPath", out var path) && path.Length > 0 && seen.Add(path))
            {
                files.Add(path);
            }
        }

        return files;
    }

    /// <summary>
    /// The <c>ReferencePath</c> items, each tagged with the <c>.csproj</c> it is the output of when
    /// MSBuild says so. <c>MSBuildSourceProjectFile</c> is present exactly on the items
    /// <c>ResolveProjectReferences</c> contributed (marked <c>ReferenceSourceTarget=ProjectReference</c>),
    /// including transitive ones, which is what lets <see cref="CompilationFactory"/> replace each with
    /// a from-source <c>CompilationReference</c> instead of the <c>bin/</c> assembly that only exists
    /// after a build.
    /// </summary>
    private static List<ProjectReferenceInput> ReadReferences(Dictionary<string, List<Dictionary<string, string>>> items)
    {
        var references = new List<ProjectReferenceInput>();
        var seen = new HashSet<string>(PathComparer);

        foreach (var item in Group(items, "ReferencePath"))
        {
            if (!item.TryGetValue("FullPath", out var path) || path.Length == 0 || !seen.Add(path))
            {
                continue;
            }

            var isProjectOutput = item.TryGetValue("ReferenceSourceTarget", out var source)
                && string.Equals(source, "ProjectReference", StringComparison.Ordinal);
            var projectFile = isProjectOutput
                && item.TryGetValue("MSBuildSourceProjectFile", out var project)
                && project.Length > 0
                    ? Path.GetFullPath(project)
                    : null;

            references.Add(new ProjectReferenceInput(path, projectFile));
        }

        return references;
    }

    private static List<Dictionary<string, string>> Group(
        Dictionary<string, List<Dictionary<string, string>>> items, string name) =>
        items.TryGetValue(name, out var group) ? group : [];

    private static string? Property(Dictionary<string, string> properties, string name) =>
        properties.TryGetValue(name, out var value) && value.Length > 0 ? value : null;

    /// <summary>
    /// <see cref="StringComparer.Ordinal"/>, one rule for every path comparison in this producer
    /// (6.2), including this de-duplication of MSBuild's own item lists. Both spellings here come out
    /// of a single MSBuild evaluation, which already de-duplicates its item lists, so this is a
    /// belt-and-braces pass against handing Roslyn one file twice (CS0101 on every type it declares).
    /// An ordinal comparison could in principle let one through under two casings on Windows -- and
    /// that lands as a compilation error, reported and degraded cleanly, not as a silently wrong edge.
    /// A case-insensitive one would instead drop a genuinely distinct file on a case-sensitive
    /// filesystem, which is the worse of the two, and it would reintroduce the exact divergence
    /// <c>FileEligibility</c> was deliberately moved off <see cref="StringComparer.OrdinalIgnoreCase"/>
    /// to close.
    /// </summary>
    private static StringComparer PathComparer => StringComparer.Ordinal;

    private static string Truncate(string text, int max)
    {
        var collapsed = text.ReplaceLineEndings(" ").Trim();
        return collapsed.Length <= max ? collapsed : collapsed[..max] + "...";
    }
}
