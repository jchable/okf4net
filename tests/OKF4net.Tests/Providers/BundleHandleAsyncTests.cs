// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net.Providers;

namespace OKF4net.Tests.Providers;

/// <summary>
/// #152 step 4: the single-flight <see cref="BundleHandle"/>. Every ordering is forced by a
/// <see cref="GatedProvider"/> and task state, never by a delay.
/// </summary>
public class BundleHandleAsyncTests
{
    private const string Fm = "type: Note\ntitle: Probe\ndescription: A probe concept.";

    private static InMemoryBundleProvider Store() => new InMemoryBundleProvider().Add("a.md", $"---\n{Fm}\n---\n\nA\n");

    [Fact]
    public async Task N_concurrent_GetCurrentAsync_calls_enumerate_once_and_share_the_image()
    {
        var gated = new GatedProvider(Store());
        using var handle = new BundleHandle(gated);

        var callers = Enumerable.Range(0, 6).Select(_ => handle.GetCurrentAsync()).ToArray();
        Assert.All(callers, c => Assert.False(c.IsCompleted));
        gated.Gate.SetResult();
        var images = await Task.WhenAll(callers);

        Assert.Equal(1, gated.Enumerations);
        Assert.All(images, image => Assert.Same(images[0], image));
    }

    [Fact]
    public async Task A_caller_that_cancels_does_not_cancel_the_shared_load()
    {
        var gated = new GatedProvider(Store());
        using var handle = new BundleHandle(gated);
        using var cts = new CancellationTokenSource();

        var cancelling = handle.GetCurrentAsync(cts.Token);
        var staying = handle.GetCurrentAsync();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelling);

        Assert.False(gated.Gate.Task.IsCompleted);
        Assert.False(staying.IsCompleted);
        gated.Gate.SetResult();
        var image = await staying;

        Assert.Single(image.Concepts);
        Assert.Equal(1, gated.Enumerations);
    }

    [Fact]
    public async Task An_access_after_Invalidate_waits_for_the_old_load_then_covers_its_generation()
    {
        var gated = new GatedProvider(Store());
        using var handle = new BundleHandle(gated);

        var before = handle.GetCurrentAsync();
        handle.Invalidate();
        var after = handle.GetCurrentAsync();

        // It did not join the load that started before the obligation: it waits for it to end.
        Assert.False(after.IsCompleted);
        Assert.Equal(1, gated.Enumerations);

        gated.Gate.SetResult();
        var oldImage = await before;
        var newImage = await after;

        Assert.NotSame(oldImage, newImage);
        Assert.Equal(2, gated.Enumerations);
        Assert.False(handle.IsStale);
    }

    [Fact]
    public async Task A_disposed_handle_still_finishes_the_load_a_search_on_its_snapshot_needs()
    {
        var gated = new GatedProvider(Store());
        var handle = new BundleHandle(gated);

        var search = handle.GetCurrentAsync();
        handle.Dispose(); // a catalog reload disposes the handles it replaced while that search runs
        gated.Gate.SetResult();
        var image = await search;

        Assert.Single(image.Concepts);
        Assert.Same(image, await handle.GetCurrentAsync());
    }

    [Fact]
    public async Task A_cancelled_load_ends_with_a_cancellation_and_disposes_the_enumerator()
    {
        var gated = new GatedProvider(new InMemoryBundleProvider().Add("a.md", $"---\n{Fm}\n---\n\nA\n").Add("b.md", $"---\n{Fm}\n---\n\nB\n").Add("c.md", $"---\n{Fm}\n---\n\nC\n"));
        gated.Gate.SetResult();
        using var cts = new CancellationTokenSource();
        gated.AfterListedName = n =>
        {
            if (n == 2)
            {
                cts.Cancel();
            }
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await Bundle.LoadAsync(gated, null, cts.Token));

        Assert.Equal(1, gated.Enumerations);
        Assert.Equal(1, gated.DisposedEnumerations);
    }

    [Fact]
    public async Task A_reload_that_fails_keeps_serving_the_previous_image_and_marks_it_stale()
    {
        var store = Store();
        var gated = new GatedProvider(store);
        gated.Gate.SetResult();
        using var handle = new BundleHandle(gated);
        var first = await handle.GetCurrentAsync();

        store.FailEnumerateWith = new InvalidOperationException("host failure");
        handle.Invalidate();
        var second = await handle.GetCurrentAsync();

        Assert.Same(first, second);
        Assert.True(handle.IsStale);
        Assert.IsType<BundleLoadException>(handle.LastLoadError);
    }

    [Fact]
    public async Task RefreshAsync_after_a_running_operation_starts_its_own_load()
    {
        var gated = new GatedProvider(Store());
        using var handle = new BundleHandle(gated);

        var running = handle.GetCurrentAsync();
        var refresh = handle.RefreshAsync();
        Assert.False(refresh.IsCompleted);
        gated.Gate.SetResult();
        var first = await running;
        var second = await refresh;

        Assert.NotSame(first, second);
        Assert.Equal(2, gated.Enumerations);
    }

    [Fact]
    public void The_synchronous_accessor_refuses_a_provider_that_does_not_complete_synchronously()
    {
        var gated = new GatedProvider(Store());
        using var handle = new BundleHandle(gated);

        Assert.Throws<InvalidOperationException>(() => handle.Current);
        Assert.Equal(0, gated.Calls);
    }
}
