// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Globalization;
using System.Reflection;
using System.Text;
using OKF4net.Internal;

namespace OKF4net.Cli;

/// <summary>
/// The <c>okf</c> command-line tool. Eight subcommands (<c>validate</c>,
/// <c>audit</c>, <c>verify</c>, <c>info</c>, <c>index</c>, <c>graph</c>,
/// <c>parse</c>, <c>fmt</c>) over hand-rolled argument parsing -- no
/// third-party dependencies. (The <c>render</c> verb — static HTML site
/// generation — lives in the separate <c>okf-render</c> binary,
/// <c>OKF4net.Render</c>, so this CI-facing validator does not carry the
/// vendored viewer JavaScript it never executes.)
///
/// <see cref="Run"/> is the sole public entry point so tests can drive the
/// CLI in-process (capturing stdout/stderr) without spawning a subprocess;
/// <see cref="Program.Main"/> wires it to the real console.
/// </summary>
public static class OkfCli
{
    /// <summary>
    /// The CLI version echoed by <c>-V</c>/<c>--version</c>, read from the
    /// assembly the build stamped rather than maintained by hand beside it.
    ///
    /// It was a <c>const</c>, which <c>-p:Version</c> — the property
    /// <c>release.yml</c> derives from the git tag and passes to
    /// <c>dotnet publish</c> — does not touch. So the tag, the NuGet package and
    /// the zip filename could all say one version while the binary inside said
    /// another; the winget package for 0.2.0 shipped a binary printing
    /// 0.1.0-alpha.1, caught by a Microsoft moderator rather than by CI. Reading
    /// the stamp removes the second place to drift instead of guarding it.
    ///
    /// AOT-safe: this reads an attribute on a statically-known assembly, not a
    /// dynamically-discovered one, so nothing here is trimmed away — CI's Native
    /// AOT job runs the published binary's <c>--version</c> to keep that honest.
    /// </summary>
    private static readonly string CliVersion =
        typeof(OkfCli).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? "0.0.0-unknown";

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
        "\n" +
        "OPTIONS:\n" +
        "    -h, --help           Show this help\n" +
        "    -V, --version        Show version\n" +
        "        --json           Machine-readable output for validate/info/audit\n" +
        "        --as-of <date>   Pin today's date (YYYY-MM-DD) for validate/audit\n" +
        "        --by <actor>     Who is recording the review, for `verify` (required)\n" +
        "        --at <ts>        UTC timestamp yyyy-MM-ddTHH:mm:ssZ for `verify` (default: now)\n" +
        "        --dry-run        Show what `verify` would record, write nothing\n" +
        "        --stale, --trust <tiers>, --status <s>, --type <t>\n" +
        "                         Filter `audit`'s worklist";

    /// <summary>Accepted by every verb, so never part of a <see cref="VerbSpec"/>'s own flag lists.</summary>
    private static readonly string[] HelpFlags = ["-h", "--help"];

    // These three are declared BEFORE `Verbs`, which reads them: static field
    // initializers run in declaration order, so a later declaration would have
    // `Verbs` capture a null array.

    /// <summary>The flags that make <c>audit</c> a filtered query rather than a report.</summary>
    private static readonly string[] AuditFilterFlags = ["--stale", "--trust", "--status", "--type"];

    /// <summary>The flag that pins "today", shared by <c>validate</c> and <c>audit</c>.</summary>
    private const string AsOfFlag = "--as-of";

    /// <summary>Every <c>audit</c> flag that consumes the following token as its value.</summary>
    private static readonly string[] AuditValuedFlags = ["--trust", "--status", "--type", AsOfFlag];

    /// <summary>
    /// One verb's contract: what it takes and what it accepts. Declaring this
    /// is what lets <see cref="CliArgs.Scan"/> reject an option no verb defines
    /// -- before, any <c>-</c>-prefixed token was kept as a valueless flag, so a
    /// typo ran the command with silently different behaviour than asked for
    /// (<c>okf validate b --jsonn</c> printed the human report and exited 0).
    ///
    /// <paramref name="ValuedFlags"/> consume the following token;
    /// <paramref name="ValuelessFlags"/> stand alone. A flag in neither list is
    /// an error for THIS verb even when another verb defines it -- the
    /// allowlist is per-verb, not global, so <c>okf validate b --dot</c> is
    /// rejected rather than quietly ignored.
    ///
    /// <paramref name="Variadic"/> says the verb takes more than one
    /// positional. It defaults to false so the one-positional rule stays the
    /// default rather than something each verb must remember to ask for, and
    /// only <c>verify</c> sets it: <c>verify &lt;bundle&gt; &lt;id&gt;…</c>
    /// names a bundle and then any number of concepts. Making arity part of
    /// the declared contract keeps that rule enforced per verb rather than
    /// relaxed globally for the one verb that needs it.
    /// </summary>
    private sealed record VerbSpec(
        string Name,
        string UsageLine,
        string Summary,
        string[] ValuedFlags,
        string[] ValuelessFlags,
        string[] OptionLines,
        Func<CliArgs, TextWriter, int> Run,
        bool Variadic = false);

    /// <summary>
    /// Every subcommand, keyed by name — genuinely the single source for
    /// dispatch, scanning and per-verb help, since each spec carries its own
    /// handler in <see cref="VerbSpec.Run"/>.
    ///
    /// The dispatch used to be a separate <c>switch</c> in <see cref="Run"/>
    /// beside this table, so a verb added here and forgotten there would fall
    /// through to "unknown subcommand" despite being fully declared — the exact
    /// drift this table exists to prevent (caught in review).
    /// </summary>
    private static readonly Dictionary<string, VerbSpec> Verbs = new(StringComparer.Ordinal)
    {
        ["validate"] = new(
            "validate", "okf validate <bundle> [--as-of <date>] [--json]",
            "Check a bundle against OKF v0.2 conformance (§11).",
            [AsOfFlag], ["--json"],
            ["    --as-of <date>   Evaluate staleness (§5.5) as of YYYY-MM-DD, not today",
             "    --json           Machine-readable output"],
            CmdValidate),
        ["audit"] = new(
            "audit", "okf audit <bundle> [filters] [--as-of <date>] [--json]",
            "Report trust, freshness and lifecycle across the bundle (§5.3–§5.5).",
            AuditValuedFlags, ["--stale", "--json"],
            ["    --stale           Only concepts past their stale_after",
             "    --trust <tiers>   Filter by trust tier (comma-separated)",
             "    --status <s>      Filter by lifecycle status",
             "    --type <t>        Filter by concept type",
             "    --as-of <date>    Evaluate staleness as of YYYY-MM-DD, not today",
             "    --json            Machine-readable output"],
            CmdAudit),
        ["verify"] = new(
            "verify", "okf verify <bundle> <id>… | - [--by <actor>] [--at <ts>] [--dry-run]",
            "Record a review of one or more concepts (§5.2).",
            ["--by", "--at"], ["--dry-run"],
            ["    --by <actor>     Who is recording the review (required)",
             "    --at <ts>        UTC timestamp yyyy-MM-ddTHH:mm:ssZ (default: now)",
             "    --dry-run        Show what would be recorded, write nothing"],
            CmdVerify,
            // The one variadic verb: a bundle followed by any number of concept
            // ids, or `-` to read them from stdin one per line.
            Variadic: true),
        ["info"] = new(
            "info", "okf info <bundle> [--json]",
            "Summarize a bundle (concepts, types, links, version).",
            [], ["--json"],
            ["    --json           Machine-readable output"],
            CmdInfo),
        ["index"] = new(
            "index", "okf index <bundle>",
            "(Re)generate every index.md in the bundle (§8).",
            [], [], [],
            CmdIndex),
        ["graph"] = new(
            "graph", "okf graph <bundle> [--dot]",
            "Print the cross-link graph (§6).",
            [], ["--dot"],
            ["    --dot            Emit Graphviz DOT instead of plain text"],
            CmdGraph),
        ["parse"] = new(
            "parse", "okf parse <file>",
            "Parse one concept document and print its structure.",
            [], [], [],
            CmdParse),
        ["fmt"] = new(
            "fmt", "okf fmt <file> [-w]",
            "Normalize a document by parse + re-serialize.",
            [], ["-w", "--write"],
            ["    -w, --write      Rewrite the file in place instead of printing to stdout"],
            CmdFmt),
    };

    /// <summary>Renders one verb's help: usage line, summary, then its own options plus the universal help flags.</summary>
    private static string HelpFor(VerbSpec spec)
    {
        var sb = new StringBuilder();
        sb.Append("USAGE:\n    ").Append(spec.UsageLine).Append("\n\n");
        sb.Append(spec.Summary).Append("\n\nOPTIONS:\n");
        foreach (var line in spec.OptionLines)
        {
            sb.Append(line).Append('\n');
        }

        sb.Append("    -h, --help       Print this help\n");
        return sb.ToString();
    }

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

        if (!Verbs.TryGetValue(cmd, out var spec))
        {
            return UnknownSubcommand(cmd, stderr);
        }

        try
        {
            // Scanned once, centrally, so the verb's declared contract is
            // enforced before any command body runs. Help is answered here,
            // BEFORE dispatch: each command body opens by demanding its
            // positional, so `okf validate --help` used to answer
            // "error: missing <bundle>" -- the one question a user asks when
            // they do not know what that argument is.
            var parsed = CliArgs.Scan(rest, spec, stdin);
            if (parsed.WantsHelp)
            {
                stdout.Write(HelpFor(spec));
                return 0;
            }

            return spec.Run(parsed, stdout);
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

        /// <summary>Whether the verb declared it takes more than one positional — see <see cref="VerbSpec.Variadic"/>.</summary>
        private bool _variadic;

        /// <summary>
        /// Standard input, carried here rather than passed to every handler:
        /// stdin is one of the invocation's inputs, like the arguments beside
        /// it, and only <c>verify -</c> reads it. Widening the dispatch
        /// delegate instead would have added a parameter seven other verbs
        /// never use.
        /// </summary>
        private TextReader _stdin = TextReader.Null;

        private CliArgs()
        {
        }

        /// <summary>Standard input for the verbs that read it — in practice only <c>verify -</c>.</summary>
        internal TextReader Stdin => _stdin;

        /// <summary>
        /// Scans <paramref name="args"/> against <paramref name="spec"/>'s declared
        /// contract, rejecting anything it does not define.
        ///
        /// Two rejections the scan did not used to make. An option in neither of
        /// the spec's flag lists is <c>unknown option</c> rather than a silently
        /// kept valueless flag, and a second positional is
        /// <c>unexpected argument</c> rather than silently dropped.
        ///
        /// That second rule now applies after <c>--</c> too, which narrows the
        /// separator's old behaviour: it used to let the first token after the
        /// separator OVERRIDE an earlier positional and swallow the rest, so
        /// <c>okf audit -- b --json</c> resolved <c>b</c> and ignored
        /// <c>--json</c> entirely. The separator's actual contract — nothing
        /// after it is ever a flag — is unchanged and still enforced here; what
        /// changes is that the ignored leftovers are now named instead of
        /// discarded. The guarantee that matters is strictly stronger: a
        /// side-effecting flag parked after the separator (<c>fmt -- f -w</c>)
        /// still never writes, and now says why.
        /// </summary>
        internal static CliArgs Scan(string[] args, VerbSpec spec, TextReader stdin)
        {
            var valuedFlags = spec.ValuedFlags;
            var scanned = new CliArgs { _valuedFlags = valuedFlags, _variadic = spec.Variadic, _stdin = stdin };

            for (var i = 0; i < args.Length; i++)
            {
                var token = args[i];

                if (token == "--")
                {
                    // Nothing past the separator is a flag -- that is what it is
                    // for (a path starting with `-`). They are positionals, and
                    // so bound by the verb's declared arity like any other.
                    for (var j = i + 1; j < args.Length; j++)
                    {
                        scanned.TakePositional(args[j]);
                    }

                    break;
                }

                if (Array.IndexOf(valuedFlags, token) >= 0)
                {
                    i = scanned.TakeValuedFlag(args, i, token);
                    continue;
                }

                // A lone "-" is POSIX's "read from standard input" — an
                // argument, not an option. Only a token with something after
                // the dash is a flag.
                if (token.Length > 1 && token.StartsWith('-'))
                {
                    scanned.TakeOption(token, spec);
                    continue;
                }

                scanned.TakePositional(token);
            }

            return scanned;
        }

        /// <summary>
        /// Records a flag that consumes the following token as its value, and
        /// returns the index the scan continues from — one past the value when
        /// there was one, otherwise unchanged.
        /// </summary>
        /// <param name="args">The full argument array being scanned.</param>
        /// <param name="i">The index of <paramref name="token"/> itself.</param>
        /// <param name="token">The valued flag.</param>
        private int TakeValuedFlag(string[] args, int i, string token)
        {
            var hasValue = CliArgScanning.HasFollowingValue(args, i);

            // First occurrence wins. A later one still consumes its own value,
            // so that value can never be read as the positional.
            if (!_flags.ContainsKey(token))
            {
                _flags[token] = hasValue ? args[i + 1] : null;
            }

            return hasValue ? i + 1 : i;
        }

        /// <summary>
        /// Records a valueless flag, rejecting one this verb does not declare.
        /// The allowlist is per-verb plus the universal help flags, so a flag
        /// another verb defines is still unknown here.
        /// </summary>
        /// <param name="token">The <c>-</c>-prefixed token.</param>
        /// <param name="spec">The verb whose contract decides what is accepted.</param>
        private void TakeOption(string token, VerbSpec spec)
        {
            if (Array.IndexOf(spec.ValuelessFlags, token) < 0 && Array.IndexOf(HelpFlags, token) < 0)
            {
                throw new CliOperationException($"unknown option: {token}");
            }

            _flags[token] = null;
        }

        /// <summary>
        /// Appends a positional, enforcing the arity the verb declared. A verb
        /// that takes one — every verb but <c>verify</c> — still reports the
        /// surplus token rather than dropping it; a variadic one keeps them all
        /// in order, since <c>verify &lt;bundle&gt; &lt;id&gt;…</c> is the whole
        /// point of that flag.
        /// </summary>
        private void TakePositional(string token)
        {
            if (!_variadic && _positionals.Count > 0)
            {
                throw new CliOperationException($"unexpected argument: {token}");
            }

            _positionals.Add(token);
        }

        /// <summary>Whether help was asked for. Answered centrally in <see cref="Run"/>, before any command body runs.</summary>
        internal bool WantsHelp => Has("-h") || Has("--help");

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
    /// Fails into the CLI's error arm when <paramref name="path"/> is not an
    /// existing directory.
    ///
    /// Only <c>index</c> needs this explicitly. Every other bundle verb goes
    /// through <see cref="Load"/>, and so inherits the identical check
    /// <see cref="Bundle.Load"/> performs; <c>index</c> hands its path straight
    /// to <see cref="IndexGenerator.RegenerateIndexes"/>, whose documented
    /// contract is to return an empty list rather than throw — which the CLI
    /// used to render as "no index files written (empty bundle?)" and exit 0,
    /// making <c>index</c> the one verb that reported success for a target that
    /// does not exist. The wording is deliberately identical to
    /// <see cref="Bundle.Load"/>'s; <c>CliTests</c> asserts the two verbs emit
    /// the same stderr so this copy cannot drift from it.
    /// </summary>
    private static void RequireBundleRoot(string path)
    {
        if (!Directory.Exists(path))
        {
            throw new CliOperationException($"bundle root is not a directory: {path}");
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
            // instead of a clean CLI error. CliArgScanning.UserMessage strips
            // ArgumentException's " (Parameter 'x')" framework-noise suffix,
            // shared with okf-render's identical need.
            throw new CliOperationException(CliArgScanning.UserMessage(e));
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
            throw new CliOperationException(CliArgScanning.UserMessage(e));
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
    private static int CmdValidate(CliArgs parsed, TextWriter stdout)
    {

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

    /// <summary>Implements the <c>audit</c> subcommand.</summary>
    private static int CmdAudit(CliArgs parsed, TextWriter stdout)
    {
        // Flag values are read BEFORE the positional is asked for. An unvalued
        // flag is the more specific diagnosis, so `okf audit --as-of` -- the
        // flag as the only argument -- must name that flag rather than report
        // "missing <bundle>", which is what the scan would otherwise leave as
        // the only visible symptom.
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

        // --as-of accepts exactly one spelling, a bare YYYY-MM-DD, which is what
        // the flag's own help text and error message promise. It is NOT the §5
        // timestamp grammar: `stale_after` and the other five §5 keys go through
        // OkfTimestamp, which also reads a datetime with an offset. Deliberately
        // narrower -- this is a CLI argument pinning the report's date stamp, not
        // bundle data being read back, so there is nothing here to stay
        // bug-compatible with. (DateOnly has no (s, format, provider, out)
        // overload; the five-argument form is the only one that takes a culture.)
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
    private static int CmdVerify(CliArgs parsed, TextWriter stdout)
    {
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
        // LineSafeText.ContainsControlCharacter; this call site exists only so
        // the message names the flag instead of arriving unattributed from the
        // writer, which is why it shares that one predicate rather than
        // spelling out its own character test.
        if (LineSafeText.ContainsControlCharacter(by))
        {
            throw new CliOperationException("--by must not contain control characters");
        }

        if (!Actor.Parse(by).IsWellFormed)
        {
            throw new CliOperationException($"--by is not a well-formed §7 actor: \"{by}\"");
        }

        // Same reason as `by` above, and the arm below is why: it QUOTES `at`.
        // Rejecting a malformed timestamp keeps it out of the bundle but not
        // out of stderr, so `--at $'bad\nrecorded …'` forged a line on a run
        // that wrote nothing. Refused before it is quoted.
        if (at is not null && LineSafeText.ContainsControlCharacter(at))
        {
            throw new CliOperationException("--at must not contain control characters");
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
            ids = ReadIdsFrom(parsed.Stdin);

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
    private static int CmdInfo(CliArgs parsed, TextWriter stdout)
    {
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
    private static int CmdIndex(CliArgs parsed, TextWriter stdout)
    {
        var path = parsed.Positional("<bundle>");
        RequireBundleRoot(path);

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

    /// <summary>
    /// Implements the <c>graph</c> subcommand: two independent renderers over
    /// the same §6 link graph, selected by <c>--dot</c>. They are separate
    /// methods because they share nothing but the bundle — the shape that was
    /// obscured while both loop nests sat inline in one <c>if/else</c>.
    /// </summary>
    private static int CmdGraph(CliArgs parsed, TextWriter stdout)
    {
        var bundle = Load(parsed.Positional("<bundle>"));

        if (parsed.Has("--dot"))
        {
            WriteGraphDot(bundle, stdout);
        }
        else
        {
            WriteGraphText(bundle, stdout);
        }

        return 0;
    }

    /// <summary>
    /// Renders the link graph as Graphviz DOT, broken links dashed and red.
    /// Byte-for-byte golden-locked (<c>tests/fixtures/golden/graph.dot</c>):
    /// a diff here is a regression, never a fixture to refresh.
    /// </summary>
    private static void WriteGraphDot(Bundle bundle, TextWriter stdout)
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

    /// <summary>
    /// Renders the link graph as indented plain text: one block per concept
    /// that has outgoing links, each link marked <c>-&gt;</c> when its target
    /// resolves and <c>-x</c> when it does not. Concepts with no outgoing links
    /// are skipped entirely.
    /// </summary>
    private static void WriteGraphText(Bundle bundle, TextWriter stdout)
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

    /// <summary>Implements the <c>parse</c> subcommand.</summary>
    private static int CmdParse(CliArgs parsed, TextWriter stdout)
    {
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
    private static int CmdFmt(CliArgs parsed, TextWriter stdout)
    {
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
