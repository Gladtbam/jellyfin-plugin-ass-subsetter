using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AssSubsetter.Configuration;
using Jellyfin.Plugin.AssSubsetter.ScheduledTasks;
using Jellyfin.Plugin.AssSubsetter.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.AssSubsetter.Tests.ScheduledTasks;

public class LibraryScanTrackerTests : IDisposable
{
    private readonly string _tempDataPath;
    private readonly Mock<ILibraryManager> _mockLibraryManager;
    private readonly Mock<SubtitleCacheService> _mockCacheService;
    private readonly PluginConfiguration _config;
    private readonly LibraryScanTracker _tracker;

    public LibraryScanTrackerTests()
    {
        _tempDataPath = Path.Join(Path.GetTempPath(), "TrackerTests_" + Path.GetRandomFileName());
        Directory.CreateDirectory(_tempDataPath);

        _config = new PluginConfiguration { EnableAutoScanProcessing = true };

        _mockLibraryManager = new Mock<ILibraryManager>();
        _mockCacheService = new Mock<SubtitleCacheService>(
            _config, null!, _tempDataPath, new NullLogger<SubtitleCacheService>());

        _tracker = new LibraryScanTracker(
            _mockLibraryManager.Object, _mockCacheService.Object, _config, new NullLogger<LibraryScanTracker>());
    }

    [Fact]
    public async Task ProcessItem_ShouldTriggerCacheService_WhenVideoAddedAndSubtitleExists()
    {
        var itemId = Guid.NewGuid();
        string videoPath = Path.Join(_tempDataPath, "new_anime.mkv");
        string assPath = Path.Join(_tempDataPath, "new_anime.ass");

        await File.WriteAllTextAsync(videoPath, "video data", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(assPath, "ass data", TestContext.Current.CancellationToken);

        var video = new Video { Id = itemId, Path = videoPath };

        await _tracker.StartAsync(TestContext.Current.CancellationToken);

        _mockLibraryManager.Raise(m => m.ItemAdded += null, this, new ItemChangeEventArgs { Item = video });

        await Task.Delay(100, TestContext.Current.CancellationToken);

        _mockCacheService.Verify(
            s => s.GetOrGenerateSubtitleAsync(itemId, assPath, It.IsAny<CancellationToken>()),
            Times.Once(),
            "The cache service should be triggered within the background task.");
    }

    [Fact]
    public void ProcessItem_ShouldDoNothing_WhenAutoScanIsDisabled()
    {
        _config.EnableAutoScanProcessing = false;

        var video = new Video { Id = Guid.NewGuid(), Path = "dummy.mkv" };

        _mockLibraryManager.Raise(m => m.ItemAdded += null, this, new ItemChangeEventArgs { Item = video });

        Thread.Sleep(50);
        _mockCacheService.Verify(
            s => s.GetOrGenerateSubtitleAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    public void Dispose()
    {
        _tracker.Dispose();
        if (Directory.Exists(_tempDataPath))
        {
            Directory.Delete(_tempDataPath, true);
        }
    }
}
