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
/// A distinct exception type rather than a bare <see cref="InvalidOperationException"/> so
/// <see cref="RoslynResolver"/> can catch exactly this, rather than catching every
/// <see cref="InvalidOperationException"/> anything downstream might raise and quietly turning a
/// genuine bug into a "project unavailable" line. Both this and
/// <see cref="UnknownLanguageVersionException"/> lead to the same outcome -- the project is reported
/// unavailable and the name-matching baseline carries it -- but they are told apart so the report can
/// name the actual cause, and so neither catch can swallow the other's failures.
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
///
/// <para><b>THREAT MODEL: this class executes the scanned repository's code.</b> Not "may", and not
/// only under some option -- an MSBuild evaluation <i>is</i> the execution of repository-authored
/// logic, and there is no read-only mode of it to ask for. Everything the project reaches runs, as the
/// user running <c>okfgen</c>: <c>Directory.Build.props</c> and <c>Directory.Build.targets</c> (found
/// from the project's own directory, which is why <see cref="Run"/> sets
/// <c>WorkingDirectory</c> there), every <c>Import</c> they pull in, any target hooked on
/// <c>BeforeTargets="ResolveReferences"</c>, and a <c>RoslynCodeTaskFactory</c> inline
/// <c>&lt;Code&gt;</c> task, which is a C# compiler invocation on source the repository supplies. The
/// three targets in <see cref="Targets"/> bound what is <i>asked for</i>; they bound nothing about
/// what evaluation runs on the way.</para>
///
/// <para>
/// So <b>only point <c>okfgen</c> at a repository you would be willing to build</b>. That is the whole
/// mitigation, stated rather than implied, and it is why <c>okfgen generate --no-msbuild</c> exists:
/// it skips this stage entirely, spawns nothing, and leaves call resolution to the name-matching
/// baseline. It is off by default because turning it on by default would silently degrade every
/// existing run's resolution quality -- a documented hazard with a lever beats a quiet downgrade.
/// </para>
///
/// <para>
/// Nothing else in this producer executes scanned input: the tree-sitter extractor parses, and
/// <see cref="CompilationFactory"/> compiles without running generators (see
/// <see cref="RoslynResolver"/>'s remarks, whose refusal is about not widening this further rather
/// than about holding a line that this class had already crossed).
/// </para>
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

        return ReadInputs(fullPath, json);
    }

    /// <summary>
    /// Turns one MSBuild answer into <see cref="ProjectInputs"/>.
    ///
    /// <para>
    /// <see langword="internal"/> rather than folded into <see cref="Query"/> so the malformed-answer
    /// branches have an executable test at all: every other route to them spawns a real
    /// <c>dotnet msbuild</c>, which cannot be made to print a document of the wrong shape. Those
    /// branches are exactly where an <see cref="InvalidOperationException"/> used to escape
    /// <see cref="MsBuildQueryException"/> and abort the whole run.
    /// </para>
    /// </summary>
    /// <exception cref="MsBuildQueryException">
    /// <paramref name="json"/> is not the JSON object, with the item and property groups in the shapes,
    /// that <c>-getItem</c>/<c>-getProperty</c> promise.
    /// </exception>
    internal static ProjectInputs ReadInputs(string projectPath, string json)
    {
        using var document = ParseJson(projectPath, json);
        var root = document.RootElement;

        var properties = ReadProperties(projectPath, root);
        var items = ReadItems(projectPath, root);

        return new ProjectInputs(
            projectPath,
            Property(properties, "AssemblyName") ?? Path.GetFileNameWithoutExtension(projectPath),
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
            // Through ParseJson, not JsonDocument.Parse: the shape check lives there, and a probe that
            // parsed the document its own way would be the one route into ReadProperties that had not
            // been through it -- the shape on which TryGetProperty throws rather than returning false.
            using var document = ParseJson(projectPath, json);
            var properties = ReadProperties(projectPath, document.RootElement);

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
        catch (MsBuildQueryException)
        {
            // This is a probe taken only after the full query already failed, so its own failure is
            // never the interesting one: returning null re-throws the caller's original error, which
            // says what actually went wrong with the project.
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
            // there rather than wherever the producer happened to be invoked from. Note what that
            // sentence means: those files are then FOUND, and being found means being evaluated, and
            // being evaluated means running. See this class's threat-model paragraph -- the choice
            // here is between evaluating the project correctly and evaluating it wrongly, not between
            // running repository logic and not running it.
            WorkingDirectory = Path.GetDirectoryName(projectPath)!,
        };

        startInfo.ArgumentList.Add("msbuild");
        startInfo.ArgumentList.Add(projectPath);

        // Node reuse is on by default, and it is wrong for this caller twice over. It leaves worker
        // processes alive after the build -- N of them per producer run, which a tool meant for CI has
        // no business doing -- and those workers INHERIT the redirected pipes, so the readers below can
        // stay open long after the msbuild process itself has exited, hanging a call whose process-level
        // timeout has already been satisfied.
        startInfo.ArgumentList.Add("-nodeReuse:false");

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
            // One token bounds the WHOLE call, not just the process. Process.WaitForExit(int) waits for
            // the process to exit but not for the redirected readers to finish, so reading them
            // afterwards is an unbounded block sitting just past a timeout that has already been
            // honoured -- and anything else holding the write end of those pipes (an inherited MSBuild
            // worker; see -nodeReuse:false above) keeps them open with no escape. Cancelling the reads
            // as well as the wait closes that gap.
            using var timeout = new CancellationTokenSource(QueryTimeout);

            // Both streams are drained concurrently, never one ReadToEnd() after the other: MSBuild
            // writes enough to fill a pipe buffer, and a sequential read deadlocks the moment the
            // stream being read second fills up while the process blocks writing to it.
            var stdoutTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(timeout.Token);

            string stdout;
            string stderr;
            int exitCode;
            try
            {
                process.WaitForExitAsync(timeout.Token).GetAwaiter().GetResult();
                stdout = stdoutTask.GetAwaiter().GetResult();
                stderr = stderrTask.GetAwaiter().GetResult();
                exitCode = process.ExitCode;
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
                throw new MsBuildQueryException(
                    $"`dotnet msbuild` for {projectPath} did not finish within {QueryTimeout.TotalSeconds:0} s.");
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException)
            {
                // The SUCCESS path has its own failure mode, and it is the one that used to escape raw.
                // WaitForExitAsync can return normally and a subsequent read still throw -- a pipe torn
                // down abnormally by a killed or crashed msbuild, by a scanner holding the handle, or by
                // a worker going away mid-write -- and process.ExitCode throws InvalidOperationException
                // if the process object is not in the state that read requires. Every one of those means
                // exactly what a non-zero exit means: this project's inputs are unknown. Left unwrapped
                // they escaped RoslynResolver.QueryProjectClosure's deliberately narrow
                // `catch (MsBuildQueryException)`, so a single project's abnormal msbuild aborted
                // generation for the WHOLE repository instead of degrading that one project to the
                // name-matching baseline.
                TryKill(process);
                throw new MsBuildQueryException(
                    $"`dotnet msbuild` for {projectPath} ended abnormally while its output was being read: {e.Message}", e);
            }

            if (exitCode != 0)
            {
                throw new MsBuildQueryException(
                    $"`dotnet msbuild` for {projectPath} exited {exitCode}. "
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

    /// <summary>
    /// Parses MSBuild's answer, and checks it is the <i>object</i> <c>-getItem</c>/<c>-getProperty</c>
    /// promise rather than merely well-formed JSON.
    ///
    /// <para>
    /// Syntax was never the only way that answer can be wrong. <c>JsonDocument.Parse</c> accepts
    /// <c>"[]"</c>, <c>"7"</c> and <c>"null"</c> quite happily, and every reader below then meets a
    /// <see cref="JsonElement"/> of the wrong kind -- on which <c>TryGetProperty</c>,
    /// <c>EnumerateObject</c> and <c>EnumerateArray</c> all throw
    /// <see cref="InvalidOperationException"/>, a type nothing upstream catches. The shape check
    /// belongs here, once, so that every route into the readers has been through it.
    /// </para>
    /// </summary>
    private static JsonDocument ParseJson(string projectPath, string json)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException e)
        {
            throw new MsBuildQueryException(
                $"`dotnet msbuild` for {projectPath} did not print the JSON that -getItem/-getProperty promise: {Truncate(json, 400)}", e);
        }

        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            document.Dispose();
            throw new MsBuildQueryException(
                $"`dotnet msbuild` for {projectPath} printed valid JSON that is not an object "
                + $"({Truncate(json, 400)}); -getItem/-getProperty promise an object.");
        }

        return document;
    }

    private static Dictionary<string, string> ReadProperties(string projectPath, JsonElement root)
    {
        // A lookup only -- Property() indexes it, nothing ever iterates it into output.
        var properties = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!TryGetGroup(projectPath, root, "Properties", JsonValueKind.Object, out var element))
        {
            return properties;
        }

        foreach (var property in element.EnumerateObject())
        {
            // A non-string value is read as absent rather than crashing the run: Property() already
            // treats an empty value as "not set", which is the same answer, and every property this
            // query asks for is a string when MSBuild answers at all.
            properties[property.Name] = property.Value.ValueKind == JsonValueKind.String
                ? property.Value.GetString() ?? string.Empty
                : string.Empty;
        }

        return properties;
    }

    private static Dictionary<string, List<Dictionary<string, string>>> ReadItems(string projectPath, JsonElement root)
    {
        var items = new Dictionary<string, List<Dictionary<string, string>>>(StringComparer.Ordinal);
        if (!TryGetGroup(projectPath, root, "Items", JsonValueKind.Object, out var element))
        {
            return items;
        }

        foreach (var group in element.EnumerateObject())
        {
            if (group.Value.ValueKind != JsonValueKind.Array)
            {
                // Refused, not skipped. An item group this reader cannot read means the item set is
                // unknown, and an empty list would be read downstream as "this project has no Compile
                // items" -- a half-answer, which is exactly what Query's contract forbids.
                throw new MsBuildQueryException(
                    $"`dotnet msbuild` for {projectPath} printed item group `{group.Name}` as "
                    + $"{group.Value.ValueKind} rather than an array.");
            }

            var entries = new List<Dictionary<string, string>>();
            foreach (var entry in group.Value.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object)
                {
                    throw new MsBuildQueryException(
                        $"`dotnet msbuild` for {projectPath} printed an entry of item group `{group.Name}` as "
                        + $"{entry.ValueKind} rather than an object.");
                }

                var metadata = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var value in entry.EnumerateObject())
                {
                    metadata[value.Name] = value.Value.ValueKind == JsonValueKind.String
                        ? value.Value.GetString() ?? string.Empty
                        : string.Empty;
                }

                entries.Add(metadata);
            }

            items[group.Name] = entries;
        }

        return items;
    }

    /// <summary>
    /// The named top-level group, or <see langword="false"/> when MSBuild did not print one at all --
    /// which is legitimate (nothing matched) and reads as empty. A group that is present but of the
    /// wrong kind is not: that is an answer this reader cannot read, and an empty result there would
    /// be indistinguishable from "no references", which the caller is told never to conclude.
    /// </summary>
    private static bool TryGetGroup(
        string projectPath, JsonElement root, string name, JsonValueKind expected, out JsonElement element)
    {
        if (!root.TryGetProperty(name, out element))
        {
            return false;
        }

        if (element.ValueKind != expected)
        {
            throw new MsBuildQueryException(
                $"`dotnet msbuild` for {projectPath} printed `{name}` as {element.ValueKind} rather than {expected}.");
        }

        return true;
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
