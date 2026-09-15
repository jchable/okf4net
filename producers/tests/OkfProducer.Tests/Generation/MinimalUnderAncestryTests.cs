// SPDX-License-Identifier: LGPL-3.0-or-later
using OkfProducer.Core.Generation;

namespace OkfProducer.Tests.Generation;

/// <summary>
/// Covers <see cref="ConceptGenerator.MinimalUnderAncestry"/>, the O(k) replacement (Task E10a) for the
/// pairwise O(k²) "does any other key dominate me" filter that used to sit inline in
/// <c>AttributePackages</c> (§5.2's "one level down" package-child rule).
/// </summary>
public class MinimalUnderAncestryTests
{
    private const char Nul = '\0';

    [Fact]
    public void Drops_a_key_whose_ancestor_is_also_present()
    {
        var keys = new SortedSet<string>(StringComparer.Ordinal)
        {
            "A",
            "A" + Nul + "B",
            "A" + Nul + "B" + Nul + "C",
            "D",
        };

        var minimal = ConceptGenerator.MinimalUnderAncestry(keys).ToList();

        Assert.Equal(["A", "D"], minimal);
    }

    /// <summary>
    /// The sort-order trap: under <see cref="StringComparer.Ordinal"/>, <c>NUL</c> is the smallest
    /// character, so <c>"A\0Ba"</c> sorts between <c>"A\0B"</c> and <c>"A\0B\0C"</c> even though it is a
    /// *sibling* of <c>"A\0B"</c>'s child <c>C</c>, not a descendant of <c>"A\0B"</c> itself -- there is
    /// no <c>NUL</c> right after the shared "A\0B" prefix. A filter that tracked only a plain string
    /// prefix (rather than the "candidate + NUL" boundary) would wrongly drop <c>"A\0Ba"</c> here.
    /// </summary>
    [Fact]
    public void Keeps_a_sibling_whose_text_happens_to_start_with_a_kept_keys_text()
    {
        var keys = new SortedSet<string>(StringComparer.Ordinal)
        {
            "A" + Nul + "B",
            "A" + Nul + "Ba",
            "A" + Nul + "B" + Nul + "C",
        };

        var minimal = ConceptGenerator.MinimalUnderAncestry(keys).ToList();

        Assert.Equal(["A" + Nul + "B", "A" + Nul + "Ba"], minimal);
    }

    [Fact]
    public void Keeps_both_keys_when_neither_is_an_ancestor_of_the_other()
    {
        var keys = new SortedSet<string>(StringComparer.Ordinal) { "A", "AB" };

        var minimal = ConceptGenerator.MinimalUnderAncestry(keys).ToList();

        Assert.Equal(["A", "AB"], minimal);
    }

    [Fact]
    public void Empty_input_yields_no_output()
    {
        var keys = new SortedSet<string>(StringComparer.Ordinal);

        Assert.Empty(ConceptGenerator.MinimalUnderAncestry(keys));
    }

    [Fact]
    public void Single_key_is_always_minimal()
    {
        var keys = new SortedSet<string>(StringComparer.Ordinal) { "A" + Nul + "B" };

        Assert.Equal(["A" + Nul + "B"], ConceptGenerator.MinimalUnderAncestry(keys).ToList());
    }

    /// <summary>
    /// The equivalence oracle: for several thousand random key sets drawn from a small alphabet that
    /// includes <c>NUL</c> -- so ancestor/descendant/sibling-prefix shapes like the trap above show up
    /// by construction -- <see cref="ConceptGenerator.MinimalUnderAncestry"/> must agree exactly (as a
    /// set) with the original O(k²) pairwise expression this task replaced:
    /// <c>keys.Where(key =&gt; !keys.Any(other =&gt; IsProperAncestor(other, key)))</c>. The seed is fixed
    /// so a failure is reproducible.
    ///
    /// <para>This oracle is also the mutant-catching evidence for the task: a deliberately wrong variant
    /// of the filter that compares <c>key.StartsWith(lastMinimal)</c> instead of
    /// <c>key.StartsWith(lastMinimal + NUL)</c> was run against this same generator and failed within
    /// the first handful of cases (see task-E10a-report.md) -- proving the property actually
    /// discriminates, rather than passing vacuously.</para>
    /// </summary>
    [Fact]
    public void Equals_the_O_of_k_squared_pairwise_filter_across_random_key_sets()
    {
        char[] alphabet = ['A', 'B', 'C', Nul];
        var random = new Random(20260915);

        for (var trial = 0; trial < 5000; trial++)
        {
            var keys = new SortedSet<string>(StringComparer.Ordinal);
            var count = random.Next(1, 9);
            for (var i = 0; i < count; i++)
            {
                var length = random.Next(1, 7);
                var chars = new char[length];
                for (var j = 0; j < length; j++)
                {
                    chars[j] = alphabet[random.Next(alphabet.Length)];
                }

                keys.Add(new string(chars));
            }

            var expected = keys.Where(key => !keys.Any(other => IsProperAncestorOracle(other, key))).ToList();
            var actual = ConceptGenerator.MinimalUnderAncestry(keys).ToList();

            Assert.True(
                expected.SequenceEqual(actual, StringComparer.Ordinal),
                $"Trial {trial}: expected [{string.Join(", ", expected.Select(Escape))}] but got "
                + $"[{string.Join(", ", actual.Select(Escape))}] for keys "
                + $"[{string.Join(", ", keys.Select(Escape))}].");
        }
    }

    /// <summary>
    /// A test-local restatement of the private <c>ConceptGenerator.IsProperAncestor</c> (candidate is a
    /// strict ancestor of key iff key is longer and starts with candidate + NUL) -- kept independent of
    /// the production helper on purpose, since the oracle exists to catch mistakes in the fast path, not
    /// to share code with it.
    /// </summary>
    private static bool IsProperAncestorOracle(string candidate, string key) =>
        key.Length > candidate.Length && key.StartsWith(candidate + Nul, StringComparison.Ordinal);

    private static string Escape(string s) => s.Replace(Nul, '#');
}
