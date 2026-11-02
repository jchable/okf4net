// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net.Providers;

namespace OKF4net.Tests.Providers;

/// <summary>
/// The tree check before a creation (spec §2, decision B10), through the in-memory provider, which
/// can hold any set of names: case and Unicode-form twins, an object that is also a prefix. The
/// validators are built the way the loader builds them, so what the check refuses is exactly what
/// the next load would discard.
/// </summary>
public class BundleTreeCheckTests
{
    private const BundleKeyConstraints Case = BundleKeyConstraints.CaseInsensitiveCollisions;
    private const BundleKeyConstraints Form = BundleKeyConstraints.UnicodeFormCollisions;
    private const BundleKeyConstraints FileDir = BundleKeyConstraints.FileDirectoryConflict;

    private static BundleKey K(string s) => BundleKey.Parse(s);

    private static InMemoryBundleProvider Storage(params string[] names)
    {
        var p = new InMemoryBundleProvider();
        foreach (var name in names)
        {
            p.Add(name, "x");
        }

        return p;
    }

    private static string? Check(InMemoryBundleProvider p, BundleKeyConstraints constraints, string key, BundleLimits? limits = null) =>
        BundleTreeCheck.CheckCreation(p, new BundleKeyValidator(constraints, BundleKeyPolicy.Native), K(key), limits ?? BundleLimits.Default);

    [Fact]
    public void A_validator_without_collision_or_conflict_rules_reads_nothing_and_accepts()
    {
        var p = Storage("A/x.md", "a.md");
        Assert.Null(Check(p, BundleKeyConstraints.TrailingDotOrSpace, "a/y.md"));
        Assert.Null(Check(p, BundleKeyConstraints.None, "a.md/b.md"));
        Assert.Empty(p.Calls);
    }

    [Theory]
    [InlineData("A/x.md", "a/y.md", "\"a/y.md\" cannot be created: \"A\" already exists and differs from \"a\" only in case or Unicode form")]
    [InlineData("d/B/x.md", "d/b/y.md", "\"d/b/y.md\" cannot be created: \"d/B\" already exists and differs from \"d/b\" only in case or Unicode form")]
    [InlineData("d/e/X.md", "d/e/x.md", "\"d/e/x.md\" cannot be created: \"d/e/X.md\" already exists and differs from \"d/e/x.md\" only in case or Unicode form")]
    [InlineData("a/b/c/D/e.md", "a/b/c/d/f.md", "\"a/b/c/d/f.md\" cannot be created: \"a/b/c/D\" already exists and differs from \"a/b/c/d\" only in case or Unicode form")]
    public void A_case_twin_at_any_level_is_refused_and_named(string existing, string created, string expected)
    {
        Assert.Equal(expected, Check(Storage(existing), Case, created));
    }

    [Fact]
    public void Exact_siblings_and_twins_off_the_path_are_no_collision()
    {
        var p = Storage("a/x.md", "a/b/c/d/e.md", "X.md", "x.md", "other/Y.md");
        Assert.Null(Check(p, Case | FileDir, "a/y.md"));
        Assert.Null(Check(p, Case | FileDir, "a/b/c/d/f.md"));
        Assert.Null(Check(p, Case | FileDir, "other/z.md"));
        Assert.Null(Check(p, Case | FileDir, "y.md"));
    }

    [Fact]
    public void A_case_twin_is_accepted_when_the_validator_does_not_fold_case()
    {
        Assert.Null(Check(Storage("A/x.md"), FileDir, "a/y.md"));
    }

    [Fact]
    public void A_Unicode_form_twin_is_refused_only_under_a_validator_that_folds_forms()
    {
        var nfc = "caf" + (char)0x00E9 + ".md";
        var nfd = "cafe" + (char)0x0301 + ".md";
        var p = Storage("d/" + nfc);
        var refused = Check(p, Form, "d/" + nfd);
        Assert.NotNull(refused);
        Assert.Contains("only in case or Unicode form", refused, StringComparison.Ordinal);
        Assert.Null(Check(p, Case, "d/" + nfd));
        Assert.Null(Check(p, BundleKeyConstraints.None, "d/" + nfd));
    }

    [Theory]
    [InlineData(FileDir)]
    [InlineData(FileDir | Case | Form)]
    public void A_proper_prefix_that_is_an_object_is_refused_and_named(BundleKeyConstraints constraints)
    {
        Assert.Equal(
            "\"a.md/b.md\" cannot be created: \"a.md\" is an object, so it cannot also be a prefix of other keys",
            Check(Storage("a.md"), constraints, "a.md/b.md"));
        Assert.Equal(
            "\"d/a.md/b/c.md\" cannot be created: \"d/a.md\" is an object, so it cannot also be a prefix of other keys",
            Check(Storage("d/a.md"), constraints, "d/a.md/b/c.md"));
    }

    [Theory]
    [InlineData(FileDir)]
    [InlineData(FileDir | Case | Form)]
    public void A_key_that_is_already_a_prefix_is_refused_and_names_an_object_under_it(BundleKeyConstraints constraints)
    {
        Assert.Equal(
            "\"a.md\" cannot be created: objects exist under it (such as \"a.md/b.md\"), so it cannot also be an object",
            Check(Storage("a.md/b.md"), constraints, "a.md"));
    }

    [Fact]
    public void Without_the_file_directory_rule_an_object_may_also_be_a_prefix()
    {
        Assert.Null(Check(Storage("a.md"), Case, "a.md/b.md"));
        Assert.Null(Check(Storage("a.md/b.md"), Case, "a.md"));
    }

    [Fact]
    public void Under_folding_the_shallowest_conflict_is_the_one_named()
    {
        var p = Storage("A/x.md", "a/b.md", "a/B/c.md");
        Assert.Equal(
            "\"a/b.md/c.md\" cannot be created: \"A\" already exists and differs from \"a\" only in case or Unicode form",
            Check(p, Case | FileDir, "a/b.md/c.md"));
    }

    [Fact]
    public void Without_folding_it_asks_one_existence_per_proper_prefix_and_lists_only_under_the_key()
    {
        var p = Storage("other.md");
        Assert.Null(Check(p, FileDir, "a/b/c.md"));
        Assert.Equal(["exists a", "exists a/b", "enumerate a/b/c.md"], p.Calls);
    }

    [Fact]
    public void With_folding_it_lists_the_bundle_once()
    {
        var p = Storage("other.md");
        Assert.Null(Check(p, Case | FileDir, "a/b/c.md"));
        Assert.Equal(["enumerate "], p.Calls);
    }

    [Fact]
    public void The_entry_bound_is_enforced_by_the_core_even_when_the_provider_ignores_it()
    {
        var limits = new BundleLimits { MaxEntries = 3 };
        var obeying = Storage("1.md", "2.md", "3.md", "4.md");
        Assert.Throws<BundleLimitExceededException>(() => Check(obeying, Case, "new.md", limits));

        var ignoring = new InMemoryBundleProvider { ObeyLimits = false };
        foreach (var name in new[] { "1.md", "2.md", "3.md", "4.md" })
        {
            ignoring.Add(name, "x");
        }

        var ex = Assert.Throws<BundleLimitExceededException>(() => Check(ignoring, Case, "new.md", limits));
        Assert.Equal(BundleBound.Entries, ex.Bound);
        Assert.Null(Check(ignoring, Case, "new.md", new BundleLimits { MaxEntries = 4 }));
    }

    [Fact]
    public void A_listing_or_an_existence_question_that_fails_propagates()
    {
        var unlistable = Storage("a.md");
        var failure = new IOException("the storage cannot be listed");
        unlistable.FailEnumerateWith = failure;
        Assert.Same(failure, Assert.Throws<IOException>(() => Check(unlistable, Case, "b.md")));
        Assert.Same(failure, Assert.Throws<IOException>(() => Check(unlistable, FileDir, "b.md")));

        var refusing = Storage("a/x.md").RefuseExistsOf("a");
        Assert.Throws<BundleAccessRefusedException>(() => Check(refusing, FileDir, "a/y.md"));
    }
}
