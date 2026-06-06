using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AssSubsetter.Configuration;
using Jellyfin.Plugin.AssSubsetter.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.AssSubsetter.Tests.Services;

public class FontCacheManagerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _cacheFile;
    private readonly PluginConfiguration _config;

    public FontCacheManagerTests()
    {
        _tempDir = Path.Join(Path.GetTempPath(), "FontCacheManagerTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _cacheFile = Path.Join(_tempDir, "font_index.json");

        _config = new PluginConfiguration
        {
            FontCacheFilePath = _cacheFile,
            CustomFontDirectories = _tempDir
        };
    }

    [Fact]
    public async Task EnsureLoadedAsync_ShouldLoadFromDisk_WhenFileExists()
    {
        // Arrange
        var fakeEntries = new List<FontCacheEntry>
        {
            new FontCacheEntry
            {
                FamilyName = "FakeFont",
                Path = "/fake/path/FakeFont.ttf",
                Weight = 400,
                IsItalic = false,
                LastWriteTimeUtc = DateTime.UtcNow
            }
        };

        await File.WriteAllTextAsync(_cacheFile, JsonSerializer.Serialize(fakeEntries), TestContext.Current.CancellationToken);

        using var manager = new FontCacheManager(NullLogger<FontCacheManager>.Instance, () => _config);

        // Act
        await manager.EnsureLoadedAsync(TestContext.Current.CancellationToken);
        var foundInfo = manager.FindFontFilePath("FakeFont");
        var notFound = manager.FindFontFilePath("Unknown");

        // Assert
        Assert.NotNull(foundInfo);
        Assert.Equal("/fake/path/FakeFont.ttf", foundInfo.Value.Path);
        Assert.Equal(0, foundInfo.Value.FaceIndex);
        Assert.Null(notFound);
    }

    [Fact]
    public async Task FindFontFilePath_ShouldUseLooseMatch_WhenExactMatchFails()
    {
        // Arrange
        var fakeEntries = new List<FontCacheEntry>
        {
            new FontCacheEntry
            {
                FamilyName = "Comic Sans MS",
                Path = "/fake/path/comic.ttf",
            }
        };

        await File.WriteAllTextAsync(_cacheFile, JsonSerializer.Serialize(fakeEntries), TestContext.Current.CancellationToken);

        using var manager = new FontCacheManager(NullLogger<FontCacheManager>.Instance, () => _config);
        await manager.EnsureLoadedAsync(TestContext.Current.CancellationToken);

        // Act - loose match
        var foundInfo = manager.FindFontFilePath("Comic Sans");

        // Assert
        Assert.NotNull(foundInfo);
        Assert.Equal("/fake/path/comic.ttf", foundInfo.Value.Path);
        Assert.Equal(0, foundInfo.Value.FaceIndex);
    }

    [Fact]
    public void Dispose_ShouldNotThrow()
    {
        using var manager = new FontCacheManager(NullLogger<FontCacheManager>.Instance, () => _config);
        // implicit dispose at end of block
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, true); } catch { /* ignore */ }
        }
    }
}
