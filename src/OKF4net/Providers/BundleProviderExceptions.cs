// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net.Providers;

/// <summary>Base of the provider contract's exceptions (spec §1). Messages are library-authored and name keys, never host paths or credentials.</summary>
public abstract class BundleProviderException : OkfException
{
    /// <summary>Creates the exception with a library-authored message.</summary>
    protected BundleProviderException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a library-authored message and the raw cause as <see cref="Exception.InnerException"/> (the host-log channel, spec §7).</summary>
    protected BundleProviderException(string message, Exception inner)
        : base(message, inner)
    {
    }
}

/// <summary>A write or delete was attempted on a provider whose <see cref="BundleProviderCapabilities.CanWrite"/> is <see langword="false"/> (spec §3). Nothing was attempted. The message never carries the provider id (for the filesystem it is a host path, §7); it is on <see cref="ProviderId"/>.</summary>
public sealed class BundleReadOnlyException(string providerId)
    : BundleProviderException("the provider is read-only; the write was refused before being attempted")
{
    /// <summary>The refusing provider's <see cref="IBundleProvider.Id"/>, for the host's log.</summary>
    public string ProviderId { get; } = providerId;
}

/// <summary>Why a write or delete was refused as a conflict — a closed set, so the message is built from it and never from text a provider wrote.</summary>
public enum BundleWriteConflictReason
{
    /// <summary>The condition did not hold: the object exists, is absent, or has another version than the one expected (spec §4).</summary>
    ConditionFailed,

    /// <summary>The storage cannot hold an object under that exact name: it holds an entry it would confuse with it (a directory, or a name differing only in case or Unicode form on a volume that folds them), or the name is one it cannot store literally (spec §2, §8).</summary>
    NameUnavailable,
}

/// <summary>A write or delete was refused because its condition did not hold, or because the storage cannot hold that exact name (spec §4); nothing was changed.</summary>
public sealed class BundleWriteConflictException(BundleKey key, BundleWriteConflictReason reason)
    : BundleProviderException(Describe(key, reason))
{
    /// <summary>A conflict on the condition (<see cref="BundleWriteConflictReason.ConditionFailed"/>).</summary>
    public BundleWriteConflictException(BundleKey key)
        : this(key, BundleWriteConflictReason.ConditionFailed)
    {
    }

    /// <summary>The key whose write or delete was refused.</summary>
    public BundleKey Key { get; } = key;

    /// <summary>Why it was refused.</summary>
    public BundleWriteConflictReason Reason { get; } = reason;

    private static string Describe(BundleKey key, BundleWriteConflictReason reason) => reason switch
    {
        BundleWriteConflictReason.NameUnavailable => $"'{key}' cannot be stored under that name: the storage holds an entry it would confuse with it (a directory, or a name differing only in case or Unicode form), or cannot hold the name",
        _ => $"'{key}' changed since it was read; the write was refused",
    };
}

/// <summary>
/// A write or delete failed in a way that leaves its outcome unknown (spec §4): the storage
/// reported an error from the commit itself, and what it holds afterwards could not be
/// established. The object may hold its previous content, the new content, or nothing; a
/// provider keeps whatever it could not account for, and says where in its own documentation.
/// The raw failure is the <see cref="Exception.InnerException"/>, for the host's log only.
/// </summary>
public sealed class BundleWriteOutcomeUnknownException(BundleKey key, Exception inner)
    : BundleProviderException($"the write of '{key}' failed with an unknown outcome: the object may hold its previous content, the new content, or nothing", inner)
{
    /// <summary>The key whose write has an unknown outcome.</summary>
    public BundleKey Key { get; } = key;
}

/// <summary>A read with an expected version found a different one (pinning, spec §5.4).</summary>
public sealed class BundleVersionMismatchException(BundleKey key)
    : BundleProviderException($"'{key}' is no longer the version that was observed")
{
    /// <summary>The key whose version changed.</summary>
    public BundleKey Key { get; } = key;
}

/// <summary>No object under the key.</summary>
public sealed class BundleObjectNotFoundException(BundleKey key)
    : BundleProviderException($"no object under '{key}'")
{
    /// <summary>The missing key.</summary>
    public BundleKey Key { get; } = key;
}

/// <summary>
/// An object exceeds <see cref="BundleLimits.MaxObjectBytes"/> (spec §6). Derives from
/// <see cref="IOException"/> so every existing handler that treats an I/O failure as
/// "unreadable" classifies it correctly without change (spec, point to validate 6).
/// </summary>
public sealed class BundleObjectTooLargeException(BundleKey key, long observedBytes, long maxBytes)
    : IOException($"'{key}' is larger than the {maxBytes}-byte bound ({observedBytes} bytes read before stopping)")
{
    /// <summary>The offending key.</summary>
    public BundleKey Key { get; } = key;
}

/// <summary>A STRICT path guard or the confinement boundary refused an access (spec §8). The message names the key only; <see cref="Detail"/> is the provider's own explanation, for the host's log.</summary>
public sealed class BundleAccessRefusedException(BundleKey? key, string detail)
    : BundleProviderException(key is null ? "access to the bundle was refused by its provider" : $"access to '{key}' was refused by the provider")
{
    /// <summary>The key, when the refusal concerned one.</summary>
    public BundleKey? Key { get; } = key;

    /// <summary>Why, in the provider's words — may name storage internals; log it, never show it to a model.</summary>
    public string Detail { get; } = detail;
}

/// <summary>The bounds of spec §6 a walk or a load can exceed — a closed set, so a message built from it carries nothing a provider wrote (decision 16).</summary>
public enum BundleBound
{
    /// <summary><see cref="BundleLimits.MaxEntries"/>.</summary>
    Entries,

    /// <summary><see cref="BundleLimits.MaxDepth"/>.</summary>
    Depth,

    /// <summary><see cref="BundleLimits.MaxKeyBytes"/>.</summary>
    KeyBytes,
}

/// <summary>A walk or a load exceeded one of its <see cref="BundleLimits"/> (spec §6). The message names the bound by its category only.</summary>
public sealed class BundleLimitExceededException(BundleBound bound, long limit)
    : BundleProviderException($"the bundle exceeds the {Describe(bound)} bound ({limit})")
{
    /// <summary>Which bound was exceeded.</summary>
    public BundleBound Bound { get; } = bound;

    /// <summary>The bound's value.</summary>
    public long Limit { get; } = limit;

    /// <summary>The fixed word for a bound in messages: <c>entries</c>, <c>depth</c>, <c>key-length</c>.</summary>
    public static string Describe(BundleBound bound) => bound switch
    {
        BundleBound.Entries => "entries",
        BundleBound.Depth => "depth",
        BundleBound.KeyBytes => "key-length",
        _ => "unknown",
    };
}
