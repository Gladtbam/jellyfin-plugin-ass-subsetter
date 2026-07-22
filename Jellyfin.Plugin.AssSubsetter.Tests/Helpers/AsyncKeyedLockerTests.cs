using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AssSubsetter.Helpers;
using Xunit;

namespace Jellyfin.Plugin.AssSubsetter.Tests.Helpers;

public class AsyncKeyedLockerTests
{
    [Fact]
    public async Task LockAsync_ShouldSerializeSameKey()
    {
        var locker = new AsyncKeyedLocker<string>(StringComparer.Ordinal);
        IDisposable first = await locker.LockAsync("same", TestContext.Current.CancellationToken);

        Task<IDisposable> secondTask = locker.LockAsync("same", TestContext.Current.CancellationToken).AsTask();
        Assert.False(secondTask.IsCompleted);

        first.Dispose();
        using IDisposable second = await secondTask;
    }

    [Fact]
    public async Task LockAsync_ShouldAllowDifferentKeysConcurrently()
    {
        var locker = new AsyncKeyedLocker<string>(StringComparer.Ordinal);
        using IDisposable first = await locker.LockAsync("first", TestContext.Current.CancellationToken);

        Task<IDisposable> secondTask = locker.LockAsync("second", TestContext.Current.CancellationToken).AsTask();

        using IDisposable second = await secondTask;
        Assert.True(secondTask.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task LockAsync_ShouldReclaimEntriesAfterLastLease()
    {
        var locker = new AsyncKeyedLocker<string>(StringComparer.Ordinal);

        for (int index = 0; index < 100; index++)
        {
            using IDisposable lease = await locker.LockAsync(index.ToString(), TestContext.Current.CancellationToken);
        }

        Assert.Equal(0, locker.ActiveKeyCount);
    }

    [Fact]
    public async Task LockAsync_ShouldReclaimCancelledWaiter()
    {
        var locker = new AsyncKeyedLocker<string>(StringComparer.Ordinal);
        IDisposable first = await locker.LockAsync("same", TestContext.Current.CancellationToken);
        using var cancellation = new CancellationTokenSource();
        Task<IDisposable> waitingTask = locker.LockAsync("same", cancellation.Token).AsTask();

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waitingTask);
        Assert.Equal(1, locker.ActiveKeyCount);

        first.Dispose();
        Assert.Equal(0, locker.ActiveKeyCount);
    }
}
