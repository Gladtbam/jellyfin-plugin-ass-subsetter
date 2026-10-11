using Jellyfin.Plugin.AssSubsetter.Fonts.Domain;
using Jellyfin.Plugin.AssSubsetter.Fonts.Maintenance;

namespace Jellyfin.Plugin.AssSubsetter.Tests.Fonts.Maintenance;

public sealed class FontMaintenanceCoordinatorTests
{
    [Fact]
    public async Task DirectoryUseIncludesDescendantReadAndWriteLeases()
    {
        var coordinator = new FontMaintenanceCoordinator();
        string directory = Path.Combine(Path.GetTempPath(), "lease-dir");
        string path = Path.Combine(directory, "child", "font.ttf");
        await using var lease = await coordinator.AcquireExclusiveAsync([path], CancellationToken.None);
        Assert.True(coordinator.IsDirectoryInUse(directory));
        Assert.False(coordinator.IsDirectoryInUse(directory + "-other"));
        await lease.DisposeAsync();
        Assert.False(coordinator.IsDirectoryInUse(directory));
        RuntimeFontFace face = new(1, path, 0, "A", "Regular", [], 400, 5, 0, false, false, "TrueType", 0, false);
        await using var read = await coordinator.AcquireReadAsync(face, CancellationToken.None);
        Assert.True(coordinator.IsDirectoryInUse(directory));
    }
    [Fact]
    public async Task AcquireExclusiveAsync_ShouldWaitForActiveReadLease()
    {
        var coordinator = new FontMaintenanceCoordinator();
        RuntimeFontFace face = new(1, "/fonts/example.ttf", 0, "Example", "Regular", ["Example"], 400, 5, 0, false, false, "TrueType", 0, false);
        await using FontFileLease readLease = await coordinator.AcquireReadAsync(face, TestContext.Current.CancellationToken);

        Task<IAsyncDisposable> exclusiveTask = coordinator.AcquireExclusiveAsync([face.Path], TestContext.Current.CancellationToken);
        await Task.Delay(25, TestContext.Current.CancellationToken);
        Assert.False(exclusiveTask.IsCompleted);

        await readLease.DisposeAsync();
        await using IAsyncDisposable exclusiveLease = await exclusiveTask;
        Assert.True(exclusiveTask.IsCompletedSuccessfully);
    }
}
