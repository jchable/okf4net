// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace OKF4net.Providers.S3.Internal;

/// <summary>
/// The bundle token (spec §4.5): <c>"s3v1:"</c> + lowercase hex SHA-256 of a stream holding, for each listed entry in
/// the listing's order, three fields each preceded by its length in bytes (unsigned, 4 bytes, big-endian): the raw
/// name in UTF-8 (without the bundle prefix), the ETag as returned in UTF-8 (quotes included), the size in ASCII
/// decimal without sign or leading zero. Fed incrementally, so a listing is hashed as it streams.
/// </summary>
internal sealed class BundleTokenHasher : IDisposable
{
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

    /// <summary>Appends one entry's three framed fields.</summary>
    public void Add(string rawName, string etag, long size)
    {
        Field(Encoding.UTF8.GetBytes(rawName));
        Field(Encoding.UTF8.GetBytes(etag));
        Field(Encoding.ASCII.GetBytes(size.ToString(CultureInfo.InvariantCulture)));
    }

    /// <summary>The token of the entries added so far.</summary>
    public string Finish() => "s3v1:" + Convert.ToHexStringLower(_hash.GetHashAndReset());

    /// <inheritdoc/>
    public void Dispose() => _hash.Dispose();

    /// <summary>The token of <paramref name="entries"/>, in their order.</summary>
    public static string Hash(IEnumerable<(string RawName, string ETag, long Size)> entries)
    {
        using var h = new BundleTokenHasher();
        foreach (var (n, e, s) in entries)
        {
            h.Add(n, e, s);
        }

        return h.Finish();
    }

    private void Field(byte[] bytes)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, checked((uint)bytes.Length));
        _hash.AppendData(length);
        _hash.AppendData(bytes);
    }
}
