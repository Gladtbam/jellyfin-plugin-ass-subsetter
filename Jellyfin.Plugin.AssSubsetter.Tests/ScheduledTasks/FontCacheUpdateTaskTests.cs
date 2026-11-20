using Jellyfin.Plugin.AssSubsetter.Configuration;
using Jellyfin.Plugin.AssSubsetter.Helpers;
using Jellyfin.Plugin.AssSubsetter.ScheduledTasks;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Jellyfin.Plugin.AssSubsetter.Tests.ScheduledTasks;

public sealed class FontCacheUpdateTaskTests
{
    [Fact]
    public async Task ExecuteAsync_DefaultConfigurationRunsOnlyAnalysis()
    {
        await AssertDispatchesAsync(new PluginConfiguration(), FontMaintenanceStages.Analyze);
    }

    [Theory]
    [InlineData(false, false, false, FontMaintenanceStages.None)]
    [InlineData(false, true, false, FontMaintenanceStages.Quarantine)]
    [InlineData(false, false, true, FontMaintenanceStages.Rename)]
    [InlineData(true, true, true, FontMaintenanceStages.Analyze | FontMaintenanceStages.Quarantine | FontMaintenanceStages.Rename)]
    public async Task ExecuteAsync_DispatchesOnlyEnabledStagesOnce(bool analyze, bool quarantine, bool rename, FontMaintenanceStages expectedStages)
    {
        var configuration = new PluginConfiguration
        {
            EnableScheduledFontAnalysis = analyze,
            EnableScheduledFontQuarantine = quarantine,
            EnableScheduledFontRenaming = rename
        };

        await AssertDispatchesAsync(configuration, expectedStages);
    }

    [Fact]
    public void GetDefaultTriggers_ShouldReturnDailyIntervalOnly()
    {
        var task = CreateTask(Mock.Of<IFontMaintenanceTaskExecutor>(), new PluginConfiguration());

        TaskTriggerInfo trigger = Assert.Single(task.GetDefaultTriggers());

        Assert.Equal(TaskTriggerInfoType.IntervalTrigger, trigger.Type);
        Assert.Equal(TimeSpan.FromHours(24).Ticks, trigger.IntervalTicks);
    }

    [Fact]
    public void Identity_ShouldRemainStableAndLocalized()
    {
        var task = CreateTask(Mock.Of<IFontMaintenanceTaskExecutor>(), new PluginConfiguration());

        Assert.Equal("LocalFontCacheUpdateTask", task.Key);
        Assert.Equal("Subtitles", task.Category);
        Assert.Equal(LocalizationHelper.GetString("FontMaintenanceTask_Name"), task.Name);
        Assert.Equal(LocalizationHelper.GetString("FontMaintenanceTask_Description"), task.Description);
    }

    private static async Task AssertDispatchesAsync(PluginConfiguration configuration, FontMaintenanceStages expectedStages)
    {
        var executor = new Mock<IFontMaintenanceTaskExecutor>(MockBehavior.Strict);
        using var cancellation = new CancellationTokenSource();
        CancellationToken token = cancellation.Token;
        executor.Setup(item => item.RunScheduledMaintenanceAsync(expectedStages, It.IsAny<int>(), It.IsAny<IProgress<double>>(), token))
            .ReturnsAsync(new FontTaskExecutionResult(null, "Complete"));
        var task = CreateTask(executor.Object, configuration);

        await task.ExecuteAsync(new InlineProgress(_ => { }), token);

        executor.Verify(item => item.RunScheduledMaintenanceAsync(expectedStages, It.IsAny<int>(), It.IsAny<IProgress<double>>(), token), Times.Once);
    }

    private static FontCacheUpdateTask CreateTask(IFontMaintenanceTaskExecutor executor, PluginConfiguration configuration)
        => new(executor, () => configuration, NullLogger<FontCacheUpdateTask>.Instance);
}
