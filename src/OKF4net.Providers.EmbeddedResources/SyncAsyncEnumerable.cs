// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net.Providers.EmbeddedResources;

/// <summary>
/// An <see cref="IAsyncEnumerable{T}"/> over a synchronous sequence: every <c>MoveNextAsync</c> and
/// <c>DisposeAsync</c> returns an already-completed <see cref="ValueTask"/>, so a listing that is already in
/// hand neither allocates a task nor waits, and a failure to list is delivered through the task like every
/// other failure of the provider contract.
/// <para>A deliberate copy of the core's internal helper of the same name (#141, decision of 2026-11-15): this
/// package is versioned beside the core but NuGet only requires a minimum version of it, so a package that
/// called an internal of the core would fail at run time with a <see cref="MissingMethodException"/> against
/// a core that renamed it. If the core's behaviour changes, change this one by hand.</para>
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
