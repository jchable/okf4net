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
/// deliberately weak and never used for pinning. A key is matched against the stored names
/// exactly (ordinal, segment by segment) on every OS and every volume, case-insensitive or
/// not (spec §8). Within one load or one validation, each
/// directory's entry names are listed once for sequential lookups and reused (<c>IProviderScope.BeginScope</c>);
/// nothing is kept past it.
/// </summary>
public sealed class FileSystemBundleProvider : IBundleProvider, IProviderScope
{
    private static readonly StringComparison PathComparison = StringComparison.Ordinal;

    private readonly string _fullRoot;
    private readonly BundleLimits _limits;

    /// <summary>
    /// The listing memo of the scope open on the calling flow, if any (<see cref="IProviderScope.BeginScope"/>).
    /// Per provider instance and per flow: a call made outside the scope that opened it — another
    /// thread, a later call on a long-lived provider — never sees it.
    /// </summary>
    private readonly AsyncLocal<ListingMemo?> _scope = new();

    /// <summary>Test hook: every directory listing this provider asked the OS for.</summary>
    private int _directoryListings;

    /// <summary>
    /// Creates a provider over <paramref name="root"/>. <paramref name="confinementBoundary"/>
    /// (a catalog root) makes the root itself subject to the STRICT guards on every access;
    /// without it the root is trusted host configuration, as today.
    /// <paramref name="additionalConstraints"/> is OR-ed into the OS's native set and can only
    /// add rules: a host on a case-insensitive mount under Linux adds
    /// <see cref="BundleKeyConstraints.CaseInsensitiveCollisions"/> so that the loader also
    /// rejects siblings that collide there. The folding flags matter only for those collision
    /// checks; exact matching of the stored name does not depend on them, it is always on.
    /// <paramref name="limits"/> bounds <see cref="Read"/>, which the interface gives no
    /// per-call limits.
    /// </summary>
    public FileSystemBundleProvider(string root, string? confinementBoundary = null, BundleKeyConstraints additionalConstraints = BundleKeyConstraints.None, BundleLimits? limits = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(root);
        Root = root;
        _fullRoot = ReparsePoints.CanonicalizeRoot(root);
        ConfinementBoundary = confinementBoundary is null ? null : ReparsePoints.CanonicalizeRoot(confinementBoundary);
        NativeConstraints = BundleKeyConstraintSets.ForCurrentOperatingSystem() | additionalConstraints;
        _limits = limits ?? BundleLimits.Default;
    }

    /// <summary>The root as given (also <see cref="Location"/>).</summary>
    public string Root { get; }

    /// <summary>The canonical boundary every access is re-checked against, or <see langword="null"/>.</summary>
    public string? ConfinementBoundary { get; }

    /// <inheritdoc/>
    public string Id => _fullRoot;

    /// <inheritdoc/>
    public string Location => Root;

    /// <inheritdoc/>
    public BundleProviderCapabilities Capabilities => new(CanWrite: false);

    /// <inheritdoc/>
    public BundleKeyConstraints NativeConstraints { get; }

    /// <summary>Test hook: how many directory listings this provider has asked the OS for so far (walks and exact-case lookups alike).</summary>
    internal int DirectoryListings => Volatile.Read(ref _directoryListings);

    /// <summary>
    /// Test hook: called with a directory's full path after the walk has examined it as an entry
    /// of its parent and before it enters it, so a test can stand in for another process acting
    /// in that window (a directory swapped for a junction). <see langword="null"/> outside tests.
    /// </summary>
    internal Action<string>? BeforeWalkEntersDirectory { get; set; }

    /// <summary>
    /// Opens a listing scope on the calling flow (<see cref="IProviderScope"/>): until the handle
    /// is disposed, each directory this flow lists — by a walk (<see cref="GetVersion"/>,
    /// <see cref="Enumerate"/>) or by an exact-case lookup (<see cref="ResolveExact"/>) — is
    /// listed once (for sequential lookups: flows forked inside the scope that miss together may
    /// each list it) and its entry names are reused by the scope's later calls, instead of
    /// listing that directory again for every key under it. <see cref="Bundle.Load(IBundleProvider, BundleLoadOptions?)"/>
    /// and <see cref="BundleValidator.Validate"/> each open one around their own work.
    /// <para><b>What is memoised.</b> Only the names a directory listing returned; a listing
    /// that fails is not kept, so the next lookup tries again. Every guard still runs at access time, inside the
    /// scope as outside it: <see cref="CheckBoundary"/> on every call, the STRICT inspection of
    /// each traversed directory before its names are used, the leaf inspection
    /// (<see cref="GuardResolved"/>), the walk's STRICT guard on every directory and its
    /// per-entry reparse-point skip, and the final <c>File.Exists</c>/open.</para>
    /// <para><b>Lifetime.</b> The memo belongs to the flow that opened the scope (an
    /// <see cref="AsyncLocal{T}"/> on this instance), so a concurrent call on another thread, or
    /// any call made after the scope ended, lists the directory itself; a flow forked inside the
    /// scope shares the memo, which is locked for that case. Disposing the handle closes the memo
    /// for good — even a flow that captured it stops using it — so nothing learned in one load or
    /// one validation is used by the next: a file created, deleted or renamed (case-only renames
    /// included) after the scope ended is seen. The scope lasts while a load or a validation
    /// runs, until it returns. A scope opened inside an open one joins it.</para>
    /// <para><b>Bound.</b> The memo counts each listed directory plus its names against this
    /// provider's <see cref="BundleLimits.MaxEntries"/>; a listing that would pass it is not
    /// kept, and from then on the scope stops memoising and every later lookup lists, as without
    /// a scope. Never an exception for that reason.</para>
    /// <para><b>Accepted window.</b> The name-to-entry correspondence is
    /// established when a directory is first listed in the scope, not at each access: a
    /// case-only rename made by another process during a load or a validation can make a later
    /// access of the same scope open the renamed entry under its former name on a case-folding
    /// volume, and a file created or renamed by another process during the scope may not be seen
    /// until the scope ends (a deletion is seen, through the final existence check). The
    /// STRICT guards still inspect that entry, and each directory above it, at access time
    /// exactly as without a scope, so the memo lets no access reach anything — outside the root,
    /// or through a reparse point — that an unmemoised access would refuse. For the name
    /// correspondence the window is one scope instead of one lookup, inside the inter-process
    /// window already accepted for every path-based guard (issue #178).</para>
    /// </summary>
    IDisposable IProviderScope.BeginScope()
    {
        if (_scope.Value is { IsOpen: true })
        {
            return ProviderScopes.None; // joins the scope already open on this flow
        }

        var memo = new ListingMemo(_limits.MaxEntries);
        _scope.Value = memo;
        return new ScopeHandle(this, memo);
    }

    /// <summary>The open memo of the calling flow's scope, or <see langword="null"/>.</summary>
    private ListingMemo? ActiveMemo => _scope.Value is { IsOpen: true } memo ? memo : null;

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
        }, isStart: true);

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

            GuardResolved(prefix, resolved); // a junction prefix is refused, not walked
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
        }, isStart: true);

        return result;
    }

    /// <inheritdoc/>
    public BundleObject Read(BundleKey key, BundleObjectVersion? expected = null)
    {
        ArgumentNullException.ThrowIfNull(key);
        CheckBoundary();
        var path = ResolveExact(key) ?? throw new BundleObjectNotFoundException(key);
        GuardResolved(key, path);
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
    /// through an entry that cannot be inspected. A directory on the path that cannot be
    /// listed — the listing is denied (an execute-only directory on Unix), or fails with any
    /// other I/O error — answers <see langword="false"/> on every OS, as an absent entry does,
    /// since exact matching must list it: no object is read and nothing outside the root is
    /// reached.
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
        // parent listing that is denied or fails are "no such entry", not refusals.
        var path = ResolveExact(key);
        if (path is null)
        {
            return false;
        }

        GuardResolved(key, path);
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
    /// The read gate for a path <see cref="ResolveExact"/> returned. Every directory between
    /// the root and <paramref name="path"/> was already STRICT-inspected by that same call,
    /// before it was listed, so only the leaf is left: it must be under the root and must itself
    /// be neither a reparse point nor uninspectable (one inspection). Together they cover what
    /// <see cref="GuardStrict"/> inspects — the leaf and every ancestor up to the root — and,
    /// like it, never inspect the root itself.
    /// </summary>
    private void GuardResolved(BundleKey key, string path)
    {
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
    /// The walk's guard for a directory below its start: under the root, and itself neither a
    /// reparse point nor uninspectable (one inspection). Its ancestors between the start and it
    /// were inspected when the same walk entered them, and the start by <see cref="GuardStrict"/>,
    /// so together they cover what <see cref="GuardStrict"/> on this directory would inspect.
    /// </summary>
    private void GuardEntered(string dir)
    {
        if (!ReparsePoints.IsWithin(_fullRoot, dir, PathComparison) || ReparsePoints.IsReparsePointOrUninspectable(dir))
        {
            throw new BundleAccessRefusedException(null, "it resolves outside the bundle root, or through a reparse point or an entry that could not be inspected");
        }
    }

    /// <summary>
    /// Maps a key to its on-disk path: each segment must match an existing entry's name
    /// <b>exactly</b> (ordinal) — spec §2's correspondence rule — else <see langword="null"/>.
    /// This runs on every OS: whether a volume folds case or Unicode form is a property of the
    /// volume, not of the OS (a vfat, NTFS, exFAT or CIFS mount, or a case-insensitive ext4
    /// directory, under Linux), so the provider never leaves the comparison to the OS. A
    /// directory whose listing is denied or fails is therefore "no such entry" everywhere.
    /// <para><b>Cost.</b> Each segment is passed to
    /// <see cref="DirectoryInfo.EnumerateFileSystemInfos(string, SearchOption)"/> as a search
    /// pattern, but .NET does not hand the pattern to the OS: on Windows the enumerator asks
    /// <c>NtQueryDirectoryFile</c> for every entry (no file-name filter), on Unix it reads every
    /// entry with <c>readdir</c>, and on both the pattern is matched in managed code
    /// (<c>FileSystemName.MatchesWin32Expression</c>) over that full listing. Every level of
    /// every <see cref="Read"/>, <see cref="Exists"/> and prefixed <see cref="Enumerate"/>
    /// therefore costs O(n) in the n entries of that directory; the <c>*</c> fallback below is
    /// not a different cost class. The literal-name pattern is kept (the managed match then
    /// builds a <see cref="FileSystemInfo"/> for the matching entry only) and the ordinal
    /// comparison decides. That is the cost outside a scope. Inside one
    /// (<see cref="IProviderScope.BeginScope"/>, opened by each load and each validation), a
    /// directory is listed in full the first time the scope needs it — or not at all, when a walk
    /// of the same scope already listed it — and every later lookup in it is a binary search
    /// over the kept names, ordinal as above.</para>
    /// <para>A segment that cannot be a literal pattern is "no such entry" on Windows: one
    /// containing any of <c>&lt; &gt; : " | ? *</c>, or that is rooted (<c>C:</c>, <c>a:b.md</c>),
    /// because no NTFS or FAT name can carry those and .NET rejects such a pattern with an
    /// <see cref="ArgumentException"/> (a layer-1-valid key must not make a question throw).
    /// Elsewhere, a segment the Win32 pattern syntax could read as anything but itself is looked
    /// up with the pattern <c>*</c> and compared ordinally (<see cref="NeedsFullListing"/>).
    /// Each traversed directory is still STRICT-guarded before it is listed, ahead of this
    /// rule.</para>
    /// <para><b>Guarding.</b> Each traversed directory below the root is
    /// STRICT-inspected once, on its own (<see cref="ReparsePoints.IsReparsePointOrUninspectable"/>),
    /// before it is listed: every one of its ancestors below the root was inspected at an
    /// earlier level of the same call, so re-walking them would inspect nothing new. The root is
    /// skipped, as everywhere in the per-key guards (<see cref="CheckBoundary"/> covers it when a
    /// boundary is set). The caller's <see cref="GuardResolved"/> then inspects the leaf alone,
    /// so for a key <c>a/b/c.md</c> the entries <c>a</c>, <c>a/b</c> and <c>a/b/c.md</c> are
    /// each inspected exactly once before the object is opened.</para>
    /// <para><b>Accepted window.</b> A directory on the path is inspected
    /// once, when this call reaches it, and is not re-inspected by a second walk at read time: a
    /// directory replaced by a junction after its own inspection is not seen by the remaining
    /// listings of this call, by the leaf check, nor by the final open or <c>File.Exists</c>, so
    /// within that window an <see cref="Exists"/> answer about a name under it, or a read through
    /// it, is possible. That window lasts one call of this method plus the open. It lies inside the
    /// inter-process window already accepted for every path-based guard (a check on a path
    /// always precedes the open of that path, and another process can act in between; the OS
    /// lock that would close it is deferred to issue #178); a second walk would only have
    /// narrowed it, never closed it. Inside a scope, the name correspondence itself — which entry
    /// a segment names — comes from the scope's first listing of that directory, so for names
    /// that window is the whole scope, from the start of a load or a validation until it
    /// returns: a case-only rename by another process during it can make a later lookup return
    /// the renamed entry's path under its former name, which a case-folding volume then opens,
    /// and a file created or renamed by another process during it may not be seen until it
    /// ends (a deletion is seen, through the final existence check). The STRICT inspections above still run on every
    /// call, before the names are used, and the leaf check and the open still follow, so nothing
    /// outside the root and no reparse point is reached that an unmemoised lookup would refuse.</para>
    /// </summary>
    private string? ResolveExact(BundleKey key)
    {
        var memo = ActiveMemo;
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

            var match = memo is null ? FindEntry(current, segment) : FindEntryMemoised(memo, current, segment);
            if (match is null)
            {
                return null;
            }

            current = match;
        }

        return current;
    }

    /// <summary>
    /// Outside a scope: lists <paramref name="directory"/> for one entry named
    /// <paramref name="segment"/> exactly (ordinal), stopping at the first match; its full path,
    /// or <see langword="null"/> when there is none or the listing is denied or fails.
    /// </summary>
    private string? FindEntry(string directory, string segment)
    {
        var pattern = NeedsFullListing(segment) ? "*" : segment;
        Interlocked.Increment(ref _directoryListings);
        try
        {
            foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos(pattern, SearchOption.TopDirectoryOnly))
            {
                if (string.Equals(entry.Name, segment, StringComparison.Ordinal))
                {
                    return entry.FullName;
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        return null;
    }

    /// <summary>
    /// Inside a scope: the same answer as <see cref="FindEntry"/>, from the scope's memo of
    /// <paramref name="directory"/>'s names, listing it in full (and keeping the names, sorted
    /// ordinally) only the first time the scope needs it. A listing that fails, at once or after
    /// returning some entries, is not kept: this lookup answers from the entries read before the
    /// failure — none, or those <see cref="FindEntry"/>, which reads the same entries in the same
    /// order and stops at its first match, would have reached — and the next lookup lists again.
    /// Once the memo has stopped (its bound was reached), a directory it does not hold is
    /// looked up by <see cref="FindEntry"/>, which streams the listing instead of copying and
    /// sorting names the memo would drop.
    /// </summary>
    private string? FindEntryMemoised(ListingMemo memo, string directory, string segment)
    {
        if (memo.TryGet(directory, out var known))
        {
            return Array.BinarySearch(known, segment, StringComparer.Ordinal) >= 0
                ? Path.Join(directory, segment)
                : null;
        }

        if (memo.IsStopped)
        {
            return FindEntry(directory, segment);
        }

        Interlocked.Increment(ref _directoryListings);
        var listed = new List<string>();
        try
        {
            foreach (var full in Directory.EnumerateFileSystemEntries(directory))
            {
                listed.Add(Path.GetFileName(full));
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return listed.Contains(segment, StringComparer.Ordinal) ? Path.Join(directory, segment) : null;
        }

        var names = listed.ToArray();
        Array.Sort(names, StringComparer.Ordinal);
        memo.Add(directory, names);
        return Array.BinarySearch(names, segment, StringComparer.Ordinal) >= 0 ? Path.Join(directory, segment) : null;
    }

    /// <summary>
    /// The entry names of the directories one scope listed, keyed by the directory's full path
    /// (<see cref="IProviderScope.BeginScope"/>), sorted ordinally; only listings that completed
    /// are kept. Bounded: each kept directory costs one plus its names
    /// against <c>budget</c>; the first listing that would pass it is dropped and nothing more is
    /// kept. Locked, since a flow forked inside the scope shares it; closed for good when the
    /// scope ends, after which it answers nothing and keeps nothing.
    /// </summary>
    private sealed class ListingMemo(int budget)
    {
        private readonly Lock _gate = new();
        private readonly Dictionary<string, string[]> _listings = new(StringComparer.Ordinal);
        private long _held;
        private bool _stopped;
        private volatile bool _open = true;

        public bool IsOpen => _open;

        /// <summary>Whether the bound was reached: nothing more is kept, so a lookup need not list in full.</summary>
        public bool IsStopped
        {
            get
            {
                lock (_gate)
                {
                    return _stopped;
                }
            }
        }

        public bool TryGet(string directory, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string[]? names)
        {
            lock (_gate)
            {
                if (_open && _listings.TryGetValue(directory, out names))
                {
                    return true;
                }
            }

            names = null;
            return false;
        }

        public void Add(string directory, string[] names)
        {
            lock (_gate)
            {
                if (!_open || _stopped || _listings.ContainsKey(directory))
                {
                    return;
                }

                var cost = 1L + names.Length;
                if (_held + cost > budget)
                {
                    _stopped = true; // stop memoising; every later lookup lists, as without a scope
                    return;
                }

                _listings.Add(directory, names);
                _held += cost;
            }
        }

        public void Close()
        {
            lock (_gate)
            {
                _open = false;
                _listings.Clear();
            }
        }
    }

    /// <summary>Ends one scope: closes its memo, and clears it from the flow when this flow still holds it.</summary>
    private sealed class ScopeHandle(FileSystemBundleProvider owner, ListingMemo memo) : IDisposable
    {
        public void Dispose()
        {
            memo.Close();
            if (ReferenceEquals(owner._scope.Value, memo))
            {
                owner._scope.Value = null;
            }
        }
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
    /// counted before the reparse-point skip and before sorting. Inside a scope
    /// (<see cref="IProviderScope.BeginScope"/>) the sorted names of each listed directory are
    /// kept, and a directory the scope already listed is not listed again: its kept names are
    /// counted the same way and visited after the same guards, so the second walk of a load
    /// (<see cref="GetVersion"/> after <see cref="Enumerate"/>) and the exact-case lookups reuse
    /// the first walk's listings.
    /// <para><b>Guarding.</b> The walk's start (<paramref name="isStart"/>) gets the full
    /// <see cref="GuardStrict"/>: the start and every directory between it and the root. For the
    /// root (<see cref="GetVersion"/>, an unprefixed <see cref="Enumerate"/>) that inspects
    /// nothing, as before; for a prefixed <see cref="Enumerate"/> the chain was also inspected by
    /// <see cref="ResolveExact"/> and <see cref="GuardResolved"/> just before. Every directory
    /// below the start is then STRICT-inspected on its own
    /// (<see cref="ReparsePoints.IsReparsePointOrUninspectable"/>), once, before it is listed:
    /// each of its ancestors between the start and itself was entered by this same walk, and so
    /// inspected before being listed, so re-walking them would inspect nothing new. The set of
    /// entries inspected before a listing is unchanged; each is inspected once per walk instead
    /// of once per descendant, which keeps a deep tree linear rather than quadratic.</para>
    /// <para><b>Accepted window.</b> An ancestor replaced by a link after its own inspection is
    /// not seen again by the listings of its descendants in this walk. That window lies inside the
    /// inter-process window already accepted for every path-based guard (issue #178), as for
    /// <see cref="ResolveExact"/>; re-walking the ancestors would only have narrowed it.</para>
    /// </summary>
    private void Walk(string dir, string rel, int depth, BundleLimits limits, Counter counter, Action<WalkEntry> visit, bool isStart)
    {
        CheckBoundary();
        // The path alone: a directory whose relative name is no valid key (U+0085, > 1 024
        // bytes) is still a directory to guard; its entries are rejected BY NAME later, by the
        // loader, never by an exception here (the third review hit BundleKeyException).
        if (isStart)
        {
            GuardStrict(null, dir);
        }
        else
        {
            GuardEntered(dir);
        }

        var memo = ActiveMemo;
        string[] names;
        if (memo is not null && memo.TryGet(dir, out var known))
        {
            // Listed earlier in this scope: counted as a fresh listing would be, every entry
            // before the skip (the bound throws iff the running total passes it).
            if (known.Length > limits.MaxEntries - counter.Entries)
            {
                throw new BundleLimitExceededException(BundleBound.Entries, limits.MaxEntries);
            }

            counter.Entries += known.Length;
            names = known;
        }
        else
        {
            Interlocked.Increment(ref _directoryListings);
            var listed = new List<string>();
            foreach (var full in Directory.EnumerateFileSystemEntries(dir))
            {
                if (++counter.Entries > limits.MaxEntries)
                {
                    throw new BundleLimitExceededException(BundleBound.Entries, limits.MaxEntries);
                }

                listed.Add(Path.GetFileName(full));
            }

            names = listed.ToArray();
            Array.Sort(names, StringComparer.Ordinal);
            memo?.Add(dir, names);
        }

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
                BeforeWalkEntersDirectory?.Invoke(full);
                Walk(full, raw, depth + 1, limits, counter, visit, isStart: false);
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
