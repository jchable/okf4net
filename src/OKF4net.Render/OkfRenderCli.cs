// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Reflection;
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
/// One command needs a fraction of <c>OkfCli</c>'s generic multi-verb argument
/// machinery (<c>VerbSpec</c>, per-verb flag allowlists, a dispatch table), so
/// this is a small hand-rolled scanner over exactly this command's grammar
/// rather than a port of that machinery. User-visible behaviour — error text
/// shape, exit codes, the <c>error: </c> prefix, the <c>--out</c> containment
/// refusal — matches what the <c>render</c> verb did.
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
    /// exit code 1. Never escapes this file.
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

        try
        {
            var parsed = Scan(args);

            if (parsed.WantsHelp)
            {
                stdout.Write(Usage);
                stdout.Write("\n");
                return 0;
            }

            if (parsed.WantsVersion)
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
    }

    /// <summary>One scan's result: the flags and single positional this command's grammar defines.</summary>
    private sealed class ParsedArgs
    {
        internal bool WantsHelp;
        internal bool WantsVersion;

        /// <summary>Whether <c>--out</c> was given at all, distinct from whether it carried a value.</summary>
        internal bool OutDirSeen;

        internal string? OutDir;
        internal string? Bundle;
    }

    /// <summary>
    /// Scans <paramref name="args"/> against this command's one-shot grammar:
    /// a single <c>&lt;bundle&gt;</c> positional, a valued <c>--out</c>, and
    /// the universal <c>-h</c>/<c>--help</c>/<c>-V</c>/<c>--version</c> flags.
    /// Everything else starting with <c>-</c> is an unknown option; a second
    /// positional is an unexpected argument; nothing after a <c>--</c>
    /// separator is ever read as a flag (so a bundle path beginning with
    /// <c>-</c> still works).
    ///
    /// The whole array is scanned before <see cref="Run"/> looks at
    /// <see cref="ParsedArgs.WantsHelp"/>/<see cref="ParsedArgs.WantsVersion"/>,
    /// so an unknown option anywhere in the arguments is reported even when
    /// <c>--help</c> appears earlier in the same invocation -- the same
    /// scan-fully-then-dispatch order <c>okf</c> uses.
    /// </summary>
    private static ParsedArgs Scan(string[] args)
    {
        var parsed = new ParsedArgs();
        var afterSeparator = false;

        for (var i = 0; i < args.Length; i++)
        {
            var token = args[i];

            if (!afterSeparator && token == "--")
            {
                afterSeparator = true;
                continue;
            }

            if (!afterSeparator)
            {
                switch (token)
                {
                    case "-h" or "--help":
                        parsed.WantsHelp = true;
                        continue;
                    case "-V" or "--version":
                        parsed.WantsVersion = true;
                        continue;
                    case "--out":
                        {
                            // The separator is not a value: swallowing it would
                            // hide "requires a value" and cancel the separator's
                            // contract for everything that follows.
                            var hasValue = i + 1 < args.Length && args[i + 1] != "--";

                            // First occurrence wins, but a later one still
                            // consumes its own value so that value can never be
                            // misread as the bundle positional.
                            if (!parsed.OutDirSeen)
                            {
                                parsed.OutDirSeen = true;
                                parsed.OutDir = hasValue ? args[i + 1] : null;
                            }

                            if (hasValue)
                            {
                                i++;
                            }

                            continue;
                        }

                    default:
                        if (token.StartsWith('-'))
                        {
                            throw new CliOperationException($"unknown option: {token}");
                        }

                        break;
                }
            }

            if (parsed.Bundle is not null)
            {
                throw new CliOperationException($"unexpected argument: {token}");
            }

            parsed.Bundle = token;
        }

        return parsed;
    }

    /// <summary>
    /// Loads the bundle, builds the site model, and writes it to
    /// <c>--out</c>. Argument-shape errors are checked in a fixed order so
    /// the reported message is deterministic regardless of what else is
    /// missing:
    ///   1. "--out" present but unvalued          -> "--out requires a value"
    ///   2. bundle positional missing              -> "missing &lt;bundle&gt;"
    ///   3. "--out" absent entirely                -> "render requires --out &lt;dir&gt;"
    /// e.g. bare "okf-render --out" reports (1) even though the bundle is
    /// also missing, and bare "okf-render" reports (2) rather than (3).
    /// </summary>
    private static int RunRender(ParsedArgs parsed, TextWriter stdout)
    {
        if (parsed.OutDirSeen && parsed.OutDir is null)
        {
            throw new CliOperationException("--out requires a value");
        }

        if (parsed.Bundle is null)
        {
            throw new CliOperationException("missing <bundle>");
        }

        if (parsed.OutDir is null)
        {
            throw new CliOperationException("render requires --out <dir>");
        }

        var bundle = Load(parsed.Bundle);
        var site = SiteModel.Build(bundle);

        IReadOnlyList<string> written;
        try
        {
            written = HtmlWriter.Write(site, parsed.OutDir);
        }
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            throw new CliOperationException(UserMessage(e));
        }

        stdout.Write($"wrote {written.Count} files to {parsed.OutDir}\n");
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

    /// <summary>
    /// Renders an exception's message for a human reading <c>error: ...</c>
    /// on a terminal, stripping .NET's <c>" (Parameter 'x')"</c> suffix that
    /// <see cref="ArgumentException"/> appends whenever
    /// <see cref="ArgumentException.ParamName"/> is set. See
    /// <c>OkfCli.UserMessage</c> for the identical rationale: correct and
    /// useful for a library caller catching the exception, but framework
    /// noise out of place in output meant for humans.
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
}
