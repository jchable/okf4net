// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net.Tests;

/// <summary>
/// Tests for <see cref="BundleConceptWriter.RecordVerifications"/>: the single
/// governed writer of the §5.2 <c>verified</c> field. Every test pins the
/// clock through the writer's own <c>UtcNow</c> seam so no assertion depends
/// on the day the suite runs.
/// </summary>
public class RecordVerificationTests
{
    private const string Fm = "---\ntype: Metric\ntitle: Daily Active Users\n";

    private static BundleConceptWriter WriterOver(TempDir tmp) =>
        new(tmp.Path) { UtcNow = () => new DateTime(2026, 8, 28, 9, 14, 0, DateTimeKind.Utc) };

    private static string Read(TempDir tmp, string rel) => File.ReadAllText(Path.Combine(tmp.Path, rel));

    [Fact]
    public void First_stamp_creates_the_list_and_leaves_everything_else_alone()
    {
        using var tmp = new TempDir();
        tmp.Write("metrics/dau.md", Fm + "custom_key: kept\n---\n\n# Body\n");

        var outcome = WriterOver(tmp).RecordVerifications(["metrics/dau"], "human:ada");

        Assert.True(outcome.Recorded);
        Assert.Null(outcome.Records.Single().ReplacedAt);

        // Substring checks would miss a dropped key or a mangled body, so the
        // whole document is compared: the frontmatter is exactly the original
        // keys in order plus `verified`, and the body is untouched.
        var after = OkfDocument.Parse(Read(tmp, "metrics/dau.md"));
        Assert.Equal(["type", "title", "custom_key", "verified"], after.Frontmatter.AsMapping().Keys);
        Assert.Equal("kept", after.Frontmatter.Get("custom_key")!.AsDisplayString());
        // Not "# Body\n": OkfDocument.Parse never returns a trailing newline
        // for a single-trailing-line body (LfLines.Split drops the final
        // empty segment, and Parse strips the leading '\n' left by the blank
        // separator line) -- Serialize() re-adds exactly one on the way out,
        // making this shape idempotent across a parse/serialize round trip.
        // Confirmed against OkfDocument.Parse/Serialize directly, independent
        // of RecordVerifications.
        Assert.Equal("# Body", after.Body);

        var stamp = Assert.Single(after.Frontmatter.Verified);
        Assert.Equal("human:ada", stamp.By!.Value.Raw);
        Assert.Equal("2026-08-28T09:14:00Z", stamp.At);
    }

    [Fact]
    public void Same_actor_replaces_its_own_stamp_in_place()
    {
        using var tmp = new TempDir();
        tmp.Write(
            "metrics/dau.md",
            Fm + "verified:\n  - { by: human:ada, at: 2026-01-01T00:00:00Z }\n"
            + "  - { by: process:nightly, at: 2026-02-02T00:00:00Z }\n---\n\nbody\n");

        var outcome = WriterOver(tmp).RecordVerifications(["metrics/dau"], "human:ada");

        Assert.True(outcome.Recorded);
        Assert.Equal("2026-01-01T00:00:00Z", outcome.Records.Single().ReplacedAt);

        var doc = OkfDocument.Parse(Read(tmp, "metrics/dau.md"));
        var stamps = doc.Frontmatter.Verified;
        Assert.Equal(2, stamps.Count);
        // Position preserved: ada stays first, nightly untouched.
        Assert.Equal("human:ada", stamps[0].By!.Value.Raw);
        Assert.Equal("2026-08-28T09:14:00Z", stamps[0].At);
        Assert.Equal("process:nightly", stamps[1].By!.Value.Raw);
        Assert.Equal("2026-02-02T00:00:00Z", stamps[1].At);
    }

    [Fact]
    public void A_different_actor_is_appended_and_never_touches_another_entry()
    {
        using var tmp = new TempDir();
        tmp.Write(
            "metrics/dau.md",
            Fm + "verified:\n  - { by: human:ada, at: 2026-01-01T00:00:00Z }\n---\n\nbody\n");

        WriterOver(tmp).RecordVerifications(["metrics/dau"], "process:nightly");

        var stamps = OkfDocument.Parse(Read(tmp, "metrics/dau.md")).Frontmatter.Verified;
        Assert.Equal(2, stamps.Count);
        Assert.Equal("human:ada", stamps[0].By!.Value.Raw);
        Assert.Equal("2026-01-01T00:00:00Z", stamps[0].At);
        Assert.Equal("process:nightly", stamps[1].By!.Value.Raw);
    }

    /// <summary>
    /// A permissive reader accepts duplicate entries for one actor (§5.2 says
    /// nothing about uniqueness), so the writer replaces the FIRST match only
    /// and never deletes an entry it is not replacing.
    /// </summary>
    [Fact]
    public void Only_the_first_duplicate_of_an_actor_is_replaced()
    {
        using var tmp = new TempDir();
        tmp.Write(
            "metrics/dau.md",
            Fm + "verified:\n  - { by: human:ada, at: 2026-01-01T00:00:00Z }\n"
            + "  - { by: human:ada, at: 2026-02-02T00:00:00Z }\n---\n\nbody\n");

        WriterOver(tmp).RecordVerifications(["metrics/dau"], "human:ada");

        var stamps = OkfDocument.Parse(Read(tmp, "metrics/dau.md")).Frontmatter.Verified;
        Assert.Equal(2, stamps.Count);
        Assert.Equal("2026-08-28T09:14:00Z", stamps[0].At);
        Assert.Equal("2026-02-02T00:00:00Z", stamps[1].At);
    }

    /// <summary>
    /// `verified: { by, at }` — a single mapping rather than a list — is a
    /// shape <see cref="Trust.ParseVerified"/> accepts (Trust.cs:32), so the
    /// writer must normalize it instead of throwing or overwriting it.
    /// </summary>
    [Fact]
    public void A_single_mapping_verified_is_normalized_to_a_list()
    {
        using var tmp = new TempDir();
        tmp.Write("metrics/dau.md", Fm + "verified: { by: process:nightly, at: 2026-01-01T00:00:00Z }\n---\n\nbody\n");

        WriterOver(tmp).RecordVerifications(["metrics/dau"], "human:ada");

        var stamps = OkfDocument.Parse(Read(tmp, "metrics/dau.md")).Frontmatter.Verified;
        Assert.Equal(2, stamps.Count);
        Assert.Equal("process:nightly", stamps[0].By!.Value.Raw);
        Assert.Equal("human:ada", stamps[1].By!.Value.Raw);
    }

    /// <summary>
    /// A concept named twice is refused rather than collapsed: preparing the
    /// same file twice from the same original content would write it twice and
    /// report two lines for one surviving stamp — a result that reads like two
    /// reviews. Nothing is written.
    /// </summary>
    [Fact]
    public void A_duplicate_concept_id_is_refused()
    {
        using var tmp = new TempDir();
        tmp.Write("metrics/dau.md", Fm + "---\n\nbody\n");
        var before = Read(tmp, "metrics/dau.md");

        var outcome = WriterOver(tmp).RecordVerifications(["metrics/dau", "metrics/dau"], "human:ada");

        Assert.False(outcome.Recorded);
        Assert.Contains("named more than once", outcome.Message);
        Assert.Equal(before, Read(tmp, "metrics/dau.md"));
    }

    /// <summary>
    /// The duplicate guard is checked on the RESOLVED target path, not the raw
    /// id string, so two case-variant spellings of the same concept collide
    /// too on a case-insensitive filesystem (Windows/macOS) — matching the
    /// <c>OrdinalIgnoreCase</c> the <c>BundleLocks</c> registry uses for the
    /// same reason. A raw-string, case-sensitive guard would let this pair
    /// through and write two records for the one stamp that survives.
    /// </summary>
    [Fact]
    public void A_case_variant_duplicate_concept_id_is_refused()
    {
        using var tmp = new TempDir();
        tmp.Write("metrics/dau.md", Fm + "---\n\nbody\n");
        var before = Read(tmp, "metrics/dau.md");

        var outcome = WriterOver(tmp).RecordVerifications(["metrics/dau", "metrics/DAU"], "human:ada");

        Assert.False(outcome.Recorded);
        Assert.Contains("named more than once", outcome.Message);
        Assert.Equal(before, Read(tmp, "metrics/dau.md"));
    }

    /// <summary>
    /// A null element must be rejected as data, not thrown: ConceptId.Parse's
    /// <c>s.Split('/')</c> throws NullReferenceException for a null id, which
    /// is not in RunTool's catch filter — and a JSON binder can hand this
    /// list a null element (e.g. <c>["a", null]</c>) regardless of the
    /// compile-time <c>IReadOnlyList&lt;string&gt;</c> annotation.
    /// </summary>
    [Fact]
    public void A_null_concept_id_in_the_batch_is_refused_without_throwing()
    {
        using var tmp = new TempDir();
        tmp.Write("metrics/dau.md", Fm + "---\n\nbody\n");
        var before = Read(tmp, "metrics/dau.md");

        var outcome = WriterOver(tmp).RecordVerifications(["metrics/dau", null!], "human:ada");

        Assert.False(outcome.Recorded);
        Assert.Contains("must not be empty", outcome.Message);
        Assert.Equal(before, Read(tmp, "metrics/dau.md"));
    }

    /// <summary>
    /// The whole point of a batch is that concept 2 failing rejects concept 1
    /// too, even though concept 1's content was already built successfully in
    /// the prepare loop. A regression that moved validation/writing into a
    /// single per-concept loop (writing as it goes, instead of preparing the
    /// whole batch before writing any of it) would still pass every
    /// single-concept test in this file but fail this one. Also covers the
    /// <see cref="VerificationOutcome.Records"/> contract: rejected during
    /// PREPARE means nothing was ever written, so <c>Records</c> is empty —
    /// not just <c>Recorded == false</c>.
    /// </summary>
    [Fact]
    public void A_later_concept_failing_validation_leaves_an_earlier_one_unwritten()
    {
        using var tmp = new TempDir();
        tmp.Write("metrics/dau.md", Fm + "---\n\nbody\n");
        tmp.Write("metrics/no-type.md", "---\ntitle: No type\n---\n\nbody\n");
        var before = Read(tmp, "metrics/dau.md");

        var outcome = WriterOver(tmp).RecordVerifications(["metrics/dau", "metrics/no-type"], "human:ada");

        Assert.False(outcome.Recorded);
        Assert.Empty(outcome.Records);
        Assert.Equal(before, Read(tmp, "metrics/dau.md"));
        Assert.DoesNotContain("verified", Read(tmp, "metrics/dau.md"));
    }

    [Theory]
    [InlineData("human:", "not a well-formed")]
    [InlineData("", "not a well-formed")]
    public void A_malformed_actor_is_refused(string by, string expected)
    {
        using var tmp = new TempDir();
        tmp.Write("metrics/dau.md", Fm + "---\n\nbody\n");

        var outcome = WriterOver(tmp).RecordVerifications(["metrics/dau"], by);

        Assert.False(outcome.Recorded);
        Assert.Contains(expected, outcome.Message);
        Assert.DoesNotContain("verified", Read(tmp, "metrics/dau.md"));
    }

    /// <summary>
    /// Pins the deliberate divergence from <c>BundleValidator.IsIso8601DateTime</c>
    /// (which validates only the date part and ignores everything after the
    /// <c>T</c>, because reading frontmatter is permissive): a bare date and a
    /// non-UTC offset both pass that permissive predicate, so testing only a
    /// garbage string like "hier" would stay green even if the strict parse
    /// were "simplified" back to it.
    /// </summary>
    [Theory]
    [InlineData("hier")]
    [InlineData("2026-08-28")]
    [InlineData("2026-08-28T09:14:00+02:00")]
    public void A_non_iso_at_is_refused(string at)
    {
        using var tmp = new TempDir();
        tmp.Write("metrics/dau.md", Fm + "---\n\nbody\n");

        var outcome = WriterOver(tmp).RecordVerifications(["metrics/dau"], "human:ada", at);

        Assert.False(outcome.Recorded);
        Assert.Contains("yyyy-MM-ddTHH:mm:ssZ", outcome.Message);
    }

    [Fact]
    public void An_unknown_concept_is_refused_without_creating_it()
    {
        using var tmp = new TempDir();

        var outcome = WriterOver(tmp).RecordVerifications(["metrics/nope"], "human:ada");

        Assert.False(outcome.Recorded);
        Assert.Contains("does not exist", outcome.Message);
        Assert.False(File.Exists(Path.Combine(tmp.Path, "metrics", "nope.md")));
    }

    /// <summary>
    /// Conformance-level validation (§11, non-empty type), NOT producer-grade:
    /// refusing to record a human's review because a third party omitted a
    /// `description` would make exactly the concepts the worklist surfaces
    /// unstampable. See the design spec §4.2.
    /// </summary>
    [Fact]
    public void A_concept_missing_description_is_still_stampable()
    {
        using var tmp = new TempDir();
        tmp.Write("metrics/dau.md", "---\ntype: Metric\n---\n\nbody\n");

        var outcome = WriterOver(tmp).RecordVerifications(["metrics/dau"], "human:ada");

        Assert.True(outcome.Recorded);
        Assert.Contains("by: human:ada", Read(tmp, "metrics/dau.md"));
    }

    [Fact]
    public void A_concept_without_type_is_refused()
    {
        using var tmp = new TempDir();
        tmp.Write("metrics/dau.md", "---\ntitle: No type\n---\n\nbody\n");

        var outcome = WriterOver(tmp).RecordVerifications(["metrics/dau"], "human:ada");

        Assert.False(outcome.Recorded);
        Assert.Contains("type", outcome.Message);
    }

    [Fact]
    public void Generated_is_never_written_or_refreshed()
    {
        using var tmp = new TempDir();
        tmp.Write("a.md", Fm + "generated: { by: okf4net/0.3.0, at: 2020-01-01T00:00:00Z }\n---\n\nbody\n");
        tmp.Write("b.md", Fm + "---\n\nbody\n");

        // AutoStampGenerated defaults to false, so a bare writer would pass this
        // test even if RecordVerifications went through the auto-stamping path.
        // OkfBundleTools turns it ON, which is the configuration that matters.
        var stamping = new BundleConceptWriter(tmp.Path)
        {
            AutoStampGenerated = true,
            UtcNow = () => new DateTime(2026, 8, 28, 9, 14, 0, DateTimeKind.Utc),
        };
        stamping.RecordVerifications(["b"], "human:ada");
        Assert.DoesNotContain("generated", Read(tmp, "b.md"));

        var writer = WriterOver(tmp);
        writer.RecordVerifications(["a"], "human:ada");
        writer.RecordVerifications(["b"], "human:ada");

        Assert.Contains("at: 2020-01-01T00:00:00Z", Read(tmp, "a.md"));
        Assert.DoesNotContain("generated", Read(tmp, "b.md"));
    }

    /// <summary>The tier okf audit reads moves as a direct consequence.</summary>
    [Fact]
    public void The_trust_tier_moves_after_a_stamp()
    {
        using var tmp = new TempDir();
        tmp.Write("metrics/dau.md", Fm + "---\n\nbody\n");
        var writer = WriterOver(tmp);

        Assert.Equal(TrustTier.Unverified, Bundle.Load(tmp.Path).Concepts[0].Document.Frontmatter.TrustTier);

        writer.RecordVerifications(["metrics/dau"], "process:nightly");
        Assert.Equal(TrustTier.MachineConfirmed, Bundle.Load(tmp.Path).Concepts[0].Document.Frontmatter.TrustTier);

        writer.RecordVerifications(["metrics/dau"], "human:ada");
        Assert.Equal(TrustTier.HumanReviewed, Bundle.Load(tmp.Path).Concepts[0].Document.Frontmatter.TrustTier);
    }

    /// <summary>
    /// Two verifications of the same concept must not lose a stamp: the read,
    /// the transform and the write all happen inside one hold of the writer's
    /// bundle lock.
    /// </summary>
    [Fact]
    public void Concurrent_verifications_of_one_concept_both_land()
    {
        using var tmp = new TempDir();
        tmp.Write("metrics/dau.md", Fm + "---\n\nbody\n");
        var writer = WriterOver(tmp);

        Parallel.Invoke(
            () => writer.RecordVerifications(["metrics/dau"], "human:ada"),
            () => writer.RecordVerifications(["metrics/dau"], "process:nightly"));

        var stamps = OkfDocument.Parse(Read(tmp, "metrics/dau.md")).Frontmatter.Verified;
        Assert.Equal(2, stamps.Count);
    }
}
