using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AssSubsetter.Configuration;
using Jellyfin.Plugin.AssSubsetter.ScheduledTasks;
using Jellyfin.Plugin.AssSubsetter.Services;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.AssSubsetter.Tests.ScheduledTasks;

public class FontCacheUpdateTaskTests : IDisposable
{
    private readonly string _tempDataPath;
    private readonly PluginConfiguration _config;
    private readonly FontCacheUpdateTask _task;

    public FontCacheUpdateTaskTests()
    {
        _tempDataPath = Path.Join(Path.GetTempPath(), "FontCacheTaskTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDataPath);

        _config = new PluginConfiguration
        {
            FontCacheFilePath = Path.Join(_tempDataPath, "font_caches.json")
        };

        var fontCacheManager = new FontCacheManager(NullLogger<FontCacheManager>.Instance, _config);
        _task = new FontCacheUpdateTask(fontCacheManager, NullLogger<FontCacheUpdateTask>.Instance);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldCompleteSuccessfully()
    {
        // Arrange
        var progressMock = new Mock<IProgress<double>>();

        // Act
        await _task.ExecuteAsync(progressMock.Object, CancellationToken.None);

        // Assert
        Assert.True(File.Exists(_config.FontCacheFilePath));
    }

    [Fact]
    public void GetDefaultTriggers_ShouldReturnStartupAndInterval()
    {
        // Arrange & Act
        var triggers = _task.GetDefaultTriggers().ToList();

        // Assert
        Assert.Equal(2, triggers.Count);
        Assert.Contains(triggers, t => t.Type == TaskTriggerInfoType.StartupTrigger);
        Assert.Contains(triggers, t => t.Type == TaskTriggerInfoType.IntervalTrigger && t.IntervalTicks == TimeSpan.FromHours(24).Ticks);
    }

    [Fact]
    public void Name_ShouldBeCorrect()
    {
        Assert.Equal("构建 ASS Subsetter 本地字体索引缓存", _task.Name);
        Assert.Equal("LocalFontCacheUpdateTask", _task.Key);
        Assert.Equal("Subtitles", _task.Category);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDataPath))
        {
            try { Directory.Delete(_tempDataPath, true); } catch { /* Ignore */ }
        }
    }
}
