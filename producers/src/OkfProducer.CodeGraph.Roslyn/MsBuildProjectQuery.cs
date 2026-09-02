// SPDX-License-Identifier: LGPL-3.0-or-later
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
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
/// <c>&lt;Code&gt;</c> task, which is a C# compiler invocation on source the repository supplies.</para>
///
/// <para><b>And the repository adds to the request itself, not merely to what runs on the way.</b>
/// This paragraph used to say the three targets in <see cref="Targets"/> bound what is <i>asked
/// for</i>. They do not. <c>dotnet msbuild</c> auto-applies a <c>Directory.Build.rsp</c> found in the
/// project's directory -- the directory <see cref="Run"/> deliberately runs in -- and what that file
/// holds is command-line switches. Measured on this host: a one-line <c>Directory.Build.rsp</c>
/// containing <c>-t:Pwn</c> made this exact query run a <c>Pwn</c> target the producer never
/// requested, which wrote its marker file, alongside the producer's own <c>-t:ResolveReferences</c>.
/// A repository can therefore turn this query into <c>-t:Build</c>, or add any other switch. Two
/// things were measured to still hold: an explicit command-line switch wins on conflict, so the
/// producer's own <c>-nodeReuse:false</c> cannot be flipped from the rsp; and the mitigation below is
/// unchanged, because it never rested on the target list. It was the enumerated <i>bound</i> that was
/// untrue, not the conclusion.</para>
///
/// <para>
/// So <b>only point <c>okfgen</c> at a repository you would be willing to build</b>. That is the whole
/// mitigation, stated rather than implied, and it is why <c>okfgen generate --no-msbuild</c> exists:
/// it skips this stage entirely, spawns no <c>dotnet msbuild</c>, and leaves call resolution to the
/// name-matching baseline. It is off by default because turning it on by default would silently
/// degrade every existing run's resolution quality -- a documented hazard with a lever beats a quiet
/// downgrade.
/// </para>
///
/// <para>
/// Nothing else in this producer <i>evaluates</i> scanned input: the tree-sitter extractor parses, and
/// <see cref="CompilationFactory"/> compiles without running generators (see
/// <see cref="RoslynResolver"/>'s remarks, whose refusal is about not widening this further rather
/// than about holding a line that this class had already crossed). It is not the only place a child
/// process runs, though, and <c>--no-msbuild</c> does not make it so: <c>GitRevision.RunGit</c> spawns
/// <c>git</c> three times per generate run, in the scanned tree, on every run including that one. Far
/// less exposure -- none of the three triggers a hook, an fsmonitor, or a pager with stdout redirected
/// -- but "no process is spawned" is false and <c>producers/README.md</c> no longer says it.
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
            ReadReferences(projectPath, items),
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
        // Guards Process.Start against a working directory that is not there -- a project directory
        // that vanished between the scan and this query, or a caller naming a project that never
        // existed. The codebase's own precedent is GitRevision.RunGit, which checks the same thing for
        // the same reason. Measured on this host (.NET 10 / Windows 11): Process.Start with a missing
        // WorkingDirectory throws Win32Exception, which the catch below ALREADY converted -- so this is
        // not a new escape being closed, it is a wrong DIAGNOSIS being fixed. That catch reports "the
        // dotnet CLI was not found", which sends an operator hunting for an SDK that is installed.
        var workingDirectory = Path.GetDirectoryName(projectPath);
        if (workingDirectory is null || !Directory.Exists(workingDirectory))
        {
            throw new MsBuildQueryException(
                $"could not start `dotnet msbuild` for {projectPath}: its directory does not exist.");
        }

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
            WorkingDirectory = workingDirectory,
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
            //
            // Capped, not ReadToEndAsync: what msbuild prints on stdout is repository-controlled, and
            // an unbounded read of it is an OutOfMemoryException a scanned repository can ask for. See
            // ReadCappedAsync for the measurement that sets the caps -- and for the mechanism that does
            // NOT do it, since the obvious guess is wrong.
            var stdoutTask = ReadCappedAsync(process.StandardOutput, MaxStdoutChars, timeout.Token);
            var stderrTask = ReadCappedAsync(process.StandardError, MaxStderrChars, timeout.Token);

            CappedRead stdoutRead;
            CappedRead stderrRead;
            int exitCode;
            try
            {
                process.WaitForExitAsync(timeout.Token).GetAwaiter().GetResult();
                stdoutRead = stdoutTask.GetAwaiter().GetResult();
                stderrRead = stderrTask.GetAwaiter().GetResult();
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
                var detail = stderrRead.Text.Length > 0 ? stderrRead.Text : stdoutRead.Text;
                throw new MsBuildQueryException(
                    $"`dotnet msbuild` for {projectPath} exited {exitCode}. "
                    + $"A project that has not been restored fails here. {Truncate(detail, 400)}");
            }

            if (stdoutRead.Overflowed)
            {
                // After the exit code, deliberately: a project that ALSO failed is better described by
                // its own error than by "it printed too much".
                throw new MsBuildQueryException(
                    $"`dotnet msbuild` for {projectPath} printed more than {MaxStdoutChars / (1024 * 1024)} MiB "
                    + "on stdout, which no legitimate -getItem/-getProperty answer approaches. The answer was "
                    + "not read.");
            }

            return stdoutRead.Text;
        }
    }

    /// <summary>
    /// How much of msbuild's stdout is kept before the answer is refused. A real answer is nowhere
    /// near this: measured on this host, <c>src/OKF4net.Mcp/OKF4net.Mcp.csproj</c> -- a restored
    /// project with over a hundred resolved references -- answers in <b>456,364 bytes</b>, and a
    /// one-file scratch project in <b>344,326</b>. 32 MiB is ~70x the larger of those.
    /// </summary>
    private const int MaxStdoutChars = 32 * 1024 * 1024;

    /// <summary>
    /// How much of msbuild's stderr is kept. Far smaller, because the only use it is ever put to is
    /// <c>Truncate(stderr, 400)</c> inside a failure message.
    /// </summary>
    private const int MaxStderrChars = 1024 * 1024;

    /// <summary>What one capped stream read produced.</summary>
    /// <param name="Text">The characters kept, up to the cap.</param>
    /// <param name="Overflowed">Whether the stream held more than the cap and the rest was discarded.</param>
    private readonly record struct CappedRead(string Text, bool Overflowed);

    /// <summary>
    /// Reads <paramref name="reader"/> to the end, keeping at most <paramref name="maxChars"/>
    /// characters and discarding -- but still draining -- anything past that.
    ///
    /// <para>
    /// <b>Draining past the cap is the point, not a detail.</b> Simply stopping would leave the child
    /// blocked on a full pipe until the two-minute timeout killed it; discarding keeps the process
    /// moving to its own exit while the producer's memory stays bounded.
    /// </para>
    ///
    /// <para>
    /// <b>What makes this reachable, measured -- and it is not the obvious mechanism.</b> The escape
    /// register offered <c>-v:diag</c> injected through a repository's <c>Directory.Build.rsp</c>. That
    /// does not reproduce: in <c>-getItem</c>/<c>-getProperty</c> mode the console log is suppressed
    /// entirely, and on this host a query run with <c>-v:diag</c> in the rsp, and one run with a
    /// <c>Directory.Build.targets</c> emitting three 100 KB high-importance <c>&lt;Message&gt;</c>
    /// lines, each printed <b>344,326 bytes</b> -- byte-identical to the clean run. What does
    /// reproduce is the JSON itself, whose size the repository controls: a fifteen-line
    /// <c>Directory.Build.targets</c> declaring 10,000 <c>Compile</c> items took the same query from
    /// 344,326 bytes to <b>10,457,323 bytes in 1.1 s</b>. One more doubling level in that file is
    /// ~100 MB, and it costs the repository nothing.
    /// </para>
    /// </summary>
    private static async Task<CappedRead> ReadCappedAsync(StreamReader reader, int maxChars, CancellationToken token)
    {
        var buffer = new char[8192];
        var kept = new StringBuilder();
        var overflowed = false;

        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            var room = maxChars - kept.Length;
            if (room >= read)
            {
                kept.Append(buffer, 0, read);
                continue;
            }

            if (room > 0)
            {
                kept.Append(buffer, 0, room);
            }

            overflowed = true;
        }

        return new CappedRead(kept.ToString(), overflowed);
    }

    /// <summary>
    /// Kills the msbuild process and its workers, or gives up quietly.
    ///
    /// <para>
    /// Both call sites are <i>inside</i> a <c>catch</c> that is about to throw an
    /// <see cref="MsBuildQueryException"/>, so anything escaping here replaces the wrapped, per-project
    /// failure with a raw one that <c>RoslynResolver.QueryProjectClosure</c> does not catch -- the
    /// whole-run abort this class keeps being fixed for. <see cref="Process.Kill(bool)"/> with
    /// <c>entireProcessTree: true</c> is documented to throw <see cref="AggregateException"/> when part
    /// of the tree could not be killed, which the two catches below did not cover.
    /// </para>
    ///
    /// <para>
    /// NOT MEASURED: read-verified against the documented contract only. Arranging a process tree whose
    /// partial kill fails is not something a test can do deterministically on this host, so no
    /// executable test reaches the <see cref="AggregateException"/> branch.
    /// </para>
    /// </summary>
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
        catch (AggregateException)
        {
            // Part of the tree survived. Same answer as access denied: the survivors are left to the OS,
            // and the caller's own MsBuildQueryException is the failure that gets reported.
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
    private static List<ProjectReferenceInput> ReadReferences(
        string projectPath, Dictionary<string, List<Dictionary<string, string>>> items)
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
                    ? FullPath(projectPath, "MSBuildSourceProjectFile", project)
                    : null;

            references.Add(new ProjectReferenceInput(path, projectFile));
        }

        return references;
    }

    /// <summary>
    /// <see cref="Path.GetFullPath(string)"/> on a value that came out of MSBuild's JSON, refused
    /// rather than thrown.
    ///
    /// <para>
    /// Item metadata is not a path anything validated: <c>MSBuildSourceProjectFile</c> is a string a
    /// repository-authored target can set to anything at all. Measured on this host (.NET 10 /
    /// Windows 11): a value holding a NUL throws <see cref="ArgumentException"/> ("Null character in
    /// path"), and a 40 KB one throws <see cref="PathTooLongException"/>. Neither is an
    /// <see cref="MsBuildQueryException"/>, so both escaped <see cref="ReadInputs"/>, escaped
    /// <see cref="Query"/>, missed <c>RoslynResolver.QueryProjectClosure</c>'s deliberately narrow
    /// <c>catch (MsBuildQueryException)</c> and aborted generation for the whole repository -- the
    /// exact shape the shape-guards above were added for, in the method that added them.
    /// </para>
    ///
    /// <para>
    /// Refused, not skipped, for the reason <see cref="ReadItems"/> gives: a reference item this
    /// reader cannot read means this project's reference set is unknown, and reading it as "no project
    /// reference here" is the half-answer <see cref="Query"/>'s contract forbids -- it would send
    /// <see cref="CompilationFactory"/> to the <c>bin/</c> assembly of a project it could have
    /// compiled from source, or to nothing at all.
    /// </para>
    /// </summary>
    private static string FullPath(string projectPath, string metadata, string value)
    {
        try
        {
            return Path.GetFullPath(value);
        }
        catch (Exception e) when (e is ArgumentException or PathTooLongException or NotSupportedException)
        {
            throw new MsBuildQueryException(
                $"`dotnet msbuild` for {projectPath} printed `{metadata}` as a value that is not a path "
                + $"({Truncate(value, 200)}).", e);
        }
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
