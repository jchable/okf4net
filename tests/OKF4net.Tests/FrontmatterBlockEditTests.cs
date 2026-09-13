// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net.Internal;
using Xunit;

namespace OKF4net.Tests;

/// <summary>
/// Tests for <see cref="FrontmatterBlockEdit.ReplaceTopLevelKey"/>: the
/// surgical block-replace primitive behind
/// <see cref="BundleConceptWriter.RecordVerifications"/>'s in-place
/// <c>verified</c> stamp (finding #11). Every test compares the WHOLE
/// document string, not a substring, because the point of this type is that
/// every byte outside the edited block survives untouched — a substring
/// assertion would miss a mangled line ending or a dropped comment elsewhere.
/// </summary>
public class FrontmatterBlockEditTests
{
    private const string Block = "verified:\n  - by: human:ada\n    at: 2026-07-01T00:00:00Z\n";

    [Fact]
    public void Replaces_an_existing_block_and_keeps_every_other_byte()
    {
        var doc = "---\r\ntype: Metric\r\n# reviewed quarterly\r\ndescription: >\r\n  Daily\r\n  users.\r\nverified:\r\n  - by: human:bob\r\n    at: 2025-01-01T00:00:00Z\r\ntags: [a, b]\r\n---\r\n# Body\r\n";
        var edited = FrontmatterBlockEdit.ReplaceTopLevelKey(doc, "verified", Block);
        Assert.Equal("---\r\ntype: Metric\r\n# reviewed quarterly\r\ndescription: >\r\n  Daily\r\n  users.\r\nverified:\r\n  - by: human:ada\r\n    at: 2026-07-01T00:00:00Z\r\ntags: [a, b]\r\n---\r\n# Body\r\n", edited);
    }

    [Fact]
    public void Inserts_before_the_closing_fence_when_the_key_is_absent()
    {
        var doc = "---\ntype: Metric\ntitle: T\n---\nbody\n";
        Assert.Equal("---\ntype: Metric\ntitle: T\nverified:\n  - by: human:ada\n    at: 2026-07-01T00:00:00Z\n---\nbody\n", FrontmatterBlockEdit.ReplaceTopLevelKey(doc, "verified", Block));
    }

    [Fact]
    public void A_flow_valued_key_on_one_line_is_replaced_whole()
    {
        var doc = "---\ntype: Metric\nverified: [{by: human:bob, at: 2025-01-01T00:00:00Z}]\ntitle: T\n---\n";
        var edited = FrontmatterBlockEdit.ReplaceTopLevelKey(doc, "verified", Block);
        Assert.Equal("---\ntype: Metric\n" + Block + "title: T\n---\n", edited);
    }

    [Fact]
    public void A_key_that_is_a_prefix_of_another_is_not_matched()
    {
        var doc = "---\nverified_by_policy: x\ntype: Metric\n---\n";
        var edited = FrontmatterBlockEdit.ReplaceTopLevelKey(doc, "verified", Block);
        Assert.StartsWith("---\nverified_by_policy: x\ntype: Metric\n" + Block, edited, System.StringComparison.Ordinal);
    }

    [Fact]
    public void No_frontmatter_fence_is_refused()
    {
        Assert.Throws<DocumentValidationException>(() => FrontmatterBlockEdit.ReplaceTopLevelKey("# just a body\n", "verified", Block));
    }

    /// <summary>
    /// The round-trip proof the whole type exists for: replacing a key with a
    /// block that has the SAME content as what is already there must leave
    /// the document byte-for-byte unchanged — line endings, trailing newline,
    /// and every unrelated line included. Run for both an LF and a CRLF
    /// document because <c>ReplaceTopLevelKey</c> picks its output line
    /// ending from the document itself (via <c>documentText.Contains("\r\n")</c>),
    /// and a bug in that choice, or in how the trailing newline is
    /// reattached, would only show up as a mismatch here, not in the
    /// "replaces a different value" tests above.
    /// </summary>
    [Theory]
    [InlineData("---\ntype: Metric\n# a comment\nverified:\n  - by: human:ada\n    at: 2026-07-01T00:00:00Z\ntags: [a, b]\n---\nbody\n")]
    [InlineData("---\r\ntype: Metric\r\n# a comment\r\nverified:\r\n  - by: human:ada\r\n    at: 2026-07-01T00:00:00Z\r\ntags: [a, b]\r\n---\r\nbody\r\n")]
    public void Untouched_document_round_trips_when_the_block_is_identical(string doc)
    {
        var edited = FrontmatterBlockEdit.ReplaceTopLevelKey(doc, "verified", Block);
        Assert.Equal(doc, edited);
    }

    // --- Regression coverage for the review round (finding #C7-1..3) -------

    /// <summary>
    /// Finding #C7-1: the parser reads a whitespace-shifted <c>key :</c> as
    /// the key <c>key</c> (<c>YamlMapping.Get</c> would find it there too), so
    /// the edit must locate it there as well — a hand-rolled
    /// column-0-<c>"verified:"</c>-PREFIX check does not, and used to insert a
    /// silently shadowed second block instead of replacing this one.
    /// </summary>
    [Fact]
    public void A_space_before_the_colon_is_still_recognized_as_the_key()
    {
        var doc = "---\ntype: Metric\nverified : [{by: human:bob, at: 2025-01-01T00:00:00Z}]\ntitle: T\n---\n";
        var edited = FrontmatterBlockEdit.ReplaceTopLevelKey(doc, "verified", Block);
        Assert.Equal("---\ntype: Metric\n" + Block + "title: T\n---\n", edited);
    }

    /// <summary>Finding #C7-1, the double-quoted spelling.</summary>
    [Fact]
    public void A_double_quoted_key_is_still_recognized_as_the_key()
    {
        var doc = "---\ntype: Metric\n\"verified\": [{by: human:bob, at: 2025-01-01T00:00:00Z}]\ntitle: T\n---\n";
        var edited = FrontmatterBlockEdit.ReplaceTopLevelKey(doc, "verified", Block);
        Assert.Equal("---\ntype: Metric\n" + Block + "title: T\n---\n", edited);
    }

    /// <summary>Finding #C7-1, the single-quoted spelling.</summary>
    [Fact]
    public void A_single_quoted_key_is_still_recognized_as_the_key()
    {
        var doc = "---\ntype: Metric\n'verified': [{by: human:bob, at: 2025-01-01T00:00:00Z}]\ntitle: T\n---\n";
        var edited = FrontmatterBlockEdit.ReplaceTopLevelKey(doc, "verified", Block);
        Assert.Equal("---\ntype: Metric\n" + Block + "title: T\n---\n", edited);
    }

    /// <summary>
    /// Finding #C7-1's "worse" case: a quoted AND a later plain
    /// <c>verified:</c> line are two GENUINELY separate entries (the real
    /// parser keeps duplicate keys via <c>PushRaw</c>, and
    /// <c>YamlMapping.Get</c>/<c>Insert</c> are first-wins) — only the FIRST
    /// (quoted) one may be touched; the second must survive completely
    /// untouched, not silently destroyed.
    /// </summary>
    [Fact]
    public void A_quoted_key_followed_by_a_separate_plain_duplicate_touches_only_the_first()
    {
        var doc = "---\ntype: Metric\n\"verified\": [{by: human:ada, at: 2025-01-01T00:00:00Z}]\nverified: [{by: human:bob, at: 2025-02-02T00:00:00Z}]\ntitle: T\n---\n";
        var edited = FrontmatterBlockEdit.ReplaceTopLevelKey(doc, "verified", Block);
        Assert.Equal(
            "---\ntype: Metric\n" + Block + "verified: [{by: human:bob, at: 2025-02-02T00:00:00Z}]\ntitle: T\n---\n",
            edited);
    }

    /// <summary>
    /// Finding #C7-2 (closing fence): <see cref="OkfDocument.Parse"/> accepts
    /// a closing fence with trailing whitespace (its own predicate trims), so
    /// this type must too, via the shared <see cref="OkfDocument.IsFenceLine"/>.
    /// </summary>
    [Fact]
    public void A_closing_fence_with_trailing_whitespace_is_recognized()
    {
        var doc = "---\ntype: Metric\ntitle: T\n---  \nbody\n";
        var edited = FrontmatterBlockEdit.ReplaceTopLevelKey(doc, "verified", Block);
        Assert.Equal("---\ntype: Metric\ntitle: T\n" + Block + "---  \nbody\n", edited);
    }

    /// <summary>
    /// Round-2 finding #C7-A: a CLOSING fence with LEADING whitespace (unlike
    /// trailing whitespace, accepted above) is refused, not edited against.
    /// <see cref="OkfDocument.Parse"/>'s own fence scan is indentation-blind
    /// and can mistake such a line for the real closing fence (e.g. one
    /// sitting inside another key's block-scalar body), which would put
    /// content after it -- a genuine <c>verified</c> entry included -- into
    /// what <c>Parse</c> already considers the BODY, invisible to this edit;
    /// stamping would then silently proceed as if that entry did not exist.
    /// The end-to-end version of this scenario lives in
    /// <c>RecordVerificationTests</c>.
    /// </summary>
    [Fact]
    public void A_closing_fence_that_is_actually_indented_is_refused()
    {
        var doc = "---\ntype: Metric\ntitle: T\n  ---\nbody\n";
        var ex = Assert.Throws<DocumentValidationException>(() => FrontmatterBlockEdit.ReplaceTopLevelKey(doc, "verified", Block));
        Assert.Contains("indented", ex.Message);
    }

    /// <summary>
    /// Finding #C7-3's fallback path: when the closing fence being inserted
    /// before is ALSO the file's last line with no trailing newline (so it
    /// has no terminator of its own to copy), the inserted block falls back
    /// to LF when the document has no CRLF anywhere else.
    /// </summary>
    [Fact]
    public void Inserting_before_a_terminatorless_closing_fence_falls_back_to_LF()
    {
        var doc = "---\ntype: Metric\n---";
        var edited = FrontmatterBlockEdit.ReplaceTopLevelKey(doc, "verified", Block);
        Assert.Equal("---\ntype: Metric\n" + Block + "---", edited);
    }

    /// <summary>Same fallback path, but the document uses CRLF elsewhere, so the fallback is CRLF too.</summary>
    [Fact]
    public void Inserting_before_a_terminatorless_closing_fence_falls_back_to_CRLF_when_the_document_uses_it_elsewhere()
    {
        var doc = "---\r\ntype: Metric\r\n---";
        var edited = FrontmatterBlockEdit.ReplaceTopLevelKey(doc, "verified", Block);
        Assert.Equal("---\r\ntype: Metric\r\nverified:\r\n  - by: human:ada\r\n    at: 2026-07-01T00:00:00Z\r\n---", edited);
    }

    /// <summary>Finding #C7-2, the opening fence.</summary>
    [Fact]
    public void An_opening_fence_with_trailing_whitespace_is_recognized()
    {
        var doc = "--- \ntype: Metric\ntitle: T\n---\nbody\n";
        var edited = FrontmatterBlockEdit.ReplaceTopLevelKey(doc, "verified", Block);
        Assert.Equal("--- \ntype: Metric\ntitle: T\n" + Block + "---\nbody\n", edited);
    }

    /// <summary>
    /// Finding #C7-4: YAML's indentless block-sequence form
    /// (<c>OkfDocument.Parse</c> accepts it via
    /// <c>BlockParser.ParseNested</c>) must not be mistaken for "the next
    /// top-level key" and cut the block off mid-sequence.
    /// </summary>
    [Fact]
    public void An_indentless_block_sequence_is_absorbed_into_the_block()
    {
        var doc = "---\ntype: M\nverified:\n- by: human:bob\n  at: 2025-01-01T00:00:00Z\ntitle: T\n---\nbody\n";
        var edited = FrontmatterBlockEdit.ReplaceTopLevelKey(doc, "verified", Block);
        Assert.Equal("---\ntype: M\n" + Block + "title: T\n---\nbody\n", edited);

        // Also prove it re-parses cleanly (the original symptom was a
        // downstream "unexpected trailing content" YAML error, not just a
        // wrong string).
        var reparsed = OkfDocument.Parse(edited);
        Assert.Equal(["type", "verified", "title"], reparsed.Frontmatter.AsMapping().Keys);
        Assert.Equal("body", reparsed.Body);
    }

    /// <summary>
    /// Minor finding #6: a trailing blank line between the block and the next
    /// key is formatting, not part of the value — it must survive, unlike the
    /// (documented, deliberate) column-0-comment case.
    /// </summary>
    [Fact]
    public void A_trailing_blank_line_after_the_block_survives()
    {
        var doc = "---\ntype: M\nverified:\n  - by: human:bob\n    at: 2025-01-01T00:00:00Z\n\ntitle: T\n---\nbody\n";
        var edited = FrontmatterBlockEdit.ReplaceTopLevelKey(doc, "verified", Block);
        Assert.Equal("---\ntype: M\n" + Block + "\ntitle: T\n---\nbody\n", edited);
    }

    /// <summary>
    /// Minor finding #9: a pathologically deep value under an UNRELATED key
    /// is carried through byte-for-byte, never touched — the direct
    /// consequence of editing only the named key's own block. (The
    /// end-to-end version, proving this through the whole
    /// <c>RecordVerifications</c> pipeline including <c>YamlEmitter</c> never
    /// seeing the deep key, lives in <c>RecordVerificationTests</c>.)
    /// </summary>
    [Fact]
    public void A_deep_value_under_an_unrelated_key_is_carried_through_untouched()
    {
        var deep = DeepYamlDocument.Text(key: "deep");
        var edited = FrontmatterBlockEdit.ReplaceTopLevelKey(deep, "verified", Block);

        // Prefix up to the `deep:` block and the block's own content must be
        // byte-identical; only the appended `verified:` block (inserted
        // before the closing fence) and the fence/body after it differ.
        var deepBlockEnd = deep.IndexOf("---\n\nbody\n", StringComparison.Ordinal);
        Assert.True(deepBlockEnd > 0);
        Assert.StartsWith(deep[..deepBlockEnd], edited, StringComparison.Ordinal);
        Assert.Equal(deep[..deepBlockEnd] + Block + "---\n\nbody\n", edited);
    }
}
