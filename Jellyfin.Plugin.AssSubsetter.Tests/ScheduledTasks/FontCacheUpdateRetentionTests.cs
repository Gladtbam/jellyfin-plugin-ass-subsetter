using Jellyfin.Plugin.AssSubsetter.Configuration;
using Jellyfin.Plugin.AssSubsetter.ScheduledTasks;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Jellyfin.Plugin.AssSubsetter.Tests.ScheduledTasks;

public sealed class FontCacheUpdateRetentionTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public async Task ExecuteAsync_UsesCurrentRetentionCountAndReportsCompletion(int retentionCount)
    {
        var executor = new Mock<IFontMaintenanceTaskExecutor>(MockBehavior.Strict);
        executor.Setup(item => item.RunScheduledMaintenanceAsync(FontMaintenanceStages.Analyze, retentionCount, It.IsAny<IProgress<double>>(), CancellationToken.None))
            .ReturnsAsync(new FontTaskExecutionResult(1, "analyzed"));
        var configuration = new PluginConfiguration();
        var progress = new List<double>();
        var task = new FontCacheUpdateTask(
            executor.Object,
            () => configuration,
            NullLogger<FontCacheUpdateTask>.Instance);
        configuration.FontHistoryRetentionCount = retentionCount;

        await task.ExecuteAsync(new InlineProgress(progress.Add), CancellationToken.None);

        executor.Verify(item => item.RunScheduledMaintenanceAsync(FontMaintenanceStages.Analyze, retentionCount, It.IsAny<IProgress<double>>(), CancellationToken.None), Times.Once);
        Assert.Equal(new[] { 0d, 100d }, progress);
    }
}
