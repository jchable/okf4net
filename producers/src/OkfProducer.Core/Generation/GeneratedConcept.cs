// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net;

namespace OkfProducer.Core.Generation;

/// <summary>A generated concept, paired with the id it will be written under.</summary>
public sealed record GeneratedConcept(ConceptId Id, OkfDocument Document)
{
    /// <summary>
    /// Every repository-relative, <c>/</c>-separated source path that contributed a declaration to
    /// this concept -- the ownership §6.3's pruning joins on, recorded by the one stage that knows it.
    ///
    /// <para>Empty for a concept that is not derived from extracted source (<c>overview</c>,
    /// <c>packages/*</c>, <c>docs/*</c>), and empty is not a neutral value: an id whose owner is
    /// unknown is never pruned, because there is no file whose fate could settle whether the symbol
    /// was deleted or merely unread. A container concept is therefore given the union of the files of
    /// everything nested under it, not left empty -- it is synthesized rather than declared, but it
    /// can still carry a hand-written description that pruning would destroy.</para>
    ///
    /// <para>An <c>init</c> property with a default rather than a third positional parameter: every
    /// existing construction site, in this producer and in its tests, means "no source file", and
    /// making them all say so explicitly would be churn that hides the two sites where the value is
    /// real.</para>
    /// </summary>
    public IReadOnlyList<string> SourceFiles { get; init; } = [];
}
