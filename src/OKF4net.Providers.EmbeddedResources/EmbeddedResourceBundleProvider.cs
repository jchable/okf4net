// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Reflection;

namespace OKF4net.Providers.EmbeddedResources;

/// <summary>
/// A read-only bundle provider over an assembly's manifest resources (#141, spec of 2026-11-12 §4):
/// the file <c>&lt;rel&gt;</c> of a bundle embedded under <c>Prefix="kb"</c> is the resource
/// <c>okf:kb/&lt;rel&gt;</c>, which the <c>OkfBundle</c> item of this package's <c>.targets</c> produces.
/// <para>The content cannot change while the process runs, so one token derived from the manifest
/// module's MVID versions the bundle and every object. Every operation returns an already-completed
/// task, so the provider serves the library's synchronous entry points. It applies no bound itself:
/// it has no walk, and the core applies <see cref="BundleLimits"/> to what it lists.</para>
/// <para><see cref="Location"/> is for display only (<c>embedded:&lt;assembly&gt;/&lt;prefix&gt;</c>): never
/// a path to open. Supported: run-time assemblies (loaded from a file, a single-file bundle, a Native AOT
/// image or bytes) and resources embedded in their manifest module; a dynamic assembly is refused.</para>
/// </summary>
public sealed class EmbeddedResourceBundleProvider : IBundleProvider
{
    /// <summary>The scheme every bundle resource name starts with (not public: the spec adds no API beyond the constructor and GetBundlePrefixes).</summary>
    private const string ResourceNamePrefix = "okf:";

    private readonly Assembly _assembly;
    private readonly string _resourcePrefix;
    private readonly string[] _names;
    private readonly HashSet<string> _known;
    private readonly BundleObjectVersion _version;

    /// <summary>Serves the bundle embedded in <paramref name="assembly"/> under <paramref name="prefix"/>.</summary>
    /// <param name="assembly">A run-time (not dynamic) assembly.</param>
    /// <param name="prefix">The <c>Prefix</c> of the <c>OkfBundle</c> item: non-empty, no <c>/</c>.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The assembly is dynamic, the prefix is empty or contains <c>/</c>, or no resource is named <c>okf:&lt;prefix&gt;/…</c>.</exception>
    public EmbeddedResourceBundleProvider(Assembly assembly, string prefix)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        ArgumentNullException.ThrowIfNull(prefix);
        RejectDynamic(assembly);
        if (prefix.Length == 0 || prefix.Contains('/', StringComparison.Ordinal))
        {
            throw new ArgumentException("A bundle prefix is non-empty and contains no '/'.", nameof(prefix));
        }

        _assembly = assembly;
        _resourcePrefix = ResourceNamePrefix + prefix + "/";
        _names = assembly.GetManifestResourceNames()
            .Where(n => n.Length > _resourcePrefix.Length && n.StartsWith(_resourcePrefix, StringComparison.Ordinal))
            .Select(n => n[_resourcePrefix.Length..])
            .ToArray();
        Array.Sort(_names, StringComparer.Ordinal);
        var name = assembly.GetName().Name;
        if (_names.Length == 0)
        {
            throw new ArgumentException(
                $"The assembly '{name}' holds no resource named '{_resourcePrefix}…': declare <OkfBundle Include=\"…\" Prefix=\"{prefix}\" /> in the project that builds it, which must hold a direct PackageReference to OKF4net.Providers.EmbeddedResources (its build assets do not flow through a ProjectReference).",
                nameof(prefix));
        }

        _known = new HashSet<string>(_names, StringComparer.Ordinal);
        var mvid = assembly.ManifestModule.ModuleVersionId.ToString("N");
        _version = new BundleObjectVersion("mvid:" + mvid);
        Id = $"embedded:{assembly.FullName}/{mvid}/{prefix}";
        Location = $"embedded:{name}/{prefix}";
    }

    /// <summary>The prefixes of the bundles embedded in <paramref name="assembly"/>, distinct, sorted ordinally. A name <c>okf:</c> with no <c>/</c>, an empty prefix, or nothing after the <c>/</c> is ignored.</summary>
    /// <exception cref="ArgumentException">The assembly is dynamic.</exception>
    public static IReadOnlyList<string> GetBundlePrefixes(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        RejectDynamic(assembly);
        return assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(ResourceNamePrefix, StringComparison.Ordinal))
            .Select(n => n[ResourceNamePrefix.Length..])
            .Select(rest => rest.IndexOf('/', StringComparison.Ordinal) is var slash and > 0 && slash < rest.Length - 1 ? rest[..slash] : null)
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    /// <inheritdoc/>
    public string Id { get; }

    /// <inheritdoc/>
    public string Location { get; }

    /// <inheritdoc/>
    public BundleProviderCapabilities Capabilities { get; } = new(CanWrite: false, CompletesSynchronously: true);

    /// <summary>Only <see cref="BundleKeyConstraints.WellFormedUnicode"/>: names are UTF-8 in the metadata, and lookup is ordinal and case-sensitive, with no normalization.</summary>
    public BundleKeyConstraints NativeConstraints => BundleKeyConstraints.WellFormedUnicode;

    /// <inheritdoc/>
    public ValueTask<BundleVersion> GetVersionAsync(BundleLimits limits, CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new BundleVersion(_version.Token));
        }
        catch (Exception e)
        {
            return ValueTask.FromException<BundleVersion>(e);
        }
    }

    /// <inheritdoc/>
    public IAsyncEnumerable<BundleListing> EnumerateAsync(BundleKey? prefix, BundleLimits limits, CancellationToken ct) =>
        SyncAsyncEnumerable.Create(() => EnumerateCore(prefix), ct);

    private IEnumerable<BundleListing> EnumerateCore(BundleKey? prefix)
    {
        var start = prefix?.ToString();
        foreach (var name in _names)
        {
            if (start is null || name.StartsWith(start + "/", StringComparison.Ordinal))
            {
                yield return new BundleListing(name, null, _version);
            }
        }
    }

    /// <inheritdoc/>
    public ValueTask<BundleObject> ReadAsync(BundleKey key, BundleObjectVersion? expected, CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            var name = key.ToString();
            if (!_known.Contains(name))
            {
                throw new BundleObjectNotFoundException(key);
            }

            if (expected is { } wanted && wanted != _version)
            {
                throw new BundleVersionMismatchException(key);
            }

            var stream = _assembly.GetManifestResourceStream(_resourcePrefix + name)
                ?? throw new BundleObjectNotFoundException(key);
            return ValueTask.FromResult(new BundleObject(stream, _version, stream.CanSeek ? stream.Length : null));
        }
        catch (Exception e)
        {
            return ValueTask.FromException<BundleObject>(e);
        }
    }

    /// <inheritdoc/>
    public ValueTask<bool> ExistsAsync(BundleKey key, CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            return ValueTask.FromResult(_known.Contains(key.ToString()));
        }
        catch (Exception e)
        {
            return ValueTask.FromException<bool>(e);
        }
    }

    /// <inheritdoc/>
    public ValueTask<BundleObjectVersion> WriteAsync(BundleKey key, ReadOnlyMemory<byte> content, BundleWriteCondition condition, CancellationToken ct) =>
        ValueTask.FromException<BundleObjectVersion>(new BundleReadOnlyException(Id));

    /// <inheritdoc/>
    public ValueTask DeleteAsync(BundleKey key, BundleWriteCondition condition, CancellationToken ct) =>
        ValueTask.FromException(new BundleReadOnlyException(Id));

    private static void RejectDynamic(Assembly assembly)
    {
        if (assembly.IsDynamic)
        {
            throw new ArgumentException("A dynamic assembly has no manifest resources to serve.", nameof(assembly));
        }
    }
}
