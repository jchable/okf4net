// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using OKF4net.Providers.S3.Internal;

namespace OKF4net.Providers.S3;

/// <summary>
/// A read-only, asynchronous bundle provider over Amazon S3 or an S3-compatible store (#142; spec of 2026-11-16).
/// The provider builds and owns its S3 client: the host gives credentials and an <see cref="S3BundleSource"/>, never
/// a client, so the request guard (one authority, one path prefix) holds on every request. Object versions are the
/// SHA-256 of the bytes read, in the filesystem provider's format; the bundle token hashes the listing.
/// <para>Not <see cref="BundleProviderCapabilities.CompletesSynchronously"/>: the library's synchronous entry points
/// refuse it. The bucket never appears in <see cref="Location"/> nor in any message.</para>
/// <para><b>Listing order.</b> The store must list keys in strictly increasing UTF-8 byte order, as Amazon S3's
/// general-purpose buckets do; a listing that does not is refused (<see cref="S3FailureKind.InvalidResponse"/>), since
/// the bundle token hashes the listing in its order (spec §4.5).</para>
/// </summary>
public sealed class S3BundleProvider : IBundleProvider, IDisposable
{
    private const int PageKeys = 1000;

    /// <summary>
    /// The page budget of one <see cref="ExistsAsync"/> (spec Annexe C10, maintainer's decision 2026-11-19). The first page
    /// that lists a key decides (an object's key sorts before every key it prefixes), so only truncated pages WITHOUT a key
    /// are followed: a store that skips non-current entries — Amazon S3 under delete markers of a versioned bucket — may
    /// send them before its answer. A thousand such pages is a generous, fixed bound (ExistsAsync has no
    /// <see cref="BundleLimits"/> to derive one from): past it the answer is <see cref="S3FailureKind.InvalidResponse"/>,
    /// never <see langword="false"/>. Each page is its own operation, under its own deadline and send cap.
    /// </summary>
    internal const int ExistsMaxPages = 1000;
    private readonly NormalizedSource _source;
    private readonly AddressingPlan _plan;
    private readonly AmazonS3Client _client;
    private readonly HttpClient _http;
    private readonly ListingWalker _walker;
    private readonly OperationGate _gate;
    private readonly CredentialResolver _credentials;

    /// <summary>Cancelled by <see cref="Dispose"/>, linked into every operation (never disposed itself: it holds no timer nor wait handle).</summary>
    private readonly CancellationTokenSource _disposal = new();
    private int _disposed;

    /// <summary>Serves the bundle <paramref name="source"/> names, reading with <paramref name="credentials"/>. No I/O.</summary>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="source"/> is outside the documented domain (see <see cref="S3BundleSource"/>).</exception>
    public S3BundleProvider(AWSCredentials credentials, S3BundleSource source)
        : this(credentials, source, static () => ClientBuilder.Network(), TimeProvider.System)
    {
    }

    /// <summary>Tests only: the network handler is replaced; the guard is still above it.</summary>
    internal S3BundleProvider(AWSCredentials credentials, S3BundleSource source, HttpMessageHandler network, TimeProvider? time = null)
        : this(credentials, source, Supplying(network), time)
    {
    }

    /// <summary>Tests only: the network handler comes from a factory, called once the source is validated.</summary>
    internal S3BundleProvider(AWSCredentials credentials, S3BundleSource source, Func<HttpMessageHandler> networkFactory, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(networkFactory);
        _source = SourceRules.Normalize(source);
        _plan = AddressingPlan.For(_source);

        // Built only now that nothing left can refuse the configuration: a refused source never owns a handler, so it
        // has nothing to dispose. From here the HttpClient owns the handler, and Dispose releases both.
        var network = networkFactory();
        ArgumentNullException.ThrowIfNull(network);
        _client = ClientBuilder.Build(new SnapshotCredentials(), _source, network, out _http);
        _gate = new OperationGate(_source.MaxConcurrentRequests, time ?? TimeProvider.System, _disposal.Token);
        _credentials = new CredentialResolver(credentials);
        _walker = new ListingWalker(_client, _source.Bucket, _gate, _credentials);
        Id = _source.Id;
        Location = _source.Location;
    }

    private static Func<HttpMessageHandler> Supplying(HttpMessageHandler network)
    {
        ArgumentNullException.ThrowIfNull(network);
        return () => network;
    }

    /// <inheritdoc/>
    public string Id { get; }

    /// <inheritdoc/>
    public string Location { get; }

    /// <inheritdoc/>
    public BundleProviderCapabilities Capabilities { get; } = new(CanWrite: false, CompletesSynchronously: false);

    /// <summary>Only <see cref="BundleKeyConstraints.WellFormedUnicode"/>: S3 stores UTF-8 names, case- and form-sensitive.</summary>
    public BundleKeyConstraints NativeConstraints => BundleKeyConstraints.WellFormedUnicode;

    /// <summary>
    /// Lists the bundle (or the keys under <paramref name="prefix"/>, at a segment boundary) one ListObjectsV2 page at a
    /// time; each raw name is the strictly decoded key without the bundle prefix, passed to the core unjudged (spec D9).
    /// A page is one operation under the shorter of <see cref="S3BundleSource.RequestTimeout"/> and
    /// <see cref="BundleLimits.RequestTimeout"/>; at most <see cref="BundleLimits.MaxEntries"/> + 1 pages are fetched.
    /// </summary>
    /// <exception cref="S3RequestException">A request failed, or the listing is inconsistent (<see cref="S3FailureKind.InvalidResponse"/>).</exception>
    public async IAsyncEnumerable<BundleListing> EnumerateAsync(BundleKey? prefix, BundleLimits limits, [EnumeratorCancellation] CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(limits);
        ThrowIfDisposed(S3Operation.List);
        var keyPrefix = _plan.KeyPrefix;
        await foreach (var entry in _walker.WalkAsync(_plan.ListPrefix(prefix), PageKeys, (long)limits.MaxEntries + 1, ListTimeout(limits), S3Operation.List, null, ct).ConfigureAwait(false))
        {
            yield return new BundleListing(entry.S3Key[keyPrefix.Length..], entry.Size, null);
        }
    }

    /// <summary>
    /// The s3v1 token of the whole listing (spec §4.5), hashed as it streams; the walk is bounded by
    /// <see cref="BundleLimits.MaxEntries"/> and <see cref="BundleLimits.MaxDepth"/>, counted on every name.
    /// </summary>
    /// <exception cref="BundleLimitExceededException">The listing exceeds a bound.</exception>
    /// <exception cref="S3RequestException">A request failed, or the listing is inconsistent.</exception>
    public async ValueTask<BundleVersion> GetVersionAsync(BundleLimits limits, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(limits);
        ThrowIfDisposed(S3Operation.List);
        using var hasher = new BundleTokenHasher();
        var count = 0L;
        var keyPrefix = _plan.KeyPrefix;
        await foreach (var entry in _walker.WalkAsync(keyPrefix, PageKeys, (long)limits.MaxEntries + 1, ListTimeout(limits), S3Operation.List, null, ct).ConfigureAwait(false))
        {
            if (++count > limits.MaxEntries)
            {
                throw new BundleLimitExceededException(BundleBound.Entries, limits.MaxEntries);
            }

            var raw = entry.S3Key[keyPrefix.Length..];
            if (raw.Count(c => c == '/') + 1 > limits.MaxDepth)
            {
                throw new BundleLimitExceededException(BundleBound.Depth, limits.MaxDepth);
            }

            hasher.Add(raw, entry.ETag, entry.Size);
        }

        return new BundleVersion(hasher.Finish());
    }

    /// <summary>
    /// ListObjectsV2 pages of one key with the object's key as prefix (spec §4.3): <see langword="true"/> when the
    /// first key listed is exactly it (a key sorts before every key it prefixes). Truncated pages without a key are
    /// followed, at most <see cref="ExistsMaxPages"/> pages in all, each one operation under the source's deadline (spec
    /// Annexe C10). A refusal or a failure is never <see langword="false"/>.
    /// </summary>
    /// <exception cref="S3RequestException">A request failed, or a page is inconsistent, or the page budget is spent.</exception>
    public async ValueTask<bool> ExistsAsync(BundleKey key, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(key); // as ReadAsync: a null key would ask about the prefix's own folder marker
        ThrowIfDisposed(S3Operation.Exists);
        var s3Key = _plan.S3Key(key);
        await foreach (var entry in _walker.WalkAsync(s3Key, 1, ExistsMaxPages, OperationTimeout(_source.RequestTimeout, null), S3Operation.Exists, key, ct).ConfigureAwait(false))
        {
            return string.Equals(entry.S3Key, s3Key, StringComparison.Ordinal);
        }

        return false;
    }

    /// <summary>
    /// One GetObject without condition (spec §4.3, D8), as one operation: a permit, the deadline of
    /// <see cref="S3BundleSource.RequestTimeout"/> from the call to the end of the body and of its release, a
    /// credentials snapshot. A declared Content-Length past <see cref="S3BundleSource.MaxObjectBytes"/> is refused
    /// before the body is read; otherwise the body is read whole into one buffer (sized by Content-Length, grown
    /// against the same bound without it), never more than the bound + 1 bytes, then hashed: the version is the
    /// lowercase hex SHA-256 of the bytes, the filesystem provider's format. <paramref name="expected"/> is compared
    /// with that hash, never with an ETag, and no conditional header is sent. The object's stream is a read-only
    /// <see cref="MemoryStream"/> over that buffer, without a copy, and its <see cref="BundleObject.Length"/> is exact.
    /// </summary>
    /// <exception cref="BundleObjectNotFoundException">404 NoSuchKey.</exception>
    /// <exception cref="BundleAccessRefusedException">403 AccessDenied.</exception>
    /// <exception cref="BundleVersionMismatchException">The bytes read are not <paramref name="expected"/>.</exception>
    /// <exception cref="BundleObjectTooLargeException">The object is larger than <see cref="S3BundleSource.MaxObjectBytes"/>.</exception>
    /// <exception cref="S3RequestException">Any other failure (spec §4.6), the provider disposed included.</exception>
    /// <exception cref="OperationCanceledException">The caller cancelled; it carries <paramref name="ct"/>.</exception>
    public async ValueTask<BundleObject> ReadAsync(BundleKey key, BundleObjectVersion? expected, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(key);
        ThrowIfDisposed(S3Operation.Read);
        var s3Key = _plan.S3Key(key);
        ArraySegment<byte> bytes = default;
        var scope = await _gate.EnterAsync(S3Operation.Read, OperationTimeout(_source.RequestTimeout, null), ct, key).ConfigureAwait(false);
        await using (scope.ConfigureAwait(false))
        {
            GetObjectResponse? response = null;
            Exception? failure = null;
            try
            {
                // Resolved BEFORE the operation context exists: the host's call runs isolated on the pool, and Task.Run
                // flows the ExecutionContext — a context set first would reach the host's code.
                var snapshot = await _credentials.ResolveAsync(scope).ConfigureAwait(false);
                OperationContext.Current.Value = new OperationContext(S3Operation.Read) { ExpectedObjectS3Key = s3Key, Credentials = snapshot };
                response = await _client.GetObjectAsync(new GetObjectRequest { BucketName = _source.Bucket, Key = s3Key }, scope.Token).ConfigureAwait(false);
                var declared = response.ContentLength;
                if (declared > _source.MaxObjectBytes)
                {
                    throw new BundleObjectTooLargeException(key, declared, _source.MaxObjectBytes);
                }

                // A body that ignores the token: cancelling may not unblock the SDK's stream (gate G7), disposing the
                // response does. The callback never throws; the read it interrupts is classified by scope.Fail (the
                // deadline's Timeout, the caller's cancellation, the provider's Disposed).
                using var abort = scope.Token.Register(static r => DisposeQuietly((IDisposable)r!), response);
                bytes = await ReadAllAsync(response.ResponseStream, declared, key, scope.Token).ConfigureAwait(false);
                if (declared > 0 && bytes.Count < declared)
                {
                    // Shorter than its framing (HttpClient reports this itself on the wire): a transport failure.
                    throw new IOException("the body ended before its declared length");
                }
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                failure = scope.Fail(e); // first, before any cleanup: the caller's cancellation is sampled now
            }
            finally
            {
                if (response is not null)
                {
                    try
                    {
                        response.Dispose(); // the SDK may verify a checksum here
                    }
                    catch (Exception e) when (e is not OutOfMemoryException)
                    {
                        failure = scope.Fail(e); // the first failure when there was one; this one, classified, otherwise
                    }
                }

                OperationContext.Current.Value = null;
            }

            if (failure is not null)
            {
                throw failure;
            }
        }

        var version = new BundleObjectVersion(Convert.ToHexStringLower(SHA256.HashData(bytes.AsSpan())));
        if (expected is { } wanted && wanted != version)
        {
            throw new BundleVersionMismatchException(key);
        }

        return new BundleObject(new MemoryStream(bytes.Array!, bytes.Offset, bytes.Count, writable: false), version, bytes.Count);
    }

    /// <summary>
    /// Reads the whole body into one buffer, never holding more than MaxObjectBytes nor allocating past it: the buffer
    /// starts at Content-Length when known (at most max), else at min(80 KiB, max), and doubles up to max; reads go into
    /// its free space only, and when it is full ONE probe byte, read into a separate array, tells the end from an excess
    /// (spec §4.3) — so at most max + 1 bytes are ever read, and max may be Array.MaxLength itself.
    /// </summary>
    private async Task<ArraySegment<byte>> ReadAllAsync(Stream body, long declared, BundleKey key, CancellationToken ct)
    {
        var max = (int)_source.MaxObjectBytes; // at most Array.MaxLength (SourceRules)
        var buffer = new byte[declared > 0 ? (int)declared : Math.Min(81920, max)];
        var probe = new byte[1];
        var total = 0;
        while (true)
        {
            if (total == buffer.Length)
            {
                var p = await body.ReadAsync(probe, ct).ConfigureAwait(false);
                if (p == 0)
                {
                    return new ArraySegment<byte>(buffer, 0, total);
                }

                if (total == max)
                {
                    throw new BundleObjectTooLargeException(key, (long)total + 1, max);
                }

                Array.Resize(ref buffer, (int)Math.Min((long)buffer.Length * 2, max));
                buffer[total++] = probe[0];
                continue;
            }

            var n = await body.ReadAsync(buffer.AsMemory(total), ct).ConfigureAwait(false);
            if (n == 0)
            {
                return new ArraySegment<byte>(buffer, 0, total);
            }

            total += n;
        }
    }

    private static void DisposeQuietly(IDisposable d)
    {
        try
        {
            d.Dispose();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // A cancellation callback never throws; the interrupted read reports the outcome.
        }
    }

    /// <inheritdoc/>
    public ValueTask<BundleObjectVersion> WriteAsync(BundleKey key, ReadOnlyMemory<byte> content, BundleWriteCondition condition, CancellationToken ct) =>
        ValueTask.FromException<BundleObjectVersion>(new BundleReadOnlyException(Id));

    /// <inheritdoc/>
    public ValueTask DeleteAsync(BundleKey key, BundleWriteCondition condition, CancellationToken ct) =>
        ValueTask.FromException(new BundleReadOnlyException(Id));

    /// <summary>
    /// Releases, once, the S3 client and the HttpClient this provider built. An operation in flight is interrupted
    /// first (its permit wait, its credentials wait, its request, its body) and fails as
    /// <see cref="S3FailureKind.Disposed"/>, like every later call.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            try
            {
                _disposal.Cancel();
            }
            catch (AggregateException)
            {
                // A callback registered on an operation's token threw: the operations are interrupted all the same, and
                // the release below must still happen.
            }

            _client.Dispose();
            _http.Dispose();
        }
    }

    private TimeSpan ListTimeout(BundleLimits limits) => OperationTimeout(_source.RequestTimeout, limits.RequestTimeout);

    /// <summary>The longest deadline an operation is given: <see cref="int.MaxValue"/> milliseconds (about 24.8 days), within what a <see cref="CancellationTokenSource"/> accepts.</summary>
    internal static readonly TimeSpan MaxOperationTimeout = TimeSpan.FromMilliseconds(int.MaxValue);

    /// <summary>
    /// One operation's deadline (spec §4.5): <paramref name="source"/>'s timeout, or <paramref name="limit"/> when it is
    /// positive and shorter — an infinite, zero or negative <see cref="BundleLimits.RequestTimeout"/> never wins — capped
    /// at <see cref="MaxOperationTimeout"/>, so no configuration makes the deadline itself throw.
    /// </summary>
    internal static TimeSpan OperationTimeout(TimeSpan source, TimeSpan? limit)
    {
        var timeout = limit is { } l && l > TimeSpan.Zero && l < source ? l : source;
        return timeout > MaxOperationTimeout ? MaxOperationTimeout : timeout;
    }

    private void ThrowIfDisposed(S3Operation operation)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            throw new S3RequestException(operation, S3FailureKind.Disposed, null, null);
        }
    }
}
