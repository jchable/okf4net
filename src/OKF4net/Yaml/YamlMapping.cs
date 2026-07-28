// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Collections.ObjectModel;

namespace OKF4net.Yaml;

/// <summary>
/// An ordered YAML mapping (`{...}` or block `key: value`). Preserves
/// insertion / source order: entries are a flat list of <em>typed</em>
/// <c>(Key, Value)</c> pairs — not a string-keyed dictionary. The OKF subset
/// always uses scalar string keys in
/// practice, but the parser accepts (and the emitter faithfully re-emits) any
/// scalar key, and duplicate keys are preserved rather than deduplicated
/// (only <see cref="Insert"/>, the typed-consumer convenience method, dedups
/// by replacing in place).
/// </summary>
public sealed class YamlMapping : YamlValue
{
    private readonly List<(YamlValue Key, YamlValue Value)> _entries = [];
    private ReadOnlyCollection<(YamlValue Key, YamlValue Value)>? _entriesView;

    /// <summary>Creates an empty mapping.</summary>
    public YamlMapping()
    {
    }

    /// <summary>Number of key/value pairs (including duplicates and non-string keys).</summary>
    public int Count => _entries.Count;

    /// <summary>True if the mapping has no entries.</summary>
    public bool IsEmpty => _entries.Count == 0;

    /// <summary>
    /// Looks up a value by string key: the first entry whose key is a
    /// <see cref="YamlString"/> equal to <paramref name="key"/>.
    /// </summary>
    public YamlValue? Get(string key)
    {
        foreach (var entry in _entries)
        {
            if (string.Equals(entry.Key.AsString(), key, StringComparison.Ordinal))
            {
                return entry.Value;
            }
        }

        return null;
    }

    /// <summary>True if <see cref="Get"/> would find the given string key.</summary>
    public bool ContainsKey(string key) => Get(key) is not null;

    /// <summary>
    /// Inserts (or, if a matching string key already exists, replaces in
    /// place) a value, preserving the position of an existing key. Returns
    /// the previous value, if any. Used by typed consumers like
    /// <see cref="OKF4net.Frontmatter.Set"/>; the parser uses
    /// <see cref="PushRaw"/> instead, which never dedups.
    /// </summary>
    public YamlValue? Insert(string key, YamlValue value)
    {
        for (var i = 0; i < _entries.Count; i++)
        {
            if (string.Equals(_entries[i].Key.AsString(), key, StringComparison.Ordinal))
            {
                var previous = _entries[i].Value;
                _entries[i] = (_entries[i].Key, value);
                return previous;
            }
        }

        _entries.Add((new YamlString(key), value));
        return null;
    }

    /// <summary>
    /// Removes the first entry whose key is a string equal to
    /// <paramref name="key"/>, preserving order of the rest.
    /// </summary>
    public YamlValue? Remove(string key)
    {
        for (var i = 0; i < _entries.Count; i++)
        {
            if (string.Equals(_entries[i].Key.AsString(), key, StringComparison.Ordinal))
            {
                var removed = _entries[i].Value;
                _entries.RemoveAt(i);
                return removed;
            }
        }

        return null;
    }

    /// <summary>
    /// Appends a raw key/value pair unconditionally — no dedup, and the key
    /// need not be a string. Used only by the parser, which must preserve
    /// duplicate keys and non-string keys verbatim; hence <c>internal</c>.
    /// </summary>
    internal void PushRaw(YamlValue key, YamlValue value) => _entries.Add((key, value));

    /// <summary>
    /// ALL <c>(key, value)</c> pairs in insertion order, including duplicate
    /// and non-string keys.
    /// This is a genuinely read-only live view over the internal list (a
    /// cached <see cref="ReadOnlyCollection{T}"/> wrapper, created once) — it
    /// cannot be downcast back to the mutable backing <c>List</c> to desync
    /// the mapping's key lookups, unlike returning the list itself typed as
    /// <c>IEnumerable</c>.
    /// </summary>
    public IReadOnlyList<(YamlValue Key, YamlValue Value)> Entries =>
        _entriesView ??= new ReadOnlyCollection<(YamlValue Key, YamlValue Value)>(_entries);

    /// <summary>
    /// Iterates over string keys only, in insertion order, skipping any
    /// non-string keys (duplicates are NOT filtered — a repeated string key
    /// appears once per occurrence).
    /// </summary>
    public IEnumerable<string> Keys => _entries.Select(e => e.Key).OfType<YamlString>().Select(s => s.Value);
}
