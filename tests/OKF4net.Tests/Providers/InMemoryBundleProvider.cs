// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Security.Cryptography;
using System.Text;
using OKF4net.Providers;

namespace OKF4net.Tests.Providers;

/// <summary>
/// A read-only provider over a dictionary, so loader tests can use names the host
/// OS could not create (reserved names, case-colliding siblings) and never touch the
/// disk. Native constraints default to <see cref="BundleKeyConstraints.None"/>.
/// <see cref="ObeyLimits"/> is on by default; off, it ignores every bound — the core
/// must still enforce them (spec §6). Objects can be added after a load to model a
/// change behind the bundle's back.
/// </summary>
public sealed class InMemoryBundleProvider : IBundleProvider
{
    private readonly SortedDictionary<string, byte[]> _objects = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Exception> _failures = new(StringComparer.Ordinal);

    public InMemoryBundleProvider(BundleKeyConstraints native = BundleKeyConstraints.None, string id = "memory")
    {
        NativeConstraints = native;
        Id = id;
    }

    public bool ObeyLimits { get; init; } = true;

    private readonly HashSet<string> _listedTwice = new(StringComparer.Ordinal);

    /// <summary>Makes <see cref="Enumerate"/> list <paramref name="rawName"/> twice — a buggy backend (Review Focus 5).</summary>
    public InMemoryBundleProvider ListTwice(string rawName)
    {
        _listedTwice.Add(rawName);
        return this;
    }

    /// <summary>Thrown by every <see cref="Read"/> when set.</summary>
    public Exception? FailReadsWith { get; set; }

    /// <summary>Thrown by the first step of every <see cref="Enumerate"/> when set (an unlistable storage).</summary>
    public Exception? FailEnumerateWith { get; set; }

    /// <summary>Thrown by <see cref="GetVersion"/> when set.</summary>
    public Exception? FailVersionWith { get; set; }

    public InMemoryBundleProvider Add(string rawName, string text)
    {
        _objects[rawName] = Encoding.UTF8.GetBytes(text);
        return this;
    }

    /// <summary>Makes <see cref="Read"/> of one object throw <paramref name="failure"/> instead.</summary>
    public InMemoryBundleProvider FailReadOf(string rawName, Exception failure)
    {
        _failures[rawName] = failure;
        return this;
    }

    public string Id { get; }
    public string Location => "memory://" + Id;
    public BundleProviderCapabilities Capabilities => new(CanWrite: false);
    public BundleKeyConstraints NativeConstraints { get; }

    public BundleVersion GetVersion(BundleLimits limits)
    {
        if (FailVersionWith is { } failure)
        {
            throw failure;
        }

        var sb = new StringBuilder();
        foreach (var (name, bytes) in _objects)
        {
            sb.Append(name).Append('\0').Append(Convert.ToHexStringLower(SHA256.HashData(bytes))).Append('\n');
        }

        return new BundleVersion(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()))));
    }

    public IEnumerable<BundleListing> Enumerate(BundleKey? prefix, BundleLimits limits)
    {
        if (FailEnumerateWith is { } failure)
        {
            throw failure;
        }

        var count = 0;
        foreach (var (name, bytes) in _objects)
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
            if (_listedTwice.Contains(name))
            {
                yield return new BundleListing(name, bytes.Length, null);
            }
        }
    }

    public BundleObject Read(BundleKey key, BundleObjectVersion? expected = null)
    {
        if (FailReadsWith is { } failure)
        {
            throw failure;
        }

        if (_failures.TryGetValue(key.ToString(), out var specific))
        {
            throw specific;
        }

        if (!_objects.TryGetValue(key.ToString(), out var bytes))
        {
            throw new BundleObjectNotFoundException(key);
        }

        var version = new BundleObjectVersion(Convert.ToHexStringLower(SHA256.HashData(bytes)));
        if (expected is { } e && e != version)
        {
            throw new BundleVersionMismatchException(key);
        }

        return new BundleObject(new MemoryStream(bytes, writable: false), version, bytes.Length);
    }

    public bool Exists(BundleKey key) => _objects.ContainsKey(key.ToString());

    public BundleObjectVersion Write(BundleKey key, ReadOnlyMemory<byte> content, BundleWriteCondition condition) => throw new BundleReadOnlyException(Id);

    public void Delete(BundleKey key, BundleWriteCondition condition) => throw new BundleReadOnlyException(Id);
}
