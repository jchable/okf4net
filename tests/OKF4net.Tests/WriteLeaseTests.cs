// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net.Agents;

namespace OKF4net.Tests;

/// <summary>
/// #152 step 1: the write lock is an explicit <see cref="WriteLease"/> over
/// <see cref="SemaphoreSlim"/>s. Every concurrency here is driven by thread state, events and
/// joins, never by a delay.
/// </summary>
public class WriteLeaseTests
{
    private const string Fm = "type: Note\ntitle: Probe\ndescription: A probe concept.";

    [Fact]
    public void A_second_acquisition_waits_until_the_lease_is_released()
    {
        using var tmp = new TempDir();
        var writer = new BundleConceptWriter(tmp.Path);
        var other = new BundleConceptWriter(tmp.Path);
        var lease = writer.EnterWriteLock();
        var acquired = false;
        var second = new Thread(() =>
        {
            using var l = other.EnterWriteLock();
            acquired = true;
        })
        { IsBackground = true };
        second.Start();

        // The thread is blocked in the wait for the lease (state WaitSleepJoin), so it has not
        // acquired it while the lease is held.
        Assert.True(SpinWait.SpinUntil(() => (second.ThreadState & ThreadState.WaitSleepJoin) != 0, TimeSpan.FromSeconds(30)));
        Assert.False(acquired);

        lease.Dispose();
        Assert.True(second.Join(TimeSpan.FromSeconds(30)));
        Assert.True(acquired);
    }

    [Fact]
    public void A_default_lease_disposes_to_nothing()
    {
        default(WriteLease).Dispose();
    }

    [Fact]
    public void A_lease_may_be_released_on_another_thread()
    {
        using var tmp = new TempDir();
        var writer = new BundleConceptWriter(tmp.Path);
        var lease = writer.EnterWriteLock();
        var releaser = new Thread(lease.Dispose);
        releaser.Start();
        releaser.Join();

        using var again = writer.EnterWriteLock();
        Assert.True(again.HoldsLexicalGateForTest);
    }

    [Fact]
    public void A_write_callback_that_reads_the_same_root_runs_after_the_lease_is_released()
    {
        using var tmp = new TempDir();
        tmp.Write("notes/seed.md", $"---\n{Fm}\n---\n\nseed\n");
        BundleConceptWriter? writer = null;
        OkfBundleTools? tools = null;
        bool? lockFreeInCallback = null;
        string? readInCallback = null;
        var calls = 0;
        writer = new BundleConceptWriter(tmp.Path, () =>
        {
            calls++;
            // The bundle's resolved lock must be free: the callback is not under the lease.
            var resolved = (SemaphoreSlim)writer!.CurrentLockObjectForTest();
            lockFreeInCallback = resolved.Wait(0);
            if (lockFreeInCallback == true)
            {
                resolved.Release();
            }

            // And a co-located reader over the same root does not wait for itself.
            readInCallback = tools!.ReadConcept("notes/seed");
        });
        tools = new OkfBundleTools(tmp.Path);

        string? result = null;
        var thread = new Thread(() => result = writer.WriteConcept("notes/new", Fm, "body\n")) { IsBackground = true };
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "the write did not return: the callback waited for the lock");

        Assert.DoesNotContain("Error", result, StringComparison.Ordinal);
        Assert.Equal(1, calls);
        Assert.True(lockFreeInCallback);
        Assert.Contains("seed", readInCallback, StringComparison.Ordinal);
    }

    [Fact]
    public void A_batch_notifies_once_per_committed_write_after_the_lease_is_released()
    {
        using var tmp = new TempDir();
        tmp.Write("p/a.md", $"---\n{Fm}\n---\n\na\n");
        tmp.Write("p/b.md", $"---\n{Fm}\n---\n\nb\n");
        BundleConceptWriter? writer = null;
        var calls = 0;
        var freeEachTime = true;
        writer = new BundleConceptWriter(tmp.Path, () =>
        {
            calls++;
            var resolved = (SemaphoreSlim)writer!.CurrentLockObjectForTest();
            if (resolved.Wait(0))
            {
                resolved.Release();
            }
            else
            {
                freeEachTime = false;
            }
        });

        var outcome = writer.RecordVerifications(["p/a", "p/b"], "human:ada");

        Assert.Equal(2, calls);
        Assert.True(freeEachTime);
        Assert.DoesNotContain("Error", outcome.ToString(), StringComparison.Ordinal);
    }
}
