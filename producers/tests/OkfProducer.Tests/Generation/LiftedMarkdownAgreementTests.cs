// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net;
using OkfProducer.Core.Generation;
using OkfProducer.Core.Scanning;

namespace OkfProducer.Tests.Generation;

/// <summary>
/// The executable guard <c>LiftedMarkdown</c> never had (#111): every transform in it is checked here
/// against the REAL <see cref="LinkScanner"/> -- the thing that reads the bundle this producer writes --
/// instead of against a hand-copy of the scanner's rules, which is what the unit tests next door pin and
/// what drifted when #101 and #105 rewrote the scanner (<c>`` a ` b `` [x](y)</c> was believed to be
/// inside a code span, the scanner closed the span, and the producer shipped a live link it thought it
/// had suppressed).
///
/// <para>
/// The property is the same everywhere: render lifted text into the shape a generated body gives it,
/// follow it with one link the PRODUCER wrote (<c>[A](/a)</c>), and ask the scanner what it finds. The
/// answer has to be exactly that one link -- not an extra link manufactured by the lifted text, and not
/// the producer's own link severed by it (an unclosed span or fence swallowing what comes after, the
/// failure <c>okf validate</c> cannot see because nothing dangles).
/// </para>
///
/// <para>
/// A fixed payload list covers the shapes the issue names; a seeded random sweep over the characters
/// the scanner treats specially covers the shapes nobody thought of. The sweep is deterministic -- the
/// same seed, the same inputs, on every host -- so a failure here is reproducible from its message.
/// </para>
/// </summary>
public sealed class LiftedMarkdownAgreementTests
{
    private const string OwnLink = "- [A](/a)";

    // Each context is how the generator actually places the transform's output.
    private static readonly (string Name, Func<string, string> Render)[] Contexts =
    [
        ("body text in a heading", t => $"# {LiftedMarkdown.LiftedBodyText(t)}\n\n## Contains\n\n{OwnLink}\n"),
        ("paragraph", t => $"# T\n\n{LiftedMarkdown.LiftedBodyParagraph(t)}\n\n## Contains\n\n{OwnLink}\n"),
        ("derived description", t => $"# T\n\n{LiftedMarkdown.BodyDescription(t, "doc-comment")}\n\n## Contains\n\n{OwnLink}\n"),
        ("code span", t => $"# T\n\n- {LiftedMarkdown.CodeSpan(t)} — {LiftedMarkdown.CodeSpan(t)}\n\n## Contains\n\n{OwnLink}\n"),
    ];

    private static readonly string[] Payloads =
    [
        // The issue's own payload, and the shapes around it.
        "`` a ` b `` [x](y)",
        "`` a ` b `` [x](y) ``",
        "` a `` b ` [x](y)",
        "``a`b``[x](y)",
        "```a``` [x](y)",
        "\\` [x](y) `",
        "\\\\` [x](y) `",
        "`a\\` [x](y)`",
        // A span that never closes, and one that closes on a later line.
        "`unclosed [x](y)",
        "`a\n[x](y)\nb`",
        "``\n[x](y)\n``",
        // Raw HTML the scanner blanks.
        "<a title=\"[x](y)\">",
        "<!-- [x](y) -->",
        "<span>[x](y)</span>",
        "<code>`</code> [x](y)",
        // Reference links and their definitions.
        "[x][r]\n\n[r]: /target",
        "[r]: /target\n\n[x][r]",
        "[x]\n\n[x]: /target",
        "[x][]\n\n[x]: /target",
        // Containers.
        "> [x](y)",
        "> a\n> [x](y)",
        "- [x](y)",
        "1. [x](y)\n2. [z](w)",
        "   [x](y)",
        // Images, angle-bracket destinations, nesting, escapes.
        "![x](y)",
        "[x](<y z>)",
        "[x [y](z)](w)",
        "[x\\](y)",
        "\\[x](y)",
        "[x](y\\)",
        "[x]\\(y)",
        // Fences.
        "```\n[x](y)",
        "```\n[x](y)\n```\n[z](w)",
        "~~~\n[x](y)",
        "```\n[x](y)\n~~~",
        "    [x](y)",
        "\t[x](y)",
        // Headings that are structural claims.
        "# Citations\n1. [x](y)",
        "[x](y)\n---",
        "[x](y)\n===",
    ];

    [Fact]
    public void The_scanner_finds_exactly_the_producers_own_link_after_every_fixed_payload()
    {
        var failures = new List<string>();
        foreach (var payload in Payloads)
        {
            Check(payload, failures);
        }

        Assert.True(failures.Count == 0, Describe(failures));
    }

    [Fact]
    public void The_scanner_finds_exactly_the_producers_own_link_after_any_text_made_of_the_characters_it_treats_specially()
    {
        // Pieces, not characters: a backtick RUN, a bracket, a backslash, an angle bracket and a newline
        // are what the scanner's rules turn on, and a run's length is a rule of its own.
        string[] pieces =
        [
            "`", "``", "```", "[", "]", "(", ")", "\\", "<", ">", "!", "\n", "\n\n", " ", "    ", "\t",
            "x", "y", "/", ":", "#", "-", "*", "&", "~~~", "[x](y)", "](y)", "<a ", "-->", "<!--", "|",
        ];

        var random = new Random(20260914);
        var failures = new List<string>();
        for (var i = 0; i < 40_000; i++)
        {
            var length = random.Next(1, 14);
            var text = string.Concat(Enumerable.Range(0, length).Select(_ => pieces[random.Next(pieces.Length)]));
            Check(text, failures);
        }

        Assert.True(failures.Count == 0, Describe(failures));
    }

    [Fact]
    public void A_generated_bundle_built_from_hostile_text_has_the_same_link_structure_as_one_built_from_benign_text()
    {
        // The guard above checks the transforms in frames. This one checks what the producer actually
        // WRITES: the whole body of every concept of a generated bundle, run through the scanner, with the
        // hostile text sitting in each of the four places lifted text enters (the repository name, a
        // package name, a package description, a documentation title). Two invariants, both about links:
        // every link target is a concept that exists (nothing manufactured), and each concept has as many
        // links as the same bundle built from harmless text (nothing severed). Together they are what
        // `okf validate` cannot see -- a severed branch dangles nothing.
        var benign = LinkCounts(Generate("my-repo", "pkg", "A package.", "Guide"));

        var random = new Random(111);
        string[] pieces = ["`", "``", "[", "]", "(", ")", "\\", "<", ">", "\n", " ", "x", "y", "/", "<!--", "-->", "~~~", "[x](y)", "](y)"];
        var inputs = Payloads.Concat(Enumerable.Range(0, 300).Select(_ =>
            string.Concat(Enumerable.Range(0, random.Next(1, 10)).Select(_ => pieces[random.Next(pieces.Length)])))).ToList();

        var failures = new List<string>();
        foreach (var hostile in inputs)
        {
            foreach (var (slot, concepts) in new (string, IReadOnlyList<GeneratedConcept>)[]
            {
                ("repository name", Generate(hostile, "pkg", "A package.", "Guide")),
                ("package name", Generate("my-repo", "pkg" + hostile, "A package.", "Guide")),
                ("package description", Generate("my-repo", "pkg", hostile, "Guide")),
                ("documentation title", Generate("my-repo", "pkg", "A package.", hostile)),
            })
            {
                var ids = concepts.Select(c => "/" + c.Id).ToHashSet(StringComparer.Ordinal);
                foreach (var concept in concepts)
                {
                    var links = LinkScanner.ExtractLinks(concept.Document.Body);
                    foreach (var link in links.Where(l => !ids.Contains(l.Target)))
                    {
                        failures.Add($"[{slot}] {Show(hostile)} -> {concept.Id} links to [{link.Text}]({link.Target}), which is not a concept");
                    }
                }

                var counts = LinkCounts(concepts);
                if (!counts.SequenceEqual(benign))
                {
                    failures.Add($"[{slot}] {Show(hostile)} -> links per concept {string.Join(",", counts)}, benign {string.Join(",", benign)}");
                }
            }
        }

        Assert.True(failures.Count == 0, $"{failures.Count} failure(s):\n" + string.Join("\n", failures.OrderBy(f => f.Length).Take(25)));
    }

    private static IReadOnlyList<GeneratedConcept> Generate(string repoName, string packageName, string description, string docTitle) =>
        new ConceptGenerator().Generate(new RepositorySnapshot(
            "/repo",
            repoName,
            [new PackageManifest("nuget", "Pkg.csproj", packageName, description)],
            [new DocFile("guide.md", docTitle)]));

    private static List<int> LinkCounts(IReadOnlyList<GeneratedConcept> concepts) =>
        concepts.Select(c => LinkScanner.ExtractLinks(c.Document.Body).Count).ToList();

    private static void Check(string text, List<string> failures)
    {
        foreach (var (name, render) in Contexts)
        {
            string body;
            try
            {
                body = render(text);
            }
            catch (Exception e)
            {
                failures.Add($"[{name}] {Show(text)} -> threw {e.GetType().Name}");
                continue;
            }

            var links = LinkScanner.ExtractLinks(body);
            if (links is not [{ Text: "A", Target: "/a" }])
            {
                failures.Add($"[{name}] {Show(text)} -> {string.Join(" ", links.Select(l => $"[{l.Text}]({l.Target})"))}");
            }
        }
    }

    // The shortest first: the minimal reproduction is the one worth reading. Counted by context and by
    // kind as well, because "an extra link" and "the producer's own link lost" are different bugs.
    private static string Describe(List<string> failures)
    {
        var summary = failures
            .GroupBy(Kind)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => $"  {g.Count(),6}  {g.Key}");

        return $"{failures.Count} case(s) where the scanner disagrees with the producer about which links exist:\n"
            + string.Join("\n", summary)
            + "\nshortest first:\n"
            + string.Join("\n", failures.OrderBy(f => f.Length).Take(25));
    }

    private static string Kind(string failure)
    {
        var context = failure[..(failure.IndexOf(']') + 1)];
        var found = failure[(failure.LastIndexOf("-> ", StringComparison.Ordinal) + 3)..];
        return context + (found.Contains("[A](/a)", StringComparison.Ordinal)
            ? " extra link"
            : found.Length == 0 ? " own link lost" : " own link lost, others found");
    }

    private static string Show(string text) =>
        "\"" + text.Replace("\\", "\\\\").Replace("\n", "\\n").Replace("\t", "\\t") + "\"";
}
