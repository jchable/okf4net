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
/// </param>
public sealed record ExtractionLimits(long MaxFileBytes, int MaxDepth, TimeSpan Timeout)
{
    /// <summary>
    /// 2 MB per file, a directory depth of 512, and a 10-minute between-files deadline -- see
    /// <see cref="Timeout"/> for exactly what that last one does and does not bound.
    /// </summary>
    public static ExtractionLimits Default { get; } = new(2 * 1024 * 1024, 512, TimeSpan.FromMinutes(10));
}
