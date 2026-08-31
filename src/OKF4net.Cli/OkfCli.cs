// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Globalization;
using System.Text;
using OKF4net.Internal;
using OKF4net.Viewer;

namespace OKF4net.Cli;

/// <summary>
/// The <c>okf</c> command-line tool. Nine subcommands (<c>validate</c>,
/// <c>audit</c>, <c>verify</c>, <c>info</c>, <c>index</c>, <c>graph</c>,
/// <c>parse</c>, <c>fmt</c>, <c>render</c>) over hand-rolled argument
/// parsing -- no third-party dependencies.
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
        "    verify   <bundle> <id>…   Record a review of one or more concepts (--by <actor>)\n" +
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
        "        --out <dir>      Output directory for `render`\n" +
        "        --as-of <date>   Pin today's date (YYYY-MM-DD) for validate/audit\n" +
        "        --by <actor>     Who is recording the review, for `verify` (required)\n" +
        "        --at <ts>        UTC timestamp yyyy-MM-ddTHH:mm:ssZ for `verify` (default: now)\n" +
        "        --dry-run        Show what `verify` would record, write nothing\n" +
        "        --stale, --trust <tiers>, --status <s>, --type <t>\n" +
        "                         Filter `audit`'s worklist";

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
    /// <param name="args">The command-line arguments, excluding the program name.</param>
    /// <param name="stdin">
    /// Standard input. Only a verb that documents reading it (today, <c>verify</c>)
    /// ever touches this reader; every other verb never reads from it, so no
    /// blocking read is introduced for the rest of the CLI.
    /// </param>
    /// <param name="stdout">Standard output.</param>
    /// <param name="stderr">Standard error.</param>
    public static int Run(string[] args, TextReader stdin, TextWriter stdout, TextWriter stderr)
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
                "verify" => CmdVerify(rest, stdin, stdout),
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
        catch (OkfException e)
        {
            // The safety net for every verb, not a substitute for the targeted
            // catches below: those exist to phrase a better message (naming the
            // file, the flag, the concept) and still run first. This one only
            // catches a library failure no verb anticipated — a YAML emit
            // failure on a document that parsed, say — and turns it into the
            // same `error: …`/exit 1 shape as everything else, instead of a
            // stack trace and exit 127. Deliberately narrow: `OkfException` is
            // this library's own expected-error base, so an unexpected BCL
            // exception still crashes loudly rather than being reported as a
            // routine failure.
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
    /// One command's arguments, scanned once so every later question agrees on
    /// what each token is.
    ///
    /// Scanning left to right, a flag listed in <c>valuedFlags</c> consumes the
    /// following token as its value; every other <c>-</c>-prefixed token is a
    /// valueless flag; anything else is positional. A <c>--</c> separator ends
    /// the scan: everything after it is positional, never a flag (so a path
    /// beginning with <c>-</c> works).
    ///
    /// Scanning once is the point. When presence, value and positional were
    /// three independent scans of the raw array, they disagreed: a token
    /// consumed as a value was still seen as a flag by the presence check, so
    /// <c>okf audit b --type --stale</c> set the stale filter even though
    /// <c>--stale</c> was <c>--type</c>'s value, and only the positional scan
    /// honoured <c>--</c>.
    /// </summary>
    private sealed class CliArgs
    {
        /// <summary>
        /// Every flag given, mapped to the value it consumed: <c>null</c> both
        /// for a valueless flag and for a valued one left without a value. Key
        /// absent means the flag was not given — one dictionary rather than a
        /// presence set beside a value map, so presence and value cannot drift
        /// apart.
        /// </summary>
        private readonly Dictionary<string, string?> _flags = new(StringComparer.Ordinal);

        /// <summary>
        /// The positional tokens, in order. `--` ends option parsing without
        /// discarding what came before it, so a verb taking several positionals
        /// (`verify <bundle> <id>…`) keeps them all.
        /// </summary>
        private readonly List<string> _positionals = [];

        /// <summary>The flags this scan was told consume a value, kept so <see cref="Value"/> can tell a user's mistake from the caller's.</summary>
        private string[] _valuedFlags = [];

        private CliArgs()
        {
        }

        /// <summary>Scans <paramref name="args"/>, treating <paramref name="valuedFlags"/> as flags that consume the next token.</summary>
        internal static CliArgs Scan(string[] args, params string[] valuedFlags)
        {
            var scanned = new CliArgs { _valuedFlags = valuedFlags };

            for (var i = 0; i < args.Length; i++)
            {
                var token = args[i];

                if (token == "--")
                {
                    // Everything past the separator is positional, never a flag.
                    // It APPENDS: the tokens before it are positionals too.
                    for (var j = i + 1; j < args.Length; j++)
                    {
                        scanned._positionals.Add(args[j]);
                    }

                    break;
                }

                if (Array.IndexOf(valuedFlags, token) >= 0)
                {
                    // The separator is not a value: swallowing it would hide
                    // "requires a value" and cancel the separator's contract for
                    // everything that follows.
                    var hasValue = i + 1 < args.Length && args[i + 1] != "--";

                    // First occurrence wins. A later one still consumes its own
                    // value, so that value can never be read as the positional.
                    if (!scanned._flags.ContainsKey(token))
                    {
                        scanned._flags[token] = hasValue ? args[i + 1] : null;
                    }

                    if (hasValue)
                    {
                        i++;
                    }

                    continue;
                }

                // A lone "-" is POSIX's "read from standard input" — an
                // argument, not an option. Only a token with something after
                // the dash is a flag.
                if (token.Length > 1 && token.StartsWith('-'))
                {
                    scanned._flags[token] = null;
                    continue;
                }

                scanned._positionals.Add(token);
            }

            return scanned;
        }

        /// <summary>True if <paramref name="flag"/> was given as a flag — not as another flag's value, and not after <c>--</c>.</summary>
        internal bool Has(string flag) => _flags.ContainsKey(flag);

        /// <summary>
        /// The value <paramref name="flag"/> consumed, or <c>null</c> when the
        /// flag is absent. Throws when the flag is present but unvalued.
        /// </summary>
        internal string? Value(string flag)
        {
            if (!_flags.TryGetValue(flag, out var value))
            {
                return null;
            }

            if (value is not null)
            {
                return value;
            }

            // Present with nothing attached. That is a user mistake only if the
            // flag was declared as taking a value; otherwise the caller asked a
            // question about a flag the scan was never told to value, and
            // reporting "requires a value" would blame the user for a bug here.
            if (Array.IndexOf(_valuedFlags, flag) < 0)
            {
                throw new InvalidOperationException(
                    $"{flag} was not declared as a valued flag in this command's CliArgs.Scan call");
            }

            throw new CliOperationException($"{flag} requires a value");
        }

        /// <summary>The first positional argument, or throws naming <paramref name="what"/>.</summary>
        internal string Positional(string what) =>
            _positionals.Count > 0 ? _positionals[0] : throw new CliOperationException($"missing {what}");

        /// <summary>Every positional argument, in order — the first is what <see cref="Positional"/> returns.</summary>
        internal IReadOnlyList<string> Positionals => _positionals;
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
        var parsed = CliArgs.Scan(args, AsOfFlag);

        // --as-of is parsed before the positional, so an unvalued flag names
        // itself rather than surfacing as "missing <bundle>".
        // Resolved once so the date the validator used is the same one --json
        // reports, whether it came from --as-of or from the system clock.
        var clock = ParseAsOf(parsed) ?? new SystemClock();
        var path = parsed.Positional("<bundle>");
        var bundle = Load(path);
        var report = BundleValidator.Validate(bundle, clock);

        if (parsed.Has("--json"))
        {
            JsonOutput.WriteValidate(stdout, path, clock.Today, bundle, report);
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

    /// <summary>The flag that pins "today", shared by <c>validate</c> and <c>audit</c>.</summary>
    private const string AsOfFlag = "--as-of";

    /// <summary>Every <c>audit</c> flag that consumes the following token as its value.</summary>
    private static readonly string[] AuditValuedFlags = ["--trust", "--status", "--type", AsOfFlag];

    /// <summary>Implements the <c>audit</c> subcommand.</summary>
    private static int CmdAudit(string[] args, TextWriter stdout)
    {
        // Flag values are read BEFORE the positional is asked for. An unvalued
        // flag is the more specific diagnosis, so `okf audit --as-of` -- the
        // flag as the only argument -- must name that flag rather than report
        // "missing <bundle>", which is what the scan would otherwise leave as
        // the only visible symptom.
        var parsed = CliArgs.Scan(args, AuditValuedFlags);
        var clock = ParseAsOf(parsed);

        // Report mode selects exactly what --stale selects; only the
        // presentation differs. --as-of and --json never switch modes.
        var filtered = AuditFilterFlags.Any(parsed.Has);
        var query = filtered ? ParseAuditQuery(parsed) : new AuditQuery(StaleOnly: true);

        var path = parsed.Positional("<bundle>");
        var bundle = Load(path);
        var report = ConceptAudit.Run(bundle, query, clock);

        if (parsed.Has("--json"))
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
    private static IOkfClock? ParseAsOf(CliArgs args)
    {
        var raw = args.Value(AsOfFlag);
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

        return new FixedClock(asOf);
    }

    /// <summary>Builds the query from the filter flags. Throws <see cref="CliOperationException"/> on an unknown vocabulary value.</summary>
    private static AuditQuery ParseAuditQuery(CliArgs args)
    {
        HashSet<TrustTier>? tiers = null;
        var trustRaw = args.Value("--trust");
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
        var statusRaw = args.Value("--status");
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
            args.Has("--stale"),
            tiers,
            status,
            args.Value("--type"));
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

    /// <summary>Implements the <c>verify</c> subcommand.</summary>
    private static int CmdVerify(string[] args, TextReader stdin, TextWriter stdout)
    {
        var parsed = CliArgs.Scan(args, "--by", "--at");

        // Both values are READ first, so a flag present without a value names
        // itself ("--by requires a value") rather than surfacing later as a
        // missing argument. They are VALIDATED after the ids, so that the most
        // structural mistake — no concept named at all — is reported first.
        var by = parsed.Value("--by");
        var at = parsed.Value("--at");

        var positionals = parsed.Positionals;
        var path = positionals.Count > 0 ? positionals[0] : throw new CliOperationException("missing <bundle>");
        var ids = positionals.Skip(1).ToList();
        if (ids.Count == 0)
        {
            throw new CliOperationException("missing <concept-id>");
        }

        // Decided from the ARGUMENTS, before anything reads the pipe.
        var readFromStdin = ids is ["-"];
        if (!readFromStdin && ids.Contains("-"))
        {
            throw new CliOperationException("\"-\" (stdin) cannot be combined with explicit concept ids");
        }

        // Validated only now: an invocation naming no concept at all is the
        // more structural mistake, and its message must come first.
        if (by is null)
        {
            throw new CliOperationException("verify requires --by <actor>");
        }

        // Checked BEFORE the well-formedness message below, which echoes `by`:
        // a newline in an echoed value forges a line in the caller's error
        // output. The write gate (BundleConceptWriter.RecordVerifications) is
        // what actually stops the value from being stored — see
        // Actor.ContainsControlCharacter; this call site exists only so the
        // message names the flag instead of arriving unattributed from the
        // writer, which is why it shares that one predicate rather than
        // spelling out its own character test.
        if (Actor.ContainsControlCharacter(by))
        {
            throw new CliOperationException("--by must not contain control characters");
        }

        if (!Actor.Parse(by).IsWellFormed)
        {
            throw new CliOperationException($"--by is not a well-formed §7 actor: \"{by}\"");
        }

        // The writer applies the same strict UTC rule; checking here too turns a
        // generic write error into a message naming the flag. Deliberately NOT
        // BundleValidator.IsIso8601DateTime, which only validates the date part.
        if (at is not null && !DateTime.TryParseExact(
                at,
                "yyyy-MM-dd'T'HH:mm:ss'Z'",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                out _))
        {
            throw new CliOperationException($"--at is not a UTC timestamp of the form yyyy-MM-ddTHH:mm:ssZ: \"{at}\"");
        }

        // Read LAST of this invocation's inputs, once every flag value is
        // known to be usable. Draining the pipe first made an already-doomed
        // invocation (`okf verify b -` with no --by) wait behind a slow
        // producer, or hang on a terminal until the user found Ctrl-D, before
        // printing an error it could have printed immediately. The message
        // ordering above is unchanged — every one of those errors is decided
        // from the argument list alone.
        if (readFromStdin)
        {
            ids = ReadIdsFrom(stdin);

            // An empty stream is "nothing to do", not an error. This is the
            // documented `okf audit … --trust unverified | cut … | okf verify
            // … -` pipeline, and `audit` deliberately exits 0 printing nothing
            // when the bundle needs no attention; failing here made the
            // headline pipeline non-idempotent and broke it under `set -e`
            // exactly when the bundle was healthy. The cheapest workaround for
            // that, `|| true`, would also swallow a genuine partial-write
            // failure — the one outcome the Records-before-throw design exists
            // to surface — so this is a correctness fix, not a cosmetic one.
            //
            // Every other empty/missing-id case stays an error: `okf verify
            // <bundle>` naming no concept at all is still `missing
            // <concept-id>` above, which is what keeps a mistyped `okf verify
            // mybundle` (for `validate`) loud.
            if (ids.Count == 0)
            {
                return 0;
            }
        }

        var bundle = Load(path);

        // Refused here as well as in the writer, so the message reads like its
        // siblings (the writer's ends with a period; the CLI's do not).
        var duplicate = ids.GroupBy(id => id, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
        {
            throw new CliOperationException($"concept '{duplicate.Key}' is named more than once");
        }

        // The writer itself already refuses the whole batch atomically if any
        // id is unknown or non-conformant (BundleConceptWriter.RecordVerifications
        // resolves, reads, parses and validates every concept before writing
        // any) — this loop does not exist to prevent a half-stamped batch.
        // What it buys is message quality: naming the offending id directly
        // ("unknown concept \"x\"" / "concept \"x\" has no `type`...") instead
        // of the writer's unattributed "Missing required frontmatter keys:
        // type", which does not say which of several ids was at fault.
        foreach (var id in ids)
        {
            if (!ConceptId.TryParse(id, out var parsedId) || bundle.Get(parsedId!) is not { } concept)
            {
                throw new CliOperationException($"unknown concept \"{id}\"");
            }

            if (concept.Document.Frontmatter.Get("type") is not { IsEmptyValue: false })
            {
                throw new CliOperationException($"concept \"{id}\" has no `type` and is not §11-conformant");
            }
        }

        if (parsed.Has("--dry-run"))
        {
            // A dry run writes nothing, so there is no timestamp to report. It
            // could format one (OkfTimestamp is reachable here), but printing a
            // date the real run would not reproduce is worse than saying "now".
            foreach (var id in ids)
            {
                stdout.Write($"would record {id}  {by}  {at ?? "(now)"}\n");
            }

            return 0;
        }

        // Constructed only now: a dry run above never needs a writer at all.
        var writer = new BundleConceptWriter(path);

        // One batch call: the writer prepares every concept before writing any,
        // so nothing is half-stamped if a later one turns out unwritable.
        var outcome = writer.RecordVerifications(ids, by, at);
        // Printed BEFORE deciding the exit code: a batch can fail part-way
        // through the write phase, and the concepts that did land must be
        // reported. Staying silent about them would repeat, one layer up, the
        // very thing the writer was fixed not to do.
        foreach (var record in outcome.Records)
        {
            // record.At is the timestamp the writer actually used — the CLI
            // reports it rather than recomputing one that could differ.
            var replaces = record.ReplacedAt is { } previous ? $"  (replaces {previous})" : string.Empty;
            stdout.Write($"recorded {record.ConceptId}  {by}  {record.At}{replaces}\n");
        }

        if (!outcome.Recorded)
        {
            throw new CliOperationException(outcome.Message.Replace("Error: ", string.Empty, StringComparison.Ordinal));
        }

        return 0;
    }

    /// <summary>Reads concept ids from <paramref name="stdin"/>, one per line, ignoring blank lines.</summary>
    private static List<string> ReadIdsFrom(TextReader stdin)
    {
        var ids = new List<string>();
        while (stdin.ReadLine() is { } line)
        {
            var trimmed = line.Trim();
            if (trimmed.Length > 0)
            {
                ids.Add(trimmed);
            }
        }

        return ids;
    }

    /// <summary>Implements the <c>info</c> subcommand.</summary>
    private static int CmdInfo(string[] args, TextWriter stdout)
    {
        var parsed = CliArgs.Scan(args);
        var path = parsed.Positional("<bundle>");
        var bundle = Load(path);

        if (parsed.Has("--json"))
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
        var parsed = CliArgs.Scan(args);
        var path = parsed.Positional("<bundle>");
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
        var parsed = CliArgs.Scan(args);
        var path = parsed.Positional("<bundle>");
        var dot = parsed.Has("--dot");
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
        // Value() itself throws (1) when the flag is present with nothing
        // after it, whether or not the bundle was given -- e.g. bare
        // "okf render --out" used to report "missing <bundle>" (the positional
        // was resolved first and hit the empty slot); asking for the value
        // first makes both value-missing spellings agree.
        //
        // Declaring "--out" to the scan is what keeps its value from being
        // mistaken for the bundle path whenever the bundle is omitted.
        var parsed = CliArgs.Scan(args, "--out");
        var outDir = parsed.Value("--out");
        var path = parsed.Positional("<bundle>");

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
        var parsed = CliArgs.Scan(args);
        var path = parsed.Positional("<file>");
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
        var parsed = CliArgs.Scan(args);
        var path = parsed.Positional("<file>");
        var write = parsed.Has("-w") || parsed.Has("--write");
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
