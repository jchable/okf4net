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
    /// The real guard: the behaviour the design doc relies on, observed end to end through the
    /// CLI. It does not care how <c>fmt</c> is wired, only that it keeps every <c>sources</c> entry
    /// exactly as written, so any rewiring through <see cref="Provenance.ToYaml"/> — direct, aliased,
    /// via a wrapper, a method group or reflection — fails it. The first assertion block proves the
    /// probe discriminates (a ToYaml round-trip really does change it); without that, a future
    /// lossless ToYaml would leave this test green for the wrong reason.
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
        var path = tmp.Write("probe.md", Probe);

        var r = TestPaths.Run(["fmt", path]);

        Assert.Equal(0, r.Code);
        Assert.Equal("", r.Err);
        var fmtSources = OkfDocument.Parse(r.Out).Frontmatter.AsMapping().Get("sources");
        Assert.True(
            fmtSources is YamlSequence && Shape(fmtSources) == Shape(probeSources),
            "`okf fmt` rewrote a sources entry (dropped or reordered a key, or added `resource`) — " +
            $"the behaviour of Provenance.ToYaml, not of raw-tree re-serialization. {DesignDoc} say " +
            "fmt cannot lose a field ToYaml does not write; either restore raw-tree fmt or rewrite " +
            $"those sections. Expected {Shape(probeSources)}, got {(fmtSources is null ? "<no sources>" : Shape(fmtSources))}");
        Assert.Contains("license: CC-BY-4.0", r.Out);
    }

    /// <summary>
    /// Smoke check, not proof: counts the source-text mentions of <c>ToYaml</c> (as a whole word,
    /// outside <c>//</c> comment lines) under <c>src/</c>. It turns a new direct caller into a failing
    /// test that names the design doc, since a second caller — even one that never touches
    /// <c>fmt</c> — makes the doc's "builder path only" wording false. It cannot see an indirect
    /// call (a wrapper around <see cref="OkfDocumentBuilder"/>, a delegate, reflection, a string
    /// built at run time), so it is not what protects <c>okf fmt</c>: the behavioural test above is.
    /// </summary>
    [Fact]
    public void Provenance_ToYaml_is_named_only_by_its_declaration_and_OkfDocumentBuilder()
    {
        var src = Path.Combine(TestPaths.RepoRoot(), "src");
        var word = new Regex(@"\bToYaml\b", RegexOptions.CultureInvariant);
        var sep = Path.DirectorySeparatorChar;

        var hits = Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{sep}bin{sep}") && !f.Contains($"{sep}obj{sep}"))
            .SelectMany(f => File.ReadLines(f)
                .Select((line, i) => (File: Path.GetFileName(f), Line: i + 1, Text: line)))
            .Where(h => !h.Text.TrimStart().StartsWith("//", StringComparison.Ordinal))
            .Where(h => word.IsMatch(h.Text))
            .ToList();

        var unexpected = hits
            .Where(h => !(h.File == "Provenance.cs" && h.Text.Contains("public static YamlSequence ToYaml(", StringComparison.Ordinal)))
            .Where(h => h.File != "OkfDocumentBuilder.cs")
            .ToList();
        var builderCalls = hits.Count(h => h.File == "OkfDocumentBuilder.cs");

        Assert.True(
            unexpected.Count == 0 && builderCalls == 1,
            $"Provenance.ToYaml's caller set changed ({builderCalls} mention(s) in OkfDocumentBuilder.cs; " +
            $"others: {string.Join("; ", unexpected.Select(h => $"{h.File}:{h.Line}"))}). " +
            $"{DesignDoc} say ToYaml serves OkfDocumentBuilder alone; widen the caller set only " +
            "together with those sections.");
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
