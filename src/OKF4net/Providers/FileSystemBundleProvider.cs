// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Security.Cryptography;
using System.Text;
using OKF4net.Internal;

namespace OKF4net.Providers;

/// <summary>
/// The default provider (spec §8): a bundle as a directory tree, writable
/// (<see cref="Write"/> commits each object atomically, issue #157).
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
    public BundleProviderCapabilities Capabilities => new(CanWrite: true);

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
    /// <remarks>
    /// <para>Shares writing and deletion, so on Windows a read never denies a concurrent replacement
    /// or delete the sharing it needs. While Windows' <c>ReplaceFile</c> replaces the object, a reader can for a
    /// moment meet a sharing violation, or find that the entry its exact listing had just matched
    /// is no longer there (measured, issue #157): on Windows only, a read retries those two —
    /// and nothing else — at most <see cref="MaxReadAttempts"/> times in all, 5 to 20 ms apart,
    /// redoing every check on each attempt (the confinement boundary, the exact-name match, the
    /// STRICT guards) and checking <paramref name="expected"/> against the bytes it actually
    /// read. A key the listing does not match is answered at once, never retried; nor is a
    /// refusal or a version mismatch. An object really deleted is not found once its listing no
    /// longer matches it, or after the last attempt. This narrows the window, it does not close
    /// it: a listing made while <c>ReplaceFile</c> runs can still find no entry under the name,
    /// and a read can still fail after its last attempt.</para>
    /// </remarks>
    public BundleObject Read(BundleKey key, BundleObjectVersion? expected = null)
    {
        ArgumentNullException.ThrowIfNull(key);
        for (var attempt = 1; ; attempt++)
        {
            CheckBoundary();
            var path = ResolveExact(key) ?? throw new BundleObjectNotFoundException(key);
            GuardResolved(key, path);
            if (!File.Exists(path) && Directory.Exists(path))
            {
                throw new BundleObjectNotFoundException(key); // a directory is no object
            }

            byte[] bytes;
            try
            {
                ReadOpeningForTest?.Invoke(path);

                // Shares writing and deletion: a read must never make a concurrent replacement
                // (File.Replace) or delete fail on Windows with a sharing violation.
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (fs.Length > _limits.MaxObjectBytes)
                {
                    throw new BundleObjectTooLargeException(key, fs.Length, _limits.MaxObjectBytes);
                }

                bytes = ReadBounded(fs, _limits.MaxObjectBytes, key);
            }
            catch (Exception failure) when (failure is FileNotFoundException or DirectoryNotFoundException || IsSharingViolation(failure))
            {
                // The entry was matched a moment ago: on Windows, the ReplaceFile window.
                if (attempt < MaxReadAttempts && OperatingSystem.IsWindows())
                {
                    Thread.Sleep(Random.Shared.Next(5, 21));
                    continue;
                }

                if (failure is FileNotFoundException or DirectoryNotFoundException)
                {
                    throw new BundleObjectNotFoundException(key);
                }

                throw;
            }

            var version = new BundleObjectVersion(Convert.ToHexStringLower(SHA256.HashData(bytes)));
            if (expected is { } e && e != version)
            {
                throw new BundleVersionMismatchException(key);
            }

            return new BundleObject(new MemoryStream(bytes, writable: false), version, bytes.Length);
        }
    }

    /// <summary>How many times in all <see cref="Read"/> opens an object on Windows when it meets the <c>ReplaceFile</c> window.</summary>
    private const int MaxReadAttempts = 3;

    /// <summary>A sharing or lock violation (<c>ERROR_SHARING_VIOLATION</c>, <c>ERROR_LOCK_VIOLATION</c>).</summary>
    private static bool IsSharingViolation(Exception e) =>
        e is IOException && e.HResult is unchecked((int)0x80070020) or unchecked((int)0x80070021);

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
    /// <remarks>
    /// <para><b>The commit protocol</b> (issue #157). (1) The parent directories are resolved
    /// segment by segment, each existing name matched exactly and each traversed directory
    /// STRICT-inspected before it is listed; a missing directory is created, then listed again to
    /// prove the volume stored it under exactly that name; the whole chain from the root is
    /// STRICT-checked once more after any creation. (2) The bytes go to a temporary in the target's
    /// own directory, <c>.okf-write-</c> plus 32 random hex digits plus <c>.tmp</c>, created with
    /// <see cref="FileMode.CreateNew"/> (an entry already there, a planted link included, is never
    /// opened) and <see cref="FileShare.None"/>; on Unix a replacement's temporary is created
    /// owner-only (0600) before any byte is written and gets the target's mode through its handle
    /// before it is closed, while a new object's temporary gets the mode any new file gets in that
    /// directory. The bytes are flushed to stable storage and the handle closed. (3) Under an
    /// in-process lock per object, shared by every instance of this provider, the boundary, the
    /// chain and the temporary are STRICT-checked again, the target's exact name and current
    /// SHA-256 are read again, and the condition is checked against them. (4) The commit:
    /// <see cref="File.Replace(string, string, string?)"/> over an existing object,
    /// <see cref="File.Move(string, string, bool)"/> without overwrite for a new one.</para>
    /// <para><b>Names.</b> A name the volume would confuse with a stored entry — a directory, a
    /// sibling differing only in case or Unicode form on a volume that folds them, a Windows name
    /// with a trailing dot or space, a character no Windows name carries — is refused with
    /// <see cref="BundleWriteConflictReason.NameUnavailable"/> before anything is created.</para>
    /// <para><b>Permissions.</b> A target the process may not write is refused as
    /// <c>File.WriteAllText</c> refused it, although a rename would need only the directory's
    /// permission on Unix: the target is opened for writing before the commit. Only
    /// <see cref="BundleWriteConditionKind.IfMatch"/> reads the target (to compare its SHA-256),
    /// so an unconditional write replaces a file the process may write but not read, as
    /// <c>File.WriteAllText</c> did, while an <see cref="BundleWriteConditionKind.IfMatch"/> write
    /// of such a file is refused with <see cref="UnauthorizedAccessException"/>: its version
    /// cannot be established.</para>
    /// <para><b>Failures.</b> A failure before the commit removes the temporary and leaves the
    /// target untouched; it is the raw <see cref="IOException"/> or
    /// <see cref="UnauthorizedAccessException"/>, whose message may name a host path (the core
    /// translates it). A replacement that the storage refuses without any effect — Windows'
    /// <c>ERROR_UNABLE_TO_REMOVE_REPLACED</c> (1175), measured while readers held the object
    /// open, or a sharing violation — is made again, at most three times in all, 5 to 25 ms
    /// apart, each time from the re-check of the condition against the bytes stored then; after
    /// the last attempt it raises <see cref="BundleWriteNotCommittedException"/>, the temporary
    /// removed and the target untouched. Any other failure of the commit call is classified from
    /// what the disk then holds, once the boundary and the STRICT guards have been renewed: the
    /// temporary still there and the target as it was — nothing happened, the raw failure, the
    /// temporary removed; the temporary still there and the target changed — another writer
    /// landed, <see cref="BundleWriteConflictException"/>, the temporary removed (an unconditional
    /// replacement does not read its target: the raw failure); the temporary gone and the target
    /// holding the new bytes — committed, success. Anything else, a guard that now refuses, and
    /// always Windows' <c>ERROR_UNABLE_TO_MOVE_REPLACEMENT</c> (1176, 1177) — which can leave no
    /// object under the key and the new bytes under the temporary's name — raises
    /// <see cref="BundleWriteOutcomeUnknownException"/> and keeps the temporary, the only copy of
    /// the new content. The temporary is never removed through a path that a link has entered
    /// since it was created; it is then left in place.</para>
    /// <para><b>What a concurrent reader sees.</b> Never a part of the new content. On Unix the
    /// commit is a <c>rename</c>, so a reader finds the old object or the new one. On Windows
    /// <c>ReplaceFile</c> is atomic for the stored state but not for readers: while it runs, a
    /// reader can for a moment find no entry under the name or meet a sharing violation (measured
    /// on Windows 11); <see cref="Read"/> shares writing and deletion, so it never denies a
    /// replacement the sharing it needs, and retries that window a bounded number of times.</para>
    /// <para><b>Durability.</b> When this method returns, the new bytes reached stable
    /// storage (<see cref="FileStream.Flush(bool)"/>) before the rename. A process crash or an I/O
    /// error during the write leaves the object holding its whole old content or its whole new
    /// content. A power loss can also leave the old content after a replacement returned, or no
    /// object after a creation returned, and a temporary behind: the directory entry is not
    /// flushed (no .NET API flushes a directory).</para>
    /// <para><b>Orphaned temporaries.</b> A temporary left behind — an unknown outcome, or a
    /// failure whose temporary could no longer be reached safely — is not a <c>.md</c>, so the
    /// core ignores it as a concept; it still counts toward <see cref="BundleLimits.MaxEntries"/>
    /// and a producer that requires an empty output directory sees it. Nothing removes it
    /// automatically: it may be the only copy of the new content.</para>
    /// <para><b>Windows ACL.</b> The temporary inherits its directory's ACL while it is written;
    /// <see cref="File.Replace(string, string, string?)"/> keeps the replaced object's own DACL on
    /// the committed object (measured). An object whose explicit ACL is narrower than its
    /// directory's is therefore readable under the directory's ACL, through the temporary, while
    /// its next content is being written.</para>
    /// <para><b>Inter-process window.</b> The commit lock is in-process: between the re-check and
    /// the commit another process can act (issue #178). On Unix
    /// <see cref="File.Move(string, string, bool)"/> without overwrite is an <c>lstat</c> then a
    /// <c>rename</c> (measured), so a name another process creates in that window is
    /// replaced.</para>
    /// </remarks>
    public BundleObjectVersion Write(BundleKey key, ReadOnlyMemory<byte> content, BundleWriteCondition condition)
    {
        ArgumentNullException.ThrowIfNull(key);
        var written = new BundleObjectVersion(Convert.ToHexStringLower(SHA256.HashData(content.Span)));
        for (var attempt = 1; ; attempt++)
        {
            CheckBoundary();
            var parent = ResolveParentForWrite(key);
            var existing = FindExactForWrite(key, parent, key.Name);
            if (existing is not null)
            {
                GuardObject(key, existing);
            }

            var temporary = WriteTemporary(parent, existing, content);
            if (CommitTemporary(key, parent, temporary, preparedAsReplacement: existing is not null, written, condition))
            {
                ForgetListings(key);
                return written;
            }

            if (attempt == MaxPreparations)
            {
                throw new BundleWriteConflictException(key);
            }
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// The object's directories are left in place, empty or not: an empty directory holds no key.
    /// A key that names a directory names no object, so nothing is deleted. The same exact-name
    /// resolution, STRICT guards, boundary re-check and per-object lock as <see cref="Write"/>.
    /// </remarks>
    public void Delete(BundleKey key, BundleWriteCondition condition)
    {
        ArgumentNullException.ThrowIfNull(key);
        CheckBoundary();
        var parent = ResolveParentForDelete(key);
        if (parent is null)
        {
            if (!Holds(condition, exists: false, null))
            {
                throw new BundleWriteConflictException(key);
            }

            return;
        }

        lock (CommitLockFor(Path.Join(parent, key.Name)))
        {
            CheckBoundary();
            GuardStrict(key, parent);
            var current = ListExact(parent, key.Name);
            if (current is not null)
            {
                GuardResolved(key, current);
                if (Directory.Exists(current))
                {
                    current = null; // a directory is no object
                }
            }

            // Only IfMatch reads the object: deleting it needs no right to read it.
            BundleObjectVersion? version = current is not null && condition.Kind == BundleWriteConditionKind.IfMatch ? HashForRead(current) : null;
            if (!Holds(condition, current is not null, version))
            {
                throw new BundleWriteConflictException(key);
            }

            if (current is null)
            {
                return;
            }

            BeforeCommitForTest?.Invoke(null, current);
            File.Delete(current);
        }

        ForgetListings(key);
    }

    /// <summary>The temporaries' name prefix (<see cref="Write"/>); the rest is 32 random lowercase hex digits and <see cref="TemporarySuffix"/>.</summary>
    internal const string TemporaryPrefix = ".okf-write-";

    /// <summary>The temporaries' name suffix.</summary>
    internal const string TemporarySuffix = ".tmp";

    /// <summary>How many times <see cref="Write"/> prepares a temporary before giving up on an object that keeps appearing under an unconditional creation (Unix only, see <see cref="CommitTemporary"/>).</summary>
    private const int MaxPreparations = 3;

    /// <summary>
    /// The in-process commit locks: the check-and-commit of one object runs under one of them,
    /// picked by the target's full path (case-insensitively, so that two spellings of one root on
    /// a folding volume share it), whatever provider instance runs it. Striped, so the set is
    /// bounded; two objects that share a stripe only wait for each other.
    /// </summary>
    private static readonly Lock[] CommitLocks = [.. Enumerable.Range(0, 64).Select(_ => new Lock())];

    /// <summary>Test hook: called with the temporary's full path as soon as it is created, before any byte is written. <see langword="null"/> outside tests.</summary>
    internal Action<string>? TemporaryCreatedForTest { get; set; }

    /// <summary>
    /// Test hook: called by <see cref="Write"/> with the parent directory's full path once every
    /// parent directory of the key exists (found, or created), before the STRICT re-check of the
    /// whole path and before the temporary is created — another process acting in that window (a
    /// directory just created swapped for a junction, a link planted under the object's name).
    /// <c>BundleConceptWriter.BeforeLateReparseCheckForTest</c> forwards to it.
    /// <see langword="null"/> outside tests.
    /// </summary>
    internal Action<string>? ParentsResolvedForTest { get; set; }

    /// <summary>Test hook: the temporary's file name instead of a random one (to plant an entry under it beforehand). <see langword="null"/> outside tests.</summary>
    internal Func<string>? TemporaryNameForTest { get; set; }

    /// <summary>Test hook: called with the temporary's full path once it is written, flushed and closed, before the commit lock is taken — another process acting in that window. <see langword="null"/> outside tests.</summary>
    internal Action<string>? AfterTemporaryWrittenForTest { get; set; }

    /// <summary>Test hook: called under the commit lock once every re-check and the condition have passed, just before the commit call, with the temporary (<see langword="null"/> for a delete) and the target's full path. <see langword="null"/> outside tests.</summary>
    internal Action<string?, string>? BeforeCommitForTest { get; set; }

    /// <summary>
    /// Test hook: replaces the commit call itself — <c>(temporary, target, replace)</c> — so a test
    /// can leave the disk as a failing <c>ReplaceFile</c> or <c>rename</c> does and throw what it
    /// throws; the provider then classifies the failure exactly as for the real call.
    /// <see langword="null"/> outside tests.
    /// </summary>
    internal Action<string, string, bool>? CommitForTest { get; set; }

    /// <summary>Test hook: called by <see cref="Read"/> with the object's full path on every attempt, once every check has passed and right before the object is opened — what it throws is what the open threw. <see langword="null"/> outside tests.</summary>
    internal Action<string>? ReadOpeningForTest { get; set; }

    /// <summary>Test hook: called with the temporary's full path after its bytes are written and just before they are flushed to stable storage — the point where a full disk, a quota or an I/O error surfaces; an exception it throws stands for that error. <see langword="null"/> outside tests.</summary>
    internal Action<string>? TemporaryFlushForTest { get; set; }

    /// <summary>Whether <paramref name="name"/> (one segment) is a temporary this provider creates.</summary>
    internal static bool IsTemporaryName(string name) =>
        name.Length == TemporaryPrefix.Length + 32 + TemporarySuffix.Length
        && name.StartsWith(TemporaryPrefix, StringComparison.Ordinal)
        && name.EndsWith(TemporarySuffix, StringComparison.Ordinal)
        && !name.AsSpan(TemporaryPrefix.Length, 32).ContainsAnyExcept("0123456789abcdef");

    /// <summary>
    /// Test seam (issue #157, orphaned temporaries): the raw names of the temporaries present in
    /// the tree — left by a write whose outcome was unknown, or by a failure that could no longer
    /// reach its temporary safely. Read through <see cref="Enumerate"/>, so it adds no walk.
    /// </summary>
    internal IReadOnlyList<string> ListOrphanedTemporaries(BundleLimits limits) =>
        [.. Enumerate(null, limits).Select(l => l.RawName).Where(n => IsTemporaryName(n[(n.LastIndexOf('/') + 1)..]))];

    private static Lock CommitLockFor(string path) =>
        CommitLocks[(StringComparer.OrdinalIgnoreCase.GetHashCode(path) & int.MaxValue) % CommitLocks.Length];

    /// <summary>Whether <paramref name="condition"/> holds for an object that <paramref name="exists"/> with version <paramref name="current"/> (read for <see cref="BundleWriteConditionKind.IfMatch"/> only).</summary>
    private static bool Holds(BundleWriteCondition condition, bool exists, BundleObjectVersion? current) => condition.Kind switch
    {
        BundleWriteConditionKind.None => true,
        BundleWriteConditionKind.MustNotExist => !exists,
        BundleWriteConditionKind.IfMatch => exists && current is { } c && condition.Expected is { } e && c == e,
        _ => throw new ArgumentOutOfRangeException(nameof(condition), condition.Kind, "unknown write condition"),
    };

    /// <summary>
    /// Resolves the parent directory of <paramref name="key"/> for a write: each segment matched
    /// exactly (<see cref="FindExactForWrite"/>, never from a scope's memo), each traversed
    /// directory STRICT-inspected before it is listed, each missing one created and then listed
    /// again to prove the volume stored it under exactly that name; finally the whole chain from
    /// the root and the object's own entry are STRICT-checked (<see cref="GuardStrict"/>), after
    /// any creation. Before anything is listed, every segment must be storable literally and the
    /// path as the OS would follow it passes the same STRICT check.
    /// </summary>
    private string ResolveParentForWrite(BundleKey key)
    {
        foreach (var segment in key.Segments)
        {
            if (CannotBeStoredLiterally(segment))
            {
                throw new BundleWriteConflictException(key, BundleWriteConflictReason.NameUnavailable);
            }
        }

        // Before any listing, the whole path as the OS would follow it: an entry on it that is a
        // link or cannot be inspected is refused as such, rather than surfacing as the raw
        // access-denied error of listing a directory that denies it (the early guard the writer
        // ran before #140).
        GuardStrict(key, Path.Join(_fullRoot, string.Join(Path.DirectorySeparatorChar, key.Segments)));
        var current = _fullRoot;
        for (var i = 0; i < key.Segments.Count - 1; i++)
        {
            GuardTraversed(key, current);
            var segment = key.Segments[i];
            var next = FindExactForWrite(key, current, segment);
            if (next is null)
            {
                next = Path.Join(current, segment);
                Directory.CreateDirectory(next);
                if (FindExactForWrite(key, current, segment) is null)
                {
                    throw new BundleWriteConflictException(key, BundleWriteConflictReason.NameUnavailable);
                }
            }
            else
            {
                GuardTraversed(key, next);
                if (!Directory.Exists(next))
                {
                    throw new BundleWriteConflictException(key, BundleWriteConflictReason.NameUnavailable); // a file where a directory is needed
                }
            }

            current = next;
        }

        ParentsResolvedForTest?.Invoke(current);

        // The chain again after any creation, and the object's own entry (the writer's late
        // guard before #140), immediately before the temporary is created.
        GuardStrict(key, Path.Join(current, key.Name));
        return current;
    }

    /// <summary>
    /// The parent directory of <paramref name="key"/> for a delete, matched exactly and guarded as
    /// for a write, or <see langword="null"/> when a segment is absent or is not a directory —
    /// then no object exists under the key. Creates nothing.
    /// </summary>
    private string? ResolveParentForDelete(BundleKey key)
    {
        var current = _fullRoot;
        for (var i = 0; i < key.Segments.Count - 1; i++)
        {
            GuardTraversed(key, current);
            var next = ListExact(current, key.Segments[i]);
            if (next is null)
            {
                return null;
            }

            GuardTraversed(key, next);
            if (!Directory.Exists(next))
            {
                return null;
            }

            current = next;
        }

        GuardStrict(key, current);
        return current;
    }

    /// <summary>The STRICT inspection of one traversed directory (the root excepted, as everywhere in the per-key guards).</summary>
    private void GuardTraversed(BundleKey key, string directory)
    {
        if (!string.Equals(directory, _fullRoot, PathComparison) && ReparsePoints.IsReparsePointOrUninspectable(directory))
        {
            throw new BundleAccessRefusedException(key, "it resolves through a reparse point or an entry that could not be inspected");
        }
    }

    /// <summary>An existing entry under the key: guarded like a read's leaf (<see cref="GuardResolved"/>), and a directory there is no object to replace.</summary>
    private void GuardObject(BundleKey key, string path)
    {
        GuardResolved(key, path);
        if (Directory.Exists(path))
        {
            throw new BundleWriteConflictException(key, BundleWriteConflictReason.NameUnavailable);
        }
    }

    /// <summary>
    /// The entry of <paramref name="directory"/> named exactly <paramref name="segment"/>
    /// (ordinal), or <see langword="null"/>; refuses with
    /// <see cref="BundleWriteConflictReason.NameUnavailable"/> a segment no entry can carry
    /// literally (<see cref="CannotBeStoredLiterally"/>) and a segment the OS resolves to an entry
    /// stored under another name — the volume folds case or Unicode form, so writing it would
    /// open or create that other entry. Never from a scope's memo, and a listing that fails
    /// throws: a write refuses what it cannot see, where a read answers "no such entry".
    /// </summary>
    private string? FindExactForWrite(BundleKey key, string directory, string segment)
    {
        if (CannotBeStoredLiterally(segment))
        {
            throw new BundleWriteConflictException(key, BundleWriteConflictReason.NameUnavailable);
        }

        var match = ListExact(directory, segment);
        if (match is null)
        {
            var path = Path.Join(directory, segment);
            if (File.Exists(path) || Directory.Exists(path))
            {
                // Another writer may have created exactly this name between the listing and the
                // lookup (two writers creating one directory or one object): list once more before
                // concluding that the volume folds the name onto another entry.
                match = ListExact(directory, segment);
                if (match is null)
                {
                    throw new BundleWriteConflictException(key, BundleWriteConflictReason.NameUnavailable);
                }
            }
        }

        return match;
    }

    /// <summary>Lists <paramref name="directory"/> in full, ignoring any scope, for the entry named exactly <paramref name="segment"/>; a failing listing throws.</summary>
    private string? ListExact(string directory, string segment)
    {
        Interlocked.Increment(ref _directoryListings);
        foreach (var full in Directory.EnumerateFileSystemEntries(directory))
        {
            if (string.Equals(Path.GetFileName(full), segment, StringComparison.Ordinal))
            {
                return full;
            }
        }

        return null;
    }

    /// <summary>A segment no entry can be named literally: <see cref="CannotBeLiteralName"/>, and on Windows a trailing dot or space, which the OS strips (<c>x.</c> would be stored as <c>x</c>).</summary>
    private static bool CannotBeStoredLiterally(string segment) =>
        CannotBeLiteralName(segment)
        || (OperatingSystem.IsWindows() && (segment.EndsWith('.') || segment.EndsWith(' ')));

    /// <summary>
    /// Creates the temporary beside the target, writes and flushes <paramref name="content"/>,
    /// closes it and returns its path; on any failure removes it (<see cref="DeleteTemporary"/>)
    /// and rethrows. See <see cref="Write"/> for the modes.
    /// </summary>
    private string WriteTemporary(string parent, string? existing, ReadOnlyMemory<byte> content)
    {
        var name = TemporaryNameForTest?.Invoke() ?? TemporaryPrefix + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)) + TemporarySuffix;
        var temporary = Path.Join(parent, name);
        // BufferSize 0: no managed buffer sits between Write and the file. With the default 4 KiB
        // buffer a small content is only buffered by Write, a disk-full / quota / I/O error then
        // surfaces at Flush, the buffer stays dirty, and the Dispose of the cleanup flushes again
        // and throws again before DeleteTemporary runs — the temporary leaks. Unbuffered, a write
        // error is raised by Write itself and Dispose has nothing left to flush.
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None, BufferSize = 0 };
        UnixFileMode? targetMode = null;
        if (existing is not null && !OperatingSystem.IsWindows())
        {
            targetMode = File.GetUnixFileMode(existing);
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        var stream = new FileStream(temporary, options);
        try
        {
            TemporaryCreatedForTest?.Invoke(temporary);
            stream.Write(content.Span);
            if (targetMode is { } mode && !OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(stream.SafeFileHandle, mode);
            }

            TemporaryFlushForTest?.Invoke(temporary);
            stream.Flush(flushToDisk: true);
            stream.Dispose();
        }
        catch
        {
            // Unconditional: a Dispose that fails (the success path's included) must not stop
            // the temporary's removal, nor replace the exception being reported.
            try
            {
                stream.Dispose();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }

            DeleteTemporary(temporary);
            throw;
        }

        return temporary;
    }

    /// <summary>
    /// Re-checks and commits under the object's lock (see <see cref="Write"/>): <see langword="true"/>
    /// once committed; <see langword="false"/>, with the temporary removed, when on Unix the object
    /// appeared since an unconditional creation was prepared — its temporary has a new file's mode
    /// where the object has its own, so <see cref="Write"/> prepares again as a replacement.
    /// </summary>
    private bool CommitTemporary(BundleKey key, string parent, string temporary, bool preparedAsReplacement, BundleObjectVersion written, BundleWriteCondition condition)
    {
        var settled = false;
        try
        {
            AfterTemporaryWrittenForTest?.Invoke(temporary);
            var target = Path.Join(parent, key.Name);
            for (var attempt = 1; ; attempt++)
            {
                if (attempt > 1)
                {
                    // Outside the lock: the other writers of this stripe are not held up.
                    Thread.Sleep(Random.Shared.Next(5, 26));
                }

                lock (CommitLockFor(target))
                {
                    CheckBoundary();
                    GuardStrict(key, temporary); // the temporary and every directory up to the root
                    var current = FindExactForWrite(key, parent, key.Name);
                    BundleObjectVersion? before = null;
                    if (current is not null)
                    {
                        GuardObject(key, current);
                        if (condition.Kind == BundleWriteConditionKind.IfMatch)
                        {
                            before = HashForWrite(current);
                        }
                    }

                    if (!Holds(condition, current is not null, before))
                    {
                        throw new BundleWriteConflictException(key);
                    }

                    if (current is not null && condition.Kind == BundleWriteConditionKind.None)
                    {
                        EnsureWritable(current);
                    }

                    if (current is not null && !preparedAsReplacement && !OperatingSystem.IsWindows())
                    {
                        return false;
                    }

                    BeforeCommitForTest?.Invoke(temporary, target);
                    try
                    {
                        if (CommitForTest is { } commit)
                        {
                            commit(temporary, target, current is not null);
                        }
                        else if (current is not null)
                        {
                            File.Replace(temporary, current, destinationBackupFileName: null);
                        }
                        else
                        {
                            File.Move(temporary, target, overwrite: false);
                        }
                    }
                    catch (IOException e) when (current is not null && RefusedBeforeAnyEffect(e))
                    {
                        // Nothing changed: the target and the temporary are as they were. Try
                        // again from the re-check, so that a condition another writer broke
                        // meanwhile is a conflict, never an overwrite; after the last attempt the
                        // temporary is removed below and the target is left as it is.
                        if (attempt == MaxCommitAttempts)
                        {
                            throw new BundleWriteNotCommittedException(key, e);
                        }

                        continue;
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                    {
                        switch (ClassifyCommitFailure(key, parent, temporary, current is not null, before, written, e))
                        {
                            case CommitFailure.NothingChanged:
                                throw; // the raw failure; the temporary is removed below
                            case CommitFailure.ChangedByAnother:
                                throw new BundleWriteConflictException(key);
                            case CommitFailure.Committed:
                                settled = true;
                                return true;
                            default:
                                settled = true; // keep the temporary: it may be the only copy of the new bytes
                                throw new BundleWriteOutcomeUnknownException(key, e);
                        }
                    }

                    settled = true;
                    return true;
                }
            }
        }
        finally
        {
            if (!settled)
            {
                DeleteTemporary(temporary);
            }
        }
    }

    /// <summary>How many times <see cref="CommitTemporary"/> makes a replacement that the storage refused without any effect (<see cref="RefusedBeforeAnyEffect"/>).</summary>
    private const int MaxCommitAttempts = 3;

    /// <summary>
    /// Whether a replacement call failed before it changed anything, so that the target and the
    /// temporary are untouched and the commit can be made again: Windows <c>ReplaceFile</c>'s
    /// <c>ERROR_UNABLE_TO_REMOVE_REPLACED</c> (1175 — "the replaced and replacement files retain
    /// their original file names"; measured while readers held the object open), or a sharing
    /// violation.
    /// </summary>
    private static bool RefusedBeforeAnyEffect(IOException e) =>
        e.HResult is unchecked((int)0x80070497) or unchecked((int)0x80070020);

    /// <summary>What a failed commit call left, as <see cref="ClassifyCommitFailure"/> establishes it.</summary>
    private enum CommitFailure
    {
        /// <summary>The temporary is still there and the target is as it was — for an unconditional replacement, whose target is not read, the temporary is still there: nothing of this write happened.</summary>
        NothingChanged,

        /// <summary>The temporary is still there and the target changed: another writer landed, ours did not.</summary>
        ChangedByAnother,

        /// <summary>The temporary is gone and the target holds the new bytes: the commit took effect.</summary>
        Committed,

        /// <summary>Anything else, a state that could not be read, or a path a renewed guard refuses.</summary>
        Unknown,
    }

    /// <summary>
    /// After the commit call threw, decides from what the storage now holds. Windows
    /// <c>ReplaceFile</c>'s <c>ERROR_UNABLE_TO_MOVE_REPLACEMENT</c> (1176) and
    /// <c>ERROR_UNABLE_TO_MOVE_REPLACEMENT_2</c> (1177) are unknown by definition — without a
    /// backup name the first can leave no object under the key and the new bytes under the
    /// temporary's name. Otherwise the confinement boundary and the STRICT guards the commit ran —
    /// the temporary and every directory up to the root, and the target's own entry — are renewed
    /// before anything is read: a guard that refuses, or a state that cannot be read, is
    /// <see cref="CommitFailure.Unknown"/> (the temporary is kept), never a success read through a
    /// path a link has entered since the commit's checks. That narrows the window between a check
    /// and an access; it does not close it (issue #178). Then a temporary still present means the
    /// rename did not happen: the target is as before (<see cref="CommitFailure.NothingChanged"/>)
    /// or changed by another writer (<see cref="CommitFailure.ChangedByAnother"/>) — except for an
    /// unconditional replacement (<paramref name="existed"/> without <paramref name="before"/>),
    /// whose target is not read, since its version was never needed and it may not be readable:
    /// nothing of that write happened. A temporary gone and a target holding the new bytes means
    /// the commit took effect; anything else is unknown. Reads only; never throws.
    /// </summary>
    private CommitFailure ClassifyCommitFailure(BundleKey key, string parent, string temporary, bool existed, BundleObjectVersion? before, BundleObjectVersion written, Exception failure)
    {
        if (failure.HResult is unchecked((int)0x80070498) or unchecked((int)0x80070499))
        {
            return CommitFailure.Unknown;
        }

        try
        {
            CheckBoundary();
            GuardStrict(key, temporary);
            GuardStrict(key, Path.Join(parent, key.Name));
            var temporaryLeft = File.Exists(temporary);
            if (temporaryLeft && existed && before is null)
            {
                return CommitFailure.NothingChanged;
            }

            var now = ListExact(parent, key.Name);
            BundleObjectVersion? after = now is null ? null : HashForRead(now);
            if (temporaryLeft)
            {
                return after == before ? CommitFailure.NothingChanged : CommitFailure.ChangedByAnother;
            }

            return after == written ? CommitFailure.Committed : CommitFailure.Unknown;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or BundleAccessRefusedException)
        {
            return CommitFailure.Unknown;
        }
    }

    /// <summary>
    /// Removes a temporary this provider created, only while nothing on its path has been replaced
    /// by a link: the boundary holds and neither the temporary nor any directory up to the root
    /// is a reparse point or uninspectable. Otherwise it is left where it is — removing it through
    /// a substituted link could remove another entry — and is an orphan. Never throws: it runs on
    /// a failure path whose own exception is the one to report.
    /// </summary>
    private void DeleteTemporary(string temporary)
    {
        try
        {
            CheckBoundary();
            if (ReparsePoints.HasReparsePointOrUninspectableAncestor(_fullRoot, temporary, PathComparison))
            {
                return;
            }

            File.Delete(temporary);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or BundleAccessRefusedException)
        {
            // left in place: an orphan (ListOrphanedTemporaries)
        }
    }

    /// <summary>
    /// The current version of an object an <see cref="BundleWriteConditionKind.IfMatch"/> write is
    /// about to replace, read through a handle opened for writing too: a target the process may
    /// not write (a read-only file, a mode or an ACL that denies writing, another program's handle
    /// that denies write sharing) is refused here, before the commit, exactly as
    /// <c>File.WriteAllText</c> refused it — a rename would otherwise replace it, since on Unix it
    /// needs only the directory's permission. A target the process may write but not read cannot
    /// satisfy <see cref="BundleWriteConditionKind.IfMatch"/>: its version cannot be established,
    /// and the write is refused with the same <see cref="UnauthorizedAccessException"/>. Nothing
    /// is written through this handle.
    /// </summary>
    private static BundleObjectVersion HashForWrite(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
        return new BundleObjectVersion(Convert.ToHexStringLower(SHA256.HashData(stream)));
    }

    /// <summary>
    /// What an unconditional replacement checks of its target (<see cref="BundleWriteConditionKind.None"/>):
    /// the right to write it, and only that — the handle is opened for writing alone and nothing
    /// is read, so a file the process may write but not read (mode 0200, an ACL denying reading)
    /// is replaced, as <c>File.WriteAllText</c> replaced it. A target the process may not write
    /// is refused exactly as <see cref="HashForWrite"/> refuses it; any failure to open the handle
    /// refuses the write, so an answer that cannot be established never lets it through.
    /// Nothing is written through this handle.
    /// </summary>
    private static void EnsureWritable(string path) =>
        File.OpenHandle(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete).Dispose();

    /// <summary>The current version of an object, read without blocking any other reader, writer or replacement.</summary>
    private static BundleObjectVersion HashForRead(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return new BundleObjectVersion(Convert.ToHexStringLower(SHA256.HashData(stream)));
    }

    /// <summary>After a write or a delete, drops the calling flow's memoised listings of every directory on the key's path, so that a later lookup in the same scope lists them again.</summary>
    private void ForgetListings(BundleKey key)
    {
        if (ActiveMemo is not { } memo)
        {
            return;
        }

        var current = _fullRoot;
        memo.Forget(current);
        for (var i = 0; i < key.Segments.Count - 1; i++)
        {
            current = Path.Join(current, key.Segments[i]);
            memo.Forget(current);
        }
    }

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

        /// <summary>Drops one directory's names (a write changed them); the next lookup lists it again.</summary>
        public void Forget(string directory)
        {
            lock (_gate)
            {
                if (_listings.Remove(directory, out var names))
                {
                    _held -= 1L + names.Length;
                }
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
    /// <see cref="ResolveExact"/>; re-walking the ancestors would only have narrowed it. The
    /// window spans the walk of the swapped directory's whole subtree. What can leak in it is
    /// names, lengths and modification times of the link's target, through
    /// <see cref="Enumerate"/> and <see cref="GetVersion"/>; never content, because every
    /// <see cref="Read"/> resolves the key again and re-inspects each of its path segments
    /// STRICTly.</para>
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
