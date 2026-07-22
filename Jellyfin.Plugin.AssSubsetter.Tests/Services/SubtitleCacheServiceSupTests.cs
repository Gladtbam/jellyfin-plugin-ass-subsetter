using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AssSubsetter.Configuration;
using Jellyfin.Plugin.AssSubsetter.Core;
using Jellyfin.Plugin.AssSubsetter.Helpers;
using Jellyfin.Plugin.AssSubsetter.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Moq.Protected;
using Xunit;

namespace Jellyfin.Plugin.AssSubsetter.Tests.Services;

[Collection("PluginInstance")]
public class SubtitleCacheServiceSupTests : IDisposable
{
    private readonly string _tempDataPath;
    private readonly string _customCachePath;
    private readonly PluginConfiguration _config;
    private readonly SubtitleCacheService _cacheService;

    public SubtitleCacheServiceSupTests()
    {
        _tempDataPath = Path.Join(Path.GetTempPath(), "SubtitleCacheSupTests_" + Path.GetRandomFileName());
        _customCachePath = Path.Join(_tempDataPath, "Cache");
        Directory.CreateDirectory(_tempDataPath);
        Directory.CreateDirectory(_customCachePath);

        var mockPaths = new Mock<MediaBrowser.Common.Configuration.IApplicationPaths>();
        mockPaths.Setup(p => p.DataPath).Returns(_tempDataPath);
        mockPaths.Setup(p => p.PluginsPath).Returns(_tempDataPath);
        var mockConfigManager = new Mock<MediaBrowser.Controller.Configuration.IServerConfigurationManager>();
        mockConfigManager.Setup(c => c.Configuration).Returns(new MediaBrowser.Model.Configuration.ServerConfiguration());
        _ = new Plugin(mockPaths.Object, new Mock<MediaBrowser.Model.Serialization.IXmlSerializer>().Object, mockConfigManager.Object);

        _config = new PluginConfiguration
        {
            SubtitleMode = SubtitleProcessingMode.ConvertToSup,
            FontCacheFilePath = Path.Join(_tempDataPath, "font_caches.json"),
            MaxCacheSizeMB = 100,
            FallbackToOriginalOnError = true,
        };

        var fontCacheManager = new FontCacheManager(NullLogger<FontCacheManager>.Instance, () => _config);
        var assParser = new AssDocumentParser();
        var assProcessor = new AssProcessor(() => _config, fontCacheManager, assParser, new NullLogger<AssProcessor>());

        // Use null converter — tests that need converter will use Mock<SubtitleCacheService>
        _cacheService = new SubtitleCacheService(
            () => _config,
            assProcessor,
            null,
            _customCachePath,
            new NullLogger<SubtitleCacheService>());
    }

    [Fact]
    public async Task GetOrGenerateSubtitleAsync_ShouldReturnSupPath_WhenCacheHit()
    {
        // Arrange
        var itemId = Guid.NewGuid();
        string originalAssPath = Path.Join(_tempDataPath, "test_subtitle.ass");
        await File.WriteAllTextAsync(originalAssPath, "mock source data", TestContext.Current.CancellationToken);

        // Pre-create the expected SUP cache file
        string fingerprint = SubtitleCacheFingerprint.Create(
            originalAssPath,
            SubtitleProcessingMode.ConvertToSup,
            width: 1920,
            height: 1080,
            frameRate: _config.AssToSupFrameRate);
        string expectedSupPath = Path.Join(_customCachePath, $"{itemId:N}_{fingerprint}.sup");
        await File.WriteAllTextAsync(expectedSupPath, "mock sup data", TestContext.Current.CancellationToken);

        // Act
        var result = await _cacheService.GetOrGenerateSubtitleAsync(
            itemId, originalAssPath, 1920, 1080, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(expectedSupPath, result.Path);
        Assert.True(result.IsReady, "Should indicate the returned path is ready.");
        Assert.Equal("application/octet-stream", result.ContentType);
    }

    [Fact]
    public async Task GetOrGenerateSubtitleAsync_ShouldNotReuseSupCache_WhenRenderingParametersChange()
    {
        var itemId = Guid.NewGuid();
        string originalAssPath = Path.Join(_tempDataPath, "rendering_parameters.ass");
        await File.WriteAllTextAsync(originalAssPath, "mock source data", TestContext.Current.CancellationToken);
        string fingerprint = SubtitleCacheFingerprint.Create(
            originalAssPath,
            SubtitleProcessingMode.ConvertToSup,
            width: 1920,
            height: 1080,
            frameRate: 24);
        string cachedSupPath = Path.Join(_customCachePath, $"{itemId:N}_{fingerprint}.sup");
        await File.WriteAllTextAsync(cachedSupPath, "mock sup data", TestContext.Current.CancellationToken);

        var widthChanged = await _cacheService.GetOrGenerateSubtitleAsync(
            itemId,
            originalAssPath,
            1280,
            1080,
            TestContext.Current.CancellationToken);

        _config.AssToSupFrameRate = 30;
        var frameRateChanged = await _cacheService.GetOrGenerateSubtitleAsync(
            itemId,
            originalAssPath,
            1920,
            1080,
            TestContext.Current.CancellationToken);

        Assert.Equal(originalAssPath, widthChanged.Path);
        Assert.False(widthChanged.IsReady);
        Assert.Equal(originalAssPath, frameRateChanged.Path);
        Assert.False(frameRateChanged.IsReady);
    }

    [Fact]
    public async Task GetOrGenerateSubtitleAsync_ShouldReturnAssPathAsFallback_WhenCacheMiss()
    {
        // Arrange — no SUP file exists
        var itemId = Guid.NewGuid();
        string originalAssPath = Path.Join(_tempDataPath, "no_cache.ass");

        // Act
        var result = await _cacheService.GetOrGenerateSubtitleAsync(
            itemId, originalAssPath, 1920, 1080, TestContext.Current.CancellationToken);

        // Assert — should fall back to original ASS
        Assert.Equal(originalAssPath, result.Path);
        Assert.False(result.IsReady, "Should indicate the returned path is NOT ready.");
        Assert.Equal("text/x-ssa", result.ContentType);
    }

    [Fact]
    public async Task GetOrGenerateSubtitleAsync_ShouldNotThrow_WhenConverterIsNull()
    {
        // Arrange
        var itemId = Guid.NewGuid();
        string originalAssPath = Path.Join(_tempDataPath, "null_converter.ass");

        // Act & Assert — should not throw, returns fallback
        var result = await _cacheService.GetOrGenerateSubtitleAsync(
            itemId, originalAssPath, 1920, 1080, TestContext.Current.CancellationToken);

        Assert.Equal(originalAssPath, result.Path);
        Assert.False(result.IsReady);
    }

    [Fact]
    public async Task GetOrGenerateSubtitleAsync_ShouldDedup_WhenCalledTwiceForSameItem()
    {
        // Arrange — Use a mock so we can track calls to GetOrGenerateSupAsync
        var mockCacheService = new Mock<SubtitleCacheService>(
            (Func<PluginConfiguration>)(() => _config), null!, null!, _customCachePath, new NullLogger<SubtitleCacheService>());

        // Make GetOrGenerateSupAsync block to simulate long-running conversion
        mockCacheService.Protected()
            .Setup<Task<string>>("GetOrGenerateSupAsync",
                ItExpr.IsAny<Guid>(), ItExpr.IsAny<string>(), ItExpr.IsAny<int>(), ItExpr.IsAny<int>(), ItExpr.IsAny<CancellationToken>())
            .Returns(async (Guid id, string path, int w, int h, CancellationToken ct) =>
            {
                await Task.Delay(3000, ct);
                return string.Empty;
            });

        mockCacheService.CallBase = true;

        var itemId = Guid.NewGuid();
        string originalAssPath = Path.Join(_tempDataPath, "dedup_test.ass");

        using var cts = new CancellationTokenSource();

        // Act — call GetOrGenerateSubtitleAsync twice rapidly (both should trigger background but dedup)
        _ = mockCacheService.Object.GetOrGenerateSubtitleAsync(itemId, originalAssPath, 1920, 1080, cts.Token);
        _ = mockCacheService.Object.GetOrGenerateSubtitleAsync(itemId, originalAssPath, 1920, 1080, cts.Token);

        // Wait briefly for background task to start
        await Task.Delay(300, TestContext.Current.CancellationToken);

        // Cancel to clean up
        cts.Cancel();

        // Assert — GetOrGenerateSupAsync should be called at most once (second call deduped)
        mockCacheService.Protected().Verify(
            "GetOrGenerateSupAsync",
            Times.AtMostOnce(),
            ItExpr.IsAny<Guid>(), ItExpr.IsAny<string>(), ItExpr.IsAny<int>(), ItExpr.IsAny<int>(), ItExpr.IsAny<CancellationToken>());
    }

    [Fact]
    public async Task GetOrGenerateSubtitleAsync_ShouldAllowRetrigger_AfterCompletion()
    {
        // Arrange
        var mockCacheService = new Mock<SubtitleCacheService>(
            (Func<PluginConfiguration>)(() => _config), null!, null!, _customCachePath, new NullLogger<SubtitleCacheService>());

        var callCount = 0;
        mockCacheService.Protected()
            .Setup<Task<string>>("GetOrGenerateSupAsync",
                ItExpr.IsAny<Guid>(), ItExpr.IsAny<string>(), ItExpr.IsAny<int>(), ItExpr.IsAny<int>(), ItExpr.IsAny<CancellationToken>())
            .Returns(async (Guid id, string path, int w, int h, CancellationToken ct) =>
            {
                Interlocked.Increment(ref callCount);
                await Task.Yield();
                return string.Empty;
            });

        mockCacheService.CallBase = true;

        var itemId = Guid.NewGuid();
        string originalAssPath = Path.Join(_tempDataPath, "retrigger_test.ass");

        using var cts = new CancellationTokenSource();

        // Act — trigger, wait for completion, trigger again
        _ = mockCacheService.Object.GetOrGenerateSubtitleAsync(itemId, originalAssPath, 1920, 1080, cts.Token);
        await Task.Delay(500, TestContext.Current.CancellationToken);

        _ = mockCacheService.Object.GetOrGenerateSubtitleAsync(itemId, originalAssPath, 1920, 1080, cts.Token);
        await Task.Delay(500, TestContext.Current.CancellationToken);

        // Assert — should have been called twice (retrigger allowed after first completes)
        Assert.True(callCount >= 2, $"Expected at least 2 calls, got {callCount}.");
    }

    [Fact]
    public async Task StopAsync_ShouldCancelAndAwaitBackgroundSupConversion()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken observedToken = default;
        var mockCacheService = new Mock<SubtitleCacheService>(
            (Func<PluginConfiguration>)(() => _config), null!, null!, _customCachePath, new NullLogger<SubtitleCacheService>())
        {
            CallBase = true
        };
        mockCacheService.Protected()
            .Setup<Task<string>>(
                "GetOrGenerateSupAsync",
                ItExpr.IsAny<Guid>(),
                ItExpr.IsAny<string>(),
                ItExpr.IsAny<int>(),
                ItExpr.IsAny<int>(),
                ItExpr.IsAny<CancellationToken>())
            .Returns(async (Guid id, string path, int width, int height, CancellationToken cancellationToken) =>
            {
                observedToken = cancellationToken;
                started.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return string.Empty;
            });
        string source = Path.Join(_tempDataPath, "lifecycle.ass");
        await File.WriteAllTextAsync(source, "source", TestContext.Current.CancellationToken);

        await mockCacheService.Object.StartAsync(TestContext.Current.CancellationToken);
        await mockCacheService.Object.GetOrGenerateSubtitleAsync(
            Guid.NewGuid(),
            source,
            1920,
            1080,
            TestContext.Current.CancellationToken);
        await started.Task.WaitAsync(TestContext.Current.CancellationToken);

        await mockCacheService.Object.StopAsync(TestContext.Current.CancellationToken);

        Assert.True(observedToken.IsCancellationRequested);
        Assert.Equal(0, mockCacheService.Object.ActiveBackgroundConversionCount);
    }

    [Fact]
    public async Task GetOrGenerateSubtitleAsync_ShouldConvertDifferentVersionsOfSameItemConcurrently()
    {
        var bothStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int startedCount = 0;
        var mockCacheService = new Mock<SubtitleCacheService>(
            (Func<PluginConfiguration>)(() => _config), null!, null!, _customCachePath, new NullLogger<SubtitleCacheService>())
        {
            CallBase = true
        };
        mockCacheService.Protected()
            .Setup<Task<string>>(
                "GetOrGenerateSupAsync",
                ItExpr.IsAny<Guid>(),
                ItExpr.IsAny<string>(),
                ItExpr.IsAny<int>(),
                ItExpr.IsAny<int>(),
                ItExpr.IsAny<CancellationToken>())
            .Returns(async (Guid id, string path, int width, int height, CancellationToken cancellationToken) =>
            {
                if (Interlocked.Increment(ref startedCount) == 2)
                {
                    bothStarted.TrySetResult();
                }

                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return string.Empty;
            });
        Guid itemId = Guid.NewGuid();
        string firstSource = Path.Join(_tempDataPath, "first-version.ass");
        string secondSource = Path.Join(_tempDataPath, "second-version.ass");
        await File.WriteAllTextAsync(firstSource, "first", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(secondSource, "second", TestContext.Current.CancellationToken);

        await mockCacheService.Object.StartAsync(TestContext.Current.CancellationToken);
        await mockCacheService.Object.GetOrGenerateSubtitleAsync(itemId, firstSource, 1920, 1080, TestContext.Current.CancellationToken);
        await mockCacheService.Object.GetOrGenerateSubtitleAsync(itemId, secondSource, 1920, 1080, TestContext.Current.CancellationToken);
        await bothStarted.Task.WaitAsync(TestContext.Current.CancellationToken);

        await mockCacheService.Object.StopAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, startedCount);
        Assert.Equal(0, mockCacheService.Object.ActiveBackgroundConversionCount);
    }

    public void Dispose()
    {
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
