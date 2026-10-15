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
            var resolved = ResolveExact(prefix, out var ancestorsGuarded);
            if (resolved is null)
            {
                return Array.Empty<BundleListing>();
            }

            GuardResolved(prefix, resolved, ancestorsGuarded); // a junction prefix is refused, not walked
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
        var path = ResolveExact(key, out var ancestorsGuarded) ?? throw new BundleObjectNotFoundException(key);
        GuardResolved(key, path, ancestorsGuarded);
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
    /// <remarks>
    /// Refuses, like <see cref="Read"/>, rather than answering <see langword="false"/>, when the
    /// key or its path is refused: outside the confinement boundary, through a reparse point, or
    /// through an entry that cannot be inspected. A directory on the path whose listing this
    /// provider is denied answers <see langword="false"/>, as an absent entry does: no object is
    /// read and nothing outside the root is reached.
    /// </remarks>
    /// <exception cref="BundleAccessRefusedException">The key or its path is refused.</exception>
    public bool Exists(BundleKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        CheckBoundary();
        // Refuses exactly as Read does (confinement boundary, reparse point, uninspectable
        // entry): a link is not "absent", and the caller (Bundle.TryResolveResourceKey) reports
        // the refusal as Unsafe. No present/absent leak: every traversed directory is STRICT
        // guarded before it is listed, so under a junction both a present and an absent name
        // are refused. A genuinely absent entry, a name that cannot be a literal name and a
        // denied parent listing are "no such entry", not refusals.
        var path = ResolveExact(key, out var ancestorsGuarded);
        if (path is null)
        {
            return false;
        }

        GuardResolved(key, path, ancestorsGuarded);
        return File.Exists(path);
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
    /// at every storage access. Every per-key walk (<see cref="GuardStrict"/>,
    /// <see cref="ResolveExact"/>) has the bundle root as its own upper bound, which it never
    /// inspects, so the chain boundary→root lies outside all of them and is checked here.
    /// <c>AnyEntryUpTo</c> inspects its starting path itself before walking up, so when the
    /// root is strictly below the boundary the one ancestor walk covers the root too; when
    /// the root <i>is</i> the boundary that walk inspects nothing, and the root is checked on
    /// its own.
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

        // AnyEntryUpTo inspects _fullRoot itself first unless it equals the boundary, where it stops at once.
        if (string.Equals(_fullRoot, ConfinementBoundary, PathComparison)
                ? ReparsePoints.IsReparsePointOrUninspectable(_fullRoot)
                : ReparsePoints.HasReparsePointOrUninspectableAncestor(ConfinementBoundary, _fullRoot, PathComparison))
        {
            throw new BundleAccessRefusedException(null, "the bundle root, or a directory between the boundary and it, is a reparse point or cannot be inspected");
        }
    }

    /// <summary>
    /// The read gate for a path <see cref="ResolveExact"/> returned. When
    /// <paramref name="ancestorsGuarded"/> is <see langword="true"/> (the exact-case branch),
    /// every directory between the root and <paramref name="path"/> was already STRICT-inspected
    /// by that same call, before it was listed, so only the leaf is left: it must be under the
    /// root and must itself be neither a reparse point nor uninspectable (one inspection).
    /// Otherwise (the joined-path branch, which inspects nothing) <see cref="GuardStrict"/>
    /// inspects the leaf and every ancestor up to the root. The root itself is never inspected
    /// here, in either case.
    /// </summary>
    private void GuardResolved(BundleKey key, string path, bool ancestorsGuarded)
    {
        if (!ancestorsGuarded)
        {
            GuardStrict(key, path);
            return;
        }

        if (!ReparsePoints.IsWithin(_fullRoot, path, PathComparison)
            || (!string.Equals(path, _fullRoot, PathComparison) && ReparsePoints.IsReparsePointOrUninspectable(path)))
        {
            throw new BundleAccessRefusedException(key, "it resolves outside the bundle root, or through a reparse point or an entry that could not be inspected");
        }
    }

    /// <summary>STRICT: the path must be under the root and neither it nor any directory between the root and it may be a reparse point or uninspectable. The root itself is not inspected here: <see cref="CheckBoundary"/> inspects it when a confinement boundary is set, and without one it is trusted host configuration.</summary>
    private void GuardStrict(BundleKey? key, string path)
    {
        // AnyEntryUpTo inspects `path` itself first, then each directory up to (not including)
        // the root; for `path` == root it inspects nothing, which is the root exception.
        if (!ReparsePoints.IsWithin(_fullRoot, path, PathComparison)
            || ReparsePoints.HasReparsePointOrUninspectableAncestor(_fullRoot, path, PathComparison))
        {
            throw new BundleAccessRefusedException(key, "it resolves outside the bundle root, or through a reparse point or an entry that could not be inspected");
        }
    }

    /// <summary>
    /// Maps a key to its on-disk path. With <see cref="MatchesCaseExactly"/>, each segment must
    /// match an existing entry's name <b>exactly</b> (ordinal) — spec §2's correspondence rule —
    /// else <see langword="null"/>. Otherwise the OS compares bytes already and the joined path
    /// is returned as is.
    /// <para><b>Cost (exact matching).</b> Each segment is passed to
    /// <see cref="DirectoryInfo.EnumerateFileSystemInfos(string, SearchOption)"/> as a search
    /// pattern, but .NET does not hand the pattern to the OS: on Windows the enumerator asks
    /// <c>NtQueryDirectoryFile</c> for every entry (no file-name filter), on Unix it reads every
    /// entry with <c>readdir</c>, and on both the pattern is matched in managed code
    /// (<c>FileSystemName.MatchesWin32Expression</c>) over that full listing. Every level of
    /// every <see cref="Read"/>, <see cref="Exists"/> and prefixed <see cref="Enumerate"/>
    /// therefore costs O(n) in the n entries of that directory; the <c>*</c> fallback below is
    /// not a different cost class. The literal-name pattern is kept (the managed match then
    /// builds a <see cref="FileSystemInfo"/> for the matching entry only) and the ordinal
    /// comparison decides. A per-load memo of directory listings is a possible follow-up.</para>
    /// <para>A segment that cannot be a literal pattern is "no such entry" on Windows: one
    /// containing any of <c>&lt; &gt; : " | ? *</c>, or that is rooted (<c>C:</c>, <c>a:b.md</c>),
    /// because no NTFS or FAT name can carry those and .NET rejects such a pattern with an
    /// <see cref="ArgumentException"/> (a layer-1-valid key must not make a question throw).
    /// Elsewhere, a segment the Win32 pattern syntax could read as anything but itself is looked
    /// up with the pattern <c>*</c> and compared ordinally (<see cref="NeedsFullListing"/>).
    /// Each traversed directory is still STRICT-guarded before it is listed, ahead of this
    /// rule.</para>
    /// <para><b>Guarding (exact matching).</b> Each traversed directory below the root is
    /// STRICT-inspected once, on its own (<see cref="ReparsePoints.IsReparsePointOrUninspectable"/>),
    /// before it is listed: every one of its ancestors below the root was inspected at an
    /// earlier level of the same call, so re-walking them would inspect nothing new. The root is
    /// skipped, as everywhere in the per-key guards (<see cref="CheckBoundary"/> covers it when a
    /// boundary is set). <paramref name="ancestorsGuarded"/> is then <see langword="true"/> and
    /// the caller's <see cref="GuardResolved"/> inspects the leaf alone, so for a key
    /// <c>a/b/c.md</c> the entries <c>a</c>, <c>a/b</c> and <c>a/b/c.md</c> are each inspected
    /// exactly once before the object is opened. Without exact matching nothing is inspected
    /// here, <paramref name="ancestorsGuarded"/> is <see langword="false"/>, and the caller runs
    /// the full <see cref="GuardStrict"/> walk.</para>
    /// <para><b>Accepted window.</b> With exact matching, a directory on the path is inspected
    /// once, when this call reaches it, and is not re-inspected by a second walk at read time: a
    /// directory replaced by a junction after its own inspection and before the final open is
    /// not seen. That window lasts one call of this method plus the open. It lies inside the
    /// inter-process window already accepted for every path-based guard (a check on a path
    /// always precedes the open of that path, and another process can act in between; the OS
    /// lock that would close it is deferred to issue #178); a second walk would only have
    /// narrowed it, never closed it.</para>
    /// </summary>
    private string? ResolveExact(BundleKey key, out bool ancestorsGuarded)
    {
        if (!MatchesCaseExactly)
        {
            ancestorsGuarded = false;
            var parts = new string[key.Segments.Count + 1];
            parts[0] = _fullRoot;
            for (var i = 0; i < key.Segments.Count; i++)
            {
                parts[i + 1] = key.Segments[i];
            }

            return Path.Combine(parts);
        }

        ancestorsGuarded = true;
        var current = _fullRoot;
        foreach (var segment in key.Segments)
        {
            // STRICT before listing a traversed directory (the root excepted: a symlinked root
            // without a boundary stays loadable, Review Focus 4). Otherwise an absent name under
            // a junction could be told apart from a present one before the read gate ever ran
            // (fourth review: NotFound vs refused). Only `current` itself: each of its ancestors
            // below the root was `current` at an earlier level of this loop and passed this same
            // test then. `current` is the root only at the first level, where the equality test
            // skips it.
            if (!string.Equals(current, _fullRoot, PathComparison)
                && ReparsePoints.IsReparsePointOrUninspectable(current))
            {
                throw new BundleAccessRefusedException(key, "it resolves through a reparse point or an entry that could not be inspected");
            }

            if (CannotBeLiteralName(segment))
            {
                return null;
            }

            var pattern = NeedsFullListing(segment) ? "*" : segment;
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

    /// <summary>
    /// Whether <see cref="ResolveExact"/> lists the directory with <c>*</c> instead of passing the
    /// segment as its own pattern. On every OS, a segment containing <c>*</c> or <c>?</c>, which
    /// would match as a wildcard. Elsewhere than Windows (where <see cref="CannotBeLiteralName"/>
    /// has already answered for those characters), also one containing <c>"</c>, <c>&lt;</c> or
    /// <c>&gt;</c> — the DOS wildcards of <see cref="MatchType.Win32"/> — or ending with
    /// <c>.</c>, which that syntax treats specially. .NET 10 escapes those three characters on
    /// Unix before matching and matches a trailing <c>.</c> literally, so such a name matches
    /// its own pattern today; listing in full keeps the answer from depending on that internal
    /// detail, at the same O(n) cost.
    /// </summary>
    private static bool NeedsFullListing(string segment) =>
        segment.AsSpan().ContainsAny('*', '?')
        || (!OperatingSystem.IsWindows() && (segment.AsSpan().ContainsAny('"', '<', '>') || segment.EndsWith('.')));

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

    /// <summary>
    /// Reads the whole file into one array sized from <see cref="FileStream.Length"/>, checked
    /// against <paramref name="max"/> again here (the file may have grown since the caller's
    /// check, and nothing is allocated past the bound). A file that shrank between that length
    /// and the read is returned at its new length; one that grew is read on, counting every
    /// byte against <paramref name="max"/> exactly as a chunked read would, and refused past it.
    /// </summary>
    private static byte[] ReadBounded(FileStream stream, long max, BundleKey key)
    {
        var length = stream.Length;
        if (length > max)
        {
            throw new BundleObjectTooLargeException(key, length, max);
        }

        if (length > Array.MaxLength)
        {
            throw new IOException("Stream was too long."); // what the MemoryStream this replaces threw
        }

        var bytes = new byte[length];
        var filled = stream.ReadAtLeast(bytes, bytes.Length, throwOnEndOfStream: false);
        if (filled < bytes.Length)
        {
            return bytes.AsSpan(0, filled).ToArray(); // shrank between the length check and the read
        }

        var next = stream.ReadByte();
        if (next < 0)
        {
            return bytes;
        }

        // Grew between the length check and the read: go on counting, as before.
        using var buffer = new MemoryStream();
        buffer.Write(bytes);
        buffer.WriteByte((byte)next);
        long total = bytes.Length + 1L;
        if (total > max)
        {
            throw new BundleObjectTooLargeException(key, total, max);
        }

        var chunk = new byte[81920];
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
