// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Runtime.CompilerServices;
using Amazon.Runtime;
using Amazon.S3;
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
    private readonly NormalizedSource _source;
    private readonly AddressingPlan _plan;
    private readonly AmazonS3Client _client;
    private readonly HttpClient _http;
    private readonly ListingWalker _walker;
    private int _disposed;

    /// <summary>Serves the bundle <paramref name="source"/> names, reading with <paramref name="credentials"/>. No I/O.</summary>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="source"/> is outside the documented domain (see <see cref="S3BundleSource"/>).</exception>
    public S3BundleProvider(AWSCredentials credentials, S3BundleSource source)
        : this(credentials, source, ClientBuilder.Network(), TimeProvider.System)
    {
    }

    /// <summary>Tests only: the network handler is replaced; the guard is still above it.</summary>
    internal S3BundleProvider(AWSCredentials credentials, S3BundleSource source, HttpMessageHandler network, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(network);
        _source = SourceRules.Normalize(source);
        _plan = AddressingPlan.For(_source);
        _client = ClientBuilder.Build(new SnapshotCredentials(), _source, network, out _http);
        var gate = new OperationGate(_source.MaxConcurrentRequests, time ?? TimeProvider.System);
        _walker = new ListingWalker(_client, _source.Bucket, gate, new CredentialResolver(credentials));
        Id = _source.Id;
        Location = _source.Location;
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
    /// One ListObjectsV2 page of one key with the object's key as prefix (spec §4.3): <see langword="true"/> when the
    /// first key listed is exactly it (a key sorts before every key it prefixes). A refusal or a failure is never
    /// <see langword="false"/>.
    /// </summary>
    /// <exception cref="S3RequestException">A request failed, or the page is inconsistent.</exception>
    public async ValueTask<bool> ExistsAsync(BundleKey key, CancellationToken ct)
    {
        ThrowIfDisposed(S3Operation.Exists);
        var s3Key = _plan.S3Key(key);
        await foreach (var entry in _walker.WalkAsync(s3Key, 1, 1, OperationTimeout(_source.RequestTimeout, null), S3Operation.Exists, key, ct).ConfigureAwait(false))
        {
            return string.Equals(entry.S3Key, s3Key, StringComparison.Ordinal);
        }

        return false;
    }

    /// <inheritdoc/>
    public ValueTask<BundleObject> ReadAsync(BundleKey key, BundleObjectVersion? expected, CancellationToken ct) =>
        ValueTask.FromException<BundleObject>(new NotImplementedException("Task 8"));

    /// <inheritdoc/>
    public ValueTask<BundleObjectVersion> WriteAsync(BundleKey key, ReadOnlyMemory<byte> content, BundleWriteCondition condition, CancellationToken ct) =>
        ValueTask.FromException<BundleObjectVersion>(new BundleReadOnlyException(Id));

    /// <inheritdoc/>
    public ValueTask DeleteAsync(BundleKey key, BundleWriteCondition condition, CancellationToken ct) =>
        ValueTask.FromException(new BundleReadOnlyException(Id));

    /// <summary>Releases, once, the S3 client and the HttpClient this provider built.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
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
