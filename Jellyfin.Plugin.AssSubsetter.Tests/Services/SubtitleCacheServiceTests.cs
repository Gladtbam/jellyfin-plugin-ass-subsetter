using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Jellyfin.Plugin.AssSubsetter.Configuration;
using Jellyfin.Plugin.AssSubsetter.Core;
using Jellyfin.Plugin.AssSubsetter.Helpers;
using Jellyfin.Plugin.AssSubsetter.Models;
using Jellyfin.Plugin.AssSubsetter.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.AssSubsetter.Tests.Services;

[Collection("PluginInstance")]
public sealed class SubtitleCacheServiceTests : IDisposable
{
    private readonly string _tempDataPath;
    private readonly string _customCachePath;
    private readonly PluginConfiguration _config;
    private readonly FontCacheManagerFixture _fontCache;
    private readonly AssProcessor _assProcessor;
    private readonly SubtitleCacheService _cacheService;

    public SubtitleCacheServiceTests()
    {
        _tempDataPath = Path.Join(Path.GetTempPath(), "SubtitleCacheTests_" + Path.GetRandomFileName());
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
            MaxCacheSizeMB = 1
        };

        _fontCache = new FontCacheManagerFixture();
        var assParser = new AssDocumentParser();
        _assProcessor = new AssProcessor(() => _config, _fontCache.Manager, assParser, new NullLogger<AssProcessor>());

        _cacheService = new SubtitleCacheService(() => _config, _assProcessor, null, _customCachePath, new NullLogger<SubtitleCacheService>());
    }

    [Fact]
    public async Task GetOrGenerateSubtitleAsync_ShouldReturnCachedFile_WhenCacheHit()
    {
        // Arrange
        Guid itemId = Guid.NewGuid();
        string originalAssPath = Path.Join(_tempDataPath, "original.ass");
        await File.WriteAllTextAsync(originalAssPath, "mock source data", TestContext.Current.CancellationToken);

        string fingerprint = SubtitleCacheFingerprint.Create(originalAssPath, SubtitleProcessingMode.Subsetting);
        string expectedCachePath = Path.Join(_customCachePath, $"{itemId:N}_{fingerprint}.ass");

        await File.WriteAllTextAsync(expectedCachePath, "mock cache data", TestContext.Current.CancellationToken);

        // Act
        var result = await _cacheService.GetOrGenerateSubtitleAsync(itemId, originalAssPath, 0, 0, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(expectedCachePath, result.Path);
        Assert.Equal("text/x-ssa", result.ContentType);
        Assert.True(result.IsReady);
    }

    [Fact]
    public async Task GetOrGenerateSubtitleAsync_ShouldNotReuseCache_WhenSourceChanges()
    {
        Guid itemId = Guid.NewGuid();
        string originalAssPath = Path.Join(_tempDataPath, "changing.ass");
        await File.WriteAllTextAsync(originalAssPath, "first source version", TestContext.Current.CancellationToken);
        string oldFingerprint = SubtitleCacheFingerprint.Create(originalAssPath, SubtitleProcessingMode.Subsetting);
        string oldCachePath = Path.Join(_customCachePath, $"{itemId:N}_{oldFingerprint}.ass");
        await File.WriteAllTextAsync(oldCachePath, "old cache data", TestContext.Current.CancellationToken);

        await File.AppendAllTextAsync(originalAssPath, " with changed metadata", TestContext.Current.CancellationToken);
        var result = await _cacheService.GetOrGenerateSubtitleAsync(
            itemId,
            originalAssPath,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotEqual(oldCachePath, result.Path);
    }

    [Fact]
    public async Task GetOrGenerateSubtitleAsync_ShouldRunLRUEviction_WhenQuotaExceeded()
    {
        // Arrange
        string cacheFileDir = _cacheService.CacheFolderPath;
        _config.MaxCacheSizeMB = 1;

        var oldId1 = Guid.NewGuid();
        var oldId2 = Guid.NewGuid();

        string oldFilePath1 = Path.Join(cacheFileDir, $"{oldId1:N}_old1.ass");
        string oldFilePath2 = Path.Join(cacheFileDir, $"{oldId2:N}_old2.ass");

        byte[] bigBuffer = new byte[600 * 1024]; // 600KB
        await File.WriteAllBytesAsync(oldFilePath1, bigBuffer, TestContext.Current.CancellationToken);

        File.SetLastAccessTime(oldFilePath1, DateTime.Now.AddMinutes(-5));

        await File.WriteAllBytesAsync(oldFilePath2, bigBuffer, TestContext.Current.CancellationToken);
        File.SetLastAccessTime(oldFilePath2, DateTime.Now.AddMinutes(-1));


        var newId = Guid.NewGuid();
        string originalPath = Path.Join(_tempDataPath, "new_original.ass");
        await File.WriteAllTextAsync(originalPath, "short subtitle lines", TestContext.Current.CancellationToken);

        // Act
        await _cacheService.GetOrGenerateSubtitleAsync(newId, originalPath, 0, 0, TestContext.Current.CancellationToken);

        // Assert
        Assert.False(File.Exists(oldFilePath1), "The oldest file should be evicted successfully.");
        Assert.True(File.Exists(oldFilePath2), "The newer file must be preserved.");
    }

    [Fact]
    public async Task GetOrGenerateSubtitleAsync_ShouldGenerateAndCacheMks()
    {
        _config.SubtitleMode = SubtitleProcessingMode.GenerateMks;
        string originalAssPath = Path.Join(_tempDataPath, "mks-source.ass");
        await File.WriteAllTextAsync(originalAssPath, "Dialogue: 0,0:00:00.00,0:00:01.00,Default,,0,0,0,,Text", TestContext.Current.CancellationToken);
        int invocationCount = 0;
        var muxer = new MksMuxer(
            NullLogger<MksMuxer>.Instance,
            () => "ffmpeg",
            async (startInfo, token) =>
            {
                invocationCount++;
                await File.WriteAllTextAsync(startInfo.ArgumentList[^1], "mks-data", token);
                return 0;
            });
        using var service = new SubtitleCacheService(() => _config, _assProcessor, null, muxer, _customCachePath, NullLogger<SubtitleCacheService>.Instance);
        Guid itemId = Guid.NewGuid();

        var first = await service.GetOrGenerateSubtitleAsync(itemId, originalAssPath, cancellationToken: TestContext.Current.CancellationToken);
        var second = await service.GetOrGenerateSubtitleAsync(itemId, originalAssPath, cancellationToken: TestContext.Current.CancellationToken);

        Assert.EndsWith(".mks", first.Path, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("video/x-matroska", first.ContentType);
        Assert.True(first.IsReady);
        Assert.Equal(first.Path, second.Path);
        Assert.Equal(1, invocationCount);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task GetOrGenerateSubtitleAsync_ShouldApplyConfiguredFallback_WhenMksFails(bool fallbackEnabled, bool expectsOriginal)
    {
        _config.SubtitleMode = SubtitleProcessingMode.GenerateMks;
        _config.FallbackToOriginalOnError = fallbackEnabled;
        string originalAssPath = Path.Join(_tempDataPath, "failed-mks.ass");
        await File.WriteAllTextAsync(originalAssPath, "Dialogue: 0,0:00:00.00,0:00:01.00,Default,,0,0,0,,Text", TestContext.Current.CancellationToken);
        var muxer = new MksMuxer(NullLogger<MksMuxer>.Instance, () => "ffmpeg", (_, _) => Task.FromResult(1));
        using var service = new SubtitleCacheService(() => _config, _assProcessor, null, muxer, _customCachePath, NullLogger<SubtitleCacheService>.Instance);

        var result = await service.GetOrGenerateSubtitleAsync(Guid.NewGuid(), originalAssPath, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(expectsOriginal ? originalAssPath : string.Empty, result.Path);
        Assert.Equal("text/x-ssa", result.ContentType);
        Assert.False(result.IsReady);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task GetOrGenerateSubtitleAsync_ShouldApplyConfiguredFallback_WhenMksIsCancelled(bool fallbackEnabled, bool expectsOriginal)
    {
        _config.SubtitleMode = SubtitleProcessingMode.GenerateMks;
        _config.FallbackToOriginalOnError = fallbackEnabled;
        string originalAssPath = Path.Join(_tempDataPath, "cancelled-mks.ass");
        await File.WriteAllTextAsync(originalAssPath, "plain", TestContext.Current.CancellationToken);
        using var service = new SubtitleCacheService(() => _config, _assProcessor, null, null, _customCachePath, NullLogger<SubtitleCacheService>.Instance);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var result = await service.GetOrGenerateSubtitleAsync(Guid.NewGuid(), originalAssPath, cancellationToken: cts.Token);

        Assert.Equal(expectsOriginal ? originalAssPath : string.Empty, result.Path);
        Assert.False(result.IsReady);
    }

    [Fact]
    public async Task GetOrGenerateSubtitleAsync_ShouldEvictOldMks_WhenQuotaExceeded()
    {
        _config.MaxCacheSizeMB = 1;
        string oldMks = Path.Join(_customCachePath, "old.mks");
        string newerAss = Path.Join(_customCachePath, "newer.ass");
        await File.WriteAllBytesAsync(oldMks, new byte[600 * 1024], TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(newerAss, new byte[600 * 1024], TestContext.Current.CancellationToken);
        File.SetLastAccessTime(oldMks, DateTime.Now.AddMinutes(-5));
        File.SetLastAccessTime(newerAss, DateTime.Now.AddMinutes(-1));

        string source = Path.Join(_tempDataPath, "quota.ass");
        await File.WriteAllTextAsync(source, "plain", TestContext.Current.CancellationToken);
        await _cacheService.GetOrGenerateSubtitleAsync(Guid.NewGuid(), source, cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(File.Exists(oldMks));
        Assert.True(File.Exists(newerAss));
    }

    [Theory]
    [InlineData(".ass")]
    [InlineData(".sup")]
    [InlineData(".MKS")]
    public async Task QuotaEvictionPreservesUnmanagedFilesAndNewerEntries(string extension)
    {
        string oldest = Path.Join(_customCachePath, "oldest" + extension);
        string newest = Path.Join(_customCachePath, "newest.ass");
        string unmanaged = Path.Join(_customCachePath, "keep.tmp");
        await File.WriteAllBytesAsync(oldest, new byte[600 * 1024], TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(newest, new byte[600 * 1024], TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(unmanaged, new byte[2 * 1024 * 1024], TestContext.Current.CancellationToken);
        File.SetLastAccessTimeUtc(oldest, DateTime.UtcNow.AddHours(-2));
        File.SetLastAccessTimeUtc(newest, DateTime.UtcNow.AddHours(-1));
        File.SetLastAccessTimeUtc(unmanaged, DateTime.UtcNow.AddDays(-1));

        string source = Path.Join(_tempDataPath, "eviction.ass");
        await File.WriteAllTextAsync(source, "plain", TestContext.Current.CancellationToken);
        await _cacheService.GetOrGenerateSubtitleAsync(Guid.NewGuid(), source, cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(File.Exists(oldest));
        Assert.True(File.Exists(newest));
        Assert.True(File.Exists(unmanaged));
    }

    [Fact]
    public async Task RestartedCacheServiceSharesOneQuotaScanAcrossConcurrentMisses()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        string source = Path.Join(_tempDataPath, "cache-restart.ass");
        await File.WriteAllTextAsync(source, "plain", token);
        Guid originalId = Guid.NewGuid();
        var existing = await _cacheService.GetOrGenerateSubtitleAsync(originalId, source, cancellationToken: token);
        _cacheService.Dispose();
        var logger = new QuotaScanLogger();
        using var restarted = new SubtitleCacheService(() => _config, _assProcessor, null, _customCachePath, logger);

        var hit = await restarted.GetOrGenerateSubtitleAsync(originalId, source, cancellationToken: token);
        Assert.True(hit.IsReady);
        Assert.Equal(existing.Path, hit.Path);
        Assert.Equal(0, logger.DirectoryScans);

        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ =>
            restarted.GetOrGenerateSubtitleAsync(Guid.NewGuid(), source, cancellationToken: token)));
        Assert.All(results, result => Assert.True(result.IsReady));
        Assert.Equal(1, logger.DirectoryScans);
    }

    [Fact]
    public async Task QuotaEvictionUsesPublishedSizeAndKeepsUnfinishedOutputs()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        string oldest = Path.Join(_customCachePath, "older.ass");
        string newer = Path.Join(_customCachePath, "newer.sup");
        string unfinished = Path.Join(_customCachePath, $"cache.{Guid.NewGuid():N}.partial.mks");
        await File.WriteAllBytesAsync(oldest, new byte[250 * 1024], token);
        await File.WriteAllBytesAsync(newer, new byte[300 * 1024], token);
        await File.WriteAllBytesAsync(unfinished, new byte[800 * 1024], token);
        File.SetLastAccessTimeUtc(unfinished, DateTime.UtcNow.AddHours(-3));
        File.SetLastAccessTimeUtc(oldest, DateTime.UtcNow.AddHours(-2));
        File.SetLastAccessTimeUtc(newer, DateTime.UtcNow.AddHours(-1));
        string source = Path.Join(_tempDataPath, "large-cache-output.ass");
        await File.WriteAllTextAsync(source, "plain", token);
        Func<PluginConfiguration> config = () => _config;
        var processor = new Mock<AssProcessor>(config, _fontCache.Manager, new AssDocumentParser(), NullLogger<AssProcessor>.Instance);
        processor.Setup(value => value.GenerateSubsetArtifactAsync(source, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SubsetArtifact(new string('a', 600 * 1024), []));
        using var service = new SubtitleCacheService(config, processor.Object, null, _customCachePath, NullLogger<SubtitleCacheService>.Instance);

        var generated = await service.GetOrGenerateSubtitleAsync(Guid.NewGuid(), source, cancellationToken: token);

        Assert.True(generated.IsReady);
        Assert.Equal(600 * 1024, new FileInfo(generated.Path).Length);
        Assert.False(File.Exists(oldest));
        Assert.True(File.Exists(newer));
        Assert.True(File.Exists(unfinished));
    }

    [Fact]
    public async Task QuotaSerializesConcurrentPublicationUsingCompletedFileSizes()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        _config.SubtitleMode = SubtitleProcessingMode.GenerateMks;
        string source = Path.Join(_tempDataPath, "concurrent-mux.ass");
        await File.WriteAllTextAsync(source, "plain", token);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int stagedOutputs = 0;
        var muxer = new MksMuxer(NullLogger<MksMuxer>.Instance, () => "ffmpeg", async (startInfo, ct) =>
        {
            await File.WriteAllBytesAsync(startInfo.ArgumentList[^1], new byte[600 * 1024], ct);
            if (Interlocked.Increment(ref stagedOutputs) == 2)
            {
                ready.TrySetResult();
            }

            await finish.Task.WaitAsync(ct);
            return 0;
        });
        using var service = new SubtitleCacheService(() => _config, _assProcessor, null, muxer, _customCachePath, NullLogger<SubtitleCacheService>.Instance);
        var first = service.GetOrGenerateSubtitleAsync(Guid.NewGuid(), source, cancellationToken: token);
        var second = service.GetOrGenerateSubtitleAsync(Guid.NewGuid(), source, cancellationToken: token);
        try
        {
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(10), token);
        }
        finally
        {
            finish.TrySetResult();
        }

        await Task.WhenAll(first, second);
        FileInfo published = Assert.Single(new DirectoryInfo(_customCachePath).GetFiles("*.mks"));
        Assert.DoesNotContain(".partial.", published.Name, StringComparison.Ordinal);
        Assert.Equal(600 * 1024, published.Length);
    }

    [Fact]
    public async Task QuotaRefreshCountsExternalWritesAndUsesLatestAccessTimes()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        string touched = Path.Join(_customCachePath, "touched.ass");
        await File.WriteAllBytesAsync(touched, new byte[400 * 1024], token);
        DateTime now = DateTime.UtcNow;
        File.SetLastAccessTimeUtc(touched, now.AddHours(-3));
        var clock = new QuotaTimeProvider();
        var logger = new QuotaScanLogger();
        var quota = new SubtitleCacheQuota(_customCachePath, logger, clock);
        const long Limit = 1024 * 1024;
        quota.EnsureCapacity(0, Limit);

        string external = Path.Join(_customCachePath, "added-externally.sup");
        await File.WriteAllBytesAsync(external, new byte[700 * 1024], token);
        File.SetLastAccessTimeUtc(external, now.AddHours(-2));
        File.SetLastAccessTimeUtc(touched, now);
        clock.Timestamp = TimeSpan.FromMinutes(1).Ticks;

        quota.EnsureCapacity(0, Limit);

        Assert.Equal(2, logger.DirectoryScans);
        Assert.True(File.Exists(touched));
        Assert.False(File.Exists(external));
    }

    private sealed class QuotaTimeProvider : TimeProvider
    {
        internal long Timestamp { get; set; }

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => Timestamp;
    }

    private sealed class QuotaScanLogger : ILogger<SubtitleCacheService>
    {
        private int _directoryScans;

        internal int DirectoryScans => Volatile.Read(ref _directoryScans);

        public bool IsEnabled(LogLevel logLevel) => true;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (formatter(state, exception).StartsWith("[AssSubsetter] Scanned subtitle cache directory", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _directoryScans);
            }
        }
    }

    public void Dispose()
    {
        _cacheService.Dispose();
        _fontCache.Dispose();
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
