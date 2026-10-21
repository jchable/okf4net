// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Collections.Concurrent;
using OKF4net.Internal;
using OKF4net.Providers;
using OKF4net.Yaml;

namespace OKF4net;

/// <summary>One concept stamped by <see cref="BundleConceptWriter.RecordVerifications"/>.</summary>
/// <param name="ConceptId">The concept that was stamped.</param>
/// <param name="At">
/// The timestamp written. Callers could format their own — the CLI and the
/// Agents layer both see <c>OkfTimestamp</c> through <c>InternalsVisibleTo</c> —
/// but two clocks are one too many: only the writer holds the seam tests pin,
/// so it reports what it wrote.
/// </param>
/// <param name="ReplacedAt">The superseded <c>at</c>, or null when the stamp is new.</param>
public readonly record struct VerificationRecord(string ConceptId, string At, string? ReplacedAt);

/// <summary>
/// The outcome of <see cref="BundleConceptWriter.RecordVerifications"/>:
/// errors-as-data, never thrown.
///
/// <b>Read <see cref="Records"/>, not just <see cref="Recorded"/>.</b> Every
/// concept is validated before the first byte is written, so a rejected batch
/// — unknown id, malformed actor, non-conformant document — writes nothing.
/// But writing several files cannot be atomic: if the third write fails on
/// I/O, the first two are already on disk. <see cref="Recorded"/> is then
/// false while <see cref="Records"/> lists the concepts whose write returned
/// successfully, and <see cref="Message"/> names them.
///
/// <b>The precise scope of that list.</b> An entry means "this concept's write
/// was committed", which is stronger than "was validated" (the reason
/// <c>Records</c> is built in the write loop, not the prepare loop). Each
/// write is atomic per object (<see cref="Providers.IBundleProvider.Write"/>,
/// issue #157): the concept the batch stopped on holds its whole previous
/// content, unless the provider reported an outcome it could not establish
/// (<see cref="Providers.BundleWriteOutcomeUnknownException"/>), which the
/// message then says. The honest reading of a failed batch is: the concepts
/// listed are stamped, the one it stopped on is named in the message, and the
/// ones after it were not attempted — re-run the batch, which is safe: a stamp
/// already there is replaced, never duplicated.
///
/// <b>A write notification that fails</b> (the <c>onWriteCommitted</c> callback
/// throwing an exception the writer converts: an I/O error, an <see cref="OkfException"/>,
/// a conflict raised by another operation included) is not a failed write: the
/// concept is on disk, so it is in <see cref="Records"/>, and the batch goes on.
/// <see cref="Recorded"/> stays true when every stamp is on disk; <see cref="Message"/>
/// is then the confirmation followed by <c>; the write notification failed: …</c>.
/// An exception the writer does not convert (an <see cref="OperationCanceledException"/>,
/// for one) propagates once that concept is on disk, and the batch ends there.
/// </summary>
/// <param name="Recorded">Whether every stamp of the batch is on disk.</param>
/// <param name="Message">A confirmation (saying so when a write notification failed), or what went wrong and how far it got.</param>
/// <param name="Records">One entry per concept actually stamped, in the order given.</param>
public readonly record struct VerificationOutcome(bool Recorded, string Message, IReadOnlyList<VerificationRecord> Records)
{
    private readonly BundleWriteReport? _report;

    /// <summary>
    /// What the batch wrote, object by object (spec §1, §4): the keys of the concepts whose write
    /// was committed, in order — the concepts of <see cref="Records"/>, a concept whose write
    /// notification then failed included — and, when a write failed,
    /// that concept's key with the reason (<see cref="Message"/> without its <c>Error: </c> prefix
    /// and without the <c>— already written: …</c> suffix; for a typed provider refusal a library
    /// sentence naming keys and types, for a raw I/O failure the runtime's own message, which can
    /// name a host path, until issue #211). Empty for a batch refused before its
    /// first write; the concepts after a failed one were not attempted and are in neither list.
    /// Never <see langword="null"/>, <see langword="default"/> included.
    /// </summary>
    public BundleWriteReport Report
    {
        get => _report ?? BundleWriteReport.Empty;
        init => _report = value;
    }
}

/// <summary>
/// What is wrong with one id passed to <see cref="BundleConceptWriter.CheckVerificationTargets"/>.
/// </summary>
internal enum VerificationTargetProblemKind
{
    /// <summary><see cref="ConceptId.TryParse(string, out ConceptId?)"/> rejects it, it is the reserved
    /// <c>index</c>/<c>log</c> name, the writer's key policy refuses its key -- see
    /// <see cref="BundleConceptWriter"/>'s private <c>ValidateConceptTarget</c> -- or the provider refuses
    /// its path (a reparse point, an entry that could not be inspected).</summary>
    InvalidId,

    /// <summary>The id resolves to a well-formed path, but no file exists there with that EXACT
    /// case for every segment — an id differing only in case from the real on-disk entry (e.g.
    /// <c>METRICS/DAU</c> naming an on-disk <c>metrics/dau.md</c>) is, correctly, a DIFFERENT,
    /// nonexistent concept id, matching what <c>bundle.Get</c> (ordinal <see cref="ConceptId"/>
    /// equality against ids <see cref="Bundle.Load(string)"/> builds from the real directory
    /// listing) has always answered — not a case-insensitive alias for it.</summary>
    NotFound,

    /// <summary>The file exists (with the right case) but its content is not a well-formed OKF
    /// document (<see cref="DocumentParseException"/> or a strict-UTF-8 decode failure).</summary>
    ParseFailure,

    /// <summary>The file exists but could not be read — an <see cref="IOException"/> (e.g. held
    /// open exclusively by another process) or <see cref="UnauthorizedAccessException"/>.</summary>
    Unreadable,

    /// <summary>The document parses but has no non-empty <c>type</c> — §11's floor.</summary>
    NotConformant,

    /// <summary>Two or more ids in the batch resolve to the same file.</summary>
    DuplicateName,
}

/// <summary>
/// One offending id found by <see cref="BundleConceptWriter.CheckVerificationTargets"/>, carrying
/// enough for a caller to phrase its own message — see that method's remarks for why this returns
/// only the FIRST offender rather than every problem in the batch.
/// </summary>
/// <param name="Kind">What is wrong with <paramref name="ConceptId"/>.</param>
/// <param name="ConceptId">The offending id, exactly as given by the caller (not normalized).</param>
/// <param name="Detail">
/// The specific reason, for kinds whose wording varies by cause: <c>BundleConceptWriter</c>'s
/// private <c>ValidateConceptTarget</c>'s own message, or its message for a path the provider refuses, for <see cref="VerificationTargetProblemKind.InvalidId"/>, or the underlying
/// exception's message for <see cref="VerificationTargetProblemKind.ParseFailure"/> and
/// <see cref="VerificationTargetProblemKind.Unreadable"/>. Captured once, here, rather than
/// re-derived by a renderer — re-running a disk check a second time to recover a string it already
/// had is both wasted I/O and, for a reparse-point check, a second window for the answer to change
/// underneath it. <see langword="null"/> for every other kind.
/// </param>
internal readonly record struct VerificationTargetProblem(VerificationTargetProblemKind Kind, string ConceptId, string? Detail = null)
{
    /// <summary>
    /// <see cref="Detail"/> terminated by EXACTLY ONE period — the single rule
    /// for the two renderers whose house style is a period-ended sentence
    /// (<see cref="BundleConceptWriter"/>'s own <c>FormatVerificationTargetProblem</c>
    /// and the <c>okf_verify</c> tool; the <c>okf verify</c> CLI verb terminates
    /// nothing and interpolates <see cref="Detail"/> directly).
    ///
    /// Neither "always append" nor "never append" is right, which is why this
    /// exists rather than a literal <c>.</c> at each call site: an
    /// <see cref="IOException"/>'s message always ends in a period already, so
    /// appending one produced a doubled <c>..</c> on every unreadable-concept
    /// refusal, while a library-authored parser message (<c>"… are not
    /// supported by the OKF YAML subset"</c>) never ends in one and needs it.
    /// </summary>
    internal string DetailAsSentence() =>
        Detail is null or "" ? string.Empty
        : Detail.EndsWith('.') ? Detail
        : Detail + ".";
}

/// <summary>
/// The core, thread-safe write primitive for OKF bundles: producer-validated,
/// serialized create/update of a concept and an atomic read-modify-write
/// append-to-concept, over one bundle provider (spec §3, §4, §9). Every byte it
/// writes goes through <see cref="IBundleProvider.Write"/>, which commits each
/// object atomically under its own path guards; the writer validates every key
/// under the same policy as a load (spec §2), refuses a creation that would make
/// the tree invalid, and checks <see cref="BundleProviderCapabilities.CanWrite"/>
/// before anything else. Both <c>OKF4net.Agents.OkfBundleTools</c> and
/// <c>OKF4net.Catalog.FileMemoryStore</c> share this one write path and one
/// process-wide per-bundle lock registry (no duplicate lock registry, no divergent
/// second write path). Never throws for an expected error — I/O, YAML,
/// validation, provider refusals and conflicts are returned as an
/// <c>Error: ...</c> result string; a provider that cannot write is a host
/// mistake and throws <see cref="BundleReadOnlyException"/>.
/// </summary>
public sealed class BundleConceptWriter
{
    /// <summary>
    /// The prefix of the lock key of a writer over any provider but the filesystem one
    /// (decision B11): the key is this prefix and <see cref="IBundleProvider.Id"/>, which no
    /// canonical root path can equal (those are absolute).
    /// </summary>
    private const string ProviderLockPrefix = "provider:";

    /// <summary>
    /// One monitor per resolved bundle root, shared across writers and aliases.
    /// Resolution happens at each outermost acquisition, through
    /// <see cref="ReparsePoints.ResolveLockKey"/>; failures use the normalized
    /// lexical path. Case-insensitive registry keys deliberately over-serialize
    /// case-distinct paths rather than split ordinary case-variant spellings.
    /// <see cref="LexicalLocks"/> additionally keeps operations on the same
    /// lexical root serialized when resolution changes between acquisitions.
    /// </summary>
    /// <remarks>
    /// Both registries live for the process's lifetime: one entry per lexical
    /// root and one per resolved key ever observed, including new targets of a
    /// retargeted link. Hosts accepting untrusted bundle roots must bound those
    /// distinct roots and targets upstream. Entries cannot simply be evicted
    /// while writers may still be using their monitors.
    /// </remarks>
    private static readonly ConcurrentDictionary<string, object> BundleLocks = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Stable gates for identical namespace-normalized lexical roots (ordinal).
    /// Case-distinct aliases must not create extra dependencies between otherwise
    /// independent resolved bundles. An operation normally holds
    /// its gate and its resolved monitor together, preserving same-root
    /// serialization across topology changes. A contended resolved monitor is
    /// waited for WITHOUT this gate, so its owner can re-enter through an alias.
    /// A thread already holding a write lock only TRIES a gate and, when it is
    /// busy, holds the resolved monitor alone for its whole operation (see
    /// <see cref="EnterWriteLock"/>'s remarks for what that costs).
    /// </summary>
    private static readonly ConcurrentDictionary<string, object> LexicalLocks = new(StringComparer.Ordinal);

    /// <summary>
    /// The current thread's held locks, keyed ORDINALly by normalized lexical
    /// root. Unlike registry over-coalescing, conflating two case-distinct
    /// aliases here can substitute another bundle's resolved monitor. Each
    /// nested acquisition of the same root re-enters what it holds (its gate
    /// only if one was taken) without
    /// following topology changes. Scopes are thread-affine.
    /// </summary>
    [ThreadStatic]
    private static Dictionary<string, HeldLock>? t_heldLocks;

    /// <summary>
    /// Over the filesystem provider, <see cref="BundleRoot"/> through
    /// <see cref="ReparsePoints.CanonicalizeRoot"/>, with Windows namespace forms
    /// normalized, computed once: the key of <see cref="t_heldLocks"/>, and the
    /// starting point <see cref="ReparsePoints.ResolveLockKey"/> resolves from.
    /// Over any other provider, <see cref="ProviderLockPrefix"/> and its
    /// <see cref="IBundleProvider.Id"/>, used as it is (nothing to resolve).
    /// </summary>
    private readonly string _lexicalRoot;

    private readonly Action? _onWriteCommitted;

    /// <summary>Where every byte is read and written.</summary>
    private readonly IBundleProvider _provider;

    /// <summary><see cref="_provider"/> when it is the filesystem provider: its root keys the lock registry and backs <see cref="BundleRoot"/>, and its test hooks back the writer's.</summary>
    private readonly FileSystemBundleProvider? _fileSystem;

    /// <summary>The key policy every write is checked against: the provider's native constraints under the options' policy, exactly as a load builds it (spec §2, decision B2).</summary>
    private readonly BundleKeyValidator _validator;

    /// <summary>The bounds of every read and listing the writer makes (spec §6).</summary>
    private readonly BundleLimits _limits;

    private Action? _beforeLateReparseCheckForTest;

    /// <summary>When true, <see cref="WriteConcept(string, string, string)"/> stamps a <c>generated</c> block (§5.2) if the caller omitted one. Off by default so only opt-in producer paths (the Agents write tool) auto-stamp.</summary>
    internal bool AutoStampGenerated { get; set; }

    /// <summary>
    /// Clock seam for the <c>generated</c> auto-stamp and for
    /// <see cref="RecordVerifications"/>'s <c>at</c>; overridable in tests.
    /// </summary>
    internal Func<DateTime> UtcNow { get; set; } = () => DateTime.UtcNow;

    /// <summary>
    /// The §7 actor spelling this library uses when it stamps
    /// <c>generated.by</c> itself: <c>"okf4net/" + OkfSpec.Version</c>. The
    /// single source of that spelling — <see cref="ProducerActor"/> defaults
    /// to it, and <c>OKF4net.Agents</c>' <c>OkfContextProvider.MemoryFrontmatter</c>
    /// reads it directly rather than re-spelling <c>"okf4net/" + OkfSpec.Version</c>
    /// a second time on a path that never goes through <see cref="ProducerActor"/>
    /// itself (see that method's remarks).
    /// </summary>
    internal static string DefaultProducerActor => "okf4net/" + OkfSpec.Version;

    /// <summary>The §7 actor recorded as <c>generated.by</c> when auto-stamping.</summary>
    internal string ProducerActor { get; set; } = DefaultProducerActor;

    /// <summary>Creates a writer over the directory <paramref name="bundleRoot"/>, through a <see cref="FileSystemBundleProvider"/> built over it.</summary>
    /// <param name="bundleRoot">The bundle's root directory.</param>
    /// <param name="onWriteCommitted">
    /// Optional callback invoked inside the write lock once after each
    /// successful write, before the lock is released — the seam
    /// <c>OkfBundleTools</c> uses to invalidate its bundle cache atomically with
    /// the write. <see langword="null"/> (the default) for callers with no cache.
    /// It runs once the write is final, after any retry, and never causes one:
    /// an exception it throws that the writer converts (an I/O error, an
    /// <see cref="OkfException"/>, a provider conflict included) is reported as
    /// each write method says, the write committed; any other (an
    /// <see cref="OperationCanceledException"/>, for one) propagates to the
    /// caller unchanged, the write committed.
    /// </param>
    public BundleConceptWriter(string bundleRoot, Action? onWriteCommitted = null)
        : this(new FileSystemBundleProvider(bundleRoot ?? throw new ArgumentNullException(nameof(bundleRoot))), onWriteCommitted)
    {
    }

    /// <summary>Creates a writer over <paramref name="provider"/> (spec §9).</summary>
    /// <param name="provider">Where the bundle is read and written. A provider whose <see cref="BundleProviderCapabilities.CanWrite"/> is <see langword="false"/> makes every write method throw <see cref="BundleReadOnlyException"/>. The in-process write lock is keyed by the provider's <see cref="IBundleProvider.Id"/> unless it is a <see cref="FileSystemBundleProvider"/> itself (keyed by its root): a decorator around a <see cref="FileSystemBundleProvider"/> therefore does not share the directory's lock with a writer built from the root string, and two such writers are serialised only by the provider's own commit lock and the write conditions (decision B11).</param>
    /// <param name="onWriteCommitted">As for <see cref="BundleConceptWriter(string, Action?)"/>.</param>
    /// <param name="options">The key policy, extra key validators and bounds the writer applies — the same as a load of this bundle should use, so that the writer never creates a key the load would discard. <see langword="null"/>: <see cref="BundleLoadOptions.Default"/>.</param>
    public BundleConceptWriter(IBundleProvider provider, Action? onWriteCommitted = null, BundleLoadOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(provider);
        _provider = provider;
        _fileSystem = provider as FileSystemBundleProvider;
        _onWriteCommitted = onWriteCommitted;
        options ??= BundleLoadOptions.Default;
        _validator = new BundleKeyValidator(provider.NativeConstraints, options.KeyPolicy, options.ExtraKeyValidators);
        _limits = options.Limits;

        // Only the LEXICAL form is computed here (and an invalid path still
        // throws here, as before). The write lock's registry key is NOT: it
        // is resolved through reparse points each time a thread takes the
        // lock (EnterWriteLock), because the topology can change after
        // construction -- a writer built over `later/bundle` while `later` did
        // not exist must still meet a writer over the junction target once
        // `later` becomes a junction (#86). Any other provider is keyed by its
        // Id, which nothing resolves (decision B11).
        _lexicalRoot = _fileSystem is not null
            ? ReparsePoints.NormalizeNamespaceForLockKey(ReparsePoints.CanonicalizeRoot(_fileSystem.Root))
            : ProviderLockPrefix + provider.Id;
    }

    /// <summary>The bundle root, as passed to the constructor (the filesystem provider's <see cref="FileSystemBundleProvider.Root"/>).</summary>
    /// <exception cref="NotSupportedException">The writer was built over another provider: a path has no meaning for it (spec §9).</exception>
    public string BundleRoot => _fileSystem?.Root
        ?? throw new NotSupportedException("this writer is not over a filesystem provider, so it has no bundle root");

    /// <summary>
    /// Enters this bundle's write lock and returns a scope whose
    /// <see cref="WriteLockScope.Dispose"/> exits it. Every write this class
    /// performs runs inside one, and a co-located caller
    /// (<c>OkfBundleTools.AppendLog</c>/<c>RegenerateIndexes</c>/cache access)
    /// takes one to serialize its own read-modify-write sequences against
    /// this writer's writes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The guarantee, exactly: writers in one process serialize when, at the
    /// moment they take the lock, their roots resolve to the same directory
    /// (<see cref="ReparsePoints.ResolveLockKey"/>). A junction or symlink to
    /// the bundle, or to one of its ancestors, is such an alias and shares the
    /// lock. If resolution fails, the key falls back to the lexical root, so
    /// the lock is then shared only by spellings of the same lexical path. A
    /// topology change DURING an operation is not followed: the outermost
    /// acquisition on a thread resolves the root, and every nested acquisition
    /// on that thread for the same writer root re-enters the object it already
    /// holds (<see cref="Monitor"/> is re-entrant) without resolving again, for
    /// the whole operation. A stable lexical gate also prevents another thread
    /// using the same lexical root from overlapping it, even if resolution
    /// changes, with one exception: a thread that already holds a write lock
    /// never waits for a gate (that wait could close a cycle with the gate's
    /// owner, who may come to need a gate this thread holds). It only tries
    /// the gate, retries it once (still without blocking) after resolving the
    /// root, and, when the gate is still busy, takes the resolved monitor alone.
    /// Ordinary contention on that root makes the gate busy, with no topology
    /// change (an outermost writer between taking the gate and taking its
    /// monitor, for instance). Such a NESTED acquisition then holds no gate
    /// for its whole operation, and if the root's resolution changes while it
    /// runs, another writer on the same lexical root resolves the new target,
    /// takes a different monitor and overlaps it. A scope releases its gate
    /// before its monitor, so a nested waiter woken by that release usually
    /// finds the gate free, and the retry catches a gate released while the
    /// root was being resolved; these narrow the case but do not close it.
    /// Different aliases can still diverge if topology changes during an
    /// active operation; no filesystem handles pin its target.
    /// </para>
    /// <para>
    /// Deadlock: no wait cycle passes through a lexical gate, because only a
    /// thread holding no write lock ever waits for one. Every other wait is
    /// for a resolved monitor, made without that root's gate. Nested calls
    /// over different resolved bundles therefore need a consistent caller lock
    /// order, and that order is over the registry's keys, which are
    /// CASE-FOLDED: two case-distinct bundles (<c>Foo</c> and <c>foo</c> on a
    /// case-sensitive volume) share one monitor and count as one bundle for
    /// ordering, so <c>Foo &lt; Bar &lt; foo</c> is not a consistent order.
    /// Because an operation keeps the monitor it resolved first, a link on the
    /// path retargeted during an operation -- even one that every alias
    /// follows, so the aliases always agree with each other -- splits one
    /// bundle into two for this purpose: aliases of it nested in opposite
    /// order can then still deadlock between their resolved monitors (never
    /// through a gate).
    /// </para>
    /// <para>
    /// Contention: the root is resolved (filesystem I/O) while the gate is
    /// held (except on the busy-gate nested path above), and a waiter through
    /// an alias, woken when the resolved monitor is released, must take the
    /// gate and resolve again before it can enter.
    /// The retry loop is unbounded but never busy-spins (each iteration waits
    /// for the monitor); under constant contention, or a topology that keeps
    /// changing, an alias waiter can be starved.
    /// </para>
    /// <para>
    /// Still in-process only: nothing here serializes a
    /// second process writing the same bundle, and a C# lock cannot stop an
    /// external actor mutating the bundle's files on disk -- see
    /// <see cref="ValidateConceptTarget"/>'s remarks for that separate,
    /// residual TOCTOU limitation.
    /// </para>
    /// <para>
    /// The scope is thread-affine, like the monitor under it: dispose it on
    /// the thread that entered it, and never hold it across an
    /// <see langword="await"/>.
    /// </para>
    /// </remarks>
    internal WriteLockScope EnterWriteLock()
    {
        // Case-distinct lexical aliases can resolve to DIFFERENT bundles.
        // Reusing a held lock is therefore stricter than coalescing registry
        // keys: an ordinal match is required here.
        var held = t_heldLocks ??= new Dictionary<string, HeldLock>(StringComparer.Ordinal);
        if (held.TryGetValue(_lexicalRoot, out var outer))
        {
            // Both are already owned by this thread: re-entry never blocks.
            if (outer.LexicalLock is { } outerGate)
            {
                Monitor.Enter(outerGate);
            }

            Monitor.Enter(outer.LockObject);
            outer.Depth++;
            return new WriteLockScope(_lexicalRoot, outer.LockObject, outer.LexicalLock);
        }

        // A thread already holding any write lock never WAITS for a lexical
        // gate: the gate's owner can come to need a gate this thread holds
        // (two aliases nested in opposite order while a link on the path is
        // retargeted), and a gate wait would close that cycle. Such a thread
        // only tries the gate; when it is busy, it goes on with the resolved
        // monitor alone.
        var holdsAnotherWriteLock = held.Count > 0;
        var lexicalLock = LexicalLocks.GetOrAdd(_lexicalRoot, static _ => new object());
        while (true)
        {
            object? lockObject = null;
            var lexicalTaken = false;
            var resolvedTaken = false;
            try
            {
                if (holdsAnotherWriteLock)
                {
                    Monitor.TryEnter(lexicalLock, ref lexicalTaken);
                }
                else
                {
                    Monitor.Enter(lexicalLock, ref lexicalTaken);
                }

                if (!lexicalTaken)
                {
                    GateBusyForTest?.Invoke();
                }

                // Resolve AFTER the gate attempt, including after each wait.
                // An earlier operation may have changed the root.
                lockObject = ResolveLockObject();

                // A nested acquisition retries a busy gate once, still
                // without blocking: its owner may have released it while
                // this thread resolved (filesystem I/O). Taken here, it is
                // recorded and released exactly like a gate taken above.
                if (holdsAnotherWriteLock && !lexicalTaken)
                {
                    Monitor.TryEnter(lexicalLock, ref lexicalTaken);
                }

                // Never blocks. Succeeds re-entrantly when this thread
                // already holds the resolved monitor through another root.
                Monitor.TryEnter(lockObject, ref resolvedTaken);
                if (resolvedTaken)
                {
                    // null: the gate was busy and is not part of this hold.
                    var gate = lexicalTaken ? lexicalLock : null;
                    held.Add(_lexicalRoot, new HeldLock(lockObject, gate));
                    // Both holds now belong to the scope.
                    lexicalTaken = false;
                    resolvedTaken = false;
                    return new WriteLockScope(_lexicalRoot, lockObject, gate);
                }
            }
            finally
            {
                if (resolvedTaken)
                {
                    Monitor.Exit(lockObject!);
                }

                if (lexicalTaken)
                {
                    Monitor.Exit(lexicalLock);
                }
            }

            // Never wait for another thread's resolved lock while holding
            // the lexical gate. Its owner may need that gate to enter this
            // alias recursively. Wait without the gate, then retry BOTH
            // acquisition and resolution; this is not the operation's hold.
            BeforeContendedWaitForTest?.Invoke();
            lock (lockObject!)
            {
            }
        }
    }

    /// <summary>
    /// Test-only hook, invoked (if set) on the acquiring thread in
    /// <see cref="EnterWriteLock"/> after it found the resolved monitor busy
    /// and released the gate, immediately before it waits for that monitor.
    /// </summary>
    internal Action? BeforeContendedWaitForTest { get; set; }

    /// <summary>
    /// Test-only hook, invoked (if set) on the acquiring thread in
    /// <see cref="EnterWriteLock"/> when a nested acquisition's first,
    /// non-blocking attempt at the lexical gate failed, before it resolves
    /// the root.
    /// </summary>
    internal Action? GateBusyForTest { get; set; }

    /// <summary>
    /// The registry object this writer's root resolves to NOW, without
    /// entering it and without consulting what the current thread holds.
    /// Test-only.
    /// </summary>
    internal object CurrentLockObjectForTest() => ResolveLockObject();

    /// <summary>
    /// This writer's lexical gate, so a test can hold it from another thread
    /// and stand in for an outermost writer caught between taking the gate and
    /// taking its resolved monitor. Test-only.
    /// </summary>
    internal object LexicalGateForTest() => LexicalLocks.GetOrAdd(_lexicalRoot, static _ => new object());

    private object ResolveLockObject() =>
        BundleLocks.GetOrAdd(_fileSystem is not null ? ReparsePoints.ResolveLockKey(_lexicalRoot) : _lexicalRoot, static _ => new object());

    /// <summary>
    /// One entry of <see cref="t_heldLocks"/>: the objects held and how many
    /// scopes hold them. <see cref="LexicalLock"/> is <see langword="null"/>
    /// when a nested acquisition proceeded without its busy gate.
    /// </summary>
    private sealed class HeldLock(object lockObject, object? lexicalLock)
    {
        public object LockObject { get; } = lockObject;

        public object? LexicalLock { get; } = lexicalLock;

        public int Depth { get; set; } = 1;
    }

    /// <summary>
    /// A hold of a bundle's write lock, from <see cref="EnterWriteLock"/>.
    /// Dispose it exactly once, on the thread that entered it.
    /// </summary>
    internal readonly struct WriteLockScope : IDisposable
    {
        private readonly string _lexicalRoot;

        private readonly object? _lexicalLock;

        internal WriteLockScope(string lexicalRoot, object lockObject, object? lexicalLock)
        {
            _lexicalRoot = lexicalRoot;
            LockObject = lockObject;
            _lexicalLock = lexicalLock;
        }

        /// <summary>The registry object this scope entered.</summary>
        internal object LockObject { get; }

        /// <summary>
        /// Whether this scope holds its root's lexical gate; <see langword="false"/>
        /// for a nested acquisition that proceeded without a busy gate. Test-only.
        /// </summary>
        internal bool HoldsLexicalGateForTest => _lexicalLock is not null;

        /// <summary>Exits the lock, and forgets it on this thread once the outermost scope ends.</summary>
        public void Dispose()
        {
            if (LockObject is null)
            {
                return; // default(WriteLockScope): nothing was entered.
            }

            if (t_heldLocks is { } held && held.TryGetValue(_lexicalRoot, out var entry) && --entry.Depth == 0)
            {
                held.Remove(_lexicalRoot);
            }

            // The gate first, then the monitor. A nested waiter blocked on
            // the monitor, woken by its release, then usually finds the gate
            // free and keeps it, instead of retrying a gate still held for
            // an instant and proceeding without it. Exits never block, so
            // the order cannot add a wait; both objects are this scope's.
            if (_lexicalLock is not null)
            {
                Monitor.Exit(_lexicalLock); // null: entered without its gate.
            }

            Monitor.Exit(LockObject);
        }
    }

    /// <summary>
    /// Test-only hook, invoked (if set) immediately before the late
    /// reparse-point re-check of a write. Over a <see cref="FileSystemBundleProvider"/>
    /// it is that provider's <c>ParentsResolvedForTest</c>: it fires inside
    /// <see cref="IBundleProvider.Write"/> once the object's parent directories
    /// exist (found, or just created), before the provider's STRICT re-check of
    /// the whole path and before its temporary is created — exactly where the
    /// writer's own <see cref="Directory.CreateDirectory(string)"/> and late
    /// re-check sat before the write moved behind the provider. Lets a test
    /// deterministically simulate a filesystem substitution racing the final
    /// write -- e.g. deleting the just-created parent directory and replacing
    /// it with a junction to an external directory -- at exactly the point
    /// such a race would need to land, instead of relying on real (flaky,
    /// unreliable) thread timing. Over any other provider it has no effect
    /// (script that provider instead). <see langword="internal"/>, always
    /// <see langword="null"/> outside tests, so it has zero effect on
    /// production behavior.
    /// </summary>
    internal Action? BeforeLateReparseCheckForTest
    {
        get => _beforeLateReparseCheckForTest;
        set
        {
            _beforeLateReparseCheckForTest = value;
            if (_fileSystem is not null)
            {
                _fileSystem.ParentsResolvedForTest = value is null ? null : _ => value();
            }
        }
    }

    /// <summary>
    /// Creates or updates one concept document. Producer-grade validation
    /// (<see cref="OkfDocument.Validate"/>: non-empty <c>type</c>,
    /// <c>title</c>, and <c>description</c>) runs BEFORE
    /// anything is written — on failure, the file on disk (if any) is left
    /// untouched. Never throws for expected errors (a null/blank/malformed
    /// concept id, a reserved id, invalid frontmatter YAML, or a failed
    /// validation) — those are reported as a plain-text message instead.
    /// </summary>
    /// <param name="conceptId">The concept id (path without <c>.md</c>), e.g. <c>tables/refunds</c>.</param>
    /// <param name="frontmatterYaml">Frontmatter as <c>key: value</c> lines (the same YAML subset used inside a document's frontmatter block, without the <c>---</c> delimiters).</param>
    /// <param name="body">The markdown body.</param>
    /// <exception cref="BundleReadOnlyException">The provider cannot write; nothing was attempted.</exception>
    public string WriteConcept(string conceptId, string frontmatterYaml, string body) =>
        WriteConcept(conceptId, frontmatterYaml, body, BundleWriteCondition.None);

    /// <summary>
    /// Like <see cref="WriteConcept(string, string, string)"/>, under a condition the provider checks
    /// atomically with the write (spec §4): <see cref="BundleWriteCondition.IfMatch"/> with the version a
    /// read returned replaces the concept only if it has not changed since;
    /// <see cref="BundleWriteCondition.MustNotExist"/> creates it only if it does not exist yet;
    /// <see cref="BundleWriteCondition.None"/> is the unconditional write of the three-argument overload.
    /// A condition that does not hold writes nothing and returns an <c>Error: …</c> that tells the caller
    /// to read the concept (again). Never retried: a retry would overwrite what the caller has not seen.
    /// </summary>
    /// <param name="conceptId">The concept id (path without <c>.md</c>), e.g. <c>tables/refunds</c>.</param>
    /// <param name="frontmatterYaml">Frontmatter as <c>key: value</c> lines (the same YAML subset used inside a document's frontmatter block, without the <c>---</c> delimiters).</param>
    /// <param name="body">The markdown body.</param>
    /// <param name="condition">The condition on the concept's current version.</param>
    /// <exception cref="BundleReadOnlyException">The provider cannot write; nothing was attempted.</exception>
    public string WriteConcept(string conceptId, string frontmatterYaml, string body, BundleWriteCondition condition)
    {
        GuardWritable();
        if (string.IsNullOrWhiteSpace(conceptId))
        {
            return "Error: invalid concept id — it must not be empty.";
        }

        if (conceptId.Contains('\0'))
        {
            return "Error: invalid concept id — it must not contain a null character.";
        }

        if (frontmatterYaml is null)
        {
            return "Error: frontmatter must not be null.";
        }

        if (frontmatterYaml.Contains('\0'))
        {
            return "Error: invalid frontmatter — it must not contain a null character.";
        }

        if (body is null)
        {
            return "Error: body must not be null.";
        }

        if (body.Contains('\0'))
        {
            return "Error: invalid body — it must not contain a null character.";
        }

        string? subject = null;
        return RunTool(
            () =>
            {
                var targetError = ValidateConceptTarget(conceptId, out var target);
                if (targetError is not null)
                {
                    return targetError;
                }

                subject = $"'{target.Id}'";
                var (content, buildError) = BuildValidatedContent(ParseFrontmatterAndMaybeStamp(frontmatterYaml), body);
                if (buildError is not null)
                {
                    return buildError;
                }

                // Serialized under the bundle's write lock (shared with
                // AppendToConceptAtomic and, since EnterWriteLock takes it from
                // the process-wide BundleLocks registry under the root's resolved
                // key, with every OTHER BundleConceptWriter instance whose root
                // resolves to this same directory, not just this instance) so
                // concurrent writers can't interleave an
                // existence check with another writer's write, and so the
                // committed callback below is atomic with the write it follows.
                using (EnterWriteLock())
                {
                    return WriteThenNotify(committed => WriteConditionedLocked(target, content!, condition, committed));
                }
            },
            () => subject);
    }

    /// <summary>
    /// Like <see cref="WriteConcept(string, string, string)"/>, but takes an already-built
    /// <see cref="Frontmatter"/> instead of pre-serialized YAML text — skips the serialize/re-parse
    /// round trip for a programmatic caller (e.g. the forthcoming <c>OkfDocumentBuilder</c>). Same
    /// producer-grade validation, per-bundle lock, and reparse-point guards as the string overload
    /// (both share <see cref="ValidateConceptTarget"/>, <see cref="BuildValidatedContent(YamlValue, string)"/>,
    /// and <see cref="WriteValidatedContentLocked"/>).
    ///
    /// Operates on a shallow copy of <paramref name="frontmatter"/>'s underlying mapping, never the
    /// caller's own <see cref="YamlMapping"/> instance — <see cref="Frontmatter.AsMapping"/> returns
    /// that instance directly (no defensive copy of its own), and mutating it in place (e.g. via
    /// auto-stamping, see <see cref="MaybeStampGenerated"/>) would otherwise silently modify an object
    /// the caller may still hold and inspect afterward.
    /// </summary>
    /// <param name="conceptId">The concept id (path without <c>.md</c>), e.g. <c>tables/refunds</c>.</param>
    /// <param name="frontmatter">The frontmatter to write. Not mutated by this call.</param>
    /// <param name="body">The markdown body.</param>
    /// <exception cref="BundleReadOnlyException">The provider cannot write; nothing was attempted.</exception>
    public string WriteConcept(string conceptId, Frontmatter frontmatter, string body) =>
        WriteConcept(conceptId, frontmatter, body, BundleWriteCondition.None);

    /// <summary>
    /// Like <see cref="WriteConcept(string, Frontmatter, string)"/>, under a condition the provider checks
    /// atomically with the write — see <see cref="WriteConcept(string, string, string, BundleWriteCondition)"/>.
    /// </summary>
    /// <param name="conceptId">The concept id (path without <c>.md</c>), e.g. <c>tables/refunds</c>.</param>
    /// <param name="frontmatter">The frontmatter to write. Not mutated by this call.</param>
    /// <param name="body">The markdown body.</param>
    /// <param name="condition">The condition on the concept's current version.</param>
    /// <exception cref="BundleReadOnlyException">The provider cannot write; nothing was attempted.</exception>
    public string WriteConcept(string conceptId, Frontmatter frontmatter, string body, BundleWriteCondition condition)
    {
        GuardWritable();
        if (string.IsNullOrWhiteSpace(conceptId))
        {
            return "Error: invalid concept id — it must not be empty.";
        }

        if (conceptId.Contains('\0'))
        {
            return "Error: invalid concept id — it must not contain a null character.";
        }

        ArgumentNullException.ThrowIfNull(frontmatter);

        if (body is null)
        {
            return "Error: body must not be null.";
        }

        if (body.Contains('\0'))
        {
            return "Error: invalid body — it must not contain a null character.";
        }

        string? subject = null;
        return RunTool(
            () =>
            {
                var targetError = ValidateConceptTarget(conceptId, out var target);
                if (targetError is not null)
                {
                    return targetError;
                }

                subject = $"'{target.Id}'";
                var mapping = ShallowCopy(frontmatter.AsMapping());
                MaybeStampGenerated(mapping);

                var (content, buildError) = BuildValidatedContent(mapping, body);
                if (buildError is not null)
                {
                    return buildError;
                }

                using (EnterWriteLock())
                {
                    return WriteThenNotify(committed => WriteConditionedLocked(target, content!, condition, committed));
                }
            },
            () => subject);
    }

    /// <summary>
    /// Copies <paramref name="map"/>'s entries into a fresh <see cref="YamlMapping"/>, preserving
    /// order and every entry verbatim (including a duplicate or non-string key, via
    /// <see cref="YamlMapping.PushRaw"/>) — a shallow copy, sufficient because
    /// <see cref="WriteConcept(string, Frontmatter, string)"/> only ever inserts a new top-level key
    /// into the copy, never mutates a nested value.
    /// </summary>
    private static YamlMapping ShallowCopy(YamlMapping map)
    {
        var copy = new YamlMapping();
        foreach (var (key, value) in map.Entries)
        {
            copy.PushRaw(key, value);
        }

        return copy;
    }

    /// <summary>
    /// Atomically reads, transforms, and rewrites one concept's body under
    /// the shared bundle lock — the seam a same-day memory-capture caller
    /// (e.g. <c>OKF4net.Agents.OkfContextProvider.CaptureMemory</c>) uses to
    /// close a lost-update race (E2): before this
    /// existed, a caller that read a concept's body via a cached bundle,
    /// built a new body from it OUTSIDE any lock, then called the plain
    /// <see cref="WriteConcept(string, string, string)"/>, could have that read/build/write sequence
    /// interleave with a concurrent caller doing the same for the same
    /// concept — both read the same "before" body, and the second write
    /// silently clobbers the first's change (a lost update), even if some
    /// OTHER, already-locked write faithfully recorded both calls, producing
    /// a count divergence between the two.
    /// Here, the read of the concept's CURRENT on-disk body, the caller's
    /// <paramref name="buildBody"/> transform, and the validated write all
    /// happen inside one unbroken hold of the bundle's write lock
    /// (<see cref="EnterWriteLock"/>), so two
    /// concurrent calls for the same concept id can never interleave: the
    /// second call's read always observes the first call's completed write.
    /// Because that lock comes from the process-wide
    /// <c>BundleLocks</c> registry (keyed by the bundle root resolved through
    /// reparse points when the lock is taken),
    /// this holds for two concurrent calls on the SAME <see cref="BundleConceptWriter"/>
    /// instance AND for two concurrent calls on two SEPARATE instances
    /// whose roots resolve to the same directory, a junction or symlink to it
    /// included -- but only within one process:
    /// it does not serialize a second process writing the same bundle path.
    /// The write is therefore conditioned on the version read
    /// (<see cref="BundleWriteCondition.IfMatch"/>, or
    /// <see cref="BundleWriteCondition.MustNotExist"/> for a creation): a
    /// change another writer committed in between is never overwritten. The
    /// whole read-transform-write is then tried again on what is there now —
    /// at most <see cref="MaxAttempts"/> attempts, with a short random delay
    /// between them (spec §4) — and after the last one the conflict is
    /// returned as an <c>Error: ...</c> message. Reuses the exact same target validation (<see cref="ValidateConceptTarget"/>),
    /// producer-grade validation and serialization (<see cref="BuildValidatedContent(string, string)"/>),
    /// and write (<see cref="WriteValidatedContentLocked"/>) and notification (<see cref="WriteThenNotify"/>)
    /// steps <see cref="WriteConcept(string, string, string)"/> itself uses — this is a locked
    /// read-modify-write wrapped AROUND that same core, not a divergent
    /// second write path.
    /// </summary>
    /// <param name="conceptId">The concept id (path without <c>.md</c>), e.g. <c>memory/2026-07-24</c>.</param>
    /// <param name="frontmatterYamlIfCreating">
    /// Frontmatter used only when the concept does not yet exist. When it
    /// already exists, its own current frontmatter is re-read and
    /// re-serialized unchanged (mirroring how a caller that read-then-called
    /// <see cref="WriteConcept(string, string, string)"/> would carry it forward) and this parameter
    /// is ignored.
    /// </param>
    /// <param name="buildBody">
    /// Given the concept's current body (<see langword="null"/> if it does
    /// not yet exist), returns the full new body to write. Invoked once per
    /// attempt — at most <see cref="MaxAttempts"/> times — inside the lock,
    /// each time against the freshly re-read current body, never a caller's
    /// own stale, pre-lock snapshot. It must therefore be a pure function of
    /// the body it receives: no side effect, nothing counted per call.
    /// </param>
    /// <returns>
    /// The same style of result text as <see cref="WriteConcept(string, string, string)"/> (a
    /// <c>Written ...</c> confirmation) or an <c>Error: ...</c> message;
    /// never throws (except as below, or what <paramref name="buildBody"/> itself throws).
    /// </returns>
    /// <exception cref="BundleReadOnlyException">The provider cannot write; nothing was attempted.</exception>
    public string AppendToConceptAtomic(
        string conceptId,
        string frontmatterYamlIfCreating,
        Func<string?, string> buildBody)
    {
        GuardWritable();
        if (string.IsNullOrWhiteSpace(conceptId))
        {
            return "Error: invalid concept id — it must not be empty.";
        }

        if (conceptId.Contains('\0'))
        {
            return "Error: invalid concept id — it must not contain a null character.";
        }

        if (frontmatterYamlIfCreating is null)
        {
            return "Error: frontmatter must not be null.";
        }

        if (frontmatterYamlIfCreating.Contains('\0'))
        {
            return "Error: invalid frontmatter — it must not contain a null character.";
        }

        string? subject = null;
        return RunTool(
            () =>
            {
                var targetError = ValidateConceptTarget(conceptId, out var target);
                if (targetError is not null)
                {
                    return targetError;
                }

                subject = $"'{target.Id}'";

                // The read of the current body, the caller's transform, and the
                // validated write all happen inside this ONE lock acquisition —
                // never released and reacquired in between — which is what makes
                // the whole read-modify-write atomic against a concurrent
                // WriteConcept/AppendToConceptAtomic call for the same concept.
                // The write is conditioned on the version read, so a writer
                // outside this lock (another process) cannot be overwritten;
                // its conflict re-reads and runs buildBody again (WithRetries).
                using (EnterWriteLock())
                {
                    return WriteThenNotify(committed => WithRetries(subject, () =>
                    {
                        string frontmatterYaml;
                        string? currentBody;

                        // Strict UTF-8: a non-UTF-8 concept file throws
                        // DecoderFallbackException (caught by RunTool below)
                        // rather than being silently re-decoded and rewritten.
                        var current = ReadCurrent(target.Key);
                        if (current is { } existing)
                        {
                            // Fail-closed: if the existing concept has malformed
                            // frontmatter (hand-edited, or a prior partial/crashed
                            // write), OkfDocument.Parse throws DocumentParseException
                            // (caught by RunTool -> Error text) so this append is
                            // dropped rather than overwriting a possibly important
                            // file. This is stricter than a permissive bundle-load
                            // path, which would treat an unparseable file as absent
                            // and silently recreate it.
                            var existingDoc = OkfDocument.Parse(existing.Text);
                            frontmatterYaml = existingDoc.Frontmatter.AsMapping().ToYamlString();
                            currentBody = existingDoc.Body;
                        }
                        else
                        {
                            frontmatterYaml = frontmatterYamlIfCreating;
                            currentBody = null;
                        }

                        var newBody = buildBody(currentBody);
                        if (newBody is null)
                        {
                            return "Error: body must not be null.";
                        }

                        if (newBody.Contains('\0'))
                        {
                            return "Error: invalid body — it must not contain a null character.";
                        }

                        var (content, buildError) = BuildValidatedContent(frontmatterYaml, newBody);
                        if (buildError is not null)
                        {
                            return buildError;
                        }

                        return WriteValidatedContentLocked(target, content!, ConditionFor(current), committed, existedBefore: current is not null);
                    }));
                }
            },
            () => subject);
    }

    /// <summary>
    /// Records a review of every concept in <paramref name="conceptIds"/>:
    /// adds — or replaces, at its position — the <c>{ by, at }</c> entry of
    /// <paramref name="by"/> in each concept's §5.2 <c>verified</c> list.
    ///
    /// <b>Byte-preservation guarantee.</b> Every other frontmatter key and the
    /// body are preserved byte for byte: the <c>verified:</c> block is edited
    /// IN PLACE in the raw text (<see cref="FrontmatterBlockEdit"/>), never by
    /// re-emitting the whole document, so CRLF line endings, YAML comments,
    /// and flow/folded scalar spellings elsewhere all survive untouched — a
    /// property this method's own re-parse-and-compare check enforces (full
    /// structural <see cref="OkfDocument.Equals(OkfDocument?)"/> against the
    /// intended result, not merely a stamp count), refusing the write rather
    /// than risk a silently wrong file if the edit ever disagrees. The ONE
    /// exception: a column-0 comment sitting INSIDE the <c>verified:</c>
    /// block (between the <c>verified:</c> line and the next top-level key),
    /// and any blank line immediately before it there, is replaced along with
    /// the block — there is no way to know which of the stamp's lines such a
    /// comment was meant to annotate, so it cannot be preserved separately
    /// from the value it comments on.
    ///
    /// Fully validated before the first write: inside one hold of the bundle
    /// lock, every concept id is checked and resolved to its bundle key, then
    /// each target is read, edited and validated, all before a single byte is
    /// written. The pre-checks read the files too, so they cannot run before
    /// the lock is taken: a concurrent writer of the same concept would make
    /// them fail on a sharing violation or a half-written file (#131). A batch is therefore REJECTED as a whole — an
    /// unknown id, a malformed actor or a non-conformant document writes
    /// nothing.
    ///
    /// It is NOT a transaction (spec §4: no multi-object transaction). Each
    /// concept is written atomically, conditioned on the version it was
    /// prepared from, but a failure during the write phase (I/O, permissions,
    /// a path the provider refuses at its late re-check, a conflict with a
    /// writer outside this lock) leaves the concepts already written stamped.
    /// That case reports <c>Recorded = false</c> with <c>Records</c> listing
    /// the concepts whose write was committed, and a message ending
    /// <c>— already written: …</c>; <see cref="VerificationOutcome"/> states
    /// exactly what the list does and does not promise.
    /// The lock is also in-process, so an external actor mutating the bundle
    /// mid-batch is not stopped: the version condition refuses to overwrite
    /// its change rather than losing it.
    ///
    /// A stamp is a dated declaration, not an authentication result: this
    /// method cannot and does not check that the caller is who
    /// <paramref name="by"/> names. What makes a stamp credible is where it
    /// lands — a reviewed diff — not the tool that wrote it.
    /// </summary>
    /// <param name="conceptIds">Concept ids (paths without <c>.md</c>); each must already exist.</param>
    /// <param name="by">The §7 actor recording the review; must be well-formed.</param>
    /// <param name="at">
    /// Timestamp in the library's own UTC shape (<c>yyyy-MM-ddTHH:mm:ssZ</c>);
    /// null uses <see cref="UtcNow"/>.
    /// </param>
    /// <exception cref="BundleReadOnlyException">The provider cannot write; nothing was attempted.</exception>
    public VerificationOutcome RecordVerifications(IReadOnlyList<string> conceptIds, string by, string? at = null)
    {
        GuardWritable();
        if (conceptIds is null || conceptIds.Count == 0)
        {
            return Failed("Error: no concept id given.");
        }

        // Guard every element before any of them reaches ValidateConceptTarget:
        // ConceptId.TryParse's Parse -> s.Split('/') throws NullReferenceException
        // for a null element (NRE is not in RunTool's catch filter), and a JSON
        // binder handing this list to a string[] can put a null in it regardless
        // of nullable annotations. Mirrors WriteConcept's own id guards verbatim.
        foreach (var conceptId in conceptIds)
        {
            if (string.IsNullOrWhiteSpace(conceptId))
            {
                return Failed("Error: invalid concept id — it must not be empty.");
            }

            if (conceptId.Contains('\0'))
            {
                return Failed("Error: invalid concept id — it must not contain a null character.");
            }
        }

        // THE governed gate for a control-bearing actor — see
        // LineSafeText.ContainsControlCharacter for why it lives on the write
        // path and what it does not cover. Every layer above inherits it from
        // here: the CLI verb and okf_verify re-run the same predicate only to
        // phrase a better message, and both renderers interpolate `by` into a
        // line with no escaping precisely because nothing this method wrote can
        // carry a newline. Note this refuses the value rather than sanitizing
        // it: an actor is an identity, and silently rewriting one would store a
        // different identity than the caller asked for.
        //
        // Checked BEFORE the well-formedness arm below, whose message echoes
        // `by`: echoing a newline-bearing value would forge a line in the
        // caller's error output — the very thing being closed here.
        if (by is not null && LineSafeText.ContainsControlCharacter(by))
        {
            return Failed("Error: a §7 actor must not contain control characters.");
        }

        // Strict on input, permissive on read: `human:` with no id promotes the
        // tier (Actor.IsHuman ignores well-formedness), so it must never be
        // written here even though a parser would accept it.
        if (by is null || !Actor.Parse(by).IsWellFormed)
        {
            return Failed($"Error: '{by}' is not a well-formed §7 actor.");
        }

        // NOT BundleValidator.IsIso8601DateTime: that predicate validates the
        // date and ignores everything after the `T` (Validate.cs:618), because
        // reading frontmatter is deliberately permissive. Writing is not: a
        // stamp this library produces is UTC in one exact shape, and accepting
        // "2026-08-28" or a +02:00 offset here would write a value the field's
        // own documentation calls UTC.
        var stampedAt = at ?? OkfTimestamp.FormatUtc(UtcNow());

        // Guarded for the same reason `by` is, and it took an external review to
        // see it: rejecting a malformed `at` is NOT enough, because the
        // rejection message below QUOTES it. A strict parse stops the value
        // being written; it does nothing about the value being echoed, so
        // `--at $'bad\nrecorded …'` forged a line in the caller's error stream
        // on a run that wrote nothing. Refused before it is quoted.
        if (LineSafeText.ContainsControlCharacter(stampedAt))
        {
            return Failed("Error: a timestamp must not contain control characters.");
        }

        if (!OkfTimestamp.IsEmittedUtcForm(stampedAt))
        {
            return Failed($"Error: '{stampedAt}' is not a UTC timestamp of the form yyyy-MM-ddTHH:mm:ssZ.");
        }

        var records = new List<VerificationRecord>(conceptIds.Count);
        var written = new List<BundleKey>(conceptIds.Count);
        BundleWriteFailure? failure = null;
        string? subject = null;
        var message = RunTool(() =>
        {
            // Target validation also reads the files. Keep it in the same
            // lock hold as prepare/write: otherwise a cooperating writer can
            // expose a sharing violation or a partially written document (#131).
            using (EnterWriteLock())
            {
                // Checked FIRST for five of its six kinds, so THIS method's own
                // refusal names the offender: before this call existed, an
                // unparseable concept escaped as an unattributed, generic
                // "Error: {yaml/parse message}" naming no concept at all -- the
                // exact gap the CLI verb and the okf_verify tool each worked
                // around with their own duplicate pre-loop.
                //
                // NotConformant is deliberately EXCLUDED here (left to fall
                // through to the unchanged prepare loop below, where
                // ValidateConformance already runs): a concept missing `type`
                // can ALSO be one of the hostile shapes below it -- a corrupted
                // round-trip, an unemittable deep nesting -- and those failures
                // are strictly MORE specific and were surfacing first long before
                // this method existed. (A third shape, an indented `---` inside a
                // block scalar misread as the closing fence (finding #C7-A), no
                // longer exists: OkfDocument.IsFenceLine accepts only a column-0
                // fence, §4.) Short-circuiting on NotConformant here would let
                // §11's generic message pre-empt a specific, better diagnostic. Bailing out here for the
                // OTHER five kinds carries no such risk FOR THE SAME ID: an
                // invalid id, a missing file, an unreadable file, an unparseable
                // document or a resolved-path duplicate are all structurally
                // prior to any edit attempt in the unchanged loop below too, so
                // moving THAT id's detection earlier changes nothing about what
                // would eventually have been reported for it. This does NOT hold
                // ACROSS ids in a batch with more than one problem: e.g. a batch
                // `[concept-with-a-NaN-float, metrics/nope]` used to refuse on
                // index 0 (the NaN concept, reached first by the old sequential
                // per-index loop below); bailing out here on CheckVerificationTargets's
                // own id-validity/existence/duplicate scan instead reports index
                // 1 (`metrics/nope`, "does not exist") first, since that scan
                // runs its OWN passes over every id before this method's prepare
                // loop ever starts. Both are correct refusals of the same batch;
                // which one is named first can move.
                var targetProblem = CheckVerificationTargets(conceptIds);
                if (targetProblem is { Kind: not VerificationTargetProblemKind.NotConformant } problem)
                {
                    return FormatVerificationTargetProblem(problem);
                }

                var targets = new List<ConceptTarget>(conceptIds.Count);
                foreach (var conceptId in conceptIds)
                {
                    var targetError = ValidateConceptTarget(conceptId, out var target);
                    if (targetError is not null)
                    {
                        return targetError;
                    }

                    targets.Add(target);
                }

                // No duplicate-by-path check here any more (a prior review round
                // singled this exact loop out as dead code once the early
                // CheckVerificationTargets call above ran first): that call
                // applies the identical resolved-path, case-insensitive rule to
                // every id in the batch before this line is ever reached, so a
                // batch containing any duplicate already returned via
                // FormatVerificationTargetProblem above. See
                // CheckVerificationTargets's own duplicate-check block for the
                // rule's rationale (moved there from this comment).

                // PREPARE every concept — read, parse, upsert the stamp, and
                // validate — before writing any of them, so an unknown,
                // unreadable, or non-conformant concept later in the list
                // rejects the WHOLE batch, even though earlier concepts in it
                // already built successfully.
                var prepared = new List<PreparedStamp>(targets.Count);
                for (var i = 0; i < targets.Count; i++)
                {
                    subject = $"'{targets[i].Id}'";
                    var prepareError = PrepareStamp(targets[i], conceptIds[i], by, stampedAt, out var stamp);
                    if (prepareError is not null)
                    {
                        return prepareError;
                    }

                    prepared.Add(stamp);
                }

                // Writing N objects cannot be atomic: the provider commits each
                // one atomically, but a failure here — I/O, permissions, a
                // refusal of the provider's late path guard, a conflict with a
                // writer outside this lock — leaves the earlier concepts
                // stamped. `records` is built HERE, one entry per committed
                // write, deliberately NOT in the prepare loop above: that is
                // what makes it mean "landed", not "was validated". A
                // batch rejected during PREPARE never reaches this loop, so
                // `records` stays empty; a batch that fails partway through
                // WRITE leaves `records` holding exactly the prefix that
                // actually landed — no separate trim/rollback step
                // to keep in sync, and no way for a future early return in
                // this loop to under- or over-report what landed.
                string? notificationFailure = null;
                for (var i = 0; i < prepared.Count; i++)
                {
                    var stamp = prepared[i];
                    var name = $"'{stamp.Target.Id}'";
                    subject = name;
                    var committed = false;
                    string writeResult;
                    try
                    {
                        // Conditioned on the version the stamp was prepared
                        // from. A conflict (a writer outside this lock) prepares
                        // THIS concept again from what is there now and retries;
                        // the concepts already written stay written.
                        var attempt = 0;
                        writeResult = WithRetries(name, () =>
                        {
                            if (++attempt > 1)
                            {
                                var prepareError = PrepareStamp(stamp.Target, stamp.ConceptId, by, stampedAt, out var fresh);
                                if (prepareError is not null)
                                {
                                    return prepareError;
                                }

                                stamp = fresh;
                            }

                            return WriteValidatedContentLocked(stamp.Target, stamp.Content, BundleWriteCondition.IfMatch(stamp.Version), () => committed = true, existedBefore: true);
                        });
                    }
                    catch (Exception ex) when (IsExpectedFailure(ex))
                    {
                        writeResult = Translate(ex, subject);
                    }

                    if (committed)
                    {
                        // On disk: recorded, and the batch goes on. The notification runs
                        // now that the write is final, outside the catch above (B48), so
                        // nothing it throws is taken for a failure of the write; a failed
                        // one is said once the batch ends.
                        records.Add(new VerificationRecord(stamp.ConceptId, stampedAt, stamp.ReplacedAt));
                        written.Add(stamp.Target.Key);
                        if (NotifyCommitted() is { } notificationError)
                        {
                            notificationFailure ??= notificationError["Error: ".Length..];
                        }

                        continue;
                    }

                    // The reason is the message without its prefix; an exception with an
                    // empty message leaves nothing, and a failure needs a reason (the
                    // constructor would throw, and RunTool would swallow the whole outcome).
                    var reason = writeResult.StartsWith("Error: ", StringComparison.Ordinal)
                        ? writeResult["Error: ".Length..]
                        : writeResult;
                    failure = new BundleWriteFailure(
                        stamp.Target.Key,
                        string.IsNullOrWhiteSpace(reason) ? "the write failed without giving a reason." : reason);
                    return records.Count == 0
                        ? writeResult
                        : $"{writeResult} — already written: {string.Join(", ", records.Select(r => r.ConceptId))}";
                }

                return notificationFailure is null
                    ? $"Recorded {prepared.Count} verification(s) by {by} at {stampedAt}."
                    : $"Recorded {prepared.Count} verification(s) by {by} at {stampedAt}; the write notification failed: {notificationFailure}";
            }
        }, () => subject);

        // On failure, Records is NOT emptied: it carries whatever reached disk
        // before the failure, so a caller can tell "nothing happened" from
        // "three of five were stamped and then it broke".
        var report = written.Count == 0 && failure is null
            ? BundleWriteReport.Empty
            : new BundleWriteReport(written, [], failure is null ? [] : [failure]);
        return message.StartsWith("Error:", StringComparison.Ordinal)
            ? new VerificationOutcome(false, message, records) { Report = report }
            : new VerificationOutcome(true, message, records) { Report = report };

        static VerificationOutcome Failed(string message) => new(false, message, []) { Report = BundleWriteReport.Empty };
    }

    /// <summary>A concept <see cref="PrepareStamp"/> read, stamped and checked, ready to be written: its new content and the version that content was built from.</summary>
    private readonly record struct PreparedStamp(ConceptTarget Target, string ConceptId, string Content, BundleObjectVersion Version, string? ReplacedAt);

    /// <summary>
    /// Reads <paramref name="target"/>'s current text through the provider, upserts
    /// <paramref name="by"/>'s stamp into its <c>verified</c> block in place and checks the
    /// result, exactly as <see cref="RecordVerifications"/> describes: <see langword="null"/> and
    /// <paramref name="prepared"/>, or the <c>Error: ...</c> refusal. Writes nothing. CALLER
    /// MUST hold the write lock.
    /// </summary>
    private string? PrepareStamp(ConceptTarget target, string conceptId, string by, string stampedAt, out PreparedStamp prepared)
    {
        prepared = default;
        if (ReadCurrent(target.Key) is not { } current)
        {
            return $"Error: concept {DebugQuote.Quote(conceptId)} does not exist.";
        }

        var text = current.Text;
        var document = OkfDocument.Parse(text);
        var map = document.Frontmatter.AsMapping();

        var upserted = UpsertStamp(map.Get("verified"), by, stampedAt, out var replacedAt);

        // LOAD-BEARING, not just "kept for replacedAt" (replacedAt
        // is UpsertStamp's own out-parameter, unaffected by this
        // call): `map` is the LIVE mapping `document.Frontmatter`
        // wraps (Frontmatter.AsMapping returns the instance
        // itself, no defensive copy -- see its own doc comment),
        // so this mutates `document` in place into exactly the
        // structure the edit below is SUPPOSED to produce. The
        // equality check further down compares the edit's actual
        // re-parsed result against THIS object -- without this
        // call, `document` would still hold the OLD `verified`
        // value and the equality check would fail on every
        // legitimate stamp.
        map.Insert("verified", upserted);

        // Surgical, not a re-emit: only the `verified:` block is
        // touched in the RAW text, so every other frontmatter key
        // -- comments, CRLF endings, folded/flow scalar spellings,
        // key order -- survives byte for byte. This is what
        // RecordVerifications's own doc comment already promises
        // ("preserving every other frontmatter key and the body")
        // and a full-frontmatter re-emit through YamlEmitter did
        // not deliver: YamlEmitter normalizes CRLF to LF, drops
        // comments, and reflows folded/flow scalars (see
        // FrontmatterBlockEdit's own doc comment). Deliberately
        // does NOT go through MaybeStampGenerated: this path
        // stamps `verified` only, never `generated`.
        var one = new YamlMapping();
        one.Insert("verified", upserted);
        var stampBlock = YamlEmitter.Emit(one);
        var content = FrontmatterBlockEdit.ReplaceTopLevelKey(text, "verified", stampBlock);

        // The edit is textual (FrontmatterBlockEdit does not
        // itself validate YAML), so re-parsing and checking FULL
        // STRUCTURAL EQUALITY against `document` -- which already
        // holds the exact intended result, per the load-bearing
        // `map.Insert` above -- is the proof that nothing else
        // moved: same frontmatter (key set, order, and every
        // OTHER value) and the same body, ordinally
        // (OkfDocument.Equals). A weaker check (e.g. only
        // counting `verified` entries) can pass on a genuinely
        // corrupted edit -- a mis-located key spelling that
        // inserted a shadowed duplicate elsewhere still re-parses
        // to the same COUNT while leaving a stale value in the
        // file (finding #C7-1). ValidateConformance (§11) runs
        // first since equality alone would not refuse a document
        // that was already missing `type` before this edit ran.
        var reparsed = OkfDocument.Parse(content);

        // Attributed here, at the SAME point this check has
        // always run (deliberately not moved earlier -- see the
        // early CheckVerificationTargets call above, which
        // excludes NotConformant for exactly this reason): a
        // bare DocumentValidationException naming no concept at
        // all is what CmdVerify and okf_verify used to work
        // around with a pre-loop of their own.
        try
        {
            reparsed.ValidateConformance();
        }
        catch (DocumentValidationException)
        {
            return FormatVerificationTargetProblem(
                new VerificationTargetProblem(VerificationTargetProblemKind.NotConformant, conceptId));
        }

        if (!reparsed.Equals(document))
        {
            // Diagnosable, not just "no": a bare "could not be
            // edited in place" gives a reader no way to tell a
            // real corruption apart from the one KNOWN false
            // positive this check has -- a NaN float anywhere in
            // the frontmatter never compares equal to itself
            // (YamlValue's float equality is IEEE-754 `==`), so a
            // concept with, say, `threshold: .nan` fails this
            // check on every verify, even a perfectly clean edit.
            // Not fixed here (changing YamlValue's equality is out
            // of scope for this method); named explicitly instead,
            // alongside the other diagnosable cases below. The
            // body case is checked first since this edit never
            // touches the body on purpose, so a body difference
            // is worth calling out distinctly from any
            // frontmatter-shaped explanation.
            var detail = !string.Equals(reparsed.Body, document.Body, StringComparison.Ordinal)
                ? "the body changed, which this edit never does on purpose"
                : ContainsNaN(document.Frontmatter.AsMapping())
                    ? "the frontmatter contains a NaN value, which never compares equal to "
                        + "itself, so this check cannot confirm the edit either way -- inspect "
                        + "the file by hand"
                    : DescribeFrontmatterDivergence(document.Frontmatter.AsMapping(), reparsed.Frontmatter.AsMapping());
            return $"Error: concept {DebugQuote.Quote(conceptId)}: the verified block could not be edited in place ({detail}).";
        }

        prepared = new PreparedStamp(target, conceptId, content, current.Version, replacedAt);
        return null;
    }

    /// <summary>
    /// Returns the <c>verified</c> sequence with <paramref name="by"/>'s stamp
    /// added, or replaced at its existing position. <see cref="YamlSequence"/>
    /// is immutable, so the list is rebuilt; only the FIRST entry matching the
    /// actor is replaced — a permissive reader accepts duplicates, and when
    /// <paramref name="existing"/> is already a sequence (or the single-entry
    /// mapping shape it is normalized into) this writer never deletes an
    /// entry it is not replacing. A malformed <c>verified</c> value that is
    /// neither — a bare scalar such as <c>verified: 2026-01-01</c> — is not
    /// preserved at all: it is discarded whole and replaced by a new
    /// single-entry sequence, the same input shape
    /// <see cref="DiagnosticCode.VerifiedMalformed"/> already reports.
    /// </summary>
    private static YamlSequence UpsertStamp(YamlValue? existing, string by, string at, out string? replacedAt)
    {
        replacedAt = null;

        var items = existing switch
        {
            YamlSequence sequence => new List<YamlValue>(sequence.Items),
            // `verified: { by, at }` — a bare mapping — is a shape ParseVerified
            // accepts, so normalize it into the list rather than discarding it.
            YamlMapping single => [single],
            _ => [],
        };

        var stamp = new YamlMapping();
        stamp.Insert("by", new YamlString(by));
        stamp.Insert("at", new YamlString(at));

        for (var i = 0; i < items.Count; i++)
        {
            if (items[i] is YamlMapping mapping
                && string.Equals(mapping.Get("by")?.AsDisplayString(), by, StringComparison.Ordinal))
            {
                replacedAt = mapping.Get("at")?.AsDisplayString();
                items[i] = stamp;
                return new YamlSequence(items);
            }
        }

        items.Add(stamp);
        return new YamlSequence(items);
    }

    /// <summary>
    /// Whether <paramref name="value"/> contains a NaN <see cref="YamlFloat"/>
    /// anywhere in its tree, mapping keys included. Exists
    /// solely to make the equality-refusal message in
    /// <see cref="RecordVerifications"/> diagnosable: <see cref="YamlValue"/>'s
    /// structural equality compares floats with IEEE-754 <c>==</c>, under
    /// which NaN never equals itself, so a frontmatter containing one (e.g.
    /// <c>threshold: .nan</c>) makes <c>reparsed.Equals(document)</c> return
    /// <see langword="false"/> on every verify attempt regardless of whether
    /// the edit itself was correct. Not a fix for that comparison (out of
    /// scope for this method) -- just enough to tell a caller WHY the check
    /// could not confirm the edit, instead of the generic message implying a
    /// real corruption.
    /// </summary>
    /// <remarks>
    /// A value is unequal to itself exactly when it holds a NaN somewhere:
    /// <see cref="YamlValue.Equals(object?)"/> takes no by-reference shortcut,
    /// every pair it meets comparing a value with itself has the same type and
    /// count, and among scalars only NaN fails <c>==</c>. So this reuses that
    /// comparison and inherits its guarantees (#158): no recursion, and an
    /// answer on any value, including one built in code that holds itself.
    /// <c>A_value_holding_NaN_is_unequal_even_to_itself</c> pins the absence of
    /// the shortcut this relies on.
    /// </remarks>
    internal static bool ContainsNaN(YamlValue value) => !value.Equals(value);

    /// <summary>
    /// Names WHICH part of the frontmatter actually diverged, for the
    /// equality-refusal message in <see cref="RecordVerifications"/> --
    /// cheaply, from the two already-parsed mappings, rather than asserting a
    /// fixed "some OTHER key changed" that is simply wrong whenever the
    /// divergence is inside <c>verified</c> itself (the common case: an edit
    /// that mis-located the block's own boundary re-parses to a `verified`
    /// value that does not match the one <see cref="UpsertStamp"/> computed,
    /// while every other key is untouched). Checks <c>verified</c> FIRST for
    /// exactly that reason. If neither `verified` nor any single OTHER
    /// top-level key can be pinned as the sole difference (e.g. the key SETS
    /// themselves differ), this returns an honest, unlocated description
    /// rather than guessing which key -- per the rule that a message should
    /// name a cause it actually checked, not one it merely suspects.
    /// </summary>
    internal static string DescribeFrontmatterDivergence(YamlMapping expected, YamlMapping actual)
    {
        // A YamlMapping keeps duplicate keys (the parser pushes entries raw),
        // and Get returns only the first, so compare EVERY verified entry, in
        // order and with multiplicity: an edit that adds, drops or alters a
        // second `verified` is a divergence of the verified block itself.
        var expectedVerified = expected.Entries.Where(IsVerified).Select(e => e.Value).ToList();
        var actualVerified = actual.Entries.Where(IsVerified).Select(e => e.Value).ToList();
        if (expectedVerified.Count != actualVerified.Count
            || !expectedVerified.SequenceEqual(actualVerified))
        {
            return "the verified block itself did not round-trip";
        }

        // Name a key only when exactly one position differs and both sides hold
        // the same key there (a value change). Anything else -- a changed count,
        // two or more differing positions, a key that moved or was inserted and
        // so shifted its neighbours -- cannot be pinned to one key, and naming
        // the first mismatch would name an unchanged or the wrong key.
        var expectedOthers = expected.Entries.Where(e => !IsVerified(e)).ToList();
        var actualOthers = actual.Entries.Where(e => !IsVerified(e)).ToList();
        if (expectedOthers.Count == actualOthers.Count)
        {
            var differing = new List<int>();
            for (var i = 0; i < expectedOthers.Count; i++)
            {
                if (!expectedOthers[i].Key.Equals(actualOthers[i].Key)
                    || !expectedOthers[i].Value.Equals(actualOthers[i].Value))
                {
                    differing.Add(i);
                }
            }

            if (differing.Count == 1 && expectedOthers[differing[0]].Key.Equals(actualOthers[differing[0]].Key))
            {
                var key = expectedOthers[differing[0]].Key;
                var name = key.AsDisplayString() ?? key.AsString() ?? "<non-string key>";

                // The key is stored text (a quoted YAML key can hold any
                // character), and this detail lands inside the one-line
                // refusal `okf verify` prints and `okf_verify` returns. A
                // name the shared predicate flags is quoted by the shared
                // quoter instead of echoed raw (#123); every other name keeps
                // its 'name' form.
                return LineSafeText.ContainsControlCharacter(name)
                    ? $"the frontmatter key {DebugQuote.Quote(name)} changed"
                    : $"the frontmatter key '{name}' changed";
            }
        }

        return "the frontmatter changed outside the verified block";

        static bool IsVerified((YamlValue Key, YamlValue Value) entry) =>
            string.Equals(entry.Key.AsString(), "verified", StringComparison.Ordinal);
    }

    /// <summary>A validated concept id and the bundle key of its document, produced by <see cref="ValidateConceptTarget"/>.</summary>
    private readonly record struct ConceptTarget(ConceptId Id, BundleKey Key);

    /// <summary>
    /// Validates <paramref name="conceptId"/> — parseable, not the reserved
    /// <c>index</c>/<c>log</c> name — and the bundle key of its document
    /// (<c>{id}.md</c>) under the writer's key policy (spec §2, decision B2):
    /// the provider's native constraints under the options' policy, plus any
    /// extra validator, exactly as a load checks the same key. Shared by every
    /// write method so they can never diverge on what counts as a valid write
    /// target. Pure: no I/O, no lock.
    /// </summary>
    /// <remarks>
    /// <b>Path guards live behind the provider.</b> Whether the key's path goes
    /// through a symlink or a junction, or an entry that cannot be inspected,
    /// whether it stays inside the bundle root, and whether the volume would
    /// confuse the name with another, is the provider's to decide at the moment
    /// of the write (<see cref="FileSystemBundleProvider"/>: a STRICT check of
    /// the whole path before anything is listed, again after the parent
    /// directories exist, and again under its commit lock just before the
    /// commit). Its refusals come back as exceptions, which <see cref="Translate"/>
    /// turns into this class's own messages. The checks and the commit are still
    /// separate system calls, so a substitution by another local actor racing
    /// the last of them is a residual, documented limitation (issue #178); the
    /// threat model is unchanged — an actor without write access to the bundle
    /// tree planting a reparse point ahead of time, or a stale one left over.
    /// </remarks>
    /// <returns>
    /// <see langword="null"/> and a populated <paramref name="target"/> on
    /// success; otherwise the <c>Error: ...</c> message to return to the
    /// caller (and <paramref name="target"/> is <see langword="default"/>).
    /// </returns>
    private string? ValidateConceptTarget(string conceptId, out ConceptTarget target)
    {
        target = default;

        if (!ConceptId.TryParse(conceptId, out var id))
        {
            return $"Error: invalid concept id {DebugQuote.Quote(conceptId)}. Concept ids are '/'-separated "
                + "segments matching [A-Za-z0-9_][A-Za-z0-9_.-]*.";
        }

        if (string.Equals(id.Name, "index", StringComparison.OrdinalIgnoreCase)
            || string.Equals(id.Name, "log", StringComparison.OrdinalIgnoreCase))
        {
            return $"Error: '{id}' is a reserved concept id — the last segment must not be "
                + "'index' or 'log' in any casing (these would collide with the bundle's "
                + "index.md/log.md files on case-insensitive filesystems).";
        }

        if (!BundleKey.TryParse(id + ".md", out var key, out var reason))
        {
            return $"Error: '{id}' cannot be a bundle key: {reason}.";
        }

        if (_validator.CheckKey(key) is { } refused)
        {
            return $"Error: '{id}' cannot be written under the bundle's key policy: {refused}.";
        }

        target = new ConceptTarget(id, key);
        return null;
    }

    /// <summary>
    /// Checks whether every id in <paramref name="conceptIds"/> names a valid, existing,
    /// parseable, §11-conformant concept, with no two ids resolving to the same file — the single
    /// governed check behind <see cref="RecordVerifications"/> (called first thing, so its own
    /// refusal names the offender), the <c>okf verify</c> CLI verb, and the <c>okf_verify</c> tool.
    /// Before this method existed, the CLI and the tool each re-implemented an id-validity/existence/
    /// conformance loop of their own purely to phrase a nicer message than <see cref="RecordVerifications"/>'s
    /// own (then-unattributed) refusal — three spellings of the same §11 floor. This is the one.
    ///
    /// Reads only the <c>k</c> concept documents named in <paramref name="conceptIds"/>, through the
    /// provider (<see cref="IBundleProvider.Exists"/> and <see cref="IBundleProvider.Read"/>, whose
    /// names match exactly) — never <see cref="Bundle.Load(string)"/> — so answering a k-id question
    /// costs O(k) reads, not a full bundle parse. Read-only: it is not guarded by
    /// <see cref="BundleProviderCapabilities.CanWrite"/>.
    /// </summary>
    /// <returns>
    /// <see langword="null"/> if every id is a valid, existing, parseable, conformant concept with
    /// no duplicate among the resolved paths; otherwise the FIRST offender this method finds,
    /// checking id validity for every id first, then the duplicate-by-resolved-path rule across
    /// every id, then existence/parse/§11 per id in <paramref name="conceptIds"/> order. For any
    /// ONE id, this is the same order <see cref="RecordVerifications"/>'s own prepare loop checks
    /// that id in afterwards, up through §11 conformance (the loop's own remaining checks — the
    /// fence/NaN-float/deep-nesting refusals — start only past that point, since this method's
    /// conformance check does not cover them). It is NOT the same ACROSS ids in a batch with more
    /// than one problem: this method resolves validity, duplicates and existence/parse/§11 for the
    /// WHOLE batch before the prepare loop ever starts, so a LATER id's not-found, unparseable,
    /// unreadable, invalid-id or duplicate problem (all caught here) now wins over an EARLIER id's
    /// fence, NaN-float or deep-nesting refusal (caught only downstream, in the prepare loop this
    /// method now runs ahead of) — the reverse of what the old single combined per-id loop
    /// reported. Both answers are correct refusals of the same batch; only which one is named
    /// first can move.
    /// </returns>
    internal VerificationTargetProblem? CheckVerificationTargets(IReadOnlyList<string> conceptIds)
    {
        // CLI/agent callers also use this preflight on its own. Serialize its
        // reads against writers; RecordVerifications keeps this reentrant
        // monitor held through the subsequent prepare/write as well.
        using (EnterWriteLock())
        {
            return CheckVerificationTargetsLocked(conceptIds);
        }
    }

    private VerificationTargetProblem? CheckVerificationTargetsLocked(IReadOnlyList<string> conceptIds)
    {
        var targets = new List<ConceptTarget>(conceptIds.Count);
        for (var i = 0; i < conceptIds.Count; i++)
        {
            var targetError = ValidateConceptTarget(conceptIds[i], out var target);
            if (targetError is not null)
            {
                return new VerificationTargetProblem(VerificationTargetProblemKind.InvalidId, conceptIds[i], targetError);
            }

            targets.Add(target);
        }

        // Duplicates are refused, not silently collapsed: preparing the same
        // file twice would build both versions from the same original
        // content and write it twice, reporting two records for the single
        // stamp that survives — a result that reads like two reviews.
        //
        // Checked on the RESOLVED target path, not the raw id string that was
        // passed in, and case-INSENSITIVELY. Note this is NOT the
        // "Windows/macOS are case-insensitive" heuristic Bundle.cs explicitly
        // rejects (see Bundle.PathComparison): case-sensitivity is a property
        // of the volume, not the OS, so no OS test could decide this
        // correctly either way. The comparison is deliberately pessimistic
        // instead — a batch is refused whenever two ids COULD name one file —
        // because the cost of the two errors is not symmetric: collapsing two
        // spellings on a case-insensitive volume silently double-reports a
        // single stamp, while the residual here is that on a case-SENSITIVE
        // volume genuinely holding both metrics/dau.md and metrics/DAU.md, a
        // batch naming both is refused and must be run as two. Stated rather
        // than reasoned away: that refusal is real, and this is the same call
        // the BundleLocks registry makes for the same class of bug.
        //
        // Formerly duplicated inside RecordVerifications's own prepare loop;
        // that copy is gone now that this method runs first and applies the
        // identical rule to the whole batch before the prepare loop's targets
        // list is even built, making the old copy unreachable dead code.
        var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < targets.Count; i++)
        {
            if (!seenKeys.Add(targets[i].Key.ToString()))
            {
                return new VerificationTargetProblem(VerificationTargetProblemKind.DuplicateName, conceptIds[i]);
            }
        }

        for (var i = 0; i < targets.Count; i++)
        {
            var target = targets[i];
            try
            {
                // The provider matches every segment of the key EXACTLY
                // (spec §2): an id like "METRICS/DAU" is not found for an
                // on-disk metrics/dau.md on any OS or volume, the same answer
                // Bundle.Load gives — it builds each concept's id from the
                // stored names, and ConceptId equality is ordinal.
                if (!_provider.Exists(target.Key))
                {
                    return new VerificationTargetProblem(VerificationTargetProblemKind.NotFound, conceptIds[i]);
                }

                // One spelling of §11 conformance (OkfDocument.ValidateConformance
                // itself), not a second hand-rolled `Get("type")` check beside it.
                OkfDocument.Parse(Bundle.ReadText(_provider, target.Key, _limits, new byte[Bundle.ReadChunkBytes], out _)).ValidateConformance();
            }
            catch (BundleAccessRefusedException ex)
            {
                // A path the provider refuses (a reparse point, an entry that
                // cannot be inspected) is an invalid target, as when the
                // writer checked the path itself; same message as a write.
                return new VerificationTargetProblem(VerificationTargetProblemKind.InvalidId, conceptIds[i], Translate(ex, $"'{target.Id}'"));
            }
            catch (BundleObjectNotFoundException)
            {
                return new VerificationTargetProblem(VerificationTargetProblemKind.NotFound, conceptIds[i]);
            }
            catch (DocumentValidationException)
            {
                return new VerificationTargetProblem(VerificationTargetProblemKind.NotConformant, conceptIds[i]);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Never let an expected I/O condition (a file held open
                // exclusively by another process, a permissions error)
                // escape as an unhandled exception -- Bundle.Load wraps the
                // same two exception types into BundleLoadException so
                // `okf validate`/`info`/etc. report a clean `error:` line
                // instead of a stack trace; this check owes verify the same.
                return new VerificationTargetProblem(VerificationTargetProblemKind.Unreadable, conceptIds[i], ex.Message);
            }
            catch (Exception ex) when (ex is OkfException or System.Text.DecoderFallbackException)
            {
                return new VerificationTargetProblem(VerificationTargetProblemKind.ParseFailure, conceptIds[i], ex.Message);
            }
        }

        return null;
    }

    /// <summary>
    /// Renders a <see cref="VerificationTargetProblem"/> in the wording <see cref="RecordVerifications"/>
    /// and the <c>okf_verify</c> tool already read for <see cref="VerificationTargetProblemKind.NotFound"/>
    /// and <see cref="VerificationTargetProblemKind.DuplicateName"/> — an <c>Error: </c>-prefixed,
    /// period-terminated sentence. <see cref="VerificationTargetProblemKind.NotConformant"/> is
    /// phrased the same way for the first time HERE (it used to reach a caller only as the bare,
    /// unattributed <see cref="DocumentValidationException"/> message); so is
    /// <see cref="VerificationTargetProblemKind.ParseFailure"/> and
    /// <see cref="VerificationTargetProblemKind.Unreadable"/>, both of which append
    /// <see cref="VerificationTargetProblem.Detail"/> — the underlying exception's own message —
    /// since a bare "could not be parsed"/"could not be read" with no cause is a worse diagnostic
    /// than the writer already had before this method existed (the unattributed message at least
    /// carried the parser's own detail). <see cref="VerificationTargetProblemKind.InvalidId"/>
    /// returns <see cref="VerificationTargetProblem.Detail"/> as captured by
    /// <see cref="CheckVerificationTargets"/> from <see cref="ValidateConceptTarget"/>'s own return
    /// value, or from the provider's refusal of the path — not re-derived here, since asking the
    /// provider a second time is both wasted I/O and a second window for that answer to change
    /// underneath it.
    /// </summary>
    private static string FormatVerificationTargetProblem(VerificationTargetProblem problem) => problem.Kind switch
    {
        VerificationTargetProblemKind.InvalidId => problem.Detail!,
        VerificationTargetProblemKind.NotFound =>
            $"Error: concept {DebugQuote.Quote(problem.ConceptId)} does not exist.",
        // DetailAsSentence, not "{Detail}.": an IOException's message already
        // ends in a period, and appending one doubled it (see that method).
        VerificationTargetProblemKind.ParseFailure =>
            $"Error: concept {DebugQuote.Quote(problem.ConceptId)} could not be parsed as a valid OKF document: {problem.DetailAsSentence()}",
        VerificationTargetProblemKind.Unreadable =>
            $"Error: concept {DebugQuote.Quote(problem.ConceptId)} could not be read: {problem.DetailAsSentence()}",
        VerificationTargetProblemKind.NotConformant =>
            $"Error: concept {DebugQuote.Quote(problem.ConceptId)} has no `type` and is not §11-conformant.",
        VerificationTargetProblemKind.DuplicateName =>
            $"Error: concept {DebugQuote.Quote(problem.ConceptId)} is named more than once.",
        _ => throw new ArgumentOutOfRangeException(nameof(problem)),
    };

    /// <summary>
    /// Parses <paramref name="frontmatterYaml"/> once and delegates the auto-stamp decision to
    /// <see cref="MaybeStampGenerated"/>. Returns the parsed (and possibly stamped) <see cref="YamlValue"/>
    /// for <see cref="BuildValidatedContent(YamlValue, string)"/> to validate and serialize directly —
    /// so the write path parses exactly once, never re-serializing and re-parsing a stamped mapping.
    /// Throws <see cref="Yaml.YamlParseException"/> on malformed frontmatter, caught by the caller's
    /// <see cref="RunTool"/> wrapper before anything is written.
    /// </summary>
    private YamlValue ParseFrontmatterAndMaybeStamp(string frontmatterYaml)
    {
        var parsed = YamlValue.Parse(frontmatterYaml);
        if (parsed is YamlMapping map)
        {
            MaybeStampGenerated(map);
        }

        return parsed;
    }

    /// <summary>
    /// When <see cref="AutoStampGenerated"/> is on and <paramref name="map"/> has no <c>generated</c>
    /// key of its own, stamps a <c>generated: { by, at }</c> block (§5.2) into it **in place** — the
    /// single stamping decision shared by the string-based <see cref="WriteConcept(string, string, string)"/>
    /// path (via <see cref="ParseFrontmatterAndMaybeStamp"/>, on a freshly parsed, caller-invisible
    /// mapping) and the <see cref="Frontmatter"/>-based <see cref="WriteConcept(string, Frontmatter, string)"/>
    /// overload (which passes a defensive copy — see that overload's remarks — precisely so this
    /// in-place mutation never reaches the caller's own <see cref="Frontmatter"/> object). No-op when
    /// the flag is off or a <c>generated</c> key is already present.
    /// </summary>
    private void MaybeStampGenerated(YamlMapping map)
    {
        if (AutoStampGenerated && !map.ContainsKey("generated"))
        {
            var generated = new YamlMapping();
            generated.Insert("by", new YamlString(ProducerActor));
            generated.Insert("at", new YamlString(OkfTimestamp.FormatUtc(UtcNow())));
            map.Insert("generated", generated);
        }
    }

    /// <summary>
    /// Parses <paramref name="frontmatterYaml"/> and delegates to the
    /// <see cref="BuildValidatedContent(YamlValue, string)"/> overload. The
    /// <see cref="AppendToConceptAtomic"/> path enters here (it parses only
    /// once); <see cref="WriteConcept(string, string, string)"/> parses up front in
    /// <see cref="ParseFrontmatterAndMaybeStamp"/> and enters the overload
    /// directly. Throws <see cref="Yaml.YamlParseException"/> (line-tagged
    /// message) on malformed input, caught by the caller's
    /// <see cref="RunTool"/> wrapper before anything is written.
    /// </summary>
    private static (string? Content, string? Error) BuildValidatedContent(string frontmatterYaml, string body) =>
        BuildValidatedContent(YamlValue.Parse(frontmatterYaml), body);

    /// <summary>
    /// Builds and validates the <see cref="OkfDocument"/> for the already-parsed
    /// <paramref name="frontmatter"/> against <paramref name="body"/>, then
    /// serializes it — the exact producer-grade validation
    /// <see cref="WriteConcept(string, string, string)"/> performs, shared verbatim with
    /// <see cref="AppendToConceptAtomic"/> so the two can never validate
    /// divergently. Throws <see cref="DocumentValidationException"/> (failed
    /// producer validation), caught by the caller's <see cref="RunTool"/>
    /// wrapper, rather than returning an error for that case; only
    /// "frontmatter parses but isn't a mapping" is reported via the returned
    /// <c>Error</c> string.
    /// </summary>
    private static (string? Content, string? Error) BuildValidatedContent(YamlValue frontmatter, string body)
    {
        Frontmatter? fm = frontmatter switch
        {
            YamlNull => new Frontmatter(),
            YamlMapping map => Frontmatter.FromMapping(map),
            _ => null,
        };

        if (fm is null)
        {
            return (null, "Error: frontmatter must be a YAML mapping of 'key: value' lines, not a list or scalar.");
        }

        var doc = new OkfDocument(fm, body);

        // Strict producer validation BEFORE any write. On failure this
        // throws DocumentValidationException (message lists MissingKeys),
        // caught by RunTool -- nothing is written for a failed write.
        doc.Validate();

        return (doc.Serialize(), null);
    }

    /// <summary>
    /// Writes already-validated <paramref name="content"/> as <paramref name="target"/>'s
    /// document through the provider, under <paramref name="condition"/>, and reports the
    /// commit through <paramref name="committed"/>. CALLER MUST already hold the write lock
    /// (<see cref="EnterWriteLock"/>) — this method does not acquire it itself, so that
    /// <see cref="AppendToConceptAtomic"/> and <see cref="RecordVerifications"/> can enclose
    /// their own preceding read-and-transform in the SAME lock acquisition as this write.
    /// The write primitive of concept writes and verification stamps: every overload of
    /// <see cref="WriteConcept(string, string, string)"/>, <see cref="AppendToConceptAtomic"/> and
    /// <see cref="RecordVerifications"/> write through here.
    /// </summary>
    /// <remarks>
    /// <para>A creation — the key absent at the provider — is first checked against the tree
    /// it would produce (<see cref="BundleTreeCheck"/>, spec §2, decision B10): a case or
    /// Unicode-form twin under a policy that folds names, an object on a proper prefix, or
    /// objects under the key, is refused here with a message naming the conflicting name,
    /// before anything is written. An update skips it.</para>
    /// <para>Provider exceptions propagate (the caller's <see cref="RunTool(Func{string}, Func{string?}?)"/>
    /// translates them, or the caller retries a conflict): nothing has then been written
    /// and <paramref name="committed"/> is not called, except for an outcome the
    /// provider could not establish (<see cref="BundleWriteOutcomeUnknownException"/>).</para>
    /// <para>Once <see cref="IBundleProvider.Write"/> has returned, the write is final: this
    /// method calls <paramref name="committed"/> and returns its confirmation, and nothing it
    /// does after the commit can throw. It never invokes <see cref="_onWriteCommitted"/>: the
    /// caller does, once its whole write operation — every attempt of a read-modify-write
    /// included — has returned (<see cref="WriteThenNotify"/>, <see cref="NotifyCommitted"/>), so
    /// that nothing the notification throws can be taken for a failure of the storage
    /// operation (decision B48).</para>
    /// </remarks>
    /// <param name="target">The concept being written.</param>
    /// <param name="content">The fully serialized, already-validated document content to write.</param>
    /// <param name="condition">The provider condition: <see cref="BundleWriteCondition.None"/> for an unconditional write.</param>
    /// <param name="existedBefore">
    /// Whether the object already existed, if the caller already knows it from a read made
    /// under the same write-lock hold. <see langword="null"/> means "not checked yet": this
    /// method asks the provider (<see cref="IBundleProvider.Exists"/>).
    /// </param>
    /// <param name="committed">Called once the provider has committed the write: the caller then notifies (<see cref="NotifyCommitted"/>).</param>
    private string WriteValidatedContentLocked(ConceptTarget target, string content, BundleWriteCondition condition, Action committed, bool? existedBefore = null)
    {
        var existed = existedBefore ?? _provider.Exists(target.Key);
        if (!existed
            && condition.Kind != BundleWriteConditionKind.IfMatch // never a creation
            && BundleTreeCheck.CheckCreation(_provider, _validator, target.Key, _limits) is { } conflict)
        {
            return $"Error: {conflict}.";
        }

        var bytes = OkfEncodings.NoBom.GetBytes(content);
        _provider.Write(target.Key, bytes, condition);
        committed();

        var status = existed ? "updated" : "new";
        return $"Written {target.Id} ({status}, {bytes.Length} bytes). Remember to run okf_regenerate_indexes.";
    }

    /// <summary>
    /// Runs one write operation of a <c>string</c> entry point — a single write, or every attempt
    /// of a read-modify-write — and, if it committed, invokes the write notification once that
    /// operation has returned (<see cref="NotifyCommitted"/>). CALLER MUST hold the write lock:
    /// the notification runs under it, atomically with the write it follows (#131).
    /// </summary>
    /// <param name="write">The operation; it calls the action it is given once the provider has committed.</param>
    /// <returns>What <paramref name="write"/> returned, or the <c>Error: …</c> text of a notification that failed.</returns>
    private string WriteThenNotify(Func<Action, string> write)
    {
        var committed = false;
        var result = write(() => committed = true);
        return committed && NotifyCommitted() is { } failure ? failure : result;
    }

    /// <summary>
    /// Invokes <see cref="_onWriteCommitted"/> once, after a committed write, outside every retry
    /// and every catch of a conflict (decision B48): the write is final, so nothing the
    /// notification throws is a failure of the write — it is never retried, never read as a
    /// refused condition. An exception the writer converts (<see cref="IsExpectedFailure"/>: an
    /// I/O error, an <see cref="OkfException"/>, a conflict raised by another operation included)
    /// is returned as the writer's <c>Error: …</c> text; any other — an
    /// <see cref="OperationCanceledException"/>, a host's bug — propagates to the caller unchanged,
    /// the write committed. CALLER MUST hold the write lock.
    /// </summary>
    /// <returns><see langword="null"/>, or the <c>Error: …</c> text of the notification's failure.</returns>
    private string? NotifyCommitted()
    {
        try
        {
            _onWriteCommitted?.Invoke();
            return null;
        }
        catch (Exception ex) when (IsExpectedFailure(ex))
        {
            // The exception's own message, byte for byte (B46): the write committed, so
            // Translate's provider-refusal texts would describe a failure that did not happen.
            return $"Error: {ex.Message}";
        }
    }

    /// <summary>
    /// The current text and version of <paramref name="key"/>, read through the provider as
    /// strict UTF-8 within the writer's bounds (<see cref="Bundle.ReadText"/>, the core's one
    /// bounded reader), or <see langword="null"/> when no object exists under it. A non-UTF-8
    /// object throws <see cref="System.Text.DecoderFallbackException"/> rather than being
    /// silently re-decoded and rewritten.
    /// </summary>
    private (string Text, BundleObjectVersion Version)? ReadCurrent(BundleKey key)
    {
        try
        {
            var text = Bundle.ReadText(_provider, key, _limits, new byte[Bundle.ReadChunkBytes], out var version);
            return (text, version);
        }
        catch (BundleObjectNotFoundException)
        {
            return null;
        }
    }

    /// <summary>The condition of a read-modify-write: the version read, or no object at all.</summary>
    private static BundleWriteCondition ConditionFor((string Text, BundleObjectVersion Version)? current) =>
        current is { } c ? BundleWriteCondition.IfMatch(c.Version) : BundleWriteCondition.MustNotExist;

    /// <summary>
    /// A write under a caller's condition (<see cref="WriteConcept(string, string, string, BundleWriteCondition)"/>):
    /// never retried, and a condition that does not hold becomes the writer's own message. CALLER
    /// MUST hold the write lock. The conflict caught here can only be the provider's refusal of the
    /// write: the notification runs after this method has returned (<see cref="WriteThenNotify"/>,
    /// decision B48).
    /// </summary>
    private string WriteConditionedLocked(ConceptTarget target, string content, BundleWriteCondition condition, Action committed)
    {
        try
        {
            return WriteValidatedContentLocked(target, content, condition, committed);
        }
        catch (BundleWriteConflictException e) when (e.Reason == BundleWriteConflictReason.ConditionFailed && condition.Kind != BundleWriteConditionKind.None)
        {
            return condition.Kind == BundleWriteConditionKind.MustNotExist
                ? $"Error: '{target.Id}' already exists; the write was refused. Read it before writing it."
                : $"Error: '{target.Id}' changed since it was read, or no longer exists; the write was refused. Read it again before writing it.";
        }
    }

    /// <summary>How many times a read-modify-write is attempted before a conflict is returned (spec §4, decision B5).</summary>
    internal const int MaxAttempts = 3;

    /// <summary>
    /// How a read-modify-write waits before its next attempt; <see cref="Thread.Sleep(TimeSpan)"/>,
    /// on the caller's thread, while the contract is synchronous (issue #152). A test replaces it
    /// to record the delays instead of sleeping.
    /// </summary>
    internal Action<TimeSpan> Sleep { get; set; } = Thread.Sleep;

    /// <summary>The random, growing delay after failed attempt <paramref name="attempt"/>: 10–40 ms after the first, 30–120 ms after the second (decision B5).</summary>
    internal static TimeSpan RetryDelay(int attempt) =>
        TimeSpan.FromMilliseconds(attempt == 1 ? Random.Shared.Next(10, 41) : Random.Shared.Next(30, 121));

    /// <summary>
    /// Runs one read-modify-write <paramref name="attempt"/> (called with 1, 2, 3) until it does not
    /// meet a <see cref="BundleWriteConflictReason.ConditionFailed"/> conflict, waiting
    /// <see cref="RetryDelay"/> between attempts. Each attempt must re-read the current state. After
    /// <see cref="MaxAttempts"/> conflicting attempts, returns the writer's message for
    /// <paramref name="subject"/>. CALLER MUST hold the write lock: the conflicts come from writers
    /// outside it (another process, a writer over another provider instance with another lock).
    /// An attempt holds the storage operation only — never the write notification, which its
    /// caller invokes once this method has returned (decision B48) — so a conflict caught here is
    /// always a refused write, never something that happened after a commit.
    /// </summary>
    private string WithRetries(string subject, Func<string> attempt)
    {
        for (var n = 1; ; n++)
        {
            try
            {
                return attempt();
            }
            catch (BundleWriteConflictException e) when (e.Reason == BundleWriteConflictReason.ConditionFailed)
            {
                if (n == MaxAttempts)
                {
                    return KeptChanging(subject);
                }

                Sleep(RetryDelay(n));
            }
        }
    }

    /// <summary>The writer's message once every attempt of a read-modify-write met a conflict.</summary>
    private static string KeptChanging(string subject) =>
        $"Error: {subject} kept changing while it was being written; the write was refused after {MaxAttempts} attempts.";

    /// <summary>
    /// The capability guard of every write method (spec §3, decision B3): checked first,
    /// before any argument, read, lock or listing, so a refused call has no effect at all.
    /// A read-only provider is a host mistake, not a content error: the exception is thrown,
    /// never returned as an <c>Error:</c> string.
    /// </summary>
    private void GuardWritable()
    {
        if (!_provider.Capabilities.CanWrite)
        {
            throw new BundleReadOnlyException(_provider.Id);
        }
    }

    /// <summary>
    /// Runs a write-method body, converting any exception that a well-formed
    /// but unlucky input could still trigger — a producer-validation failure
    /// (<see cref="OkfException"/>, e.g. <see cref="DocumentValidationException"/>),
    /// a provider refusal or conflict (<see cref="BundleProviderException"/>),
    /// a rejected argument surfaced late by a BCL API (<see cref="ArgumentException"/>),
    /// a storage read/write failure (<see cref="IOException"/>,
    /// <see cref="UnauthorizedAccessException"/>), or a strict-UTF-8 decode
    /// failure reading an existing document (<see cref="System.Text.DecoderFallbackException"/>)
    /// — into a plain-text message (<see cref="Translate"/>). This is the single
    /// enforcement point for the "never throw for an expected error" rule.
    /// <see cref="BundleReadOnlyException"/> is not converted: it is a host mistake.
    /// </summary>
    /// <param name="body">The method body.</param>
    /// <param name="subject">How the message names the object being written, read when an exception is converted (<c>'tables/x'</c>, <c>log.md</c>); <see langword="null"/>, or returning <see langword="null"/>, before the body knows it.</param>
    private static string RunTool(Func<string> body, Func<string?>? subject = null)
    {
        try
        {
            return body();
        }
        catch (Exception ex) when (IsExpectedFailure(ex))
        {
            return subject?.Invoke() is { } s ? Translate(ex, s) : $"Error: {ex.Message}";
        }
    }

    /// <summary>The exceptions <see cref="RunTool"/> converts into an <c>Error:</c> string.</summary>
    private static bool IsExpectedFailure(Exception ex) =>
        ex is not BundleReadOnlyException
        && ex is OkfException or ArgumentException or IOException or UnauthorizedAccessException or System.Text.DecoderFallbackException;

    /// <summary>
    /// The writer's message for a failure while writing <paramref name="subject"/> (spec §7: for the
    /// typed provider exceptions, built from the exception's type and fields, never from the
    /// provider's own text). A path the provider refuses keeps the message the writer gave its own
    /// reparse-point guard before the write moved behind the provider (decision B15); a name the
    /// storage cannot hold gets a library-authored one. Anything else is the exception's message
    /// unchanged — including a raw <see cref="IOException"/> or
    /// <see cref="UnauthorizedAccessException"/>, whose .NET message may name a host path, exactly
    /// as the writer reported a <see cref="File.WriteAllText(string, string)"/> failure before
    /// (issue #211 tracks path-free I/O texts).
    /// </summary>
    private static string Translate(Exception ex, string subject) => ex switch
    {
        BundleAccessRefusedException { Key: not null } =>
            $"Error: {subject} resolves through a reparse point (symlink/junction), or an entry that could not be inspected, inside the bundle, which is not allowed.",
        BundleWriteConflictException { Reason: BundleWriteConflictReason.NameUnavailable } =>
            $"Error: {subject} cannot be stored under that name: the bundle holds an entry it would confuse with it (a directory, or a name differing only in case or Unicode form), or cannot hold the name.",
        _ => $"Error: {ex.Message}",
    };
}
