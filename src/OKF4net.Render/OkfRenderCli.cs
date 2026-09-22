// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Reflection;
using OKF4net.Internal;
using OKF4net.Viewer;

namespace OKF4net.Render;

/// <summary>
/// The <c>okf-render</c> command-line tool: a single command, generating a
/// browsable static HTML site from an OKF bundle.
///
/// This used to be the <c>render</c> verb of the multi-verb <c>okf</c> CLI. It
/// was split into its own Native AOT binary so <c>okf validate</c> — the
/// self-contained CI validator distributed via winget — does not carry the
/// vendored viewer JavaScript (<c>OKF4net.Viewer</c>'s marked.js copy plus its
/// own sanitizer) it never executes. <c>OKF4net.Mcp</c> already established the
/// pattern this follows: a leaf executable owns the dependencies its one job
/// needs, rather than pushing them into the shared core.
///
/// One command needs a fraction of <c>OkfCli</c>'s generic multi-verb
/// dispatch machinery (<c>VerbSpec</c>, per-verb flag allowlists, a dispatch
/// table keyed by verb name), so this tool has no verb table of its own — it
/// calls <see cref="OKF4net.Internal.CliArgs"/> directly with this command's
/// one-shot grammar rather than porting that dispatch machinery for a single
/// command. The scanning itself is NOT a second hand-rolled copy: it used to
/// be, and the two copies drifted (a lone <c>-</c> was rejected here as
/// <c>unknown option: -</c> while <c>okf</c> took it as a positional; a
/// repeated <c>--out</c> was first-wins here while <c>okf</c> refuses a
/// repeated valued flag) until both were moved onto the one shared
/// <see cref="OKF4net.Internal.CliArgs"/> scanner. User-visible behaviour —
/// error text shape, exit codes, the <c>error: </c> prefix, the
/// bare-invocation usage block, the <c>--out</c> containment refusal —
/// mirrors what <c>OkfCli</c> does for its own verbs, with wording rewritten
/// to name this tool and its one command rather than the <c>render</c> verb
/// it used to be (that verb no longer exists in any binary, so no message
/// here may name it). The <see cref="ArgumentException"/> <c>ParamName</c>-
/// suffix strip is likewise shared, from <see cref="CliArgScanning"/>.
///
/// <see cref="Run"/> is the sole public entry point so tests can drive the
/// tool in-process (capturing stdout/stderr) without spawning a subprocess;
/// <see cref="Program.Main"/> wires it to the real console.
/// </summary>
public static class OkfRenderCli
{
    /// <summary>
    /// The tool's version echoed by <c>-V</c>/<c>--version</c>, read from the
    /// assembly the build stamped. See <c>OkfCli.CliVersion</c> for why this
    /// is read from the assembly attribute rather than a hand-maintained
    /// constant.
    /// </summary>
    private static readonly string CliVersion =
        typeof(OkfRenderCli).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? "0.0.0-unknown";

    /// <summary>The <c>--help</c> / usage text.</summary>
    private const string Usage =
        "okf-render — generate a browsable static HTML site from an OKF bundle\n" +
        "\n" +
        "USAGE:\n" +
        "    okf-render <bundle> --out <dir>\n" +
        "\n" +
        "Generates one page per concept (frontmatter table + rendered body), a\n" +
        "generated index, navigable cross-links with broken links flagged, and\n" +
        "backlinks. The output is self-contained and opens straight off the\n" +
        "filesystem -- no server needed.\n" +
        "\n" +
        "OPTIONS:\n" +
        "    --out <dir>      Output directory (required)\n" +
        "    -h, --help       Show this help\n" +
        "    -V, --version    Show version";

    /// <summary>
    /// Internal control-flow signal for a failure: caught once at the top of
    /// <see cref="Run"/> and rendered as <c>error: {msg}</c> on stderr with
    /// exit code 1. Never escapes this file. Distinct from
    /// <see cref="CliArgumentException"/> (the shared scanner's own error
    /// type, caught alongside this one in <see cref="Run"/>): this one is
    /// for the render-specific failures raised after scanning (missing
    /// <c>--out</c>, a bad bundle, a write failure).
    /// </summary>
    private sealed class CliOperationException(string message) : Exception(message);

    /// <summary>
    /// Runs the tool against <paramref name="args"/> (excluding the program
    /// name), writing to the given writers, and returns the process exit
    /// code. Forces "\n"-only line endings on both writers regardless of
    /// platform: LF is the tool's canonical output, matching <c>okf</c>.
    /// </summary>
    public static int Run(string[] args, TextWriter stdout, TextWriter stderr)
    {
        stdout.NewLine = "\n";
        stderr.NewLine = "\n";

        if (args.Length == 0)
        {
            // A bare invocation is the discovery gesture for a one-command
            // tool: the full usage block on stderr, not "error: missing
            // <bundle>", mirroring OkfCli.Run's identical zero-argument case.
            stderr.Write(Usage);
            stderr.Write("\n");
            return 1;
        }

        try
        {
            // Shares okf's CliArgs scanner (OKF4net.Internal) rather than a
            // second hand-rolled copy of the same rules: the two had already
            // drifted once (a lone "-" was rejected here as "unknown option: -"
            // while okf took it as a positional; a repeated "--out" was
            // first-wins here while okf refuses a repeated valued flag since
            // its own C2 fix). "-V"/"--version" go in the valueless-flag list,
            // not the help-flag list: they must be reachable through
            // CliArgs.Has, not fold into WantsHelp, so this method's
            // help/version handling stays exactly as before.
            var parsed = CliArgs.Scan(args, ["--out"], ["-V", "--version"], variadic: false, helpFlags: ["-h", "--help"]);

            if (parsed.WantsHelp)
            {
                stdout.Write(Usage);
                stdout.Write("\n");
                return 0;
            }

            if (parsed.Has("-V") || parsed.Has("--version"))
            {
                stdout.Write($"okf-render {CliVersion} (OKF spec v{OkfSpec.Version})\n");
                return 0;
            }

            return RunRender(parsed, stdout);
        }
        catch (CliOperationException e)
        {
            stderr.Write($"error: {e.Message}\n");
            return 1;
        }
        catch (CliArgumentException e)
        {
            stderr.Write($"error: {e.Message}\n");
            return 1;
        }
    }

    /// <summary>
    /// Loads the bundle, builds the site model, and writes it to
    /// <c>--out</c>. Argument-shape errors are checked in a fixed order so
    /// the reported message is deterministic regardless of what else is
    /// missing:
    ///   1. "--out" present but unvalued          -> "--out requires a value" (<see cref="CliArgs.Value"/>)
    ///   2. bundle positional missing              -> "missing &lt;bundle&gt;" (<see cref="CliArgs.Positional"/>)
    ///   3. "--out" absent entirely                -> "missing --out &lt;dir&gt;"
    /// e.g. "okf-render b --out" reports (1) even though the bundle is
    /// present, and "okf-render b" reports (3) rather than passing an empty
    /// output directory through. A fully bare invocation is handled earlier,
    /// in <see cref="Run"/>, before this method is ever reached.
    /// </summary>
    private static int RunRender(CliArgs parsed, TextWriter stdout)
    {
        var outDir = parsed.Value("--out");
        var bundlePath = parsed.Positional("<bundle>");

        if (outDir is null)
        {
            throw new CliOperationException("missing --out <dir>");
        }

        var bundle = Load(bundlePath);
        var site = SiteModel.Build(bundle);

        IReadOnlyList<string> written;
        try
        {
            written = HtmlWriter.Write(site, outDir);
        }
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            throw new CliOperationException(CliArgScanning.UserMessage(e));
        }

        stdout.Write($"wrote {written.Count} files to {outDir}\n");
        return 0;
    }

    /// <summary>Loads a bundle, converting a failure into this tool's error arm.</summary>
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

}
