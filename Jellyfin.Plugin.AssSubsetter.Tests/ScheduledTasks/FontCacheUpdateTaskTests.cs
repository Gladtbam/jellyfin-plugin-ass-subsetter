using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AssSubsetter.Configuration;
using Jellyfin.Plugin.AssSubsetter.ScheduledTasks;
using Jellyfin.Plugin.AssSubsetter.Services;
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
        _tempDataPath = Path.Combine(Path.GetTempPath(), "FontCacheTaskTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDataPath);

        _config = new PluginConfiguration
        {
            FontCacheFilePath = Path.Combine(_tempDataPath, "font_caches.json")
        };

        var fontCacheManager = new FontCacheManager(NullLogger<FontCacheManager>.Instance, () => _config);
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
    public void GetDefaultTriggers_ShouldReturnEmptyArray()
    {
        // Arrange & Act
        var triggers = _task.GetDefaultTriggers();

        // Assert
        Assert.Empty(triggers);
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
