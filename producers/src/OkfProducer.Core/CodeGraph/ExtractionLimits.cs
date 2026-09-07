// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OkfProducer.Core.CodeGraph;

/// <summary>
/// Hostile-input guards a single extraction run must respect. Task 1 only carries these values
/// through; Task 4 is what makes an <c>ILanguageExtractor</c> actually enforce them.
/// </summary>
/// <param name="MaxFileBytes">
/// The largest file an extractor will open. Enforced from the file's reported length before any byte
/// is read (see <c>TreeSitterExtractor.TryReadSource</c>), so an oversized file costs a stat call,
/// not a parse.
/// </param>
/// <param name="MaxDepth">
/// The deepest directory nesting <see cref="CodeGraphBuilder.Build"/> will walk into, counted in path
/// segments and checked before a file handle is opened.
/// </param>
/// <param name="Timeout">
/// A <b>between-files</b> deadline, and deliberately documented as nothing more. It is checked once
/// per file, before that file is handed to the extractor
/// (<see cref="CodeGraphBuilder.Build"/>), so it bounds how many files a run will start -- it does
/// <b>not</b> bound how long any single one of them takes. A file that parses pathologically slowly
/// runs to completion however long that is, and neither this value nor a caller's
/// <see cref="System.Threading.CancellationToken"/> interrupts it.
///
/// <para>
/// That gap is a property of the parser this producer builds on, measured rather than assumed: the
/// public surface of <c>TreeSitter.DotNet</c> 1.3.0 exposes <c>Parser.Parse(string)</c> and
/// <c>Parser.Parse(string, Tree)</c> and nothing else that could carry a deadline -- no
/// cancellation-token overload, no options argument, no timeout property. Its internal P/Invoke layer
/// declares no <c>ts_parser_parse_with_options</c>, no <c>ts_parser_set_timeout_micros</c> and no
/// <c>ts_parser_set_cancellation_flag</c> either, and its <c>Parser</c> handle is <c>internal</c>, so
/// there is no supported route to the native progress callback (which the native
/// <c>tree-sitter.dll</c> shipped in that same package does export) from outside the package.
/// Wrapping the call in a <c>Task</c> would not close the gap -- an unabortable native call keeps
/// running, and a run that abandons a thread to it is strictly worse than one that waits -- so the
/// bound is documented as what it is instead of being faked. Closing it needs the wrapper to expose
/// the progress callback; until then an operator's only real bound on a single pathological file is
/// <see cref="MaxFileBytes"/> and, outside this process, killing the run.
/// </para>
///
/// <para>
/// <b>That trade has a named expiry, and it is not "when someone gets around to it".</b> It rests
/// on <c>okfgen</c> being an interactive, local, operator-driven command: Ctrl-C is a real bound
/// because there is an operator at a terminal to press it, and <see cref="MaxFileBytes"/>' 2 MB cap
/// keeps the input small enough that a pathological parse is unlikely to reach a human's patience
/// first. <b>The first time this extraction path runs anywhere non-interactive -- a CI step, an MCP
/// or agent tool, a scheduled or server-side run -- that operator disappears and an unbounded parse
/// becomes a hang with nothing to stop it.</b> Treat that as the trigger to close the gap for real,
/// not as a new occasion to re-document it. The native <c>tree-sitter.dll</c> in this same package
/// already exports <c>ts_parser_parse_with_options</c>, so the work is in the binding
/// (a wrapper upgrade that surfaces the progress callback, or replacing the binding), not in the
/// parser.
/// </para>
/// </param>
public sealed record ExtractionLimits(long MaxFileBytes, int MaxDepth, TimeSpan Timeout)
{
    /// <summary>
    /// Refuses a value that cannot mean what a limit means, at construction rather than mid-run.
    ///
    /// <para>A non-positive <see cref="Timeout"/> is the one that mattered: it cancels the linked
    /// source immediately, so <c>CodeGraphBuilder.Build</c> raised out of the walk instead of coming
    /// back with an incomplete <see cref="RunStatus"/> -- the honest reporting path this type exists
    /// to feed. An operator who typed a bad number got a stack trace where the design promises a run
    /// that says what it could not do. A non-positive <see cref="MaxFileBytes"/> or
    /// <see cref="MaxDepth"/> is refused for the same reason: it does not bound a run, it empties one,
    /// and doing so silently is worse than saying no.</para>
    /// </summary>
    private readonly long _maxFileBytes = Positive(MaxFileBytes, nameof(MaxFileBytes));
    private readonly int _maxDepth = (int)Positive(MaxDepth, nameof(MaxDepth));
    private readonly TimeSpan _timeout = PositiveDuration(Timeout);

    /// <inheritdoc cref="MaxFileBytes"/>
    public long MaxFileBytes
    {
        get => _maxFileBytes;
        init => _maxFileBytes = Positive(value, nameof(MaxFileBytes));
    }

    /// <inheritdoc cref="MaxDepth"/>
    public int MaxDepth
    {
        get => _maxDepth;
        init => _maxDepth = (int)Positive(value, nameof(MaxDepth));
    }

    /// <inheritdoc cref="Timeout"/>
    public TimeSpan Timeout
    {
        get => _timeout;
        init => _timeout = PositiveDuration(value);
    }

    /// <summary>
    /// Refuses a bound that does not bound.
    ///
    /// <para>Written as explicit <c>init</c> accessors over backing fields, not as property
    /// initialisers, and the difference is not stylistic: a record's <c>with</c> expression copies
    /// backing fields through the compiler-generated copy constructor and <b>does not re-run property
    /// initialisers</b>. The initialiser form validated the primary constructor only, and
    /// <c>ExtractionLimits.Default with { Timeout = ... }</c> -- which is how every caller in this
    /// solution builds one -- sailed straight past it. Measured, by a test that failed.</para>
    /// </summary>
    private static long Positive(long value, string name) =>
        value > 0 ? value : throw new ArgumentOutOfRangeException(name, value, "must be positive.");

    /// <inheritdoc cref="Positive"/>
    private static TimeSpan PositiveDuration(TimeSpan value) =>
        value > TimeSpan.Zero
            ? value
            : throw new ArgumentOutOfRangeException(nameof(Timeout), value, "must be a positive duration.");

    /// <summary>
    /// 2 MB per file, a directory depth of 512, and a 10-minute between-files deadline -- see
    /// <see cref="Timeout"/> for exactly what that last one does and does not bound.
    /// </summary>
    public static ExtractionLimits Default { get; } = new(2 * 1024 * 1024, 512, TimeSpan.FromMinutes(10));
}
