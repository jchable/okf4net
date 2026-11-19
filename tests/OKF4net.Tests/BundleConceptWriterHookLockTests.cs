// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net.Tests;

/// <summary>
/// #140: <see cref="BundleConceptWriter.BeforeLateReparseCheckForTest"/> fires inside the
/// provider's write, which is itself inside the writer's per-bundle write lock. Tests that use the
/// hook to simulate a racing filesystem substitution rely on that: a competing writer must not
/// be able to get in between the hook and the commit. This pins it, with no clock: the hook runs
/// a probe on ANOTHER thread, joins it, and the probe only asks whether the bundle's
/// lock can be taken (<see cref="SemaphoreSlim.Wait(int)"/> with a zero timeout never blocks).
/// </summary>
public class BundleConceptWriterHookLockTests
{
    private const string Fm = "type: Note\ntitle: Probe\ndescription: A probe concept.";

    public static TheoryData<string> Operations() => new() { "WriteConcept", "AppendToConceptAtomic", "AppendLog", "RecordVerifications" };

    private static void Perform(BundleConceptWriter writer, string operation)
    {
        var result = operation switch
        {
            "WriteConcept" => writer.WriteConcept("p/new", Fm, "b\n"),
            "AppendToConceptAtomic" => writer.AppendToConceptAtomic("p/seed", Fm, existing => (existing ?? string.Empty) + "more\n"),
            "AppendLog" => writer.AppendLog("Update", "probe"),
            "RecordVerifications" => writer.RecordVerifications(["p/seed"], "human:ada").ToString(),
            _ => throw new ArgumentOutOfRangeException(nameof(operation)),
        };
        Assert.DoesNotContain("Error", result, StringComparison.Ordinal);
    }

    private static bool CanTakeFromAnotherThread(SemaphoreSlim lockObject)
    {
        var taken = false;
        var probe = new Thread(() =>
        {
            if (lockObject.Wait(0))
            {
                taken = true;
                lockObject.Release();
            }
        });
        probe.Start();
        probe.Join();
        return taken;
    }

    [Theory]
    [MemberData(nameof(Operations))]
    public void The_write_lock_is_held_while_the_late_guard_hook_runs(string operation)
    {
        using var tmp = new TempDir();
        tmp.Write("p/seed.md", $"---\n{Fm}\n---\n\nseed\n");
        var writer = new BundleConceptWriter(tmp.Path);
        // A second writer over the same directory: the lock is the bundle's, not the instance's.
        var other = new BundleConceptWriter(tmp.Path);
        var lockObject = (SemaphoreSlim)other.CurrentLockObjectForTest();
        Assert.Same(lockObject, writer.CurrentLockObjectForTest());

        var fired = 0;
        bool? takenInsideHook = null;
        writer.BeforeLateReparseCheckForTest = () =>
        {
            fired++;
            takenInsideHook = CanTakeFromAnotherThread(lockObject);
        };

        Perform(writer, operation);

        Assert.Equal(1, fired);
        Assert.False(takenInsideHook, "another thread could take the bundle's write lock while the hook ran");

        // Control: the probe itself works, so the false above means "held", not "probe broken".
        Assert.True(CanTakeFromAnotherThread(lockObject));
    }
}
