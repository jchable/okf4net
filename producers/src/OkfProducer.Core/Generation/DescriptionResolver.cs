// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net;
using OkfProducer.Core.CodeGraph;

namespace OkfProducer.Core.Generation;

/// <summary>
/// Decides a generated concept's <c>description</c> and the <c>description_source</c> extension key
/// that goes with it, applying §4.2's field-preservation rule before <paramref name="chain"/> is
/// tried at all:
///
/// <list type="table">
/// <listheader><term><c>description_source</c> on the existing concept</term><description>behaviour</description></listheader>
/// <item><term><c>doc-comment</c></term><description>re-derived -- the code stays the source of truth, so an improved comment propagates</description></item>
/// <item><term><c>generated</c></term><description>re-derived -- this is the slot a later LLM enrichment step fills</description></item>
/// <item><term><c>manual</c></term><description>never overwritten</description></item>
/// <item><term><c>llm</c></term><description>never overwritten</description></item>
/// <item><term>absent (never written by this producer before)</term><description>derive normally</description></item>
/// </list>
///
/// Without this rule, a human-written description would disappear at the very next
/// <c>generate</c> and the bundle would be a throwaway artefact rather than an editable knowledge
/// base -- it is also the prerequisite for a later LLM enrichment step that fills the <c>generated</c>
/// slot without a field-level rule being immediately overwritten on the run right after.
///
/// When preservation does not apply, <paramref name="chain"/> is walked in order and the first
/// non-null <see cref="IDescriptionSource.Describe"/> result wins. Adding a source (e.g. that future
/// LLM step) means adding one more <see cref="IDescriptionSource"/> to the chain passed to the
/// constructor; this type needs no change.
/// </summary>
public sealed class DescriptionResolver(IReadOnlyList<IDescriptionSource> chain)
{
    /// <summary>The frontmatter extension key this resolver reads and writes.</summary>
    public const string DescriptionSourceKey = "description_source";

    /// <summary><c>description_source</c> value for a human-written description. Never re-derived.</summary>
    public const string ManualLabel = "manual";

    /// <summary><c>description_source</c> value for a model-written description. Never re-derived.</summary>
    public const string LlmLabel = "llm";

    /// <summary>
    /// Resolves the description for <paramref name="fact"/>. <paramref name="existing"/> is the
    /// frontmatter of the concept this run would otherwise overwrite -- <see langword="null"/> for a
    /// concept this producer has not written before, which always derives normally.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// No source in <paramref name="chain"/> produced a result, and no <c>manual</c>/<c>llm</c>
    /// description was preserved either -- the chain passed to the constructor has no terminal
    /// fallback that always produces a result (e.g. a <see cref="SignatureSource"/>).
    /// </exception>
    public (string Text, string Source) Resolve(SymbolFact fact, Frontmatter? existing)
    {
        var existingSource = existing?.Get(DescriptionSourceKey)?.AsDisplayString();
        if (existingSource is ManualLabel or LlmLabel && existing!.Description is { } preserved)
        {
            return (preserved, existingSource);
        }

        foreach (var source in chain)
        {
            if (source.Describe(fact) is { } result)
            {
                return result;
            }
        }

        throw new InvalidOperationException(
            "DescriptionResolver: no source in the chain produced a description, and no manual/llm description was preserved to fall back on.");
    }
}
