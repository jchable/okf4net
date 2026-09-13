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
}
