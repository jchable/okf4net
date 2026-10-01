// SPDX-License-Identifier: LGPL-3.0-or-later
using Xunit.Sdk;

namespace OKF4net.Tests;

/// <summary>
/// The comparator behind every full-projection test. Its two jobs are to
/// ignore what the design calls presentation (object property order) and to
/// refuse what a field-by-field assertion cannot see (a property missing,
/// added or duplicated, an array truncated, an integer rounded).
/// </summary>
public class JsonShapeTests
{
    [Fact]
    public void AssertEquivalent_ignores_object_property_order()
    {
        JsonShape.AssertEquivalent("""{"a":1,"b":[true,null]}""", """{"b":[true,null],"a":1}""");
    }

    [Fact]
    public void AssertEquivalent_reports_missing_and_unexpected_properties()
    {
        var ex = Assert.Throws<TrueException>(() =>
            JsonShape.AssertEquivalent("""{"a":1,"b":2}""", """{"a":1,"c":2}"""));
        Assert.Contains("$.b: missing", ex.Message, StringComparison.Ordinal);
        Assert.Contains("$.c: unexpected property", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AssertEquivalent_rejects_duplicate_property_names()
    {
        // JsonDocument accepts {"a":0,"a":1}; a name-set comparison would see
        // one "a" and TryGetProperty would return one value, hiding the
        // duplicate. The comparator counts names.
        var ex = Assert.Throws<TrueException>(() =>
            JsonShape.AssertEquivalent("""{"a":1}""", """{"a":0,"a":1}"""));
        Assert.Contains("$.a: duplicated", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AssertEquivalent_treats_array_order_and_length_as_significant()
    {
        var reordered = Assert.Throws<TrueException>(() =>
            JsonShape.AssertEquivalent("""[1,2]""", """[2,1]"""));
        Assert.Contains("$[0]", reordered.Message, StringComparison.Ordinal);

        var truncated = Assert.Throws<TrueException>(() =>
            JsonShape.AssertEquivalent("""[1,2]""", """[1]"""));
        Assert.Contains("expected 2 elements, got 1", truncated.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AssertEquivalent_compares_numbers_as_text_not_as_doubles()
    {
        // 2^53 and 2^53+1 are the same double; they are not the same JSON number.
        var ex = Assert.Throws<TrueException>(() =>
            JsonShape.AssertEquivalent("""{"n":9007199254740992}""", """{"n":9007199254740993}"""));
        Assert.Contains("$.n: expected 9007199254740992, got 9007199254740993", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AssertEquivalent_compares_decoded_strings_not_escaped_text()
    {
        // System.Text.Json's default encoder escapes '`' as a backslash-u
        // sequence; the expected side is written with the plain character.
        // The escaped text is produced at run time by the serializer itself,
        // never spelled with a backslash in this source: an editor, a tool or
        // a review that materialises this file can decode a backslash-u
        // sequence on the way, and the two arguments then become identical
        // text -- which is exactly how the first version of this test was
        // inert. The guard below proves the escape is really there.
        var escaped = "{\"m\":" + System.Text.Json.JsonSerializer.Serialize("`x`") + "}";
        Assert.Contains("u0060", escaped, StringComparison.Ordinal);
        Assert.DoesNotContain("`", escaped, StringComparison.Ordinal);

        JsonShape.AssertEquivalent("""{"m":"`x`"}""", escaped);

        var different = "{\"m\":" + System.Text.Json.JsonSerializer.Serialize("axa") + "}";
        Assert.Throws<TrueException>(() =>
            JsonShape.AssertEquivalent("""{"m":"`x`"}""", different));
    }

    [Fact]
    public void AssertEquivalent_distinguishes_null_from_absent_and_kinds()
    {
        var nullVsAbsent = Assert.Throws<TrueException>(() =>
            JsonShape.AssertEquivalent("""{"a":null}""", """{}"""));
        Assert.Contains("$.a: missing", nullVsAbsent.Message, StringComparison.Ordinal);

        var kind = Assert.Throws<TrueException>(() =>
            JsonShape.AssertEquivalent("""{"a":1}""", """{"a":"1"}"""));
        Assert.Contains("$.a: expected Number, got String", kind.Message, StringComparison.Ordinal);
    }
}
