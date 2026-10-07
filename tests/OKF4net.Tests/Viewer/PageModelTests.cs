// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net.Viewer;

namespace OKF4net.Tests.Viewer;

/// <summary>The centre column's model: display body (C4), page head (C5), frontmatter view (C6), bundle name.</summary>
public class PageModelTests
{
    private static ViewerPage Page(TempDir tmp, string frontmatter, string body = "")
    {
        tmp.Write("c.md", $"---\n{frontmatter}---\n{body}");
        return Assert.Single(SiteModel.Build(Bundle.Load(tmp.Path)).Pages);
    }

    // Spec §11.3, C4. Each row: the body, the display title, the display body.
    [Theory]
    [InlineData("# Gross margin\n\nText.", "Gross margin", "\nText.")]
    [InlineData("\n  \t\n# Gross margin\nText.", "Gross margin", "Text.")]
    [InlineData("# Gross margin\r\nText.", "Gross margin", "Text.")]
    [InlineData("\r\n# Gross margin\r\n\r\nText.", "Gross margin", "\r\nText.")]
    [InlineData("# Gross margin ##\nText.", "Gross margin", "Text.")]
    [InlineData("# Gross margin##\nText.", "Gross margin", "# Gross margin##\nText.")]
    [InlineData("# C#\nText.", "C#", "Text.")]
    [InlineData("# C#\nText.", "C", "# C#\nText.")]
    [InlineData("# ###\nText.", "###", "# ###\nText.")]
    [InlineData("#\tGross margin\nText.", "Gross margin", "Text.")]
    [InlineData("#   Gross    margin   \nText.", "Gross margin", "Text.")]
    [InlineData("# Gross margin\nText.", "  Gross   margin ", "Text.")]
    [InlineData(" # Gross margin\nText.", "Gross margin", " # Gross margin\nText.")]
    [InlineData("## Gross margin\nText.", "Gross margin", "## Gross margin\nText.")]
    [InlineData("#Gross margin\nText.", "Gross margin", "#Gross margin\nText.")]
    [InlineData("Gross margin\n============\nText.", "Gross margin", "Gross margin\n============\nText.")]
    [InlineData("```\n# Gross margin\n```", "Gross margin", "```\n# Gross margin\n```")]
    [InlineData("Intro.\n# Gross margin", "Gross margin", "Intro.\n# Gross margin")]
    [InlineData("# *Gross margin*\nText.", "Gross margin", "# *Gross margin*\nText.")]
    [InlineData("# Gross margin", "Gross margin", "")]
    [InlineData("", "Gross margin", "")]
    // Hostile cases added by the implementer, beyond the brief's 21 rows.
    [InlineData("    # Gross margin\nText.", "Gross margin", "    # Gross margin\nText.")]
    [InlineData("# Gross margin\rText.", "Gross margin", "# Gross margin\rText.")]
    [InlineData("# Gross margin \t##\t \nText.", "Gross margin", "Text.")]
    [InlineData("# Gross margin \\#\nText.", "Gross margin", "# Gross margin \\#\nText.")]
    [InlineData("# Gross margin\n# Gross margin\nText.", "Gross margin", "# Gross margin\nText.")]
    [InlineData("<!-- c -->\n# Gross margin\nText.", "Gross margin", "<!-- c -->\n# Gross margin\nText.")]
    [InlineData("# Gross margin\n\n\n", "Gross margin", "\n\n")]
    [InlineData("# **Gross margin**\nText.", "**Gross margin**", "Text.")]
    [InlineData("# Gross &amp; margin\nText.", "Gross &amp; margin", "Text.")]
    [InlineData("# Gross & margin\nText.", "Gross &amp; margin", "# Gross & margin\nText.")]
    public void Duplicate_title_rule(string body, string title, string expected)
        => Assert.Equal(expected, SiteModel.StripDuplicateTitle(body, title));

    [Fact]
    public void A_page_keeps_its_raw_body_and_carries_the_display_body()
    {
        using var tmp = new TempDir();
        var page = Page(tmp, "type: Note\ntitle: Gross margin\n", "# Gross margin\n\nText.\n");

        Assert.Equal("# Gross margin\n\nText.", page.Body);
        Assert.Equal("\nText.", page.DisplayBody);
    }

    [Theory]
    [InlineData("", null)]
    [InlineData("status: \"\"\n", null)]
    [InlineData("status: [draft]\n", null)]
    [InlineData("status: draft\n", "draft")]
    [InlineData("status: ~\n", null)]
    [InlineData("status: { a: 1 }\n", null)]
    [InlineData("status: \"   \"\n", null)]
    [InlineData("status: \"<b>x</b>\"\n", "<b>x</b>")]
    public void Status_is_shown_only_as_a_non_empty_scalar_and_raw(string extra, string? expected)
    {
        using var tmp = new TempDir();

        Assert.Equal(expected, Page(tmp, $"type: Note\ntitle: T\n{extra}").Head!.Status);
    }

    [Fact]
    public void A_human_tier_shows_its_last_human_verifier_by_id_with_its_date_and_the_others_count()
    {
        using var tmp = new TempDir();
        var head = Page(tmp,
            "type: Note\ntitle: T\nverified:\n"
            + "  - { by: \"human:alice\", at: \"2026-06-01T08:00:00Z\" }\n"
            + "  - { by: \"tool:okf-ci\", at: \"2026-06-02T08:00:00Z\" }\n"
            + "  - { by: \"human:bob\", at: \"2026-07-01T09:00:00Z\" }\n").Head!;

        Assert.Equal("human-reviewed", head.Trust);
        Assert.Equal("bob", head.Verifier);
        Assert.Equal("2026-07-01", head.VerifiedDate);
        Assert.Equal(1, head.MoreVerifications);
    }

    [Fact]
    public void A_machine_tier_shows_its_last_verifier_raw_and_a_date_that_is_not_iso_as_written()
    {
        using var tmp = new TempDir();
        var head = Page(tmp,
            "type: Note\ntitle: T\nverified:\n"
            + "  - { by: \"tool:okf-ci\", at: \"2026-06-02T08:00:00Z\" }\n"
            + "  - { by: \"process:nightly\", at: \"yesterday\" }\n").Head!;

        Assert.Equal("machine-confirmed", head.Trust);
        Assert.Equal("process:nightly", head.Verifier);
        Assert.Equal("yesterday", head.VerifiedDate);
        Assert.Equal(1, head.MoreVerifications);
    }

    [Fact]
    public void A_malformed_human_actor_is_shown_raw()
    {
        using var tmp = new TempDir();
        var head = Page(tmp, "type: Note\ntitle: T\nverified:\n  - { by: \"human:\", at: \"2026-07-01\" }\n").Head!;

        Assert.Equal("human-reviewed", head.Trust);
        Assert.Equal("human:", head.Verifier);
        Assert.Equal("2026-07-01", head.VerifiedDate);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("human:   ")]
    public void An_empty_or_whitespace_verifier_is_absent_but_the_tier_and_date_stand(string by)
    {
        using var tmp = new TempDir();
        var head = Page(tmp, $"type: Note\ntitle: T\nverified:\n  - {{ by: \"{by}\", at: \"2026-06-01T08:00:00Z\" }}\n").Head!;

        Assert.Null(head.Verifier);
        Assert.Equal("2026-06-01", head.VerifiedDate);
        Assert.Equal(by.StartsWith("human:", StringComparison.Ordinal) ? "human-reviewed" : "machine-confirmed", head.Trust);
    }

    [Fact]
    public void A_verifier_is_trimmed()
    {
        using var tmp = new TempDir();
        var head = Page(tmp, "type: Note\ntitle: T\nverified:\n  - { by: \"  tool:okf-ci \", at: \"2026-06-01\" }\n").Head!;

        Assert.Equal("tool:okf-ci", head.Verifier);
    }

    [Fact]
    public void A_verified_entry_without_by_names_no_verifier_and_no_date()
    {
        using var tmp = new TempDir();
        var head = Page(tmp, "type: Note\ntitle: T\nverified: { at: \"2026-07-01\" }\n").Head!;

        Assert.Equal("machine-confirmed", head.Trust);
        Assert.Null(head.Verifier);
        Assert.Null(head.VerifiedDate);
        Assert.Equal(0, head.MoreVerifications);
    }

    [Fact]
    public void An_unverified_concept_names_no_verifier_and_a_missing_date_is_omitted()
    {
        using var tmp = new TempDir();
        Assert.Null(Page(tmp, "type: Note\ntitle: T\n").Head!.Verifier);

        using var other = new TempDir();
        var head = Page(other, "type: Note\ntitle: T\nverified:\n  - { by: \"human:alice\" }\n").Head!;
        Assert.Equal("alice", head.Verifier);
        Assert.Null(head.VerifiedDate);
    }

    [Fact]
    public void The_head_carries_the_type_and_the_stale_date_of_the_index()
    {
        using var tmp = new TempDir();
        var head = Page(tmp, "type: Attested Computation\ntitle: T\nstale_after: \"2026-12-31\"\n").Head!;

        Assert.Equal("Attested Computation", head.Type);
        Assert.Equal("2026-12-31", head.StaleAfterDate);

        using var untyped = new TempDir();
        var bare = Page(untyped, "title: T\n").Head!;
        Assert.Equal(string.Empty, bare.Type);
        Assert.Null(bare.StaleAfterDate);
    }

    [Fact]
    public void Frontmatter_folds_all_but_the_first_four_entries_not_shown_above()
    {
        using var tmp = new TempDir();
        var page = Page(tmp,
            "type: Note\ntitle: T\nstatus: stable\na: 1\nverified: { by: \"human:x\", at: \"2026-01-01\" }\n"
            + "b: 2\nc: 3\nstale_after: \"2027-01-01\"\nd: 4\ne: 5\n");

        Assert.Equal(new[] { "type", "title", "status", "a", "verified", "b", "c", "stale_after", "d", "e" }, page.Frontmatter.Select(e => e.Key));
        Assert.Equal(new[] { "a", "b", "c", "d" }, page.Frontmatter.Where(e => !e.Extra).Select(e => e.Key));
    }

    [Fact]
    public void Frontmatter_joins_a_sequence_of_scalars_and_keeps_other_structures_as_compact_yaml()
    {
        using var tmp = new TempDir();
        var page = Page(tmp,
            "type: Note\ntitle: T\ntags: [finance, margin, attested]\nexecutor:\n  resource: skills/run.md\n"
            + "empty: []\nmixed:\n  - a\n  - { b: 1 }\nn: 3\n");
        var entries = page.Frontmatter.ToDictionary(e => e.Key);

        Assert.Equal("finance, margin, attested", entries["tags"].Value);
        Assert.False(entries["tags"].Structured);
        Assert.True(entries["executor"].Structured);
        Assert.Contains("skills/run.md", entries["executor"].Value, StringComparison.Ordinal);
        Assert.True(entries["empty"].Structured);
        Assert.NotEqual(string.Empty, entries["empty"].Value);
        Assert.True(entries["mixed"].Structured);
        Assert.Equal("3", entries["n"].Value);
        Assert.False(entries["n"].Structured);
    }

    [Fact]
    public void A_hand_built_page_and_entry_have_no_head_no_display_body_and_nothing_folded()
    {
        var entry = new ViewerFrontmatterEntry("k", "v");
        var page = new ViewerPage(ConceptId.Parse("x"), "X", "x.html", [entry], "# X", [], []);

        Assert.False(entry.Extra);
        Assert.False(entry.Structured);
        Assert.Null(page.Head);
        Assert.Null(page.DisplayBody);
    }

    [Fact]
    public void Bundle_name_is_the_folder_name_of_the_bundle_root()
    {
        using var tmp = new TempDir();
        tmp.Write("acme_retail/a.md", "---\ntype: Note\ntitle: A\n---\n");
        var root = Path.Combine(tmp.Path, "acme_retail");

        Assert.Equal("acme_retail", SiteModel.Build(Bundle.Load(root)).BundleName);
        Assert.Equal("acme_retail", SiteModel.BundleNameOf(root + Path.DirectorySeparatorChar));
    }

    [Fact]
    public void Bundle_name_of_a_volume_root_is_bundle()
        => Assert.Equal("bundle", SiteModel.BundleNameOf(Path.GetPathRoot(Path.GetTempPath())!));
}
