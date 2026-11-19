// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net.Providers;

/// <summary>
/// A provider that can memoise work for the length of one bulk operation of the core — one
/// <see cref="Bundle.Load(IBundleProvider, BundleLoadOptions?)"/>, one
/// <see cref="BundleValidator.Validate"/> — and drop it when that operation ends. Internal: a
/// scope is an optimisation the core opens around its own calls, never a contract a host
/// relies on, and a provider used outside any scope answers exactly as it does inside one for
/// a tree that does not change meanwhile.
/// </summary>
internal interface IProviderScope
{
    /// <summary>
    /// Opens a scope for the calling flow and returns the handle that closes it. While it is
    /// open, the calls this flow makes may reuse what earlier calls of the same scope learned;
    /// once it is disposed, nothing learned inside it is used again, by this flow or by any
    /// flow that inherited it. A scope opened while one is already open on the same flow joins
    /// it: the outer scope stays the one that ends the memo.
    /// </summary>
    IDisposable BeginScope();
}

/// <summary>Opens a provider scope when the provider supports one.</summary>
internal static class ProviderScopes
{
    /// <summary>The scope of <paramref name="provider"/>, or <see langword="null"/> (a valid <c>using</c> target) when it has none.</summary>
    internal static IDisposable? Begin(IBundleProvider provider) => (provider as IProviderScope)?.BeginScope();

    /// <summary>A handle that closes nothing: a joined scope, or a wrapped provider without scopes.</summary>
    internal static IDisposable None { get; } = new NoScope();

    private sealed class NoScope : IDisposable
    {
        public void Dispose()
        {
        }
    }
}
