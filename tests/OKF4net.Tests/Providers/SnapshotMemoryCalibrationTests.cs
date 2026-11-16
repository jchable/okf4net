// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Runtime.CompilerServices;
using System.Text;
using OKF4net.Providers;
using Xunit.Abstractions;

namespace OKF4net.Tests.Providers;

/// <summary>
/// #151: <see cref="Bundle.EstimatedMemoryBytes"/> against the bytes an image really retains, measured with
/// <see cref="GC.GetTotalMemory(bool)"/> around a load whose provider is built beforehand (its own storage is in
/// the baseline). Four shapes — many tiny concepts, medium ones, a few large ones, link-heavy ones — so the two
/// constants of the linear model are pinned on more than one point. The tolerance is stated, not tuned per shape:
/// the estimate must stay within a factor of <see cref="Tolerance"/> of the measurement, either way.
/// Not parallel with other tests: the measure reads the whole heap.
/// </summary>
[Collection(nameof(SnapshotMemoryCalibrationCollection))]
public class SnapshotMemoryCalibrationTests(ITestOutputHelper output)
{
    /// <summary>The estimate stays within this factor of the measured retained bytes, either way.</summary>
    private const double Tolerance = 1.25;

    public static TheoryData<string, int, int, int> Shapes => new()
    {
        { "tiny", 2_000, 120, 1 },
        { "medium", 400, 4_000, 4 },
        { "large", 40, 64_000, 8 },
        { "link-heavy", 600, 1_500, 40 },
    };

    [Theory]
    [MemberData(nameof(Shapes))]
    public void The_estimate_stays_within_its_tolerance_of_the_retained_bytes(string shape, int count, int bodyBytes, int linksPerConcept)
    {
        var provider = Build(count, bodyBytes, linksPerConcept);

        var (estimate, measured) = Measure(provider);
        var ratio = (double)estimate / measured;
        output.WriteLine($"{shape}: estimate {estimate:N0} B, measured {measured:N0} B, ratio {ratio:F2}");

        Assert.InRange(ratio, 1 / Tolerance, Tolerance);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (long Estimate, long Measured) Measure(InMemoryBundleProvider provider)
    {
        var before = GC.GetTotalMemory(forceFullCollection: true);
        var bundle = Bundle.Load(provider);
        var after = GC.GetTotalMemory(forceFullCollection: true);
        GC.KeepAlive(bundle);
        return (bundle.EstimatedMemoryBytes, after - before);
    }

    internal static InMemoryBundleProvider Build(int count, int bodyBytes, int linksPerConcept)
    {
        var provider = new InMemoryBundleProvider();
        var filler = new StringBuilder();
        while (filler.Length < bodyBytes)
        {
            filler.Append("Revenue is recognised when the order ships; refunds reduce it on the day they are issued. ");
        }

        var body = filler.ToString(0, bodyBytes);
        for (var i = 0; i < count; i++)
        {
            var links = new StringBuilder();
            for (var l = 1; l <= linksPerConcept; l++)
            {
                links.Append(CultureInfo(i, l, count));
            }

            provider.Add(
                $"area{i % 20:D2}/c{i:D5}.md",
                $"---\ntype: Metric\ntitle: Concept {i}\ndescription: The concept number {i} of the calibration bundle.\ntags: [calibration, shape]\n---\n\n{body}\n\n{links}");
        }

        return provider;
    }

    private static string CultureInfo(int i, int l, int count)
    {
        var target = (i + l) % count;
        return $"- [link {l}](/area{target % 20:D2}/c{target:D5}.md)\n";
    }
}

/// <summary>Runs the calibration alone: a heap measurement is meaningless while other tests allocate.</summary>
[CollectionDefinition(nameof(SnapshotMemoryCalibrationCollection), DisableParallelization = true)]
public sealed class SnapshotMemoryCalibrationCollection;
