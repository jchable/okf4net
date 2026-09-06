// SPDX-License-Identifier: LGPL-3.0-or-later
using OkfProducer.Core.Scanning;

// Same alias, and for the same reason, as ConceptGenerator's own: in a namespace with a sibling
// `CodeGraph` NAMESPACE in scope, the bare name binds to that namespace before it can bind to the
// type of the same name (CS0118).
using CodeGraphModel = OkfProducer.Core.CodeGraph.CodeGraph;

namespace OkfProducer.Core.Generation;

/// <summary>Turns a <see cref="RepositorySnapshot"/> into the OKF concepts describing it.</summary>
public interface IConceptGenerator
{
    /// <summary>
    /// Generates every concept for <paramref name="snapshot"/>, each paired with its concept id.
    /// <paramref name="codeGraph"/> non-null adds the <c>code/</c> family; <see langword="null"/> is
    /// the <c>--no-code</c> path. <paramref name="options"/> carries everything the run needs beyond
    /// those two -- permalink base, language profiles, the existing-frontmatter reader §4.2's field
    /// preservation runs through, the source-ownership map §5.1's package link is attributed from, and
    /// the sink a degraded run reports through.
    ///
    /// <para><b>Why the interface carries this overload and not only the one below.</b> This is the
    /// method the shipped composition calls. Left off, the only seam the CLI could resolve would be
    /// the four-argument form nothing in production uses, so the CLI would have to reach past the
    /// interface to the concrete class -- and an abstraction every caller bypasses documents a
    /// contract that is not the one being honoured. <see cref="IBundleWriter.Write"/> already carries
    /// its full signature, optional pruning arguments included, for the same reason.</para>
    /// </summary>
    IReadOnlyList<GeneratedConcept> Generate(RepositorySnapshot snapshot, CodeGraphModel? codeGraph, GenerateOptions options);

    /// <summary>
    /// The no-code, no-options generation: exactly what this producer emitted before the code-graph
    /// stage existed. A default implementation rather than a second obligation on every implementer,
    /// because it is a convenience spelling of the overload above and nothing more -- and because
    /// making it abstract would let an implementation give the two forms different meanings.
    /// </summary>
    IReadOnlyList<GeneratedConcept> Generate(RepositorySnapshot snapshot) =>
        Generate(snapshot, codeGraph: null, GenerateOptions.Default);
}
