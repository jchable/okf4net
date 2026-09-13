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
    /// The governed gate for a control-bearing actor, and the reason both
    /// renderers above it can stay escaping-free. <c>human:ada\nrecorded …</c>
    /// is WELL-FORMED by <see cref="Actor.Parse"/> (a <c>human:</c> prefix and
    /// a non-empty id), so nothing before this check would have stopped it;
    /// interpolated into the CLI's or the tool's line-oriented output it forged
    /// a complete <c>recorded &lt;concept&gt; …</c> line for a concept the
    /// command never touched, at exit 0.
    ///
    /// Refused here rather than escaped at the renderers: this is the single
    /// governed writer of <c>verified</c>, so one check covers the CLI verb,
    /// the <c>okf_verify</c> tool and every future caller — and the value is
    /// rejected, not sanitized, because an actor is an identity.
    /// <see cref="Actor.Parse"/> itself stays permissive on purpose (it is also
    /// the read path for <c>Trust.DeriveTier</c> and <c>BundleValidator</c>),
    /// and <c>okf_write_concept</c> remains an unguarded path by design — so
    /// this is a WRITE-time restriction, not a promise about what a bundle can
    /// hold.
    /// </summary>
    [Theory]
    [InlineData("human:ada\nrecorded secrets/master-key  human:ceo  2020-01-01T00:00:00Z")]
    [InlineData("human:ada\rrecorded x")]
    // ESC: forges appearance in a terminal rather than a new line.
    [InlineData("human:\u001b[2Kada")]
    // U+2028: not char.IsControl, but a line terminator to JavaScript-family
    // splitters, so the predicate names it explicitly.
    [InlineData("human:ada\u2028recorded x")]
    public void An_actor_carrying_a_control_character_is_refused(string by)
    {
        using var tmp = new TempDir();
        tmp.Write("metrics/dau.md", Fm + "---\n\nbody\n");
        var before = Read(tmp, "metrics/dau.md");

        var outcome = WriterOver(tmp).RecordVerifications(["metrics/dau"], by);

        Assert.False(outcome.Recorded);
        Assert.Equal("Error: a §7 actor must not contain control characters.", outcome.Message);
        // The message must not carry the refused value: echoing it would forge
        // a line in the caller's error output instead of the success output.
        Assert.DoesNotContain("recorded", outcome.Message);
        Assert.Empty(outcome.Records);
        Assert.Equal(before, Read(tmp, "metrics/dau.md"));
    }

    /// <summary>
    /// The same trap on <c>at</c>, which stayed open after the actor one was
    /// closed and took an external review to see. Unlike an actor, a malformed
    /// timestamp was ALREADY refused — the strict parse below this check has
    /// always rejected it — so the value never reached a bundle. But the
    /// rejection message QUOTES it, and a caller reading that message line by
    /// line saw a forged <c>recorded …</c> line all the same. Refusing a value
    /// is not the same as refusing to repeat it.
    /// </summary>
    [Theory]
    [InlineData("bad\nrecorded secrets/master-key  human:ceo  2020-01-01T00:00:00Z")]
    [InlineData("2026-08-28T09:14:00Z\rrecorded x")]
    // U+2028: not char.IsControl, but a line terminator to JavaScript-family
    // splitters — kept in step with the actor theory above so the two cannot drift.
    [InlineData("2026-08-28T09:14:00Z\u2028recorded x")]
    public void A_timestamp_carrying_a_control_character_is_refused(string at)
    {
        using var tmp = new TempDir();
        tmp.Write("metrics/dau.md", Fm + "---\n\nbody\n");
        var before = Read(tmp, "metrics/dau.md");

        var outcome = WriterOver(tmp).RecordVerifications(["metrics/dau"], "human:ada", at);

        Assert.False(outcome.Recorded);
        Assert.Equal("Error: a timestamp must not contain control characters.", outcome.Message);
        // The point of the fix: the refused value is not repeated back.
        Assert.DoesNotContain("recorded", outcome.Message);
        Assert.Empty(outcome.Records);
        Assert.Equal(before, Read(tmp, "metrics/dau.md"));
    }

    /// <summary>
    /// <see cref="VerificationOutcome"/> promises errors-as-data, never thrown
    /// — and a hostile-but-loadable concept used to break that promise. A
    /// frontmatter that parses and cannot be re-emitted (see
    /// <see cref="DeepYamlDocument"/>) made <c>YamlEmitter</c> throw a bare
    /// <c>InvalidOperationException</c>, which is not in <c>RunTool</c>'s catch
    /// filter, so it escaped this method entirely — out of <c>okf_verify</c>
    /// into the MCP host, and out of the CLI as a stack trace. The emitter now
    /// signals it as a <c>YamlEmitException</c> (an <see cref="OkfException"/>,
    /// like the parser's own), which that filter already covered.
    ///
    /// The throw lands in the PREPARE loop, before any write, so batch
    /// atomicity holds: nothing is written and <c>Records</c> is empty.
    ///
    /// Since C7 (<see cref="FrontmatterBlockEdit"/>), this method only ever
    /// re-emits the <c>verified</c> block itself through <c>YamlEmitter</c> —
    /// every other key survives as untouched raw text, so the deep nesting
    /// has to live IN the pre-existing <c>verified</c> value for this
    /// scenario to still be reachable (<c>DeepYamlDocument.Text(key: "verified")</c>);
    /// nesting it under an unrelated key, as this test did before C7, would
    /// now be silently carried through untouched and never reach the emitter
    /// at all — the surgical edit's whole point.
    /// </summary>
    [Fact]
    public void A_document_that_parses_but_cannot_be_emitted_is_reported_not_thrown()
    {
        using var tmp = new TempDir();
        tmp.Write("metrics/dau.md", Fm + "---\n\nbody\n");
        tmp.Write("metrics/deep.md", DeepYamlDocument.Text(key: "verified"));
        var before = Read(tmp, "metrics/dau.md");

        var outcome = WriterOver(tmp).RecordVerifications(["metrics/dau", "metrics/deep"], "human:ada");

        Assert.False(outcome.Recorded);
        Assert.Contains("nesting depth limit exceeded", outcome.Message);
        Assert.StartsWith("Error: ", outcome.Message);
        Assert.Empty(outcome.Records);
        // The earlier concept in the batch is untouched: the failure happened
        // while preparing, not while writing.
        Assert.Equal(before, Read(tmp, "metrics/dau.md"));
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
    /// The C7 fix, proven at the writer level (not just <see cref="FrontmatterBlockEditTests"/>,
    /// which never touches <c>RecordVerifications</c> or a real bundle file):
    /// a CRLF document with a column-0 comment, a folded <c>description: &gt;</c>
    /// scalar, and a flow-style <c>tags: [a, b]</c> list is stamped, and the
    /// on-disk result differs from the original by ONLY the <c>verified:</c>
    /// lines — every CRLF ending, the comment, and the folded/flow spellings
    /// survive. Asserted by stripping the <c>verified:</c> block out of both
    /// texts and comparing what remains byte-for-byte, which a substring
    /// assertion on the stamp alone would not catch (it would miss e.g. a
    /// silently normalized CRLF elsewhere in the file).
    /// </summary>
    [Fact]
    public void Only_the_verified_lines_change_on_disk_everything_else_is_byte_identical()
    {
        using var tmp = new TempDir();
        const string before =
            "---\r\n"
            + "type: Metric\r\n"
            + "title: Daily Active Users\r\n"
            + "# reviewed quarterly\r\n"
            + "description: >\r\n"
            + "  Daily\r\n"
            + "  active users.\r\n"
            + "tags: [engagement, kpi]\r\n"
            + "---\r\n"
            + "\r\n"
            + "# Body\r\n";
        tmp.Write("metrics/dau.md", before);

        var outcome = WriterOver(tmp).RecordVerifications(["metrics/dau"], "human:ada");
        Assert.True(outcome.Recorded);

        var after = Read(tmp, "metrics/dau.md");

        // The stamped fields, checked structurally.
        var stamp = Assert.Single(OkfDocument.Parse(after).Frontmatter.Verified);
        Assert.Equal("human:ada", stamp.By!.Value.Raw);
        Assert.Equal("2026-08-28T09:14:00Z", stamp.At);

        // Every other byte, checked by removing the `verified:` lines from
        // both texts and comparing what remains. The stamp was appended
        // (no `verified:` key existed before), so the CRLF-joined lines
        // added are exactly these three.
        var verifiedBlockCrlf = "verified:\r\n  -\r\n    by: human:ada\r\n    at: 2026-08-28T09:14:00Z\r\n";
        Assert.Contains(verifiedBlockCrlf, after);
        Assert.Equal(before, after.Replace(verifiedBlockCrlf, string.Empty));
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

    // --- End-to-end regression coverage for the review round (#C7-1..4) ----
    // FrontmatterBlockEditTests covers the mechanical text edit in isolation;
    // these prove the SAME shapes through the real writer -- UpsertStamp's
    // merge semantics, the equality-based corruption guard, and the actual
    // file on disk -- which is the only way to see whether a "fixed" block
    // edit still adds up to a correct end-to-end stamp.

    /// <summary>
    /// Finding #C7-3: a document that is otherwise all-LF but has ONE CRLF
    /// line in the BODY must keep that one CRLF exactly where it was --
    /// compared as bytes, since <c>OkfDocument.Parse</c> strips '\r' on read
    /// and could never see a regression here.
    /// </summary>
    [Fact]
    public void A_single_CRLF_body_line_in_an_LF_document_is_not_normalized()
    {
        using var tmp = new TempDir();
        const string before = "---\ntype: Metric\ntitle: Daily Active Users\n---\n\nbody line1\r\nline2\n";
        tmp.Write("metrics/dau.md", before);

        var outcome = WriterOver(tmp).RecordVerifications(["metrics/dau"], "human:ada");

        Assert.True(outcome.Recorded);
        Assert.Equal(
            "---\ntype: Metric\ntitle: Daily Active Users\nverified:\n  -\n    by: human:ada\n    at: 2026-08-28T09:14:00Z\n---\n\nbody line1\r\nline2\n",
            Read(tmp, "metrics/dau.md"));
    }

    /// <summary>
    /// Finding #C7-3, the other direction: a document that is otherwise
    /// all-LF but has ONE CRLF frontmatter line (not the one being edited)
    /// must keep it, compared as bytes.
    /// </summary>
    [Fact]
    public void A_single_CRLF_frontmatter_line_in_an_LF_document_is_not_normalized()
    {
        using var tmp = new TempDir();
        const string before = "---\ntype: Metric\ntitle: Daily Active Users\r\n---\n\nbody\n";
        tmp.Write("metrics/dau.md", before);

        var outcome = WriterOver(tmp).RecordVerifications(["metrics/dau"], "human:ada");

        Assert.True(outcome.Recorded);
        Assert.Equal(
            "---\ntype: Metric\ntitle: Daily Active Users\r\nverified:\n  -\n    by: human:ada\n    at: 2026-08-28T09:14:00Z\n---\n\nbody\n",
            Read(tmp, "metrics/dau.md"));
    }

    /// <summary>
    /// Finding #C7-2 / #C7-A (round 2): a bare "---" line INSIDE a
    /// <c>verified: |</c> block scalar's own body is, by
    /// <see cref="OkfDocument.Parse"/>'s own (pre-existing, line-based) fence
    /// scan, mistaken for the closing fence -- so the document's real
    /// "frontmatter" is just <c>type</c> and the malformed <c>verified</c>
    /// block scalar, and everything from <c>title: T</c> onward is already,
    /// per <c>Parse</c> itself, the BODY. The fix does not correct that
    /// pre-existing quirk (it reaches <c>validate</c>/<c>info</c>/<c>search</c>
    /// too and is out of scope here, not because it would move a golden byte
    /// -- no fixture has a fence line with leading or trailing whitespace).
    /// What round 2 adds: <c>FrontmatterBlockEdit</c> now REFUSES outright
    /// when the closing fence line it locates is not itself at column 0,
    /// rather than editing against that misread boundary and merely hoping
    /// the equality check catches anything that went wrong with it -- round
    /// 1 made the edit see the SAME boundary <c>Parse</c> does (closing the
    /// silent body-into-frontmatter corruption #C7-2 was named for), but that
    /// alone was not enough: this exact shape then re-passed the equality
    /// check anyway (both sides consistently misread the same way) and wrote
    /// a file whose visible <c>verified:</c> line sits between two pieces of
    /// a split <c>description</c>, which a spec reader would not accept as a
    /// fence and should never be edited against.
    /// </summary>
    [Fact]
    public void A_bare_fence_inside_a_verified_block_scalar_body_is_refused()
    {
        using var tmp = new TempDir();
        const string before = "---\ntype: M\nverified: |\n  ---\ntitle: T\n---\nBody\n";
        tmp.Write("metrics/x.md", before);

        var outcome = WriterOver(tmp).RecordVerifications(["metrics/x"], "human:ada");

        Assert.False(outcome.Recorded);
        Assert.Contains("indented", outcome.Message);
        Assert.Equal(before, Read(tmp, "metrics/x.md"));
    }

    /// <summary>
    /// Finding #C7-1, end to end: a space-before-the-colon spelling of the
    /// key must be REPLACED in place (merging the existing <c>human:ada</c>
    /// stamp), not left stale with a shadowed duplicate inserted before the
    /// fence -- the exact "reports success while writing a stale, duplicated
    /// key" failure mode the finding is named for.
    /// </summary>
    [Fact]
    public void A_space_before_the_colon_key_is_replaced_not_duplicated_end_to_end()
    {
        using var tmp = new TempDir();
        tmp.Write(
            "metrics/x.md",
            "---\ntype: M\nverified : [{by: human:ada, at: 2025-01-01T00:00:00Z}]\ntitle: T\n---\nbody\n");

        var outcome = WriterOver(tmp).RecordVerifications(["metrics/x"], "human:ada");

        Assert.True(outcome.Recorded);
        Assert.Equal("2025-01-01T00:00:00Z", outcome.Records.Single().ReplacedAt);
        var after = Read(tmp, "metrics/x.md");
        Assert.Equal(
            "---\ntype: M\nverified:\n  -\n    by: human:ada\n    at: 2026-08-28T09:14:00Z\ntitle: T\n---\nbody\n",
            after);
        // Belt and braces on the exact corruption the finding described: only
        // ONE `verified` stamp on disk, not a shadowed duplicate.
        Assert.Single(OkfDocument.Parse(after).Frontmatter.Verified);
    }

    /// <summary>
    /// Finding #C7-2 / #C7-A (round 2): the same mechanism as the block-scalar
    /// case above, but with a real (non-malformed) <c>verified</c> sequence
    /// preceding the stray indented fence -- the key search WOULD find and
    /// correctly merge it (this is not the "hidden verified" shape), but the
    /// closing fence <c>FrontmatterBlockEdit</c> located is still indented,
    /// so it refuses before ever reaching the key search, on the same
    /// column-0 rule as the block-scalar case.
    /// </summary>
    [Fact]
    public void A_bare_fence_indented_inside_a_verified_sequence_is_refused()
    {
        using var tmp = new TempDir();
        const string before = "---\ntype: M\nverified:\n  - {by: human:ada, at: 2025-01-01T00:00:00Z}\n  ---\ntags: [x]\n---\nBody\n";
        tmp.Write("metrics/x.md", before);

        var outcome = WriterOver(tmp).RecordVerifications(["metrics/x"], "human:ada");

        Assert.False(outcome.Recorded);
        Assert.Contains("indented", outcome.Message);
        Assert.Equal(before, Read(tmp, "metrics/x.md"));
    }

    /// <summary>
    /// Finding #C7-A, the exact regression the round-2 review named "x04":
    /// unlike the two shapes above, this one has a GENUINE, pre-existing
    /// <c>verified: [bob]</c> line that Parse's indented-fence misread pushes
    /// into what it considers the BODY -- invisible to the edit entirely.
    /// Before this fix, the edit found no <c>verified</c> key (correctly, per
    /// its own — Parse-consistent — view), inserted a brand-new one before
    /// the misread fence, and the structural-equality check passed (both
    /// `document` and the re-parsed result agreed, since both misread the
    /// SAME way) -- reporting success while the file ended up holding TWO
    /// visible <c>verified:</c> lines, bob's stamp permanently stale and
    /// invisible to every OKF4net tool from then on, and <c>description</c>
    /// split across the inserted block. The column-0 fence check refuses
    /// this before any of that happens.
    /// </summary>
    [Fact]
    public void A_hidden_verified_entry_behind_an_indented_fence_is_refused_not_silently_orphaned()
    {
        using var tmp = new TempDir();
        const string before =
            "---\ndescription: |\n  Intro\n  ---\n  Details\nverified:\n  - {by: human:bob, at: 2025-01-01T00:00:00Z}\ntitle: T\n---\n";
        tmp.Write("metrics/x04.md", before);

        var outcome = WriterOver(tmp).RecordVerifications(["metrics/x04"], "human:ada");

        Assert.False(outcome.Recorded);
        Assert.Contains("indented", outcome.Message);
        Assert.Equal(before, Read(tmp, "metrics/x04.md"));
    }

    /// <summary>
    /// Finding #C7-4: YAML's indentless block-sequence form must be absorbed
    /// into the block, not mistaken for the next top-level key -- proven
    /// end-to-end (a merge that both keeps the untouched <c>process:nightly</c>-
    /// style entry AND appends the new one, through a real write).
    /// </summary>
    [Fact]
    public void An_indentless_sequence_is_stampable_end_to_end()
    {
        using var tmp = new TempDir();
        tmp.Write(
            "metrics/x.md",
            "---\ntype: M\nverified:\n- by: human:bob\n  at: 2025-01-01T00:00:00Z\ntitle: T\n---\nbody\n");

        var outcome = WriterOver(tmp).RecordVerifications(["metrics/x"], "human:ada");

        Assert.True(outcome.Recorded);
        Assert.Null(outcome.Records.Single().ReplacedAt);
        Assert.Equal(
            "---\ntype: M\nverified:\n  -\n    by: human:bob\n    at: 2025-01-01T00:00:00Z\n  -\n    by: human:ada\n    at: 2026-08-28T09:14:00Z\ntitle: T\n---\nbody\n",
            Read(tmp, "metrics/x.md"));
    }

    /// <summary>
    /// Minor finding #9: a pathologically deep value under an UNRELATED key
    /// (the same shape <see cref="DeepYamlDocument"/> uses to make
    /// <c>YamlEmitter</c> throw when the WHOLE frontmatter is re-emitted) is
    /// now stampable at all -- <c>RecordVerifications</c> never hands it to
    /// the emitter -- and survives completely byte-identical.
    /// </summary>
    [Fact]
    public void A_deep_value_under_an_unrelated_key_is_stampable_and_survives_byte_identical()
    {
        using var tmp = new TempDir();
        var before = DeepYamlDocument.Text(key: "deep");
        tmp.Write("metrics/deep.md", before);

        var outcome = WriterOver(tmp).RecordVerifications(["metrics/deep"], "human:ada");

        Assert.True(outcome.Recorded);
        var insertPoint = before.LastIndexOf("---\n\nbody\n", StringComparison.Ordinal);
        Assert.True(insertPoint > 0);
        Assert.Equal(
            before[..insertPoint] + "verified:\n  -\n    by: human:ada\n    at: 2026-08-28T09:14:00Z\n---\n\nbody\n",
            Read(tmp, "metrics/deep.md"));
    }

    /// <summary>
    /// A clean refusal with NOTHING WRITTEN for a hostile shape (round-2
    /// review's "k16"): a NO-BREAK SPACE (U+00A0) sitting on its own line
    /// between two <c>verified</c> sequence entries. The real YAML parser's
    /// blank-line check (<c>string.TrimStart()</c>) treats U+00A0 as
    /// whitespace and skips it, so the document parses fine and both stamps
    /// are visible to <c>UpsertStamp</c>; but <c>FrontmatterBlockEdit</c>'s
    /// own continuation scan only recognizes ASCII space/tab as a
    /// blank-line stand-in, so it stops absorbing lines at the NBSP line --
    /// excluding the SECOND sequence entry from the replaced range while the
    /// emitted replacement (built from the correctly-merged, fully-parsed
    /// value) still includes it.
    ///
    /// <b>This test does NOT, by itself, prove the check discriminates from
    /// a weaker one.</b> A round-3 review found that for THIS shape, the OLD
    /// stamp-COUNT check (<c>Verified.Count != upserted.Count</c>) refuses
    /// too, by coincidence: the re-parsed file ends up with 4 `verified`
    /// entries against 3 expected, so a bare count comparison catches it
    /// just as well as full equality -- reverting to the count check would
    /// leave this test green. <see cref="A_stray_indented_line_inside_a_verified_entry_is_refused_a_count_check_would_miss"/>
    /// is the one that actually discriminates (verified by temporarily
    /// weakening the check and confirming THAT test goes red while this one
    /// and the form-feed test below stay green -- see the C7 fix report,
    /// round 3). This test still earns its place: it is an independent,
    /// hostile input that must never corrupt the file, and every refusal
    /// path deserves its own "nothing written" proof regardless of which
    /// internal check caught it.
    /// </summary>
    [Fact]
    public void A_no_break_space_line_inside_the_verified_sequence_is_refused_not_corrupted()
    {
        using var tmp = new TempDir();
        var nbsp = char.ConvertFromUtf32(0x00A0);
        var before = "---\ntype: M\nverified:\n  - {by: human:bob, at: 2025-01-01T00:00:00Z}\n" + nbsp
            + "\n  - {by: human:carol, at: 2025-02-02T00:00:00Z}\ntitle: T\n---\nbody\n";
        tmp.Write("metrics/x.md", before);

        var outcome = WriterOver(tmp).RecordVerifications(["metrics/x"], "human:ada");

        Assert.False(outcome.Recorded);
        Assert.Contains("could not be edited in place", outcome.Message);
        Assert.Equal(before, Read(tmp, "metrics/x.md"));
    }

    /// <summary>
    /// Same failure mode as the NBSP test above, with a FORM FEED (U+000C)
    /// line instead (round-2 review's "x09") -- a second, independently
    /// constructed shape hitting the same gap between the real parser's
    /// blank-line predicate and this editor's ASCII-only one. Same caveat as
    /// the NBSP test: a bare stamp-count check would ALSO refuse this exact
    /// shape (it too re-parses to 4 entries against 3 expected), so this
    /// test does not by itself discriminate full equality from a count
    /// comparison -- <see cref="A_stray_indented_line_inside_a_verified_entry_is_refused_a_count_check_would_miss"/>
    /// is the one proven (by deliberately weakening the check) to need full
    /// equality. Kept for the same reason: an independent hostile input that
    /// must end in a clean refusal, whichever check catches it.
    /// </summary>
    [Fact]
    public void A_form_feed_line_inside_the_verified_sequence_is_refused_not_corrupted()
    {
        using var tmp = new TempDir();
        var formFeed = char.ConvertFromUtf32(0x000C);
        var before = "---\ntype: M\nverified:\n  - {by: human:bob, at: 2025-01-01T00:00:00Z}\n" + formFeed
            + "\n  - {by: human:carol, at: 2025-02-02T00:00:00Z}\ntitle: T\n---\nbody\n";
        tmp.Write("metrics/x.md", before);

        var outcome = WriterOver(tmp).RecordVerifications(["metrics/x"], "human:ada");

        Assert.False(outcome.Recorded);
        Assert.Contains("could not be edited in place", outcome.Message);
        Assert.Equal(before, Read(tmp, "metrics/x.md"));
    }

    /// <summary>
    /// Minor finding (round 2): a NaN float ANYWHERE in the frontmatter (§4.1
    /// places no constraint against one) makes <c>YamlValue</c>'s structural
    /// equality -- which compares floats with IEEE-754 <c>==</c>, under which
    /// NaN never equals itself -- return false on every verify attempt for
    /// that concept, even a perfectly correct edit. Not fixed (changing
    /// <c>YamlValue.Equals</c> is out of scope here): the refusal message
    /// must at least name the real cause instead of implying real corruption.
    /// Still a clean refusal with nothing written -- a false positive on
    /// "could this be verified", not a false negative on corruption.
    /// </summary>
    [Fact]
    public void A_NaN_float_elsewhere_in_the_frontmatter_is_refused_with_a_diagnosable_message()
    {
        using var tmp = new TempDir();
        const string before = "---\ntype: M\nthreshold: .nan\n---\nbody\n";
        tmp.Write("metrics/x.md", before);

        var outcome = WriterOver(tmp).RecordVerifications(["metrics/x"], "human:ada");

        Assert.False(outcome.Recorded);
        Assert.Contains("NaN", outcome.Message);
        Assert.Equal(before, Read(tmp, "metrics/x.md"));
    }

    /// <summary>
    /// Round-3 finding, the test that actually pins full structural equality
    /// against the OLD stamp-count check: <c>verified:\n  - by: human:bob\n</c>
    /// then a lone NO-BREAK-SPACE line, then an indented continuation
    /// <c>    note: x</c>, then <c>title: T</c>. The real parser folds
    /// <c>note: x</c> into `bob`'s own sequence-item mapping (the NBSP line
    /// is skipped as blank, so `note` sits at the same indent as `by` and
    /// joins it) -- so the ORIGINAL document's `verified` is
    /// <c>[{by: bob, note: x}]</c>, and <c>UpsertStamp</c> appends a clean
    /// <c>{by: ada, at: …}</c>, giving an EXPECTED, two-entry
    /// <c>[{by: bob, note: x}, {by: ada, at: …}]</c>.
    /// <c>FrontmatterBlockEdit</c>'s own continuation scan, though, stops
    /// absorbing at the NBSP line (ASCII-only blank check), so the emitted
    /// replacement (which correctly re-includes bob's `note: x`, since
    /// <c>UpsertStamp</c> read it off the real parse) is followed by the
    /// SAME, now-orphaned <c>note: x</c> line surviving untouched in the
    /// text -- which the real parser then folds into `ada`'s item instead
    /// (nothing stops it: `note: x` sits at ada's own indent too), giving a
    /// RE-PARSED <c>[{by: bob, note: x}, {by: ada, at: …, note: x}]</c>.
    ///
    /// The item COUNT is identical (2 == 2) -- the OLD stamp-count check
    /// (<c>Verified.Count != upserted.Count</c>, the one finding #C7-1
    /// exploited) would see nothing wrong and report success with `note: x`
    /// silently duplicated onto `ada`'s stamp. Only comparing the actual
    /// VALUES (full <see cref="OkfDocument.Equals(OkfDocument?)"/>) sees
    /// `ada`'s extra key and refuses. Proven, not asserted: see the C7 fix
    /// report's round-3 section for the RED output from temporarily
    /// reverting the check to the count comparison and running this exact
    /// test.
    /// </summary>
    [Fact]
    public void A_stray_indented_line_inside_a_verified_entry_is_refused_a_count_check_would_miss()
    {
        using var tmp = new TempDir();
        var nbsp = char.ConvertFromUtf32(0x00A0);
        const string note = "    note: x\n";
        var before = "---\ntype: M\nverified:\n  - by: human:bob\n" + nbsp + "\n" + note + "title: T\n---\nbody\n";
        tmp.Write("metrics/x.md", before);

        var outcome = WriterOver(tmp).RecordVerifications(["metrics/x"], "human:ada");

        Assert.False(outcome.Recorded);
        // The corrected, LOCATED message (round 3, item 2): the divergence
        // is genuinely INSIDE the verified block, not "some other key" --
        // asserted exactly, not just contains(), since a wrong-but-plausible
        // message is exactly what item 2 exists to catch.
        Assert.Equal(
            "Error: concept \"metrics/x\": the verified block could not be edited in place (the verified block itself did not round-trip).",
            outcome.Message);
        Assert.Equal(before, Read(tmp, "metrics/x.md"));
    }
}
