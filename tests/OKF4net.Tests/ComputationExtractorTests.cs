// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net;
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
}
