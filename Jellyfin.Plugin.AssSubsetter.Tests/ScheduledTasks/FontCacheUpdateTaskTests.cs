using Jellyfin.Plugin.AssSubsetter.ScheduledTasks;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.AssSubsetter.Tests.ScheduledTasks;

public sealed class FontCacheUpdateTaskTests
{
    [Fact]
    public async Task ExecuteAsync_ShouldDelegateRefreshAndAnalysisAndReportBounds()
    {
        var executor = new RecordingTaskExecutor();
        var reports = new List<double>();
        var task = new FontCacheUpdateTask(executor, () => new Jellyfin.Plugin.AssSubsetter.Configuration.PluginConfiguration(), NullLogger<FontCacheUpdateTask>.Instance);

        await task.ExecuteAsync(new InlineProgress(reports.Add), CancellationToken.None);

        Assert.Equal(1, executor.RefreshAndAnalyzeCalls);
        Assert.Equal(0, reports.First());
        Assert.Equal(100, reports.Last());
        Assert.All(reports, value => Assert.InRange(value, 0, 100));
    }

    [Fact]
    public void GetDefaultTriggers_ShouldReturnDailyIntervalOnly()
    {
        var task = new FontCacheUpdateTask(new RecordingTaskExecutor(), () => new Jellyfin.Plugin.AssSubsetter.Configuration.PluginConfiguration(), NullLogger<FontCacheUpdateTask>.Instance);

        TaskTriggerInfo[] triggers = task.GetDefaultTriggers().ToArray();

        Assert.Single(triggers);
        Assert.Equal(TaskTriggerInfoType.IntervalTrigger, triggers[0].Type);
        Assert.Equal(TimeSpan.FromHours(24).Ticks, triggers[0].IntervalTicks);
    }

    [Fact]
    public void Identity_ShouldRemainStableAndLocalized()
    {
        var task = new FontCacheUpdateTask(new RecordingTaskExecutor(), () => new Jellyfin.Plugin.AssSubsetter.Configuration.PluginConfiguration(), NullLogger<FontCacheUpdateTask>.Instance);

        Assert.Equal("LocalFontCacheUpdateTask", task.Key);
        Assert.Equal("Subtitles", task.Category);
        Assert.Equal(Jellyfin.Plugin.AssSubsetter.Helpers.LocalizationHelper.GetString("FontAnalysisTask_Name"), task.Name);
        Assert.Equal(Jellyfin.Plugin.AssSubsetter.Helpers.LocalizationHelper.GetString("FontAnalysisTask_Description"), task.Description);
    }
}
