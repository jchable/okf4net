// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net;

/// <summary>
/// An <see cref="IAsyncEnumerable{T}"/> over a synchronous sequence, for a provider whose
/// listing is already in hand (#152): every <c>MoveNextAsync</c> and <c>DisposeAsync</c>
/// returns an already-completed <see cref="ValueTask"/>, so nothing allocates and nothing
/// waits. The sequence is produced at the first <c>MoveNextAsync</c>, so a failure to list
/// is delivered through the task, like every other failure of the provider contract.
/// (A hand-written enumerator rather than an <see langword="async"/> iterator, which would
/// have no <see langword="await"/> to justify it.)
/// </summary>
internal static class SyncAsyncEnumerable
{
    /// <summary>An async view of the sequence <paramref name="produce"/> returns when first enumerated.</summary>
    internal static IAsyncEnumerable<T> Create<T>(Func<IEnumerable<T>> produce, CancellationToken ct) => new Source<T>(produce, ct);


    private sealed class Source<T>(Func<IEnumerable<T>> produce, CancellationToken ct) : IAsyncEnumerable<T>
    {
        public IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken = default) =>
            new Enumerator<T>(produce, ct, cancellationToken);
    }

    private sealed class Enumerator<T>(Func<IEnumerable<T>> produce, CancellationToken ct, CancellationToken enumeratorToken) : IAsyncEnumerator<T>
    {
        private IEnumerator<T>? _inner;

        public T Current => _inner is null ? default! : _inner.Current;

        public ValueTask<bool> MoveNextAsync()
        {
            try
            {
                // Both tokens count: the one EnumerateAsync was given and the one GetAsyncEnumerator was given.
                ct.ThrowIfCancellationRequested();
                enumeratorToken.ThrowIfCancellationRequested();
                _inner ??= produce().GetEnumerator();
                return ValueTask.FromResult(_inner.MoveNext());
            }
            catch (Exception e)
            {
                return ValueTask.FromException<bool>(e);
            }
        }

        public ValueTask DisposeAsync()
        {
            _inner?.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
