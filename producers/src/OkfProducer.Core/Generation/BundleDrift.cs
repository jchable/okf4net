// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net;

namespace OkfProducer.Core.Generation;

/// <summary>
/// What one <see cref="BundleDrift.Check"/> found. <see cref="Differences"/> is empty when the bundle
/// on disk is exactly what regenerating it produces.
/// </summary>
/// <param name="Differences">
/// One plain sentence per file that differs, sorted <see cref="StringComparer.Ordinal"/> by the
/// bundle-relative path it names, with no severity prefix -- the caller decides how to render them.
/// One entry does not name a file: when <see cref="ConceptsRegenerated"/> is zero, the first sentence
/// says the regeneration produced nothing, so an operator reading this list is told why the check
/// failed rather than being handed an empty list beside a non-zero exit code.
/// </param>
/// <param name="FieldsExcluded">
/// Whether the outside-a-git-repository projection was applied (see
/// <see cref="BundleDrift.CheckDescription"/>). <see langword="false"/> means the comparison really
/// was byte for byte over every file; <see langword="true"/> means two fields on <c>overview</c> were
/// removed from both sides first, and the guarantee is correspondingly weaker. Reported rather than
/// inferred, so a caller can say which of the two properties it just verified.
/// </param>
/// <param name="ConceptsRegenerated">
/// How many concepts the caller's regeneration actually wrote into the copy.
///
/// <para><b>A contract check on the caller's delegate, and exactly that much.</b> The regeneration is
/// supplied by the caller, so a caller that composes its pipeline wrongly hands over a run that writes
/// nothing. The copy then equals the original, no difference is found, and <c>--check</c> prints "no
/// drift" on every bundle for ever. Zero here therefore makes <see cref="IsClean"/> false whatever the
/// byte comparison said. <c>CheckTests.A_regeneration_that_writes_nothing_is_never_reported_clean</c>
/// is what shows it fires.</para>
///
/// <para><b>What it does NOT cover, said plainly because this doc comment used to imply it did.</b>
/// It once read "or not at all, which is exactly the state the CLI is in until its code-graph stage is
/// wired" -- that stage is wired now, and <c>ConceptGenerator</c> always emits <c>overview</c>, so
/// every composition the shipped CLI can build returns at least 1 and this floor cannot fire from
/// there. In particular it does not catch <c>--check --no-code</c>, where the code stage is skipped
/// but <c>overview</c> and the package and doc families are still written: the count is positive, no
/// <c>code/</c> concept is regenerated, the copy's manifest is byte-identical because a code-less run
/// writes none, and the check reports clean over an arbitrarily stale <c>code/</c> family. A note
/// cannot fix that -- the producer's README says a note never changes the exit code -- so the CLI
/// rejects the combination outright, the way it already rejects <c>--check --reset</c>. This floor is
/// a guard on the <c>Func</c> contract, not a guard on how the caller composed its pipeline.</para>
/// </param>
public sealed record DriftReport(IReadOnlyList<string> Differences, bool FieldsExcluded, int ConceptsRegenerated)
{
    /// <summary>
    /// The bundle-relative, <c>/</c>-separated path of every symbolic link and junction the check
    /// declined to walk into -- neither copied nor compared, on either side. Complete: a link whose own
    /// path also turned up in <see cref="Differences"/> is still listed here, and listed again by
    /// <see cref="LinksReportedAsDrift"/>.
    ///
    /// <para><b>Being skipped is not by itself a difference.</b> Both sides are listed by the same
    /// walk, and that walk stops at a reparse point, so the link's own path is absent from the bundle
    /// side's file set; counting every link as drift on that ground alone would fail a check over a
    /// bundle nobody had touched. The list is reported for the same reason <see cref="FieldsExcluded"/>
    /// is -- it says which property the run just verified. A clean report with a non-empty list means
    /// "everything I compared matches", over less than the whole directory.</para>
    ///
    /// <para><b>Two successive versions of this paragraph drew a false conclusion from that, and both
    /// were universals.</b> The first said "a link in a bundle is not drift: it is there on both
    /// sides". The second said the link's own path "can never be reported as a file one side has and
    /// the other lacks". Both are false, by the same mechanism: the two file sets are not built at the
    /// same moment. <c>CopyDirectory</c> reproduces directories and files and deliberately not the
    /// link; the regeneration then writes into the copy; and only then is the copy walked. So where the
    /// regeneration writes a <b>file</b> at exactly the link's path, the copy holds that file and the
    /// bundle side holds nothing, and the path IS reported.</para>
    ///
    /// <para><b>It is reported, and it should be</b> -- the bundle carries a link where this producer
    /// puts a concept, and that is drift. What <c>Compare</c> gives it is a sentence saying so,
    /// rather than the "produced by regenerating, but missing from the bundle" sentence an ordinary
    /// absence gets; and <see cref="LinksReportedAsDrift"/> tells a caller not to also print its
    /// skipped-link note, so the operator gets one line about the path instead of two that read as
    /// contradicting each other.</para>
    ///
    /// <para><b>What <c>generate</c> does about the same link, since this paragraph used to say
    /// "<c>generate</c> refuses to write through the link" and that is a universal the code does not
    /// support.</b> <c>BundleWriter.CommitStaging</c> refuses a destination only when
    /// <c>BundlePaths.ResolveInsideRoot</c> says it leaves the bundle root; a link that resolves back
    /// INSIDE the root passes that gate. Measured on the host above, with the junction at
    /// <c>code/csharp/n/scanner.md</c> and <c>--update</c>: pointing at a directory outside the bundle,
    /// the run refused it before attempting the move and recorded the write failure "the path leaves
    /// the bundle root through a symbolic link or junction" -- 14 concepts written, nothing at the far
    /// end; pointing at <c>&lt;bundle&gt;/docs</c>, nothing refused it -- the gate asks whether the
    /// destination escapes, and it does not -- and the move itself failed.</para>
    ///
    /// <para>Both now end as a write failure naming that one concept, with the run carrying on. The
    /// second used to throw <see cref="UnauthorizedAccessException"/> out of the whole run; that was
    /// fixed alongside this (<c>CommitStaging</c> catches the move and records a reason), and this
    /// paragraph is the second thing that had to be corrected when it was, having been written while
    /// the crash was still the behaviour. What survives unchanged is the narrow statement: neither
    /// shape puts the concept in the bundle, and the gate is not what stops the second one. Which is
    /// why the drift sentence above says what the BUNDLE holds and claims nothing about what
    /// <c>generate</c> would do next.</para>
    ///
    /// <para><b>Measured, on one host, on the two shapes that host can make.</b> Windows 11 build
    /// 26200 on .NET 10.0.8, against the committed golden bundle. A junction at
    /// <c>code/csharp/n/scanner</c> -- a path the generator writes a DIRECTORY of concepts under -- is
    /// listed here, its children are reported as differences, and its own path is not; that shape is
    /// unchanged and deliberately so, since suppressing differences under a skipped link would let a
    /// link mask real drift. A junction named <c>code/csharp/n/scanner.md</c> -- a path the generator
    /// writes a FILE at -- is listed here AND named by one difference. The two runs are
    /// <c>CheckTests.A_link_where_the_generator_writes_is_reported_as_drift_rather_than_passed_over</c>
    /// and
    /// <c>CheckTests.A_link_standing_where_a_concept_file_belongs_is_named_as_the_link_it_is</c>.
    /// A third, <c>Check_neither_copies_nor_compares_what_a_link_in_the_bundle_points_at</c>, reaches a
    /// clean result only because it puts the link at <c>code/x</c>, a path no id this producer emits
    /// maps to.</para>
    ///
    /// <para><b>The shape this repository cannot run, labelled as reasoning rather than as a
    /// measurement.</b> The natural form of the file case is a FILE symbolic link at
    /// <c>code/csharp/n/scanner.md</c> -- no privilege needed on Linux or macOS, and "a clone brings
    /// the link" is the documented threat. <c>File.CreateSymbolicLink</c> fails on this host without
    /// SeCreateSymbolicLinkPrivilege (checked: it raises "this operation requires an administrator
    /// privilege"), so no test here creates one, and the junction above stands in. The argument that
    /// they are the same case is read off one branch and not run: <c>Descend</c> tests
    /// <c>BundlePaths.IsReparsePoint</c> BEFORE it asks <see cref="Directory.Exists(string)"/>, so
    /// nothing downstream of that branch is told which kind of reparse point it was.</para>
    ///
    /// <para>Empty for every bundle that holds no link, which is every bundle this producer writes.</para>
    /// </summary>
    public IReadOnlyList<string> LinksSkipped { get; init; } = [];

    /// <summary>
    /// The subset of <see cref="LinksSkipped"/> whose own path is also named by a sentence in
    /// <see cref="Differences"/>, because the regeneration wrote a file at exactly that path.
    ///
    /// <para><b>Why a caller needs it.</b> A caller that prints one note per skipped link would print,
    /// for these paths, a note saying the path "was neither copied nor compared" beside a difference
    /// naming the same path -- two lines about one path that read as contradicting each other. The
    /// difference carries the whole story for these, so the note is redundant; <c>OkfgenCli</c> notes
    /// <c>LinksSkipped</c> minus this set.</para>
    ///
    /// <para><b>A link at a path the generator writes a DIRECTORY under is not in here</b>, and that is
    /// the case this distinction exists to leave alone: the differences then name the link's
    /// <i>children</i> and the note names the link, which are different paths saying different things,
    /// both true.</para>
    /// </summary>
    public IReadOnlyList<string> LinksReportedAsDrift { get; init; } = [];

    /// <summary>
    /// Whether the bundle matches what regenerating it produces -- <b>and</b> whether regenerating
    /// produced anything at all. Both, deliberately: see <see cref="ConceptsRegenerated"/>.
    ///
    /// <para><see cref="LinksSkipped"/> is <b>not</b> consulted: see why on that member.</para>
    /// </summary>
    public bool IsClean => ConceptsRegenerated > 0 && Differences.Count == 0;

    /// <summary>The process exit code this report warrants: <c>0</c> when clean, <c>1</c> on drift.</summary>
    public int ExitCode => IsClean ? 0 : 1;
}

/// <summary>
/// §6.2's <c>--check</c>: regenerates a bundle over a copy of itself and reports every byte that
/// differs, so a stale bundle cannot ship silently.
///
/// <para><b>Why a copy, and not an empty temporary directory.</b> The obvious implementation --
/// generate into an empty directory, diff the bytes -- contradicts §4.2 and can never pass. A
/// description whose <c>description_source</c> is <c>manual</c> or <c>llm</c> exists only in the
/// bundle on disk; a generation starting from nothing has nothing to preserve, so it re-derives, and
/// every concept anyone has ever hand-edited reads as drift for ever -- precisely the concepts people
/// care most about. Copying the bundle first and running the full <c>--update</c> path over the copy
/// compares the output of a <i>real</i> generation, and turns field preservation into part of what is
/// verified rather than the thing that defeats it.</para>
///
/// <para><b>The excluded fields are a closed list, and it is stated in
/// <see cref="CheckDescription"/>, not left to this implementation.</b> Inside a git repository
/// nothing is excluded at all. Outside one, <c>generated.at</c> and <c>revision</c> on
/// <c>overview</c> alone are removed from both sides, because the stamp falls back to the wall clock
/// there (see <see cref="GitRevision"/>) and a wall-clock value cannot be reproduced by regenerating.
/// Outside git the property is therefore not "byte for byte" but "byte for byte over a projection
/// with two fields removed", which is weaker, and the help text says so.</para>
///
/// <para><b>The one thing "in both directions" does not reach: a concept added to the bundle by
/// hand.</b> The regeneration runs over a <i>copy</i> that already holds it, and pruning only ever
/// considers ids the previous manifest claims (§6.3 rule 2), so a <c>.md</c> someone wrote at an id
/// this producer never generated survives untouched on both sides and is invisible here. That is a
/// consequence of the copy-based definition, not a gap in the comparison: the alternative -- treating
/// every unclaimed file as drift -- would report the hand-written concepts §6.3 exists to protect.
/// <c>BundleWriter</c> is where such a file is surfaced, as a "no manifest claims it" note.</para>
/// </summary>
public static class BundleDrift
{
    /// <summary>The one file the outside-git projection may ever be applied to.</summary>
    private const string OverviewFile = "overview.md";

    /// <summary>The concept-level key removed from <see cref="OverviewFile"/> outside a git repository.</summary>
    private const string RevisionKey = "revision";

    /// <summary>The <c>generated</c> block, and the key inside it, removed from <see cref="OverviewFile"/> outside a git repository.</summary>
    private const string GeneratedKey = "generated";
    private const string GeneratedAtKey = "at";

    /// <summary>
    /// The <c>--check</c> flag's help text (Task 13 wires the flag; this is the sentence it must
    /// carry). It states the exclusion list in full because §6.2 requires that list to be closed and
    /// visible to the operator, not a property of whatever this file happens to do -- and it names the
    /// two flag combinations the CLI refuses, because <c>--help</c> is where an operator meets the
    /// flag and a refusal discovered by being refused is a refusal discovered too late.
    /// </summary>
    public const string CheckDescription =
        "Regenerate the bundle over a copy of itself and exit non-zero if anything differs. "
        + "Inside a git repository the comparison is strictly byte for byte and nothing is excluded. "
        + "Outside one, exactly two fields are excluded, both on `overview` alone: `generated.at` and "
        + "`revision`. There is no HEAD commit to stamp outside a repository, so both fall back to the "
        + "wall clock and neither can be reproduced by regenerating; the comparison is then byte for "
        + "byte over a projection with those two fields removed -- a weaker property than the one "
        + "above. No other FIELD is ever excluded, in either case. One thing is out of reach rather "
        + "than excluded: a concept you added to the bundle by hand, at an id this producer never "
        + "generates, is copied forward untouched and so cannot differ -- `generate` reports it as "
        + "unowned instead. Cannot be combined with --reset (which would destroy the bundle it is "
        + "supposed to be comparing) or with --no-code (a regeneration that skips the code stage "
        + "produces no `code` concept at all, so every `code/` concept is copied forward untouched, "
        + "cannot differ, and the check would report no drift however stale they are). Both "
        + "combinations are rejected with an error rather than run. One more thing is out of reach "
        + "rather than excluded: a symbolic link or junction inside the bundle is neither copied nor "
        + "compared, on either side, because what hangs off the far end was never the bundle's. That "
        + "does not make a link invisible. Where regenerating writes a concept at the link's own path, "
        + "that path is reported as drift, in a sentence saying a link is what the bundle holds there; "
        + "where it writes concepts UNDER a linked directory, each of them is reported. Every skipped "
        + "link the differences do not already name is reported as a note.";

    /// <summary>
    /// Copies the bundle at <paramref name="bundlePath"/> into a temporary directory, hands that copy
    /// to <paramref name="regenerateInto"/> -- which must run the full <c>--update</c> generation over
    /// it, exactly as an ordinary run would -- and compares the result with the original, file by file
    /// and byte by byte.
    ///
    /// <para>The temporary copy is deleted before this method returns, whether it succeeded or threw.
    /// <paramref name="bundlePath"/> itself is never written to: <c>--check</c> reports, it does not
    /// repair.</para>
    /// </summary>
    /// <param name="bundlePath">The bundle to check. Read only.</param>
    /// <param name="repoPath">
    /// The repository the bundle was generated from. Used for exactly one decision -- whether it is
    /// inside a git repository, which is what settles whether the two unreproducible fields are
    /// excluded. It is deliberately the <i>repository</i> that decides and not the bundle: the stamp
    /// under test is read from the repository's HEAD (§6.1), so the repository is where the question
    /// "is there a HEAD to stamp?" has an answer.
    /// </param>
    /// <param name="regenerateInto">
    /// Runs the generation into the directory it is given, and <b>returns how many concepts it
    /// wrote</b>. The caller supplies the run because composing the pipeline (scan, extract, resolve,
    /// generate, write) needs the language extractor and resolvers, which live in projects this one
    /// does not, and must not, reference.
    ///
    /// <para>The return value is not bookkeeping: it is the only thing standing between this method
    /// and a caller whose regeneration silently does nothing, which would make every bundle look
    /// clean for ever. See <see cref="DriftReport.ConceptsRegenerated"/>.</para>
    /// </param>
    /// <exception cref="ArgumentException"><paramref name="bundlePath"/> or <paramref name="repoPath"/> is null or empty.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="regenerateInto"/> is null.</exception>
    /// <exception cref="InvalidOperationException"><paramref name="bundlePath"/> is not an existing directory.</exception>
    public static DriftReport Check(string bundlePath, string repoPath, Func<string, int> regenerateInto)
    {
        ArgumentException.ThrowIfNullOrEmpty(bundlePath);
        ArgumentException.ThrowIfNullOrEmpty(repoPath);
        ArgumentNullException.ThrowIfNull(regenerateInto);

        if (!Directory.Exists(bundlePath))
        {
            throw new InvalidOperationException(
                $"Cannot check '{bundlePath}': it is not an existing bundle directory. --check compares an existing bundle against a regeneration of it; there is nothing to compare here.");
        }

        // Read once, before the copy: the fields excluded below depend on whether the REPOSITORY has a
        // HEAD to stamp from, and a run of `git` per compared file would be both slow and, if the
        // working tree moved underneath, inconsistent between files.
        var insideGitRepository = GitRevision.HeadSha(repoPath) is not null;

        var workingRoot = Path.Combine(Path.GetTempPath(), "okfgen-check-" + Guid.NewGuid().ToString("N"));

        // Same leaf name as the original, so nothing that might read the bundle's own directory name
        // can see a difference between the two sides. Nothing does today; the copy costs nothing.
        var leaf = Path.GetFileName(Path.GetFullPath(bundlePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var copyPath = Path.Combine(workingRoot, leaf.Length > 0 ? leaf : "bundle");

        try
        {
            var linksSkipped = CopyDirectory(bundlePath, copyPath);
            var regenerated = regenerateInto(copyPath);

            var differences = new List<string>();
            if (regenerated <= 0)
            {
                // Said first, and said as a sentence rather than left implicit in an exit code: a
                // caller staring at "1 difference" wants to know that the comparison itself was
                // meaningless, not to go hunting for a file that changed.
                differences.Add(
                    "the regeneration produced no concept at all, so this comparison proves nothing:"
                    + " a run that writes nothing leaves the copy identical to the bundle and every bundle looks clean.");
            }

            // The link set handed to Compare is the one CopyDirectory just built from the BUNDLE side,
            // rather than a second walk's: it is the set the caller will render notes from, so the
            // two decisions -- what to note and what to word as a link -- cannot disagree.
            var comparison = Compare(bundlePath, copyPath, insideGitRepository, linksSkipped);
            differences.AddRange(comparison.Differences);

            return new DriftReport(differences, !insideGitRepository, regenerated)
            {
                LinksSkipped = linksSkipped,
                LinksReportedAsDrift = comparison.LinksNamed,
            };
        }
        finally
        {
            TryDeleteDirectory(workingRoot);
        }
    }

    /// <summary>What one <see cref="Compare"/> produced.</summary>
    /// <param name="Differences">One sentence per differing path, ordered <see cref="StringComparer.Ordinal"/>.</param>
    /// <param name="LinksNamed">
    /// The skipped links whose own path one of those sentences names, for
    /// <see cref="DriftReport.LinksReportedAsDrift"/>.
    /// </param>
    private sealed record Comparison(IReadOnlyList<string> Differences, IReadOnlyList<string> LinksNamed);

    /// <summary>
    /// Every difference between the two directories, in both directions: a file only the bundle has,
    /// a file only the regeneration produced, and a file both have whose bytes differ. Ordered
    /// <see cref="StringComparer.Ordinal"/> by relative path, so two runs over the same drift report
    /// it in the same order.
    /// </summary>
    /// <param name="linksSkipped">
    /// The bundle side's skipped links, from the same <see cref="Walk"/> that built the copy. Used for
    /// one thing only: telling the two reasons a path can be absent from the bundle's file set apart
    /// (see below). It never adds or removes a difference.
    /// </param>
    private static Comparison Compare(string bundlePath, string copyPath, bool insideGitRepository, IReadOnlyList<string> linksSkipped)
    {
        var original = RelativeFiles(bundlePath);
        var regenerated = RelativeFiles(copyPath);
        var skipped = new HashSet<string>(linksSkipped, StringComparer.Ordinal);

        var differences = new List<string>();
        var linksNamed = new List<string>();

        foreach (var relative in original.Union(regenerated, StringComparer.Ordinal).OrderBy(p => p, StringComparer.Ordinal))
        {
            var inOriginal = original.Contains(relative);
            var inRegenerated = regenerated.Contains(relative);

            if (!inRegenerated)
            {
                differences.Add($"{relative}: in the bundle, but regenerating does not produce it.");
                continue;
            }

            if (!inOriginal)
            {
                // "Absent from the bundle's file set" has two causes and they need different
                // sentences. Ordinarily the bundle simply has no such file. But Walk records a
                // reparse point as a link instead of a file, so a link's own path is absent too --
                // and there the bundle DOES hold something at the path, which is the one fact an
                // operator needs and which "missing from the bundle" denies. Still a difference
                // either way: the concept this producer writes there is not in the bundle.
                if (skipped.Contains(relative))
                {
                    linksNamed.Add(relative);
                    differences.Add($"{relative}: the bundle holds a symbolic link or junction here, not the concept regenerating writes at this path.");
                }
                else
                {
                    differences.Add($"{relative}: produced by regenerating, but missing from the bundle.");
                }

                continue;
            }

            var left = File.ReadAllBytes(Path.Combine(bundlePath, ToNativePath(relative)));
            var right = File.ReadAllBytes(Path.Combine(copyPath, ToNativePath(relative)));

            if (left.AsSpan().SequenceEqual(right))
            {
                continue;
            }

            // The projection is consulted ONLY once the raw bytes have already been found to differ,
            // and only for the one file and the one context §6.2 admits. It can therefore forgive a
            // difference, which is its whole job, but it can never manufacture one, and it can never
            // touch any other file.
            if (!insideGitRepository
                && string.Equals(relative, OverviewFile, StringComparison.Ordinal)
                && ProjectOverview(left) is { } projectedLeft
                && ProjectOverview(right) is { } projectedRight
                && string.Equals(projectedLeft, projectedRight, StringComparison.Ordinal))
            {
                continue;
            }

            differences.Add($"{relative}: content differs from what regenerating produces.");
        }

        return new Comparison(differences, linksNamed);
    }

    /// <summary>
    /// <paramref name="content"/> with <c>revision</c> and <c>generated.at</c> removed, re-serialized
    /// through OKF4net's own writer so both sides of a comparison are normalized identically, or
    /// <see langword="null"/> when it does not parse as an OKF document at all.
    ///
    /// <para>A <see langword="null"/> is not "equal": the caller treats it as a difference, which is
    /// the right direction -- a bundle file that stopped being a parseable concept is drift worth
    /// reporting, not drift worth forgiving.</para>
    /// </summary>
    private static string? ProjectOverview(byte[] content)
    {
        try
        {
            if (!OkfDocument.TryParse(System.Text.Encoding.UTF8.GetString(content), out var document, out _))
            {
                return null;
            }

            var mapping = document.Frontmatter.AsMapping();
            mapping.Remove(RevisionKey);
            mapping.Get(GeneratedKey)?.AsMapping()?.Remove(GeneratedAtKey);

            return document.Serialize();
        }
        catch (OkfException)
        {
            return null;
        }
    }

    /// <summary>
    /// What <see cref="Walk"/> found under a directory: the ordinary files and directories below it,
    /// and the reparse points it declined to walk into.
    /// </summary>
    /// <param name="Directories">Relative native paths of every ordinary directory, parents before children.</param>
    /// <param name="Files">Relative native paths of every ordinary file.</param>
    /// <param name="Links">Relative <c>/</c>-separated paths of every symbolic link and junction, sorted <see cref="StringComparer.Ordinal"/>.</param>
    private sealed record Tree(IReadOnlyList<string> Directories, IReadOnlyList<string> Files, IReadOnlyList<string> Links);

    /// <summary>
    /// Everything under <paramref name="root"/> that is really <i>in</i> it: an ordinary recursive
    /// walk that stops at each symbolic link and junction and records it instead of descending.
    ///
    /// <para><b>Why this is not <c>Directory.EnumerateFiles(root, "*", AllDirectories)</c>, which is
    /// what it used to be.</b> That call descends a reparse point -- .NET's enumeration has no filter
    /// for one -- so both halves of <c>--check</c> reached through a junction: the copy MATERIALIZED
    /// the far side as real directories and real files, and the comparison listed paths at the far
    /// end as if they were bundle files. The copy is the damaging half. The regeneration then ran
    /// against a directory in which somebody else's file genuinely <i>was</i> inside the root, so
    /// every containment gate in <c>BundleWriter</c> passed and the run said the things those gates
    /// exist to stop it saying -- that it had "taken ownership of that id and overwritten the file",
    /// and that the file "sits under the owned prefix but no manifest claims it" -- about a file
    /// outside the bundle, in the one mode that writes nothing to the bundle at all.</para>
    ///
    /// <para><b>Both sides skip, and that symmetry is the point.</b> Skipping in the copy alone would
    /// invent drift: the original still listed the far-side paths, the copy no longer had them, and
    /// every one would read as "in the bundle, but regenerating does not produce it".</para>
    ///
    /// <para><b>What is given up, and why it was never held.</b> A bundle whose owned subtree is a
    /// link is no longer compared through it. It was not checkable in any useful sense before:
    /// <c>generate</c> refuses every destination that resolves outside the root, so this producer
    /// cannot write there -- it records write failures instead -- and a <c>--check</c> that compared
    /// a subtree no run can regenerate was reporting on content the producer had already declined to
    /// own. The links are reported (<see cref="DriftReport.LinksSkipped"/>) rather than passed over in
    /// silence, since a check that quietly stops looking at part of a bundle is the failure mode this
    /// whole file is built against.</para>
    ///
    /// <para>Dotted names are kept: <see cref="GenerationManifest.FileName"/> is written output like
    /// any other, and a manifest that no longer describes the bundle is exactly the staleness this
    /// check exists to catch. So are hidden and system files -- the enumeration this replaced skipped
    /// neither (measured), and this one skips neither.</para>
    ///
    /// <para><b>One structural limit, recorded and deliberately not acted on.</b>
    /// <see cref="Descend"/> recurses, so directory depth sits on the call stack rather than in a
    /// queue, and a tree deep enough would end the process with a <see cref="StackOverflowException"/>
    /// where the BCL enumerator this replaced would have raised a catchable
    /// <see cref="IOException"/> at the platform's path-length limit. The depth needed is thousands of
    /// levels; nothing this producer writes is more than a handful, and the input is a bundle the
    /// operator points at rather than anything derived from a repository. It is recorded because it is
    /// a genuine difference from what was here before, and left alone because the exact depth at which
    /// it bites depends on the platform's path limit and, on Windows, on whether long paths are in
    /// play -- <b>and that was not measured.</b> <c>BundleWriter.FirstLinkUnder</c>, written later for
    /// a related walk, uses an explicit stack instead; this one has three lists to fill and is not
    /// worth converting on an unmeasured concern.</para>
    /// </summary>
    private static Tree Walk(string root)
    {
        var directories = new List<string>();
        var files = new List<string>();
        var links = new List<string>();

        Descend(root, root, directories, files, links);

        links.Sort(StringComparer.Ordinal);
        return new Tree(directories, files, links);
    }

    private static void Descend(string root, string current, List<string> directories, List<string> files, List<string> links)
    {
        foreach (var entry in Directory.GetFileSystemEntries(current))
        {
            var relative = Path.GetRelativePath(root, entry);

            // Tested BEFORE Directory.Exists, which answers true for a junction and would send the
            // walk through it.
            if (BundlePaths.IsReparsePoint(entry))
            {
                links.Add(relative.Replace(Path.DirectorySeparatorChar, '/'));
                continue;
            }

            if (Directory.Exists(entry))
            {
                directories.Add(relative);
                Descend(root, entry, directories, files, links);
            }
            else
            {
                files.Add(relative);
            }
        }
    }

    /// <summary>
    /// The <c>/</c>-separated relative paths of every file really inside <paramref name="root"/>.
    /// See <see cref="Walk"/> for what "really inside" excludes.
    /// </summary>
    private static HashSet<string> RelativeFiles(string root) =>
        new(
            Walk(root).Files.Select(path => path.Replace(Path.DirectorySeparatorChar, '/')),
            StringComparer.Ordinal);

    private static string ToNativePath(string relative) => relative.Replace('/', Path.DirectorySeparatorChar);

    /// <summary>
    /// Copies everything really inside <paramref name="source"/> into <paramref name="destination"/>,
    /// and returns the reparse points it declined to follow. See <see cref="Walk"/>.
    /// </summary>
    private static IReadOnlyList<string> CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);

        var tree = Walk(source);

        foreach (var directory in tree.Directories)
        {
            Directory.CreateDirectory(Path.Combine(destination, directory));
        }

        foreach (var file in tree.Files)
        {
            File.Copy(Path.Combine(source, file), Path.Combine(destination, file), overwrite: true);
        }

        return tree.Links;
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover temporary copy is untidy, never wrong: it is outside the bundle and outside
            // the repository, and failing a clean check over it would report drift that does not exist.
        }
    }
}
