// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Globalization;
using System.Text;
using OKF4net.Providers;

namespace OKF4net.Tests.Providers;

/// <summary>
/// Derives four Unicode tables from the host's ICU and compares them with the ones
/// shipped in <see cref="NfcQuickCheck"/> (the NFC quick-check sets) and
/// <see cref="CanonicalForm"/> (canonical decompositions and combining-class ranks).
/// Runs only where normalization works (never under invariant globalization). On a
/// mismatch — first run with empty tables, or a host ICU with a newer Unicode than
/// the tables — the failure names a file holding the exact C# literals to paste.
/// </summary>
public class UnicodeTableTests
{
    private static bool NormalizationWorks => "e\u0301".Normalize(NormalizationForm.FormC).Length == 1;

    private static string Nfc(string s) => s.Normalize(NormalizationForm.FormC);

    private static string Nfd(string s) => s.Normalize(NormalizationForm.FormD);

    private static bool IsHangulSyllable(int cp) => cp is >= 0xAC00 and <= 0xD7A3;

    /// <summary>
    /// No = { c : NFC(c) != c } ∪ { c : the normalizer refuses c } (decision 14).
    /// Maybe = the last character of every direct canonical pair that recomposes,
    /// walked from each primary composite's full decomposition (head = NFC(all but
    /// last); while the head is one character, the last is a composing second
    /// element; then recurse on the head).
    /// Decompositions = { c → NFD(c) : NFD(c) != c, c not a Hangul syllable } (Hangul is
    /// arithmetic, <see cref="Hangul_decomposition_is_arithmetic"/> pins that).
    /// </summary>
    internal static (SortedSet<int> No, SortedSet<int> Maybe, SortedDictionary<int, string> Decompositions, int Refused) Derive()
    {
        var no = new SortedSet<int>();
        var maybe = new SortedSet<int>();
        var decompositions = new SortedDictionary<int, string>();
        var refused = 0;
        for (var cp = 0; cp <= 0x10FFFF; cp++)
        {
            if (cp is >= 0xD800 and <= 0xDFFF)
            {
                continue;
            }

            var s = new Rune(cp).ToString();
            string nfc;
            string nfd;
            try
            {
                nfc = Nfc(s);
                nfd = Nfd(s);
            }
            catch (ArgumentException)
            {
                // The host normalizer refuses this scalar (noncharacters such as U+FFFE): fail closed.
                no.Add(cp);
                refused++;
                continue;
            }

            if (!string.Equals(nfc, s, StringComparison.Ordinal))
            {
                no.Add(cp);
            }

            if (!string.Equals(nfd, s, StringComparison.Ordinal) && !IsHangulSyllable(cp))
            {
                decompositions[cp] = nfd;
            }

            var seq = nfd.EnumerateRunes().ToList();
            if (seq.Count < 2 || !string.Equals(Nfc(string.Concat(seq)), s, StringComparison.Ordinal))
            {
                continue;
            }

            while (seq.Count >= 2)
            {
                var head = Nfc(string.Concat(seq.Take(seq.Count - 1))).EnumerateRunes().ToList();
                if (head.Count != 1)
                {
                    break;
                }

                maybe.Add(seq[^1].Value);
                seq = Nfd(head[0].ToString()).EnumerateRunes().ToList();
            }
        }

        return (no, maybe, decompositions, refused);
    }

    /// <summary>
    /// The canonical combining classes, as RANKS (1 = the lowest non-zero class), derived
    /// without the UCD numbers: NFD reorders two adjacent non-starters exactly when the
    /// second has the lower class, so comparing NFD(r + x) with x + r orders x against a
    /// representative r, and equal classes reorder in neither direction. A non-starter is
    /// a mark (General_Category Mn or Mc — every ccc != 0 code point is one) that is its own
    /// NFD and that NFD moves against at least one of three probes of classes 1, 230 and
    /// 240 (U+0334, U+0301, U+0345): a class strictly between 1 and 240 moves against one
    /// of the two extremes, class 230 moves against 240, 1 against 230, 240 against 1.
    /// </summary>
    internal static SortedDictionary<int, int> DeriveCombiningClassRanks()
    {
        int[] probes = [0x0334, 0x0301, 0x0345];
        var nonStarters = new List<int>();
        for (var cp = 0; cp <= 0x10FFFF; cp++)
        {
            if (cp is >= 0xD800 and <= 0xDFFF)
            {
                continue;
            }

            var rune = new Rune(cp);
            var category = Rune.GetUnicodeCategory(rune);
            if (category is not (UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark))
            {
                continue;
            }

            var s = rune.ToString();
            string nfd;
            try
            {
                nfd = Nfd(s);
            }
            catch (ArgumentException)
            {
                continue;
            }

            if (!string.Equals(nfd, s, StringComparison.Ordinal))
            {
                continue; // decomposes: its constituents get a class, it does not
            }

            foreach (var p in probes)
            {
                var probe = new Rune(p).ToString();
                if (!string.Equals(Nfd(probe + s), probe + s, StringComparison.Ordinal)
                    || !string.Equals(Nfd(s + probe), s + probe, StringComparison.Ordinal))
                {
                    nonStarters.Add(cp);
                    break;
                }
            }
        }

        // Representatives of the classes met so far, in increasing class order.
        var representatives = new List<int>();
        var classIndexOf = new Dictionary<int, int>();
        foreach (var cp in nonStarters)
        {
            var x = new Rune(cp).ToString();
            var lo = 0;
            var hi = representatives.Count - 1;
            var found = -1;
            while (lo <= hi)
            {
                var mid = (lo + hi) >> 1;
                var r = new Rune(representatives[mid]).ToString();
                var rx = Nfd(r + x);
                var xr = Nfd(x + r);
                if (string.Equals(rx, x + r, StringComparison.Ordinal))
                {
                    hi = mid - 1; // x moved before r: class(x) < class(r)
                }
                else if (string.Equals(xr, r + x, StringComparison.Ordinal))
                {
                    lo = mid + 1; // r moved before x: class(x) > class(r)
                }
                else
                {
                    found = mid;
                    break;
                }
            }

            if (found < 0)
            {
                representatives.Insert(lo, cp);
                found = lo;
            }

            classIndexOf[cp] = representatives[found];
        }

        var rankOf = representatives.Select((rep, i) => (rep, rank: i + 1)).ToDictionary(t => t.rep, t => t.rank);
        return new SortedDictionary<int, int>(classIndexOf.ToDictionary(kv => kv.Key, kv => rankOf[kv.Value]));
    }

    internal static List<(int Start, int End)> ToRanges(SortedSet<int> set)
    {
        var ranges = new List<(int, int)>();
        int? start = null;
        var prev = -2;
        foreach (var cp in set)
        {
            if (start is null)
            {
                start = cp;
            }
            else if (cp != prev + 1)
            {
                ranges.Add((start.Value, prev));
                start = cp;
            }

            prev = cp;
        }

        if (start is not null)
        {
            ranges.Add((start.Value, prev));
        }

        return ranges;
    }

    /// <summary>Flat triples (start, end, rank) over consecutive code points of one rank — the layout of <see cref="CanonicalForm.CombiningClassData"/>.</summary>
    internal static List<int> ToClassTriples(SortedDictionary<int, int> ranks)
    {
        var data = new List<int>();
        int? start = null;
        var prev = -2;
        var rank = 0;
        foreach (var (cp, r) in ranks)
        {
            if (start is not null && (cp != prev + 1 || r != rank))
            {
                data.AddRange([start.Value, prev, rank]);
                start = null;
            }

            start ??= cp;
            rank = r;
            prev = cp;
        }

        if (start is not null)
        {
            data.AddRange([start.Value, prev, rank]);
        }

        return data;
    }

    /// <summary>Flat records (code, count, code points…) — the layout of <see cref="CanonicalForm.DecompositionData"/>.</summary>
    internal static List<int> ToDecompositionData(SortedDictionary<int, string> decompositions)
    {
        var data = new List<int>();
        foreach (var (cp, nfd) in decompositions)
        {
            var runes = nfd.EnumerateRunes().Select(r => r.Value).ToList();
            data.Add(cp);
            data.Add(runes.Count);
            data.AddRange(runes);
        }

        return data;
    }

    private static string RangeLiteral(string name, List<(int Start, int End)> ranges) =>
        $"    internal static readonly (int Start, int End)[] {name} =\n    [\n"
        + string.Join("\n", ranges.Select(r => $"        (0x{r.Start:X4}, 0x{r.End:X4}),"))
        + "\n    ];\n";

    private static string FlatLiteral(string name, List<int> data, Func<List<int>, int, int> recordLength)
    {
        var sb = new StringBuilder();
        sb.Append($"    internal static readonly int[] {name} =\n    [\n");
        for (var i = 0; i < data.Count;)
        {
            var len = recordLength(data, i);
            sb.Append("        ").Append(string.Join(", ", data.Skip(i).Take(len).Select(v => $"0x{v:X4}"))).Append(",\n");
            i += len;
        }

        return sb.Append("    ];\n").ToString();
    }

    [SkippableFact]
    public void Tables_match_the_host_normalizer()
    {
        Skip.IfNot(NormalizationWorks, "string.Normalize is a no-op here (invariant globalization): nothing to derive from");
        var (no, maybe, decompositions, refused) = Derive();
        var ranks = DeriveCombiningClassRanks();
        var expectedNo = ToRanges(no);
        var expectedMaybe = ToRanges(maybe);
        var expectedDecompositions = ToDecompositionData(decompositions);
        var expectedClasses = ToClassTriples(ranks);

        var literal = "// NfcQuickCheck.cs\n\n" + RangeLiteral("No", expectedNo) + "\n" + RangeLiteral("Maybe", expectedMaybe)
            + "\n// CanonicalForm.cs\n\n" + FlatLiteral("DecompositionData", expectedDecompositions, (d, i) => 2 + d[i + 1])
            + "\n" + FlatLiteral("CombiningClassData", expectedClasses, (_, _) => 3);
        var file = Path.Combine(AppContext.BaseDirectory, "unicode-tables.generated.txt");
        File.WriteAllText(file, literal);
        var message = $"The shipped Unicode tables differ from this host's ICU. The C# literals to paste into "
            + $"src/OKF4net/Providers/NfcQuickCheck.cs and CanonicalForm.cs are in {file}.\n"
            + $"(No: {no.Count} code points in {expectedNo.Count} ranges, {refused} of them refused by the normalizer; "
            + $"Maybe: {maybe.Count} in {expectedMaybe.Count}; Decompositions: {decompositions.Count} entries, {expectedDecompositions.Count} ints; "
            + $"combining classes: {ranks.Count} code points in {ranks.Values.Distinct().Count()} ranks, {expectedClasses.Count / 3} triples.)";
        Assert.True(expectedNo.SequenceEqual(NfcQuickCheck.No), message);
        Assert.True(expectedMaybe.SequenceEqual(NfcQuickCheck.Maybe), message);
        Assert.True(expectedDecompositions.SequenceEqual(CanonicalForm.DecompositionData), message);
        Assert.True(expectedClasses.SequenceEqual(CanonicalForm.CombiningClassData), message);
    }

    [SkippableFact]
    public void Hangul_decomposition_is_arithmetic()
    {
        Skip.IfNot(NormalizationWorks, "string.Normalize is a no-op here (invariant globalization): nothing to compare with");
        for (var cp = 0xAC00; cp <= 0xD7A3; cp++)
        {
            var s = new Rune(cp).ToString();
            Assert.Equal(Nfd(s), CanonicalForm.Decompose(s));
        }
    }

    [SkippableFact]
    public void Decompose_agrees_with_the_host_normalizer_on_every_scalar_and_on_mark_sequences()
    {
        Skip.IfNot(NormalizationWorks, "string.Normalize is a no-op here (invariant globalization): nothing to compare with");
        for (var cp = 0; cp <= 0x10FFFF; cp++)
        {
            if (cp is >= 0xD800 and <= 0xDFFF)
            {
                continue;
            }

            var s = new Rune(cp).ToString();
            string nfd;
            try
            {
                nfd = Nfd(s);
            }
            catch (ArgumentException)
            {
                continue; // a noncharacter: refused by the quick check before any fold
            }

            Assert.Equal(nfd, CanonicalForm.Decompose(s));
        }

        // Reordering: every pair of non-starters after a base, both orders.
        var marks = DeriveCombiningClassRanks().Keys.ToList();
        var rng = new Random(20261014);
        for (var i = 0; i < 20_000; i++)
        {
            var a = new Rune(marks[rng.Next(marks.Count)]).ToString();
            var b = new Rune(marks[rng.Next(marks.Count)]).ToString();
            var c = new Rune(marks[rng.Next(marks.Count)]).ToString();
            var s = "a" + a + b + c;
            Assert.Equal(Nfd(s), CanonicalForm.Decompose(s));
        }
    }

    [Theory]
    [InlineData(0x00E9, NfcStatus.Yes)]   // é precomposed
    [InlineData(0x0301, NfcStatus.Maybe)] // combining acute
    [InlineData(0x212B, NfcStatus.No)]    // ANGSTROM SIGN → Å
    [InlineData(0x2126, NfcStatus.No)]    // OHM SIGN → Ω
    [InlineData(0xF900, NfcStatus.No)]    // CJK compatibility ideograph
    [InlineData(0xFFFE, NfcStatus.No)]    // noncharacter the normalizer refuses: closed
    [InlineData(0x1161, NfcStatus.Maybe)] // Hangul jungseong A
    [InlineData(0xAC00, NfcStatus.Yes)]   // precomposed syllable
    [InlineData(0x0E48, NfcStatus.Yes)]   // Thai tone mark: composes with nothing
    [InlineData(0x093F, NfcStatus.Yes)]   // Devanagari vowel sign I
    [InlineData(0x0041, NfcStatus.Yes)]
    public void Spot_checks(int codePoint, NfcStatus expected)
    {
        Assert.Equal(expected, NfcQuickCheck.Of(new Rune(codePoint)));
    }

    [Theory]
    [InlineData("caf\u00e9", "cafe\u0301")]                     // é → e + acute
    [InlineData("\u212b", "A\u030a")]                            // ANGSTROM SIGN → A + ring (singleton to a composite, fully decomposed)
    [InlineData("\u00c5", "A\u030a")]                            // Å → the same
    [InlineData("\uac01", "\u1100\u1161\u11a8")]                // Hangul LVT syllable: arithmetic
    [InlineData("\u0229\u0301", "e\u0327\u0301")]               // ȩ + acute: already in canonical order (202 < 230)
    [InlineData("\u00e9\u0327", "e\u0327\u0301")]               // é + cedilla: REORDERED to the same form (the two names are one on APFS)
    [InlineData("a\u0305\u0335", "a\u0335\u0305")]              // overline (230) then stroke overlay (1): reordered
    [InlineData("\u0e17\u0e35\u0e48", "\u0e17\u0e35\u0e48")]  // Thai: unchanged
    [InlineData("plain", "plain")]
    public void Decompose_spot_checks(string input, string expected)
    {
        Assert.Equal(expected, CanonicalForm.Decompose(input));
    }

    [Theory]
    [InlineData("a\u0335\u0305", true)]   // stroke overlay (class 1) then overline (230)
    [InlineData("a\u0305\u0335", false)]  // the reverse: not canonically ordered
    [InlineData("e\u0327\u0301", true)]
    [InlineData("e\u0301\u0327", false)]
    [InlineData("a\u0301b\u0327", true)]  // a starter between them: two separate runs
    [InlineData("caf\u00e9", true)]
    public void IsCanonicallyOrdered_spot_checks(string input, bool expected)
    {
        Assert.Equal(expected, CanonicalForm.IsCanonicallyOrdered(input));
    }

    [Fact]
    public void Combining_class_ranks_order_like_the_ucd_classes()
    {
        Assert.Equal(0, CanonicalForm.ClassOf(new Rune('a')));
        Assert.Equal(0, CanonicalForm.ClassOf(new Rune(0x00E9)));
        var overlay = CanonicalForm.ClassOf(new Rune(0x0335));  // ccc 1
        var cedilla = CanonicalForm.ClassOf(new Rune(0x0327));  // ccc 202
        var below = CanonicalForm.ClassOf(new Rune(0x0323));    // ccc 220
        var above = CanonicalForm.ClassOf(new Rune(0x0301));    // ccc 230
        var iota = CanonicalForm.ClassOf(new Rune(0x0345));     // ccc 240
        Assert.True(0 < overlay && overlay < cedilla && cedilla < below && below < above && above < iota);
        Assert.Equal(above, CanonicalForm.ClassOf(new Rune(0x0300))); // grave: also 230
    }
}
