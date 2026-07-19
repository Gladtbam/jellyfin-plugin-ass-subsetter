using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AssSubsetter.Configuration;
using Jellyfin.Plugin.AssSubsetter.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.AssSubsetter.Tests.Services;

[Collection("PluginInstance")]
public class PlaybackPrefetchServiceTests : IDisposable
{
    private readonly string _tempDataPath;
    private readonly Mock<ISessionManager> _mockSessionManager;
    private readonly Mock<ILibraryManager> _mockLibraryManager;
    private readonly Mock<SubtitleCacheService> _mockCacheService;
    private readonly PluginConfiguration _config;
    private readonly PlaybackPrefetchService _service;

    public PlaybackPrefetchServiceTests()
    {
        _tempDataPath = Path.Join(Path.GetTempPath(), "PrefetchTests_" + Path.GetRandomFileName());
        Directory.CreateDirectory(_tempDataPath);

        var mockPaths = new Mock<MediaBrowser.Common.Configuration.IApplicationPaths>();
        mockPaths.Setup(p => p.DataPath).Returns(_tempDataPath);
        mockPaths.Setup(p => p.PluginsPath).Returns(_tempDataPath);
        var mockConfigManager = new Mock<MediaBrowser.Controller.Configuration.IServerConfigurationManager>();
        mockConfigManager.Setup(c => c.Configuration).Returns(new MediaBrowser.Model.Configuration.ServerConfiguration());
        _ = new Plugin(mockPaths.Object, new Mock<MediaBrowser.Model.Serialization.IXmlSerializer>().Object, mockConfigManager.Object);

        _config = new PluginConfiguration
        {
            EnablePrefetchSubsetting = true,
            PrefetchTriggerPercent = 90
        };

        _mockSessionManager = new Mock<ISessionManager>();
        _mockLibraryManager = new Mock<ILibraryManager>();
        _mockCacheService = new Mock<SubtitleCacheService>(
            (Func<PluginConfiguration>)(() => _config), null!, null!, _tempDataPath, new NullLogger<SubtitleCacheService>());

        _service = new PlaybackPrefetchService(
            _mockSessionManager.Object,
            _mockLibraryManager.Object,
            _mockCacheService.Object,
            () => _config,
            new NullLogger<PlaybackPrefetchService>());
    }

    [Fact]
    public async Task StartAsync_ShouldSubscribeToPlaybackEvents()
    {
        // Act
        await _service.StartAsync(TestContext.Current.CancellationToken);

        // Assert: verify event handlers were attached by raising events without error
        _mockSessionManager.Raise(m => m.PlaybackProgress += null, this, CreateProgressEventArgs(new Video()));
        _mockSessionManager.Raise(m => m.PlaybackStopped += null, this, CreateStopEventArgs(new Video()));
    }

    [Fact]
    public async Task StopAsync_ShouldUnsubscribeFromPlaybackEvents()
    {
        // Arrange
        await _service.StartAsync(TestContext.Current.CancellationToken);

        // Act
        await _service.StopAsync(TestContext.Current.CancellationToken);

        // Assert: no exceptions thrown
    }

    [Fact]
    public async Task OnPlaybackProgress_ShouldDoNothing_WhenPrefetchIsDisabled()
    {
        // Arrange
        _config.EnablePrefetchSubsetting = false;
        await _service.StartAsync(TestContext.Current.CancellationToken);

        var episode = CreateEpisode(1, 1000);
        var args = CreateProgressEventArgs(episode, playbackPositionTicks: 950);

        // Act
        _mockSessionManager.Raise(m => m.PlaybackProgress += null, this, args);
        await Task.Delay(100, TestContext.Current.CancellationToken);

        // Assert
        _mockCacheService.Verify(
            s => s.GetOrGenerateSubtitleAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task OnPlaybackProgress_ShouldDoNothing_WhenItemIsNotEpisode()
    {
        // Arrange
        await _service.StartAsync(TestContext.Current.CancellationToken);

        var video = new Video { RunTimeTicks = 1000 };
        var args = CreateProgressEventArgs(video, playbackPositionTicks: 950);

        // Act
        _mockSessionManager.Raise(m => m.PlaybackProgress += null, this, args);
        await Task.Delay(100, TestContext.Current.CancellationToken);

        // Assert
        _mockCacheService.Verify(
            s => s.GetOrGenerateSubtitleAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task OnPlaybackProgress_ShouldDoNothing_WhenProgressBelowThreshold()
    {
        // Arrange
        _config.PrefetchTriggerPercent = 90;
        await _service.StartAsync(TestContext.Current.CancellationToken);

        var episode = CreateEpisode(1, 1000);
        // 50% progress - below 90% threshold
        var args = CreateProgressEventArgs(episode, playbackPositionTicks: 500);

        // Act
        _mockSessionManager.Raise(m => m.PlaybackProgress += null, this, args);
        await Task.Delay(100, TestContext.Current.CancellationToken);

        // Assert
        _mockCacheService.Verify(
            s => s.GetOrGenerateSubtitleAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task OnPlaybackProgress_ShouldDoNothing_WhenPositionTicksIsNull()
    {
        // Arrange
        await _service.StartAsync(TestContext.Current.CancellationToken);

        var episode = CreateEpisode(1, 1000);
        var args = CreateProgressEventArgs(episode, playbackPositionTicks: null);

        // Act
        _mockSessionManager.Raise(m => m.PlaybackProgress += null, this, args);
        await Task.Delay(100, TestContext.Current.CancellationToken);

        // Assert
        _mockCacheService.Verify(
            s => s.GetOrGenerateSubtitleAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task OnPlaybackProgress_ShouldDoNothing_WhenRuntimeTicksIsNull()
    {
        // Arrange
        await _service.StartAsync(TestContext.Current.CancellationToken);

        var episode = CreateEpisode(1, runtimeTicks: null);
        var args = CreateProgressEventArgs(episode, playbackPositionTicks: 950);

        // Act
        _mockSessionManager.Raise(m => m.PlaybackProgress += null, this, args);
        await Task.Delay(100, TestContext.Current.CancellationToken);

        // Assert
        _mockCacheService.Verify(
            s => s.GetOrGenerateSubtitleAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task OnPlaybackProgress_ShouldTriggerPrefetch_WhenProgressExceedsThreshold()
    {
        // Arrange
        _config.PrefetchTriggerPercent = 90;
        await _service.StartAsync(TestContext.Current.CancellationToken);

        var currentEpisode = CreateEpisode(indexNumber: 1, runtimeTicks: 1000);
        var nextEpisode = CreateEpisodeWithSubtitle(indexNumber: 2);

        SetupNextEpisodeQuery(currentEpisode, nextEpisode);

        // 95% progress - above 90% threshold
        var args = CreateProgressEventArgs(currentEpisode, playbackPositionTicks: 950);

        // Act
        _mockSessionManager.Raise(m => m.PlaybackProgress += null, this, args);
        await Task.Delay(500, TestContext.Current.CancellationToken);

        // Assert
        _mockCacheService.Verify(
            s => s.GetOrGenerateSubtitleAsync(nextEpisode.Id, It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Once());
    }

    [Fact]
    public async Task OnPlaybackProgress_ShouldPrefetchAllAssFiles_WhenMultipleExist()
    {
        // Arrange
        _config.PrefetchTriggerPercent = 90;
        await _service.StartAsync(TestContext.Current.CancellationToken);

        var currentEpisode = CreateEpisode(indexNumber: 1, runtimeTicks: 1000);
        var nextEpisode = CreateEpisodeWithMultipleSubtitles(indexNumber: 2, subtitleCount: 3);

        SetupNextEpisodeQuery(currentEpisode, nextEpisode);

        var args = CreateProgressEventArgs(currentEpisode, playbackPositionTicks: 950);

        // Act
        _mockSessionManager.Raise(m => m.PlaybackProgress += null, this, args);
        await Task.Delay(500, TestContext.Current.CancellationToken);

        // Assert: should be called 3 times (once for each ASS subtitle)
        _mockCacheService.Verify(
            s => s.GetOrGenerateSubtitleAsync(nextEpisode.Id, It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Exactly(3));
    }

    [Fact]
    public async Task OnPlaybackProgress_ShouldNotTriggerTwice_ForSameSessionAndItem()
    {
        // Arrange
        _config.PrefetchTriggerPercent = 90;
        await _service.StartAsync(TestContext.Current.CancellationToken);

        var currentEpisode = CreateEpisode(indexNumber: 1, runtimeTicks: 1000);
        var nextEpisode = CreateEpisodeWithSubtitle(indexNumber: 2);

        SetupNextEpisodeQuery(currentEpisode, nextEpisode);

        var session = new SessionInfo(null!, null!) { Id = "session-1" };
        var args1 = CreateProgressEventArgs(currentEpisode, playbackPositionTicks: 920, session: session);
        var args2 = CreateProgressEventArgs(currentEpisode, playbackPositionTicks: 950, session: session);

        // Act: fire progress twice for the same session+episode
        _mockSessionManager.Raise(m => m.PlaybackProgress += null, this, args1);
        await Task.Delay(500, TestContext.Current.CancellationToken);
        _mockSessionManager.Raise(m => m.PlaybackProgress += null, this, args2);
        await Task.Delay(500, TestContext.Current.CancellationToken);

        // Assert: should only be called once despite two progress events
        _mockCacheService.Verify(
            s => s.GetOrGenerateSubtitleAsync(nextEpisode.Id, It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Once());
    }

    [Fact]
    public async Task OnPlaybackStopped_ShouldClearTriggeredState_AllowingRetrigger()
    {
        // Arrange
        _config.PrefetchTriggerPercent = 90;
        await _service.StartAsync(TestContext.Current.CancellationToken);

        var currentEpisode = CreateEpisode(indexNumber: 1, runtimeTicks: 1000);
        var nextEpisode = CreateEpisodeWithSubtitle(indexNumber: 2);

        SetupNextEpisodeQuery(currentEpisode, nextEpisode);

        var session = new SessionInfo(null!, null!) { Id = "session-1" };

        // Act: first trigger
        var progressArgs = CreateProgressEventArgs(currentEpisode, playbackPositionTicks: 950, session: session);
        _mockSessionManager.Raise(m => m.PlaybackProgress += null, this, progressArgs);
        await Task.Delay(500, TestContext.Current.CancellationToken);

        // Stop playback (clears triggered state)
        var stopArgs = CreateStopEventArgs(currentEpisode, session: session);
        _mockSessionManager.Raise(m => m.PlaybackStopped += null, this, stopArgs);

        // Re-trigger
        _mockSessionManager.Raise(m => m.PlaybackProgress += null, this, progressArgs);
        await Task.Delay(500, TestContext.Current.CancellationToken);

        // Assert: should be called twice total
        _mockCacheService.Verify(
            s => s.GetOrGenerateSubtitleAsync(nextEpisode.Id, It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Exactly(2));
    }

    [Fact]
    public async Task OnPlaybackProgress_ShouldDoNothing_WhenNoNextEpisodeFound()
    {
        // Arrange
        _config.PrefetchTriggerPercent = 90;
        await _service.StartAsync(TestContext.Current.CancellationToken);

        var currentEpisode = CreateEpisode(indexNumber: 1, runtimeTicks: 1000);

        // Setup: no children returned
        _mockLibraryManager.Setup(m => m.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns([]);

        var args = CreateProgressEventArgs(currentEpisode, playbackPositionTicks: 950);

        // Act
        _mockSessionManager.Raise(m => m.PlaybackProgress += null, this, args);
        await Task.Delay(500, TestContext.Current.CancellationToken);

        // Assert
        _mockCacheService.Verify(
            s => s.GetOrGenerateSubtitleAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task OnPlaybackProgress_ShouldSkipSubsettedFiles()
    {
        // Arrange
        _config.PrefetchTriggerPercent = 90;
        await _service.StartAsync(TestContext.Current.CancellationToken);

        var currentEpisode = CreateEpisode(indexNumber: 1, runtimeTicks: 1000);

        // Create next episode with both normal and subsetted ASS files
        string nextVideoName = "EP02";
        string nextVideoPath = Path.Join(_tempDataPath, nextVideoName + ".mkv");
        File.WriteAllText(nextVideoPath, "video");
        File.WriteAllText(Path.Join(_tempDataPath, nextVideoName + ".ass"), "normal sub");
        File.WriteAllText(Path.Join(_tempDataPath, nextVideoName + ".subsetted.ass"), "subsetted sub");

        var nextEpisode = new Episode
        {
            Id = Guid.NewGuid(),
            IndexNumber = 2,
            Path = nextVideoPath,
            RunTimeTicks = 1000
        };

        SetupNextEpisodeQuery(currentEpisode, nextEpisode);

        var args = CreateProgressEventArgs(currentEpisode, playbackPositionTicks: 950);

        // Act
        _mockSessionManager.Raise(m => m.PlaybackProgress += null, this, args);
        await Task.Delay(500, TestContext.Current.CancellationToken);

        // Assert: should only be called once (normal sub only, not subsetted)
        _mockCacheService.Verify(
            s => s.GetOrGenerateSubtitleAsync(nextEpisode.Id, It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Once());
    }

    // --- Helper Methods ---

    private Episode CreateEpisode(int? indexNumber, long? runtimeTicks = 1000)
    {
        var episode = new Episode
        {
            Id = Guid.NewGuid(),
            IndexNumber = indexNumber,
            RunTimeTicks = runtimeTicks,
            Path = Path.Join(_tempDataPath, $"EP{indexNumber:D2}.mkv"),
            ParentId = Guid.NewGuid()
        };
        return episode;
    }

    private Episode CreateEpisodeWithSubtitle(int indexNumber)
    {
        string videoName = $"EP{indexNumber:D2}";
        string videoPath = Path.Join(_tempDataPath, videoName + ".mkv");
        string assPath = Path.Join(_tempDataPath, videoName + ".ass");
        File.WriteAllText(videoPath, "video data");
        File.WriteAllText(assPath, "ass data");

        return new Episode
        {
            Id = Guid.NewGuid(),
            IndexNumber = indexNumber,
            Path = videoPath,
            RunTimeTicks = 1000
        };
    }

    private Episode CreateEpisodeWithMultipleSubtitles(int indexNumber, int subtitleCount)
    {
        string videoName = $"EP{indexNumber:D2}";
        string videoPath = Path.Join(_tempDataPath, videoName + ".mkv");
        File.WriteAllText(videoPath, "video data");

        string[] langs = ["chs", "cht", "jpn", "eng", "kor"];
        for (int i = 0; i < subtitleCount && i < langs.Length; i++)
        {
            File.WriteAllText(Path.Join(_tempDataPath, $"{videoName}.{langs[i]}.ass"), "ass data");
        }

        return new Episode
        {
            Id = Guid.NewGuid(),
            IndexNumber = indexNumber,
            Path = videoPath,
            RunTimeTicks = 1000
        };
    }

    private void SetupNextEpisodeQuery(Episode currentEpisode, Episode nextEpisode)
    {
        _mockLibraryManager.Setup(m => m.GetItemList(It.Is<InternalItemsQuery>(q => q.ParentId == currentEpisode.ParentId)))
            .Returns([currentEpisode, nextEpisode]);
    }

    private static PlaybackProgressEventArgs CreateProgressEventArgs(
        BaseItem item,
        long? playbackPositionTicks = null,
        SessionInfo? session = null)
    {
        return new PlaybackProgressEventArgs
        {
            Item = item,
            PlaybackPositionTicks = playbackPositionTicks,
            Session = session
        };
    }

    private static PlaybackStopEventArgs CreateStopEventArgs(
        BaseItem item,
        SessionInfo? session = null)
    {
        return new PlaybackStopEventArgs
        {
            Item = item,
            Session = session
        };
    }

    public void Dispose()
    {
        _service.Dispose();
        if (Directory.Exists(_tempDataPath))
        {
            try
            {
                Directory.Delete(_tempDataPath, true);
            }
            catch (IOException)
            {
                /* Ignore */
            }
            catch (UnauthorizedAccessException)
            {
                /* Ignore */
            }
        }
    }
}
