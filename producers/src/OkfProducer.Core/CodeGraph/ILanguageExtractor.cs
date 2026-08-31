// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OkfProducer.Core.CodeGraph;

/// <summary>Extracts declarations and call sites from a single source file.</summary>
public interface ILanguageExtractor
{
    /// <summary>
    /// Extracts every declaration and call site in the file at <paramref name="absolutePath"/>.
    /// </summary>
    /// <param name="relativePath">The file's path relative to the repository root -- carried onto every
    /// produced <see cref="SymbolFact"/> and <see cref="CallSite"/>.</param>
    /// <param name="absolutePath">The file's path on disk, to read its contents from.</param>
    /// <param name="profile">The language profile (queries, grammar, doc-comment prefix) to extract with.</param>
    ExtractionResult Extract(string relativePath, string absolutePath, LanguageProfile profile);
}
