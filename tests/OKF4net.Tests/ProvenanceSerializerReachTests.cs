// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Linq;
using System.Text.RegularExpressions;
using OKF4net.Yaml;

namespace OKF4net.Tests;

/// <summary>
/// Guards the claim three sections (1, 4 and 8) of
/// <c>docs/superpowers/specs/2026-09-01-okf-per-entry-usage-window-design.md</c> rest on:
/// a <c>sources</c> entry field that <see cref="Provenance.ToYaml"/> does not write back is lost
/// only for <see cref="OkfDocumentBuilder"/> callers, never by <c>okf fmt</c>, which re-serializes
/// the raw frontmatter tree and does not route <c>sources</c> through <see cref="Provenance.ToYaml"/>.
/// If either test here fails, those three sections have gone false: fix the code, or rewrite them.
/// </summary>
public class ProvenanceSerializerReachTests
{
    private const string DesignDoc =
        "docs/superpowers/specs/2026-09-01-okf-per-entry-usage-window-design.md (sections 1, 4 and 8)";

    // A sources sequence that Provenance.ToYaml cannot reproduce, in four independent ways:
    // the first entry carries a key Source has no member for (`license`), its keys are not in
    // ToYaml's canonical order (resource before id), its usage_window bounds are not in ToYaml's
    // order (to before from), and the second entry has no `resource`, which ToYaml writes as "".
    private const string Probe =
        "---\n" +
        "type: Metric\n" +
        "title: Probe\n" +
        "description: A sources entry ToYaml cannot reproduce.\n" +
        "sources:\n" +
        "  - resource: references/a.md\n" +
        "    id: s1\n" +
        "    license: CC-BY-4.0\n" +
        "    usage_window:\n" +
        "      to: 2026-06-30\n" +
        "      from: 2026-01-01\n" +
        "  - id: s2\n" +
        "---\n" +
        "body\n";

    /// <summary>
    /// The real guard, for the one command the design doc makes a claim about: <c>okf fmt</c>, in
    /// both of its output modes — printed to stdout, and written back to disk with <c>-w</c> (each
    /// mode is its own branch in <c>CmdFmt</c>, so each is run). It observes the behaviour end to
    /// end through the CLI and does not care how <c>fmt</c> is wired: a rewiring of either mode
    /// through <see cref="Provenance.ToYaml"/> — direct, aliased, via a wrapper, a method group or
    /// reflection — changes a <c>sources</c> entry and fails it. It says nothing about any other
    /// writer (<c>BundleConceptWriter</c>, <c>okf verify</c>, the agent tools), about which the doc
    /// makes no claim. The first assertion proves the probe discriminates (a ToYaml round-trip
    /// really does change it); without that, a future lossless ToYaml would leave this test green
    /// for the wrong reason.
    /// </summary>
    [Fact]
    public void Fmt_keeps_a_sources_entry_that_Provenance_ToYaml_would_rewrite()
    {
        var probeSources = (YamlSequence)OkfDocument.Parse(Probe).Frontmatter.AsMapping().Get("sources")!;
        var viaToYaml = Provenance.ToYaml(Provenance.ParseSources(probeSources));
        Assert.True(
            Shape(viaToYaml) != Shape(probeSources),
            "The probe no longer discriminates: a ParseSources -> ToYaml round-trip reproduces it, " +
            "so this test could not tell fmt-through-ToYaml from raw-tree fmt. Pick a probe ToYaml " +
            $"still rewrites, and re-check what {DesignDoc} says ToYaml loses. " +
            $"ToYaml gave: {Shape(viaToYaml)}");

        using var tmp = new TempDir();

        // `okf fmt <file>`: the formatted document on stdout.
        var printed = TestPaths.Run(["fmt", tmp.Write("printed.md", Probe)]);
        Assert.Equal(0, printed.Code);
        Assert.Equal("", printed.Err);
        AssertSourcesKept("okf fmt", printed.Out, probeSources);

        // `okf fmt -w <file>`: the formatted document written back to disk, read back from there.
        var written = tmp.Write("written.md", Probe);
        var inPlace = TestPaths.Run(["fmt", written, "-w"]);
        Assert.Equal(0, inPlace.Code);
        Assert.Equal("", inPlace.Err);
        AssertSourcesKept("okf fmt -w", File.ReadAllText(written), probeSources);
    }

    /// <summary>
    /// Smoke check, not proof: counts the source-text mentions of <c>ToYaml</c> (as a whole word,
    /// outside <c>//</c> comment lines) in the <c>.cs</c> files under <c>src/</c>, build output
    /// excluded. It turns a new direct caller into a failing test that names the design doc, since
    /// a second caller — even one that never touches <c>fmt</c> — makes the doc's "builder path
    /// only" wording false. It is a text match, not a symbol lookup: a new, unrelated method that
    /// happens to be named <c>ToYaml</c> trips it too, and it cannot see an indirect call (a wrapper
    /// around <see cref="OkfDocumentBuilder"/>, a delegate, reflection, a name built at run time).
    /// So it is not what protects <c>okf fmt</c>: the behavioural test above is.
    /// </summary>
    [Fact]
    public void Provenance_ToYaml_is_named_only_by_its_declaration_and_OkfDocumentBuilder()
    {
        var src = Path.Combine(TestPaths.RepoRoot(), "src");
        var word = new Regex(@"\bToYaml\b", RegexOptions.CultureInvariant);
        const string Declaration = "OKF4net/Provenance.cs";
        const string Builder = "OKF4net/OkfDocumentBuilder.cs";

        var hits = Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
            .Select(f => (Full: f, Rel: Path.GetRelativePath(src, f).Replace('\\', '/')))
            .Where(f => !f.Rel.Split('/').Any(part => part is "bin" or "obj"))
            .SelectMany(f => File.ReadLines(f.Full)
                .Select((line, i) => (File: f.Rel, Line: i + 1, Text: line)))
            .Where(h => !h.Text.TrimStart().StartsWith("//", StringComparison.Ordinal))
            .Where(h => word.IsMatch(h.Text))
            .ToList();

        var unexpected = hits
            .Where(h => !(h.File == Declaration && h.Text.Contains("public static YamlSequence ToYaml(", StringComparison.Ordinal)))
            .Where(h => h.File != Builder)
            .ToList();
        var builderCalls = hits.Count(h => h.File == Builder);

        Assert.True(
            unexpected.Count == 0 && builderCalls == 1,
            $"Provenance.ToYaml's caller set may have changed ({builderCalls} mention(s) in src/{Builder}; " +
            $"others: {string.Join("; ", unexpected.Select(h => $"src/{h.File}:{h.Line}"))}). " +
            $"{DesignDoc} say ToYaml serves OkfDocumentBuilder alone; widen the caller set only " +
            "together with those sections. This is a whole-word text match: if the new mention is an " +
            "unrelated method that is also named ToYaml, rename it or narrow this check instead.");
    }

    private static void AssertSourcesKept(string command, string document, YamlSequence expected)
    {
        var actual = OkfDocument.Parse(document).Frontmatter.AsMapping().Get("sources");
        Assert.True(
            actual is YamlSequence && Shape(actual) == Shape(expected),
            $"`{command}` rewrote a sources entry (dropped or reordered a key, or added `resource`) — " +
            $"the behaviour of Provenance.ToYaml, not of raw-tree re-serialization. {DesignDoc} say " +
            "fmt cannot lose a field ToYaml does not write; either restore raw-tree fmt or rewrite " +
            $"those sections. Expected {Shape(expected)}, got {(actual is null ? "<no sources>" : Shape(actual))}");
        Assert.Contains("license: CC-BY-4.0", document);
    }

    // Keys of every mapping, in order, recursively; scalars as their display text. Two sequences
    // with the same shape carry the same fields in the same order with the same values.
    private static string Shape(YamlValue value) => value switch
    {
        YamlSequence s => "[" + string.Join(", ", s.Items.Select(Shape)) + "]",
        YamlMapping m => "{" + string.Join(", ", m.Entries.Select(e => $"{e.Key.AsDisplayString()}: {Shape(e.Value)}")) + "}",
        _ => value.AsDisplayString() ?? "<null>",
    };
}
