// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Collections;

namespace OKF4net.Catalog;

/// <summary>
/// The <c>settings</c> of a <c>catalog.json</c> source (spec §9, "nommer un provider"): a
/// read-only map of string values, compared by value so that two sources with the same
/// settings are equal and a reload can tell whether a source's configuration changed.
/// Which names are accepted is the provider factory's declaration
/// (<see cref="IBundleProviderFactory.AcceptedSettings"/>); a credential never travels here,
/// it is injected into the factory by the host.
/// </summary>
public sealed class ProviderSettings : IReadOnlyDictionary<string, string>, IEquatable<ProviderSettings>
{
    private readonly KeyValuePair<string, string>[] _pairs;

    private ProviderSettings(KeyValuePair<string, string>[] pairs)
    {
        _pairs = pairs;
    }

    /// <summary>No setting.</summary>
    public static ProviderSettings Empty { get; } = new([]);

    /// <summary>Builds a settings map from <paramref name="pairs"/>, ordered by name (ordinal).</summary>
    /// <exception cref="ArgumentException">A name or a value is <see langword="null"/>, or a name appears twice.</exception>
    public static ProviderSettings From(IEnumerable<KeyValuePair<string, string>> pairs)
    {
        ArgumentNullException.ThrowIfNull(pairs);
        var sorted = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, value) in pairs)
        {
            if (name is null || value is null)
            {
                throw new ArgumentException("a setting needs a name and a value", nameof(pairs));
            }

            if (!sorted.TryAdd(name, value))
            {
                throw new ArgumentException($"the setting '{name}' appears twice", nameof(pairs));
            }
        }

        return sorted.Count == 0 ? Empty : new([.. sorted]);
    }

    /// <inheritdoc/>
    public string this[string key] => TryGetValue(key, out var value) ? value : throw new KeyNotFoundException(key);

    /// <inheritdoc/>
    public IEnumerable<string> Keys => _pairs.Select(p => p.Key);

    /// <inheritdoc/>
    public IEnumerable<string> Values => _pairs.Select(p => p.Value);

    /// <inheritdoc/>
    public int Count => _pairs.Length;

    /// <inheritdoc/>
    public bool ContainsKey(string key) => TryGetValue(key, out _);

    /// <inheritdoc/>
    public bool TryGetValue(string key, out string value)
    {
        foreach (var pair in _pairs)
        {
            if (string.Equals(pair.Key, key, StringComparison.Ordinal))
            {
                value = pair.Value;
                return true;
            }
        }

        value = null!;
        return false;
    }

    /// <inheritdoc/>
    public IEnumerator<KeyValuePair<string, string>> GetEnumerator() => ((IEnumerable<KeyValuePair<string, string>>)_pairs).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>Value equality: the same names with the same values (ordinal).</summary>
    public bool Equals(ProviderSettings? other)
    {
        if (other is null || other._pairs.Length != _pairs.Length)
        {
            return false;
        }

        for (var i = 0; i < _pairs.Length; i++)
        {
            if (!string.Equals(_pairs[i].Key, other._pairs[i].Key, StringComparison.Ordinal)
                || !string.Equals(_pairs[i].Value, other._pairs[i].Value, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    /// <inheritdoc/>
    public override bool Equals(object? obj) => Equals(obj as ProviderSettings);

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var (name, value) in _pairs)
        {
            hash.Add(name, StringComparer.Ordinal);
            hash.Add(value, StringComparer.Ordinal);
        }

        return hash.ToHashCode();
    }

    /// <summary>The settings as <c>name=value</c> pairs joined by <c>;</c>, for diagnostics.</summary>
    public override string ToString() => string.Join(";", _pairs.Select(p => $"{p.Key}={p.Value}"));
}
