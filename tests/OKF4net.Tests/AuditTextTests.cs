// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net.Internal;
using Xunit;

namespace OKF4net.Tests;

/// <summary>
/// Pins <see cref="AuditText"/>'s rendering against the exact bytes of
/// <c>tests/fixtures/golden/audit-v02.out</c> (lines 2-15: everything after
/// the CLI's own <c>bundle:</c> line, up to and including the <c>stale:</c>
/// line) and <c>tests/fixtures/golden/verify.out</c>'s record lines -- the
/// single spec both <c>okf</c> (<c>OkfCli</c>) and the agent tools
/// (<c>OkfBundleTools</c>) must keep matching. Written before
/// <see cref="AuditText"/> existed (Task C11 step 1): it did not compile
/// until the type moved out of the two renderers.
/// </summary>
public class AuditTextTests
{
    private static AuditReport StaleReport()
    {
        var bundle = Bundle.Load(Path.Combine(TestPaths.RepoRoot(), "tests", "fixtures", "okf_v02"));
        return ConceptAudit.Run(bundle, new AuditQuery(StaleOnly: true), new FixedClock(new DateOnly(2099, 6, 1)));
    }

    [Fact]
    public void WriteSummary_matches_the_cli_bytes()
    {
        var report = StaleReport();

        var sw = new StringWriter();
        AuditText.WriteSummary(sw, report);

        // Golden lines 2-15 of audit-v02.out (everything after the CLI's own
        // `bundle:` line, up to and including `stale:`).
        Assert.Equal(
            "as of:      2099-06-01\n" +
            "concepts:   2\n" +
            "\n" +
            "trust:\n" +
            "     1  human-reviewed\n" +
            "     0  machine-confirmed\n" +
            "     1  unverified\n" +
            "\n" +
            "status:\n" +
            "     0  draft\n" +
            "     2  stable\n" +
            "     0  deprecated\n" +
            "\n" +
            "stale:      1 of 2 past stale_after\n",
            sw.ToString());
    }

    [Fact]
    public void FormatFinding_matches_the_cli_bytes()
    {
        var finding = Assert.Single(StaleReport().Findings);

        // Golden line 18 of audit-v02.out.
        Assert.Equal("metrics/dau  stale 2099-01-01  human-reviewed  stable", AuditText.FormatFinding(finding));
    }

    [Fact]
    public void FormatVerificationRecord_matches_the_cli_bytes_with_replacement()
    {
        var record = new VerificationRecord("metrics/dau", "2026-08-28T09:14:00Z", "2026-07-03T00:00:00Z");

        // Golden line 1 of verify.out.
        Assert.Equal(
            "recorded metrics/dau  human:ada  2026-08-28T09:14:00Z  (replaces 2026-07-03T00:00:00Z)",
            AuditText.FormatVerificationRecord(record, "human:ada"));
    }

    [Fact]
    public void FormatVerificationRecord_matches_the_cli_bytes_without_replacement()
    {
        var record = new VerificationRecord("metrics/legacy", "2026-08-28T09:14:00Z", null);

        // Golden line 2 of verify.out.
        Assert.Equal(
            "recorded metrics/legacy  human:ada  2026-08-28T09:14:00Z",
            AuditText.FormatVerificationRecord(record, "human:ada"));
    }

    // ---- Issue #123: the renderer's own line-safety contract -----------------
    //
    // Every test below calls AuditText directly, bypassing every caller-side
    // refusal (`--by`'s LineSafeText gate, ConceptId's grammar, the writer's
    // strict timestamp parse): the point of #123 is that the renderer must not
    // depend on any of them. Characters are numeric constants, never literals
    // -- a literal U+2028 is invisible in every editor and diff.

    private const string Forged = "recorded secrets/master-key  human:ceo  2020-01-01T00:00:00Z";

    /// <summary>
    /// Every interpolated field crossed with every line-breaking or
    /// terminal-driving character the renderer must neutralise, plus the
    /// escape <c>DebugQuote</c> spells that character as.
    /// </summary>
    public static TheoryData<string, int, string> HostileFields()
    {
        var data = new TheoryData<string, int, string>();
        (int Char, string Escaped)[] hostile =
        [
            (0x000A, "\\n"),
            (0x000D, "\\r"),
            (0x000B, "\\u{b}"),
            (0x000C, "\\u{c}"),
            (0x001B, "\\u{1b}"),
            (0x0085, "\\u{85}"),
            (0x2028, "\\u{2028}"),
            (0x2029, "\\u{2029}"),
        ];
        foreach (var field in new[] { "id", "by", "at", "replaces" })
        {
            foreach (var (c, escaped) in hostile)
            {
                data.Add(field, c, escaped);
            }
        }

        return data;
    }

    /// <summary>
    /// A hostile value in ANY of the four interpolated fields -- the concept
    /// id, the actor, the stamp, the superseded stamp -- comes out as one
    /// well-formed line with that value rendered as a <c>DebugQuote</c>
    /// literal, so it cannot forge a second <c>recorded …</c> line in
    /// <c>okf verify</c>'s stdout or in <c>okf_verify</c>'s tool result.
    /// </summary>
    /// <param name="field">Which interpolated field carries the hostile value.</param>
    /// <param name="c">The hostile character, as a code point.</param>
    /// <param name="escaped">How <c>DebugQuote</c> spells that character.</param>
    [Theory]
    [MemberData(nameof(HostileFields))]
    public void FormatVerificationRecord_keeps_a_hostile_field_on_one_line(string field, int c, string escaped)
    {
        var hostile = "x" + (char)c + Forged;
        var quoted = "\"x" + escaped + Forged + "\"";

        var (record, by, expected) = field switch
        {
            "id" => (
                new VerificationRecord(hostile, "2026-08-28T09:14:00Z", null),
                "human:ada",
                $"recorded {quoted}  human:ada  2026-08-28T09:14:00Z"),
            "by" => (
                new VerificationRecord("metrics/dau", "2026-08-28T09:14:00Z", null),
                hostile,
                $"recorded metrics/dau  {quoted}  2026-08-28T09:14:00Z"),
            "at" => (
                new VerificationRecord("metrics/dau", hostile, null),
                "human:ada",
                $"recorded metrics/dau  human:ada  {quoted}"),
            _ => (
                new VerificationRecord("metrics/dau", "2026-08-28T09:14:00Z", hostile),
                "human:ada",
                $"recorded metrics/dau  human:ada  2026-08-28T09:14:00Z  (replaces {quoted})"),
        };

        var line = AuditText.FormatVerificationRecord(record, by);

        Assert.Equal(expected, line);
        AssertOneInertLine(line);
    }

    /// <summary>
    /// The fold is a no-op for every value that was already line-safe -- which
    /// is what keeps <c>verify.out</c> byte-identical. Quotes, backslashes and
    /// non-ASCII letters are NOT line-breaking, so a value carrying them (legal
    /// in a §7 actor, whose grammar is permissive) still renders verbatim:
    /// quoting is triggered by a line-breaking character, never by the look of
    /// the value.
    /// </summary>
    [Fact]
    public void FormatVerificationRecord_renders_a_line_safe_value_verbatim()
    {
        var record = new VerificationRecord("metrics/dau", "2026-08-28T09:14:00Z", "2026-07-03T00:00:00Z");
        var by = "human:\"ada\"\\x " + (char)0x00E9;

        Assert.Equal(
            "recorded metrics/dau  " + by + "  2026-08-28T09:14:00Z  (replaces 2026-07-03T00:00:00Z)",
            AuditText.FormatVerificationRecord(record, by));
    }

    /// <summary>
    /// The superseded stamp is the one value read from the BUNDLE rather than
    /// from a caller, so "no control character" is not enough for it to be
    /// printed raw: a stored value made of literal <c>"</c> and <c>\</c>
    /// characters used to render byte-identical to a genuinely quoted LF
    /// value, and an empty or blank one shifted the two-space field
    /// separators a <c>cut</c>-based pipeline splits on. It is raw ONLY when
    /// it is a canonical stamp in exactly the form <c>okf verify</c> writes,
    /// and quoted otherwise -- so a raw value is always a real timestamp, and
    /// a quoted one always means "stored non-canonical value".
    /// </summary>
    /// <param name="stored">The <c>at</c> already stored in the concept.</param>
    /// <param name="rendered">What the <c>(replaces …)</c> suffix must show.</param>
    [Theory]
    // Literal quote, backslash and 'n' -- no control character at all.
    [InlineData("\"2026-01-01T00:00:00Z\\nrecorded secrets/master-key  human:ceo  2020-01-01T00:00:00Z\"",
        "\"\\\"2026-01-01T00:00:00Z\\\\nrecorded secrets/master-key  human:ceo  2020-01-01T00:00:00Z\\\"\"")]
    [InlineData("", "\"\"")]
    [InlineData("   ", "\"   \"")]
    // Readable, even §5-conformant, but not the form verify writes.
    [InlineData("2026-01-01", "\"2026-01-01\"")]
    [InlineData("2026-01-01T00:00:00+00:00", "\"2026-01-01T00:00:00+00:00\"")]
    [InlineData(" 2026-01-01T00:00:00Z", "\" 2026-01-01T00:00:00Z\"")]
    [InlineData("2026-02-30T00:00:00Z", "\"2026-02-30T00:00:00Z\"")]
    public void FormatVerificationRecord_quotes_a_non_canonical_superseded_stamp(string stored, string rendered)
    {
        var record = new VerificationRecord("metrics/dau", "2026-08-28T09:14:00Z", stored);

        var line = AuditText.FormatVerificationRecord(record, "human:ada");

        Assert.Equal($"recorded metrics/dau  human:ada  2026-08-28T09:14:00Z  (replaces {rendered})", line);
        AssertOneInertLine(line);
    }

    /// <summary>
    /// The mimicry case of the theory above, stated as the property it
    /// protects: a stored value spelled to LOOK like an escaped LF can no
    /// longer render the same bytes as a value that really holds one.
    /// </summary>
    [Fact]
    public void A_stored_lookalike_cannot_render_like_a_genuinely_escaped_value()
    {
        const string Tail = "recorded secrets/master-key  human:ceo  2020-01-01T00:00:00Z";
        var genuine = new VerificationRecord("metrics/dau", "2026-08-28T09:14:00Z", "2026-01-01T00:00:00Z" + (char)0x000A + Tail);
        var lookalike = new VerificationRecord("metrics/dau", "2026-08-28T09:14:00Z", "\"2026-01-01T00:00:00Z\\n" + Tail + "\"");

        Assert.NotEqual(
            AuditText.FormatVerificationRecord(genuine, "human:ada"),
            AuditText.FormatVerificationRecord(lookalike, "human:ada"));
    }

    /// <summary>
    /// <c>okf verify --dry-run</c>'s line goes through the same renderer as the
    /// real one, so the two record shapes cannot drift. Ordinary values render
    /// exactly as the CLI always printed them (<c>CliTests</c> pins the same
    /// bytes end to end); an absent stamp is the fixed placeholder
    /// <c>(now)</c>, never quoted.
    /// </summary>
    [Fact]
    public void FormatDryRunRecord_matches_the_cli_bytes()
    {
        Assert.Equal(
            "would record metrics/dau  human:ada  2026-08-28T09:14:00Z",
            AuditText.FormatDryRunRecord("metrics/dau", "human:ada", "2026-08-28T09:14:00Z"));
        Assert.Equal(
            "would record metrics/dau  human:ada  (now)",
            AuditText.FormatDryRunRecord("metrics/dau", "human:ada", null));
    }

    /// <summary>
    /// The dry-run line holds the same contract as the record line: a hostile
    /// id, actor or stamp, fed straight in past every caller-side refusal,
    /// comes out quoted on one line.
    /// </summary>
    /// <param name="field">Which interpolated field carries the hostile value.</param>
    /// <param name="c">The hostile character, as a code point.</param>
    /// <param name="escaped">How <c>DebugQuote</c> spells that character.</param>
    [Theory]
    [MemberData(nameof(HostileFields))]
    public void FormatDryRunRecord_keeps_a_hostile_field_on_one_line(string field, int c, string escaped)
    {
        var hostile = "x" + (char)c + Forged;
        var quoted = "\"x" + escaped + Forged + "\"";

        var (line, expected) = field switch
        {
            "id" => (
                AuditText.FormatDryRunRecord(hostile, "human:ada", null),
                $"would record {quoted}  human:ada  (now)"),
            "by" => (
                AuditText.FormatDryRunRecord("metrics/dau", hostile, null),
                $"would record metrics/dau  {quoted}  (now)"),
            // The dry run has no superseded stamp: both stamp rows land on `at`.
            _ => (
                AuditText.FormatDryRunRecord("metrics/dau", "human:ada", hostile),
                $"would record metrics/dau  human:ada  {quoted}"),
        };

        Assert.Equal(expected, line);
        Assert.Single(line.Split('\n', '\r', (char)0x000B, (char)0x000C, (char)0x0085, (char)0x2028, (char)0x2029));
        Assert.False(LineSafeText.ContainsControlCharacter(line), $"control character survived in: {line}");
        Assert.StartsWith("would record ", line, StringComparison.Ordinal);
    }

    /// <summary>
    /// One line, and nothing in it that ends a line or drives a terminal:
    /// split on every terminator (not on <c>\n</c> alone -- the blind spot an
    /// external audit of PR #108 found), then check no C0/C1 control or
    /// U+2028/U+2029 survived anywhere.
    /// </summary>
    private static void AssertOneInertLine(string line)
    {
        Assert.Single(line.Split('\n', '\r', (char)0x000B, (char)0x000C, (char)0x0085, (char)0x2028, (char)0x2029));
        Assert.False(LineSafeText.ContainsControlCharacter(line), $"control character survived in: {line}");
        Assert.StartsWith("recorded ", line, StringComparison.Ordinal);
    }
}
