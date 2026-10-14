// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Security.Cryptography;
using System.Text;
using OKF4net.Internal;

namespace OKF4net.Providers;

/// <summary>
/// The default provider (spec §8): a bundle as a directory tree. Read-only in this
/// lot (<see cref="Capabilities"/> says so; lot B adds the atomic write).
/// <see cref="Enumerate"/> is eager — the walk completes inside the call — so no
/// deferred storage access exists; every directory passes the STRICT guard before it
/// is listed, a reparse-point entry is skipped (never followed), every inspected entry
/// is counted before the skip and before sorting, and with a confinement boundary the
/// chain boundary→root is re-checked before every storage access. Versions are SHA-256
/// of content; the bundle token is a digest of the listing (name, length, mtime),
/// deliberately weak and never used for pinning.
/// </summary>
public sealed class FileSystemBundleProvider : IBundleProvider
{
    private static readonly StringComparison PathComparison = StringComparison.Ordinal;

    private readonly string _fullRoot;
    private readonly BundleLimits _limits;

    /// <summary>
    /// Creates a provider over <paramref name="root"/>. <paramref name="confinementBoundary"/>
    /// (a catalog root) makes the root itself subject to the STRICT guards on every access;
    /// without it the root is trusted host configuration, as today.
    /// <paramref name="additionalConstraints"/> is OR-ed into the OS's native set and can only
    /// add rules (a folding flag also turns exact-case matching on, for a case-insensitive
    /// mount on Linux); <paramref name="limits"/> bounds <see cref="Read"/>, which the
    /// interface gives no per-call limits.
    /// </summary>
    public FileSystemBundleProvider(string root, string? confinementBoundary = null, BundleKeyConstraints additionalConstraints = BundleKeyConstraints.None, BundleLimits? limits = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(root);
        Root = root;
        _fullRoot = ReparsePoints.CanonicalizeRoot(root);
        ConfinementBoundary = confinementBoundary is null ? null : ReparsePoints.CanonicalizeRoot(confinementBoundary);
        NativeConstraints = BundleKeyConstraintSets.ForCurrentOperatingSystem() | additionalConstraints;
        _limits = limits ?? BundleLimits.Default;
        MatchesCaseExactly = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            || NativeConstraints.HasFlag(BundleKeyConstraints.CaseInsensitiveCollisions)
            || NativeConstraints.HasFlag(BundleKeyConstraints.UnicodeFormCollisions);
    }

    /// <summary>The root as given (also <see cref="Location"/>).</summary>
    public string Root { get; }

    /// <summary>The canonical boundary every access is re-checked against, or <see langword="null"/>.</summary>
    public string? ConfinementBoundary { get; }

    /// <summary>Whether keys are matched segment by segment against the real entry names (spec §2's correspondence rule). On by the OS or by a folding flag; never off by a caller.</summary>
    public bool MatchesCaseExactly { get; }

    /// <inheritdoc/>
    public string Id => _fullRoot;

    /// <inheritdoc/>
    public string Location => Root;

    /// <inheritdoc/>
    public BundleProviderCapabilities Capabilities => new(CanWrite: false);

    /// <inheritdoc/>
    public BundleKeyConstraints NativeConstraints { get; }

    /// <inheritdoc/>
    public BundleVersion GetVersion(BundleLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);
        CheckBoundary();
        var sb = new StringBuilder();
        var counter = new Counter();
        Walk(_fullRoot, string.Empty, 0, limits, counter, entry =>
        {
            if (entry.IsDirectory)
            {
                return;
            }

            long length;
            long mtime;
            try
            {
                var info = new FileInfo(entry.FullPath);
                length = info.Length;
                mtime = info.LastWriteTimeUtc.Ticks;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                length = -1;
                mtime = -1;
            }

            sb.Append(entry.RawName).Append('\0').Append(length).Append('\0').Append(mtime).Append('\n');
        });

        return new BundleVersion(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()))));
    }

    /// <inheritdoc/>
    public IEnumerable<BundleListing> Enumerate(BundleKey? prefix, BundleLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);
        CheckBoundary();
        var start = _fullRoot;
        if (prefix is not null)
        {
            var resolved = ResolveExact(prefix);
            if (resolved is null)
            {
                return Array.Empty<BundleListing>();
            }

            GuardStrict(prefix, resolved); // a junction prefix is refused, not walked
            if (!Directory.Exists(resolved))
            {
                return Array.Empty<BundleListing>();
            }

            start = resolved;
        }

        var result = new List<BundleListing>();
        Walk(start, string.Empty, prefix?.Segments.Count ?? 0, limits, new Counter(), entry =>
        {
            if (entry.IsDirectory)
            {
                return;
            }

            long? length;
            try
            {
                length = new FileInfo(entry.FullPath).Length;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                length = null;
            }

            var raw = prefix is null ? entry.RawName : prefix + "/" + entry.RawName;
            result.Add(new BundleListing(raw, length, null));
        });

        return result;
    }

    /// <inheritdoc/>
    public BundleObject Read(BundleKey key, BundleObjectVersion? expected = null)
    {
        ArgumentNullException.ThrowIfNull(key);
        CheckBoundary();
        var path = ResolveExact(key) ?? throw new BundleObjectNotFoundException(key);
        GuardStrict(key, path);
        if (!File.Exists(path))
        {
            throw new BundleObjectNotFoundException(key);
        }

        byte[] bytes;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (fs.Length > _limits.MaxObjectBytes)
            {
                throw new BundleObjectTooLargeException(key, fs.Length, _limits.MaxObjectBytes);
            }

            bytes = ReadBounded(fs, _limits.MaxObjectBytes, key);
        }
        catch (FileNotFoundException)
        {
            throw new BundleObjectNotFoundException(key);
        }
        catch (DirectoryNotFoundException)
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

    /// <inheritdoc/>
    public bool Exists(BundleKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        CheckBoundary();
        string? path;
        try
        {
            path = ResolveExact(key);
        }
        catch (BundleAccessRefusedException)
        {
            return false; // a question, not an access: a refused traversal reads as "no such object"
        }

        if (path is null || !File.Exists(path))
        {
            return false;
        }

        return !ReparsePoints.IsReparsePointOrUninspectable(path)
            && !ReparsePoints.HasReparsePointOrUninspectableAncestor(_fullRoot, path, PathComparison);
    }

    /// <inheritdoc/>
    public BundleObjectVersion Write(BundleKey key, ReadOnlyMemory<byte> content, BundleWriteCondition condition) =>
        throw new BundleReadOnlyException(Id);

    /// <inheritdoc/>
    public void Delete(BundleKey key, BundleWriteCondition condition) =>
        throw new BundleReadOnlyException(Id);

    /// <summary>
    /// With a boundary: the root must be under it, and neither the root nor any entry
    /// between the boundary and the root may be a reparse point or uninspectable — STRICT,
    /// at every storage access, because the ancestor walk stops <i>before</i> the root.
    /// </summary>
    private void CheckBoundary()
    {
        if (ConfinementBoundary is null)
        {
            return;
        }

        if (!ReparsePoints.IsWithin(ConfinementBoundary, _fullRoot, PathComparison))
        {
            throw new BundleAccessRefusedException(null, "the bundle root is outside its confinement boundary");
        }

        if (ReparsePoints.IsReparsePointOrUninspectable(_fullRoot)
            || ReparsePoints.HasReparsePointOrUninspectableAncestor(ConfinementBoundary, _fullRoot, PathComparison))
        {
            throw new BundleAccessRefusedException(null, "the bundle root, or a directory between the boundary and it, is a reparse point or cannot be inspected");
        }
    }

    /// <summary>STRICT: the path must be under the root and neither it nor any directory between the root and it may be a reparse point or uninspectable.</summary>
    private void GuardStrict(BundleKey? key, string path)
    {
        if (!ReparsePoints.IsWithin(_fullRoot, path, PathComparison)
            || (!string.Equals(path, _fullRoot, PathComparison) && ReparsePoints.IsReparsePointOrUninspectable(path))
            || ReparsePoints.HasReparsePointOrUninspectableAncestor(_fullRoot, path, PathComparison))
        {
            throw new BundleAccessRefusedException(key, "it resolves outside the bundle root, or through a reparse point or an entry that could not be inspected");
        }
    }

    /// <summary>
    /// Maps a key to its on-disk path. With <see cref="MatchesCaseExactly"/>, each segment must
    /// match an existing entry's name <b>exactly</b> (ordinal) — spec §2's correspondence rule —
    /// else <see langword="null"/>. Otherwise the OS compares bytes already and the joined path
    /// is returned as is. The segment is handed to the OS as a literal-name search pattern, so
    /// the OS filters by name rather than the code listing each directory in full. A segment that
    /// cannot be a literal pattern is "no such entry": on Windows one containing any of
    /// <c>&lt; &gt; : " | ? *</c>, or that is rooted (<c>C:</c>, <c>a:b.md</c>), because no NTFS or
    /// FAT name can carry those and .NET rejects such a pattern with an
    /// <see cref="ArgumentException"/> (a layer-1-valid key must not make a question throw). On
    /// every OS a segment containing <c>*</c> or <c>?</c> is looked up by listing the directory
    /// and comparing names ordinally, since the pattern would otherwise match as a wildcard.
    /// Each traversed directory is still STRICT-guarded before it is listed, ahead of this rule.
    /// </summary>
    private string? ResolveExact(BundleKey key)
    {
        if (!MatchesCaseExactly)
        {
            var parts = new string[key.Segments.Count + 1];
            parts[0] = _fullRoot;
            for (var i = 0; i < key.Segments.Count; i++)
            {
                parts[i + 1] = key.Segments[i];
            }

            return Path.Combine(parts);
        }

        var current = _fullRoot;
        foreach (var segment in key.Segments)
        {
            // STRICT before listing a traversed directory (the root excepted: a symlinked root
            // without a boundary stays loadable, Review Focus 4). Otherwise an absent name under
            // a junction could be told apart from a present one before the read gate ever ran
            // (fourth review: NotFound vs refused).
            if (!string.Equals(current, _fullRoot, PathComparison)
                && (ReparsePoints.IsReparsePointOrUninspectable(current)
                    || ReparsePoints.HasReparsePointOrUninspectableAncestor(_fullRoot, current, PathComparison)))
            {
                throw new BundleAccessRefusedException(key, "it resolves through a reparse point or an entry that could not be inspected");
            }

            if (CannotBeLiteralName(segment))
            {
                return null;
            }

            // A wildcard character in the segment would be matched, not read literally: list everything instead.
            var pattern = segment.AsSpan().ContainsAny('*', '?') ? "*" : segment;
            string? match = null;
            try
            {
                foreach (var entry in new DirectoryInfo(current).EnumerateFileSystemInfos(pattern, SearchOption.TopDirectoryOnly))
                {
                    if (string.Equals(entry.Name, segment, StringComparison.Ordinal))
                    {
                        match = entry.FullName;
                        break;
                    }
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return null;
            }

            if (match is null)
            {
                return null;
            }

            current = match;
        }

        return current;
    }

    /// <summary>Windows only: a segment no NTFS/FAT entry can be named, and that .NET would reject as a search pattern.</summary>
    private static bool CannotBeLiteralName(string segment) =>
        OperatingSystem.IsWindows()
        && (segment.AsSpan().ContainsAny("<>:\"|?*") || Path.IsPathRooted(segment));

    private sealed class Counter
    {
        public int Entries;
    }

    private readonly record struct WalkEntry(string FullPath, string RawName, bool IsDirectory);

    /// <summary>
    /// Depth-first, ordinal per directory, recursive (depth bounded by
    /// <see cref="BundleLimits.MaxDepth"/>), <b>not</b> an iterator: it runs to completion and
    /// hands each entry to <paramref name="visit"/>. Before listing any directory it re-checks
    /// the boundary and the directory itself STRICTly (a directory replaced by a junction
    /// since the parent was listed is refused, not entered). Every entry a listing returns is
    /// counted before the reparse-point skip and before sorting.
    /// </summary>
    private void Walk(string dir, string rel, int depth, BundleLimits limits, Counter counter, Action<WalkEntry> visit)
    {
        CheckBoundary();
        // The path alone: a directory whose relative name is no valid key (U+0085, > 1 024
        // bytes) is still a directory to guard; its entries are rejected BY NAME later, by the
        // loader, never by an exception here (the third review hit BundleKeyException).
        GuardStrict(null, dir);
        var names = new List<string>();
        foreach (var full in Directory.EnumerateFileSystemEntries(dir))
        {
            if (++counter.Entries > limits.MaxEntries)
            {
                throw new BundleLimitExceededException(BundleBound.Entries, limits.MaxEntries);
            }

            names.Add(Path.GetFileName(full));
        }

        names.Sort(StringComparer.Ordinal);
        foreach (var name in names)
        {
            var full = Path.Combine(dir, name);
            if (ReparsePoints.IsReparsePoint(full))
            {
                continue; // lenient: a walk skips, the read gate refuses
            }

            if (depth + 1 > limits.MaxDepth)
            {
                throw new BundleLimitExceededException(BundleBound.Depth, limits.MaxDepth);
            }

            var raw = rel.Length == 0 ? name : rel + "/" + name;
            if (Directory.Exists(full))
            {
                visit(new WalkEntry(full, raw, true));
                Walk(full, raw, depth + 1, limits, counter, visit);
            }
            else if (File.Exists(full))
            {
                visit(new WalkEntry(full, raw, false));
            }
        }
    }

    private static byte[] ReadBounded(Stream stream, long max, BundleKey key)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        long total = 0;
        int read;
        while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
        {
            total += read;
            if (total > max)
            {
                throw new BundleObjectTooLargeException(key, total, max);
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }
}
