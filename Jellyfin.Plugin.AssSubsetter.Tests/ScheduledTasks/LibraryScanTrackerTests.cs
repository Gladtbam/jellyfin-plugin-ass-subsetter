using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AssSubsetter.Configuration;
using Jellyfin.Plugin.AssSubsetter.Models;
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
            (Func<PluginConfiguration>)(() => _config), null!, null!, _tempDataPath, new NullLogger<SubtitleCacheService>());

        _tracker = new LibraryScanTracker(
            _mockLibraryManager.Object, _mockCacheService.Object, () => _config, new NullLogger<LibraryScanTracker>());
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
            s => s.GetOrGenerateSubtitleAsync(itemId, assPath, It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Once(),
            "The cache service should be triggered within the background task.");
    }

    [Fact]
    public async Task ProcessItem_ShouldProcessAllAssSubtitles_WhenVideoHasMultipleLanguages()
    {
        var itemId = Guid.NewGuid();
        string videoPath = Path.Join(_tempDataPath, "multi_language.mkv");
        string zhPath = Path.Join(_tempDataPath, "multi_language.zh.ass");
        string enPath = Path.Join(_tempDataPath, "multi_language.en.ass");
        await File.WriteAllTextAsync(videoPath, "video", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(zhPath, "zh", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(enPath, "en", TestContext.Current.CancellationToken);

        int callCount = 0;
        var allProcessed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _mockCacheService
            .Setup(service => service.GetOrGenerateSubtitleAsync(
                itemId, It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Callback(() =>
            {
                if (Interlocked.Increment(ref callCount) == 2)
                {
                    allProcessed.TrySetResult(true);
                }
            })
            .ReturnsAsync((Guid _, string path, int _, int _, CancellationToken _) => new SubtitleResult(path, "text/x-ssa", true));

        await _tracker.StartAsync(TestContext.Current.CancellationToken);
        _mockLibraryManager.Raise(
            manager => manager.ItemAdded += null,
            this,
            new ItemChangeEventArgs { Item = new Video { Id = itemId, Path = videoPath } });

        await allProcessed.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

        _mockCacheService.Verify(
            service => service.GetOrGenerateSubtitleAsync(itemId, zhPath, It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Once);
        _mockCacheService.Verify(
            service => service.GetOrGenerateSubtitleAsync(itemId, enPath, It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ProcessItem_ShouldContinueWithRemainingSubtitle_WhenOneSubtitleFails()
    {
        var itemId = Guid.NewGuid();
        string videoPath = Path.Join(_tempDataPath, "continue.mkv");
        string failingPath = Path.Join(_tempDataPath, "continue.ass");
        string remainingPath = Path.Join(_tempDataPath, "continue.en.ass");
        await File.WriteAllTextAsync(videoPath, "video", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(failingPath, "fail", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(remainingPath, "continue", TestContext.Current.CancellationToken);

        var remainingProcessed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _mockCacheService
            .Setup(service => service.GetOrGenerateSubtitleAsync(
                itemId, failingPath, It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("test failure"));
        _mockCacheService
            .Setup(service => service.GetOrGenerateSubtitleAsync(
                itemId, remainingPath, It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Callback(() => remainingProcessed.TrySetResult(true))
            .ReturnsAsync(new SubtitleResult(remainingPath, "text/x-ssa", true));

        await _tracker.StartAsync(TestContext.Current.CancellationToken);
        _mockLibraryManager.Raise(
            manager => manager.ItemAdded += null,
            this,
            new ItemChangeEventArgs { Item = new Video { Id = itemId, Path = videoPath } });

        await remainingProcessed.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

        _mockCacheService.Verify(
            service => service.GetOrGenerateSubtitleAsync(itemId, failingPath, It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Once);
        _mockCacheService.Verify(
            service => service.GetOrGenerateSubtitleAsync(itemId, remainingPath, It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public void ProcessItem_ShouldDoNothing_WhenAutoScanIsDisabled()
    {
        _config.EnableAutoScanProcessing = false;

        var video = new Video { Id = Guid.NewGuid(), Path = "dummy.mkv" };

        _mockLibraryManager.Raise(m => m.ItemAdded += null, this, new ItemChangeEventArgs { Item = video });

        Thread.Sleep(50);
        _mockCacheService.Verify(
            s => s.GetOrGenerateSubtitleAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
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
