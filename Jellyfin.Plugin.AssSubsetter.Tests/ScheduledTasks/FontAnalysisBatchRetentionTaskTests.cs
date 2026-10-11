using Jellyfin.Plugin.AssSubsetter.Configuration;
using Jellyfin.Plugin.AssSubsetter.ScheduledTasks;
using Moq;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.AssSubsetter.Tests.ScheduledTasks;

public sealed class FontAnalysisBatchRetentionTaskTests
{
    [Fact]
    public async Task ExecuteAsync_UsesCurrentConfigurationAndReportsCompletion()
    {
        var executor = new Mock<IFontMaintenanceTaskExecutor>();
        executor.Setup(item => item.PruneHistoryAsync(2, It.IsAny<IProgress<double>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FontHistoryPruneResult(3, 5, false));
        var configuration = new PluginConfiguration { FontHistoryRetentionCount = 2 };
        var progress = new List<double>();
        var task = new FontAnalysisBatchRetentionTask(
            executor.Object,
            () => configuration,
            NullLogger<FontAnalysisBatchRetentionTask>.Instance);

        await task.ExecuteAsync(new InlineProgress(progress.Add), CancellationToken.None);

        executor.Verify(item => item.PruneHistoryAsync(2, It.IsAny<IProgress<double>>(), It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal(new double[] { 0, 100 }, progress);
    }

    [Fact]
    public async Task ExecuteAsync_ZeroHistoryRetentionKeepsReportsAndGenerations()
    {
        var executor = new Mock<IFontMaintenanceTaskExecutor>();
        executor.Setup(item => item.PruneHistoryAsync(0, It.IsAny<IProgress<double>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FontHistoryPruneResult(0, 0, false));
        var task = new FontAnalysisBatchRetentionTask(
            executor.Object,
            () => new PluginConfiguration { FontHistoryRetentionCount = 0 },
            NullLogger<FontAnalysisBatchRetentionTask>.Instance);

        await task.ExecuteAsync(new InlineProgress(_ => { }), CancellationToken.None);

        executor.Verify(item => item.PruneHistoryAsync(0, It.IsAny<IProgress<double>>(), It.IsAny<CancellationToken>()), Times.Once);
    }
}
