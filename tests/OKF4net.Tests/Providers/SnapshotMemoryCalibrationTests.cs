// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Runtime.CompilerServices;
using System.Text;
using OKF4net.Providers;
using Xunit.Abstractions;

namespace OKF4net.Tests.Providers;

/// <summary>
/// #151: <see cref="Bundle.EstimatedMemoryBytes"/> against the bytes an image really retains: what dropping it frees, measured with
/// <see cref="GC.GetTotalMemory(bool)"/> with the image alive and then dropped (the provider stays alive throughout, so its storage is in
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

    [SkippableTheory]
    [MemberData(nameof(Shapes))]
    public void The_estimate_stays_within_its_tolerance_of_the_retained_bytes(string shape, int count, int bodyBytes, int linksPerConcept)
    {
        // Skipped on macOS by decision (2026-11-16): on the macOS CI runner GC.GetTotalMemory read one shape or
        // another two to four times too high on each run, even keeping the smallest of three measures, while
        // Windows and Linux read 0.98-1.04. The heap reading is what is unreliable there, not the model, which
        // these two platforms pin; the ceiling's behaviour tests (MemoryCeilingTests) run everywhere.
        Skip.If(OperatingSystem.IsMacOS(), "the heap measure is unreliable on macOS; the model is pinned on Windows and Linux");
        var provider = Build(count, bodyBytes, linksPerConcept);

        var (estimate, measured) = Measure(provider);
        var ratio = (double)estimate / measured;
        output.WriteLine($"{shape}: estimate {estimate:N0} B, measured {measured:N0} B, ratio {ratio:F2}");

        Assert.InRange(ratio, 1 / Tolerance, Tolerance);
    }

    /// <summary>
    /// The bytes the image retains: the heap with the image alive minus the heap once it is dropped. Measuring
    /// what dropping it frees, rather than what loading it added, keeps out anything the load allocates that
    /// outlives it without belonging to it (lazily built statics, a runtime's own caches, the provider's call
    /// log), which the "after minus before" form counted and which differed between runtimes (macOS read about
    /// twice the Windows and Linux figure). A warm-up load runs first for the same reason, and the smallest of
    /// <see cref="Repetitions"/> measures is kept: outside noise can only add to a measure (one macOS run read
    /// 24.6 MB for an image the other runs and platforms put at 6.2 MB), never take from it.
    /// </summary>
    private static (long Estimate, long Measured) Measure(InMemoryBundleProvider provider)
    {
        _ = LoadHeld(provider, out _, out _); // warm-up: first-use allocations happen here, not in the measure
        var estimate = 0L;
        var smallest = long.MaxValue;
        for (var i = 0; i < Repetitions; i++)
        {
            var image = LoadHeld(provider, out estimate, out var held);
            var released = GC.GetTotalMemory(forceFullCollection: true);
            Assert.False(image.IsAlive, "the image was still reachable when its release was measured");
            smallest = Math.Min(smallest, held - released);
        }

        return (estimate, smallest);
    }

    /// <summary>How many times each shape is measured; the smallest measure is kept.</summary>
    private const int Repetitions = 3;

    /// <summary>Loads an image, measures the heap while it is alive, and returns it only weakly, so that it is unreachable once this returns.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference LoadHeld(InMemoryBundleProvider provider, out long estimate, out long held)
    {
        var bundle = Bundle.Load(provider);
        estimate = bundle.EstimatedMemoryBytes;
        held = GC.GetTotalMemory(forceFullCollection: true);
        GC.KeepAlive(bundle);
        return new WeakReference(bundle);
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
