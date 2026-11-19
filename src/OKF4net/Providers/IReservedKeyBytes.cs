// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net.Providers;

/// <summary>
/// A provider whose keys are stored under a longer name in some other key space: the view of
/// one prefix of another provider (the catalog's memory scopes). A key of the view is a key
/// there only while the stored name stays within <see cref="BundleKey.MaxUtf8Bytes"/>, so the
/// view can hold, under that bound, names its storage cannot. <see cref="Bundle.ListNamesAsync"/>
/// classifies such a name as over-long, exactly as a name past the bound in an ordinary
/// bundle: never read, a parse problem for a concept, and its ancestors still counted by the
/// tree validation.
/// </summary>
internal interface IReservedKeyBytes
{
    /// <summary>How many UTF-8 bytes of <see cref="BundleKey.MaxUtf8Bytes"/> the storage name adds to a key of this provider.</summary>
    int ReservedKeyBytes { get; }
}
