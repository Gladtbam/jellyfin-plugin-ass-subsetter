using Jellyfin.Plugin.AssSubsetter.Configuration;
using Jellyfin.Plugin.AssSubsetter.ScheduledTasks;
using Moq;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.AssSubsetter.Tests.ScheduledTasks;

public sealed class FontCacheUpdateRetentionTests
{
    [Fact]
    public async Task ExecuteAsync_UsesCurrentConfigurationAndReportsCompletion()
    {
        var executor = new Mock<IFontMaintenanceTaskExecutor>();
        executor.Setup(item => item.RefreshAnalyzeAndPruneAsync(2, It.IsAny<IProgress<double>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FontTaskExecutionResult(1, "analyzed"));
        var configuration = new PluginConfiguration { FontHistoryRetentionCount = 2 };
        var progress = new List<double>();
        var task = new FontCacheUpdateTask(
            executor.Object,
            () => configuration,
            NullLogger<FontCacheUpdateTask>.Instance);

        await task.ExecuteAsync(new InlineProgress(progress.Add), CancellationToken.None);

        executor.Verify(item => item.RefreshAnalyzeAndPruneAsync(2, It.IsAny<IProgress<double>>(), It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal(new double[] { 0, 100 }, progress);
    }

    [Fact]
    public async Task ExecuteAsync_ZeroHistoryRetentionKeepsReportsAndGenerations()
    {
        var executor = new Mock<IFontMaintenanceTaskExecutor>();
        executor.Setup(item => item.RefreshAnalyzeAndPruneAsync(0, It.IsAny<IProgress<double>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FontTaskExecutionResult(1, "analyzed"));
        var task = new FontCacheUpdateTask(
            executor.Object,
            () => new PluginConfiguration { FontHistoryRetentionCount = 0 },
            NullLogger<FontCacheUpdateTask>.Instance);

        await task.ExecuteAsync(new InlineProgress(_ => { }), CancellationToken.None);

        executor.Verify(item => item.RefreshAnalyzeAndPruneAsync(0, It.IsAny<IProgress<double>>(), It.IsAny<CancellationToken>()), Times.Once);
    }
}
