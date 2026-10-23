// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net.Providers;

namespace OKF4net.Agents.Internal;

/// <summary>
/// What one session was shown, and to what end (spec §5.4, D1): per key, the version shown
/// as a <b>full concept</b> (<c>okf_read_concept</c>), the version shown as <b>code</b>
/// (<c>okf_get_computation</c>, for the concept and for its computation file), or that the
/// key was shown <b>absent</b> (<c>okf_read_concept</c> answered not found). A full-concept
/// observation authorises a rewrite and the run of an inline computation; a code observation
/// the run only; an absent one a creation. A new observation of the same purpose replaces the
/// previous one; an absent observation clears both others and either of them clears absent.
/// <para><b>Bounded</b> to <see cref="Capacity"/> keys, the least recently used evicted first
/// (a record, or a lookup that finds what it asks for, makes a key the most recent; a lookup
/// that finds nothing of that purpose touches nothing); an evicted entry is gone, and the
/// action it would have authorised is refused until a new read. The registry is per tool
/// set: it is not shared through the handle, nor with another tool set, nor with a context
/// provider, and it survives every refresh of the image. Thread-safe.</para>
/// </summary>
internal sealed class ObservationRegistry
{
    /// <summary>The bound of spec §5.4: 10 000 keys.</summary>
    public const int DefaultCapacity = 10_000;

    private readonly Lock _gate = new();
    private readonly Dictionary<BundleKey, LinkedListNode<Entry>> _entries = [];
    private readonly LinkedList<Entry> _order = new(); // most recently used first

    /// <summary>Creates a registry bounded to <paramref name="capacity"/> keys.</summary>
    public ObservationRegistry(int capacity = DefaultCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        Capacity = capacity;
    }

    /// <summary>The bound on the number of keys.</summary>
    public int Capacity { get; }

    /// <summary>The number of keys observed and not evicted.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    /// <summary>Records that the whole concept under <paramref name="key"/> was shown at <paramref name="version"/>.</summary>
    public void RecordFullConcept(BundleKey key, BundleObjectVersion version)
    {
        lock (_gate)
        {
            var entry = Touch(key);
            entry.FullConcept = version;
            entry.Absent = false;
        }
    }

    /// <summary>Records that the code under <paramref name="key"/> (a concept's contract and code, or a computation file) was shown at <paramref name="version"/>.</summary>
    public void RecordCode(BundleKey key, BundleObjectVersion version)
    {
        lock (_gate)
        {
            var entry = Touch(key);
            entry.Code = version;
            entry.Absent = false;
        }
    }

    /// <summary>Records that <paramref name="key"/> was shown absent.</summary>
    public void RecordAbsent(BundleKey key)
    {
        lock (_gate)
        {
            var entry = Touch(key);
            entry.Absent = true;
            entry.FullConcept = null;
            entry.Code = null;
        }
    }

    /// <summary>Drops every observation of <paramref name="key"/>: what the session saw no longer exists under that version.</summary>
    public void Forget(BundleKey key)
    {
        lock (_gate)
        {
            if (_entries.Remove(key, out var node))
            {
                _order.Remove(node);
            }
        }
    }

    /// <summary>The version the whole concept was shown at, if it was.</summary>
    public bool TryGetFullConcept(BundleKey key, out BundleObjectVersion version) => TryGet(key, static e => e.FullConcept, out version);

    /// <summary>The version the code was shown at, if it was.</summary>
    public bool TryGetCode(BundleKey key, out BundleObjectVersion version) => TryGet(key, static e => e.Code, out version);

    /// <summary>Whether the key was shown absent (and not shown since).</summary>
    public bool IsAbsent(BundleKey key)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out var node) || !node.Value.Absent)
            {
                return false;
            }

            MoveToFront(node);
            return true;
        }
    }

    private bool TryGet(BundleKey key, Func<Entry, BundleObjectVersion?> read, out BundleObjectVersion version)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var node) && read(node.Value) is { } observed)
            {
                MoveToFront(node);
                version = observed;
                return true;
            }

            version = default;
            return false;
        }
    }

    /// <summary>The entry of <paramref name="key"/>, made the most recent — created if needed, evicting the least recent past the capacity. Under the lock.</summary>
    private Entry Touch(BundleKey key)
    {
        if (_entries.TryGetValue(key, out var node))
        {
            MoveToFront(node);
            return node.Value;
        }

        if (_entries.Count >= Capacity)
        {
            var oldest = _order.Last!;
            _order.RemoveLast();
            _entries.Remove(oldest.Value.Key);
        }

        var entry = new Entry(key);
        node = _order.AddFirst(entry);
        _entries[key] = node;
        return entry;
    }

    private void MoveToFront(LinkedListNode<Entry> node)
    {
        if (!ReferenceEquals(_order.First, node))
        {
            _order.Remove(node);
            _order.AddFirst(node);
        }
    }

    private sealed class Entry(BundleKey key)
    {
        public BundleKey Key { get; } = key;

        public BundleObjectVersion? FullConcept { get; set; }

        public BundleObjectVersion? Code { get; set; }

        public bool Absent { get; set; }
    }
}
