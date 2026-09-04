// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OkfProducer.Core.CodeGraph;

/// <summary>
/// One call expression found in source, before resolution. <see cref="Offset"/> is a UTF-8 byte
/// offset into <see cref="RelativePath"/>'s contents (see <see cref="SymbolFact"/> for why).
/// </summary>
public sealed record CallSite(
    string CallerContainer,
    string CallerName,
    string CalledName,
    string RelativePath,
    int Offset);
