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
    private volatile Dictionary<string, int>? _index; // lazily built; see IndexOf

    /// <summary>
    /// Test seam (#198): units of key-lookup work done so far, one per entry
    /// examined by a search plus one per entry visited by an index build.
    /// Counted, never timed.
    /// </summary>
    internal long KeyProbes { get; private set; }

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
        => IndexOf(key) is var i and >= 0 ? _entries[i].Value : null;

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
        var i = IndexOf(key);
        if (i >= 0)
        {
            var previous = _entries[i].Value;
            _entries[i] = (_entries[i].Key, value);
            return previous;
        }

        _entries.Add((new YamlString(key), value));
        _index?.TryAdd(key, _entries.Count - 1);
        return null;
    }

    /// <summary>
    /// Removes the first entry whose key is a string equal to
    /// <paramref name="key"/>, preserving order of the rest.
    /// </summary>
    public YamlValue? Remove(string key)
    {
        var i = IndexOf(key);
        if (i < 0)
        {
            return null;
        }

        var removed = _entries[i].Value;
        _entries.RemoveAt(i);

        // Every later position shifted, and a later duplicate of the key may
        // now be its first occurrence: drop the index, it is rebuilt on demand.
        _index = null;
        return removed;
    }

    /// <summary>
    /// Position of the first entry whose key is a <see cref="YamlString"/>
    /// equal (ordinal) to <paramref name="key"/>, or -1. Served from a
    /// key → first-position dictionary built on the first lookup, so a mapping
    /// that is only parsed and emitted never pays for it. Not thread-safe
    /// against mutation (like the rest of the type); concurrent readers of a
    /// mapping nobody mutates are safe: each builds the same dictionary
    /// privately and publishes it through the <c>volatile</c> field, a release
    /// store that a reader's acquire load sees fully built, so the worst case
    /// is a duplicated build.
    /// </summary>
    private int IndexOf(string key)
    {
        var index = _index;
        if (index is null)
        {
            index = new Dictionary<string, int>(_entries.Count, StringComparer.Ordinal);
            for (var i = 0; i < _entries.Count; i++)
            {
                KeyProbes++;
                if (_entries[i].Key is YamlString s)
                {
                    index.TryAdd(s.Value, i);
                }
            }

            _index = index;
        }

        KeyProbes++;
        return index.TryGetValue(key, out var found) ? found : -1;
    }

    /// <summary>
    /// Appends a raw key/value pair unconditionally — no dedup, and the key
    /// need not be a string. Used only by the parser, which must preserve
    /// duplicate keys and non-string keys verbatim; hence <c>internal</c>.
    /// </summary>
    internal void PushRaw(YamlValue key, YamlValue value)
    {
        _entries.Add((key, value));

        // Append-only: never builds the index, only keeps an existing one
        // current (a duplicate leaves the first position in place).
        if (_index is not null && key is YamlString s)
        {
            _index.TryAdd(s.Value, _entries.Count - 1);
        }
    }

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
