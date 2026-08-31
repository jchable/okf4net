// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net;
using OkfProducer.Core.CodeGraph;

namespace OkfProducer.Core.Generation;

/// <summary>
/// Everything <see cref="ConceptGenerator"/> needs beyond the two things it generates *from* (a
/// <see cref="Scanning.RepositorySnapshot"/> and a <see cref="OkfProducer.Core.CodeGraph.CodeGraph"/>).
/// Declared with <c>init</c>-only properties rather than as a positional record: later tasks add
/// fields to it (§6.1's HEAD-commit stamp, §6.2's <c>--check</c>), and a positional record would make
/// every one of those additions a breaking change to every call site.
/// </summary>
public sealed record GenerateOptions
{
    /// <summary>
    /// The options a run uses when the caller supplies none: no repository URL (so no
    /// <c>resource</c> is emitted at all -- see <see cref="RepoUrl"/>), no language profiles, no
    /// existing bundle to preserve descriptions from, and no source-ownership map (so no
    /// package -> namespace containment link -- see <see cref="SourceOwnership"/>).
    /// </summary>
    public static GenerateOptions Default { get; } = new();

    /// <summary>
    /// Base URL of the repository being scanned (e.g. <c>https://github.com/o/r</c>), from the CLI's
    /// <c>--repo-url</c>. When it and <see cref="Rev"/> are both present and this is an absolute
    /// <c>http</c>/<c>https</c> URL, every code concept carries a <c>resource</c> pointing at its
    /// declaration with a line span; otherwise <b>no <c>resource</c> is emitted at all</b>.
    ///
    /// That "otherwise" is not a shortcut, it is §4.3: the validator resolves a bare relative
    /// <c>resource</c> against the <i>concept's own directory</i>, not the bundle root
    /// (<c>Bundle.TryResolveResource</c>), so a repo-relative path such as <c>src/Links.cs</c> carried
    /// by <c>code/csharp/okf4net/link-scanner/scan.md</c> would be looked for under
    /// <c>&lt;bundle&gt;/code/csharp/okf4net/link-scanner/src/Links.cs</c> -- a miss for every code
    /// concept, and one <c>FrontmatterPathMissing</c> warning apiece. Omitting the field costs exactly
    /// the same number of warnings (<c>resource</c> is a recommended field), so the two options cost
    /// the same and only one of them is honest.
    /// </summary>
    public string? RepoUrl { get; init; }

    /// <summary>
    /// The git ref to build <see cref="RepoUrl"/>-based permalinks against (the CLI's <c>--rev</c>,
    /// defaulting to the current branch name rather than a sha, since a sha would churn every code
    /// concept on every commit -- §4.3). A ref containing <c>/</c> (e.g. <c>feature/x</c>) keeps its
    /// separators: each segment is escaped individually, never the whole ref, because a forge's blob
    /// URL reads the slashes.
    /// </summary>
    public string? Rev { get; init; }

    /// <summary>
    /// The language profiles this run extracted with. Used only for
    /// <see cref="LanguageProfile.SplitContainer"/>, which decides how a symbol's container is cut
    /// into concept-id segments. A symbol whose <see cref="SymbolFact.Language"/> matches no profile
    /// here still gets a concept: the generator falls back to a container-only profile carrying just
    /// that language, which is exactly what <see cref="LanguageProfile.SplitContainer"/> keys off.
    /// </summary>
    public IReadOnlyList<LanguageProfile> Profiles { get; init; } = [];

    /// <summary>
    /// Looks up the frontmatter of the concept already in the bundle under a given id, or
    /// <see langword="null"/> when this producer has not written that concept before. This is the seam
    /// §4.2's field preservation runs through: without it <see cref="DescriptionResolver"/> would
    /// always see <see langword="null"/> and a hand-written <c>description</c> would be destroyed on
    /// the next <c>generate</c>. Left <see langword="null"/> here, every concept derives normally --
    /// correct for a fresh bundle, and the reason a caller writing into an existing one must supply it.
    /// </summary>
    public Func<ConceptId, Frontmatter?>? ExistingFrontmatter { get; init; }

    /// <summary>
    /// Which project compiles which source file, as MSBuild evaluated it -- the seam §5.1's
    /// package -> namespace containment link is attributed through. Supplied by the composition root
    /// (the CLI, which references every project) rather than read here, because the query that
    /// produces it, <c>MsBuildProjectQuery</c>, lives in <c>OkfProducer.CodeGraph.Roslyn</c>, which
    /// references this project and not the reverse.
    ///
    /// <para><see langword="null"/> -- no MSBuild available, a repository that was never restored, or
    /// simply a caller that supplied none -- means <b>no package -> namespace link is emitted at all</b>,
    /// and the run says so through <see cref="Note"/>. Deliberately not a fall back to directory
    /// containment: a missing link leaves the spine incomplete, which is visible and harmless, while a
    /// link derived from the directory tree attributes a namespace to the wrong package whenever a
    /// project adds, removes or links sources across directories -- a confident lie, which is what
    /// §5.1 rules out.</para>
    /// </summary>
    public SourceOwnershipMap? SourceOwnership { get; init; }

    /// <summary>
    /// Where the run reports what it could not do: a note is one plain sentence, with no severity
    /// prefix, so the caller decides how to render it (the CLI prefixes <c>note: </c> and writes to
    /// stderr). Left <see langword="null"/>, notes are dropped -- generation itself never depends on
    /// this sink, and never fails because of it.
    ///
    /// <para>Its one guarantee is that a run that silently produced <i>less</i> than it was asked for
    /// -- the whole package -> namespace level of the containment spine missing for want of a
    /// <see cref="SourceOwnership"/> map -- has somewhere to say so, instead of the operator having to
    /// notice the absence by reading the bundle.</para>
    /// </summary>
    public Action<string>? Note { get; init; }
}
