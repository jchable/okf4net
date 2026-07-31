// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OkfProducer.Core.Generation;

/// <summary>How <see cref="IBundleWriter"/> treats a non-empty output directory.</summary>
public enum WritePolicy
{
    /// <summary>Refuse to write unless the output directory is empty or missing (the default).</summary>
    RequireEmpty,

    /// <summary>Write into a non-empty directory, preserving files this run doesn't generate.</summary>
    Update,

    /// <summary>Delete the output directory (if it exists) and recreate it before writing.</summary>
    Reset,
}
