// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net.Providers;

namespace OKF4net.Tests.Providers;

public class BundleKeyValidatorTests
{
    private static BundleKeyValidator Strict() => new(BundleKeyConstraints.None, BundleKeyPolicy.Strict);
    private static BundleKeyValidator NativeLinux() => new(BundleKeyConstraintSets.Linux, BundleKeyPolicy.Native);
    private static BundleKeyValidator NativeWindows() => new(BundleKeyConstraintSets.Windows, BundleKeyPolicy.Native);
    private static BundleKeyValidator NativeMacOS() => new(BundleKeyConstraintSets.MacOS, BundleKeyPolicy.Native);

    [Fact]
    public void Strict_is_the_union_of_every_known_native_set()
    {
        var v = Strict();
        Assert.Equal(BundleKeyConstraintSets.Strict, v.Effective);
        Assert.True(v.Effective.HasFlag(BundleKeyConstraints.WindowsReservedNames));
        Assert.True(v.Effective.HasFlag(BundleKeyConstraints.RequireNfc));
        Assert.True(v.Effective.HasFlag(BundleKeyConstraints.FileDirectoryConflict));
    }

    [Fact]
    public void Native_never_drops_below_the_providers_own_constraints()
    {
        var v = NativeWindows();
        Assert.Equal(BundleKeyConstraintSets.Windows, v.Effective);
        Assert.NotNull(v.CheckKey(BundleKey.Parse("con.md")));
    }

    [Theory]
    [InlineData("a./b.md")]      // trailing dot in a directory segment: Windows strips it → alias of a/b.md
    [InlineData("a /b.md")]      // trailing space
    [InlineData("con.md")]
    [InlineData("Con.md")]       // reserved names are case-insensitive
    [InlineData("COM1.md")]
    [InlineData("com\u00b9.md")] // COM¹
    [InlineData("lpt\u00b3/x.md")]
    [InlineData("lPt\u00b9.md")]
    [InlineData("nul.txt.md")]   // reserved base before the first '.'
    [InlineData("a:b.md")]
    [InlineData("a|b.md")]
    [InlineData("a\"b.md")]
    public void Strict_rejects_windows_aliases_and_reserved_names(string raw)
    {
        var key = BundleKey.Parse(raw);
        Assert.NotNull(Strict().CheckKey(key));
        Assert.Null(NativeLinux().CheckKey(key)); // legal on a POSIX filesystem
    }

    [Theory]
    [InlineData("tables/orders.md")]
    [InlineData("a.b.c.md")]
    [InlineData("console.md")]   // 'console' is not 'con'
    [InlineData("com10.md")]     // only COM1..COM9 and the superscripts
    [InlineData("_con.md")]
    [InlineData("foo..md")]      // a trailing dot in the STEM is not an alias; only a whole segment ending with '.' is (a./b.md above)
    public void Strict_accepts_ordinary_names(string raw)
    {
        Assert.Null(Strict().CheckKey(BundleKey.Parse(raw)));
    }

    [Theory]
    [InlineData("caf\u00e9.md")]                 // NFC
    [InlineData("ni\u00f1o/\u00fcber.md")]
    [InlineData("\uAC00\uB098.md")]              // precomposed Hangul
    [InlineData("\u0e17\u0e35\u0e48.md")]        // Thai with stacked marks: none composes
    [InlineData("\u0915\u093f.md")]              // Devanagari KA + vowel sign I
    [InlineData("\u65e5\u672c\u8a9e.md")]
    [InlineData("a\u0335\u0305.md")]             // two non-composing marks in canonical order (class 1, then 230)
    public void Strict_accepts_nfc_names_without_normalizing(string raw)
    {
        Assert.Null(Strict().CheckKey(BundleKey.Parse(raw)));
    }

    [Theory]
    [InlineData("cafe\u0301.md", "U+0301")]   // NFD: composing acute
    [InlineData("\u212b.md", "U+212B")]       // never NFC
    [InlineData("\u1100\u1161.md", "U+1161")] // conjoining jamo
    [InlineData("q\u0323.md", "U+0323")]      // over-rejection, documented: the mark composes with other bases
    [InlineData("x\ufffe.md", "U+FFFE")]      // refused by the normalizer: closed (decision 14)
    [InlineData("a\u0305\u0335.md", "canonical order")] // overline (230) before stroke overlay (1): NFC would reorder them (decision 17)
    public void Strict_refuses_what_the_quick_check_cannot_call_nfc(string raw, string expectedInReason)
    {
        var key = BundleKey.Parse(raw);
        var reason = Strict().CheckKey(key);
        Assert.NotNull(reason);
        Assert.Contains(expectedInReason, reason);
        Assert.Null(NativeLinux().CheckKey(key));
    }

    // Not an [InlineData] theory: attribute arguments are stored in UTF-8 metadata, which
    // turns a lone surrogate into U+FFFD before the test ever sees it (the very aliasing
    // this constraint is about).
    private static readonly (string Raw, string CodeUnit)[] UnpairedSurrogates =
    [
        ("\uD800.sql", "U+D800"),       // isolated high surrogate
        ("a\uDC00.sql", "U+DC00"),      // isolated low surrogate
        ("\uDC00\uD800.sql", "U+DC00"), // a low then a high: no pair
        ("dir\uD83D/x.sql", "U+D83D"),  // in a directory segment, high surrogate before '/'
    ];

    [Fact]
    public void An_unpaired_surrogate_is_refused_wherever_names_are_stored_in_UTF8()
    {
        foreach (var (raw, codeUnit) in UnpairedSurrogates)
        {
            var key = BundleKey.Parse(raw);
            foreach (var v in new[] { Strict(), NativeLinux(), NativeMacOS() })
            {
                var reason = v.CheckKey(key);
                Assert.NotNull(reason);
                Assert.Contains($"unpaired surrogate {codeUnit}", reason);
                Assert.Contains("U+FFFD", reason);
            }

            Assert.Null(NativeWindows().CheckKey(key)); // NTFS stores UTF-16: both names are distinct there
        }
    }

    [Theory]
    [InlineData("\uD83D\uDE00.sql")]     // U+1F600 as a well-formed pair
    [InlineData("a/\uD83D\uDE00/b.sql")]
    public void A_surrogate_pair_is_accepted_everywhere(string raw)
    {
        var key = BundleKey.Parse(raw);
        Assert.Null(Strict().CheckKey(key));
        Assert.Null(NativeLinux().CheckKey(key));
        Assert.Null(NativeMacOS().CheckKey(key));
        Assert.Null(NativeWindows().CheckKey(key));
    }

    [Fact]
    public void Two_names_a_UTF8_encoder_would_merge_both_fail_the_tree_under_native_linux()
    {
        // Both would be written as U+FFFD.sql by a UTF-8 store.
        var r = NativeLinux().ValidateTree(["\uD800.sql", "\uD801.sql", "ok.sql"]);
        Assert.Equal([BundleKey.Parse("ok.sql")], r.Accepted);
        Assert.Equal(2, r.Rejected.Count);
        Assert.All(r.Rejected, rej => Assert.Contains("unpaired surrogate", rej.Reason));
    }

    [Fact]
    public void WellFormedUnicode_is_a_storage_constraint_of_the_UTF8_sets_only()
    {
        Assert.True(BundleKeyConstraintSets.Linux.HasFlag(BundleKeyConstraints.WellFormedUnicode));
        Assert.True(BundleKeyConstraintSets.MacOS.HasFlag(BundleKeyConstraints.WellFormedUnicode));
        Assert.True(BundleKeyConstraintSets.Strict.HasFlag(BundleKeyConstraints.WellFormedUnicode));
        Assert.False(BundleKeyConstraintSets.Windows.HasFlag(BundleKeyConstraints.WellFormedUnicode));
    }

    [Fact]
    public void Tree_rejects_per_level_case_collisions_with_their_subtrees_and_reports_the_prefixes()
    {
        var r = Strict().ValidateTree(["A/x.md", "a/y.md", "b/z.md"]);
        Assert.Equal([BundleKey.Parse("b/z.md")], r.Accepted);
        Assert.Equal(2, r.Rejected.Count);
        Assert.All(r.Rejected, rej => Assert.Contains("collides", rej.Reason));
        Assert.Contains(r.Rejected, rej => rej.RawName == "A/x.md");
        Assert.Contains(r.Rejected, rej => rej.RawName == "a/y.md");
        Assert.Equal([BundleKey.Parse("A"), BundleKey.Parse("a")], r.RejectedPrefixes.OrderBy(k => k).ToList());
    }

    [Theory]
    [InlineData("A\u03a3/x.md", "a\u03c3/y.md")] // capital sigma vs small sigma: one name on NTFS (review-measured)
    [InlineData("a\u03c2/x.md", "a\u03c3/y.md")] // final sigma vs small sigma: NTFS upcases both to Σ
    [InlineData("STRASSE/x.md", "strasse/y.md")]
    public void Tree_folds_like_a_filesystem_upcase_table_not_like_a_linguistic_lowercase(string first, string second)
    {
        var r = Strict().ValidateTree([first, second]);
        Assert.Empty(r.Accepted);
        Assert.Equal(2, r.Rejected.Count);
    }

    // Canonically equivalent siblings: one name on APFS. The NFD spelling is refused by
    // RequireNfc, but it is still THERE, so its NFC twin collides with it and both prefixes
    // are condemned (seventh review: r7 grouped by case fold only and accepted `caf\u00e9/x.md`).
    [Theory]
    [InlineData("caf\u00e9/x.md", "cafe\u0301/y.md")]       // é vs e + acute
    [InlineData("\u00c5/x.md", "\u212b/y.md")]              // Å vs ANGSTROM SIGN
    [InlineData("\uac00/x.md", "\u1100\u1161/y.md")]        // Hangul syllable vs conjoining jamo
    public void Tree_rejects_canonically_equivalent_siblings_under_strict(string nfc, string other)
    {
        var r = Strict().ValidateTree([nfc, other]);
        Assert.Empty(r.Accepted);
        Assert.Equal(2, r.Rejected.Count);
        Assert.Contains(r.Rejected, rej => rej.RawName == nfc && rej.Reason.Contains("collides"));
        Assert.Contains(r.Rejected, rej => rej.RawName == other); // refused by RequireNfc, structurally present all the same
        Assert.Equal(
            new[] { nfc, other }.Select(n => BundleKey.Parse(n).Parent!).OrderBy(k => k).ToList(),
            r.RejectedPrefixes);
    }

    [Theory]
    [InlineData("caf\u00e9/x.md", "cafe\u0301/y.md")]
    [InlineData("\u00c5/x.md", "\u212b/y.md")]
    [InlineData("\u0229\u0301/x.md", "\u00e9\u0327/y.md")] // ȩ + acute vs é + cedilla: equivalent only after reordering (classes 202 < 230)
    public void UnicodeFormCollisions_alone_rejects_both_forms(string nfc, string other)
    {
        // No RequireNfc: both names are admissible on their own (the third row's acute is a
        // `Maybe` mark that Strict would refuse); together they are one object on the volume.
        var v = new BundleKeyValidator(BundleKeyConstraints.UnicodeFormCollisions, BundleKeyPolicy.Native);
        var r = v.ValidateTree([nfc, other]);
        Assert.Empty(r.Accepted);
        Assert.Equal(2, r.Rejected.Count);
        Assert.All(r.Rejected, rej => Assert.Contains("collides", rej.Reason));
        Assert.Equal(2, r.RejectedPrefixes.Count);
    }

    [Fact]
    public void A_native_windows_set_keeps_both_unicode_forms_but_folds_case()
    {
        // NTFS compares code points, not canonical forms: é and e + acute are two files.
        var r = NativeWindows().ValidateTree(["caf\u00e9/x.md", "cafe\u0301/y.md", "CAF\u00c9/z.md"]);
        Assert.Equal([BundleKey.Parse("cafe\u0301/y.md")], r.Accepted);
        Assert.Equal([BundleKey.Parse("CAF\u00c9"), BundleKey.Parse("caf\u00e9")], r.RejectedPrefixes);
    }

    [Fact]
    public void Tree_rejects_deep_case_collisions_too()
    {
        var r = Strict().ValidateTree(["p/q/R/x.md", "p/q/r/y.md", "p/q/s/z.md"]);
        Assert.Equal([BundleKey.Parse("p/q/s/z.md")], r.Accepted);
        Assert.Equal(2, r.Rejected.Count);
        Assert.Contains(BundleKey.Parse("p/q/R"), r.RejectedPrefixes);
    }

    [Fact]
    public void Tree_rejects_file_directory_conflicts_in_both_directions()
    {
        var r = Strict().ValidateTree(["a.md", "a.md/b.md", "ok.md"]);
        Assert.Equal([BundleKey.Parse("ok.md")], r.Accepted);
        Assert.Equal(2, r.Rejected.Count);
        Assert.All(r.Rejected, rej => Assert.Contains("both an object and a prefix", rej.Reason));
        Assert.Equal([BundleKey.Parse("a.md")], r.RejectedPrefixes);
    }

    [Fact]
    public void Tree_keeps_distinct_case_on_a_native_case_sensitive_set()
    {
        var r = NativeLinux().ValidateTree(["A/x.md", "a/y.md"]);
        Assert.Equal(2, r.Accepted.Count);
        Assert.Empty(r.Rejected);
        Assert.Empty(r.RejectedPrefixes);
    }

    [Fact]
    public void Tree_reports_base_rule_failures_and_duplicates_with_the_raw_name()
    {
        var r = Strict().ValidateTree(["bad\\name.md", "dup.md", "dup.md", "fine.md"]);
        Assert.Equal([BundleKey.Parse("fine.md")], r.Accepted);
        Assert.Contains(r.Rejected, rej => rej.RawName == "bad\\name.md" && rej.Reason.Contains("backslash"));
        Assert.Contains(r.Rejected, rej => rej.RawName == "dup.md" && rej.Reason.Contains("twice"));
    }

    [Fact]
    public void A_duplicated_object_still_counts_as_a_prefix_conflict()
    {
        var r = Strict().ValidateTree(["x", "x", "x/y.sql"]);
        Assert.Empty(r.Accepted);
        Assert.Equal([BundleKey.Parse("x")], r.RejectedPrefixes);
        Assert.Contains(r.Rejected, rej => rej.RawName == "x" && rej.Reason.Contains("twice"));
        Assert.Contains(r.Rejected, rej => rej.RawName == "x/y.sql" && rej.Reason.Contains("both an object and a prefix"));
    }

    [Theory]
    [InlineData(new[] { "Ab/x.sql", "Ab/x.sql", "aB/y.sql", "aB/y.sql" }, new[] { "Ab", "aB" })]
    [InlineData(new[] { "x", "x", "x/y.sql", "x/y.sql" }, new[] { "x" })]
    [InlineData(new[] { "x.md", "x.md/A.sql", "x.md/a.sql" }, new[] { "x.md", "x.md/A.sql", "x.md/a.sql" })] // children case-doomed, parent still an object-and-prefix
    public void Prefixes_are_condemned_even_when_every_offender_is_a_duplicate_or_case_doomed(string[] listing, string[] prefixes)
    {
        var r = Strict().ValidateTree(listing);
        Assert.Empty(r.Accepted);
        Assert.Equal(prefixes.Select(BundleKey.Parse).OrderBy(k => k), r.RejectedPrefixes);
    }

    [Fact]
    public void A_restricting_validator_cannot_erase_an_objects_structural_presence()
    {
        // The host refuses the parent: the child must not come back in through the gap.
        var v = new BundleKeyValidator(BundleKeyConstraintSets.Linux, BundleKeyPolicy.Native, [k => k.ToString() == "x.md" ? "refused by the host" : null]);
        var r = v.ValidateTree(["x.md", "x.md/ok.sql"]);
        Assert.Empty(r.Accepted);
        Assert.Equal([BundleKey.Parse("x.md")], r.RejectedPrefixes);

        // Same with a Strict rejection of the child: the parent is still an object-and-prefix.
        var strict = Strict().ValidateTree(["x.md", "x.md/con.sql"]);
        Assert.Empty(strict.Accepted);
        Assert.Equal([BundleKey.Parse("x.md")], strict.RejectedPrefixes);
    }

    [Fact]
    public void An_unadmitted_child_still_makes_its_parent_object_a_prefix()
    {
        // The caller refused `x.md/longname.sql` (a bound): it is still under `x.md`.
        var r = Strict().ValidateTree(["x.md", "ok.md"], ["x.md/longname.sql"]);
        Assert.Equal([BundleKey.Parse("ok.md")], r.Accepted);
        Assert.Equal([BundleKey.Parse("x.md")], r.RejectedPrefixes);
        var rej = Assert.Single(r.Rejected);
        Assert.Equal("x.md", rej.RawName);
        Assert.Contains("both an object and a prefix", rej.Reason);

        // Native Linux keeps FileDirectoryConflict: same outcome without the policy.
        Assert.Equal([BundleKey.Parse("x.md")], NativeLinux().ValidateTree(["x.md"], ["x.md/longname.sql"]).RejectedPrefixes);
    }

    [Fact]
    public void A_sibling_implied_only_by_a_name_over_the_key_bound_still_collides()
    {
        var tooLong = "docs/" + new string('x', BundleKey.MaxUtf8Bytes) + ".md"; // fails TryParse: never a key
        var r = Strict().ValidateTree(["Docs/a.md", tooLong, "ok.md"]);
        Assert.Equal([BundleKey.Parse("ok.md")], r.Accepted);
        Assert.Contains(r.Rejected, rej => rej.RawName == "Docs/a.md" && rej.Reason.Contains("collides"));
        Assert.Contains(r.Rejected, rej => rej.RawName == tooLong && rej.Reason.Contains("longer"));
        Assert.Equal([BundleKey.Parse("Docs"), BundleKey.Parse("docs")], r.RejectedPrefixes);

        // Under a case-sensitive native set, the two directories are distinct.
        Assert.Contains(BundleKey.Parse("Docs/a.md"), NativeLinux().ValidateTree(["Docs/a.md", tooLong]).Accepted);
    }

    [Fact]
    public void A_parent_object_is_condemned_by_a_child_whose_deeper_segment_is_no_key()
    {
        // `x.md/bad<U+0085>` fails the base rules, but `x.md` is still a directory there.
        var r = NativeLinux().ValidateTree(["x.md", "x.md/bad\u0085/y.sql", "ok.md"]);
        Assert.Equal([BundleKey.Parse("ok.md")], r.Accepted);
        Assert.Equal([BundleKey.Parse("x.md")], r.RejectedPrefixes);
        Assert.Contains(r.Rejected, rej => rej.RawName == "x.md" && rej.Reason.Contains("both an object and a prefix"));
        Assert.Contains(r.Rejected, rej => rej.RawName == "x.md/bad\u0085/y.sql" && rej.Reason.Contains("control"));
    }

    [Fact]
    public void A_prefix_only_entry_is_never_accepted_nor_rejected_on_its_own()
    {
        var r = Strict().ValidateTree(["ok.md", "a/b/bad\\name.sql"], ["c/d/longname.sql"]);
        Assert.Equal([BundleKey.Parse("ok.md")], r.Accepted);
        var rej = Assert.Single(r.Rejected); // the unparseable name itself, not `a` nor `a/b`; nothing for `c/d`
        Assert.Equal("a/b/bad\\name.sql", rej.RawName);
        Assert.Empty(r.RejectedPrefixes);

        // And a prefix-only entry colliding with nothing but another prefix-only entry is
        // condemned as a prefix, still with no rejection of its own.
        var both = Strict().ValidateTree(["ok.md"], ["P/longname.sql", "p/longname.sql"]);
        Assert.Equal([BundleKey.Parse("ok.md")], both.Accepted);
        Assert.Empty(both.Rejected);
        Assert.Equal([BundleKey.Parse("P"), BundleKey.Parse("p")], both.RejectedPrefixes);
    }

    [Fact]
    public void The_outermost_condemned_prefix_is_the_one_named_in_the_reason()
    {
        var r = Strict().ValidateTree(["A/x.md", "a/B/y.md", "a/b/z.md"]);
        Assert.Empty(r.Accepted);
        var deep = Assert.Single(r.Rejected, rej => rej.RawName == "a/b/z.md");
        Assert.Contains("'a'", deep.Reason); // not 'a/b': the reader is told the first prefix that fails
    }

    [Fact]
    public void Large_listings_whose_condemned_prefixes_grow_with_them_validate_in_one_pass()
    {
        // 65 536 names whose condemned prefixes grow WITH the listing: 16 384 case-colliding
        // pairs (`p{i}/A` beside `p{i}/a`) and 16 384 file/prefix conflicts (`q{i}` beside
        // `q{i}/child.sql`). That is the shape on which the seventh review measured r7's
        // per-candidate scan of the condemned list: 26 s (collisions) and 13 s (conflicts) for
        // 64 000 names; with one set lookup per ancestor it is under a second. A fixture with
        // two colliding roots — r8's — never exposed that scan (two keys to scan per candidate,
        // 0.4 s either way: eighth review). The assertions are on the outcome — a timing
        // assertion is a flake, not a proof — so a regression to the scan shows up as this
        // test's duration in the run, which Task 8 records.
        var names = new List<string>(65_536);
        for (var i = 0; i < 16_384; i++)
        {
            names.Add($"p{i}/A/x.sql");
            names.Add($"p{i}/a/y.sql");
            names.Add($"q{i}");
            names.Add($"q{i}/child.sql");
        }

        var r = Strict().ValidateTree(names);
        Assert.Empty(r.Accepted);
        Assert.Equal(65_536, r.Rejected.Count);
        Assert.Equal(3 * 16_384, r.RejectedPrefixes.Count); // p{i}/A, p{i}/a and q{i}
        Assert.Contains(BundleKey.Parse("p16383/a"), r.RejectedPrefixes);
        Assert.Contains(BundleKey.Parse("q0"), r.RejectedPrefixes);
    }

    [Theory]
    [InlineData("Ab/x.md", "AB/X.MD", "AB/X.MD", "Ab/x.md")]
    [InlineData("caf\u00e9/x.md", "CAFE\u0301/X.MD", "CAF\u00c9/X.MD", "caf\u00e9/x.md")]   // Strict decomposes then upcases; Windows upcases only; Linux: identity
    [InlineData("\u212b/x.md", "A\u030a/X.MD", "\u212b/X.MD", "\u212b/x.md")]               // ANGSTROM SIGN has no case mapping
    public void FoldKeyText_folds_the_way_the_effective_constraints_compare_siblings(string raw, string strict, string windows, string linux)
    {
        var key = BundleKey.Parse(raw);
        Assert.Equal(strict, Strict().FoldKeyText(key));
        Assert.Equal(windows, NativeWindows().FoldKeyText(key));
        Assert.Equal(linux, NativeLinux().FoldKeyText(key));
        Assert.Equal(strict, NativeMacOS().FoldKeyText(key)); // macOS folds both
    }

    [Fact]
    public void Extra_validators_only_restrict()
    {
        var v = new BundleKeyValidator(BundleKeyConstraintSets.Linux, BundleKeyPolicy.Native,
            [k => k.Segments.Any(s => s.Any(char.IsUpper)) ? "upper-case segment" : null]);
        Assert.NotNull(v.CheckKey(BundleKey.Parse("Tables/x.md")));
        Assert.Null(v.CheckKey(BundleKey.Parse("tables/x.md")));
    }
}
