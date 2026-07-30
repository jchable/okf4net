// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net;
using OKF4net.Internal;
using Xunit;

namespace OKF4net.Tests;

public class ComputationExtractorTests
{
    private static OkfDocument Doc(string frontmatter, string body) =>
        OkfDocument.Parse("---\n" + frontmatter + "---\n" + body);

    [Fact]
    public void Inline_extracts_first_fenced_block_under_Computation_heading()
    {
        var doc = Doc("type: Attested Computation\nruntime: bigquery\n",
            "# Computation\n\n```sql\nSELECT SUM(amount) AS revenue\nFROM t\nWHERE fiscal_year = @year\n```\n");
        var c = doc.Computation();
        Assert.Equal(ComputationSource.Inline, c.Source);
        Assert.Equal("SELECT SUM(amount) AS revenue\nFROM t\nWHERE fiscal_year = @year", c.InlineCode);
        Assert.Null(c.Path);
    }

    [Fact]
    public void File_based_takes_path_and_ignores_body()
    {
        var doc = Doc("type: Attested Computation\ncomputation: references/computations/revenue.sql\n", "no fence here\n");
        var c = doc.Computation();
        Assert.Equal(ComputationSource.File, c.Source);
        Assert.Equal("references/computations/revenue.sql", c.Path);
        Assert.Null(c.InlineCode);
    }

    [Fact]
    public void Tilde_fence_supported_and_indented_block_ignored()
    {
        var doc = Doc("type: Attested Computation\n",
            "# Computation\n\n~~~\nSELECT 1\n~~~\n");
        Assert.Equal("SELECT 1", doc.Computation().InlineCode);

        var indented = Doc("type: Attested Computation\n", "# Computation\n\n    SELECT 1\n");
        Assert.Null(indented.Computation().InlineCode);   // indenté ≠ fencé (on suit le texte spec)
    }

    [Fact]
    public void No_heading_or_no_fence_yields_no_inline()
    {
        Assert.Null(Doc("type: Attested Computation\n", "no heading\n").Computation().InlineCode);
        Assert.Null(Doc("type: Attested Computation\n", "# Computation\n\nprose only\n").Computation().InlineCode);
    }

    [Fact]
    public void Heading_indented_up_to_three_spaces_is_recognized()
    {
        var doc = Doc("type: Attested Computation\n", "  # Computation\n\n```sql\nSELECT 1\n```\n");
        Assert.Equal("SELECT 1", doc.Computation().InlineCode);
    }

    [Fact]
    public void Unclosed_fence_returns_remainder_to_end_of_input()
    {
        var doc = Doc("type: Attested Computation\n", "# Computation\n\n```sql\nSELECT 1\nSELECT 2\n");
        Assert.Equal("SELECT 1\nSELECT 2", doc.Computation().InlineCode);
    }

    [Fact]
    public void First_of_two_Computation_headings_governs()
    {
        var doc = Doc("type: Attested Computation\n",
            "# Computation\n\n```sql\nSELECT FIRST\n```\n\nSome prose in between.\n\n# Computation\n\n```sql\nSELECT SECOND\n```\n");
        Assert.Equal("SELECT FIRST", doc.Computation().InlineCode);
    }

    [Fact]
    public void Prose_between_heading_and_fence_yields_no_inline()
    {
        var doc = Doc("type: Attested Computation\n", "# Computation\n\nSome prose here.\n\n```sql\nSELECT 1\n```\n");
        Assert.Null(doc.Computation().InlineCode);
    }

    [Fact]
    public void Heading_like_line_inside_an_earlier_unrelated_fence_is_not_treated_as_the_real_heading()
    {
        // The "# Computation" on line 3 is literal content of the fence
        // opened on line 2 -- it is not a real heading, and line 4 (that
        // same fence's own closing ```) must not be mistaken for the
        // opening fence of a sanctioned computation. There is no genuine
        // heading anywhere in this body, so the correct result is null.
        var body = "Some intro text.\n\n```\n# Computation\n```\n\nSELECT ordinary_body_text";
        Assert.Null(ComputationExtractor.ExtractInline(body));
    }

    [Fact]
    public void Genuine_heading_after_an_earlier_fake_in_fence_heading_is_still_found()
    {
        // Same decoy fence as above, properly closed, followed by a real
        // "# Computation" heading and a real fenced block. The scan must
        // skip the decoy and resume looking, rather than either stopping
        // on the decoy or giving up entirely just because an earlier fence
        // existed.
        var body = "Some intro text.\n\n```\n# Computation\n```\n\n# Computation\n\n```sql\nSELECT real_query\n```";
        Assert.Equal("SELECT real_query", ComputationExtractor.ExtractInline(body));
    }

    [Fact]
    public void Tilde_fenced_decoy_heading_is_also_correctly_ignored()
    {
        // Same shape as the backtick decoy, but using ~~~ as the unrelated
        // fence's marker, to confirm the fence-open/close tracking during
        // the heading scan isn't backtick-specific.
        var body = "Some intro text.\n\n~~~\n# Computation\n~~~\n\nSELECT ordinary_body_text";
        Assert.Null(ComputationExtractor.ExtractInline(body));
    }
}
