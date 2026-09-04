// SPDX-License-Identifier: LGPL-3.0-or-later
using OkfProducer.Core.CodeGraph;

namespace OkfProducer.Core.Generation;

/// <summary>
/// One candidate source of a generated concept's <c>description</c> field. <see cref="DescriptionResolver"/>
/// tries a fixed, ordered chain of these and takes the first non-null result -- see that type's
/// summary for the ordering and the field-preservation rule wrapped around it. A source is stateless
/// and looks only at <paramref name="fact"/>'s own fields; it never needs wider context (e.g. sibling
/// symbols in the same file). Adding a new source to the chain (e.g. a future LLM enrichment step)
/// requires no change to this interface or to <see cref="DescriptionResolver"/> itself.
/// </summary>
public interface IDescriptionSource
{
    /// <summary>
    /// Attempts to derive a description for <paramref name="fact"/>. Returns <see langword="null"/>
    /// when this source has nothing to offer for this particular fact, so
    /// <see cref="DescriptionResolver"/> falls through to the next source in its chain. A non-null
    /// <c>Text</c> is a complete sentence, not a fragment; <c>Source</c> is the literal value this
    /// producer writes to the concept's <c>description_source</c> extension key, so that a later
    /// <c>generate</c> run knows whether it may re-derive the description or must preserve it.
    /// </summary>
    (string Text, string Source)? Describe(SymbolFact fact);
}
