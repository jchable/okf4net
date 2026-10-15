// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net.Providers;

namespace OKF4net.Tests.Providers;

public class ResourceKeyResolverTests
{
    private static readonly BundleKey Concept = BundleKey.Parse("computations/revenue.md");
    private static readonly BundleKey TopLevel = BundleKey.Parse("revenue.md");

    [Theory]
    [InlineData("https://example.test/q.sql")]
    [InlineData("s3://bucket/q.sql")]
    public void A_url_is_never_dereferenced(string raw)
    {
        Assert.Equal(ResourceKeyOutcome.Url, ResourceKeyResolver.Resolve(Concept, raw, out var key));
        Assert.Null(key);
    }

    [Theory]
    [InlineData("/references/a.sql", "references/a.sql")]   // bundle-relative
    [InlineData("references/a.sql", "references/a.sql")]    // bare: from the root (S6.2-1)
    [InlineData("./a.sql", "computations/a.sql")]           // concept-relative
    [InlineData("../references/a.sql", "references/a.sql")]
    [InlineData("a//./b.sql", "a/b.sql")]                   // empty and '.' segments ignored
    [InlineData("%2e%2e/x.sql", "%2e%2e/x.sql")]            // never percent-decoded
    [InlineData("/./a.sql", "a.sql")]
    public void Resolves_the_spec_table_rows_to_keys(string raw, string expected)
    {
        Assert.Equal(ResourceKeyOutcome.Candidate, ResourceKeyResolver.Resolve(Concept, raw, out var key));
        Assert.Equal(BundleKey.Parse(expected), key);
    }

    [Fact]
    public void Concept_relative_from_a_top_level_concept_resolves_from_the_root()
    {
        Assert.Equal(ResourceKeyOutcome.Candidate, ResourceKeyResolver.Resolve(TopLevel, "./a.sql", out var key));
        Assert.Equal(BundleKey.Parse("a.sql"), key);
    }

    [Theory]
    [InlineData("../../q.sql")]       // climbs above the root
    [InlineData("/../q.sql")]
    [InlineData("../../")]            // above the root, trailing '/' or not
    [InlineData("references/../../../")]
    [InlineData("C:\\x.sql")]         // drive forms
    [InlineData("e:q.sql")]
    [InlineData("/e:q.sql")]
    [InlineData("C:/x.sql")]
    [InlineData("references\\a.sql")] // backslash → Unsafe (point to validate 7)
    [InlineData("a\0b.sql")]          // control character
    public void Unsafe_rows(string raw)
    {
        Assert.Equal(ResourceKeyOutcome.Unsafe, ResourceKeyResolver.Resolve(Concept, raw, out var key));
        Assert.Null(key);
    }

    [Theory]
    [InlineData(".", ResourceKeyOutcome.Candidate)]
    [InlineData("./", ResourceKeyOutcome.Prefix)]
    public void A_bare_dot_names_the_concepts_own_prefix_not_an_escape(string raw, ResourceKeyOutcome expected)
    {
        // Decision 10: lexically this is the prefix `computations`, nothing escapes; the
        // caller then finds no object under it and reports Missing (a trailing '/' says so
        // outright). From a top-level concept the prefix is the root itself: Missing too.
        Assert.Equal(expected, ResourceKeyResolver.Resolve(Concept, raw, out var key));
        Assert.Equal(BundleKey.Parse("computations"), key);
        Assert.Equal(ResourceKeyOutcome.Root, ResourceKeyResolver.Resolve(TopLevel, raw, out var rootKey));
        Assert.Null(rootKey);
    }

    /// <summary>
    /// A value that normalises to the bundle root names no object and escapes nothing: on
    /// <c>dev</c> the filesystem resolver reported it <c>Missing</c> (<c>File.Exists</c> on the
    /// root directory), and only a climb ABOVE the root was <c>Unsafe</c>.
    /// </summary>
    [Theory]
    [InlineData("/")]
    [InlineData("//")]
    [InlineData("")]
    [InlineData("../")]               // from computations/revenue.md: its parent is the root
    [InlineData("..")]
    [InlineData("sub/..")]
    [InlineData("a/b/../../")]
    [InlineData("/./")]
    public void A_value_resolving_to_the_root_itself_is_root_not_unsafe(string raw)
    {
        Assert.Equal(ResourceKeyOutcome.Root, ResourceKeyResolver.Resolve(Concept, raw, out var key));
        Assert.Null(key);
    }

    [Theory]
    [InlineData("../")]
    [InlineData("../x/")]
    public void From_a_top_level_concept_a_climb_above_the_root_stays_unsafe(string raw)
    {
        Assert.Equal(ResourceKeyOutcome.Unsafe, ResourceKeyResolver.Resolve(TopLevel, raw, out var key));
        Assert.Null(key);
    }

    /// <summary>
    /// A value ending with '/' names a prefix, never an object: on <c>dev</c>,
    /// <c>File.Exists("…/a.sql/")</c> was false even with the file present, so the validator
    /// said <c>not found</c>. The key is the prefix, for the caller's policy and provider checks.
    /// </summary>
    [Theory]
    [InlineData("references/a.sql/", "references/a.sql")]
    [InlineData("/references/a.sql/", "references/a.sql")]
    [InlineData("references/a.sql//", "references/a.sql")]
    [InlineData("references/a.sql/./", "references/a.sql")]
    [InlineData("references/", "references")]
    [InlineData("../references/", "references")]
    public void A_trailing_slash_names_a_prefix(string raw, string expected)
    {
        Assert.Equal(ResourceKeyOutcome.Prefix, ResourceKeyResolver.Resolve(Concept, raw, out var key));
        Assert.Equal(BundleKey.Parse(expected), key);
    }

    [Theory]
    [InlineData("references\\a.sql/")]  // base rules still win over the trailing '/'
    [InlineData("C:/x/")]
    [InlineData("a\0b/")]
    public void A_trailing_slash_does_not_lift_an_unsafe_value(string raw)
    {
        Assert.Equal(ResourceKeyOutcome.Unsafe, ResourceKeyResolver.Resolve(Concept, raw, out var key));
        Assert.Null(key);
    }

    [Fact]
    public void A_resolved_key_longer_than_the_bound_is_unsafe()
    {
        var raw = "/" + new string('a', 1030);
        Assert.Equal(ResourceKeyOutcome.Unsafe, ResourceKeyResolver.Resolve(Concept, raw, out _));
    }
}
