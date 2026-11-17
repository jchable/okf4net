// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Globalization;
using System.Text;
using System.Xml;

namespace OKF4net.Providers.S3.Internal;

/// <summary>One object of a listing page: its S3 key decoded strictly, its ETag as the store gave it (quotes included), its size.</summary>
internal sealed record S3Entry(string S3Key, string ETag, long Size);

/// <summary>A listing page as the provider's own parse read it (spec D9).</summary>
internal sealed record ListPage(IReadOnlyList<S3Entry> Entries, bool IsTruncated, string? NextToken, int MaxKeys);

/// <summary>
/// Parses a raw ListObjectsV2 page the bounding handler captured (spec D9, §4.3): safe by construction — no DTD
/// (a DOCTYPE fails the parse), no resolver, at most <see cref="BoundingHandler.ListBodyBytes"/> characters, no
/// recursion — and strict: the root is <c>ListBucketResult</c> in the S3 namespace or none; <c>EncodingType</c> is
/// exactly <c>url</c>; <c>IsTruncated</c> exactly <c>true</c> or <c>false</c>; each <c>Contents</c> has exactly one
/// non-empty <c>Key</c>, decoded strictly as a listed value (a raw <c>+</c> is a space; the only url-encoded field read: the token is opaque, never encoded), one non-empty <c>ETag</c> and one decimal, unsigned <c>Size</c>; every
/// field appears at most once in its parent, and a field's name in another namespace is refused rather than read or
/// skipped; a truncated page carries a non-empty token of at most <see cref="BoundingHandler.TokenBytes"/> UTF-8
/// bytes; a page holding more than the requested number of entries is refused at the first one too many, before the
/// rest is collected. Anything else is <see cref="S3FailureKind.InvalidResponse"/>. Other elements are skipped with
/// their subtrees. The SDK's own model of the page is never trusted for keys.
/// </summary>
internal static class ListPageParser
{
    /// <summary>The namespace of S3's XML documents; a compatible store may also omit it.</summary>
    public const string S3Namespace = "http://s3.amazonaws.com/doc/2006-03-01/";

    private static readonly XmlReaderSettings Settings = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        IgnoreComments = true,
        IgnoreWhitespace = true,
        IgnoreProcessingInstructions = true,
        MaxCharactersInDocument = BoundingHandler.ListBodyBytes,
        CloseInput = true,
    };

    /// <summary>Parses <paramref name="body"/> without an entry bound.</summary>
    /// <exception cref="GuardException">The page is not trustworthy (<see cref="S3FailureKind.InvalidResponse"/>).</exception>
    public static ListPage Parse(byte[] body) => Parse(body, int.MaxValue);

    /// <summary>Parses <paramref name="body"/>, refusing a page with more than <paramref name="maxEntries"/> entries.</summary>
    /// <exception cref="GuardException">The page is not trustworthy (<see cref="S3FailureKind.InvalidResponse"/>).</exception>
    public static ListPage Parse(byte[] body, int maxEntries)
    {
        try
        {
            return ParseCore(body, maxEntries);
        }
        catch (Exception e) when (e is XmlException or FormatException or OverflowException or InvalidOperationException or ArgumentException or NotSupportedException)
        {
            // ArgumentException covers DecoderFallbackException; nothing from the document reaches the message.
            throw new GuardException(S3FailureKind.InvalidResponse);
        }
    }

    private static GuardException Invalid() => new(S3FailureKind.InvalidResponse);

    /// <summary>
    /// A structured walk with EXPLICIT reader advancement: <see cref="XmlReader.ReadElementContentAsString()"/> and
    /// <see cref="XmlReader.Skip"/> already move past the element, so <see cref="XmlReader.Read"/> is called only for
    /// a node nothing consumed. Page fields are children of the root (depth 1), entry fields children of a
    /// <c>Contents</c> (depth 2).
    /// </summary>
    private static ListPage ParseCore(byte[] body, int maxEntries)
    {
        using var reader = XmlReader.Create(new MemoryStream(body, writable: false), Settings);
        if (reader.MoveToContent() != XmlNodeType.Element || reader.LocalName != "ListBucketResult"
            || reader.NamespaceURI is not (S3Namespace or "") || reader.IsEmptyElement)
        {
            throw Invalid();
        }

        var ns = reader.NamespaceURI;
        var entries = new List<S3Entry>();
        var page = new Dictionary<string, string>(StringComparer.Ordinal);
        Advance(reader); // into the root
        while (!(reader.NodeType == XmlNodeType.EndElement && reader.Depth == 0))
        {
            if (reader.NodeType != XmlNodeType.Element)
            {
                Advance(reader);
                continue;
            }

            if (reader.LocalName is "Contents" or "EncodingType" or "IsTruncated" or "NextContinuationToken" or "MaxKeys"
                && reader.NamespaceURI != ns)
            {
                throw Invalid(); // a field's name in another namespace: neither read nor silently skipped
            }

            if (reader.LocalName == "Contents")
            {
                if (entries.Count >= maxEntries)
                {
                    throw Invalid(); // a page larger than the request's MaxKeys, refused before the rest is collected
                }

                entries.Add(Entry(reader, ns));
                continue;
            }

            if (reader.LocalName is "EncodingType" or "IsTruncated" or "NextContinuationToken" or "MaxKeys")
            {
                var name = reader.LocalName;
                if (!page.TryAdd(name, reader.ReadElementContentAsString()))
                {
                    throw Invalid();
                }

                continue;
            }

            reader.Skip(); // any other element, with its subtree (Name, Prefix, KeyCount, CommonPrefixes, …)
        }

        while (reader.Read())
        {
            // To the end of the document: trailing markup or text fails the reader itself.
        }

        if (page.GetValueOrDefault("EncodingType") != "url" || !page.TryGetValue("IsTruncated", out var truncatedText)
            || truncatedText is not ("true" or "false"))
        {
            throw Invalid();
        }

        var truncated = truncatedText == "true";
        var token = page.GetValueOrDefault("NextContinuationToken");
        if (truncated && (string.IsNullOrEmpty(token) || Encoding.UTF8.GetByteCount(token) > BoundingHandler.TokenBytes))
        {
            throw Invalid();
        }

        var maxKeys = page.TryGetValue("MaxKeys", out var mk) ? int.Parse(mk, NumberStyles.None, CultureInfo.InvariantCulture) : 0;
        return new ListPage(entries, truncated, token, maxKeys);
    }

    /// <summary>Reads one <c>Contents</c> (the reader on its start tag) and leaves the reader past its end tag.</summary>
    private static S3Entry Entry(XmlReader reader, string ns)
    {
        if (reader.IsEmptyElement)
        {
            throw Invalid();
        }

        string? encodedKey = null, etag = null, sizeText = null;
        Advance(reader); // into Contents
        while (!(reader.NodeType == XmlNodeType.EndElement && reader.Depth == 1))
        {
            if (reader.NodeType != XmlNodeType.Element)
            {
                Advance(reader);
                continue;
            }

            if (reader.LocalName is "Key" or "ETag" or "Size")
            {
                if (reader.NamespaceURI != ns)
                {
                    throw Invalid();
                }

                var name = reader.LocalName;
                var value = reader.ReadElementContentAsString();
                ref var slot = ref name == "Key" ? ref encodedKey : ref name == "ETag" ? ref etag : ref sizeText;
                if (slot is not null)
                {
                    throw Invalid(); // a repeated field
                }

                slot = value;
                continue;
            }

            reader.Skip(); // LastModified, Owner, StorageClass, ChecksumAlgorithm, …
        }

        Advance(reader); // past </Contents>
        if (string.IsNullOrEmpty(encodedKey) || string.IsNullOrEmpty(etag)
            || sizeText is null || !long.TryParse(sizeText, NumberStyles.None, CultureInfo.InvariantCulture, out var size)
            || !S3KeyCodec.TryDecodeListed(encodedKey, out var decoded))
        {
            throw Invalid();
        }

        return new S3Entry(decoded, etag, size);
    }

    private static void Advance(XmlReader reader)
    {
        if (!reader.Read())
        {
            throw Invalid(); // the document ended inside the root (the reader normally refuses first)
        }
    }
}
