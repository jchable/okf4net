// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net.Providers;

/// <summary>
/// What an operation over several objects did (spec §1, §4): there is no multi-object
/// transaction, so each object is written or deleted atomically on its own and the report
/// says exactly which ones landed and which failed, with why. An operation that returns one
/// can be re-run safely.
/// </summary>
/// <param name="Written">The keys written, in the order they were written.</param>
/// <param name="Deleted">The keys deleted, in the order they were deleted.</param>
/// <param name="Failed">The keys that were not written or deleted, each with a library-authored reason.</param>
public sealed record BundleWriteReport(IReadOnlyList<BundleKey> Written, IReadOnlyList<BundleKey> Deleted, IReadOnlyList<BundleWriteFailure> Failed)
{
    /// <summary>A report of an operation that touched nothing.</summary>
    public static BundleWriteReport Empty { get; } = new([], [], []);

    /// <summary>The keys written, in the order they were written.</summary>
    public IReadOnlyList<BundleKey> Written { get; init; } = Written ?? throw new ArgumentNullException(nameof(Written));

    /// <summary>The keys deleted, in the order they were deleted.</summary>
    public IReadOnlyList<BundleKey> Deleted { get; init; } = Deleted ?? throw new ArgumentNullException(nameof(Deleted));

    /// <summary>The keys that were not written or deleted, each with a library-authored reason.</summary>
    public IReadOnlyList<BundleWriteFailure> Failed { get; init; } = Failed ?? throw new ArgumentNullException(nameof(Failed));

    /// <summary><see langword="true"/> when nothing failed.</summary>
    public bool Succeeded => Failed.Count == 0;
}

/// <summary>One object an operation could not write or delete, and why (a library-authored sentence that names keys, never host paths).</summary>
/// <param name="Key">The object's key.</param>
/// <param name="Reason">Why it failed.</param>
public sealed record BundleWriteFailure(BundleKey Key, string Reason)
{
    /// <summary>The object's key.</summary>
    public BundleKey Key { get; init; } = Key ?? throw new ArgumentNullException(nameof(Key));

    /// <summary>Why it failed.</summary>
    public string Reason { get; init; } = string.IsNullOrEmpty(Reason) ? throw new ArgumentException("a failure needs a reason", nameof(Reason)) : Reason;
}
