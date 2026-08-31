// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OkfProducer.Core.CodeGraph;

/// <summary>Extracts declarations and call sites from a single source file.</summary>
public interface ILanguageExtractor
{
    /// <summary>
    /// Extracts every declaration and call site in the file at <paramref name="absolutePath"/>,
    /// enforcing <paramref name="limits"/>'s hostile-input guards (§2.3) itself: a file over
    /// <see cref="ExtractionLimits.MaxFileBytes"/> must never be loaded into memory, and its bytes
    /// must be decoded strictly (invalid UTF-8 reported, not silently replaced). Any guard this call
    /// cannot satisfy is reported through <see cref="ExtractionResult.Status"/>, never thrown --
    /// hostile source is an expected input, not an exceptional one.
    /// </summary>
    /// <param name="relativePath">The file's path relative to the repository root -- carried onto every
    /// produced <see cref="SymbolFact"/> and <see cref="CallSite"/>.</param>
    /// <param name="absolutePath">The file's path on disk, to read its contents from.</param>
    /// <param name="profile">The language profile (queries, grammar, doc-comment prefix) to extract with.</param>
    /// <param name="limits">The hostile-input guards (§2.3) this call must enforce.</param>
    ExtractionResult Extract(string relativePath, string absolutePath, LanguageProfile profile, ExtractionLimits limits);
}
