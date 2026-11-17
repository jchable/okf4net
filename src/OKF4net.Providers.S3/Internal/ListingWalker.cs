// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Amazon.S3;
using Amazon.S3.Model;

namespace OKF4net.Providers.S3.Internal;

/// <summary>
/// The private entry iterator (spec §4.3) behind EnumerateAsync, GetVersionAsync and ExistsAsync: one ListObjectsV2
/// page per operation scope (its own permit, deadline, credentials snapshot and <see cref="OperationContext"/>, so the
/// guard's per-call send cap never spans pages), parsed from the raw bytes the bounding handler captured, never from
/// the SDK's model. Pages are fetched lazily and no permit is held while an entry is yielded, so the core's entry
/// bound stops the walk by disposing the enumerator, and a consumer may read objects between entries.
/// <para>Every transport inconsistency of spec D9 is <see cref="S3FailureKind.InvalidResponse"/>, detected on the
/// page that shows it: a key outside the listed prefix; keys not strictly increasing in UTF-8 (code point) order,
/// across pages too (a duplicate, a step back, or a store sorting by UTF-16 — the order is a declared precondition
/// of the s3v1 token); a repeated continuation token; a truncated page without an entry (no progress); and more than
/// <c>maxPages</c> pages. The parser refuses the rest (an untrustworthy document, a page larger than its MaxKeys, an
/// empty or oversized token). <c>parse</c> is a test seam (default <see cref="ListPageParser.Parse(byte[], int)"/>).</para>
/// </summary>
internal sealed class ListingWalker(AmazonS3Client client, string bucket, OperationGate gate, CredentialResolver credentials, Func<byte[], int, ListPage>? parse = null)
{
    private readonly Func<byte[], int, ListPage> _parse = parse ?? ListPageParser.Parse;

    public async IAsyncEnumerable<S3Entry> WalkAsync(string listPrefix, int maxKeys, long maxPages, TimeSpan timeout, S3Operation operation, BundleKey? key, [EnumeratorCancellation] CancellationToken ct)
    {
        string? token = null;
        string? previous = null;
        var seenTokens = new HashSet<TokenDigest>();
        for (long page = 0; ; page++)
        {
            if (page >= maxPages)
            {
                throw Inconsistent(operation, key); // each truncated page brings an entry: past the entry bound's pages, no honest store
            }

            var parsed = await FetchAsync(listPrefix, maxKeys, token, timeout, operation, key, ct).ConfigureAwait(false);
            if (parsed.IsTruncated && parsed.Entries.Count == 0)
            {
                throw Inconsistent(operation, key); // no progress
            }

            foreach (var entry in parsed.Entries)
            {
                if (!entry.S3Key.StartsWith(listPrefix, StringComparison.Ordinal)
                    || (previous is not null && S3KeyCodec.CompareByCodePoint(previous, entry.S3Key) >= 0))
                {
                    throw Inconsistent(operation, key);
                }

                previous = entry.S3Key;
                ct.ThrowIfCancellationRequested();
                yield return entry;
            }

            if (!parsed.IsTruncated)
            {
                yield break;
            }

            if (!seenTokens.Add(TokenDigest.Of(parsed.NextToken!)))
            {
                throw Inconsistent(operation, key);
            }

            token = parsed.NextToken;
        }
    }

    private static S3RequestException Inconsistent(S3Operation operation, BundleKey? key) => new(operation, S3FailureKind.InvalidResponse, null, key);

    /// <summary>One page, as one operation (spec §4.5): permit and deadline, a credentials snapshot, one SDK call, the strict parse.</summary>
    private async Task<ListPage> FetchAsync(string listPrefix, int maxKeys, string? token, TimeSpan timeout, S3Operation operation, BundleKey? key, CancellationToken ct)
    {
        var scope = await gate.EnterAsync(operation, timeout, ct, key).ConfigureAwait(false);
        await using (scope.ConfigureAwait(false))
        {
            Exception failure;
            var capture = new ListPageCapture();
            try
            {
                // Resolved BEFORE the operation context exists: the host's call runs isolated on the pool, and Task.Run
                // flows the ExecutionContext — a context set first would reach the host's code.
                var snapshot = await credentials.ResolveAsync(scope).ConfigureAwait(false);
                OperationContext.Current.Value = new OperationContext(operation) { ExpectedListPrefix = listPrefix, Capture = capture, Credentials = snapshot };
                await client.ListObjectsV2Async(
                    new ListObjectsV2Request
                    {
                        BucketName = bucket,
                        Prefix = listPrefix,
                        MaxKeys = maxKeys,
                        ContinuationToken = token,
                        Encoding = EncodingType.Url, // the property is Encoding (gate G12); the SDK does not decode the keys
                    },
                    scope.Token).ConfigureAwait(false);
                return _parse(capture.Body ?? throw new GuardException(S3FailureKind.InvalidResponse), maxKeys);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                // Fail first, before any await: the caller's cancellation is sampled now. The one synchronous step
                // before it: the provider's own parse is the authority on a page (spec D9), so when the SDK's
                // deserializer failed on a page the bounding handler captured (measured: the SDK throws XmlException
                // on "<not-xml") and that parse refuses the page too, the page is the failure — InvalidResponse,
                // whatever the SDK made of it. A caller that has already cancelled skips the parse, and the parse never
                // throws (PageRefused), so Fail is the first thing that can end this block.
                failure = scope.Fail(scope.CallerCancelled || e is GuardException or OperationCanceledException || !PageRefused(capture, maxKeys)
                    ? e
                    : new GuardException(S3FailureKind.InvalidResponse));
            }
            finally
            {
                OperationContext.Current.Value = null;
            }

            throw failure;
        }
    }

    /// <summary>
    /// Whether a successful page was captured and the provider's own parse refuses it. Never throws (but an
    /// <see cref="OutOfMemoryException"/>): any failure of the parse is a refusal, so nothing escapes before <c>scope.Fail</c>.
    /// </summary>
    private bool PageRefused(ListPageCapture capture, int maxKeys)
    {
        if (capture.Body is not { } body)
        {
            return false; // no successful page reached the handler: the SDK's failure is the failure
        }

        try
        {
            _ = _parse(body, maxKeys);
            return false;
        }
        catch (Exception parseFailure) when (parseFailure is not OutOfMemoryException)
        {
            return true;
        }
    }

    /// <summary>The SHA-256 of a continuation token: 32 bytes remembered per page, whatever the token's length.</summary>
    private readonly record struct TokenDigest(ulong A, ulong B, ulong C, ulong D)
    {
        public static TokenDigest Of(string token)
        {
            Span<byte> h = stackalloc byte[32];
            SHA256.HashData(Encoding.UTF8.GetBytes(token), h);
            return new TokenDigest(
                BitConverter.ToUInt64(h[..8]), BitConverter.ToUInt64(h[8..16]), BitConverter.ToUInt64(h[16..24]), BitConverter.ToUInt64(h[24..]));
        }
    }
}
