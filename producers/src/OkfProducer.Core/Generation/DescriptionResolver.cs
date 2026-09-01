// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net;
using OkfProducer.Core.CodeGraph;

namespace OkfProducer.Core.Generation;

/// <summary>
/// Decides a generated concept's <c>description</c> and the <c>description_source</c> extension key
/// that goes with it, applying §4.2's field-preservation rule before <paramref name="chain"/> is
/// tried at all. The rule is inverted from an allow-list of what to preserve to an allow-list of
/// what to re-derive, because the two ways to get it wrong are not symmetric: re-deriving something
/// that should have been preserved destroys a human's (or a model's) work irrecoverably, while
/// preserving something that could safely have been refreshed only leaves a description stale. The
/// cheaper mistake is the default:
///
/// <list type="table">
/// <listheader><term><c>description_source</c> on the existing concept</term><description>behaviour</description></listheader>
/// <item><term><c>doc-comment</c></term><description>re-derived -- the code stays the source of truth, so an improved comment propagates</description></item>
/// <item><term><c>generated</c></term><description>re-derived -- this is the slot a later LLM enrichment step fills</description></item>
/// <item><term>absent</term><description>derive normally -- <b>and this covers two different situations, only one of which is harmless</b>: see below</description></item>
/// <item><term>anything else -- <c>manual</c>, <c>llm</c>, a typo'd or differently-cased spelling of either, or a value this producer has never written at all</term><description>preserved, never overwritten</description></item>
/// </list>
///
/// <para><b>What "absent" really means, stated because the row above used to claim the wrong one.</b>
/// The row read "absent (never written by this producer before)", which is true of a fresh bundle and
/// false of the case that costs someone their work: a concept a <i>human</i> wrote by hand, at an id
/// this producer later generates. That file has no <c>description_source</c> either, so it takes this
/// same branch and its description is re-derived -- and then the whole file is overwritten by
/// <c>BundleWriter</c>, body and all. The two situations are indistinguishable <i>here</i>, and
/// deliberately left so: a null <paramref name="existing"/> and an existing document with no such key
/// both mean "this producer has no record of writing a description at this id", and inventing a
/// <c>manual</c> claim for the second would write a label into a user's file that nobody chose.
/// <b>Where they are told apart is <c>BundleWriter</c></b>, which knows whether a file was already
/// there and whether any manifest claimed it, and raises a note naming the file it took ownership of.
/// §4.2 preservation applies to concepts this producer wrote before; a hand-written concept is kept
/// out of harm's way by giving it an id the producer does not generate.</para>
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

    /// <summary>
    /// The documented, canonical <c>description_source</c> value a producer should write for a
    /// human-written description. Not used by <see cref="Resolve"/>'s own comparison -- every value
    /// other than the two known-derived labels is preserved regardless of spelling -- this constant
    /// exists so other code that *writes* the label (a future "mark as manual" workflow, tests)
    /// has one spelling to agree on.
    /// </summary>
    public const string ManualLabel = "manual";

    /// <summary>
    /// The documented, canonical <c>description_source</c> value a producer should write for a
    /// model-written description. See <see cref="ManualLabel"/>'s remarks: not load-bearing for the
    /// preservation check itself.
    /// </summary>
    public const string LlmLabel = "llm";

    /// <summary>
    /// Resolves the description for <paramref name="fact"/>. <paramref name="existing"/> is the
    /// frontmatter of the concept this run would otherwise overwrite -- <see langword="null"/> for a
    /// concept this producer has not written before, which always derives normally.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// No source in <paramref name="chain"/> produced a result, and no description was preserved
    /// either -- the chain passed to the constructor has no terminal fallback that always produces a
    /// result (e.g. a <see cref="SignatureSource"/>).
    /// </exception>
    public (string Text, string Source) Resolve(SymbolFact fact, Frontmatter? existing)
    {
        var existingSource = existing?.Get(DescriptionSourceKey)?.AsDisplayString();

        // Trimmed and compared case-insensitively, on purpose -- a deliberate exception to this
        // codebase's Ordinal convention (Tasks 4 and 6 both moved a comparison *from*
        // OrdinalIgnoreCase *to* Ordinal after review). It is not a reversal of that judgement: this
        // value is hand-typed into frontmatter by a human, and the two failure directions are not
        // symmetric. Accepting a spelling variant ("Manual", "MANUAL", a quoted " manual" that OKF4net's
        // YAML parser does not trim) costs nothing. Rejecting one on a case or whitespace technicality
        // means silently re-deriving over it below -- destroying a human's work with no error raised
        // anywhere. The asymmetry, not the letter case, is what makes this comparison different.
        var normalized = existingSource?.Trim().ToLowerInvariant();
        var isKnownDerivedLabel = normalized is DocCommentSource.SourceLabel or SignatureSource.SourceLabel;

        if (existingSource is not null && !isKnownDerivedLabel && existing!.Description is { } preserved)
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
            "DescriptionResolver: no source in the chain produced a description, and no description was preserved to fall back on.");
    }
}
