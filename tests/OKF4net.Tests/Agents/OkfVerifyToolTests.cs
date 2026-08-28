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

    /// <summary>
    /// The write phase cannot be atomic across several files: "a" lands on
    /// disk first, THEN the write to "b" fails (made unwritable below), so
    /// "a" is already stamped by the time the batch fails. This pins the
    /// exact contract fixed twice already — once in the core (b25553b, moving
    /// <c>records.Add</c> out of the prepare loop so <c>Records</c> means
    /// "written", not "prepared") and once in the CLI verb
    /// (<c>CliTests.Verify_prints_the_records_that_landed_before_a_later_write_failure</c>)
    /// — and now here, in the tool, which must render every landed record
    /// BEFORE appending <c>outcome.Message</c> on <c>!outcome.Recorded</c>,
    /// never swallow it. A version of <see cref="OkfBundleTools.Verify"/> that
    /// swapped the records loop and the message append, or that returned
    /// <c>outcome.Message</c> alone on failure, would produce text that does
    /// not start with the "recorded a" line — exactly what this test would
    /// catch and the three all-during-PREPARE tests above cannot, since none
    /// of them ever populates <c>Records</c>.
    ///
    /// Same black-box technique as <c>CliTests</c>'s test: made genuinely
    /// unwritable via the read-only attribute (not the internal
    /// <see cref="BundleConceptWriter.BeforeLateReparseCheckForTest"/> seam,
    /// which is private to whatever <see cref="BundleConceptWriter"/> instance
    /// a test holds a reference to), with the same enforcement probe/skip
    /// guard — some environments (e.g. a CI job running as root on Linux) do
    /// not enforce the read-only bit at all.
    /// </summary>
    [Fact]
    public void Verify_reports_the_records_that_landed_before_a_later_write_failure()
    {
        using var tmp = new TempDir();
        tmp.Write("a.md", "---\ntype: Metric\ntitle: A\n---\n\nbody\n");
        var bPath = tmp.Write("b.md", "---\ntype: Metric\ntitle: B\n---\n\nbody\n");
        var originalB = File.ReadAllText(bPath);
        File.SetAttributes(bPath, File.GetAttributes(bPath) | FileAttributes.ReadOnly);

        try
        {
            try
            {
                File.WriteAllText(bPath, originalB);
                return; // read-only wasn't enforced on this platform/user -- skip.
            }
            catch (UnauthorizedAccessException)
            {
                // Expected: read-only is enforced here, continue.
            }

            var text = ToolsOver(tmp).Verify("a, b", "human:ada");

            // The concept written BEFORE the failure must be reported FIRST,
            // not swallowed by an early `if (!outcome.Recorded) return
            // outcome.Message + "\n";`, and not reordered after the error
            // line. Note: unlike the CLI's own equivalent black-box test
            // (CliTests.Verify_prints_the_records_that_landed_before_a_later_write_failure),
            // this deliberately does not assert on "already written: a" --
            // WriteValidatedContentLocked does not itself catch
            // UnauthorizedAccessException (only BundleConceptWriter's OUTER
            // RunTool does, generically, with no knowledge of `records`), so
            // a genuine I/O failure here never reaches the per-record
            // "{writeResult} — already written: ..." branch at all; that
            // branch is only reachable via the late reparse-point guard's
            // returned (not thrown) error string. Confirmed empirically by
            // running this exact scenario before writing this assertion.
            Assert.StartsWith("recorded a  human:ada  2026-08-28T09:14:00Z\n", text);
            Assert.DoesNotContain("recorded b", text);
            Assert.Contains("Error:", text);
            // The write really landed on disk, not just in memory.
            Assert.Contains("by: human:ada", File.ReadAllText(Path.Combine(tmp.Path, "a.md")));
        }
        finally
        {
            File.SetAttributes(bPath, File.GetAttributes(bPath) & ~FileAttributes.ReadOnly);
        }
    }

    /// <summary>
    /// Re-verifying the same concept with a different <c>at</c> replaces the
    /// prior stamp rather than appending a second one — and the rendered line
    /// carries a <c>(replaces ...)</c> suffix naming the timestamp it
    /// replaced, byte-identical to <c>OkfCli.cs</c>'s <c>CmdVerify</c>
    /// rendering. None of the other tests in this file ever re-verify the
    /// same concept, so <c>record.ReplacedAt</c> is null in all of them and
    /// this branch is otherwise untested.
    /// </summary>
    [Fact]
    public void Verify_renders_a_replaces_suffix_when_reverifying_the_same_concept()
    {
        using var tmp = new TempDir();
        tmp.Write("metrics/dau.md", "---\ntype: Metric\n---\n\nbody\n");
        var tools = ToolsOver(tmp);

        tools.Verify("metrics/dau", "human:ada", "2026-01-01T00:00:00Z");
        var text = tools.Verify("metrics/dau", "human:ada", "2026-02-02T00:00:00Z");

        Assert.Equal(
            "recorded metrics/dau  human:ada  2026-02-02T00:00:00Z  (replaces 2026-01-01T00:00:00Z)\n",
            text);
    }

    /// <summary>
    /// Mirrors the CLI's own resolution loop (<c>OkfCli.cs</c>'s
    /// <c>CmdVerify</c>): every id is checked for existence AND §11
    /// conformance (non-empty <c>type</c>) before anything is written, and the
    /// rejection names the offending id — not a bare, unattributed writer
    /// error — so an agent clearing an <c>okf_audit</c> worklist in one call
    /// does not have to bisect an eight-id batch by hand to find which one
    /// lacks <c>type</c>.
    /// </summary>
    [Fact]
    public void Verify_names_the_non_conformant_concept_when_rejecting_a_batch()
    {
        using var tmp = new TempDir();
        tmp.Write("a.md", "---\ntype: Metric\n---\n\nbody\n");
        tmp.Write("b.md", "---\ntitle: No Type\n---\n\nbody\n");
        var before = File.ReadAllText(Path.Combine(tmp.Path, "a.md"));

        var text = ToolsOver(tmp).Verify("a, b", "human:ada");

        Assert.Contains("concept 'b' has no `type` and is not §11-conformant", text);
        Assert.Equal(before, File.ReadAllText(Path.Combine(tmp.Path, "a.md")));
    }
}
