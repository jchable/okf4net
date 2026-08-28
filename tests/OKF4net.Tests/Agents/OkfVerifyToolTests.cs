// SPDX-License-Identifier: LGPL-3.0-or-later
using Microsoft.Extensions.AI;
using OKF4net.Agents;

namespace OKF4net.Tests.Agents;

/// <summary>
/// Tests for <c>okf_verify</c>. The tool is symmetric with the CLI verb — same
/// actors accepted, `human:` included — a deliberate decision: a stamp is a
/// declaration, and its credibility comes from landing in a reviewed diff, not
/// from the tool that wrote it. Being a mutator, it belongs to
/// <see cref="OkfBundleTools.WriteToolNames"/> and disappears from a read-only
/// deployment.
/// </summary>
public class OkfVerifyToolTests
{
    private static OkfBundleTools ToolsOver(TempDir tmp) =>
        new(tmp.Path) { UtcNow = () => new DateTime(2026, 8, 28, 9, 14, 0, DateTimeKind.Utc) };

    [Fact]
    public void Verify_records_a_stamp()
    {
        using var tmp = new TempDir();
        tmp.Write("metrics/dau.md", "---\ntype: Metric\n---\n\nbody\n");

        var text = ToolsOver(tmp).Verify("metrics/dau", "human:ada");

        // Byte-identical to the CLI verb's line — the two renderers are
        // separate on purpose, so only an exact assertion keeps them aligned.
        Assert.Equal("recorded metrics/dau  human:ada  2026-08-28T09:14:00Z\n", text);
        Assert.Contains("by: human:ada", File.ReadAllText(Path.Combine(tmp.Path, "metrics", "dau.md")));
    }

    [Fact]
    public void Verify_is_registered_and_is_a_write_tool()
    {
        using var tmp = new TempDir();
        tmp.Write("a.md", "---\ntype: Metric\n---\n\nbody\n");

        Assert.Contains("okf_verify", ToolsOver(tmp).GetTools().OfType<AIFunction>().Select(t => t.Name));
        Assert.Contains("okf_verify", OkfBundleTools.WriteToolNames);
    }

    [Fact]
    public void Verify_returns_a_usage_message_for_a_malformed_actor()
    {
        using var tmp = new TempDir();
        tmp.Write("metrics/dau.md", "---\ntype: Metric\n---\n\nbody\n");

        Assert.Contains("Usage: okf_verify", ToolsOver(tmp).Verify("metrics/dau", "human:"));
    }

    [Fact]
    public void Verify_reports_an_unknown_concept_without_writing()
    {
        using var tmp = new TempDir();
        tmp.Write("metrics/dau.md", "---\ntype: Metric\n---\n\nbody\n");

        var text = ToolsOver(tmp).Verify("metrics/nope", "human:ada");

        Assert.Contains("does not exist", text);
        Assert.False(File.Exists(Path.Combine(tmp.Path, "metrics", "nope.md")));
    }

    /// <summary>
    /// All-or-nothing across the whole list: one unknown id leaves every other
    /// concept untouched. A single-id test cannot catch this.
    /// </summary>
    [Fact]
    public void Verify_refuses_a_concept_named_twice()
    {
        using var tmp = new TempDir();
        tmp.Write("a.md", "---\ntype: Metric\n---\n\nbody\n");
        var before = File.ReadAllText(Path.Combine(tmp.Path, "a.md"));

        var text = ToolsOver(tmp).Verify("a, a", "human:ada");

        Assert.Contains("named more than once", text);
        Assert.Equal(before, File.ReadAllText(Path.Combine(tmp.Path, "a.md")));
    }

    [Fact]
    public void Verify_writes_nothing_when_one_id_of_several_is_unknown()
    {
        using var tmp = new TempDir();
        tmp.Write("a.md", "---\ntype: Metric\n---\n\nbody\n");
        var before = File.ReadAllText(Path.Combine(tmp.Path, "a.md"));

        var text = ToolsOver(tmp).Verify("a, nope", "human:ada");

        Assert.Contains("does not exist", text);
        Assert.DoesNotContain("recorded a", text);
        Assert.Equal(before, File.ReadAllText(Path.Combine(tmp.Path, "a.md")));
    }

    /// <summary>
    /// The schema is what decides what a bare call means, like okf_audit's:
    /// the two ids/actor parameters required, the timestamp optional.
    /// </summary>
    [Fact]
    public void Verify_schema_requires_ids_and_actor_but_not_at()
    {
        var tools = new OkfBundleTools(Path.Combine(TestPaths.RepoRoot(), "tests", "fixtures", "okf_v02"));
        var function = tools.GetTools().OfType<AIFunction>().Single(t => t.Name == "okf_verify");
        var properties = function.JsonSchema.GetProperty("properties");

        foreach (var name in new[] { "conceptIds", "by", "at" })
        {
            Assert.True(properties.TryGetProperty(name, out _), $"schema should declare '{name}'.");
        }

        var required = function.JsonSchema.GetProperty("required").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Contains("conceptIds", required);
        Assert.Contains("by", required);
        Assert.DoesNotContain("at", required);
    }

    /// <summary>
    /// Invoked through the framework's own binding, not by calling the C#
    /// method: the arguments arrive as JSON and must reach the parameters for
    /// the stamp to land. A tool can be registered, schema-correct and still
    /// unusable from a host if that binding is wrong.
    /// </summary>
    [Fact]
    public async Task Verify_stamps_when_invoked_through_the_AIFunction_binding()
    {
        using var tmp = new TempDir();
        tmp.Write("metrics/dau.md", "---\ntype: Metric\n---\n\nbody\n");
        var tools = ToolsOver(tmp);
        var function = tools.GetTools().OfType<AIFunction>().Single(t => t.Name == "okf_verify");

        // Same shape as okf_read_concept's invocation test
        // (AIFunctionExposureTests.cs:223) — including the null-forgiving `!`,
        // which that call needs too.
        var arguments = new AIFunctionArguments(new Dictionary<string, object?>
        {
            ["conceptIds"] = "metrics/dau",
            ["by"] = "human:ada",
            ["at"] = "2026-08-28T09:14:00Z",
        }!);
        await function.InvokeAsync(arguments);

        // The emitter writes sequences in BLOCK style — a bare `-`, then the
        // mapping indented under it (verified by running `okf fmt`) — so assert
        // the two lines, never a flow-style `- { by: …, at: … }`.
        var text = File.ReadAllText(Path.Combine(tmp.Path, "metrics", "dau.md"));
        Assert.Contains("by: human:ada", text);
        Assert.Contains("at: 2026-08-28T09:14:00Z", text);
    }

    /// <summary>A bundle that vanishes after construction surfaces as an error string, never an exception.</summary>
    [Fact]
    public void Verify_returns_an_error_string_when_the_bundle_is_gone()
    {
        var tmp = new TempDir();
        tmp.Write("a.md", "---\ntype: Metric\n---\n\nbody\n");
        var tools = ToolsOver(tmp);
        tmp.Dispose();

        Assert.StartsWith("Error: ", tools.Verify("a", "human:ada"));
    }

    [Fact]
    public void Verify_stamps_every_id_in_a_comma_separated_list()
    {
        using var tmp = new TempDir();
        tmp.Write("a.md", "---\ntype: Metric\n---\n\nbody\n");
        tmp.Write("b.md", "---\ntype: Metric\n---\n\nbody\n");

        var text = ToolsOver(tmp).Verify("a, b", "human:ada");

        Assert.Contains("recorded a  human:ada", text);
        Assert.Contains("recorded b  human:ada", text);
    }
}
