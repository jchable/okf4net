// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net.Internal;

/// <summary>
/// Detects filesystem reparse points (symlinks, junctions, mount points)
/// using lstat-based semantics: it reports the type of the directory entry
/// ITSELF without following a symlink.
///
/// This is the semantics the bundle and index directory walks need: unlike
/// <c>Path.is_dir()</c>/<c>is_file()</c> (and .NET's
/// <see cref="Directory.Exists(string)"/>/<see cref="File.Exists(string)"/>),
/// which resolve through any symlink to the type of its target, a
/// reparse-point entry is treated as neither a plain file nor a plain
/// directory, so it is skipped rather than traversed or collected.
///
/// On Windows, <see cref="File.GetAttributes(string)"/> reproduces this
/// (Win32 <c>GetFileAttributes</c> reports the entry's own
/// <see cref="FileAttributes.ReparsePoint"/> without following it). On Unix,
/// however, <c>File.GetAttributes</c> resolves THROUGH a symlink (stat, not
/// lstat), so a symlink to a directory reports as a plain directory with no
/// reparse flag; <see cref="IsReparsePoint"/> therefore falls back to
/// <see cref="FileSystemInfo.LinkTarget"/>, which reads the entry itself and
/// is non-null exactly for a link, on every platform.
/// </summary>
internal static class ReparsePoints
{
    /// <summary>
    /// What a single, non-following look at one directory entry found. Kept
    /// as four states rather than a bool because the two predicates below
    /// agree on three of them and deliberately disagree on the fourth.
    /// </summary>
    private enum EntryState
    {
        /// <summary>No entry exists at the path (nor at one of its parents, nor a volume or share to hold it).</summary>
        Absent,

        /// <summary>A plain file or directory, not itself a link.</summary>
        Plain,

        /// <summary>A symlink, junction, mount point or other reparse point.</summary>
        ReparsePoint,

        /// <summary>
        /// The entry could not be looked at: an <see cref="UnauthorizedAccessException"/>
        /// or an <see cref="IOException"/> other than not-found. Whether it is
        /// a link is unknown.
        /// </summary>
        Uninspectable,
    }

    /// <summary>
    /// <c>true</c> if <paramref name="path"/> is itself a reparse point
    /// (symlink, junction, ...), without following it. Returns <c>false</c>
    /// (rather than throwing) if the attributes cannot be read -- e.g. a path
    /// that vanished between enumeration and this check, since such a race
    /// means there's no entry left to skip, or an entry the current user may
    /// not inspect.
    /// </summary>
    /// <remarks>
    /// The LENIENT predicate: for a WALK, which enumerates entries and skips
    /// the links among them (<c>Bundle</c>'s and <c>IndexGenerator</c>'s
    /// <c>CollectMarkdown</c>, <c>IndexGenerator</c>'s per-child skip) or
    /// reports on what it sees (<c>Bundle.TryResolveResource</c>'s status,
    /// which <c>okf validate</c> reports). That status also precedes a read,
    /// so the read re-checks strictly instead: <c>Bundle.ReadResourceText</c>
    /// refuses a resolved path that is, or sits below, a reparse point or an
    /// uninspectable entry, and what validation reports stays unchanged. A
    /// walk fails OPEN on an
    /// entry it cannot inspect because refusing there changes what a bundle
    /// loads or what <c>okf validate</c> reports, for a failure that is the
    /// filesystem's, not the bundle's. A GUARD -- code that refuses or allows
    /// a write, a delete or an outward read -- must not use this: an entry
    /// whose link status it cannot read is exactly the entry it cannot vouch
    /// for, and a junction carrying a deny-ReadAttributes ACE (with its
    /// parent denying listing) is still traversed by the OS on the write that
    /// follows. Guards use <see cref="IsReparsePointOrUninspectable"/>.
    /// </remarks>
    internal static bool IsReparsePoint(string path) => Inspect(path) == EntryState.ReparsePoint;

    /// <summary>
    /// <c>true</c> if <paramref name="path"/> is itself a reparse point, OR
    /// if whether it is one cannot be determined (an
    /// <see cref="UnauthorizedAccessException"/>, or an <see cref="IOException"/>
    /// other than not-found, while reading it). <c>false</c> for a plain file
    /// or directory, and for an entry that does not exist -- a file about to
    /// be created, or a directory about to be created above it.
    /// </summary>
    /// <remarks>
    /// The STRICT predicate, for GUARDS: code that refuses or allows a write,
    /// a delete or an outward read through <paramref name="path"/>. A guard
    /// fails CLOSED, because the cost is asymmetric: refusing an entry it
    /// could not inspect costs a clear error, while allowing it lets the OS
    /// follow whatever that entry turns out to be. The escape this closes was
    /// executed, not hypothesised: a junction carrying a deny-ReadAttributes
    /// ACE, under a parent denying listing, makes
    /// <see cref="File.GetAttributes(string)"/> throw
    /// <see cref="UnauthorizedAccessException"/> (its <c>FindFirstFile</c>
    /// fallback needs the parent's listing) while a write still traverses the
    /// junction -- and <see cref="IsReparsePoint"/>, answering "not a link",
    /// let <c>okf-render</c> write outside <c>--out</c>.
    ///
    /// Not-found is NOT uninspectable, on purpose: every write guard checks
    /// entries that do not exist yet, and refusing those would refuse every
    /// new file. Both <see cref="FileNotFoundException"/> (a missing leaf) and
    /// <see cref="DirectoryNotFoundException"/> (a missing parent, or a parent
    /// that is a regular file) map to <c>false</c>; measured on Windows and on
    /// Linux (.NET 10), where both throw exactly those two for those cases.
    /// In the Linux case measured, an uninspectable entry (<c>EACCES</c>) is
    /// one whose parent denies search, so the write that follows fails too;
    /// the two predicates differ there only in which error the caller reports.
    ///
    /// Absence counts even when Windows reports it as a plain
    /// <see cref="IOException"/> rather than a not-found type: a drive that
    /// holds no volume (<c>ERROR_NOT_READY</c>, e.g. an empty card reader or
    /// optical drive), a share that does not exist
    /// (<c>ERROR_BAD_NET_NAME</c>) and a server that cannot be found
    /// (<c>ERROR_BAD_NETPATH</c>) -- all measured. Nothing exists at such a
    /// path and nothing can be created there, so it is not an entry anyone
    /// could redirect; refusing it would only replace the caller's own,
    /// accurate I/O error (for <c>okf-render --out F:\site</c>, "the device is
    /// not ready") with a false guard diagnosis.
    ///
    /// Everything else that is not a not-found is uninspectable, including
    /// failures that have nothing to do with links: an ACL that denies
    /// reading attributes on a plain directory, an invalid name (Windows
    /// <c>nul</c>, <c>a&lt;b</c>) or an over-long component. A guard refuses
    /// those too -- it cannot tell them from a link it may not read -- which
    /// is why a guard refusal that names a reparse point also says "or an
    /// entry that could not be inspected" rather than naming a link it has
    /// not seen.
    ///
    /// Walks keep <see cref="IsReparsePoint"/> -- see its remarks for why.
    /// </remarks>
    internal static bool IsReparsePointOrUninspectable(string path) => Inspect(path) is EntryState.ReparsePoint or EntryState.Uninspectable;

    /// <summary>
    /// Looks at <paramref name="path"/> once, without following it, and
    /// classifies what it found. Never throws for an I/O or access failure --
    /// it classifies it -- so the lenient and strict predicates cannot
    /// disagree about anything except <see cref="EntryState.Uninspectable"/>.
    /// </summary>
    private static EntryState Inspect(string path)
    {
        try
        {
            // Fast path: on Windows a symlink/junction/mount point sets the
            // ReparsePoint attribute, and File.GetAttributes reports the entry
            // itself (Win32 GetFileAttributes semantics).
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            {
                return EntryState.ReparsePoint;
            }

            // lstat-correct cross-platform fallback. On Unix, File.GetAttributes
            // resolves THROUGH a symlink (stat, not lstat), so a symlink to a
            // directory reports as a plain Directory with no ReparsePoint flag --
            // which would let a symlinked subdirectory be walked/indexed as if
            // it were a real directory. FileSystemInfo.LinkTarget reads the
            // entry itself and is non-null exactly when the entry is a link,
            // on every platform.
            return new DirectoryInfo(path).LinkTarget is not null || new FileInfo(path).LinkTarget is not null
                ? EntryState.ReparsePoint
                : EntryState.Plain;
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException || IsAbsentVolumeOrShare(e))
        {
            return EntryState.Absent;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return EntryState.Uninspectable;
        }
    }

    /// <summary>HRESULT of a Win32 <c>ERROR_NOT_READY</c> (21): the drive holds no volume.</summary>
    private const int HResultNotReady = unchecked((int)0x80070015);

    /// <summary>HRESULT of a Win32 <c>ERROR_BAD_NETPATH</c> (53): the network path (server) was not found.</summary>
    private const int HResultBadNetPath = unchecked((int)0x80070035);

    /// <summary>HRESULT of a Win32 <c>ERROR_BAD_NET_NAME</c> (67): the network name (share) was not found.</summary>
    private const int HResultBadNetName = unchecked((int)0x80070043);

    /// <summary>
    /// <c>true</c> for the Windows I/O errors that mean "nothing is there" but
    /// arrive as a plain <see cref="IOException"/> -- see
    /// <see cref="IsReparsePointOrUninspectable"/>'s remarks. Matched on the
    /// exact type, so no <see cref="IOException"/> subclass carrying one of
    /// these codes for another reason is swept in.
    /// </summary>
    private static bool IsAbsentVolumeOrShare(Exception e) =>
        e.GetType() == typeof(IOException) && e.HResult is HResultNotReady or HResultBadNetPath or HResultBadNetName;

    /// <summary>
    /// Resolves <paramref name="root"/> to a full path with any trailing
    /// directory separator trimmed. <see cref="Path.GetFullPath(string)"/>
    /// alone preserves a trailing separator if the input has one -- e.g.
    /// <c>"/foo"</c> and <c>"/foo/"</c> survive it as distinct strings -- but
    /// every walk in this class stops via EXACT STRING EQUALITY against an
    /// ancestor produced by <see cref="Path.GetDirectoryName(string)"/>,
    /// which never carries a trailing separator. An untrimmed root therefore
    /// never matches, and the walk overshoots past the intended root into
    /// the real filesystem above it -- on macOS, for example, reaching the
    /// genuine <c>/var</c> symlink and rejecting an entirely valid write.
    /// Every caller that computes its own root for a reparse-point or
    /// containment check against this class must resolve it through here,
    /// not through a bare <see cref="Path.GetFullPath(string)"/>.
    /// </summary>
    internal static string CanonicalizeRoot(string root) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));

    /// <summary>
    /// <c>true</c> if <paramref name="path"/> itself, or any directory strictly
    /// between it and <paramref name="root"/>, is a filesystem reparse point --
    /// checked via <see cref="IsReparsePoint"/>, which reports each entry's own
    /// type without following it. Both <paramref name="root"/> and
    /// <paramref name="path"/> are expected to already be resolved (typically
    /// via <see cref="Path.GetFullPath(string)"/>) by the caller; this method
    /// performs no canonicalization of its own.
    /// </summary>
    /// <remarks>
    /// The LENIENT walk: an entry whose link status cannot be read counts as
    /// "not a link", so this is for callers that fail open (see
    /// <see cref="IsReparsePoint"/>'s remarks). A guard uses
    /// <see cref="HasReparsePointOrUninspectableAncestor(string, string, StringComparison)"/>,
    /// the same walk over the strict predicate.
    /// </remarks>
    /// <param name="root">
    /// The walk's upper bound. Deliberately never inspected by this walk --
    /// the loop's exit test (<paramref name="rootComparison"/>) stops before
    /// <see cref="IsReparsePoint"/> is ever called on <paramref name="root"/>
    /// itself -- callers that need <paramref name="path"/> itself checked
    /// (e.g. an existing concept file that may itself be a planted symlink)
    /// must call <see cref="IsReparsePoint"/> on it separately.
    /// </param>
    /// <param name="path">The starting point of the upward walk.</param>
    /// <param name="rootComparison">
    /// The comparison used to detect that the walk has reached
    /// <paramref name="root"/>. Every current caller of this walk and of its
    /// strict twin passes <see cref="StringComparison.Ordinal"/> uniformly --
    /// the 2-arg overloads, <c>IndexGenerator</c>'s private wrapper,
    /// <c>FileMemoryStore.PathComparison</c>, <c>CatalogPathResolver</c>'s
    /// <c>ContainmentComparison</c> and <c>HtmlWriter.GuardWithinOutputDirectory</c>
    /// all do. The parameter stays explicit rather than hardcoding
    /// <c>Ordinal</c> internally, keeping this comparison a visible,
    /// independently testable seam instead of an implicit assumption.
    /// </param>
    /// <returns>
    /// <see langword="true"/> if a reparse point was found; otherwise
    /// <see langword="false"/> (including if the walk runs past the
    /// filesystem root without ever reaching <paramref name="root"/>, which
    /// stops the walk rather than looping forever).
    /// </returns>
    internal static bool HasReparsePointAncestor(string root, string path, StringComparison rootComparison) =>
        AnyEntryUpTo(root, path, rootComparison, IsReparsePoint);

    /// <summary>
    /// The STRICT twin of <see cref="HasReparsePointAncestor(string, string, StringComparison)"/>:
    /// the same walk, with the same bounds, over
    /// <see cref="IsReparsePointOrUninspectable"/> -- <c>true</c> as soon as
    /// <paramref name="path"/> itself, or any directory strictly between it
    /// and <paramref name="root"/>, is a reparse point OR cannot be inspected.
    /// A directory that does not exist yet does not count. For guards, which
    /// fail closed -- see <see cref="IsReparsePointOrUninspectable"/>'s remarks.
    /// </summary>
    internal static bool HasReparsePointOrUninspectableAncestor(string root, string path, StringComparison rootComparison) =>
        AnyEntryUpTo(root, path, rootComparison, IsReparsePointOrUninspectable);

    /// <summary>
    /// The walk shared by the lenient and strict ancestor checks, so the two
    /// can differ only in <paramref name="matches"/>, never in their bounds.
    /// </summary>
    private static bool AnyEntryUpTo(string root, string path, StringComparison rootComparison, Func<string, bool> matches)
    {
        var current = path;

        while (!string.Equals(current, root, rootComparison))
        {
            if (matches(current))
            {
                return true;
            }

            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent) || string.Equals(parent, current, StringComparison.Ordinal))
            {
                // Walked past the filesystem root without ever reaching
                // root -- callers already guard containment separately, but
                // stop here rather than loop forever.
                break;
            }

            current = parent;
        }

        return false;
    }

    /// <summary>
    /// <c>true</c> if <paramref name="path"/> itself, or any directory
    /// strictly between it and <paramref name="bundleRoot"/>, is a
    /// filesystem reparse point (symlink, junction, mount point) -- resolves
    /// <paramref name="bundleRoot"/> via <see cref="CanonicalizeRoot"/> (see
    /// its remarks for why a bare <see cref="Path.GetFullPath(string)"/>
    /// is not enough here), then delegates to
    /// <see cref="HasReparsePointAncestor(string, string, StringComparison)"/>
    /// with <see cref="StringComparison.Ordinal"/>. Complements
    /// <see cref="IsWithinBundleRoot"/>: that check only compares resolved
    /// path STRINGS, so a junction that lexically resolves under
    /// <paramref name="bundleRoot"/> still passes it even though the OS
    /// follows the junction the moment something actually touches disk
    /// (<see cref="Directory.Exists(string)"/>,
    /// <see cref="File.ReadAllText(string)"/>, <see cref="File.WriteAllText(string, string)"/>)
    /// -- silently reading or writing outside the bundle. Walking every
    /// intermediate directory and rejecting on the first reparse point closes
    /// that gap. Never inspects <paramref name="path"/> itself -- a caller
    /// whose target could itself be a planted file symlink (not just an
    /// ancestor directory) must separately check <see cref="IsReparsePoint"/>
    /// on it.
    /// </summary>
    /// <remarks>
    /// <see cref="StringComparison.Ordinal"/> on every platform, not an
    /// OS-conditional choice -- case-sensitivity is a runtime property of the
    /// specific volume, not of the OS: APFS/HFS+ can be configured
    /// case-sensitive, and a volume mounted on Linux can be case-insensitive
    /// (FAT/exFAT, a case-folding network share). Every legitimate caller's
    /// <paramref name="path"/> is built via <see cref="Path.Combine(string, string)"/>
    /// from the same <paramref name="bundleRoot"/> passed to this method, so
    /// its prefix always keeps <paramref name="bundleRoot"/>'s exact casing --
    /// <c>Ordinal</c> costs nothing for legitimate input, and closes the same
    /// case-variant escape <see cref="IsWithinBundleRoot"/> closes.
    /// </remarks>
    internal static bool HasReparsePointAncestor(string bundleRoot, string path)
    {
        var fullRoot = CanonicalizeRoot(bundleRoot);
        var current = Path.GetFullPath(path);
        return HasReparsePointAncestor(fullRoot, current, StringComparison.Ordinal);
    }

    /// <summary>
    /// The STRICT twin of <see cref="HasReparsePointAncestor(string, string)"/>:
    /// the same root canonicalization and <see cref="StringComparison.Ordinal"/>
    /// walk, delegating to
    /// <see cref="HasReparsePointOrUninspectableAncestor(string, string, StringComparison)"/>.
    /// For guards, which fail closed -- see
    /// <see cref="IsReparsePointOrUninspectable"/>'s remarks.
    /// </summary>
    internal static bool HasReparsePointOrUninspectableAncestor(string bundleRoot, string path)
    {
        var fullRoot = CanonicalizeRoot(bundleRoot);
        var current = Path.GetFullPath(path);
        return HasReparsePointOrUninspectableAncestor(fullRoot, current, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>true</c> if <paramref name="path"/> is <paramref name="root"/>
    /// itself or a descendant of it, comparing the two path strings with
    /// <paramref name="comparison"/>. Both parameters are expected to already
    /// be resolved (typically via <see cref="Path.GetFullPath(string)"/>) by
    /// the caller; this method performs no canonicalization of its own.
    /// </summary>
    /// <remarks>
    /// <paramref name="comparison"/> is entirely the caller's choice, and the
    /// right choice depends on the call site's POLARITY -- what a "true"
    /// answer authorizes. Derive it per call site; do not copy a sibling
    /// guard's value, because two guards a few lines apart can legitimately
    /// need opposite answers.
    /// <list type="bullet">
    /// <item><description>
    /// A guard that REFUSES when the answer is <c>true</c> (an "is this
    /// forbidden territory?" check) fails safe by over-matching, so
    /// <c>OrdinalIgnoreCase</c> is its safe direction: on a case-INSENSITIVE
    /// volume a case-variant spelling is the same physical directory, and
    /// <c>Ordinal</c> would miss it and permit what should be refused. Cost of
    /// over-refusing is a clear error the caller can work around.
    /// </description></item>
    /// <item><description>
    /// A guard that ALLOWS when the answer is <c>true</c> (an "is this inside
    /// my sandbox?" check) fails safe by under-matching, so
    /// <see cref="StringComparison.Ordinal"/> is its safe direction: on a
    /// case-SENSITIVE volume <c>/tmp/Out</c> and <c>/tmp/out</c> are genuinely
    /// different directories, and <c>OrdinalIgnoreCase</c> would call an
    /// escaping path a prefix-match and authorize a write outside the sandbox.
    /// </description></item>
    /// </list>
    /// Both polarities exist in this repo, deliberately:
    /// <see cref="IsWithinBundleRoot"/>, <c>Bundle.cs</c> and
    /// <c>CatalogPathResolver</c> gate inclusion and pass <c>Ordinal</c>;
    /// <c>HtmlWriter.GuardOutputDirectory</c> gates refusal and passes
    /// <c>OrdinalIgnoreCase</c>, while <c>HtmlWriter</c>'s sibling
    /// <c>GuardWithinOutputDirectory</c> gates permission a dozen lines later
    /// and passes <c>Ordinal</c>. That second guard originally inherited
    /// <c>OrdinalIgnoreCase</c> from the first without re-deriving the
    /// polarity, and shipped an escape past a review that had explicitly
    /// blessed the first guard's choice -- which is why this is stated as a
    /// rule about polarity rather than a per-caller convention.
    /// </remarks>
    internal static bool IsWithin(string root, string path, StringComparison comparison)
    {
        if (string.Equals(root, path, comparison))
        {
            return true;
        }

        var rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        return path.StartsWith(rootWithSeparator, comparison);
    }

    /// <summary>
    /// <c>true</c> if <paramref name="candidate"/> is <paramref name="root"/>
    /// itself or a descendant of it, resolving <paramref name="root"/> via
    /// <see cref="CanonicalizeRoot"/> (so <paramref name="candidate"/> equal
    /// to <paramref name="root"/> itself still matches <see cref="IsWithin"/>'s
    /// exact-equality check even when <paramref name="root"/> has a trailing
    /// separator -- see <see cref="CanonicalizeRoot"/>'s remarks) and
    /// comparing with <see cref="StringComparison.Ordinal"/> on every
    /// platform. A lexical check alone: a junction/symlink among
    /// <paramref name="candidate"/>'s ancestors can still resolve here even
    /// though the OS would follow it to somewhere else entirely once actual
    /// I/O touches disk -- pair with <see cref="HasReparsePointAncestor(string, string)"/>
    /// for that.
    /// </summary>
    /// <remarks>
    /// Case-sensitivity is a runtime property of the specific volume, not of
    /// the OS: APFS/HFS+ can be configured case-sensitive, and a volume
    /// mounted on Linux can be case-insensitive (FAT/exFAT, a case-folding
    /// network share) -- an OS-conditional comparison leaves an escape open
    /// on exactly the combination it assumes cannot occur.
    /// <see cref="StringComparison.Ordinal"/> has no cost for legitimate
    /// input: every legitimate <paramref name="candidate"/> is built via
    /// <see cref="Path.Combine(string, string)"/> from the same
    /// <paramref name="root"/> passed to this method (a purely lexical
    /// operation that never re-cases from disk), so its prefix always keeps
    /// <paramref name="root"/>'s exact casing. Only a <c>..</c> climb
    /// re-entering a case-variant sibling of <paramref name="root"/> itself
    /// produces a mismatched prefix -- precisely the escape this method must
    /// reject.
    /// </remarks>
    internal static bool IsWithinBundleRoot(string root, string candidate)
    {
        var fullRoot = CanonicalizeRoot(root);
        var fullCandidate = Path.GetFullPath(candidate);
        return IsWithin(fullRoot, fullCandidate, StringComparison.Ordinal);
    }

    /// <summary>
    /// Resolves <paramref name="path"/> to the real location the OS would
    /// land on once it actually touches disk, by walking upward from
    /// <paramref name="path"/> (inclusive) for the nearest ancestor that is
    /// itself a filesystem reparse point (symlink, junction, mount point),
    /// resolving that ancestor to its final target via
    /// <see cref="Directory.ResolveLinkTarget(string, bool)"/>, and
    /// re-attaching whatever trailing path segments do not exist yet.
    /// <paramref name="resolved"/> is <paramref name="path"/> unchanged if no
    /// ancestor up to the filesystem root is a reparse point -- the common
    /// case, and the only one <see cref="Path.GetFullPath(string)"/> alone
    /// can see.
    /// </summary>
    /// <returns>
    /// <see langword="false"/> -- with <paramref name="resolved"/> set to
    /// <paramref name="path"/>, which the caller must not trust -- if an entry
    /// on the walk could not be inspected, or a link on it could not be
    /// followed (<see cref="Directory.ResolveLinkTarget(string, bool)"/> threw:
    /// a junction whose attributes are readable but whose target is not) (see
    /// <see cref="IsReparsePointOrUninspectable"/>): whether it redirects the
    /// path, and where to, is unknown. This resolution backs a GUARD
    /// (<c>HtmlWriter.GuardOutputDirectory</c>), so it fails closed and lets
    /// the caller refuse, rather than reporting the lexical path as if the
    /// walk had proven no redirect. An entry that does not exist yet is not
    /// uninspectable; the walk continues past it. Otherwise
    /// <see langword="true"/>.
    /// </returns>
    /// <remarks>
    /// Bounded by <paramref name="path"/>'s own ancestor depth, not by any
    /// caller-supplied root: unlike <see cref="HasReparsePointAncestor(string, string)"/>
    /// (which walks a candidate KNOWN to be lexically nested under a root,
    /// and can safely stop there), a path under attack here is not
    /// necessarily lexically nested under anything at all -- that is the
    /// whole point of the bypass this exists to catch -- so there is no
    /// shorter bound to walk to than "however deep <paramref name="path"/>'s
    /// own path is". Deliberately does not chase a SECOND reparse point that
    /// might appear further up past the first one resolved: any escape
    /// reachable only through a reparse point nested INSIDE the resolved
    /// location is a caller-side containment check's concern (e.g.
    /// <c>HtmlWriter.GuardWithinOutputDirectory</c>, which walks every
    /// intermediate directory between an output root and each file actually
    /// written), not this one-shot resolution's.
    ///
    /// Only an UNINSPECTABLE entry fails the resolution. A reparse point that
    /// is inspectable but resolves to no link target (a reparse tag that is
    /// neither a symlink nor a junction, which sets the attribute without
    /// being a link .NET can follow) still falls back to the
    /// lexical path, as before: that is a known entry, not an unknown one, and
    /// refusing it would refuse any output directory beneath such an entry.
    ///
    /// Moved here from <c>OKF4net.Viewer</c>'s <c>HtmlWriter</c> (originally
    /// private there) so it has one home shared across callers instead of a
    /// leaf-local copy; <c>OKF4net.Viewer</c> reaches it via
    /// <c>InternalsVisibleTo</c>. <c>BundleConceptWriter</c>'s lock-keying
    /// gap (<see href="https://github.com/jchable/okf4net/issues/86">#86</see>)
    /// can call this same method rather than growing its own copy, once that
    /// fix is designed -- noted here as a pointer only, not acted on by this
    /// change.
    /// </remarks>
    internal static bool TryResolveThroughReparsePoints(string path, out string resolved)
    {
        var current = path;
        var tail = new List<string>();
        resolved = path;

        while (true)
        {
            var state = Inspect(current);
            if (state == EntryState.Uninspectable)
            {
                return false;
            }

            if (state == EntryState.ReparsePoint)
            {
                FileSystemInfo? resolvedTarget;
                try
                {
                    resolvedTarget = Directory.ResolveLinkTarget(current, returnFinalTarget: true);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // A link whose attributes are readable but whose target
                    // cannot be read (measured: a junction carrying a
                    // deny-ReadAttributes ACE under a listable parent). Where
                    // it leads is unknown -- the same answer as an
                    // uninspectable entry, so the same refusal, not a throw
                    // out of a Try method.
                    return false;
                }

                if (resolvedTarget is null)
                {
                    // A reparse point with no link target (see remarks):
                    // resolution is not this method's only line of defense,
                    // so fall back to the lexical path rather than throwing.
                    return true;
                }

                var target = resolvedTarget.FullName;
                for (var i = tail.Count - 1; i >= 0; i--)
                {
                    target = Path.Combine(target, tail[i]);
                }

                resolved = target;
                return true;
            }

            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent) || string.Equals(parent, current, StringComparison.Ordinal))
            {
                return true; // Reached the filesystem root without finding a reparse point.
            }

            tail.Add(Path.GetFileName(current));
            current = parent;
        }
    }
}
