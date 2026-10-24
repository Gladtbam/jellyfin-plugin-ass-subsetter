using Jellyfin.Plugin.AssSubsetter.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.AssSubsetter.Tests.Services;

public sealed class BoundedBackgroundSchedulerTests
{
    [Fact]
    public async Task Admission_IsBoundedDeduplicatedAndRetryable()
    {
        await using var scheduler = new BoundedBackgroundScheduler(NullLogger.Instance, pendingCapacity: 1);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pendingStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int running = 0;
        async Task Block(CancellationToken token)
        {
            if (Interlocked.Increment(ref running) == 2)
            {
                started.TrySetResult();
            }

            await release.Task.WaitAsync(token);
        }

        Assert.True(scheduler.TryEnqueue("one", Block));
        // Wait for the first dequeue before using the single pending slot.
        await WaitUntilAsync(() => Volatile.Read(ref running) == 1);
        Assert.True(scheduler.TryEnqueue("two", Block));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.True(scheduler.TryEnqueue("one", _ => throw new InvalidOperationException("duplicate executed")));
        Assert.True(scheduler.TryEnqueue("pending", async token =>
        {
            pendingStarted.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
        }));
        Assert.False(scheduler.TryEnqueue("retry", _ => Task.CompletedTask));
        Assert.Equal(3, scheduler.Count);
        Assert.Equal(2, Volatile.Read(ref running));
        release.TrySetResult();
        await pendingStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.True(scheduler.TryEnqueue("retry", _ => Task.CompletedTask));
        await scheduler.StopAsync();
        Assert.Equal(0, scheduler.Count);
        Assert.False(scheduler.TryEnqueue("after-stop", _ => Task.CompletedTask));
    }

    [Fact]
    public async Task Shutdown_CancelsAndWaitsForWorkerCleanup()
    {
        await using var scheduler = new BoundedBackgroundScheduler(NullLogger.Instance);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True(scheduler.TryEnqueue("job", async token =>
        {
            started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, token);
            }
            finally
            {
                cancelled.TrySetResult();
                await cleanup.Task;
            }
        }));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Task stop = scheduler.StopAsync();
        try
        {
            await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.Same(stop, scheduler.StopAsync());
            Assert.False(stop.IsCompleted);
        }
        finally
        {
            cleanup.TrySetResult();
        }

        await stop.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(0, scheduler.Count);
    }

    [Fact]
    public async Task FailedJob_ReleasesKeyAndWorker()
    {
        await using var scheduler = new BoundedBackgroundScheduler(NullLogger.Instance, concurrency: 1);
        Assert.True(scheduler.TryEnqueue("job", _ => throw new InvalidOperationException("expected")));
        await WaitUntilAsync(() => scheduler.Count == 0);
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True(scheduler.TryEnqueue("job", _ =>
        {
            completed.TrySetResult();
            return Task.CompletedTask;
        }));
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        while (!condition())
        {
            await Task.Delay(10, timeout.Token);
        }
    }
}
