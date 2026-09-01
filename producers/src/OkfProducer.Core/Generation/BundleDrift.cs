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
    /// Whether the bundle matches what regenerating it produces -- <b>and</b> whether regenerating
    /// produced anything at all. Both, deliberately: see <see cref="ConceptsRegenerated"/>.
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
        + "combinations are rejected with an error rather than run.";

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
            CopyDirectory(bundlePath, copyPath);
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

            differences.AddRange(Compare(bundlePath, copyPath, insideGitRepository));

            return new DriftReport(differences, !insideGitRepository, regenerated);
        }
        finally
        {
            TryDeleteDirectory(workingRoot);
        }
    }

    /// <summary>
    /// Every difference between the two directories, in both directions: a file only the bundle has,
    /// a file only the regeneration produced, and a file both have whose bytes differ. Ordered
    /// <see cref="StringComparer.Ordinal"/> by relative path, so two runs over the same drift report
    /// it in the same order.
    /// </summary>
    private static IReadOnlyList<string> Compare(string bundlePath, string copyPath, bool insideGitRepository)
    {
        var original = RelativeFiles(bundlePath);
        var regenerated = RelativeFiles(copyPath);

        var differences = new List<string>();

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
                differences.Add($"{relative}: produced by regenerating, but missing from the bundle.");
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

        return differences;
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
    /// Every file under <paramref name="root"/>, as <c>/</c>-separated paths relative to it. Dotted
    /// names are included deliberately: <see cref="GenerationManifest.FileName"/> is written output
    /// like any other, and a manifest that no longer describes the bundle is exactly the kind of
    /// staleness this check exists to catch.
    /// </summary>
    private static HashSet<string> RelativeFiles(string root) =>
        new(
            Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/')),
            StringComparer.Ordinal);

    private static string ToNativePath(string relative) => relative.Replace('/', Path.DirectorySeparatorChar);

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);

        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            File.Copy(file, Path.Combine(destination, Path.GetRelativePath(source, file)), overwrite: true);
        }
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
