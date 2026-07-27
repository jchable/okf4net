// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net.Tests;

/// <summary>
/// Tests for <c>ConceptId</c> semantics, covering the
/// <c>ValidateSegment</c> / <c>Parse</c> / <c>New</c> / <c>FromPath</c>
/// rules (§2).
/// </summary>
public class ConceptIdTests
{
    [Fact]
    public void Parse_and_tostring_roundtrip()
        => Assert.Equal("tables/users", ConceptId.Parse("tables/users").ToString());

    [Fact]
    public void Name_and_parent()
    {
        var id = ConceptId.Parse("a/b/c");
        Assert.Equal("c", id.Name);
        Assert.Equal("a/b", id.Parent!.ToString());
        Assert.Null(ConceptId.Parse("root").Parent);
    }

    // --- parse() tolerance: empty segments produced by leading/trailing/duplicate
    // slashes are silently dropped, NOT an error. ---

    [Theory]
    [InlineData("a//b", "a/b")]     // duplicate slash collapses
    [InlineData("/a/b", "a/b")]     // leading slash tolerated
    [InlineData("a/b/", "a/b")]     // trailing slash tolerated
    [InlineData("//a//b//", "a/b")]
    public void Parse_tolerates_redundant_slashes(string input, string expected)
        => Assert.Equal(expected, ConceptId.Parse(input).ToString());

    // --- invalid ids: drawn from the actual rules in ValidateSegment and
    // Parse. Note that "a//b" is NOT invalid (see
    // Parse_tolerates_redundant_slashes above) -- only genuinely invalid
    // segments appear here. ---

    [Theory]
    [InlineData("")]                 // empty string -> zero segments
    [InlineData("/")]                 // only slashes -> zero segments
    [InlineData("///")]               // only slashes -> zero segments
    [InlineData("../b")]              // segment ".." starts with '.', invalid leading char
    [InlineData("a/./b")]             // segment "." starts with '.', invalid leading char
    [InlineData("a/b/..")]            // trailing ".." segment
    [InlineData("-abc")]              // leading '-' not allowed as first char
    [InlineData(".abc")]              // leading '.' not allowed as first char
    [InlineData("a/-bc")]             // leading '-' in a non-first segment
    [InlineData("a/.bc")]             // leading '.' in a non-first segment
    [InlineData("a b")]               // space not a permitted char at all
    [InlineData("a@b")]               // '@' not a permitted char
    [InlineData("a/b c")]             // space in a non-first segment
    public void Invalid_ids_throw(string bad)
        => Assert.Throws<ConceptIdException>(() => ConceptId.Parse(bad));

    [Fact]
    public void Empty_string_error_message_uses_debug_quote_format()
    {
        var ex = Assert.Throws<ConceptIdException>(() => ConceptId.Parse(""));
        Assert.Equal("Empty concept id: \"\"", ex.Message);
    }

    [Fact]
    public void Invalid_segment_error_message_uses_debug_quote_format()
    {
        var ex = Assert.Throws<ConceptIdException>(() => ConceptId.Parse("-abc"));
        Assert.Equal("Invalid concept id segment: \"-abc\"", ex.Message);
    }

    [Theory]
    [InlineData("_abc")]  // leading underscore allowed
    [InlineData("a1_2")]
    [InlineData("a.b-c")] // '.' and '-' allowed as non-leading chars
    [InlineData("A9")]
    public void Valid_segments_do_not_throw(string good)
        => ConceptId.ValidateSegment(good);

    [Fact]
    public void ValidateSegment_rejects_empty_segment()
    {
        var ex = Assert.Throws<ConceptIdException>(() => ConceptId.ValidateSegment(""));
        Assert.Equal("Invalid concept id segment: \"\"", ex.Message);
    }

    // --- New(): validates each segment and requires at least one.
    // Unlike Parse(), it does NOT drop empty strings from the caller-supplied list. ---

    [Fact]
    public void New_requires_at_least_one_segment()
    {
        var ex = Assert.Throws<ConceptIdException>(() => ConceptId.New(Array.Empty<string>()));
        Assert.Equal("concept_id must have at least one segment", ex.Message);
    }

    [Fact]
    public void New_does_not_drop_empty_strings_and_validates_each()
    {
        var ex = Assert.Throws<ConceptIdException>(() => ConceptId.New(new[] { "a", "" }));
        Assert.Equal("Invalid concept id segment: \"\"", ex.Message);
    }

    [Fact]
    public void New_builds_valid_id_from_segments()
        => Assert.Equal("a/b", ConceptId.New(new[] { "a", "b" }).ToString());

    // --- TryParse ---

    [Fact]
    public void TryParse_true_for_valid_input()
    {
        Assert.True(ConceptId.TryParse("a/b", out var id));
        Assert.Equal("a/b", id!.ToString());
    }

    [Fact]
    public void TryParse_false_for_invalid_input()
    {
        Assert.False(ConceptId.TryParse("", out var id));
        Assert.Null(id);
    }

    // --- FromPath / ToPath ---

    [Fact]
    public void FromPath_strips_md_and_normalizes_separators()
    {
        var id = ConceptId.FromPath(@"C:\bundle", @"C:\bundle\tables\users.md");
        Assert.Equal("tables/users", id.ToString());
    }

    [Fact]
    public void FromPath_works_with_forward_slashes_too()
    {
        var id = ConceptId.FromPath("C:/bundle", "C:/bundle/tables/users.md");
        Assert.Equal("tables/users", id.ToString());
    }

    [Fact]
    public void FromPath_leaves_non_md_suffix_untouched()
    {
        // FromPath only strips a ".md" suffix if present -- it does not
        // require or enforce it.
        var id = ConceptId.FromPath(@"C:\bundle", @"C:\bundle\log");
        Assert.Equal("log", id.ToString());
    }

    [Fact]
    public void FromPath_single_level_strips_md()
    {
        var id = ConceptId.FromPath(@"C:\bundle", @"C:\bundle\index.md");
        Assert.Equal("index", id.ToString());
    }

    [Fact]
    public void FromPath_throws_when_path_is_outside_bundle_root()
        => Assert.Throws<ConceptIdException>(
            () => ConceptId.FromPath(@"C:\bundle", @"C:\other\tables\users.md"));

    [Fact]
    public void FromPath_throws_when_path_equals_root_exactly()
        // relative part is empty -> zero segments -> New()'s "at least one segment" rule
        => Assert.Throws<ConceptIdException>(
            () => ConceptId.FromPath(@"C:\bundle", @"C:\bundle"));

    [Fact]
    public void ToPath_appends_md()
        => Assert.Equal(Path.Combine("root", "tables", "users.md"),
                        ConceptId.Parse("tables/users").ToPath("root"));

    [Fact]
    public void ToPath_single_segment_has_no_directories()
        => Assert.Equal(Path.Combine("root", "index.md"),
                        ConceptId.Parse("index").ToPath("root"));

    [Fact]
    public void FromPath_and_ToPath_roundtrip()
    {
        var id = ConceptId.FromPath(@"C:\bundle", @"C:\bundle\tables\users.md");
        Assert.Equal(Path.Combine(@"C:\bundle", "tables", "users.md"), id.ToPath(@"C:\bundle"));
    }

    [Fact]
    public void FromPath_normalizes_away_non_leading_dot_segments()
    {
        // FromPath iterates path components, which normalize away a
        // non-leading "." path segment (no CurDir component is yielded for
        // it), so "root/a/./b.md" resolves to "a/b", not an error.
        var id = ConceptId.FromPath(@"C:\bundle", @"C:\bundle\a\.\b.md");
        Assert.Equal("a/b", id.ToString());
    }

    [Fact]
    public void FromPath_still_rejects_dotdot_segments()
    {
        // Unlike ".", ".." is NOT normalized away by path-component
        // iteration -- it survives as a literal segment and fails
        // ValidateSegment (its first char '.' is not a valid leading char).
        Assert.Throws<ConceptIdException>(
            () => ConceptId.FromPath(@"C:\bundle", @"C:\bundle\a\..\b.md"));
    }

    // --- Equality / hashing: ConceptId is used as a Dictionary key (Task 8). ---

    [Fact]
    public void Equal_ids_compare_equal_and_hash_the_same()
    {
        var a = ConceptId.Parse("tables/users");
        var b = ConceptId.Parse("tables/users");
        Assert.True(a.Equals(b));
        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void Different_ids_compare_unequal()
    {
        var a = ConceptId.Parse("tables/users");
        var b = ConceptId.Parse("tables/orders");
        Assert.False(a.Equals(b));
    }

    [Fact]
    public void Segment_comparison_is_ordinal_case_sensitive()
    {
        var a = ConceptId.Parse("Tables/Users");
        var b = ConceptId.Parse("tables/users");
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void ConceptId_usable_as_dictionary_key()
    {
        var a = ConceptId.Parse("tables/users");
        var b = ConceptId.Parse("tables/users");
        var dict = new Dictionary<ConceptId, int> { [a] = 42 };
        Assert.True(dict.ContainsKey(b));
        Assert.Equal(42, dict[b]);
    }

    [Fact]
    public void Equals_false_for_null_and_different_type()
    {
        var a = ConceptId.Parse("tables/users");
        Assert.False(a.Equals(null));
        Assert.False(a.Equals((object)"tables/users"));
    }

    // --- Ordering (F13): ids are ordered over their segment list --
    // element-wise, ordinal string comparison, and shorter-is-less on a
    // strict-prefix tie. ---

    [Fact]
    public void CompareTo_orders_segment_wise_not_by_joined_string()
    {
        // Compare element-wise over segments. "a/b" vs "ab":
        // first elements "a" vs "ab" -> "a" < "ab" (ordinal), so "a/b" < "ab"
        // even though the joined strings would sort the other way
        // ('/' (0x2F) < 'b' (0x62), so a joined-string compare would also
        // say "a/b" < "ab" here -- but this test locks in the *segment-wise*
        // mechanism the brief calls out, not just the observed outcome).
        var aSlashB = ConceptId.Parse("a/b");
        var ab = ConceptId.New(["ab"]);
        Assert.True(aSlashB.CompareTo(ab) < 0);
        Assert.True(ab.CompareTo(aSlashB) > 0);
    }

    [Fact]
    public void CompareTo_shorter_prefix_sorts_first()
    {
        var a = ConceptId.New(["a"]);
        var aB = ConceptId.New(["a", "b"]);
        Assert.True(a.CompareTo(aB) < 0);
        Assert.True(aB.CompareTo(a) > 0);
    }

    [Fact]
    public void CompareTo_is_ordinal_case_sensitive()
    {
        var upper = ConceptId.New(["B"]);
        var lower = ConceptId.New(["a"]);
        // Ordinal: 'B' (0x42) < 'a' (0x61).
        Assert.True(upper.CompareTo(lower) < 0);
    }

    [Fact]
    public void Sort_orders_by_segment_wise_comparison()
    {
        var ids = new List<ConceptId>
        {
            ConceptId.New(["ab"]),
            ConceptId.Parse("b"),
            ConceptId.Parse("a/b"),
            ConceptId.New(["a"]),
        };
        ids.Sort();
        Assert.Equal(
            new[] { "a", "a/b", "ab", "b" },
            ids.Select(id => id.ToString()));
    }

    [Fact]
    public void CompareTo_returns_zero_for_equal_ids()
    {
        var a = ConceptId.Parse("tables/users");
        var b = ConceptId.Parse("tables/users");
        Assert.Equal(0, a.CompareTo(b));
    }

    [Fact]
    public void CompareTo_null_returns_positive_per_icomparable_convention()
    {
        var a = ConceptId.Parse("a");
        Assert.True(a.CompareTo(null) > 0);
        Assert.True(((IComparable)a).CompareTo(null) > 0);
    }

    [Fact]
    public void IComparable_CompareTo_rejects_wrong_type()
    {
        var a = ConceptId.Parse("a");
        Assert.Throws<ArgumentException>(() => ((IComparable)a).CompareTo("a"));
    }

    [Fact]
    public void Comparison_operators_match_CompareTo()
    {
        var a = ConceptId.New(["a"]);
        var aB = ConceptId.New(["a", "b"]);
        Assert.True(a < aB);
        Assert.True(a <= aB);
        Assert.True(aB > a);
        Assert.True(aB >= a);
        Assert.True(a <= ConceptId.New(["a"]));
        Assert.True(a >= ConceptId.New(["a"]));
    }

    [Fact]
    public void Segments_is_not_downcast_mutable_via_New()
    {
        // ConceptId is used as a dictionary key (Bundle's `_index`); a
        // downcast-mutable Segments would let a caller corrupt the key after
        // insertion. New() takes an IReadOnlyList<string> -- passing a List
        // must not let the caller reach back into the stored Segments.
        var backing = new List<string> { "a", "b" };
        var id = ConceptId.New(backing);

        Assert.IsNotType<List<string>>(id.Segments, exactMatch: false);

        backing.Add("c"); // mutating the caller's original list...
        Assert.Equal(["a", "b"], id.Segments); // ...must not affect the ConceptId
    }

    [Fact]
    public void Segments_is_not_downcast_mutable_via_Parse()
    {
        var id = ConceptId.Parse("a/b");
        Assert.IsNotType<List<string>>(id.Segments, exactMatch: false);
    }
}
