using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Jellyfin.Plugin.AssSubsetter.Configuration;
using Jellyfin.Plugin.AssSubsetter.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.AssSubsetter.Tests.Services;

public class SubtitleCacheServiceTests : IDisposable
{
    private readonly string _tempDataPath;
    private readonly string _customCachePath;
    private readonly PluginConfiguration _config;
    private readonly Mock<ToolManager> _mockToolManager;
    private readonly AssProcessor _assProcessor;
    private readonly SubtitleCacheService _cacheService;

    public SubtitleCacheServiceTests()
    {
        _tempDataPath = Path.Combine(Path.GetTempPath(), "SubtitleCacheTests_" + Path.GetRandomFileName());
        _customCachePath = Path.Combine(_tempDataPath, "Cache");
        Directory.CreateDirectory(_tempDataPath);
        Directory.CreateDirectory(_customCachePath);

        var mockPaths = new Mock<MediaBrowser.Common.Configuration.IApplicationPaths>();
        mockPaths.Setup(p => p.DataPath).Returns(_tempDataPath);
        mockPaths.Setup(p => p.PluginsPath).Returns(_tempDataPath);
        _ = new Plugin(mockPaths.Object, new Mock<MediaBrowser.Model.Serialization.IXmlSerializer>().Object);

        _config = new PluginConfiguration
        {
            MaxCacheSizeMB = 1
        };

        _mockToolManager = new Mock<ToolManager>(new NullLogger<ToolManager>());
        _assProcessor = new AssProcessor(_mockToolManager.Object, _config, new NullLogger<AssProcessor>());

        _cacheService = new SubtitleCacheService(_config, _assProcessor, _customCachePath, new NullLogger<SubtitleCacheService>());
    }

    [Fact]
    public async Task GetOrGenerateSubtitleAsync_ShouldReturnCachedFile_WhenCacheHit()
    {
        // Arrange
        Guid itemId = Guid.NewGuid();
        string originalAssPath = Path.Combine(_tempDataPath, "original.ass");

        string safeFileName = Path.GetFileName(originalAssPath);
        string expectedCachePath = Path.Combine(_customCachePath, $"{itemId:N}_{safeFileName}");

        File.WriteAllText(expectedCachePath, "mock cache data");

        // Act
        var result = await _cacheService.GetOrGenerateSubtitleAsync(itemId, originalAssPath, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(expectedCachePath, result);
    }

    [Fact]
    public async Task GetOrGenerateSubtitleAsync_ShouldRunLRUEviction_WhenQuotaExceeded()
    {
        // Arrange
        string cacheFileDir = _cacheService.CacheFolderPath;
        _config.MaxCacheSizeMB = 1;

        var oldId1 = Guid.NewGuid();
        var oldId2 = Guid.NewGuid();

        string oldFilePath1 = Path.Combine(cacheFileDir, $"{oldId1:N}_old1.ass");
        string oldFilePath2 = Path.Combine(cacheFileDir, $"{oldId2:N}_old2.ass");

        byte[] bigBuffer = new byte[600 * 1024]; // 600KB
        await File.WriteAllBytesAsync(oldFilePath1, bigBuffer, TestContext.Current.CancellationToken);

        File.SetLastAccessTime(oldFilePath1, DateTime.Now.AddMinutes(-5));

        await File.WriteAllBytesAsync(oldFilePath2, bigBuffer, TestContext.Current.CancellationToken);
        File.SetLastAccessTime(oldFilePath2, DateTime.Now.AddMinutes(-1));

        string fakeExe = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "cmd.exe" : "bash";
        _mockToolManager.Setup(m => m.GetToolPathAsync(It.IsAny<CancellationToken>())).ReturnsAsync(fakeExe);

        var newId = Guid.NewGuid();
        string originalPath = Path.Combine(_tempDataPath, "new_original.ass");
        await File.WriteAllTextAsync(originalPath, "short subtitle lines", TestContext.Current.CancellationToken);

        // Act
        await _cacheService.GetOrGenerateSubtitleAsync(newId, originalPath, TestContext.Current.CancellationToken);

        // Assert
        Assert.False(File.Exists(oldFilePath1), "The oldest file should be evicted successfully.");
        Assert.True(File.Exists(oldFilePath2), "The newer file must be preserved.");
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDataPath))
        {
            try
            {
                Directory.Delete(_tempDataPath, true);
            }
            catch
            {
                 /* Ignore */
            }
        }
    }
}
