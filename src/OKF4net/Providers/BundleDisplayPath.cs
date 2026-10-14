// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net.Providers;

/// <summary>
/// The display rule of spec §8: a provider's <see cref="IBundleProvider.Location"/>
/// followed by the key's segments joined with the OS separator — exactly what
/// <c>Path.Combine(root, …)</c> produced before the seam, so the CLI's bytes do not move.
/// </summary>
public static class BundleDisplayPath
{
    /// <summary>Composes the displayable path of <paramref name="key"/> under <paramref name="location"/>.</summary>
    public static string Compose(string location, BundleKey key)
    {
        ArgumentNullException.ThrowIfNull(location);
        ArgumentNullException.ThrowIfNull(key);
        var parts = new string[key.Segments.Count + 1];
        parts[0] = location;
        for (var i = 0; i < key.Segments.Count; i++)
        {
            parts[i + 1] = key.Segments[i];
        }

        return Path.Combine(parts);
    }
}
