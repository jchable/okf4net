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
}
