// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net;

namespace OkfProducer.Core.Generation;

/// <summary>
/// Reads the frontmatter of the concept already sitting in a bundle under a given id -- the reader
/// side of <see cref="GenerateOptions.ExistingFrontmatter"/>, and therefore the thing that makes
/// §4.2's field preservation actually happen on a run against a real directory.
///
/// <para><b>Why this exists at all.</b> <see cref="DescriptionResolver"/> preserves a <c>manual</c> or
/// <c>llm</c> description only if it is handed the existing frontmatter; handed
/// <see langword="null"/> it re-derives, silently destroying the text. Every construction of that
/// delegate before this type was a test's lambda, so nothing in the shipped path preserved anything.
/// <c>--check</c> makes that gap load-bearing rather than latent: <see cref="BundleDrift.Check"/>
/// regenerates over a copy of the bundle precisely so preservation runs, and without a reader it
/// would report every hand-written description as drift, for ever.</para>
///
/// <para><b>Not thread-safe, and single-run by design.</b> The returned delegate memoizes what it
/// reads, so one generation pass touches each concept file once; it is meant to be built immediately
/// before a <see cref="IConceptGenerator.Generate"/> call and dropped after it. Reading the bundle
/// while the same run is writing it would be a different thing entirely -- and cannot happen, since
/// <see cref="BundleWriter"/> stages every file and commits only after generation has finished.</para>
/// </summary>
public static class ExistingBundleFrontmatter
{
    /// <summary>
    /// A reader over the bundle at <paramref name="bundleRoot"/>: returns the frontmatter of the
    /// concept stored under an id, or <see langword="null"/> when there is no such file, it cannot be
    /// read, or it is not a parseable OKF document.
    ///
    /// <para><b>Every failure is <see langword="null"/>.</b> A null means "this producer has not
    /// written that concept before", which sends <see cref="DescriptionResolver"/> down its ordinary
    /// derive path -- the same outcome as a fresh bundle. Throwing instead would turn one unreadable
    /// file into a failed generation, and a bundle with one corrupt concept would become impossible to
    /// regenerate, which is the opposite of what a producer is for. The cost is stated plainly: a
    /// hand-written description inside a file this method cannot parse is not preserved, because
    /// nothing here can find it.</para>
    /// </summary>
    /// <param name="bundleRoot">Root of the bundle being written into.</param>
    /// <exception cref="ArgumentException"><paramref name="bundleRoot"/> is null or empty.</exception>
    public static Func<ConceptId, Frontmatter?> For(string bundleRoot)
    {
        ArgumentException.ThrowIfNullOrEmpty(bundleRoot);

        var cache = new Dictionary<string, Frontmatter?>(StringComparer.Ordinal);

        return id =>
        {
            ArgumentNullException.ThrowIfNull(id);

            var key = id.ToString();
            if (cache.TryGetValue(key, out var cached))
            {
                return cached;
            }

            var frontmatter = Read(bundleRoot, id);
            cache[key] = frontmatter;
            return frontmatter;
        };
    }

    /// <summary>
    /// <b>An ungated read, and the invariant that makes it safe is not written in this file.</b>
    /// <c>id.ToPath(bundleRoot)</c> is string concatenation; nothing here asks
    /// <see cref="BundlePaths.ResolveInsideRoot"/> anything, so in a bundle holding a junction at
    /// <c>code/x</c>, generating <c>code/x/report</c> reads the frontmatter of somebody else's
    /// <c>report.md</c> on the far side of it. Three separate facts stop that borrowed text reaching
    /// the operator, and none of the three lives here -- which is why they are named here.
    ///
    /// <para><b>One: the id that reads is the id that is written.</b> Every call of
    /// <c>GenerateOptions.ExistingFrontmatter</c> in <c>ConceptGenerator</c> -- overview, package, doc,
    /// code concept, container concept -- passes the id of the concept currently being built, and each
    /// result is immediately paired with that same id in a <c>GeneratedConcept</c>. Same id means the
    /// same path and so the same link: a description can only be borrowed into the one document that
    /// would have been written where it was read from. There is no cross-concept borrowing to reason
    /// about.</para>
    ///
    /// <para><b>Two: the containment links carry no description.</b> A parent concept names its
    /// children through <c>ConceptGenerator.Child</c>, which is an id and the link text and nothing
    /// else. So even the parent of a linked-through id -- <c>overview.md</c>, a package concept -- has
    /// no channel through which the borrowed text could ride into a document that <i>is</i>
    /// written.</para>
    ///
    /// <para><b>Three: the write is refused.</b> <c>BundleWriter.CommitStaging</c> resolves every
    /// staged destination and refuses the ones that do not land inside the bundle root -- a set that
    /// includes every id this read can reach outside for, and then some, since an unresolvable path is
    /// refused there too. The concept is reported as a write failure instead, and the borrowed
    /// description never lands anywhere.</para>
    ///
    /// <para><b>Recorded rather than gated, deliberately.</b> Gating would cost a component walk per
    /// concept id and buy no property that the three facts above do not already give. But they are
    /// three facts in three other files, none of which mentions this one: a change that made a refused
    /// write stop being refused, or gave <c>Child</c> a description field, would silently make this
    /// read matter. The read itself also opens a path somebody else chose, which is the same class of
    /// exposure <c>GenerationManifest.WriteTo</c> records for the manifest's read side -- reached
    /// through a different shape of link here, since a junction anywhere along the id's path is enough
    /// and the manifest's needs a file symbolic link on the last component.</para>
    /// </summary>
    private static Frontmatter? Read(string bundleRoot, ConceptId id)
    {
        try
        {
            var path = id.ToPath(bundleRoot);
            if (!File.Exists(path))
            {
                return null;
            }

            return OkfDocument.TryParse(File.ReadAllText(path), out var document, out _)
                ? document.Frontmatter
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }
}
