// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Text;
using OKF4net.Providers.S3;
using OKF4net.Providers.S3.Internal;

namespace OKF4net.Tests.Providers.S3;

/// <summary>Spec D9: the provider's own strict parse of a raw page.</summary>
public class ListPageParserTests
{
    internal const string S3Namespace = "http://s3.amazonaws.com/doc/2006-03-01/";

    internal static string Page(IEnumerable<(string EncodedKey, string ETag, long Size)> entries, bool truncated, string? token, string? encodingType = "url", int maxKeys = 1000)
    {
        var sb = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\"?><ListBucketResult xmlns=\"" + S3Namespace + "\"><Name>okf</Name>");
        sb.Append("<MaxKeys>").Append(maxKeys).Append("</MaxKeys>");
        if (encodingType is not null) sb.Append("<EncodingType>").Append(encodingType).Append("</EncodingType>");
        sb.Append("<IsTruncated>").Append(truncated ? "true" : "false").Append("</IsTruncated>");
        if (token is not null) sb.Append("<NextContinuationToken>").Append(token).Append("</NextContinuationToken>");
        foreach (var (k, e, s) in entries)
            sb.Append("<Contents><Key>").Append(k).Append("</Key><ETag>").Append(System.Security.SecurityElement.Escape(e)).Append("</ETag><Size>").Append(s).Append("</Size></Contents>");
        return sb.Append("</ListBucketResult>").ToString();
    }

    private static ListPage Parse(string xml) => ListPageParser.Parse(Encoding.UTF8.GetBytes(xml));

    private static void AssertRefused(byte[] body, int maxEntries = int.MaxValue) =>
        Assert.Equal(S3FailureKind.InvalidResponse, Assert.Throws<GuardException>(() => ListPageParser.Parse(body, maxEntries)).Kind);

    private static void AssertRefused(string xml, int maxEntries = int.MaxValue) => AssertRefused(Encoding.UTF8.GetBytes(xml), maxEntries);

    private static string One => Page([("kb/a.md", "\"e\"", 1)], false, null);

    [Fact]
    public void A_page_decodes_its_keys_strictly()
    {
        var p = Parse(Page([("kb/caf%C3%A9.md", "\"e1\"", 3)], truncated: false, token: null));
        var e = Assert.Single(p.Entries);
        Assert.Equal("kb/café.md", e.S3Key);
        Assert.Equal("\"e1\"", e.ETag);
        Assert.Equal(3, e.Size);
    }

    [Fact]
    public void Compact_and_indented_pages_parse_alike_and_no_field_is_skipped()
    {
        var compact = Page([("kb/a.md", "\"e1\"", 1), ("kb/b.md", "\"e2\"", 2)], truncated: true, token: "t1");
        var indented = System.Xml.Linq.XDocument.Parse(compact).ToString(); // whitespace between every element
        foreach (var xml in new[] { compact, indented })
        {
            var p = Parse(xml);
            Assert.Equal(["kb/a.md", "kb/b.md"], p.Entries.Select(x => x.S3Key));
            Assert.Equal(["\"e1\"", "\"e2\""], p.Entries.Select(x => x.ETag));
            Assert.Equal([1L, 2L], p.Entries.Select(x => x.Size));
            Assert.True(p.IsTruncated);
            Assert.Equal("t1", p.NextToken);
            Assert.Equal(1000, p.MaxKeys);
        }
    }

    public static TheoryData<string> Refused => new()
    {
        Page([("kb/a.md", "\"e\"", 1)], false, null, encodingType: null),
        Page([("kb/a.md", "\"e\"", 1)], false, null, encodingType: "other"),
        Page([("kb/%FF.md", "\"e\"", 1)], false, null),
        Page([("kb/a.md", "", 1)], false, null),
        Page([("kb/a.md", "\"e\"", -1)], false, null),
        Page([("kb/a.md", "\"e\"", 1)], true, null),
        Page([("kb/a.md", "\"e\"", 1)], true, new string('t', 1025)),
        "<not-xml",
        "<?xml version=\"1.0\"?><!DOCTYPE x [<!ENTITY e \"x\">]><ListBucketResult/>",
        "<?xml version=\"1.0\"?><Other><EncodingType>url</EncodingType><IsTruncated>false</IsTruncated></Other>", // another root
        Page([("kb/a.md", "\"e\"", 1)], false, null).Replace("<ETag>", "<ETag>\"x\"</ETag><ETag>", StringComparison.Ordinal), // a repeated field
        Page([("kb/a.md", "\"e\"", 1)], false, null).Replace("<IsTruncated>false</IsTruncated>", "<IsTruncated>false</IsTruncated><IsTruncated>true</IsTruncated>", StringComparison.Ordinal),
        Page([("kb/a.md", "\"e\"", 1)], false, null).Replace("<Size>1</Size>", "", StringComparison.Ordinal), // a missing field
    };

    [Theory]
    [MemberData(nameof(Refused))]
    public void An_untrustworthy_page_is_InvalidResponse(string xml) =>
        Assert.Equal(S3FailureKind.InvalidResponse, Assert.Throws<GuardException>(() => Parse(xml)).Kind);

    // ---- The hostile battery: safe by construction (no DTD, no resolver, bounded), strict on every field. ----

    internal const string BillionLaughs =
        "<?xml version=\"1.0\"?><!DOCTYPE lolz [<!ENTITY lol \"lol\"><!ENTITY lol1 \"&lol;&lol;&lol;&lol;&lol;&lol;&lol;&lol;&lol;&lol;\">"
        + "<!ENTITY lol2 \"&lol1;&lol1;&lol1;&lol1;&lol1;&lol1;&lol1;&lol1;&lol1;&lol1;\"><!ENTITY lol3 \"&lol2;&lol2;&lol2;&lol2;&lol2;&lol2;&lol2;&lol2;&lol2;&lol2;\">"
        + "<!ENTITY lol4 \"&lol3;&lol3;&lol3;&lol3;&lol3;&lol3;&lol3;&lol3;&lol3;&lol3;\"><!ENTITY lol5 \"&lol4;&lol4;&lol4;&lol4;&lol4;&lol4;&lol4;&lol4;&lol4;&lol4;\">"
        + "<!ENTITY lol6 \"&lol5;&lol5;&lol5;&lol5;&lol5;&lol5;&lol5;&lol5;&lol5;&lol5;\"><!ENTITY lol7 \"&lol6;&lol6;&lol6;&lol6;&lol6;&lol6;&lol6;&lol6;&lol6;&lol6;\">"
        + "<!ENTITY lol8 \"&lol7;&lol7;&lol7;&lol7;&lol7;&lol7;&lol7;&lol7;&lol7;&lol7;\"><!ENTITY lol9 \"&lol8;&lol8;&lol8;&lol8;&lol8;&lol8;&lol8;&lol8;&lol8;&lol8;\">]>"
        + "<ListBucketResult><EncodingType>url</EncodingType><IsTruncated>false</IsTruncated><Contents><Key>&lol9;</Key><ETag>e</ETag><Size>1</Size></Contents></ListBucketResult>";

    public static TheoryData<string, string> Hostile => new()
    {
        { "billion laughs", BillionLaughs },
        { "external entity", "<?xml version=\"1.0\"?><!DOCTYPE x [<!ENTITY xxe SYSTEM \"file:///etc/passwd\">]><ListBucketResult><EncodingType>url</EncodingType><IsTruncated>false</IsTruncated><Contents><Key>&xxe;</Key><ETag>e</ETag><Size>1</Size></Contents></ListBucketResult>" },
        { "external DTD", "<?xml version=\"1.0\"?><!DOCTYPE ListBucketResult SYSTEM \"http://127.0.0.1:1/x.dtd\"><ListBucketResult><EncodingType>url</EncodingType><IsTruncated>false</IsTruncated></ListBucketResult>" },
        { "undeclared entity", One.Replace("<Key>kb/a.md</Key>", "<Key>kb/&undeclared;.md</Key>", StringComparison.Ordinal) },
        { "foreign root namespace", One.Replace(S3Namespace, "urn:evil", StringComparison.Ordinal) },
        { "a field in a foreign namespace", One.Replace("<Key>kb/a.md</Key>", "<x:Key xmlns:x=\"urn:evil\">kb/a.md</x:Key>", StringComparison.Ordinal) },
        { "a page field in a foreign namespace", One.Replace("<IsTruncated>false</IsTruncated>", "<x:IsTruncated xmlns:x=\"urn:evil\">false</x:IsTruncated>", StringComparison.Ordinal) },
        { "a duplicate page field in a foreign namespace", One.Replace("<IsTruncated>false</IsTruncated>", "<IsTruncated>false</IsTruncated><x:IsTruncated xmlns:x=\"urn:evil\">true</x:IsTruncated>", StringComparison.Ordinal) },
        { "a truncated document", One[..(One.Length / 2)] },
        { "a second root", One + "<ListBucketResult/>" },
        { "trailing text", One + "junk" },
        { "a control character by reference", One.Replace("<ETag>&quot;e&quot;</ETag>", "<ETag>&#1;</ETag>", StringComparison.Ordinal) },
        { "a raw control character", One.Replace("<ETag>&quot;e&quot;</ETag>", "<ETag>\u0001</ETag>", StringComparison.Ordinal) },
        { "a surrogate by reference", One.Replace("<ETag>&quot;e&quot;</ETag>", "<ETag>&#xD800;</ETag>", StringComparison.Ordinal) },
        { "an empty key", One.Replace("<Key>kb/a.md</Key>", "<Key></Key>", StringComparison.Ordinal) },
        { "an empty Contents", One.Replace("</ListBucketResult>", "<Contents/></ListBucketResult>", StringComparison.Ordinal) },
        { "a Contents without a key", One.Replace("<Key>kb/a.md</Key>", "", StringComparison.Ordinal) },
        { "a Contents without an ETag", One.Replace("<ETag>&quot;e&quot;</ETag>", "", StringComparison.Ordinal) },
        { "a repeated key", One.Replace("<Key>kb/a.md</Key>", "<Key>kb/a.md</Key><Key>kb/b.md</Key>", StringComparison.Ordinal) },
        { "a repeated size", One.Replace("<Size>1</Size>", "<Size>1</Size><Size>1</Size>", StringComparison.Ordinal) },
        { "a key with markup", One.Replace("<Key>kb/a.md</Key>", "<Key>kb/<b>a</b>.md</Key>", StringComparison.Ordinal) },
        { "a signed size", One.Replace("<Size>1</Size>", "<Size>+1</Size>", StringComparison.Ordinal) },
        { "a size with spaces", One.Replace("<Size>1</Size>", "<Size> 1 </Size>", StringComparison.Ordinal) },
        { "an overflowing size", One.Replace("<Size>1</Size>", "<Size>99999999999999999999</Size>", StringComparison.Ordinal) },
        { "a hexadecimal size", One.Replace("<Size>1</Size>", "<Size>0x1</Size>", StringComparison.Ordinal) },
        { "a missing IsTruncated", One.Replace("<IsTruncated>false</IsTruncated>", "", StringComparison.Ordinal) },
        { "IsTruncated True", One.Replace("<IsTruncated>false</IsTruncated>", "<IsTruncated>True</IsTruncated>", StringComparison.Ordinal) },
        { "IsTruncated 0", One.Replace("<IsTruncated>false</IsTruncated>", "<IsTruncated>0</IsTruncated>", StringComparison.Ordinal) },
        { "IsTruncated padded", One.Replace("<IsTruncated>false</IsTruncated>", "<IsTruncated> false</IsTruncated>", StringComparison.Ordinal) },
        { "EncodingType URL", One.Replace("<EncodingType>url</EncodingType>", "<EncodingType>URL</EncodingType>", StringComparison.Ordinal) },
        { "a repeated EncodingType", One.Replace("<EncodingType>url</EncodingType>", "<EncodingType>url</EncodingType><EncodingType>url</EncodingType>", StringComparison.Ordinal) },
        { "a repeated token", Page([("kb/a.md", "\"e\"", 1)], true, "t").Replace("<NextContinuationToken>t</NextContinuationToken>", "<NextContinuationToken>t</NextContinuationToken><NextContinuationToken>u</NextContinuationToken>", StringComparison.Ordinal) },
        { "an empty token on a truncated page", Page([("kb/a.md", "\"e\"", 1)], true, "") },
        { "a token of 513 two-byte characters", Page([("kb/a.md", "\"e\"", 1)], true, new string('é', 513)) },
        { "a token with markup", Page([("kb/a.md", "\"e\"", 1)], true, "<b>t</b>") },
        { "a raw key outside percent-encoding", Page([("kb/a%.md", "\"e\"", 1)], false, null) },
        { "an overlong encoding", Page([("kb/%C0%AF.md", "\"e\"", 1)], false, null) },
        { "an encoded surrogate", Page([("kb/%ED%A0%80.md", "\"e\"", 1)], false, null) },
        { "an unparseable MaxKeys", One.Replace("<MaxKeys>1000</MaxKeys>", "<MaxKeys>-1</MaxKeys>", StringComparison.Ordinal) },
    };

    [Theory]
    [MemberData(nameof(Hostile))]
    public void A_hostile_page_is_InvalidResponse(string name, string xml)
    {
        _ = name;
        AssertRefused(xml);
    }

    [Fact]
    public void Invalid_utf8_bytes_are_InvalidResponse()
    {
        var bytes = Encoding.UTF8.GetBytes(One.Replace("<ETag>&quot;e&quot;</ETag>", "<ETag>Q</ETag>", StringComparison.Ordinal));
        bytes[Array.IndexOf(bytes, (byte)'Q')] = 0xFF;
        AssertRefused(bytes);
    }

    [Fact]
    public void A_declared_encoding_the_bytes_do_not_have_is_InvalidResponse() =>
        AssertRefused(One.Replace("encoding=\"UTF-8\"", "encoding=\"UTF-16\"", StringComparison.Ordinal));

    [Fact]
    public void An_empty_body_is_InvalidResponse() => AssertRefused([]);

    [Fact]
    public void The_entry_bound_is_applied_while_parsing()
    {
        // A page holding more entries than the request asked for is refused at entry max + 1, not after collecting them all.
        var entries = Enumerable.Range(0, 1001).Select(i => ($"kb/n{i:D4}.md", "\"e\"", 1L)).ToArray();
        AssertRefused(Page(entries, false, null), maxEntries: 1000);
        Assert.Equal(1000, ListPageParser.Parse(Encoding.UTF8.GetBytes(Page(entries[..1000], false, null)), 1000).Entries.Count);
        AssertRefused(Page([("kb/a.md", "\"e\"", 1), ("kb/b.md", "\"e\"", 1)], false, null), maxEntries: 1);
    }

    [Fact]
    public void A_token_is_bounded_in_utf8_bytes_not_characters()
    {
        Assert.Equal(new string('t', 1024), Parse(Page([("kb/a.md", "\"e\"", 1)], true, new string('t', 1024))).NextToken);
        Assert.Equal(new string('é', 512), Parse(Page([("kb/a.md", "\"e\"", 1)], true, new string('é', 512))).NextToken);
    }

    [Fact]
    public void The_root_may_omit_the_namespace_as_some_compatible_stores_do()
    {
        var p = Parse(One.Replace(" xmlns=\"" + S3Namespace + "\"", "", StringComparison.Ordinal));
        Assert.Equal("kb/a.md", Assert.Single(p.Entries).S3Key);
    }

    [Fact]
    public void Unknown_elements_with_their_subtrees_are_skipped_and_never_read_as_fields()
    {
        var xml = One.Replace(
            "<Contents>",
            "<CommonPrefixes><Prefix>kb/x/</Prefix></CommonPrefixes><Owner><Key>kb/hidden.md</Key><Contents><Key>kb/deep.md</Key><ETag>e</ETag><Size>1</Size></Contents></Owner><Contents><Owner><ID>1</ID><Key>kb/nested.md</Key></Owner><StorageClass>STANDARD</StorageClass>",
            StringComparison.Ordinal);
        var e = Assert.Single(Parse(xml).Entries);
        Assert.Equal("kb/a.md", e.S3Key);
    }

    [Fact]
    public void A_deeply_nested_unknown_element_is_skipped_without_recursion()
    {
        const int depth = 100_000;
        var deep = string.Concat(Enumerable.Repeat("<x>", depth)) + string.Concat(Enumerable.Repeat("</x>", depth));
        var p = Parse(One.Replace("<Name>okf</Name>", deep, StringComparison.Ordinal));
        Assert.Single(p.Entries);
    }

    [Fact]
    public void Many_unknown_elements_cost_no_entries()
    {
        var noise = string.Concat(Enumerable.Repeat("<Owner/>", 200_000));
        var p = ListPageParser.Parse(Encoding.UTF8.GetBytes(One.Replace("<Name>okf</Name>", noise, StringComparison.Ordinal)), 1);
        Assert.Single(p.Entries);
    }

    [Fact]
    public void A_listed_key_is_form_decoded_a_plus_is_a_space_and_2B_a_plus()
    {
        var p = Parse(Page([("kb/a+b.md", "\"e\"", 1), ("kb/a%2Bb.md", "\"e\"", 1), ("kb/c%20d.md", "\"e\"", 1)], false, null));
        Assert.Equal(["kb/a b.md", "kb/a+b.md", "kb/c d.md"], p.Entries.Select(e => e.S3Key));
    }

    [Fact]
    public void A_page_without_entries_that_is_complete_is_valid()
    {
        var p = Parse(Page([], false, null));
        Assert.Empty(p.Entries);
        Assert.False(p.IsTruncated);
        Assert.Null(p.NextToken);
    }

    [Fact]
    public void Escaped_text_is_read_once_as_xml_and_the_key_once_as_percent_encoding()
    {
        // &amp; is the XML layer; %25 is the URL layer: a literal "%26" key name, never "&".
        var p = Parse(Page([("kb/%2526.md", "\"a&b\"", 0)], false, null));
        var e = Assert.Single(p.Entries);
        Assert.Equal("kb/%26.md", e.S3Key);
        Assert.Equal("\"a&b\"", e.ETag);
        Assert.Equal(0, e.Size);
    }
}
