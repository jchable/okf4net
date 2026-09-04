// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OkfProducer.Core.CodeGraph;

/// <summary>Extracts declarations and call sites from a single source file.</summary>
public interface ILanguageExtractor
{
    /// <summary>
    /// Extracts every declaration and call site in the file at <paramref name="absolutePath"/>,
    /// enforcing <paramref name="limits"/>'s hostile-input guards (§2.3) itself: a file over
    /// <see cref="ExtractionLimits.MaxFileBytes"/> must never be loaded into memory, and its bytes
    /// must be decoded strictly as UTF-8 or UTF-16-with-BOM (an invalid byte sequence in the selected
    /// encoding reported, never silently replaced). Any guard this call cannot satisfy is reported
    /// through <see cref="ExtractionResult.Status"/>, never thrown -- hostile source is an expected
    /// input, not an exceptional one.
    ///
    /// <para>
    /// <see cref="ExtractionLimits.Timeout"/> is deliberately NOT among the guards an implementation
    /// is asked to enforce, and this call takes no <see cref="System.Threading.CancellationToken"/>
    /// for the same reason: <see cref="CodeGraphBuilder.Build"/> checks the deadline between files,
    /// and once it has entered this method it has no way to leave early. An implementation is
    /// therefore free to run as long as its parser takes, and a caller must not read a returned
    /// <see cref="ExtractionResult"/> as evidence that any deadline was respected. Adding a token
    /// here would only be honest once a parser underneath can act on one -- see
    /// <see cref="ExtractionLimits.Timeout"/> for the measurement behind that.
    /// </para>
    /// </summary>
    /// <param name="relativePath">The file's path relative to the repository root -- carried onto every
    /// produced <see cref="SymbolFact"/> and <see cref="CallSite"/>.</param>
    /// <param name="absolutePath">The file's path on disk, to read its contents from.</param>
    /// <param name="profile">The language profile (queries, grammar, doc-comment prefix) to extract with.</param>
    /// <param name="limits">The hostile-input guards (§2.3) this call must enforce.</param>
    ExtractionResult Extract(string relativePath, string absolutePath, LanguageProfile profile, ExtractionLimits limits);
}
