using Jellyfin.Plugin.AssSubsetter.Fonts.Maintenance;
using Jellyfin.Plugin.AssSubsetter.ScheduledTasks;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Jellyfin.Plugin.AssSubsetter.Tests.Fonts.Maintenance;

public sealed class FontRestoreRequestWorkerTests
{
    [Fact]
    public async Task Signal_ShouldProcessDurableRestoreRequestWithoutScheduledTask()
    {
        var processed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        var executor = new Mock<IFontMaintenanceTaskExecutor>();
        executor.Setup(item => item.RestoreQuarantinedFontsAsync(It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                int call = Interlocked.Increment(ref calls);
                if (call == 2)
                {
                    processed.TrySetResult();
                    return Task.FromResult(new FontTaskExecutionResult(4, "restored", 9));
                }

                return Task.FromResult(new FontTaskExecutionResult(null, "request-missing"));
            });

        using var worker = new FontRestoreRequestWorker(executor.Object, NullLogger<FontRestoreRequestWorker>.Instance);
        await worker.StartAsync(CancellationToken.None);
        await SpinUntilAsync(() => Volatile.Read(ref calls) >= 1);
        worker.Signal();
        await processed.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await worker.StopAsync(CancellationToken.None);

        Assert.True(calls >= 2);
    }

    [Fact]
    public async Task StartAsync_ShouldDrainRequestPersistedBeforeWorkerStarts()
    {
        var processed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        var executor = new Mock<IFontMaintenanceTaskExecutor>();
        executor.Setup(item => item.RestoreQuarantinedFontsAsync(It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                if (Interlocked.Increment(ref calls) == 1)
                {
                    processed.TrySetResult();
                    return Task.FromResult(new FontTaskExecutionResult(4, "restored", 9));
                }

                return Task.FromResult(new FontTaskExecutionResult(null, "request-missing"));
            });

        using var worker = new FontRestoreRequestWorker(executor.Object, NullLogger<FontRestoreRequestWorker>.Instance);
        await worker.StartAsync(CancellationToken.None);
        await processed.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await worker.StopAsync(CancellationToken.None);

        Assert.True(calls >= 1);
    }
    private static async Task SpinUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
        {
            await Task.Delay(10, timeout.Token);
        }
    }
}
