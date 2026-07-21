// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net.Yaml;

/// <summary>
/// An ordered YAML mapping (`{...}` or block `key: value`). Preserves
/// insertion / source order, mirroring the Rust <c>Mapping</c> (a
/// <c>Vec</c> of entries). Keys are fixed to <see cref="string"/>: the OKF
/// subset always uses scalar string keys, and the parser (Task 2) rejects
/// non-scalar keys the same way the Rust parser does.
/// </summary>
public sealed class YamlMapping : YamlValue
{
    private readonly List<KeyValuePair<string, YamlValue>> _entries = [];
    private readonly Dictionary<string, int> _index = new(StringComparer.Ordinal);

    public YamlMapping()
    {
    }

    /// <summary>Number of key/value pairs.</summary>
    public int Count => _entries.Count;

    /// <summary>True if the mapping has no entries.</summary>
    public bool IsEmpty => _entries.Count == 0;

    /// <summary>Looks up a value by string key.</summary>
    public YamlValue? Get(string key) =>
        _index.TryGetValue(key, out var idx) ? _entries[idx].Value : null;

    /// <summary>True if the mapping contains the given string key.</summary>
    public bool ContainsKey(string key) => _index.ContainsKey(key);

    /// <summary>
    /// Inserts (or, if the key already exists, replaces) a value, preserving
    /// the position of an existing key. Returns the previous value, if any.
    /// </summary>
    public YamlValue? Insert(string key, YamlValue value)
    {
        if (_index.TryGetValue(key, out var idx))
        {
            var previous = _entries[idx].Value;
            _entries[idx] = new KeyValuePair<string, YamlValue>(key, value);
            return previous;
        }

        _index[key] = _entries.Count;
        _entries.Add(new KeyValuePair<string, YamlValue>(key, value));
        return null;
    }

    /// <summary>Removes a value by string key, preserving order of the rest.</summary>
    public YamlValue? Remove(string key)
    {
        if (!_index.TryGetValue(key, out var idx))
        {
            return null;
        }

        var removed = _entries[idx].Value;
        _entries.RemoveAt(idx);
        _index.Remove(key);

        for (var i = idx; i < _entries.Count; i++)
        {
            _index[_entries[i].Key] = i;
        }

        return removed;
    }

    /// <summary>Iterates over (key, value) pairs in insertion order.</summary>
    public IEnumerable<KeyValuePair<string, YamlValue>> Entries => _entries;

    /// <summary>Iterates over keys in insertion order.</summary>
    public IEnumerable<string> Keys => _entries.Select(e => e.Key);
}
