// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net.Agents;
using OKF4net.Providers;
using OKF4net.Tests.Providers;
using OKF4net.Yaml;

namespace OKF4net.Tests.Agents;

/// <summary>
/// #256: the frontmatter <c>okf_read_concept</c> shows is valid YAML of the
/// subset that parses back to the stored mapping, so a read-modify-write
/// through <c>okf_write_concept</c> (the flow the observation gate requires)
/// keeps nested values -- the §5.2 <c>generated</c> map, a <c>verified</c>
/// list of maps, <c>sources</c> -- instead of storing them as flattened strings.
/// </summary>
public class ReadConceptFrontmatterRoundTripTests
{
    private const string Doc =
        "---\n" +
        "type: Metric\n" +
        "title: Weekly revenue\n" +
        "description: \"Revenue: summed per ISO week.\"\n" +
        "tags: [sales, revenue]\n" +
        "generated:\n" +
        "  by: human:me\n" +
        "  at: 2026-11-01T10:00:00Z\n" +
        "verified:\n" +
        "  - by: human:ada\n" +
        "    at: 2026-11-02T09:00:00Z\n" +
        "  - by: agent:checker\n" +
        "    at: 2026-11-03T09:00:00Z\n" +
        "    note: |\n" +
        "      two lines\n" +
        "      of note\n" +
        "sources:\n" +
        "  - title: Finance handbook\n" +
        "    url: https://example.com/handbook\n" +
        "  - ../references/schema.sql\n" +
        "empty_map: {}\n" +
        "empty_list: []\n" +
        "---\n\nBody of the concept.\n";

    [Fact]
    public async Task The_frontmatter_shown_by_read_concept_writes_back_unchanged()
    {
        var storage = new InMemoryBundleProvider(canWrite: true).Add("metrics/revenue.md", Doc);
        var tools = new OkfBundleTools(new BundleHandle(storage));
        var original = OkfDocument.Parse(Doc).Frontmatter.AsMapping();

        var shown = ShownFrontmatter(await tools.ReadConceptTool("metrics/revenue"));

        // What is shown parses, by itself, to the stored mapping.
        Assert.True(original.Equals(YamlParser.Parse(shown)), "shown frontmatter does not parse back to the original:\n" + shown);

        var written = await tools.WriteConceptTool("metrics/revenue", shown, "Body of the concept.\n");
        Assert.StartsWith("Written", written, StringComparison.Ordinal);

        var bundle = await new BundleHandle(storage).GetCurrentAsync(CancellationToken.None);
        var stored = bundle.Get(ConceptId.Parse("metrics/revenue"))!.Document.Frontmatter.AsMapping();
        Assert.True(original.Equals(stored), "stored frontmatter differs from the original:\n" + YamlEmitter.Emit(stored));
        Assert.IsType<YamlMapping>(stored.Get("generated"));
        Assert.IsType<YamlSequence>(stored.Get("verified"));
    }

    /// <summary>
    /// The frontmatter region of a read: the lines between the title header
    /// (and its optional status line) and the body's <c>markdown</c> fence,
    /// without the fence lines of a <c>yaml</c> block if it is one.
    /// </summary>
    private static string ShownFrontmatter(string read)
    {
        var lines = read.Split('\n').ToList();
        var start = 1;
        while (start < lines.Count && (lines[start].Length == 0 || lines[start].StartsWith("> ", StringComparison.Ordinal)))
        {
            start++;
        }

        var end = lines.FindIndex(start, l => l.TrimStart('`') == "markdown" && l.StartsWith("```", StringComparison.Ordinal));
        Assert.True(end > start, "no frontmatter region in:\n" + read);
        var region = lines.GetRange(start, end - start);
        while (region.Count > 0 && region[^1].Length == 0)
        {
            region.RemoveAt(region.Count - 1);
        }

        if (region.Count >= 2 && region[0].StartsWith("```", StringComparison.Ordinal) && region[0].TrimStart('`') == "yaml")
        {
            region = region.GetRange(1, region.Count - 2);
        }

        return string.Join("\n", region) + "\n";
    }
}
