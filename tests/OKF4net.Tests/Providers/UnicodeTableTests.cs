// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Globalization;
using System.Text;
using OKF4net.Providers;
using Xunit.Abstractions;

namespace OKF4net.Tests.Providers;

/// <summary>
/// Derives four Unicode tables from the host's ICU and compares them with the ones
/// shipped in <see cref="NfcQuickCheck"/> (the NFC quick-check sets) and
/// <see cref="CanonicalForm"/> (canonical decompositions and combining-class ranks).
/// Runs only where normalization works (never under invariant globalization). The
/// host's relation to the tables is read from the data itself
/// (<see cref="CompareWithHost"/>), on the Unicode stability policy — the canonical
/// decomposition and the combining class of an assigned character never change — so a
/// host can only add entries for code points a later Unicode assigned. Three cases:
/// <list type="bullet">
/// <item><b>Same version</b> — the host reproduces every table entry and adds none: the
/// shipped literals must equal the host's exactly, and any difference fails naming a
/// file holding the exact C# literals to paste.</item>
/// <item><b>Newer host</b> — it reproduces every table entry, and its only additions are
/// code points the tables do not mention at all: every No/Maybe code point, every
/// decomposition record and the ORDER of the combining-class ranks (not their numbers,
/// which shift when a later Unicode inserts a class) must be the host's;
/// <see cref="CanonicalForm.Decompose"/> is compared with the host only on what the
/// tables cover (<see cref="IsInsideTableCoverage"/>); the test output gives the number
/// of host-only code points, so a CI log shows the version gap.</item>
/// <item><b>Older host</b> — a table entry is missing or different on the host, a table
/// is empty (a first run), or a code point the tables mention gains an entry only on the
/// host: that host cannot validate the tables, and the failure names the file of
/// literals.</item>
/// </list>
/// </summary>
public class UnicodeTableTests(ITestOutputHelper output)
{
    /// <summary>How the host's ICU relates to the shipped tables (see the class summary).</summary>
    internal enum HostRelation
    {
        /// <summary>The host's tables are the shipped ones.</summary>
        Same,

        /// <summary>The host reproduces every shipped entry and adds entries only for code points the tables never mention.</summary>
        Newer,

        /// <summary>The host does not reproduce the shipped tables: it cannot validate them.</summary>
        Older,
    }

    /// <summary>
    /// The outcome of <see cref="CompareWithHost"/>: the relation, the shipped entries the
    /// host does not reproduce (non-empty exactly when <see cref="HostRelation.Older"/>), and
    /// the code points the tables never mention that the host gives an entry.
    /// </summary>
    internal sealed record TableComparison(HostRelation Relation, IReadOnlyList<string> Unreproduced, IReadOnlySet<int> HostOnly);

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

    /// <summary>The code points of range pairs — the layout of <see cref="NfcQuickCheck.No"/> and <see cref="NfcQuickCheck.Maybe"/>.</summary>
    internal static SortedSet<int> ExpandRanges(IEnumerable<(int Start, int End)> ranges)
    {
        var set = new SortedSet<int>();
        foreach (var (start, end) in ranges)
        {
            for (var cp = start; cp <= end; cp++)
            {
                set.Add(cp);
            }
        }

        return set;
    }

    /// <summary>The records of <see cref="CanonicalForm.DecompositionData"/>, read back: code point → decomposition.</summary>
    internal static SortedDictionary<int, string> ParseDecompositionData(IReadOnlyList<int> data)
    {
        var decompositions = new SortedDictionary<int, string>();
        for (var i = 0; i < data.Count; i += 2 + data[i + 1])
        {
            decompositions[data[i]] = string.Concat(Enumerable.Range(i + 2, data[i + 1]).Select(k => new Rune(data[k]).ToString()));
        }

        return decompositions;
    }

    /// <summary>The triples of <see cref="CanonicalForm.CombiningClassData"/>, read back: code point → rank.</summary>
    internal static SortedDictionary<int, int> ExpandClassTriples(IReadOnlyList<int> data)
    {
        var ranks = new SortedDictionary<int, int>();
        for (var i = 0; i < data.Count; i += 3)
        {
            for (var cp = data[i]; cp <= data[i + 1]; cp++)
            {
                ranks[cp] = data[i + 2];
            }
        }

        return ranks;
    }

    /// <summary>
    /// Classifies the host's tables against the shipped ones. A shipped entry the host does
    /// not reproduce — a No or Maybe code point that is not No or Maybe on the host, a
    /// decomposition the host lacks or gives differently, a ranked mark the host calls a
    /// starter, two marks the host orders differently (each shipped rank must map to exactly
    /// one host rank, and increasingly: the induced order, not the rank numbers, which a
    /// later Unicode shifts by inserting a class) — or an empty shipped table, or an entry
    /// the host alone gives a code point the tables mention, is <see cref="HostRelation.Older"/>.
    /// Otherwise the host's extra entries, all for code points the tables never mention,
    /// make it <see cref="HostRelation.Newer"/>; none make it <see cref="HostRelation.Same"/>.
    /// </summary>
    internal static TableComparison CompareWithHost(
        IReadOnlySet<int> tableNo,
        IReadOnlySet<int> tableMaybe,
        IReadOnlyDictionary<int, string> tableDecompositions,
        IReadOnlyDictionary<int, int> tableRanks,
        IReadOnlySet<int> hostNo,
        IReadOnlySet<int> hostMaybe,
        IReadOnlyDictionary<int, string> hostDecompositions,
        IReadOnlyDictionary<int, int> hostRanks)
    {
        static string Hex(string s) => string.Join(" ", s.EnumerateRunes().Select(r => $"U+{r.Value:X4}"));

        var unreproduced = new List<string>();
        foreach (var (name, tableCount, hostCount) in new[]
        {
            ("No", tableNo.Count, hostNo.Count),
            ("Maybe", tableMaybe.Count, hostMaybe.Count),
            ("Decompositions", tableDecompositions.Count, hostDecompositions.Count),
            ("Combining classes", tableRanks.Count, hostRanks.Count),
        })
        {
            if (tableCount == 0 && hostCount > 0)
            {
                unreproduced.Add($"{name}: the shipped table is empty (a first run)");
            }
        }

        unreproduced.AddRange(tableNo.Where(cp => !hostNo.Contains(cp)).Select(cp => $"No: U+{cp:X4} is not No on the host"));
        unreproduced.AddRange(tableMaybe.Where(cp => !hostMaybe.Contains(cp)).Select(cp => $"Maybe: U+{cp:X4} is not Maybe on the host"));
        foreach (var (cp, decomposition) in tableDecompositions)
        {
            if (!hostDecompositions.TryGetValue(cp, out var host))
            {
                unreproduced.Add($"Decompositions: U+{cp:X4} does not decompose on the host");
            }
            else if (!string.Equals(host, decomposition, StringComparison.Ordinal))
            {
                unreproduced.Add($"Decompositions: U+{cp:X4} is {Hex(decomposition)} in the table, {Hex(host)} on the host");
            }
        }

        var hostRanksOfTableRank = new SortedDictionary<int, SortedSet<int>>();
        foreach (var (cp, rank) in tableRanks)
        {
            if (!hostRanks.TryGetValue(cp, out var hostRank))
            {
                unreproduced.Add($"Combining classes: U+{cp:X4} (table rank {rank}) is a starter on the host");
                continue;
            }

            if (!hostRanksOfTableRank.TryGetValue(rank, out var hostRanksOfRank))
            {
                hostRanksOfTableRank[rank] = hostRanksOfRank = [];
            }

            hostRanksOfRank.Add(hostRank);
        }

        var previous = 0;
        foreach (var (rank, hostRanksOfRank) in hostRanksOfTableRank)
        {
            if (hostRanksOfRank.Count != 1)
            {
                unreproduced.Add($"Combining classes: the marks of table rank {rank} have host ranks {string.Join(", ", hostRanksOfRank)}");
            }
            else if (hostRanksOfRank.Min <= previous)
            {
                unreproduced.Add($"Combining classes: table rank {rank} is host rank {hostRanksOfRank.Min}, not above the previous table rank's host rank {previous}");
            }

            previous = hostRanksOfRank.Max;
        }

        var mentioned = new HashSet<int>(tableNo);
        mentioned.UnionWith(tableMaybe);
        mentioned.UnionWith(tableDecompositions.Keys);
        mentioned.UnionWith(tableRanks.Keys);
        var hostOnly = new SortedSet<int>();
        foreach (var (name, extra) in new[]
        {
            ("No", hostNo.Where(cp => !tableNo.Contains(cp))),
            ("Maybe", hostMaybe.Where(cp => !tableMaybe.Contains(cp))),
            ("Decompositions", hostDecompositions.Keys.Where(cp => !tableDecompositions.ContainsKey(cp))),
            ("Combining classes", hostRanks.Keys.Where(cp => !tableRanks.ContainsKey(cp))),
        })
        {
            foreach (var cp in extra)
            {
                if (mentioned.Contains(cp))
                {
                    unreproduced.Add($"{name}: U+{cp:X4} is mentioned by the tables, yet has this entry only on the host");
                }
                else
                {
                    hostOnly.Add(cp);
                }
            }
        }

        var relation = unreproduced.Count > 0 ? HostRelation.Older : hostOnly.Count > 0 ? HostRelation.Newer : HostRelation.Same;
        return new TableComparison(relation, unreproduced, hostOnly);
    }

    /// <summary>
    /// The failure message for a comparison, or null when the guard passes:
    /// <see cref="HostRelation.Same"/> passes only when the shipped literals equal the host's
    /// exactly; <see cref="HostRelation.Newer"/> passes, its checks being the absence of any
    /// unreproduced entry; <see cref="HostRelation.Older"/> fails, naming the first entries.
    /// </summary>
    internal static string? Judge(TableComparison comparison, bool literalsEqual, string pasteMessage) => comparison.Relation switch
    {
        HostRelation.Same => literalsEqual ? null : pasteMessage,
        HostRelation.Newer => null,
        _ => pasteMessage + $"\nThis host does not reproduce {comparison.Unreproduced.Count} shipped entries, so it cannot validate "
            + "the tables (an older Unicode than theirs, a first run, or a change the stability policy rules out); the first ones:\n"
            + string.Join("\n", comparison.Unreproduced.Take(20)),
    };

    /// <summary>
    /// Whether the shipped tables cover a scalar's host NFD: not when the host decomposes it
    /// and the tables have no record (Hangul syllables excepted, being arithmetic), nor when
    /// one of its code points has a host class and no table class. On a same-version host
    /// every scalar is covered, the tables being the host's.
    /// </summary>
    internal static bool IsInsideTableCoverage(
        int cp,
        string hostNfd,
        IReadOnlyDictionary<int, string> tableDecompositions,
        IReadOnlyDictionary<int, int> tableRanks,
        IReadOnlyDictionary<int, int> hostRanks)
    {
        if (!string.Equals(hostNfd, new Rune(cp).ToString(), StringComparison.Ordinal)
            && !IsHangulSyllable(cp)
            && !tableDecompositions.ContainsKey(cp))
        {
            return false;
        }

        return !hostNfd.EnumerateRunes().Any(r => hostRanks.ContainsKey(r.Value) && !tableRanks.ContainsKey(r.Value));
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
        var comparison = CompareWithHost(
            ExpandRanges(NfcQuickCheck.No),
            ExpandRanges(NfcQuickCheck.Maybe),
            ParseDecompositionData(CanonicalForm.DecompositionData),
            ExpandClassTriples(CanonicalForm.CombiningClassData),
            no,
            maybe,
            decompositions,
            ranks);
        output.WriteLine($"Host relation to the shipped tables: {comparison.Relation}; host-only code points: {comparison.HostOnly.Count}"
            + (comparison.HostOnly.Count > 0 ? $" (first: {string.Join(", ", comparison.HostOnly.Take(10).Select(cp => $"U+{cp:X4}"))})" : "")
            + $"; unreproduced shipped entries: {comparison.Unreproduced.Count}.");
        var literalsEqual = expectedNo.SequenceEqual(NfcQuickCheck.No)
            && expectedMaybe.SequenceEqual(NfcQuickCheck.Maybe)
            && expectedDecompositions.SequenceEqual(CanonicalForm.DecompositionData)
            && expectedClasses.SequenceEqual(CanonicalForm.CombiningClassData);
        var failure = Judge(comparison, literalsEqual, message);
        Assert.True(failure is null, failure);
    }

    [Fact]
    public void The_shipped_tables_read_back_to_their_own_literals()
    {
        // The newer-host branch compares the tables as read back by these three helpers, so
        // they must invert the layouts exactly (host-independent: no normalizer involved).
        Assert.Equal(NfcQuickCheck.No, ToRanges(ExpandRanges(NfcQuickCheck.No)));
        Assert.Equal(NfcQuickCheck.Maybe, ToRanges(ExpandRanges(NfcQuickCheck.Maybe)));
        Assert.Equal(CanonicalForm.DecompositionData, ToDecompositionData(ParseDecompositionData(CanonicalForm.DecompositionData)));
        Assert.Equal(CanonicalForm.CombiningClassData, ToClassTriples(ExpandClassTriples(CanonicalForm.CombiningClassData)));
    }

    private static string Text(params int[] codePoints) => string.Concat(codePoints.Select(cp => new Rune(cp).ToString()));

    /// <summary>A miniature of the four tables: two No, two Maybe, two decompositions, five marks in four ranks.</summary>
    private static (SortedSet<int> No, SortedSet<int> Maybe, SortedDictionary<int, string> Decompositions, SortedDictionary<int, int> Ranks) Sample() =>
    (
        [0x212B, 0xF900],
        [0x0301, 0x0327],
        new SortedDictionary<int, string> { [0x00E9] = Text(0x0065, 0x0301), [0x212B] = Text(0x0041, 0x030A) },
        new SortedDictionary<int, int> { [0x0335] = 1, [0x0327] = 2, [0x0300] = 3, [0x0301] = 3, [0x0345] = 4 }
    );

    private static TableComparison CompareSample(
        Action<SortedSet<int>, SortedSet<int>, SortedDictionary<int, string>, SortedDictionary<int, int>> changeHost)
    {
        var table = Sample();
        var host = Sample();
        changeHost(host.No, host.Maybe, host.Decompositions, host.Ranks);
        return CompareWithHost(table.No, table.Maybe, table.Decompositions, table.Ranks, host.No, host.Maybe, host.Decompositions, host.Ranks);
    }

    [Fact]
    public void Classification_same_version_passes_only_on_equal_literals()
    {
        var comparison = CompareSample((_, _, _, _) => { });
        Assert.Equal(HostRelation.Same, comparison.Relation);
        Assert.Empty(comparison.HostOnly);
        Assert.Null(Judge(comparison, literalsEqual: true, "paste"));
        Assert.Equal("paste", Judge(comparison, literalsEqual: false, "paste"));
    }

    [Fact]
    public void Classification_newer_host_with_new_code_points_and_an_inserted_class_passes()
    {
        var comparison = CompareSample((no, maybe, decompositions, ranks) =>
        {
            no.Add(0x105E5);
            maybe.Add(0x105D3);
            decompositions[0x105C9] = Text(0x105D2, 0x0307); // Unicode 16: TODHRI LETTER EI
            // A new class between table ranks 2 and 3: every rank above it shifts by one.
            ranks[0x16D70] = 3;
            ranks[0x0300] = 4;
            ranks[0x0301] = 4;
            ranks[0x0345] = 5;
        });
        Assert.Equal(HostRelation.Newer, comparison.Relation);
        Assert.Empty(comparison.Unreproduced);
        Assert.Equal(new[] { 0x105C9, 0x105D3, 0x105E5, 0x16D70 }, comparison.HostOnly);
        Assert.Null(Judge(comparison, literalsEqual: false, "paste"));
    }

    /// <summary>Each older (or inconsistent) host, keyed by the unreproduced line it must produce.</summary>
    private static readonly Dictionary<string, Action<SortedSet<int>, SortedSet<int>, SortedDictionary<int, string>, SortedDictionary<int, int>>> OlderHostChanges = new()
    {
        { "No: U+F900", (no, _, _, _) => no.Remove(0xF900) },
        { "Maybe: U+0327", (_, maybe, _, _) => maybe.Remove(0x0327) },
        { "Decompositions: U+00E9 does not decompose", (_, _, d, _) => d.Remove(0x00E9) },
        { "Decompositions: U+212B is U+0041 U+030A in the table, U+00C5 on the host", (_, _, d, _) => d[0x212B] = Text(0x00C5) },
        { "Combining classes: U+0345 (table rank 4) is a starter", (_, _, _, r) => r.Remove(0x0345) },
        { "the marks of table rank 3 have host ranks 3, 4", (_, _, _, r) => { r[0x0301] = 4; r[0x0345] = 5; } },
        { "table rank 3 is host rank 2", (_, _, _, r) => { r[0x0300] = 2; r[0x0301] = 2; r[0x0345] = 3; } },
        { "table rank 2 is host rank 1", (_, _, _, r) => { r[0x0335] = 2; r[0x0327] = 1; } },
        { "Maybe: U+0345 is mentioned by the tables", (_, maybe, _, _) => maybe.Add(0x0345) },
        { "Decompositions: U+0301 is mentioned by the tables", (_, _, d, _) => d[0x0301] = Text(0x0300) },
    };

    public static TheoryData<string> OlderHosts => new(OlderHostChanges.Keys);

    [Theory]
    [MemberData(nameof(OlderHosts))]
    public void Classification_older_host_fails_with_the_paste_message(string expected)
    {
        var comparison = CompareSample(OlderHostChanges[expected]);
        Assert.Equal(HostRelation.Older, comparison.Relation);
        Assert.Contains(comparison.Unreproduced, line => line.Contains(expected, StringComparison.Ordinal));
        var failure = Judge(comparison, literalsEqual: false, "The shipped Unicode tables differ from this host's ICU.");
        Assert.NotNull(failure);
        Assert.StartsWith("The shipped Unicode tables differ from this host's ICU.", failure, StringComparison.Ordinal);
        Assert.Contains(expected, failure, StringComparison.Ordinal);
    }

    [Fact]
    public void Classification_empty_shipped_tables_are_a_first_run_not_a_newer_host()
    {
        var host = Sample();
        var comparison = CompareWithHost(
            new SortedSet<int>(), new SortedSet<int>(), new SortedDictionary<int, string>(), new SortedDictionary<int, int>(),
            host.No, host.Maybe, host.Decompositions, host.Ranks);
        Assert.Equal(HostRelation.Older, comparison.Relation);
        Assert.Contains("Decompositions: the shipped table is empty (a first run)", comparison.Unreproduced);
    }

    [Fact]
    public void Coverage_excludes_what_only_a_newer_host_decomposes_or_ranks()
    {
        var table = Sample();
        var hostRanks = new SortedDictionary<int, int>(table.Ranks) { [0x16D70] = 3 };
        Assert.True(IsInsideTableCoverage(0x00E9, Text(0x0065, 0x0301), table.Decompositions, table.Ranks, hostRanks));
        Assert.True(IsInsideTableCoverage(0xAC00, Text(0x1100, 0x1161), table.Decompositions, table.Ranks, hostRanks));
        Assert.True(IsInsideTableCoverage(0x0041, Text(0x0041), table.Decompositions, table.Ranks, hostRanks));
        Assert.False(IsInsideTableCoverage(0x105C9, Text(0x105D2, 0x0307), table.Decompositions, table.Ranks, hostRanks));
        Assert.False(IsInsideTableCoverage(0x16D70, Text(0x16D70), table.Decompositions, table.Ranks, hostRanks));
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
        var tableDecompositions = ParseDecompositionData(CanonicalForm.DecompositionData);
        var tableRanks = ExpandClassTriples(CanonicalForm.CombiningClassData);
        var hostRanks = DeriveCombiningClassRanks();
        var outside = 0;
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

            // Only a newer host has scalars outside the tables' coverage; Tables_match_the_host_normalizer
            // rules on whether that host is newer or cannot validate the tables.
            if (!IsInsideTableCoverage(cp, nfd, tableDecompositions, tableRanks, hostRanks))
            {
                outside++;
                continue;
            }

            Assert.Equal(nfd, CanonicalForm.Decompose(s));
        }

        // Reordering: every pair of non-starters after a base, both orders — among the marks
        // both the host and the tables rank.
        var marks = hostRanks.Keys.Where(tableRanks.ContainsKey).ToList();
        output.WriteLine($"Scalars outside the tables' coverage, not compared: {outside}; marks ranked by the host only: {hostRanks.Count - marks.Count}.");
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
