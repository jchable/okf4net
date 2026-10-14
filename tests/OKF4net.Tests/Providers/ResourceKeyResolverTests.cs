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
    [InlineData("../")]               // resolves to the root itself
    [InlineData("/")]
    [InlineData("")]
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
    [InlineData(".")]
    [InlineData("./")]
    public void A_bare_dot_names_the_concepts_own_prefix_not_an_escape(string raw)
    {
        // Decision 10: lexically this is the prefix `computations`, nothing escapes; the
        // caller then finds no object under it and reports Missing. From a top-level
        // concept the prefix is the root, which IS Unsafe (covered by "../" above).
        Assert.Equal(ResourceKeyOutcome.Candidate, ResourceKeyResolver.Resolve(Concept, raw, out var key));
        Assert.Equal(BundleKey.Parse("computations"), key);
        Assert.Equal(ResourceKeyOutcome.Unsafe, ResourceKeyResolver.Resolve(TopLevel, raw, out _));
    }

    [Fact]
    public void A_resolved_key_longer_than_the_bound_is_unsafe()
    {
        var raw = "/" + new string('a', 1030);
        Assert.Equal(ResourceKeyOutcome.Unsafe, ResourceKeyResolver.Resolve(Concept, raw, out _));
    }
}
