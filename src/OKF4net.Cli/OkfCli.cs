// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Globalization;
using System.Text;
using OKF4net.Internal;
using OKF4net.Viewer;

namespace OKF4net.Cli;

/// <summary>
/// The <c>okf</c> command-line tool. Eight subcommands (<c>validate</c>,
/// <c>audit</c>, <c>info</c>, <c>index</c>, <c>graph</c>, <c>parse</c>,
/// <c>fmt</c>, <c>render</c>) over hand-rolled argument parsing -- no
/// third-party dependencies.
///
/// <see cref="Run"/> is the sole public entry point so tests can drive the
/// CLI in-process (capturing stdout/stderr) without spawning a subprocess;
/// <see cref="Program.Main"/> wires it to the real console.
/// </summary>
public static class OkfCli
{
    /// <summary>The CLI version string, echoed by <c>-V</c>/<c>--version</c>.</summary>
    private const string CliVersion = "0.5.0";

    /// <summary>The <c>--help</c> / usage text.</summary>
    private const string Usage =
        "okf — Open Knowledge Format toolkit\n" +
        "\n" +
        "USAGE:\n" +
        "    okf <command> [args]\n" +
        "\n" +
        "COMMANDS:\n" +
        "    validate <bundle>    Check a bundle against OKF v0.2 conformance (§11)\n" +
        "    audit    <bundle>    Report trust, freshness and lifecycle across the bundle\n" +
        "    info     <bundle>    Summarize a bundle (concepts, types, links, version)\n" +
        "    index    <bundle>    (Re)generate every index.md in the bundle\n" +
        "    graph    <bundle>    Print the cross-link graph (--dot for Graphviz DOT)\n" +
        "    parse    <file>      Parse one concept document and print its structure\n" +
        "    fmt      <file>      Normalize a document by parse + re-serialize (-w writes)\n" +
        "    render   <bundle> --out <dir>   Generate a browsable HTML site from a bundle\n" +
        "\n" +
        "OPTIONS:\n" +
        "    -h, --help           Show this help\n" +
        "    -V, --version        Show version\n" +
        "        --json           Machine-readable output for validate/info/audit\n" +
        "        --out <dir>      Output directory for `render`";

    /// <summary>
    /// Internal control-flow signal for a command failure: caught once at the
    /// top of <see cref="Run"/> and rendered as <c>error: {msg}</c> on stderr
    /// with exit code 1. Never escapes this file.
    /// </summary>
    private sealed class CliOperationException(string message) : Exception(message);

    /// <summary>
    /// Runs the CLI against <paramref name="args"/> (excluding the program
    /// name), writing to the given writers, and returns the process exit code.
    /// Forces "\n"-only line endings on both writers regardless of platform:
    /// LF is the tool's canonical output.
    /// </summary>
    public static int Run(string[] args, TextWriter stdout, TextWriter stderr)
    {
        stdout.NewLine = "\n";
        stderr.NewLine = "\n";

        if (args.Length == 0)
        {
            stderr.Write(Usage);
            stderr.Write("\n");
            return 1;
        }

        var cmd = args[0];
        var rest = args[1..];

        switch (cmd)
        {
            case "-h" or "--help" or "help":
                stdout.Write(Usage);
                stdout.Write("\n");
                return 0;
            case "-V" or "--version" or "version":
                stdout.Write($"okf {CliVersion} (OKF spec v{OkfSpec.Version})\n");
                return 0;
        }

        try
        {
            return cmd switch
            {
                "validate" => CmdValidate(rest, stdout),
                "audit" => CmdAudit(rest, stdout),
                "info" => CmdInfo(rest, stdout),
                "index" => CmdIndex(rest, stdout),
                "graph" => CmdGraph(rest, stdout),
                "parse" => CmdParse(rest, stdout),
                "fmt" => CmdFmt(rest, stdout),
                "render" => CmdRender(rest, stdout),
                _ => UnknownSubcommand(cmd, stderr),
            };
        }
        catch (CliOperationException e)
        {
            stderr.Write($"error: {e.Message}\n");
            return 1;
        }
    }

    /// <summary>Handles an unknown subcommand: writes the message and usage directly, bypassing the <c>error: </c> prefix.</summary>
    private static int UnknownSubcommand(string other, TextWriter stderr)
    {
        stderr.Write($"unknown subcommand: {other}\n\n{Usage}\n");
        return 1;
    }

    // ----------------------------------------------------------------
    // Argument parsing helpers.
    // ----------------------------------------------------------------

    /// <summary>
    /// Returns the first positional argument, or throws. Everything after a
    /// <c>--</c> separator is treated as positional (so paths beginning with
    /// <c>-</c> work).
    /// </summary>
    /// <param name="args">The command's argument list.</param>
    /// <param name="what">Description of the missing positional, used in the error message.</param>
    /// <param name="valuedFlags">
    /// Flags that consume the following token as their value (e.g. <c>--out</c>)
    /// rather than as a candidate positional. Every existing verb's flags are
    /// valueless (<c>--dot</c>, <c>--json</c>, <c>-w</c>), so this is empty for
    /// them and the scan behaves exactly as before; <c>render</c> passes
    /// <c>--out</c> so its value is never mistaken for the bundle path.
    /// </param>
    private static string Positional(string[] args, string what, params string[] valuedFlags)
    {
        var sepIdx = Array.IndexOf(args, "--");
        if (sepIdx >= 0 && sepIdx + 1 < args.Length)
        {
            return args[sepIdx + 1];
        }

        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (Array.IndexOf(valuedFlags, a) >= 0)
            {
                i++; // Skip the value that belongs to this flag.
                continue;
            }

            if (!a.StartsWith('-'))
            {
                return a;
            }
        }

        throw new CliOperationException($"missing {what}");
    }

    /// <summary>True if <paramref name="flag"/> is present in <paramref name="args"/>.</summary>
    private static bool HasFlag(string[] args, string flag) => Array.IndexOf(args, flag) >= 0;

    /// <summary>
    /// The value following <paramref name="flag"/>, or <c>null</c> when the
    /// flag is absent. Throws when the flag is present but unvalued.
    /// </summary>
    private static string? FlagValue(string[] args, string flag)
    {
        var index = Array.IndexOf(args, flag);
        if (index < 0)
        {
            return null;
        }

        if (index + 1 >= args.Length)
        {
            throw new CliOperationException($"{flag} requires a value");
        }

        return args[index + 1];
    }

    /// <summary>
    /// Renders an exception's message for a human reading <c>error: ...</c>
    /// on a terminal, stripping .NET's <c>" (Parameter 'x')"</c> suffix that
    /// <see cref="ArgumentException"/> appends whenever
    /// <see cref="ArgumentException.ParamName"/> is set. That suffix is
    /// framework noise -- correct and useful for a library caller catching
    /// the exception (so <see cref="OKF4net.Viewer.HtmlWriter.Write"/> keeps
    /// throwing it unchanged), but out of place in CLI output meant for
    /// humans. Every catch site that would otherwise surface an
    /// <see cref="ArgumentException"/>'s <c>Message</c> to the CLI funnels
    /// through here instead, so no verb can leak it.
    /// </summary>
    private static string UserMessage(Exception e)
    {
        if (e is ArgumentException { ParamName: not null } argEx)
        {
            var suffix = $" (Parameter '{argEx.ParamName}')";
            if (argEx.Message.EndsWith(suffix, StringComparison.Ordinal))
            {
                return argEx.Message[..^suffix.Length];
            }
        }

        return e.Message;
    }

    /// <summary>Loads a bundle, converting a failure into the CLI's error arm.</summary>
    private static Bundle Load(string path)
    {
        try
        {
            return Bundle.Load(path);
        }
        catch (BundleLoadException e)
        {
            throw new CliOperationException(e.Message);
        }
    }

    /// <summary>
    /// Reads a file as strict UTF-8, converting I/O and decode failures into
    /// the CLI's error arm. Shared by the <c>parse</c> and <c>fmt</c> commands.
    /// </summary>
    private static string ReadFileStrict(string path)
    {
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // Every filesystem failure must funnel into the same
            // `error: {msg}` exit-1 path. .NET rejects garbage paths
            // (embedded NUL, reserved device names, ...) with ArgumentException
            // or NotSupportedException rather than an I/O exception, so both
            // must be caught here too or they escape as unhandled exceptions
            // instead of a clean CLI error.
            throw new CliOperationException(UserMessage(e));
        }

        try
        {
            return OkfEncodings.Strict.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            throw new CliOperationException("stream did not contain valid UTF-8");
        }
    }

    /// <summary>Writes a file as strict UTF-8 (no BOM), converting I/O failures into the CLI's error arm.</summary>
    private static void WriteFileStrict(string path, string content)
    {
        try
        {
            File.WriteAllText(path, content, OkfEncodings.NoBom);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // See ReadFileStrict above: same funnel, same rationale.
            throw new CliOperationException(UserMessage(e));
        }
    }

    /// <summary>True if the document has conformant frontmatter, without relying on exceptions for control flow at the call site.</summary>
    private static bool IsConformant(OkfDocument doc)
    {
        try
        {
            doc.ValidateConformance();
            return true;
        }
        catch (DocumentValidationException)
        {
            return false;
        }
    }

    // ----------------------------------------------------------------
    // Commands.
    // ----------------------------------------------------------------

    /// <summary>Implements the <c>validate</c> subcommand.</summary>
    private static int CmdValidate(string[] args, TextWriter stdout)
    {
        var path = Positional(args, "<bundle>");
        var bundle = Load(path);
        var report = BundleValidator.Validate(bundle);

        if (HasFlag(args, "--json"))
        {
            JsonOutput.WriteValidate(stdout, path, bundle, report);
            return report.IsConformant ? 0 : 1;
        }

        foreach (var d in report.Diagnostics)
        {
            stdout.Write(d.ToString());
            stdout.Write("\n");
        }

        var errors = report.ErrorCount;
        var warnings = report.WarningCount;
        var infos = report.Of(Severity.Info).Count();
        stdout.Write($"\n{bundle.Count} concept(s); {errors} error(s), {warnings} warning(s), {infos} info.\n");

        if (report.IsConformant)
        {
            stdout.Write($"✓ conformant with OKF v{OkfSpec.Version}\n");
            return 0;
        }

        stdout.Write($"✗ not conformant with OKF v{OkfSpec.Version}\n");
        return 1;
    }

    /// <summary>The flags that make <c>audit</c> a filtered query rather than a report.</summary>
    private static readonly string[] AuditFilterFlags = ["--stale", "--trust", "--status", "--type"];

    /// <summary>Every <c>audit</c> flag that consumes the following token as its value.</summary>
    private static readonly string[] AuditValuedFlags = ["--trust", "--status", "--type", "--as-of"];

    /// <summary>An <see cref="IOkfClock"/> pinned to one date, backing <c>--as-of</c>.</summary>
    private sealed class PinnedClock(DateOnly today) : IOkfClock
    {
        public DateOnly Today { get; } = today;
    }

    /// <summary>Implements the <c>audit</c> subcommand.</summary>
    private static int CmdAudit(string[] args, TextWriter stdout)
    {
        // Flag values are validated BEFORE the positional is resolved. An
        // unvalued flag is the more specific diagnosis, and `okf audit --as-of`
        // -- the flag as the only argument -- would otherwise report
        // "missing <bundle>" and hide the actual mistake, because Positional
        // skips a valued flag's slot without checking that it has a value.
        var clock = ParseAsOf(args);

        // Report mode selects exactly what --stale selects; only the
        // presentation differs. --as-of and --json never switch modes.
        var filtered = AuditFilterFlags.Any(f => HasFlag(args, f));
        var query = filtered ? ParseAuditQuery(args) : new AuditQuery(StaleOnly: true);

        var path = Positional(args, "<bundle>", AuditValuedFlags);
        var bundle = Load(path);
        var report = ConceptAudit.Run(bundle, query, clock);

        if (HasFlag(args, "--json"))
        {
            JsonOutput.WriteAudit(stdout, path, query, report);
            return 0;
        }

        if (filtered)
        {
            foreach (var finding in report.Findings)
            {
                stdout.Write(FormatAuditFinding(finding));
                stdout.Write("\n");
            }

            return 0;
        }

        WriteAuditReport(stdout, path, report);
        return 0;
    }

    /// <summary>Parses <c>--as-of</c>; null when absent (the audit then uses the system clock).</summary>
    private static IOkfClock? ParseAsOf(string[] args)
    {
        var raw = FlagValue(args, "--as-of");
        if (raw is null)
        {
            return null;
        }

        // DateOnly has no (s, format, provider, out) overload -- the five-argument
        // form is the only one that takes a culture, and it is the same contract
        // Lifecycle.From uses for stale_after.
        if (!DateOnly.TryParseExact(raw, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var asOf))
        {
            throw new CliOperationException($"--as-of is not a valid YYYY-MM-DD date: \"{raw}\"");
        }

        return new PinnedClock(asOf);
    }

    /// <summary>Builds the query from the filter flags. Throws <see cref="CliOperationException"/> on an unknown vocabulary value.</summary>
    private static AuditQuery ParseAuditQuery(string[] args)
    {
        HashSet<TrustTier>? tiers = null;
        var trustRaw = FlagValue(args, "--trust");
        if (trustRaw is not null)
        {
            if (!AuditVocabulary.TryParseTrustTiers(trustRaw, out var parsed, out var badEntry))
            {
                throw new CliOperationException(
                    $"unknown trust tier \"{badEntry}\"; expected unverified, machine-confirmed or human-reviewed");
            }

            tiers = parsed;
        }

        ConceptStatus? status = null;
        var statusRaw = FlagValue(args, "--status");
        if (statusRaw is not null)
        {
            if (!AuditVocabulary.TryParseStatus(statusRaw.Trim(), out var parsed))
            {
                throw new CliOperationException(
                    $"unknown status \"{statusRaw.Trim()}\"; expected draft, stable or deprecated");
            }

            status = parsed;
        }

        return new AuditQuery(
            HasFlag(args, "--stale"),
            tiers,
            status,
            FlagValue(args, "--type"));
    }

    /// <summary>Renders one concept line: id, freshness, trust tier, status -- two spaces between fields.</summary>
    private static string FormatAuditFinding(AuditFinding finding)
    {
        var freshness = AuditVocabulary.Freshness(finding.Lifecycle, finding.IsStale);

        return $"{finding.Id}  {freshness}  {AuditVocabulary.Name(finding.Trust)}  {AuditVocabulary.Name(finding.Lifecycle.Status)}";
    }

    /// <summary>Renders the report form: summary counters over the whole bundle, then the worklist.</summary>
    private static void WriteAuditReport(TextWriter stdout, string bundlePath, AuditReport report)
    {
        stdout.Write($"bundle:     {bundlePath}\n");
        stdout.Write($"as of:      {report.AsOf.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}\n");
        stdout.Write($"concepts:   {report.ConceptCount}\n");

        // Labels always come from AuditVocabulary -- never as literals here.
        // Duplicating them in each renderer is exactly the drift the shared
        // vocabulary exists to prevent. Only the ORDER is decided locally: the
        // report shows the strongest tier first, so it walks the canonical
        // (weakest-first) list in reverse.
        stdout.Write("\ntrust:\n");
        foreach (var tier in AuditVocabulary.TrustTiersInOrder.Reverse())
        {
            stdout.Write($"  {report.TrustCounts[tier],4}  {AuditVocabulary.Name(tier)}\n");
        }

        stdout.Write("\nstatus:\n");
        foreach (var status in AuditVocabulary.StatusesInOrder)
        {
            stdout.Write($"  {report.StatusCounts[status],4}  {AuditVocabulary.Name(status)}\n");
        }

        stdout.Write($"\nstale:      {report.StaleCount} of {report.ConceptCount} past stale_after\n");

        if (report.Findings.Count == 0)
        {
            stdout.Write("\nneeds attention: none\n");
            return;
        }

        stdout.Write($"\nneeds attention ({report.Findings.Count}):\n");
        foreach (var finding in report.Findings)
        {
            stdout.Write("  ");
            stdout.Write(FormatAuditFinding(finding));
            stdout.Write("\n");
        }
    }

    /// <summary>Implements the <c>info</c> subcommand.</summary>
    private static int CmdInfo(string[] args, TextWriter stdout)
    {
        var path = Positional(args, "<bundle>");
        var bundle = Load(path);

        if (HasFlag(args, "--json"))
        {
            JsonOutput.WriteInfo(stdout, path, bundle);
            return 0;
        }

        stdout.Write($"bundle:     {bundle.Root}\n");
        var okfVersion = bundle.OkfVersion;
        if (okfVersion is not null)
        {
            stdout.Write($"okf_version: {okfVersion}\n");
        }

        stdout.Write($"concepts:   {bundle.Count}\n");
        stdout.Write($"index.md:   {bundle.IndexFiles.Count}\n");
        stdout.Write($"log.md:     {bundle.LogFiles.Count}\n");

        var byType = JsonOutput.BuildTypeHistogram(bundle);

        if (byType.Count > 0)
        {
            stdout.Write("\ntypes:\n");
            foreach (var (t, n) in byType)
            {
                stdout.Write($"  {n,4}  {t}\n");
            }
        }

        var broken = bundle.BrokenLinks();
        var totalLinks = 0;
        foreach (var c in bundle.Concepts)
        {
            totalLinks += bundle.LinksFrom(c.Id).Count;
        }

        stdout.Write($"\nlinks:      {totalLinks} internal ({broken.Count} broken)\n");

        if (bundle.ParseErrors.Count > 0)
        {
            stdout.Write("\nunparseable files:\n");
            foreach (var (p, e) in bundle.ParseErrors)
            {
                stdout.Write($"  {p}: {e}\n");
            }
        }

        return 0;
    }

    /// <summary>Implements the <c>index</c> subcommand.</summary>
    private static int CmdIndex(string[] args, TextWriter stdout)
    {
        var path = Positional(args, "<bundle>");
        IReadOnlyList<string> written;
        try
        {
            written = IndexGenerator.RegenerateIndexes(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new CliOperationException(e.Message);
        }

        if (written.Count == 0)
        {
            stdout.Write("no index files written (empty bundle?)\n");
        }
        else
        {
            foreach (var p in written)
            {
                stdout.Write($"wrote {p}\n");
            }

            stdout.Write($"\n{written.Count} index file(s) regenerated.\n");
        }

        return 0;
    }

    /// <summary>Implements the <c>graph</c> subcommand.</summary>
    private static int CmdGraph(string[] args, TextWriter stdout)
    {
        var path = Positional(args, "<bundle>");
        var dot = HasFlag(args, "--dot");
        var bundle = Load(path);

        if (dot)
        {
            stdout.Write("digraph okf {\n");
            stdout.Write("  rankdir=LR; node [shape=box, fontsize=10];\n");
            foreach (var c in bundle.Concepts)
            {
                foreach (var link in bundle.LinksFrom(c.Id))
                {
                    var style = link.Exists ? "" : " [style=dashed, color=red]";
                    stdout.Write($"  {DebugQuote.Quote(c.Id.ToString())} -> {DebugQuote.Quote(link.Target.ToString())}{style};\n");
                }
            }

            stdout.Write("}\n");
        }
        else
        {
            foreach (var c in bundle.Concepts)
            {
                var links = bundle.LinksFrom(c.Id);
                if (links.Count == 0)
                {
                    continue;
                }

                stdout.Write($"{c.Id}\n");
                foreach (var link in links)
                {
                    var mark = link.Exists ? "->" : "-x";
                    stdout.Write($"  {mark} {link.Target}\n");
                }
            }
        }

        return 0;
    }

    /// <summary>Implements the <c>render</c> subcommand.</summary>
    private static int CmdRender(string[] args, TextWriter stdout)
    {
        // Validate "--out"'s value shape before resolving the bundle, so the
        // reported error is deterministic regardless of argument order:
        //   1. "--out" present but unvalued          -> "--out requires a value"
        //   2. bundle positional missing              -> "missing <bundle>"
        //   3. "--out" absent entirely                -> "render requires --out <dir>"
        // FlagValue itself throws (1) when the flag is present with nothing
        // after it, whether or not the bundle was given -- e.g. bare
        // "okf render --out" used to report "missing <bundle>" (Positional
        // ran first and hit the empty slot before FlagValue ever saw it);
        // calling FlagValue first makes both value-missing spellings agree.
        var outDir = FlagValue(args, "--out");

        // "--out" is the CLI's first valued option -- every other verb's
        // flags are valueless (--dot, --json, -w) -- so Positional must be
        // told to skip both the flag and its value, or that value would be
        // mistaken for the bundle path whenever the bundle is omitted.
        var path = Positional(args, "<bundle>", "--out");

        if (outDir is null)
        {
            throw new CliOperationException("render requires --out <dir>");
        }

        var bundle = Load(path);
        var site = SiteModel.Build(bundle);

        IReadOnlyList<string> written;
        try
        {
            written = HtmlWriter.Write(site, outDir);
        }
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            throw new CliOperationException(UserMessage(e));
        }

        stdout.Write($"wrote {written.Count} files to {outDir}\n");
        return 0;
    }

    /// <summary>Implements the <c>parse</c> subcommand.</summary>
    private static int CmdParse(string[] args, TextWriter stdout)
    {
        var path = Positional(args, "<file>");
        var text = ReadFileStrict(path);
        OkfDocument doc;
        try
        {
            doc = OkfDocument.Parse(text);
        }
        catch (DocumentParseException e)
        {
            throw new CliOperationException(e.Message);
        }

        var mapping = doc.Frontmatter.AsMapping();
        stdout.Write($"frontmatter ({mapping.Count} key(s)):\n");
        foreach (var (k, v) in mapping.Entries)
        {
            // Note: both k.ToYamlString() and v.ToYamlString() already end in
            // "\n" (the emitter always terminates its output that way), and
            // the line itself adds another. Mapping keys are typed raw Values
            // (whose Display rendering is the YAML text), not plain strings, so
            // this output carries that embedded-newline quirk deliberately.
            stdout.Write($"  {k.ToYamlString()}: {v.ToYamlString()}\n");
        }

        var conformant = IsConformant(doc);
        stdout.Write($"\nhas non-empty `type`: {(conformant ? "true" : "false")}\n");
        stdout.Write($"body: {Encoding.UTF8.GetByteCount(doc.Body)} byte(s)\n");

        var links = doc.Links();
        if (links.Count > 0)
        {
            stdout.Write($"\nlinks ({links.Count}):\n");
            foreach (var l in links)
            {
                stdout.Write($"  [{l.Kind}] {l.Text} -> {l.Target}\n");
            }
        }

        var citations = doc.Citations();
        if (citations.Count > 0)
        {
            stdout.Write($"\ncitations ({citations.Count}):\n");
            foreach (var cit in citations)
            {
                stdout.Write($"  [{cit.Number}] {cit.Raw}\n");
            }
        }

        return conformant ? 0 : 1;
    }

    /// <summary>Implements the <c>fmt</c> subcommand.</summary>
    private static int CmdFmt(string[] args, TextWriter stdout)
    {
        var path = Positional(args, "<file>");
        var write = HasFlag(args, "-w") || HasFlag(args, "--write");
        var text = ReadFileStrict(path);
        OkfDocument doc;
        try
        {
            doc = OkfDocument.Parse(text);
        }
        catch (DocumentParseException e)
        {
            throw new CliOperationException(e.Message);
        }

        var outText = doc.Serialize();

        if (write)
        {
            WriteFileStrict(path, outText);
            stdout.Write($"formatted {path}\n");
        }
        else
        {
            stdout.Write(outText);
        }

        return 0;
    }
}
