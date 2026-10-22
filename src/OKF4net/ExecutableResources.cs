// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net.Providers;

namespace OKF4net;

/// <summary>
/// The state of one executable resource a §10 concept references — its <c>computation</c>
/// file or its <c>attester.resource</c> — as surveyed when the bundle was loaded (spec §5.1).
/// A state other than <see cref="Pinned"/> never fails the load and never removes the concept
/// from the image; it refuses the execution of that computation, naming the resource and the
/// state — except <see cref="Undeclared"/> for the <c>computation</c> field, where the code is
/// inline and still runs.
/// </summary>
public enum ExecutableResourceState
{
    /// <summary>Read and hashed at the load: executable, pinned to <see cref="ExecutableResource.Version"/>.</summary>
    Pinned,

    /// <summary>The concept declares no such field (for the computation: no <c>computation:</c> path, the code is inline or absent; for the attester: no <c>attester.resource</c>, which refuses the execution, D6).</summary>
    Undeclared,

    /// <summary>The value is a <c>scheme://…</c> URL: never read, and the execution is refused (D6).</summary>
    Url,

    /// <summary>The §6.2 resolution was refused (spec §2): the value climbs above the root, names a rejected key, or its path is refused by the provider.</summary>
    Unsafe,

    /// <summary>No object under the resolved key.</summary>
    Missing,

    /// <summary>The object could not be read, or is not valid UTF-8.</summary>
    Unreadable,

    /// <summary>The object is larger than <see cref="BundleLimits.MaxObjectBytes"/> (spec §6); nothing past the bound was read.</summary>
    TooLarge,
}

/// <summary>One executable resource of a §10 concept and its surveyed state (spec §5.1).</summary>
/// <param name="Field">The frontmatter field: <c>computation</c> or <c>attester.resource</c>.</param>
/// <param name="RawPath">The field's raw value, or <see langword="null"/> when <see cref="State"/> is <see cref="ExecutableResourceState.Undeclared"/>.</param>
/// <param name="State">The surveyed state.</param>
/// <param name="Key">The resolved key, when the value resolved to one and the read was attempted (<see cref="ExecutableResourceState.Pinned"/>, <see cref="ExecutableResourceState.Unreadable"/>, <see cref="ExecutableResourceState.TooLarge"/>, and <see cref="ExecutableResourceState.Missing"/> or <see cref="ExecutableResourceState.Unsafe"/> when the provider reported it at the read); <see langword="null"/> for a value the §6.2 resolution itself classified.</param>
/// <param name="Version">The object's version when <see cref="State"/> is <see cref="ExecutableResourceState.Pinned"/>: what a later read is pinned to.</param>
public sealed record ExecutableResource(string Field, string? RawPath, ExecutableResourceState State, BundleKey? Key, BundleObjectVersion? Version)
{
    /// <summary><see langword="true"/> when the resource was read and hashed at the load.</summary>
    public bool IsPinned => State == ExecutableResourceState.Pinned;
}

/// <summary>The two executable resources of one §10 concept (spec §5.1), surveyed at the load.</summary>
/// <param name="Computation">The <c>computation</c> file; <see cref="ExecutableResourceState.Undeclared"/> when the code is inline (or absent).</param>
/// <param name="Attester">The <c>attester.resource</c>.</param>
public sealed record ExecutableResourceSurvey(ExecutableResource Computation, ExecutableResource Attester);
