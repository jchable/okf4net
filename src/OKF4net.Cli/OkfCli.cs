// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Text;
using OKF4net.Internal;

namespace OKF4net.Cli;

/// <summary>
/// The <c>okf</c> command-line tool. Six subcommands (<c>validate</c>,
/// <c>info</c>, <c>index</c>, <c>graph</c>, <c>parse</c>, <c>fmt</c>) over
/// hand-rolled argument parsing -- no third-party dependencies.
///
/// <see cref="Run"/> is the sole public entry point so tests can drive the
/// CLI in-process (capturing stdout/stderr) without spawning a subprocess;
/// <see cref="Program.Main"/> wires it to the real console.
/// </summary>
public static class OkfCli
{
    /// <summary>The CLI version string, echoed by <c>-V</c>/<c>--version</c>.</summary>
    private const string CliVersion = "0.3.1-preview.1";

    /// <summary>The <c>--help</c> / usage text.</summary>
    private const string Usage =
        "okf — Open Knowledge Format toolkit\n" +
        "\n" +
        "USAGE:\n" +
        "    okf <command> [args]\n" +
        "\n" +
        "COMMANDS:\n" +
        "    validate <bundle>    Check a bundle against OKF v0.2 conformance (§11)\n" +
        "    info     <bundle>    Summarize a bundle (concepts, types, links, version)\n" +
        "    index    <bundle>    (Re)generate every index.md in the bundle\n" +
        "    graph    <bundle>    Print the cross-link graph (--dot for Graphviz DOT)\n" +
        "    parse    <file>      Parse one concept document and print its structure\n" +
        "    fmt      <file>      Normalize a document by parse + re-serialize (-w writes)\n" +
        "\n" +
        "OPTIONS:\n" +
        "    -h, --help           Show this help\n" +
        "    -V, --version        Show version";

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
                "info" => CmdInfo(rest, stdout),
                "index" => CmdIndex(rest, stdout),
                "graph" => CmdGraph(rest, stdout),
                "parse" => CmdParse(rest, stdout),
                "fmt" => CmdFmt(rest, stdout),
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
    private static string Positional(string[] args, string what)
    {
        var sepIdx = Array.IndexOf(args, "--");
        if (sepIdx >= 0 && sepIdx + 1 < args.Length)
        {
            return args[sepIdx + 1];
        }

        foreach (var a in args)
        {
            if (!a.StartsWith('-'))
            {
                return a;
            }
        }

        throw new CliOperationException($"missing {what}");
    }

    /// <summary>True if <paramref name="flag"/> is present in <paramref name="args"/>.</summary>
    private static bool HasFlag(string[] args, string flag) => Array.IndexOf(args, flag) >= 0;

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
            throw new CliOperationException(e.Message);
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
            throw new CliOperationException(e.Message);
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

    /// <summary>Implements the <c>info</c> subcommand.</summary>
    private static int CmdInfo(string[] args, TextWriter stdout)
    {
        var path = Positional(args, "<bundle>");
        var bundle = Load(path);

        stdout.Write($"bundle:     {bundle.Root}\n");
        var okfVersion = bundle.OkfVersion;
        if (okfVersion is not null)
        {
            stdout.Write($"okf_version: {okfVersion}\n");
        }

        stdout.Write($"concepts:   {bundle.Count}\n");
        stdout.Write($"index.md:   {bundle.IndexFiles.Count}\n");
        stdout.Write($"log.md:     {bundle.LogFiles.Count}\n");

        var byType = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var c in bundle.Concepts)
        {
            var t = c.Document.Frontmatter.Type ?? "(none)";
            byType[t] = byType.GetValueOrDefault(t) + 1;
        }

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
