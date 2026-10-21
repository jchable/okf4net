// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Security.Cryptography;
using System.Text;
using OKF4net.Providers;

namespace OKF4net.Tests.Providers;

/// <summary>
/// A provider over a dictionary, so loader tests can use names the host OS could not
/// create (reserved names, case-colliding siblings) and never touch the disk, and writer
/// tests can script a storage's behaviour. Read-only unless constructed with
/// <c>canWrite: true</c>; writable, it honours the write contract exactly as the filesystem
/// provider does — the same conditions, the same SHA-256 versions, the same exceptions
/// (<see cref="WritableProviderContractTests"/> holds both to it). Native constraints default
/// to <see cref="BundleKeyConstraints.None"/>. <see cref="ObeyLimits"/> is on by default; off,
/// it ignores every bound — the core must still enforce them (spec §6). <see cref="Add"/>
/// stores an object unconditionally, bypassing <see cref="CanWrite"/>, to model a change
/// behind the bundle's back. Every call is recorded in <see cref="Calls"/>. Thread-safe.
/// </summary>
public sealed class InMemoryBundleProvider : IBundleProvider
{
    private readonly Lock _gate = new();
    private readonly SortedDictionary<string, byte[]> _objects = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Exception> _failures = new(StringComparer.Ordinal);
    private readonly HashSet<string> _listedTwice = new(StringComparer.Ordinal);
    private readonly HashSet<string> _existsRefused = new(StringComparer.Ordinal);
    private readonly Dictionary<int, Exception> _failingWrites = [];
    private readonly List<string> _calls = [];
    private int _writeCalls;
    private bool _conflictOnNextWrite;
    private Action<InMemoryBundleProvider>? _beforeNextWrite;

    public InMemoryBundleProvider(BundleKeyConstraints native = BundleKeyConstraints.None, string id = "memory", bool canWrite = false)
    {
        NativeConstraints = native;
        Id = id;
        CanWrite = canWrite;
    }

    public bool ObeyLimits { get; init; } = true;

    /// <summary>Whether <see cref="Write"/> and <see cref="Delete"/> are allowed (the constructor's <c>canWrite</c>).</summary>
    public bool CanWrite { get; }

    /// <summary>Every call so far, in order: <c>version</c>, <c>enumerate {prefix}</c>, <c>read {key}</c>, <c>exists {key}</c>, <c>write {key} {condition kind}</c>, <c>delete {key} {condition kind}</c> (refused calls included).</summary>
    public IReadOnlyList<string> Calls
    {
        get
        {
            lock (_gate)
            {
                return [.. _calls];
            }
        }
    }

    /// <summary>Forgets the calls recorded so far.</summary>
    public void ClearCalls()
    {
        lock (_gate)
        {
            _calls.Clear();
        }
    }

    /// <summary>Makes <see cref="Enumerate"/> list <paramref name="rawName"/> twice — a buggy backend (Review Focus 5).</summary>
    public InMemoryBundleProvider ListTwice(string rawName)
    {
        lock (_gate)
        {
            _listedTwice.Add(rawName);
        }

        return this;
    }

    /// <summary>Thrown by every <see cref="Read"/> when set.</summary>
    public Exception? FailReadsWith { get; set; }

    /// <summary>Thrown by the first step of every <see cref="Enumerate"/> when set (an unlistable storage).</summary>
    public Exception? FailEnumerateWith { get; set; }

    /// <summary>Thrown by <see cref="GetVersion"/> when set.</summary>
    public Exception? FailVersionWith { get; set; }

    /// <summary>Stores an object unconditionally, whatever <see cref="CanWrite"/> says (a change made behind the bundle's back); not recorded in <see cref="Calls"/>.</summary>
    public InMemoryBundleProvider Add(string rawName, string text)
    {
        lock (_gate)
        {
            _objects[rawName] = Encoding.UTF8.GetBytes(text);
        }

        return this;
    }

    /// <summary>Makes <see cref="Read"/> of one object throw <paramref name="failure"/> instead.</summary>
    public InMemoryBundleProvider FailReadOf(string rawName, Exception failure)
    {
        lock (_gate)
        {
            _failures[rawName] = failure;
        }

        return this;
    }

    /// <summary>Makes the next <see cref="Write"/> or <see cref="Delete"/> that reaches the storage throw <see cref="BundleWriteConflictException"/> before checking its condition, storing nothing — once.</summary>
    public InMemoryBundleProvider ConflictOnNextWrite()
    {
        lock (_gate)
        {
            _conflictOnNextWrite = true;
        }

        return this;
    }

    /// <summary>Runs <paramref name="change"/> inside the next <see cref="Write"/> or <see cref="Delete"/> that reaches the storage, just before its condition is checked — another writer landing first. Once.</summary>
    public InMemoryBundleProvider BeforeNextWrite(Action<InMemoryBundleProvider> change)
    {
        lock (_gate)
        {
            _beforeNextWrite = change;
        }

        return this;
    }

    /// <summary>Makes the <paramref name="nth"/> (from 1) <see cref="Write"/> or <see cref="Delete"/> call that reaches the storage throw <paramref name="failure"/>, storing nothing.</summary>
    public InMemoryBundleProvider FailWrite(int nth, Exception failure)
    {
        lock (_gate)
        {
            _failingWrites[nth] = failure;
        }

        return this;
    }

    /// <summary>Makes <see cref="Exists"/> of one key throw <see cref="BundleAccessRefusedException"/>, like a provider refusing a link or a path outside its boundary.</summary>
    public InMemoryBundleProvider RefuseExistsOf(string rawName)
    {
        lock (_gate)
        {
            _existsRefused.Add(rawName);
        }

        return this;
    }

    public string Id { get; }
    public string Location => "memory://" + Id;
    public BundleProviderCapabilities Capabilities => new(CanWrite);
    public BundleKeyConstraints NativeConstraints { get; }

    public BundleVersion GetVersion(BundleLimits limits)
    {
        Record("version");
        if (FailVersionWith is { } failure)
        {
            throw failure;
        }

        var sb = new StringBuilder();
        lock (_gate)
        {
            foreach (var (name, bytes) in _objects)
            {
                sb.Append(name).Append('\0').Append(Convert.ToHexStringLower(SHA256.HashData(bytes))).Append('\n');
            }
        }

        return new BundleVersion(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()))));
    }

    public IEnumerable<BundleListing> Enumerate(BundleKey? prefix, BundleLimits limits)
    {
        Record($"enumerate {prefix}");
        return EnumerateCore(prefix, limits);
    }

    private IEnumerable<BundleListing> EnumerateCore(BundleKey? prefix, BundleLimits limits)
    {
        if (FailEnumerateWith is { } failure)
        {
            throw failure;
        }

        KeyValuePair<string, byte[]>[] snapshot;
        string[] twice;
        lock (_gate)
        {
            snapshot = [.. _objects];
            twice = [.. _listedTwice];
        }

        var count = 0;
        foreach (var (name, bytes) in snapshot)
        {
            if (prefix is not null && !(name == prefix.ToString() || name.StartsWith(prefix + "/", StringComparison.Ordinal)))
            {
                continue;
            }

            if (ObeyLimits && ++count > limits.MaxEntries)
            {
                throw new BundleLimitExceededException(BundleBound.Entries, limits.MaxEntries);
            }

            yield return new BundleListing(name, bytes.Length, null);
            if (twice.Contains(name, StringComparer.Ordinal))
            {
                yield return new BundleListing(name, bytes.Length, null);
            }
        }
    }

    public BundleObject Read(BundleKey key, BundleObjectVersion? expected = null)
    {
        Record($"read {key}");
        if (FailReadsWith is { } failure)
        {
            throw failure;
        }

        byte[]? bytes;
        lock (_gate)
        {
            if (_failures.TryGetValue(key.ToString(), out var specific))
            {
                throw specific;
            }

            _objects.TryGetValue(key.ToString(), out bytes);
        }

        if (bytes is null)
        {
            throw new BundleObjectNotFoundException(key);
        }

        var version = VersionOf(bytes);
        if (expected is { } e && e != version)
        {
            throw new BundleVersionMismatchException(key);
        }

        return new BundleObject(new MemoryStream(bytes, writable: false), version, bytes.Length);
    }

    public bool Exists(BundleKey key)
    {
        Record($"exists {key}");
        lock (_gate)
        {
            return _existsRefused.Contains(key.ToString())
                ? throw new BundleAccessRefusedException(key, "secret-token=123 refused by the storage")
                : _objects.ContainsKey(key.ToString());
        }
    }

    public BundleObjectVersion Write(BundleKey key, ReadOnlyMemory<byte> content, BundleWriteCondition condition)
    {
        ArgumentNullException.ThrowIfNull(key);
        Record($"write {key} {condition.Kind}");
        if (!CanWrite)
        {
            throw new BundleReadOnlyException(Id);
        }

        lock (_gate)
        {
            BeginMutation(key);
            _objects.TryGetValue(key.ToString(), out var current);
            Check(key, current, condition);
            var bytes = content.ToArray();
            _objects[key.ToString()] = bytes;
            return VersionOf(bytes);
        }
    }

    public void Delete(BundleKey key, BundleWriteCondition condition)
    {
        ArgumentNullException.ThrowIfNull(key);
        Record($"delete {key} {condition.Kind}");
        if (!CanWrite)
        {
            throw new BundleReadOnlyException(Id);
        }

        lock (_gate)
        {
            BeginMutation(key);
            _objects.TryGetValue(key.ToString(), out var current);
            Check(key, current, condition);
            _objects.Remove(key.ToString());
        }
    }

    private static BundleObjectVersion VersionOf(byte[] bytes) => new(Convert.ToHexStringLower(SHA256.HashData(bytes)));

    /// <summary>The scripted hooks, in order: the Nth-call failure, the one-shot conflict, the one-shot change. Caller holds the gate.</summary>
    private void BeginMutation(BundleKey key)
    {
        _writeCalls++;
        if (_failingWrites.Remove(_writeCalls, out var failure))
        {
            throw failure;
        }

        if (_conflictOnNextWrite)
        {
            _conflictOnNextWrite = false;
            throw new BundleWriteConflictException(key);
        }

        if (_beforeNextWrite is { } change)
        {
            _beforeNextWrite = null;
            change(this); // re-enters the gate: Lock is reentrant
        }
    }

    /// <summary>The condition rules of <see cref="IBundleProvider.Write"/> and <see cref="IBundleProvider.Delete"/>, on the current bytes (or none).</summary>
    private static void Check(BundleKey key, byte[]? current, BundleWriteCondition condition)
    {
        var holds = condition.Kind switch
        {
            BundleWriteConditionKind.None => true,
            BundleWriteConditionKind.MustNotExist => current is null,
            BundleWriteConditionKind.IfMatch => current is not null && condition.Expected is { } expected && VersionOf(current) == expected,
            _ => throw new ArgumentOutOfRangeException(nameof(condition), condition.Kind, "unknown condition"),
        };
        if (!holds)
        {
            throw new BundleWriteConflictException(key);
        }
    }

    private void Record(string call)
    {
        lock (_gate)
        {
            _calls.Add(call);
        }
    }
}
