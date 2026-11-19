// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net;

/// <summary>
/// A hold of a bundle's write lock, from <c>BundleConceptWriter.EnterWriteLock</c>: the
/// writer's lexical gate and the resolved lock of its bundle, both
/// <see cref="SemaphoreSlim"/>s. Unlike a <see cref="Monitor"/> it is not tied to a
/// thread, so it may be held across an <see langword="await"/> and disposed on another
/// thread. It is not re-entrant: the code an operation calls under its lease receives the
/// lease instead of acquiring again.
/// </summary>
internal readonly struct WriteLease : IDisposable
{
    private readonly SemaphoreSlim? _gate;

    internal WriteLease(SemaphoreSlim gate, SemaphoreSlim resolved)
    {
        _gate = gate;
        LockObject = resolved;
    }

    /// <summary>The registry object this lease holds (the bundle's resolved lock).</summary>
    internal SemaphoreSlim? LockObject { get; }

    /// <summary>Whether this lease holds its root's lexical gate. Test-only.</summary>
    internal bool HoldsLexicalGateForTest => _gate is not null;

    /// <summary>Releases the gate, then the resolved lock. Dispose it exactly once; <c>default</c> does nothing.</summary>
    public void Dispose()
    {
        if (LockObject is null)
        {
            return;
        }

        // The gate first, then the resolved lock: a waiter woken by the second
        // release then usually finds the gate free. Releases never block.
        _gate!.Release();
        LockObject.Release();
    }
}
